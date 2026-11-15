-- Glacier design kit: the payload composer (installed into the kit by kit.lua after kit_compose.lua).
--
-- kit.layout{gametype = "payload"} generates through this module: a cart route through 3-5 segments whose legs run
-- on different headings. It builds on the layout composer's state (I.layout.compose: placement and clearance, wall
-- bookkeeping, traced vestibules, loop closing) and registers I.layout.generators.payload.
--
-- The method:
--   * the programme: segments by players, their floor levels (climbs of one storey on graded roads, level legs, at
--     most one downhill pay-off), a tunnel leg, a chasm under a cart bridge; each segment a road ending in a
--     checkpoint yard, the last in the fortified finale;
--   * the track is designed first, as a polyline: one real corner (90 degrees, inside a yard: the cart leaves it by
--     a side wall and the next leg keeps the frame, its heading turned) and bends of 25-50 degrees (the cart road
--     leaves the yard's front wall, runs on, bends once and enters the next road, which lies in a frame turned by the
--     bend). Turn sequences are drawn from the seeded stream, mostly turning one way, and kept when legs that do not
--     meet keep 72 m apart and the track's bounding box (with a margin for the rooms) is at most twice as long as
--     wide; the corner's place is drawn among the joints that give such a plan; the map is centred on the origin;
--   * each segment is laid out in its own frame by construction (u across the heading, v along it): the road, the
--     yard past it (a road or a cart bridge over the chasm between them), and on each side a band of rooms that meet
--     wall to wall: a room at one of the road's level pads (a doorway onto it) and a room beside the yard that
--     starts where it ends (a doorway or a drop through the 1 m wall between them, or a climb's run apart when a
--     storey differs). On the flank side, at the track's level, the yard's room is a long low gallery from the
--     road's middle along the yard (a middle room on the yard's floor before it on a climbing or long leg); on the
--     high side the road's room is a long terrace beside the whole road after a bend, and the yard's room is a
--     storey up with a drop into the yard. The flank keeps to the outside of the turns, the high ground to the
--     inside; at the corner the yard's high room stands on its front wall, a stair's run from the next leg's flank;
--     on the last leg the flank's gallery is the finale's gate (a doorway through its entry wall beside the road);
--   * most rooms are plain rectangles; some keep the archetype's corners and a few take large ones (cut once their
--     openings are known); halls take their headroom by size, galleries are low, the yards open to the sky;
--   * across a bend, the cart road's ports and its bend vertex are computed from the walls (exit point + d1 along
--     the old heading, + d2 along the new one); the rooms on the outside are tied by a traced vestibule or a free
--     corridor whose path is computed here, the high rooms on the inside by a traced wedge room (or a corridor, or a
--     stair down onto the next road's entry pad); across the corner the rooms share one frame and meet wall to wall;
--   * small traced rooms where two walls of different frames meet at an angle, and bays off the yards' free walls,
--     while the zone budget has room;
--   * loops are closed inside each segment and across its joints, zones still short of links are linked to their
--     nearest neighbours (wall to wall or by computed corridors), then the objectives: the cart along the spine, the
--     checkpoints at the yards, the spawn groups with the hand-off.
--
-- This module returns one function, install(kit, internals). Generation uses the seeded stream it is handed, so the
-- same seed always gives the same graph.

local function install(kit, I)
local L = I.layout
local LC = L.compose
local DATA, LA = L.DATA, L.LA
local SIDES, OPP, DIRV = L.SIDES, L.OPP, L.DIRV
local clamp, q2, even = L.clamp, L.q2, L.even
local to_world, to_local, norm_rot = L.to_world, L.to_local, L.norm_rot
local EPS = I.EPS
local _ = kit

-- The side a heading turns to on a right turn (yaw + 90) and on a left turn.
local RIGHT = { ["+z"] = "+x", ["+x"] = "-z", ["-z"] = "-x", ["-x"] = "+z" }
local LEFT = { ["+z"] = "-x", ["-x"] = "-z", ["-z"] = "+x", ["+x"] = "+z" }

local function snap16(v) return math.floor(v * 16 + 0.5) / 16 end
local function same_frame(a, b) return L.same_rot(norm_rot(a), norm_rot(b)) end

-- ---------------------------------------------------------------------------------------------
-- Plan shapes and the clearance test
-- ---------------------------------------------------------------------------------------------

-- A rectangle in a frame (local {x0, z0, x1, z1}) as a world polygon.
local function rect_poly(th, r)
  local out = {}
  local q = { { r[1], r[2] }, { r[3], r[2] }, { r[3], r[4] }, { r[1], r[4] } }
  for i = 1, 4 do local x, z = to_world(th, q[i][1], q[i][2]) out[i] = { x, z } end
  return out
end

local function wp_of(i)
  if not i.wp then i.wp = rect_poly(i.f, i.r) end
  return i.wp
end

local function bb_of(i)
  local wp = wp_of(i)
  if not i.bb or i.bbw ~= wp then
    local x0, z0, x1, z1 = L.bbox(wp)
    i.bb, i.bbw = { x0, z0, x1, z1 }, wp
  end
  return i.bb
end

-- True when two placed shapes come closer than m: rectangles of one frame by their local boxes, anything else by
-- world bounding boxes first and polygons only when those are close.
local function closer(ia, ib, m)
  if ia.r and ib.r and same_frame(ia.f, ib.f) then
    local a, b = ia.r, ib.r
    return math.max(b[1] - a[3], a[1] - b[3], b[2] - a[4], a[2] - b[4]) < m - EPS
  end
  local A, B = bb_of(ia), bb_of(ib)
  if math.max(B[1] - A[3], A[1] - B[3], B[2] - A[4], A[2] - B[4]) >= m then return false end
  return L.poly_dist(wp_of(ia), wp_of(ib), m + 0.5) < m - EPS
end

-- How far two rectangles of one frame overlap along the walls that face each other, and on which axis they
-- are apart: axis, gap, overlap.
local function facing(a, b)
  local gx, gz = math.max(b[1] - a[3], a[1] - b[3]), math.max(b[2] - a[4], a[2] - b[4])
  if gx > -EPS and gz < -EPS then return "x", gx, math.min(a[4], b[4]) - math.max(a[2], b[2]) end
  if gz > -EPS and gx < -EPS then return "z", gz, math.min(a[3], b[3]) - math.max(a[1], b[1]) end
  return nil
end

-- The composer's clearance test with a bounding-box pre-test (a long track puts most zones far apart).
local function fits(self, info, skip, m)
  m = m or self.gap
  for _, p in ipairs(self.G.zones) do
    if not (skip and skip[p]) then
      local ip = self.info[p]
      if ip and closer(info, ip, m) then return false end
    end
  end
  for _, f in ipairs(self.foot) do
    if not (skip and (skip[f.za] or skip[f.zb])) and closer(info, f, DATA.min_wall) then return false end
  end
  for _, f in ipairs(self.routes or {}) do
    if not (skip and (skip[f.za] or skip[f.zb])) and closer(info, f, DATA.min_wall) then return false end
  end
  return true
end

-- An even size near an area and aspect (across / along) for a zone whose length runs along heading h, clamped to
-- the archetype.
local function side_size(A, area, aspect, h, along_max)
  local across = math.sqrt(area * aspect)
  local along = area / across
  if along_max then along = math.min(along, along_max) end
  local w, d
  if h == "+z" or h == "-z" then w, d = across, along else w, d = along, across end
  return { clamp(even(w), A.min[1], A.max[1]), clamp(even(d), A.min[2], A.max[2]) }
end

-- {w, d} for a zone with across x along in heading h.
local function dims(h, across, along) if h == "+z" or h == "-z" then return { across, along } end return { along, across } end

-- ---------------------------------------------------------------------------------------------
-- Segment frames: heading coordinates (u to the right of the heading, v along it) from the road's entry point
-- ---------------------------------------------------------------------------------------------

local function hv(sg, u, v)
  local r, f = DIRV[RIGHT[sg.h]], DIRV[sg.h]
  return sg.e[1] + u * r[1] + v * f[1], sg.e[2] + u * r[2] + v * f[2]
end
local function hw(sg, u, v) return to_world(sg.th, hv(sg, u, v)) end
local function fwd(sg) local x, z = to_world(sg.th, DIRV[sg.h][1], DIRV[sg.h][2]) return { x, z } end

-- The side a turn of sign sign leaves on its outside, in heading h's terms.
local function outside_of(sign, h) return (sign > 0) and LEFT[h] or RIGHT[h] end

-- Where the yard of segment sg sits (heading coordinates of its centre) and where the cart leaves it.
local function yard_v(sg) return sg.L + sg.g + sg.yd / 2 end

-- Lays the frames out from the joints: segment 1 at the origin heading +z; a bend turns the next frame by its
-- angle (the cart road leaves the yard's front wall, runs d1 on, bends and runs d2 to the next road's entry), a
-- corner keeps the frame and turns the heading to the side the cart leaves the yard by.
local function layout_frames(segs, joints, origin)
  segs[1].th, segs[1].h, segs[1].e = 0, "+z", origin or { 0, 0 }
  for s = 1, #segs - 1 do
    local a, b, J = segs[s], segs[s + 1], joints[s]
    if J.kind == "bend" then
      local ex, ez = hw(a, a.sig + J.se, a.L + a.g + a.yd)
      local f = fwd(a)
      b.th, b.h = norm_rot(a.th + J.sign * J.delta), a.h
      local f2 = fwd(b)
      local px, pz = ex + f[1] * J.d1 + f2[1] * J.d2, ez + f[2] * J.d1 + f2[2] * J.d2
      local lx, lz = to_local(b.th, px, pz)
      b.e = { snap16(lx), snap16(lz) }
    else
      b.th, b.h = a.th, (J.sign > 0) and RIGHT[a.h] or LEFT[a.h]
      local x, z = hv(a, a.sig + J.sign * (a.yw / 2 + J.gc), yard_v(a) + J.tau)
      b.e = { x, z }
    end
  end
end

-- The track as world polylines, one per segment: road entry, road exit, yard centre, the cart's exit from the yard
-- (and the bend). Segment 1 starts behind its road at the start hall.
local function track_legs(segs, joints)
  local legs = {}
  for s, sg in ipairs(segs) do
    local pts = {}
    local function add(u, v) local x, z = hw(sg, u, v) pts[#pts + 1] = { x, z } end
    if s == 1 then add(0, -16) end
    add(0, 0)
    add(0, sg.L)
    add(sg.sig, yard_v(sg))
    local J = joints[s]
    if J and J.kind == "bend" then
      add(sg.sig + J.se, sg.L + sg.g + sg.yd)
      local x, z = hw(sg, sg.sig + J.se, sg.L + sg.g + sg.yd + J.d1)
      pts[#pts + 1] = { x, z }
    elseif J then
      add(sg.sig + J.sign * sg.yw / 2, yard_v(sg) + J.tau)
    else
      add(sg.sig, sg.L + sg.g + sg.yd)
    end
    legs[s] = pts
  end
  return legs
end

local function seg_dist(a, b, c, d)
  local function pt(p, q, r)
    local ex, ez = r[1] - q[1], r[2] - q[2]
    local l2 = ex * ex + ez * ez
    local t = l2 > 0 and clamp(((p[1] - q[1]) * ex + (p[2] - q[2]) * ez) / l2, 0, 1) or 0
    local x, z = q[1] + ex * t - p[1], q[2] + ez * t - p[2]
    return math.sqrt(x * x + z * z)
  end
  return math.min(pt(a, c, d), pt(b, c, d), pt(c, a, b), pt(d, a, b))
end

-- How far a track plan is from the rules: legs that are not neighbours keep clear apart, the map is not a strip.
-- Returns the shortfall (0 = good) and the bounding box aspect.
local function track_fault(segs, joints)
  layout_frames(segs, joints)
  local legs = track_legs(segs, joints)
  local fault = 0
  for i = 1, #legs do
    for j = i + 2, #legs do
      local A, B = legs[i], legs[j]
      local d = math.huge
      for a = 1, #A - 1 do for b = 1, #B - 1 do d = math.min(d, seg_dist(A[a], A[a + 1], B[b], B[b + 1])) end end
      if d < 72 then fault = fault + (72 - d) end
    end
  end
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, P in ipairs(legs) do for _, q in ipairs(P) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end end
  local w, d = x1 - x0 + 56, z1 - z0 + 56
  local aspect = math.max(w, d) / math.min(w, d)
  if aspect > 2.0 then fault = fault + (aspect - 2.0) * 40 end
  return fault, aspect
end

-- ---------------------------------------------------------------------------------------------
-- The programme and the track plan
-- ---------------------------------------------------------------------------------------------

local function programme(G, rng)
  local players = G.players
  local step = G.step
  local nseg = 3 + ((players >= 12) and 1 or 0) + ((players >= 20) and 1 or 0)
  nseg = clamp(nseg, DATA.payload_segments[1], DATA.payload_segments[2])
  if G.segments then nseg = clamp(G.segments, 3, 5) end
  -- The level wishes per segment (a climb of one step, level, or the one downhill pay-off), drawn once; the turns
  -- below may hold a segment level.
  local rolls = {}
  for s = 1, nseg do rolls[s] = rng.float() end
  local tunnel = rng.int(2, nseg)
  local bridge = rng.int(1, nseg - 1)
  if bridge == tunnel then bridge = (bridge > 1) and (bridge - 1) or (bridge + 1) end
  if bridge >= nseg then bridge = 1 end
  local segs = {}
  for s = 1, nseg do
    local last = (s == nseg)
    local sg = { rw = rng.pick({ 10, 10, 12 }), g = 1, tunnel = (s == tunnel) or nil, bridge = (s == bridge) or nil }
    sg.flatL = rng.pick({ 36, 40, 44 })
    if sg.bridge then sg.g = q2(rng.range(8, 11)) end
    -- the checkpoint yard (the finale on the last leg), square on the road or slid a little along its entry wall
    local yard = last and LA.finale or LA.yard
    local area = last and rng.range(1500, 1900) or rng.range(700, 1000)
    local side = clamp(even(math.sqrt(area * rng.range(0.72, 1.35))), yard.min[1], yard.max[1])
    sg.yw, sg.yd = side, clamp(even(area / side), yard.min[2], yard.max[2])
    -- (the finale at its full size: its chamfered entry wall keeps room for a doorway beside the road)
    if last then sg.yw, sg.yd = yard.max[1], yard.max[1] end
    sg.sig = q2(rng.range(-3, 3))
    -- (the finale sits square on its road: its entry wall keeps room for a doorway beside the road on both sides)
    if last then sg.sig = 0 end
    segs[s] = sg
  end
  -- Floor levels from the wishes: climbs of one step (at least one), level legs, at most one downhill pay-off. A
  -- segment held level (flat[s]) stays level; the last segment takes the climb when nothing else does.
  local function levels(flat)
    local lv = { [0] = 0 }
    local downs, ups = 0, 0
    for s = 1, nseg do
      local roll = rolls[s]
      local d
      if s == 1 then d = (roll < 0.5) and 1 or 0
      elseif roll < 0.5 then d = 1
      elseif roll < 0.8 or downs >= 1 or lv[s - 1] == 0 then d = 0
      else d = -1 end
      if flat[s] then d = 0 end
      if d == -1 then downs = downs + 1 end
      if d == 1 then ups = ups + 1 end
      lv[s] = math.max(0, lv[s - 1] + d)
    end
    if ups == 0 then lv[nseg] = lv[nseg - 1] + 1 end
    for s = 1, nseg do
      local sg = segs[s]
      sg.y_in, sg.y_out = lv[s - 1] * step, lv[s] * step
      sg.rise = sg.y_out - sg.y_in
      -- the road: level pads at both ends (side doors), a grade of at most 15% between
      sg.L = (sg.rise ~= 0) and (24 + even(math.abs(sg.rise) / 0.14 + 1)) or sg.flatL
    end
    return lv
  end
  -- The joints: one real corner, the others bends of 25-50 degrees, mostly turning the same way (the route folds
  -- round instead of running off in a strip). A segment between two turns that go opposite ways, or that ends in
  -- the corner, stays level (its side rooms meet the next leg's on one level). Kept when the legs keep clear and the
  -- map is compact.
  local best, best_f, best_lv = nil, math.huge, nil
  local nj = nseg - 1
  -- (the first good plan for each place of the corner; the corner's place is then drawn among those)
  local good, goods = {}, {}
  for _ = 1, 40 do
    local corner = rng.int(1, nj)
    local sign = rng.chance(0.5) and 1 or -1
    local joints = {}
    local flat = {}
    for j = 1, nj do
      if j > 1 and rng.chance(0.3) then sign = -sign flat[j] = true end
      if j == corner then
        joints[j] = { kind = "corner", sign = sign, gc = 1, tau = q2(rng.range(-3, 3)) }
        flat[j] = true
      else
        -- the cart leaves the yard toward the outside of the bend (more room for the high ground inside it)
        local room = math.max(0, segs[j].yw / 2 - 3.75 - 4 - 1.5)
        joints[j] = { kind = "bend", sign = sign, delta = rng.pick({ 25, 30, 35, 40, 45, 50 }), d1 = q2(rng.range(8.5, 11)), d2 = q2(rng.range(9, 12)),
          se = q2(-sign * rng.range(0, math.min(4, room))) }
      end
    end
    levels(flat)
    local f = track_fault(segs, joints)
    if f < best_f then best, best_f, best_lv = joints, f, flat end
    if f <= 0 and not good[corner] then good[corner] = { joints, flat } goods[#goods + 1] = corner end
    if #goods >= nj then break end
  end
  if #goods > 0 then
    local pick = good[goods[rng.int(1, #goods)]]
    best, best_lv = pick[1], pick[2]
  end
  local lv = levels(best_lv)
  local f, aspect = track_fault(segs, best)
  -- The map centred on the world origin (turned frames turn about it: the closer their zones lie to it, the
  -- smaller the coordinates their brushes are built at).
  local legs = track_legs(segs, best)
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, P in ipairs(legs) do for _, q in ipairs(P) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end end
  local origin = { -math.floor((x0 + x1) / 2 + 0.5), -math.floor((z0 + z1) / 2 + 0.5) }
  return { origin = origin, nseg = nseg, segs = segs, joints = best, tunnel = tunnel, bridge = bridge, lv = lv, fault = f, aspect = aspect }
end

-- ---------------------------------------------------------------------------------------------
-- Composition
-- ---------------------------------------------------------------------------------------------

-- The wall stretch (frame coordinates along the side walls) of a road's entry or exit level pad.
local function road_pad(R, which)
  local F = L.road_frame(R)
  local s0, s1 = 1, F.ls - 1
  if which == "exit" then s0, s1 = F.len - F.ln + 1, F.len - 1 end
  local a, b = F.at(s0), F.at(s1)
  if a > b then a, b = b, a end
  return { a, b }
end

-- The world outward normal and the world end points of a rectangle zone's side.
local function side_geo(z, side)
  local x0, z0, x1, z1 = L.rect_of(z)
  local th = norm_rot(z.rot)
  local a, b
  if side == "+x" then a, b = { x1, z0 }, { x1, z1 }
  elseif side == "-x" then a, b = { x0, z1 }, { x0, z0 }
  elseif side == "+z" then a, b = { x1, z1 }, { x0, z1 }
  else a, b = { x0, z0 }, { x1, z0 } end
  local ax, az = to_world(th, a[1], a[2])
  local bx, bz = to_world(th, b[1], b[2])
  local nx, nz = to_world(th, DIRV[side][1], DIRV[side][2])
  return { nx, nz }, { ax, az }, { bx, bz }
end

-- The world point of a rectangle zone's wall at along-wall coordinate c.
local function wall_world(z, side, c)
  local p = L.wall_point(z, side, c)
  local x, zz = to_world(norm_rot(z.rot), p[1], p[2])
  return { x, zz }
end

-- The centre intervals a w m opening can take on a zone's wall at its floor (archetype ranges less booked spans).
local function free_spans(C, z, side, W, off)
  local free = L.ranges_of(z, side, off or 0)
  for _, s in ipairs(C:used_of(z, side)) do free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end
  local out = {}
  for _, s in ipairs(free) do if s[2] - s[1] >= W - EPS then out[#out + 1] = { s[1] + W / 2, s[2] - W / 2 } end end
  return out
end

local function nearest_in(ivs, c)
  local best, bd = nil, math.huge
  for _, iv in ipairs(ivs) do
    local v = clamp(c, iv[1], iv[2])
    if math.abs(v - c) < bd then best, bd = v, math.abs(v - c) end
  end
  return best
end

-- The along-wall coordinate of a world point projected on a rectangle zone's wall.
local function along(z, side, q)
  local lx, lz = to_local(norm_rot(z.rot), q[1], q[2])
  if side == "+x" or side == "-x" then return lz end
  return lx
end

-- Adds a free link's plan (mitred quads) to the routes the clearance test keeps zones and other routes from.
local function book_route(C, l, za, zb, pts, hw_)
  local M = L.mitre(pts, hw_)
  if not M then return nil end
  C.routes = C.routes or {}
  for _, q in ipairs(M.quads) do C.routes[#C.routes + 1] = { wp = q, link = l, za = za, zb = zb } end
  return M
end

-- True when a free link's quads keep a wall's thickness from every zone (its own zones only past the first and
-- last quads) and from every footprint.
local function route_clear(C, M, za, zb)
  local n = #M.quads
  for k, q in ipairs(M.quads) do
    local qi = { wp = q }
    for _, p in ipairs(C.G.zones) do
      local ip = C.info[p]
      local own = (p == za and k == 1) or (p == zb and k == n)
      if ip and not own and closer(qi, ip, DATA.min_wall) then
        -- (a quad of its own zone's port stub only touches that zone at the wall)
        if not ((p == za or p == zb) and n == 1) then return false end
      end
    end
    for _, f in ipairs(C.foot) do if closer(qi, f, DATA.min_wall) then return false end end
    for _, f in ipairs(C.routes or {}) do if closer(qi, f, DATA.min_wall) then return false end end
  end
  return true
end

-- The walls a free link can leave zone z by, for a W m opening at its floor: a rectangle's sides and a traced
-- zone's edges (key = the side's name or the edge's number), each with its world outward normal, mid point, the
-- centre intervals the opening can take (along-wall coordinates: archetype ranges or the edge less a metre at each
-- end, less booked openings), the world point at an along-wall coordinate and the along-wall coordinate of a point.
local function zone_sides(C, z, W)
  local out = {}
  if z.archetype ~= "poly" then
    for _, sd in ipairs(SIDES) do
      local n, a0, a1 = side_geo(z, sd)
      out[#out + 1] = { key = sd, n = n, mid = { (a0[1] + a1[1]) / 2, (a0[2] + a1[2]) / 2 }, ivs = free_spans(C, z, sd, W),
        pt = function(c) return wall_world(z, sd, c) end, along = function(q) return along(z, sd, q) end }
    end
    return out
  end
  local pts = C.info[z] and C.info[z].wp
  for i = 1, #(pts or {}) do
    local a, b = pts[i], pts[i % #pts + 1]
    local ex, ez = b[1] - a[1], b[2] - a[2]
    local el = math.sqrt(ex * ex + ez * ez)
    if el >= W + 2.5 then
      local tx, tz = ex / el, ez / el
      local free = { { 1, el - 1 } }
      for _, s in ipairs(C:used_of(z, i)) do free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end
      local ivs = {}
      for _, s in ipairs(free) do if s[2] - s[1] >= W - EPS then ivs[#ivs + 1] = { s[1] + W / 2, s[2] - W / 2 } end end
      out[#out + 1] = { key = i, n = { tz, -tx }, mid = { (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 }, ivs = ivs,
        pt = function(c) return { a[1] + tx * c, a[2] + tz * c } end,
        along = function(q) return (q[1] - a[1]) * tx + (q[2] - a[2]) * tz end }
    end
  end
  return out
end

-- A free corridor (or stair / ramp) between two zones (rectangles of any frames, traced zones) whose path is
-- computed here: a port on a wall of each that faces the other, then straight across, or one bend where the two
-- walls' normals meet, or a dog-leg. Returns the link or nil.
local function free_cross(C, za, zb, o)
  o = o or {}
  local dy = zb.pos[2] - za.pos[2]
  local t
  if math.abs(dy) <= DATA.max_step + EPS then t = "corridor"
  elseif math.abs(dy) <= C.step + 1 + EPS then t = o.climb or (C.rng.chance(0.5) and "stair" or "ramp")
  else return nil end
  local W = (t == "corridor") and 4.5 or 4
  local hw_ = W / 2
  local run = (t == "stair") and L.stair_run(math.abs(dy)) or ((t == "ramp") and L.ramp_run(math.abs(dy)) or 0)
  local cax, caz = L.centre_of(za)
  local cbx, cbz = L.centre_of(zb)
  local SA, SB = zone_sides(C, za, W), zone_sides(C, zb, W)
  local best = nil
  local cands = {}
  for _, A in ipairs(SA) do
    local na, ac = A.n, A.mid
    if #A.ivs > 0 and (cbx - ac[1]) * na[1] + (cbz - ac[2]) * na[2] > 4 then
      for _, B in ipairs(SB) do
        local nb, bc = B.n, B.mid
        if #B.ivs > 0 and (cax - bc[1]) * nb[1] + (caz - bc[2]) * nb[2] > 4 and (bc[1] - ac[1]) * na[1] + (bc[2] - ac[2]) * na[2] > 2 and
          (ac[1] - bc[1]) * nb[1] + (ac[2] - bc[2]) * nb[2] > 2 then
          -- ports: each toward the other wall's nearest point
          local ca = nearest_in(A.ivs, A.along(bc))
          local pa = A.pt(snap16(ca))
          local cb = nearest_in(B.ivs, B.along(pa))
          local pb = B.pt(snap16(cb))
          ca = nearest_in(A.ivs, A.along(pb))
          pa = A.pt(snap16(ca))
          local s = hw_ + DATA.min_wall
          local routes = {}
          local den = na[1] * nb[2] - na[2] * nb[1]
          local ex, ez = pb[1] - pa[1], pb[2] - pa[2]
          if math.abs(den) > 1e-6 then
            local tt = (ex * nb[2] - ez * nb[1]) / den
            local uu = (ex * na[2] - ez * na[1]) / den
            if tt >= s and uu >= s then routes[#routes + 1] = { pa, { pa[1] + na[1] * tt, pa[2] + na[2] * tt }, pb } end
          end
          -- out of each wall on a stub, then straight across (two bends)
          local D = ex * na[1] + ez * na[2]
          local E = -(ex * nb[1] + ez * nb[2])
          for _, t1 in ipairs({ s, s + 3, D / 2 }) do
            for _, u1 in ipairs({ s, s + 3, E / 2 }) do
              if t1 >= s - EPS and u1 >= s - EPS then
                routes[#routes + 1] = { pa, { pa[1] + na[1] * t1, pa[2] + na[2] * t1 }, { pb[1] + nb[1] * u1, pb[2] + nb[2] * u1 }, pb }
              end
            end
          end
          if math.abs(ex * na[2] - ez * na[1]) < 0.01 and na[1] * nb[1] + na[2] * nb[2] < -0.9999 then routes[#routes + 1] = { pa, pb } end
          -- (a stair or ramp runs straight: the resolver's port stubs leave a bent one short of its run)
          if run > 0 then
            local kept = {}
            for _, pts in ipairs(routes) do if #pts == 2 then kept[#kept + 1] = pts end end
            routes = kept
          end
          for _, pts in ipairs(routes) do
            local ok = true
            local len = 0
            local dirs = {}
            for i = 1, #pts - 1 do
              local dx, dz = pts[i + 1][1] - pts[i][1], pts[i + 1][2] - pts[i][2]
              local l = math.sqrt(dx * dx + dz * dz)
              if l < 1.5 then ok = false end
              len = len + l
              dirs[i] = { dx / math.max(l, 1e-9), dz / math.max(l, 1e-9) }
            end
            for i = 2, #dirs do if L.angle_of(dirs[i - 1], dirs[i]) > 90 + 0.01 then ok = false end end
            if ok and len <= (o.max_len or 44) then
              cands[#cands + 1] = { len = len, pts = pts, sa = A.key, sb = B.key, ca = snap16(ca), cb = snap16(cb), pa = pa, pb = pb }
            end
          end
        end
      end
    end
  end
  -- (the shortest that keeps clear)
  for i = 2, #cands do
    local c, j = cands[i], i - 1
    while j >= 1 and cands[j].len > c.len do cands[j + 1] = cands[j] j = j - 1 end
    cands[j + 1] = c
  end
  for _, c in ipairs(cands) do
    if not best then
      local M = L.mitre(c.pts, hw_)
      if M and run > 0 then
        local st = 0
        for _, sp in ipairs(M.straight) do st = math.max(st, sp[2] - sp[1]) end
        if st < run + 1.5 then M = nil end
      end
      if M and route_clear(C, M, za, zb) then best = c end
    end
  end
  if not best then return nil end
  local inner = {}
  for i = 2, #best.pts - 1 do inner[#inner + 1] = { best.pts[i][1], best.pts[i][2] } end
  local l = { type = t, width = W, a_y = za.pos[2], b_y = zb.pos[2], a_side = best.sa, b_side = best.sb, a_at = { best.pa[1], best.pa[2] },
    b_at = { best.pb[1], best.pb[2] }, path = inner, keep = true }
  local H = DATA.corridor_height
  C:commit_free(l, za, zb, {
    { zone = za, side = best.sa, lo = best.ca - hw_, hi = best.ca + hw_, y0 = za.pos[2], y1 = za.pos[2] + H },
    { zone = zb, side = best.sb, lo = best.cb - hw_, hi = best.cb + hw_, y0 = zb.pos[2], y1 = zb.pos[2] + H } })
  book_route(C, l, za, zb, best.pts, hw_)
  return l
end

-- The zones given that exist, as a list.
local function present(...)
  local out = {}
  for i = 1, select("#", ...) do local z = select(i, ...) if z then out[#out + 1] = z end end
  return out
end

-- (below: a walk link between two rooms)
local link_pair

-- A vestibule (traced room joined to both) or a free corridor between two rooms of neighbouring segments.
local function tie(C, za, zb, seg, o)
  if not za or not zb or C:linked(za, zb) then return false end
  if za.archetype == "poly" or zb.archetype == "poly" then return link_pair(C, za, zb, { keep = true }) ~= nil end
  if math.abs(za.pos[2] - zb.pos[2]) <= DATA.max_step + EPS then
    local tries = {}
    for _, sa in ipairs(SIDES) do
      local na, a0, a1 = side_geo(za, sa)
      for _, sb in ipairs(SIDES) do
        local nb, b0, b1 = side_geo(zb, sb)
        local dot = na[1] * nb[1] + na[2] * nb[2]
        if dot < -0.3 then
          local ac = { (a0[1] + a1[1]) / 2, (a0[2] + a1[2]) / 2 }
          local bc = { (b0[1] + b1[1]) / 2, (b0[2] + b1[2]) / 2 }
          local gap = (bc[1] - ac[1]) * na[1] + (bc[2] - ac[2]) * na[2]
          if gap >= 2.5 and gap <= ((o and o.max_gap) or 18) then tries[#tries + 1] = { sa = sa, sb = sb, d = gap - 3 * dot } end
        end
      end
    end
    for i = 2, #tries do
      local c, j = tries[i], i - 1
      while j >= 1 and tries[j].d > c.d do tries[j + 1] = tries[j] j = j - 1 end
      tries[j + 1] = c
    end
    for _, e in ipairs(tries) do
      local V = C:bridge_poly(za, e.sa, zb, e.sb, { palette = zb.palette, max_gap = (o and o.max_gap) or 18, role = "room", len = o and o.len })
      if V then V.segment = seg return true end
    end
  end
  local l = free_cross(C, za, zb)
  if l then l.keep = true end
  return l ~= nil
end

-- (below: the free links between a rectangle and a traced zone)
local poly_link

-- A walk link between two rooms: wall to wall when they face each other in one frame (a doorway or corridor on
-- one level, a stair, ramp or drop between storeys), else a computed free corridor or stair; a traced room takes a
-- free doorway, stair or ramp straight across from a parallel wall. Returns the link or nil.
link_pair = function(C, a, b, o)
  o = o or {}
  if not a or not b or a == b or C:linked(a, b) then return nil end
  if a.archetype == "poly" or b.archetype == "poly" then
    local l = nil
    if a.archetype ~= "poly" then l = poly_link(C, a, b, o) elseif b.archetype ~= "poly" then l = poly_link(C, b, a, o) end
    if l or o.classic_only then return l end
    l = free_cross(C, a, b, { max_len = o.max_len })
    if l then l.keep = o.keep end
    return l
  end
  local f = L.link_frame(a, b)
  if f and f.gap <= (o.max_gap or 32) and f.hi - f.lo >= 5 then
    local hi, lo = a, b
    if b.pos[2] > a.pos[2] then hi, lo = b, a end
    local dy = hi.pos[2] - lo.pos[2]
    local tries = {}
    if dy <= DATA.max_step + EPS then
      if f.gap <= 1.6 then tries = { { "doorway", C.rng.pick({ 3, 4 }) } } else tries = { { "corridor", C.rng.pick({ 4, 4.5, 5 }) } } end
    elseif dy <= C.step + 1 + EPS then
      if f.gap <= 2.5 and dy >= 1.5 and dy <= DATA.drop_max and hi.pos[2] + DATA.corridor_height <= lo.pos[2] + lo.height - EPS then tries[#tries + 1] = { "drop", 4 } end
      local first = o.climb or (C.rng.chance(0.5) and "stair" or "ramp")
      tries[#tries + 1] = { first, 4 }
      tries[#tries + 1] = { (first == "stair") and "ramp" or "stair", 4 }
    end
    for _, tr in ipairs(tries) do
      local l = { type = tr[1], width = tr[2], a_y = hi.pos[2], b_y = lo.pos[2], keep = o.keep }
      if L.gap_need(l, hi, lo) <= f.gap + EPS and C:commit_classic(l, hi, lo) then return l end
    end
  end
  if o.classic_only then return nil end
  local l = free_cross(C, a, b, { max_len = o.max_len })
  if l then l.keep = o.keep end
  return l
end

-- A rectangle zone's wall as a plan line: at(c, g) = the world point at along-wall coordinate c, g metres out; the
-- world tangent (growing c) and outward normal.
local function wall_line(z, side)
  local th = norm_rot(z.rot)
  local tx, tz
  if side == "+x" or side == "-x" then tx, tz = to_world(th, 0, 1) else tx, tz = to_world(th, 1, 0) end
  local nx, nz = to_world(th, DIRV[side][1], DIRV[side][2])
  local function at(c, g)
    local p = L.wall_point(z, side, c)
    local x, zz = to_world(th, p[1], p[2])
    return { x + nx * g, zz + nz * g }
  end
  return at, { tx, tz }, { nx, nz }
end

-- The centre for a w m opening inside the stretch [a, b] of a wall, within its free spans (nearest the middle).
local function door_in(free, a, b, w)
  local best, bd = nil, math.huge
  local m = (a + b) / 2
  for _, s in ipairs(free) do
    local lo, hi = math.max(s[1], a + 0.75) + w / 2, math.min(s[2], b - 0.75) - w / 2
    if hi >= lo then
      local c = L.q4(clamp(m, lo, hi))
      if c < lo - EPS then c = c + 0.25 end
      if c > hi + EPS then c = c - 0.25 end
      if c >= lo - EPS and c <= hi + EPS and math.abs(c - m) < bd then best, bd = c, math.abs(c - m) end
    end
  end
  return best
end

-- A free doorway, stair or ramp straight across the gap between a wall of rectangle zone R and a parallel edge of
-- traced zone P (a wall's thickness for a doorway, a climb's run for a stair or ramp). Returns the link or nil.
-- o: keep, climb ("stair" / "ramp").
poly_link = function(C, R, P, o)
  o = o or {}
  if not R or not P or C:linked(R, P) then return nil end
  local iR, iP = C.info[R], C.info[P]
  if not (iR and iR.r and iP and iP.wp) then return nil end
  local pts = iP.wp
  for _, side in ipairs(SIDES) do
    local at, t, n = wall_line(R, side)
    local p0 = at(0, 0)
    for i = 1, #pts do
      local a, b = pts[i], pts[i % #pts + 1]
      local ex, ez = b[1] - a[1], b[2] - a[2]
      local el = math.sqrt(ex * ex + ez * ez)
      -- (a counter-clockwise outline: edge i's outward normal is (ez, -ex) / length)
      if el >= 5 and (ez * n[1] - ex * n[2]) / el < -0.999 then
        local ga = (a[1] - p0[1]) * n[1] + (a[2] - p0[2]) * n[2]
        local gb = (b[1] - p0[1]) * n[1] + (b[2] - p0[2]) * n[2]
        if math.abs(ga - gb) < 0.15 and math.min(ga, gb) >= 0.95 and ga <= 14 then
          local g = (ga + gb) / 2
          local ca = (a[1] - p0[1]) * t[1] + (a[2] - p0[2]) * t[2]
          local cb = (b[1] - p0[1]) * t[1] + (b[2] - p0[2]) * t[2]
          local lo, hi = math.min(ca, cb), math.max(ca, cb)
          for _, off in ipairs(R.levels) do
            local yr = R.pos[2] + off
            local dy = math.abs(P.pos[2] - yr)
            local typ, w = nil, 4
            if dy <= DATA.max_step + EPS and g <= 1.6 then typ, w = "doorway", C.rng.pick({ 3, 4 })
            elseif dy > DATA.max_step + EPS and dy <= C.step + 1 + EPS then
              local st = o.climb or "stair"
              local need = ((st == "stair") and L.stair_run(dy) or L.ramp_run(dy)) + 2
              if g >= need - EPS and g <= need + 4 then typ = st end
            end
            if typ then
              local c = door_in(C:free_spans(R, side, off), lo, hi, w)
              local ce = nil
              if c then
                -- (the opening on the edge, along it from its first point, clear of the edge's other openings)
                local q = at(c, g)
                ce = ((q[1] - a[1]) * ex + (q[2] - a[2]) * ez) / el
                for _, u in ipairs(C:used_of(P, i)) do if ce + w / 2 > u.lo - 1.5 and ce - w / 2 < u.hi + 1.5 then c = nil end end
              end
              if c and typ ~= "doorway" then
                -- (a climb: checked on the pair alone first, as the resolver routes it)
                local l = { type = typ, width = w, a_side = side, b_side = i, a_at = at(c, 0), b_at = at(c, g), a_y = yr, b_y = P.pos[2] }
                local ia, ib = R.id, P.id
                R.id, P.id = "_a", "_b"
                l.a, l.b, l.id = "_a", "_b", "_l"
                local plan = L.resolve({ zones = { R, P }, links = { l }, gametype = C.G.gametype, class = C.G.class, method = "free" }, nil, false)
                R.id, P.id = ia, ib
                if #plan.errors > 0 then c = nil end
              end
              if c then
                local l = { type = typ, width = w, a_side = side, b_side = i, a_at = at(c, 0), b_at = at(c, g), a_y = yr, b_y = P.pos[2], keep = o.keep }
                C:commit_free(l, R, P, { { zone = R, side = side, lo = c - w / 2, hi = c + w / 2, y0 = yr, y1 = yr + 4.5 },
                  { zone = P, side = i, lo = ce - w / 2, hi = ce + w / 2, y0 = P.pos[2], y1 = P.pos[2] + 4.5 } })
                if g > 1.6 then book_route(C, l, R, P, { l.a_at, l.b_at }, w / 2) end
                return l
              end
            end
          end
        end
      end
    end
  end
  return nil
end

local connect = link_pair

-- Composes the whole graph into C from the programme and track plan P. Returns the zones by role or nil.
local function compose(C, P, G, rng)
  local themes = G.themes
  local step = G.step
  local segs, joints, nseg = P.segs, P.joints, P.nseg
  local Z = { R = {}, Y = {}, re = {}, rx = {}, yo = {}, yh = {}, side = {} }
  layout_frames(segs, joints, P.origin)
  -- 1. The spine: roads and yards in their frames, the classic links between them, the cart roads between segments.
  for s = 1, nseg do
    local sg = segs[s]
    local last = (s == nseg)
    local theme = themes[((s - 1) % #themes) + 1]
    sg.theme = theme
    local R = C:zone("road", { role = "span", segment = s, size = dims(sg.h, sg.rw, sg.L), y = sg.y_in,
      params = { heading = sg.h, rise = sg.rise, south = 12, north = 12, tunnel = sg.tunnel or nil }, palette = { theme = theme, variant = 1 } })
    local rx_, rz_ = hv(sg, 0, sg.L / 2)
    local iR = C:rect_info(R, sg.th, rx_, rz_)
    if not C:fits(iR) then return nil, "road " .. s end
    C:add(R, iR)
    if s > 1 and joints[s - 1].kind == "corner" then
      local Yp = Z.Y[s - 1]
      local cart = { type = "road", width = 8, a_y = Yp.pos[2], b_y = R.pos[2], sky = true, centre = true, keep = true }
      if not C:commit_classic(cart, Yp, R) then return nil, "corner road " .. s end
    end
    local Y
    if last then
      Y = C:zone("finale", { role = "checkpoint", segment = s, size = dims(sg.h, sg.yw, sg.yd), y = sg.y_out,
        params = { heading = sg.h }, palette = { theme = "bunker", variant = 2 } })
    else
      Y = C:zone("yard", { role = "checkpoint", segment = s, size = dims(sg.h, sg.yw, sg.yd), y = sg.y_out, palette = { theme = theme, variant = 2 } })
      -- (canted corners of different sizes: the archetype's, or large)
      Y.chamfer = rng.pick({ 3, 3, 5, 6 })
    end
    local yx, yz = hv(sg, sg.sig, yard_v(sg))
    local iY = C:rect_info(Y, sg.th, yx, yz)
    if not C:fits(iY) then return nil, "yard " .. s end
    C:add(Y, iY)
    local link
    if sg.bridge then
      link = { type = "bridge", width = 7, a_y = sg.y_out, b_y = sg.y_out, style = rng.pick({ "girder", "truss" }), centre = true, keep = true }
    else
      link = { type = "road", width = 8, a_y = sg.y_out, b_y = sg.y_out, sky = true, centre = true, keep = true }
    end
    if not C:commit_classic(link, R, Y) then return nil, "road to yard " .. s end
    Z.R[s], Z.Y[s] = R, Y
    -- the cart road from the previous yard round the bend: ports and the bend vertex computed from the walls
    if s > 1 and joints[s - 1].kind == "bend" then
      local Yp, a = Z.Y[s - 1], segs[s - 1]
      local J = joints[s - 1]
      local ex, ez = hw(a, a.sig + J.se, a.L + a.g + a.yd)
      local ca = snap16(along(Yp, a.h, { ex, ez }))
      local pa = wall_world(Yp, a.h, ca)
      local F = L.road_frame(R)
      local cb = snap16(F.centre)
      local pb = wall_world(R, OPP[sg.h], cb)
      local na, nb = fwd(a), fwd(sg)
      -- the bend: where the line out of the yard meets the line into the road
      local den = na[1] * (-nb[2]) - na[2] * (-nb[1])
      local qx, qz = pb[1] - pa[1], pb[2] - pa[2]
      local tt = (qx * (-nb[2]) - qz * (-nb[1])) / den
      local B = { pa[1] + na[1] * tt, pa[2] + na[2] * tt }
      local cart = { type = "road", width = 8, a_y = Yp.pos[2], b_y = R.pos[2], sky = true, keep = true, a_side = a.h, b_side = OPP[sg.h],
        a_at = { pa[1], pa[2] }, b_at = { pb[1], pb[2] }, path = { B } }
      local M = L.mitre({ pa, B, pb }, 4)
      if not M or not route_clear(C, M, Yp, R) then return nil, "bend road " .. s end
      C:commit_free(cart, Yp, R, { { zone = Yp, side = a.h, lo = ca - 4, hi = ca + 4, y0 = Yp.pos[2], y1 = Yp.pos[2] + 8 },
        { zone = R, side = OPP[sg.h], lo = cb - 4, hi = cb + 4, y0 = R.pos[2], y1 = R.pos[2] + 8 } })
      book_route(C, cart, Yp, R, { pa, B, pb }, 4)
    end
  end
  -- the attackers' start hall behind the first road, and their staging room beside it
  local s1 = segs[1]
  local start = C:zone("start", { role = "spawn", segment = 1, size = { rng.pick({ 18, 20, 22 }), rng.pick({ 14, 16 }) }, y = 0,
    palette = { theme = themes[1], variant = 2, team = "blue" } })
  local stx, stz = hv(s1, 0, -1 - start.size[2] / 2)
  local iS = C:rect_info(start, 0, stx, stz)
  if not C:fits(iS) then return nil, "start" end
  C:add(start, iS)
  if not C:commit_classic({ type = "doorway", width = 4, a_y = 0, b_y = 0, centre = true, keep = true }, start, Z.R[1]) then return nil, "start door" end
  -- 2. The rooms of every segment, laid out in its frame by construction (heading coordinates: u across, v along).
  -- Each side of the track carries a band of rooms that meet wall to wall: a room at one of the road's level pads
  -- (on the flank side the entry pad at the track's level, on the high side the exit pad a storey up) and a room
  -- beside the yard that starts where it ends (a doorway or drop through the 1 m wall between them, or a climb's run
  -- apart when a storey differs). On the flank side that room is a long low gallery from the road's middle along
  -- the yard (the flank route itself). The flank keeps to the outside of the turns, the high ground to the inside;
  -- at a corner the yard's high room stands on its front wall, a stair's run from the next leg's flank.
  local used = { hall = 1 }
  local flank_pool = { "hall", "storage", "colonnade", "gallery", "pit", "courtyard" }
  local high_pool = { "overlook", "courtyard", "hall", "colonnade", "gallery", "overlook" }
  local spine_area = 0
  for s = 1, nseg do spine_area = spine_area + L.zone_area(Z.R[s]) + L.zone_area(Z.Y[s]) end
  local per = clamp((G.target_area * 0.92 - spine_area - 700) / (4 * nseg + 1), 300, 640)
  local room_cap = math.huge
  for s = 1, nseg - 1 do room_cap = math.min(room_cap, 0.85 * L.zone_area(Z.Y[s])) end
  local stair_gap = L.stair_run(step) + 2.5
  local ramp_gap = L.ramp_run(step) + 2.5
  -- the gap a climb of one storey takes between two walls (a stair's run or a ramp's, the seeded stream's pick)
  local function climb_gap() return rng.chance(0.6) and stair_gap or ramp_gap end
  -- The shape a room keeps: most plain (no chamfer), some with the archetype's corners, a few with large ones; halls
  -- and colonnades take their headroom by size (tall when big, low when small).
  local function style(z, kind)
    if kind == "gallery" then z.cmode = "plain"
    else
      local r = rng.float()
      z.cmode = (r < 0.66) and "plain" or ((r < 0.86) and "arch" or "large")
    end
    z.cf = rng.range(0.18, 0.26)
    z.h_tall, z.h_low = rng.pick({ 8, 9, 10 }), rng.pick({ 6, 6.5, 7 })
  end
  local function finish_styled(z)
    L.finish_zone(z, step)
    -- (a large chamfer is cut once the room's openings are known, see below)
    if z.cmode == "plain" then z.chamfer = 0 end
    if not z.h_tall then return end
    if z.archetype == "hall" then z.height = (L.zone_area(z) >= 340) and z.h_tall or z.h_low
    elseif z.archetype == "colonnade" then z.height = (L.zone_area(z) >= 400) and math.max(9, z.h_tall) or 7 end
  end
  -- An archetype from pool that takes at least across x along in heading h, and its size limits (across, along).
  local function make_room(pool, role, s, y, across, along, variant, arch, kind)
    local sg = segs[s]
    local cand = {}
    for _, a in ipairs(arch and {} or pool) do
      local A = LA[a]
      -- (dims turns {w, d} into {across, along} the same way it turns {across, along} into {w, d})
      local mn, mx = dims(sg.h, A.min[1], A.min[2]), dims(sg.h, A.max[1], A.max[2])
      if ((used[a] or 0) < DATA.repeat_max or A.repeatable) and mn[1] <= across and mx[1] >= across and mn[2] <= along then cand[#cand + 1] = a end
    end
    if #cand == 0 and not arch then
      -- (every kind that takes the size is used twice: the widest that is still free, or a terrace)
      for _, a in ipairs({ "colonnade", "hall", "courtyard", "gallery", "overlook" }) do
        local A = LA[a]
        local mx = dims(sg.h, A.max[1], A.max[2])
        if #cand == 0 and ((used[a] or 0) < DATA.repeat_max or A.repeatable) and mx[1] >= across then cand[1] = a end
      end
      if #cand == 0 then cand = { "overlook" } end
    end
    -- (flank rooms lean to the low-ceilinged kinds: the track, the yards and the terraces are tall)
    local low = {}
    for _, c in ipairs(cand) do if role == "flank" and (c == "storage" or c == "hall") then low[#low + 1] = c end end
    local a = arch or ((#low > 0 and rng.chance(0.6)) and rng.pick(low) or rng.pick(cand))
    used[a] = (used[a] or 0) + 1
    local A = LA[a]
    local mn, mx = dims(sg.h, A.min[1], A.min[2]), dims(sg.h, A.max[1], A.max[2])
    local z = C:zone(a, { role = role, segment = s, size = { mn[1], mn[2] }, y = y, params = { split = sg.h }, palette = { theme = sg.theme, variant = variant } })
    style(z, kind)
    z.lim = { mn[1], mx[1], mn[2], mx[2] }
    return z
  end
  -- Places z at a heading-coordinate rectangle of segment sg (u0 < u1, v0 < v1); nil when it does not fit.
  local function place_hv(z, sg, u0, u1, v0, v1)
    local ax, az = hv(sg, u0, v0)
    local bx, bz = hv(sg, u1, v1)
    local x0, x1, z0, z1 = math.min(ax, bx), math.max(ax, bx), math.min(az, bz), math.max(az, bz)
    z.size = { x1 - x0, z1 - z0 }
    finish_styled(z)
    local info = C:rect_info(z, sg.th, (x0 + x1) / 2, (z0 + z1) / 2)
    if not C:fits(info) then return nil end
    C:add(z, info)
    z.hrect = { u0, u1, v0, v1 }
    return info
  end
  -- Tries a room's rectangle and smaller or shifted ones: inner edge at u_in on side sx (+1 right, -1 left),
  -- width W, from v0 to v1 anchored at the end given (anchor "v0" keeps v0, "v1" keeps v1).
  local function place_band(z, sg, sx, u_in, W, v0, v1, anchor, accept)
    local lim = z.lim
    local D = v1 - v0
    local tries = { { W, D, 0 }, { W, D - 4, 0 }, { W - 4, D, 0 }, { W + 4, D, 0 }, { W, D + 4, 0 }, { W - 4, D - 4, 0 }, { W - 6, D - 6, 0 },
      { W - 8, D, 0 }, { W - 8, D - 6, 0 }, { lim[1], D - 4, 0 }, { lim[1], lim[3], 0 }, { W, D, 3 }, { lim[1], lim[3], 4 },
      -- (further out from the wall it meets: a longer doorway or corridor to it)
      { W - 4, D - 4, 0, 3 }, { lim[1], lim[3], 0, 4 }, { lim[1], lim[3], 4, 6 } }
    for _, tr in ipairs(tries) do
      local w, d = clamp(even(tr[1]), lim[1], lim[2]), clamp(even(tr[2]), lim[3], lim[4])
      -- (no room as big as a checkpoint yard: the yards are the map's big spaces)
      if w * d > room_cap then d = math.max(lim[3], math.floor(room_cap / w / 2) * 2) end
      local a0 = (anchor == "v1") and (v1 - d - tr[3]) or (v0 + tr[3])
      local ui = u_in + (tr[4] or 0)
      local ua, ub = ui, ui + w
      if sx < 0 then ua, ub = -ui - w, -ui end
      if place_hv(z, sg, ua, ub, a0, a0 + d) then
        z.hrect = { ui, ui + w, a0, a0 + d }
        -- (accept: the links that must land, made now; a place they do not fit is given up)
        if not accept or accept(z) then return true end
        C:remove_zone(z)
      end
    end
    return false
  end
  -- A long low gallery on side sx from u0 to u1 (metres out from the track's centre line) and v0 to v1: a tunnel
  -- while the kind is free and the size fits it, else a traced room (the outer corner at the cant end canted), a
  -- sixteenth past a wall's thickness from its neighbours. One reached by a climb is a rectangle (a tunnel, else the
  -- narrowest long hall that is free, as long as it allows): a free stair or ramp lines up square only with a
  -- rectangle's wall. Returns the zone or nil.
  local function make_gallery(s, sx, u0, u1, v0, v1, y, cant, accept, climb)
    local sg = segs[s]
    local kinds = climb and { "tunnel", "hall", "storage", "colonnade" } or { "tunnel" }
    for _, a in ipairs(kinds) do
      local A = LA[a]
      local mn, mx = dims(sg.h, A.min[1], A.min[2]), dims(sg.h, A.max[1], A.max[2])
      local w = clamp(u1 - u0, mn[1], mx[1])
      local d = clamp(v1 - v0, mn[2], mx[2])
      -- (a tunnel takes the size as it is; a hall only when a climb needs one, reaching as far along as it can)
      local ok = (used[a] or 0) < DATA.repeat_max and ((a == "tunnel" and w == u1 - u0 and d == v1 - v0) or (a ~= "tunnel" and d >= v1 - v0 - 12))
      if ok then
        local z = C:zone(a, { role = "flank", segment = s, size = { 8, 18 }, y = y, params = { split = sg.h }, palette = { theme = sg.theme, variant = 1 } })
        style(z, "gallery")
        local ua, ub = u0, u0 + w
        if sx < 0 then ua, ub = -u0 - w, -u0 end
        if place_hv(z, sg, ua, ub, v0, v0 + d) then
          z.hrect = { u0, u0 + w, v0, v0 + d }
          if not accept or accept(z) then used[a] = (used[a] or 0) + 1 return z end
          C:remove_zone(z)
        end
      end
    end
    local e = 0.125
    local uv = { { u0 + e, v0 + e }, { u1 - e, v0 + e }, { u1 - e, v1 - e }, { u0 + e, v1 - e } }
    if cant and u1 - u0 >= 10 - EPS then
      local cu, cv = 2, q2(rng.range(4, 8))
      if cant == "v0" then uv = { { u0 + e, v0 + e }, { u1 - e - cu, v0 + e }, { u1 - e, v0 + e + cv }, { u1 - e, v1 - e }, { u0 + e, v1 - e } }
      else uv = { { u0 + e, v0 + e }, { u1 - e, v0 + e }, { u1 - e, v1 - e - cv }, { u1 - e - cu, v1 - e }, { u0 + e, v1 - e } } end
    end
    local pts = {}
    for i, q in ipairs(uv) do local x, zz = hw(sg, sx * q[1], q[2]) pts[i] = { snap16(x), snap16(zz) } end
    pts = L.ccw(pts)
    if not L.simple(pts) then return nil end
    local info = C:poly_info(pts)
    if not C:fits(info) then return nil end
    local z = C:zone("poly", { role = "flank", segment = s, outline = pts, y = y, palette = { theme = sg.theme, variant = 1 } })
    z.height = rng.pick({ 4.5, 5, 5.5 })
    C:add(z, info)
    z.hrect = { u0 + e, u1 - e, v0 + e, v1 - e }
    if not accept or accept(z) then return z end
    C:remove_zone(z)
    return nil
  end
  -- the sides of every segment
  for s = 1, nseg do
    local sg = segs[s]
    local Jp, Jn = joints[s - 1], joints[s]
    local rs = Jp and outside_of(Jp.sign, sg.h) or outside_of(Jn.sign, sg.h)
    local sd = { rs = rs, hs = OPP[rs] }
    if s == nseg then sd.yo, sd.yh = sd.rs, sd.hs
    elseif Jn.kind == "bend" then sd.yo = outside_of(Jn.sign, sg.h) sd.yh = OPP[sd.yo]
    else sd.yo, sd.yh = outside_of(Jn.sign, sg.h), sg.h end
    Z.side[s] = sd
  end
  local function sx_of(sg, side) return (side == RIGHT[sg.h]) and 1 or -1 end
  local function yard_u(s, side)
    local sg = segs[s]
    return sx_of(sg, side) * sg.sig + sg.yw / 2 + 1
  end
  -- the corner's turn side, when segment s ends in the corner
  local function turn_side(s)
    local J = joints[s]
    if J and J.kind == "corner" then return (J.sign > 0) and RIGHT[segs[s].h] or LEFT[segs[s].h] end
    return nil
  end
  -- the yard room a side carries ("yo" the flank, "yh" the high room, nil on the corner's turn side) and its floor
  local function yard_kind(s, side)
    local sd = Z.side[s]
    if side == turn_side(s) then return nil end
    if side == sd.yo then return "yo" end
    if side == sd.yh then return "yh" end
    return nil
  end
  local function yard_level(s, kind)
    local sg = segs[s]
    if s == nseg or kind == "yh" then return sg.y_out + step end
    return sg.y_out
  end
  -- a doorway between a road and a room beside one of its level pads at floor y
  local function road_door(R, z, y)
    if z.archetype == "poly" then return poly_link(C, R, z, { keep = true }) end
    local l = { type = "doorway", width = rng.pick({ 3, 4 }), a_y = y, b_y = y, keep = true }
    if C:commit_classic(l, R, z) then return l end
    return nil
  end
  -- 2a. Road rooms: the flank room at the entry pad (the track's level there), the high room at the exit pad (a
  -- storey above the entry; a stair down to the pad when it is a storey above that too). Each is wide enough to
  -- meet the room beside the yard on its side past both rooms' corners (the yard is wider than the road); the flank
  -- room is short when a gallery runs on from it.
  for s = 1, nseg do
    local sg, sd = segs[s], Z.side[s]
    local R = Z.R[s]
    local ua = sg.rw / 2 + 1
    local last = (s == nseg)
    local kr = yard_kind(s, sd.rs)
    local W, D
    local v0 = rng.pick({ -1, 0, 0, 1 })
    if kr then
      -- (as wide as the gallery's far side, so the two meet across the gallery's whole width; a climb onto the
      -- gallery from the last leg's road room takes a 4 m stair or ramp through its end: wider then)
      sg.gal_w = (last and math.abs(sg.rise) > DATA.max_step + EPS) and rng.pick({ 10, 12 }) or rng.pick({ 8, 8, 10, 12 })
      W = math.max(14, even(yard_u(s, sd.rs) - ua + sg.gal_w + rng.pick({ 0, 2, 4 })))
      D = even(rng.range(12, 18))
      if last then W = even(rng.range(16, 22))
      elseif kr == "yo" then
        -- (the gallery that runs on from it: 26-36 m, reaching along the yard; the room takes the rest of the road.
        -- On a climbing leg, or one too long for the two, a middle room on the yard's floor between them: the
        -- climb is the stair or ramp from the road room up into it, the gallery its doorway on)
        local climb = math.abs(sg.rise) > DATA.max_step + EPS
        local need = climb and stair_gap or 1
        -- (to the yard's front, near the next leg's flank, at a bend; half way along the yard at the corner)
        if joints[s].kind == "bend" then sg.gal_g1 = sg.L + sg.g + sg.yd + rng.pick({ 4, 6, 8 })
        else sg.gal_g1 = sg.L + sg.g + clamp(sg.yd * rng.range(0.4, 0.7), 12, sg.yd) end
        local total = sg.gal_g1 - v0
        if climb or total > 30 + need + 40 then
          D = even(rng.range(14, 20))
          sg.mid_d = clamp(even(total - D - need - 1 - rng.pick({ 28, 32, 36 })), 14, 30)
        else
          D = clamp(even(sg.gal_g1 - rng.pick({ 26, 30, 34 }) - need - v0), 12, 30)
        end
      end
    else
      W = rng.pick({ 20, 22, 24 })
      D = clamp(even(per * rng.range(0.9, 1.1) / W), 18, math.min(30, sg.L - 2))
    end
    -- (after the corner: reaching past the corner yard's front, where its high room's stair comes down)
    local Jc = joints[s - 1]
    if Jc and Jc.kind == "corner" then W = math.max(W, even(segs[s - 1].yd / 2 - Jc.tau - sg.rw / 2 + 12)) end
    local re = make_room(flank_pool, "flank", s, sg.y_in, W, D, 3, nil, "room")
    if not place_band(re, sg, sx_of(sg, sd.rs), ua, W, v0, v0 + D, "v0") then return nil, "flank " .. s end
    re.bside = sd.rs
    road_door(R, re, sg.y_in)
    local hy = sg.y_in + step
    local dy = hy - sg.y_out
    local gapA = (math.abs(dy) <= DATA.max_step + EPS) and 1 or climb_gap()
    local kh = yard_kind(s, sd.hs)
    local W2 = kh and math.max(20, even(yard_u(s, sd.hs) - (sg.rw / 2 + gapA) + rng.pick({ 10, 12, 14 }))) or rng.pick({ 20, 22, 24 })
    local D2 = clamp(even(per * rng.range(0.8, 1.1) / W2), 18, math.min(30, sg.L))
    -- (after a bend, on its inside: a long terrace beside the whole road, from the bend, where the last leg's high
    -- room meets it, to the exit pad)
    local Jp = joints[s - 1]
    if Jp and Jp.kind == "bend" and sd.hs == outside_of(-Jp.sign, sg.h) then
      W2 = math.max(14, even(yard_u(s, sd.hs) - (sg.rw / 2 + gapA) + rng.pick({ 10, 12 })))
      D2 = even(sg.L + sg.g - 1 - rng.pick({ 0, 1, 2 }))
    end
    local rx = make_room(high_pool, "high", s, hy, W2, math.min(D2, 30), 2, nil, "room")
    -- (no two neighbours of one size)
    if D2 == D and W2 == W then D2 = D2 + 2 end
    rx.bside = sd.hs
    if not place_band(rx, sg, sx_of(sg, sd.hs), sg.rw / 2 + gapA, W2, sg.L + sg.g - 1 - D2, sg.L + sg.g - 1, "v1") then
      -- (no room on its side, e.g. inside a turn the previous leg's rooms fill: on the flank's side, ahead of it, or
      -- none: the high ground of this leg is then the yard's)
      if place_band(rx, sg, sx_of(sg, sd.rs), sg.rw / 2 + gapA, W2, sg.L + sg.g - 1 - D2, sg.L + sg.g - 1, "v1") then
        rx.bside = sd.rs
        sd.hs = sd.rs
        link_pair(C, re, rx, { keep = true, classic_only = true, max_gap = 34 })
      else
        used[rx.archetype] = used[rx.archetype] - 1
        rx = nil
      end
    end
    -- (above a downhill road's exit pad by two storeys: high ground over the road with no way down onto it there)
    if not rx then
    elseif gapA <= 1 then road_door(R, rx, hy)
    elseif dy <= step + 1 then
      local l = { type = "stair", width = 4, a_y = hy, b_y = sg.y_out, keep = true }
      if gapA > stair_gap + EPS and rng.chance(0.6) then l.type = "ramp" end
      if not C:commit_classic(l, rx, R) then
        l.type = (l.type == "stair") and "ramp" or "stair"
        C:commit_classic(l, rx, R)
      end
    end
    Z.re[s], Z.rx[s] = re, rx
  end
  -- 2b. Yard rooms. On each side the room beside the yard starts where the road room of that side ends (or a
  -- climb's run past it): on the flank side a long low gallery from the road's middle along the yard, else a room
  -- reaching along the yard (a doorway into the yard on the track's level, a drop from the high room; the finale
  -- takes its side rooms on its terrace, a storey up). On the last leg the flank's gallery runs beside the road to
  -- the finale's gate: a doorway into the court through its entry wall and one onto the road's exit pad.
  Z.gal = {}
  local function yard_room(s, side, kind)
    local sg, sd = segs[s], Z.side[s]
    local sx = sx_of(sg, side)
    local Y, R = Z.Y[s], Z.R[s]
    local last = (s == nseg)
    -- (the road room on this side: the high room when both stand on it, none when the high room moved across)
    local RR = nil
    if Z.rx[s] and Z.rx[s].bside == side then RR = Z.rx[s] elseif Z.re[s].bside == side then RR = Z.re[s] end
    local lvl = yard_level(s, kind)
    local u_in = yard_u(s, side)
    local rv1 = RR and RR.hrect[4] or (sg.L + sg.g - 1)
    local function to_yard(z_)
      if last or kind == "yo" then return C:commit_classic({ type = "doorway", width = rng.pick({ 3, 4 }), a_y = lvl, b_y = lvl, keep = true }, z_, Y) end
      return C:commit_classic({ type = "drop", width = 4, a_y = lvl, b_y = Y.pos[2], keep = true }, z_, Y)
    end
    local function gap_to(y)
      if not RR then return 1, false end
      local d = math.abs(y - RR.pos[2])
      if d <= DATA.max_step + EPS then return 1, true end
      if d <= step + 1 + EPS then return climb_gap(), true end
      return 1, false
    end
    local need, can = gap_to(lvl)
    if kind == "yo" and side == sd.rs and RR == Z.re[s] then
      local gw = sg.gal_w or 8
      if last then
        -- the finale's gate gallery beside the road
        local ua = sg.rw / 2 + 1
        local gneed, gcan = gap_to(sg.y_out)
        local g0, g1 = rv1 + gneed, sg.L + sg.g - 1
        if g1 - g0 >= 14 then
          local Gz = make_gallery(s, sx, ua, ua + gw, g0, g1, sg.y_out, nil, function(z_)
            return connect(C, Y, z_, { keep = true, classic_only = true }) ~= nil
          end, gneed > 1)
          if Gz then
            road_door(R, Gz, sg.y_out)
            if gcan then connect(C, RR, Gz, { keep = true, classic_only = true, max_gap = gneed + 1 }) end
            Z.gal[s] = Gz
            -- (the gate gallery is the finale's flank: the defenders' rooms stand on the terrace across the court)
            return Gz
          end
        end
      else
        -- the middle room on a long or climbing leg (beside the road's graded middle, on the yard's floor)
        if sg.mid_d then
          local ua = sg.rw / 2 + 1
          local Mz = make_room(flank_pool, "flank", s, lvl, math.max(14, u_in + gw - ua), sg.mid_d, 2, nil, "room")
          local Wm = math.max(14, even(u_in + gw - ua + rng.pick({ 0, 2 })))
          if place_band(Mz, sg, sx, ua, Wm, rv1 + need, rv1 + need + sg.mid_d, "v0", function(z_)
            return not can or connect(C, RR, z_, { keep = true, classic_only = true, max_gap = need + 1 }) ~= nil
          end) then
            RR, rv1, need, can = Mz, Mz.hrect[4], 1, true
          else
            used[Mz.archetype] = used[Mz.archetype] - 1
          end
        end
        -- the flank gallery beside the road and on along the yard
        local g0 = rv1 + need
        local g1 = clamp(sg.gal_g1 or (g0 + 30), math.max(g0 + 3 * gw, sg.L + sg.g + 12), sg.L + sg.g + sg.yd + 8)
        -- (at most 40 m: a gallery, not a corridor; an even length keeps its walls on the grid)
        g1 = g0 + even(math.min(g1 - g0, 40))
        if g1 - g0 >= 20 and g1 >= sg.L + sg.g + 10 then
          -- (its outer corner canted at the end away from the next leg: the near end's full width takes the link on)
          local Gz = make_gallery(s, sx, u_in, u_in + gw, g0, g1, lvl, (joints[s].kind == "bend") and "v0" or "v1", function(z_)
            return connect(C, Y, z_, { keep = true, classic_only = true }) ~= nil
          end, need > 1)
          if Gz then
            if can then connect(C, RR, Gz, { keep = true, classic_only = true, max_gap = need + 1 }) end
            Z.gal[s] = Gz
            return Gz
          end
        end
      end
    end
    local z = (kind == "yo") and make_room(flank_pool, "flank", s, lvl, 18, 20, 1, nil, "room") or
      make_room(high_pool, "high", s, lvl, 16, 20, 3, (s % 2 == 1) and "overlook" or nil, "room")
    local v0 = rv1 + need
    if last and kind == "yo" and Z.gal[s] then
      local gneed = gap_to(sg.y_out)
      v0 = math.max(v0, Z.gal[s].hrect[4] + gneed)
    end
    -- (from a road room at the entry pad the room runs on along the yard; else as deep as the yard, more or less)
    local D
    if rv1 < sg.L - 4 then D = even(sg.L + sg.g + sg.yd * rng.range(0.45, 0.75) - v0)
    else D = even(sg.yd + rng.pick({ -8, -4, 0, 4 })) end
    D = clamp(D, 18, 40)
    local W = clamp(even(per * rng.range(0.85, 1.15) / D), 16, 26)
    if RR and math.abs(W - (RR.hrect[2] - RR.hrect[1])) < 0.5 then W = W + 2 end
    local placed = place_band(z, sg, sx, u_in, W, v0, v0 + D, "v0", to_yard)
    if not placed then
      -- (beside the yard only)
      local v1 = math.max(v0, sg.L + sg.g)
      placed = place_band(z, sg, sx, u_in, W, v1, v1 + clamp(even(sg.yd + rng.pick({ -8, -4, 0 })), 18, 28), "v0", to_yard)
    end
    if not placed then return nil end
    if can then connect(C, RR, z, { keep = true, classic_only = true, max_gap = need + 1 }) end
    if not (RR and C:linked(RR, z)) and last and kind == "yo" and Z.gal[s] then
      connect(C, Z.gal[s], z, { keep = true, classic_only = true, max_gap = 12 })
    end
    return z
  end
  for s = 1, nseg do
    local sg, sd = segs[s], Z.side[s]
    local Y = Z.Y[s]
    local Jn = joints[s]
    local last = (s == nseg)
    local yo, yh
    if not last and Jn.kind == "corner" then
      -- the high room on the yard's front wall (a drop into the yard), its end a stair's run from the next road's
      -- flank (the stair down to it)
      local T = turn_side(s)
      local tx = sx_of(sg, T)
      local nre = Z.re[s + 1]
      local cg = stair_gap
      yh = make_room(high_pool, "high", s, sg.y_out + step, 18, 18, 3, (s % 2 == 1) and "overlook" or nil, "room")
      yh.cmode = "plain"
      local W = rng.pick({ 22, 24, 26 })
      local D = clamp(even(per * rng.range(0.9, 1.1) / W), 18, 26)
      D = math.max(18, math.min(D, math.floor(room_cap / (W + 4) / 2) * 2))
      local y1 = sg.L + sg.g + sg.yd
      -- (the next road's flank, across this frame's heading)
      local nx0, nz0, nx1, nz1 = L.rect_of(nre)
      local r = DIRV[RIGHT[sg.h]]
      local us = {}
      for _, q in ipairs({ { nx0, nz0 }, { nx1, nz1 } }) do us[#us + 1] = (q[1] - sg.e[1]) * r[1] + (q[2] - sg.e[2]) * r[2] end
      local nu0, nu1 = math.min(us[1], us[2]), math.max(us[1], us[2])
      local edge = (tx > 0) and (nu0 - cg) or (nu1 + cg)
      local ok = false
      for _, w in ipairs({ W, W - 4, W + 4, yh.lim[1] }) do
        local ww = clamp(even(w), yh.lim[1], yh.lim[2])
        local dd = clamp(even(D), yh.lim[3], yh.lim[4])
        local ua, ub = edge - ww, edge
        if tx < 0 then ua, ub = edge, edge + ww end
        if not ok and place_hv(yh, sg, ua, ub, y1 + 1, y1 + 1 + dd) then
          ok = C:commit_classic({ type = "drop", width = 4, a_y = yh.pos[2], b_y = Y.pos[2], keep = true }, yh, Y)
          if not ok then C:remove_zone(yh) end
        end
      end
      if not ok then return nil, "corner front " .. s end
      link_pair(C, yh, nre, { keep = true, classic_only = true, max_gap = cg + 1, climb = "stair" })
      yo = yard_room(s, sd.yo, "yo")
      if not yo then return nil, "corner flank " .. s end
      connect(C, yo, yh, { keep = true, classic_only = true, max_gap = 12 })
      -- the inside room: into the yard, and on to the next road's entry pad
      local inr = (sd.rs == T) and Z.re[s] or Z.rx[s]
      link_pair(C, inr, Y, { keep = true, classic_only = true, max_gap = 3 })
      link_pair(C, inr, Z.R[s + 1], { keep = true, classic_only = true, max_gap = 16 })
    else
      yo = yard_room(s, sd.yo, "yo")
      if not yo then return nil, "yard flank " .. s end
      yh = yard_room(s, sd.yh, "yh")
      if not yh then return nil, "yard high " .. s end
    end
    Z.yo[s], Z.yh[s] = yo, yh
  end
  for _, z in ipairs(G.zones) do z.lim = nil end
  -- the staging room beside the start hall, meeting the first flank
  -- (beside the start hall on the first flank's side, behind the flank: a doorway into each)
  local staging = C:zone("hall", { role = "staging", segment = 1, size = side_size(LA.hall, rng.range(300, 420), rng.range(0.8, 1.2), "+z"), y = 0,
    palette = { theme = themes[1], variant = 3, team = "blue" } })
  staging.lim = { LA.hall.min[1], LA.hall.max[1], LA.hall.min[2], LA.hall.max[2] }
  local _, rz0 = L.rect_of(Z.re[1])
  local rv0 = rz0 - s1.e[2]
  local Ds = clamp(staging.size[2], 14, 20)
  local function at_start(z_)
    return C:commit_classic({ type = "doorway", width = rng.pick({ 3, 4 }), a_y = 0, b_y = 0, keep = true }, start, z_)
  end
  local placed = place_band(staging, s1, sx_of(s1, Z.side[1].rs), start.size[1] / 2 + 1, staging.size[1], rv0 - 1 - Ds, rv0 - 1, "v1", at_start)
  staging.lim = nil
  if not placed and not C:attach(staging, 0, { start }, { styles = { abut = true, wide = true, short = true }, ear = false, budget = 400 }) then return nil, "staging" end
  if not C:linked(staging, Z.re[1]) then
    if not link_pair(C, staging, Z.re[1], { keep = true, classic_only = true, max_gap = 14 }) then C:second_door(staging, {}) end
  end
  Z.start, Z.staging = start, staging
  -- 3. Across every joint: the flank and the high routes. At a bend the outside pair (the yard's flank, the next
  -- road's flank) and the inside pair (the high rooms) are tied by vestibules or computed free corridors; at a
  -- corner the rooms share the frame and meet wall to wall.
  for s = 1, nseg - 1 do
    local J = joints[s]
    local A = present(Z.yo[s], Z.yh[s], Z.rx[s], Z.Y[s], Z.re[s])
    local B = present(Z.re[s + 1], Z.rx[s + 1], Z.R[s + 1])
    local made = 0
    if J.kind == "corner" then
      for _, a in ipairs(A) do
        for _, b in ipairs(B) do
          if made < 3 and not (a == Z.Y[s] and b == Z.R[s + 1]) and link_pair(C, a, b, { keep = true, max_gap = 18, classic_only = true }) then made = made + 1 end
        end
      end
    else
      -- the outside: the yard's flank to the next road's flank; the inside: the yard's high room to the next
      -- road's high room, or down a stair onto the next road's entry pad
      if tie(C, Z.yo[s], Z.re[s + 1], s + 1) then made = made + 1 end
      local wedge = { len = even(rng.range(8, 12)), max_gap = 26 }
      if tie(C, Z.yh[s], Z.rx[s + 1], s + 1, wedge) or tie(C, Z.yh[s], Z.R[s + 1], s + 1) then made = made + 1 end
      if made < 2 then
        -- (else the nearest pairs of the two legs' rooms, the yard and the next road)
        local prs = {}
        for _, a in ipairs(A) do
          for _, b in ipairs(B) do
            if a and b and not (a == Z.Y[s] and b == Z.R[s + 1]) and not C:linked(a, b) then
              local A, B = bb_of(C.info[a]), bb_of(C.info[b])
              prs[#prs + 1] = { a, b, d = math.max(B[1] - A[3], A[1] - B[3], B[2] - A[4], A[2] - B[4], 0) }
            end
          end
        end
        for i = 2, #prs do
          local c, j = prs[i], i - 1
          while j >= 1 and prs[j].d > c.d do prs[j + 1] = prs[j] j = j - 1 end
          prs[j + 1] = c
        end
        for _, pr in ipairs(prs) do
          if made < 2 and pr.d <= 30 and tie(C, pr[1], pr[2], s + 1) then made = made + 1 end
        end
      end
    end
    -- (still short of the three routes across the gate: the nearest pairs of any rooms of the two legs, by a link
    -- on the wall or a computed corridor, stair or ramp)
    local function crossings()
      local seg, n = {}, 0
      for _, z in ipairs(G.zones) do seg[z.id] = z.segment end
      for _, l in ipairs(G.links) do
        local a, b = seg[l.a], seg[l.b]
        if L.WALK[l.type] and a and b and math.min(a, b) == s and math.max(a, b) == s + 1 then n = n + 1 end
      end
      return n
    end
    if crossings() < DATA.payload_boundary_routes then
      local prs = {}
      for _, a in ipairs(G.zones) do
        if a.segment == s and a.role ~= "spawn" and a.role ~= "staging" then
          for _, b in ipairs(G.zones) do
            if b.segment == s + 1 and not C:linked(a, b) and not (LA[a.archetype].spine and LA[b.archetype].spine) then
              local A2, B2 = bb_of(C.info[a]), bb_of(C.info[b])
              local d = math.max(B2[1] - A2[3], A2[1] - B2[3], B2[2] - A2[4], A2[2] - B2[4], 0)
              if d <= 36 then prs[#prs + 1] = { a, b, d = d } end
            end
          end
        end
      end
      for i = 2, #prs do
        local c, j = prs[i], i - 1
        while j >= 1 and prs[j].d > c.d do prs[j + 1] = prs[j] j = j - 1 end
        prs[j + 1] = c
      end
      for k = 1, math.min(12, #prs) do
        if crossings() < DATA.payload_boundary_routes then link_pair(C, prs[k][1], prs[k][2], { keep = true, max_gap = 18, max_len = 40 }) end
      end
    end
  end
  -- 4. Small traced rooms where two walls meet at an angle (between the frames of neighbouring legs: the inside of
  -- the bends), then bays off the yards' free walls (an apse or canted bay joined to the yard, a covered alcove off
  -- the open court), while the zone budget has room.
  local cap = G.budget.zones[2] - 1
  local function host_segment(Pz)
    for _, l in ipairs(G.links) do
      local o = (l.a == Pz.id) and l.b or ((l.b == Pz.id) and l.a or nil)
      if o then local h = C:byid(o) if h and h.segment then return h.segment end end
    end
    return nil
  end
  local nc = math.min(rng.pick({ 3, 4 }), cap - #G.zones)
  if nc > 0 then
    for _, Pz in ipairs(C:corner_rooms(nc, { area = rng.range(60, 100), amin = 40, amax = 110, reach = 24, variants = 3, role = "room",
      height = function() return rng.pick({ 4.5, 5, 5.5 }) end })) do Pz.segment = host_segment(Pz) end
  end
  for s = 1, nseg - 1 do
    -- Bays go on unturned yards only: a traced bay joined to a turned yard's wall leaves an open edge on the joined
    -- boundary in the sealed build.
    if #G.zones < cap and L.zone_rot(Z.Y[s]) == 0 then
      local Y = Z.Y[s]
      local sides = { "+x", "-x", "+z", "-z" }
      rng.shuffle(sides)
      for _, side in ipairs(sides) do
        if #G.zones < cap then
          local Bz = C:bay(Y, side, {})
          if Bz then Bz.segment = s break end
        end
      end
    end
  end
  return Z
end

-- The payload rules the loop closing keeps: three routes across every checkpoint.
local function payload_rules(nseg)
  return function(C)
    local G = C.G
    local seg = {}
    for _, z in ipairs(G.zones) do seg[z.id] = z.segment end
    local d = 0
    local cross = {}
    for _, l in ipairs(G.links) do
      local a, b = seg[l.a], seg[l.b]
      if L.WALK[l.type] and a and b and math.abs(a - b) == 1 then
        local k = math.min(a, b)
        cross[k] = (cross[k] or 0) + 1
      end
    end
    for s = 1, nseg - 1 do
      local c = cross[s] or 0
      if c < DATA.payload_boundary_routes then d = d + 2 * (DATA.payload_boundary_routes - c) end
    end
    return d
  end
end

-- The plan gap between two zones' world bounding boxes.
local function box_gap(C, a, b)
  local A, B = bb_of(C.info[a]), bb_of(C.info[b])
  return math.max(B[1] - A[3], A[1] - B[3], B[2] - A[4], A[2] - B[4], 0)
end

-- Links zone z to the nearest zones of its own or a neighbouring segment (from pool, nearest first) until one
-- link is made.
local function link_out(C, z, pool, max_d)
  local cand = {}
  for _, p in ipairs(pool) do
    if p ~= z and C.info[p] and not C:linked(z, p) and math.abs((p.segment or 0) - (z.segment or 0)) <= 1 then
      local d = box_gap(C, z, p)
      if d <= max_d then cand[#cand + 1] = { p = p, d = d } end
    end
  end
  for i = 2, #cand do
    local c, j = cand[i], i - 1
    while j >= 1 and cand[j].d > c.d do cand[j + 1] = cand[j] j = j - 1 end
    cand[j + 1] = c
  end
  for k = 1, math.min(6, #cand) do
    if link_pair(C, z, cand[k].p, { keep = true, max_gap = max_d + 2, max_len = max_d + 10 }) then return true end
  end
  return false
end

-- Gives zones with one way in a second, and the zones behind an articulation zone a way round it (wall to wall, or
-- computed free corridors; nothing is routed blind).
local function rescue(C)
  local G = C.G
  for _, z in ipairs(G.zones) do
    if C:degree(z) < 2 then link_out(C, z, G.zones, 22) end
  end
  for _ = 1, 4 do
    local idx = L.index_zones(G)
    local adj = L.adjacency(G, idx)
    local area = C:area()
    local cut = nil
    for _, a in ipairs(L.articulation(G, idx, adj)) do
      if a.cut >= DATA.articulation_share * area - EPS and (not cut or a.cut > cut.cut) then cut = a end
    end
    if not cut then break end
    local set = {}
    for _, id in ipairs(cut.behind) do set[id] = true end
    local behind, rest = {}, {}
    for _, z in ipairs(G.zones) do
      if set[z.id] then behind[#behind + 1] = z elseif z.id ~= cut.zone then rest[#rest + 1] = z end
    end
    local ok = false
    for _, z in ipairs(behind) do if not ok then ok = link_out(C, z, rest, 22) end end
    if not ok then break end
  end
end

-- Zone ids in order (z1, z2, ...); links and objectives follow.
local function renumber(G)
  local map = {}
  for i, z in ipairs(G.zones) do map[z.id] = "z" .. i end
  for i, z in ipairs(G.zones) do z.id = "z" .. i end
  for i, l in ipairs(G.links) do
    l.a, l.b, l.id = map[l.a] or l.a, map[l.b] or l.b, "l" .. i
    l.free_link, l.est_len, l.keep, l.centre = nil, nil, nil, nil
  end
  for _, o in ipairs(G.objectives) do
    if o.zone then o.zone = map[o.zone] or o.zone end
    if o.zones then for i, id in ipairs(o.zones) do o.zones[i] = map[id] or id end end
  end
end

local function gen_payload(G, rng, t)
  local P, Z, C = nil, nil, nil
  for _ = 1, 4 do
    G.zones, G.links, G.objectives = {}, {}, {}
    P = programme(G, rng)
    C = LC.new_comp(G, rng, false)
    C.fits = fits
    Z = compose(C, P, G, rng)
    if Z then break end
  end
  if not Z then return true end
  local nseg = P.nseg
  -- loops inside each segment and across its joints (classic links between rooms of one frame)
  local rules = payload_rules(nseg)
  local function skip(za, zb)
    if not za.segment or not zb.segment or math.abs(za.segment - zb.segment) > 1 then return true end
    return false
  end
  if C:deficit(rules, 2) > 0 then rescue(C) end
  -- (the bands and ties already loop every segment: more wall-to-wall links only where a rule still asks)
  if C:deficit(rules, 2) > 0 then
    local cands = C:extra_candidates({ free = false, skip = skip })
    C:close_loops(cands, { need_levels = 2, cap = 4, p_add = 0.3, p_prune = 0.3, rules = rules,
      required = function(l) return l.type == "join" or l.keep end })
  end
  -- (every free link's route is computed here with its ports and waypoints: kit.layout's resolve checks them)
  if C:deficit(rules, 2) > 0 then rescue(C) end
  -- bays left with their one way in (the join into the yard) become reward nooks, one per 1,500 m2, or go
  local nooks, nook_max = 0, math.max(1, math.floor(C:area() / DATA.nook_area))
  local list = {}
  for _, z in ipairs(G.zones) do list[#list + 1] = z end
  for _, z in ipairs(list) do
    if z.role == "bay" then
      if C:degree(z) >= 2 then z.role = "room"
      elseif nooks < nook_max then z.role = "nook" nooks = nooks + 1
      else C:remove_zone(z) end
    end
  end
  -- Large chamfers: cut on the rooms the programme gave one, as deep as their openings allow (every booked opening
  -- stays inside the walls' straight part).
  local joined = {}
  for _, l in ipairs(G.links) do if l.type == "join" then joined[l.a] = true joined[l.b] = true end end
  for _, z in ipairs(G.zones) do
    -- (a joined room keeps its corners: a join opens where the two plans touch)
    if z.cmode == "large" and C.info[z] and C.info[z].r and not joined[z.id] then
      local c = q2(clamp(math.min(z.size[1], z.size[2]) * z.cf, 3, 7))
      while c > (z.chamfer or 0) do
        local ok = true
        for _, side in ipairs(SIDES) do
          local lo, hi = L.wall_span(z, side)
          for _, u in ipairs(C:used_of(z, side)) do if u.lo < lo + c + 0.75 - EPS or u.hi > hi - c - 0.75 + EPS then ok = false end end
        end
        if ok then z.chamfer = c break end
        c = c - 0.5
      end
    end
    z.cmode, z.cf, z.h_tall, z.h_low, z.hrect, z.bside = nil, nil, nil, nil, nil, nil
  end
  -- Objectives: the cart along the spine, checkpoints at the yards, spawn groups with the hand-off.
  local spine = {}
  for s = 1, nseg do spine[#spine + 1] = Z.R[s].id spine[#spine + 1] = Z.Y[s].id end
  G.objectives[#G.objectives + 1] = { kind = "cart", zones = spine, attacking_team = "blue" }
  local function spawns(zone, team, role, count, group)
    local id = "s" .. (#G.objectives + 1)
    G.objectives[#G.objectives + 1] = { kind = "spawns", id = id, zone = zone.id, team = team, role = role, count = count, group = group }
    return id
  end
  -- Every spawn group belongs to the checkpoint it serves: Alpine switches the respawns a checkpoint links ON while
  -- that checkpoint is the cart's next target and OFF once it is captured, and never touches a respawn no checkpoint
  -- links (alpinefaction game_patch/multi/gametype.cpp:877-887 rev_should_enable_respawn_point, 986-1000
  -- koth_update_respawn_points, 2144-2152 the hill's linked respawns). So checkpoint 1 takes the starting groups,
  -- checkpoint s the attackers' forward group beside checkpoint s-1 and the defenders' hold beside checkpoint s.
  local first = { spawns(Z.start, "blue", "attack", 6, "cp1"), spawns(Z.staging, "blue", "attack", 2, "cp1") }
  -- (each segment's spawn room: the first of its yard rooms and road rooms that is a full room, not a low gallery
  -- or a traced room, with two ways out; the finale's defenders hold its terrace room)
  local flank_cp = {}
  for s = 1, nseg do
    for _, z in ipairs(present(Z.yo[s], Z.yh[s], Z.rx[s], Z.re[s])) do
      if not flank_cp[s] and z.archetype ~= "poly" and z.archetype ~= "tunnel" and C.info[z] and L.zone_area(z) >= 180 and C:degree(z) >= 2 then flank_cp[s] = z end
    end
    flank_cp[s] = flank_cp[s] or Z.yo[s] or Z.yh[s]
  end
  first[3] = spawns(flank_cp[1], "red", "defend", 6, "cp1")
  for s = 1, nseg do
    local cp = { kind = "checkpoint", index = s, zone = Z.Y[s].id, spawns = {} }
    if s == 1 then cp.spawns = first
    else
      cp.spawns[1] = spawns(flank_cp[s - 1], "blue", "attack", 6, "cp" .. s)
      cp.spawns[2] = spawns(flank_cp[s], "red", "defend", 6, "cp" .. s)
    end
    G.objectives[#G.objectives + 1] = cp
  end
  G.segments = nseg
  renumber(G)
  return true
end

L.generators.payload = gen_payload

end

return install
