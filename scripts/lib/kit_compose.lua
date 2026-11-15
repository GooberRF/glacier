-- Glacier design kit: the layout composer (installed into the kit by kit.lua after kit_layout.lua).
--
-- kit.layout generates through this module: zones are placed freely (no grid), spaces adjoin through shared
-- walls, links bend and run at angles. It builds on kit_layout's internals (I.layout: data tables, the archetype
-- library, link geometry, the resolver and the checker) and registers one generator per gametype in
-- I.layout.generators; a generator returns true when it produced the graph.
--
-- The method is anchor-and-attach growth with scored candidates, then loop closing:
--   * a programme per gametype: zones with roles, archetypes, target areas (a size hierarchy: a hub well above the
--     mean, long galleries, small rooms), storeys and palettes;
--   * growth: the anchor sits at the origin; every further zone is tried against the walls of the zones already
--     placed in its frame (each wall x join style x slide along the wall x orientation). Join styles: abut (a 1 m
--     wall with a doorway or a wide opening), join (two zones opened into one space), drop (from a higher floor
--     through an abutting wall into a tall room), short corridor, climb (the gap a stair, ramp or lift needs).
--     Candidates that collide with placed zones or links are rejected; the rest are scored (contacts with further
--     zones for loops, compactness, staggered rather than flush or centred walls, kinds of climb the storeys still
--     lack, seeded noise) and one of the best few is taken;
--   * the storeys by construction: a sunken court under the hub gives the floor below it, a two-storey hall beside
--     the hub (its gallery ring dropping into the court, its stairs) the floor above;
--   * wings: a cluster of zones in a turned frame (zone.rot), tied to the body by a traced hinge room (a poly zone
--     joined to the wing, with a doorway into the body) and closed into a loop by a vestibule, a corner room or a
--     bent free corridor; a loop wing: one turned room along a wall, hinge at one end and vestibule at the other;
--   * a perimeter gallery (DM M and L): one traced L along two sides of the mass, a doorway from a room on each;
--   * traced rooms: corner rooms in the angles the mass leaves (one edge a wall off each of two walls, a doorway
--     through each), and finished rectangle rooms re-outlined with canted corners and notches (L plans) where their
--     walls have no openings; bays (apses, canted bays) joined to halls;
--   * size, corner and headroom programme: a hub about twice the mean room, long galleries, small rooms, most
--     rectangle rooms with plain corners, tall halls and low small rooms and galleries;
--   * loop closing: every pair of zones that face each other (or lie close in different frames) is a candidate
--     link; links are added until the c.1-1 rules hold (degree, articulation, cycle rank, two routes between key
--     zones, two kinds of vertical link per storey pair, the gametype rules), the rest are added and pruned with the
--     seeded stream while the rules hold;
--   * team modes compose the red half and the centre and mirror the red half for blue.
--
-- This module returns one function, install(kit, internals). Generation uses the seeded stream it is handed (the
-- kit never reads rng), so the same seed always gives the same graph.

local function install(kit, I)
local L = I.layout
local DATA, LA, POOL = L.DATA, L.LA, L.POOL
local SIDES, OPP, DIRV = L.SIDES, L.OPP, L.DIRV
local clamp, q2, q4, even = L.clamp, L.q2, L.q4, L.even
local to_world, to_local, norm_rot = L.to_world, L.to_local, L.norm_rot
local EPS = I.EPS
local _ = kit

-- ---------------------------------------------------------------------------------------------
-- Small helpers
-- ---------------------------------------------------------------------------------------------

-- The turns of a wing's frame from the body's (degrees): mostly 30-60, sometimes shallower.
local WING_ANGLES = { 30, 45, 60, -30, -45, -60, 30, -30, 45, -45, 15, -15, 22.5, -22.5 }

local function snapv(v, k) return math.floor(v * k + 0.5) / k end
local function same_frame(a, b) return a ~= nil and b ~= nil and L.same_rot(norm_rot(a), norm_rot(b)) end
local function contains(list, v) for _, x in ipairs(list) do if x == v then return true end end return false end

-- Keeps the k best items (by item.score, best first) in a short list.
local function keep_best(list, k, item)
  local n = #list
  if n >= k and item.score <= list[n].score then return end
  local i = (n >= k) and k or (n + 1)
  list[i] = item
  while i > 1 and list[i - 1].score < list[i].score do list[i - 1], list[i] = list[i], list[i - 1] i = i - 1 end
end

-- Finishes a zone from its archetype (levels, chamfer, height), keeping the chamfer and headroom the programme
-- chose for it (want_chamfer, want_height).
local function finish(z, step)
  L.finish_zone(z, step)
  if z.want_chamfer then z.chamfer = z.want_chamfer end
  if z.want_height then z.height = z.want_height end
  return z
end

-- One of the best few: uniform among those within margin of the best.
local function pick_best(rng, list, margin)
  if #list == 0 then return nil end
  local n = 1
  while n < #list and list[n + 1].score >= list[1].score - (margin or 0.75) do n = n + 1 end
  return list[rng.int(1, n)]
end

-- A rectangle in a frame (local {x0, z0, x1, z1}) as a world polygon.
local function rect_poly(th, r)
  local out = {}
  local q = { { r[1], r[2] }, { r[3], r[2] }, { r[3], r[4] }, { r[1], r[4] } }
  for i = 1, 4 do local x, z = to_world(th, q[i][1], q[i][2]) out[i] = { x, z } end
  return out
end

-- The plan distance between two placed shapes: rectangles of one frame by their local boxes (the checker's
-- axis test), anything else by world polygons. Negative or zero when they overlap.
local function wp_of(i)
  if not i.wp then i.wp = rect_poly(i.f, i.r) end
  return i.wp
end
-- A placed shape's world plan box {x0, z0, x1, z1} (kept with the shape: the broad phase of every clearance test).
local function bb_of(i)
  local b = i.bb
  if not b then
    local x0, z0, x1, z1 = L.bbox(wp_of(i))
    b = { x0, z0, x1, z1 }
    i.bb = b
  end
  return b
end
local function box_gap(a, b)
  local gx, gz = math.max(b[1] - a[3], a[1] - b[3], 0), math.max(b[2] - a[4], a[2] - b[4], 0)
  return math.sqrt(gx * gx + gz * gz)
end
-- The plan distance up to lim: exact when the shapes' boxes come within lim, else their box gap (which is more).
local function sep(ia, ib, lim)
  if ia.r and ib.r and same_frame(ia.f, ib.f) then
    local a, b = ia.r, ib.r
    return math.max(b[1] - a[3], a[1] - b[3], b[2] - a[4], a[2] - b[4])
  end
  lim = lim or 80
  local g = box_gap(bb_of(ia), bb_of(ib))
  if g > lim then return g end
  return L.poly_dist(wp_of(ia), wp_of(ib), lim)
end
-- True when two placed shapes come closer than m.
local function near(ia, ib, m)
  if ia.r and ib.r and same_frame(ia.f, ib.f) then
    local a, b = ia.r, ib.r
    return math.max(b[1] - a[3], a[1] - b[3], b[2] - a[4], a[2] - b[4]) < m
  end
  return L.poly_near(wp_of(ia), wp_of(ib), m, bb_of(ia), bb_of(ib))
end

-- How far two rectangles of one frame overlap along the walls that face each other, and on which axis they
-- are apart: axis, gap, overlap.
local function facing(a, b)
  local gx, gz = math.max(b[1] - a[3], a[1] - b[3]), math.max(b[2] - a[4], a[2] - b[4])
  if gx > -EPS and gz < -EPS then return "x", gx, math.min(a[4], b[4]) - math.max(a[2], b[2]) end
  if gz > -EPS and gx < -EPS then return "z", gz, math.min(a[3], b[3]) - math.max(a[1], b[1]) end
  return nil
end

-- ---------------------------------------------------------------------------------------------
-- The composition state
-- ---------------------------------------------------------------------------------------------

local Comp = {}

local function new_comp(G, rng, open_air)
  local C = { G = G, rng = rng, step = G.step, info = {}, used = {}, foot = {},
    gap = open_air and 2 or DATA.min_wall, open_air = open_air and true or false }
  for k, v in pairs(Comp) do C[k] = v end
  return C
end

function Comp:byid(id) for _, z in ipairs(self.G.zones) do if z.id == id then return z end end return nil end

-- A zone from an archetype (sized, levels, chamfer and height from the archetype); unplaced until put/add.
function Comp:zone(arch, o)
  local z = { archetype = arch, role = o.role, team = o.team, lane = o.lane, segment = o.segment, params = o.params or {},
    palette = o.palette, pos = { 0, o.y or 0, 0 } }
  if o.size then z.size = { o.size[1], o.size[2] } end
  if o.outline then
    z.outline = o.outline
    local x0, z0, x1, z1 = L.bbox(o.outline)
    z.pos[1], z.pos[3] = (x0 + x1) / 2, (z0 + z1) / 2
    z.size = { x1 - x0, z1 - z0 }
  end
  if o.sky then z.sky = true end
  finish(z, self.step)
  if o.sky then z.sky = true end
  if o.label then z.label = o.label end
  return z
end

-- The placed shape of a rectangle zone with local centre (cx, cz) in frame th.
function Comp:rect_info(z, th, cx, cz)
  local w, d = z.size[1], z.size[2]
  local r = { cx - w / 2, cz - d / 2, cx + w / 2, cz + d / 2 }
  return { f = norm_rot(th), r = r, c = { cx, cz } }
end

function Comp:poly_info(pts) return { wp = L.ccw(pts) } end

-- Sets a zone's pos / rot from a rectangle shape (no registration).
function Comp:put(z, info)
  if info.r then
    local th = info.f
    z.rot = (th ~= 0) and th or nil
    local x, zz = to_world(th, info.c[1], info.c[2])
    z.pos[1], z.pos[3] = x, zz
  end
end

function Comp:add(z, info)
  self.version = (self.version or 0) + 1
  self:put(z, info)
  -- (ids stay unique while zones come and go; renumber makes them z1, z2, ... at the end)
  if not z.id then
    self.nid = (self.nid or 0) + 1
    z.id = "z" .. self.nid
  end
  self.G.zones[#self.G.zones + 1] = z
  if info.r then wp_of(info) end
  self.info[z] = info
  return z
end

function Comp:remove_zone(z)
  self.version = (self.version or 0) + 1
  local kept = {}
  for _, x in ipairs(self.G.zones) do if x ~= z then kept[#kept + 1] = x end end
  self.G.zones = kept
  local links = {}
  for _, l in ipairs(self.G.links) do if l.a ~= z.id and l.b ~= z.id then links[#links + 1] = l else self:release(l) end end
  self.G.links = links
  self.info[z] = nil
end

-- True when a shape keeps the clearance (self.gap, or m) from every placed zone except those in skip, and from
-- every link footprint.
function Comp:fits(info, skip, m)
  m = m or self.gap
  for _, p in ipairs(self.G.zones) do
    if not (skip and skip[p]) then
      local ip = self.info[p]
      if ip and near(info, ip, m - EPS) then return false end
    end
  end
  for _, f in ipairs(self.foot) do
    if not (skip and (skip[f.za] or skip[f.zb])) and near(info, f, DATA.min_wall - EPS) then return false end
  end
  return true
end

-- The area-weighted centre of the placed zones (world).
function Comp:centroid()
  local sx, sz, sa = 0, 0, 0
  for _, z in ipairs(self.G.zones) do
    local a = L.zone_area(z)
    local cx, cz = L.centre_of(z)
    sx, sz, sa = sx + cx * a, sz + cz * a, sa + a
  end
  if sa == 0 then return 0, 0, 1 end
  return sx / sa, sz / sa, sa
end

-- ---------------------------------------------------------------------------------------------
-- Ports: the composer keeps the resolver's book of used wall spans for joins and classic links, so the offsets
-- it chooses are the ones the resolver accepts.
-- ---------------------------------------------------------------------------------------------

function Comp:used_of(z, side)
  local u = self.used[z]
  if not u then u = {} self.used[z] = u end
  local s = u[side]
  if not s then s = {} u[side] = s end
  return s
end

-- The centre intervals a classic link can take on its two walls (the resolver's rules), or nil.
function Comp:classic_spans(l, za, zb)
  local f = L.link_frame(za, zb)
  if not f then return nil end
  if f.gap < L.gap_need(l, za, zb) - EPS then return nil end
  local W = L.port_width(l)
  local oa, ob = L.port_offsets(l, za, zb)
  local free = L.intersect(L.intersect(L.ranges_of(za, f.side_a, oa), L.ranges_of(zb, f.side_b, ob)), { { f.lo, f.hi } })
  local ba, bb = L.port_bands(l, za, zb, self.G)
  for _, s in ipairs(self:used_of(za, f.side_a)) do
    if s.y0 < ba[2] + 0.5 and s.y1 > ba[1] - 0.5 then free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end
  end
  for _, s in ipairs(self:used_of(zb, f.side_b)) do
    if s.y0 < bb[2] + 0.5 and s.y1 > bb[1] - 0.5 then free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end
  end
  free = self:slab_clear(za, zb, f, free)
  -- team modes: a red opening on a centre zone's wall across the axis keeps clear of the axis (its blue twin
  -- takes the mirrored span)
  if self.mirror and (za.centre ~= zb.centre) and (f.axis == "z") then free = L.subtract(free, -0.75, 0.75) end
  local cs = {}
  for _, s in ipairs(free) do if s[2] - s[1] >= W - EPS then cs[#cs + 1] = { s[1] + W / 2, s[2] - W / 2 } end end
  if #cs == 0 then return nil end
  return cs, f, W, ba, bb
end

-- The plan box of a placed shape in frame th (its own box when it is a rectangle of that frame).
function Comp:box_in(i, th)
  if i.r and same_frame(i.f, th) then return i.r end
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, q in ipairs(wp_of(i)) do
    local x, z = to_local(th, q[1], q[2])
    x0, z0, x1, z1 = math.min(x0, x), math.min(z0, z), math.max(x1, x), math.max(z1, z)
  end
  return { x0, z0, x1, z1 }
end

-- Opening spans less what stands in the gap between two facing walls: a link's passage keeps a wall's thickness
-- from every other zone and link there.
function Comp:slab_clear(za, zb, f, free)
  local a, b = math.min(f.wa, f.wb), math.max(f.wa, f.wb)
  if b - a <= 1.25 then return free end
  local th = norm_rot(za.rot)
  local m = DATA.min_wall
  local function block(r)
    local p0, p1, q0, q1
    if f.axis == "x" then p0, p1, q0, q1 = r[1], r[3], r[2], r[4] else p0, p1, q0, q1 = r[2], r[4], r[1], r[3] end
    if p1 > a + EPS and p0 < b - EPS then free = L.subtract(free, q0 - m, q1 + m) end
  end
  for _, p in ipairs(self.G.zones) do
    local ip = self.info[p]
    if p ~= za and p ~= zb and ip then block(self:box_in(ip, th)) end
  end
  for _, ft in ipairs(self.foot) do block(self:box_in(ft, th)) end
  return free
end

-- Commits a classic link: chooses its offset (doors near the ends of the free span as often as not), books the
-- spans and the footprint, appends it to the graph. offset: a wanted offset (mirrored twins) or nil.
function Comp:commit_classic(l, za, zb, offset)
  local cs, f, W, ba, bb = self:classic_spans(l, za, zb)
  if not cs then return false end
  local rng = self.rng
  local c = nil
  if offset then
    for _, s in ipairs(cs) do if offset >= s[1] - EPS and offset <= s[2] + EPS then c = offset end end
    if not c then return false end
  else
    local s = cs[rng.int(1, #cs)]
    local span = s[2] - s[1]
    if l.centre then c = (s[1] + s[2]) / 2
    elseif span > 2 and rng.chance(0.55) then
      local e = rng.range(0, math.min(2, span / 4))
      c = rng.chance(0.5) and (s[1] + e) or (s[2] - e)
    else c = rng.range(s[1], s[2]) end
    c = q4(c)
    if c < s[1] - EPS then c = c + 0.25 end
    if c > s[2] + EPS then c = c - 0.25 end
    if c < s[1] - EPS or c > s[2] + EPS then c = (s[1] + s[2]) / 2 end
  end
  self.version = (self.version or 0) + 1
  l.offset = c
  l.centre = nil
  l.a, l.b = za.id, zb.id
  local ua, ub = self:used_of(za, f.side_a), self:used_of(zb, f.side_b)
  ua[#ua + 1] = { lo = c - W / 2, hi = c + W / 2, y0 = ba[1], y1 = ba[2], link = l }
  ub[#ub + 1] = { lo = c - W / 2, hi = c + W / 2, y0 = bb[1], y1 = bb[2], link = l }
  local a, b = math.min(f.wa, f.wb), math.max(f.wa, f.wb)
  if b - a > 0.6 then
    local r
    if f.axis == "x" then r = { a, c - W / 2, b, c + W / 2 } else r = { c - W / 2, a, c + W / 2, b } end
    local th = norm_rot(za.rot)
    self.foot[#self.foot + 1] = { f = th, r = r, wp = rect_poly(th, r), link = l, za = za, zb = zb }
  end
  self.G.links[#self.G.links + 1] = l
  if self.after_commit then self:after_commit(l, za, zb) end
  return true
end

-- The span a join between two rectangle zones of one frame opens (the resolver's choice: the widest free span of
-- their shared boundary), or nil. The composer only joins walls with no other port near the shared boundary.
function Comp:join_span(za, zb)
  if not same_frame(za.rot or 0, zb.rot or 0) then return nil end
  if math.abs(za.pos[2] - zb.pos[2]) > DATA.max_step + EPS then return nil end
  local ax0, az0, ax1, az1 = L.rect_of(za)
  local bx0, bz0, bx1, bz1 = L.rect_of(zb)
  local gx, gz = math.max(bx0 - ax1, ax0 - bx1), math.max(bz0 - az1, az0 - bz1)
  local f = nil
  if math.abs(gx) <= 0.05 and gz < -EPS then
    f = { axis = "x" }
    if bx0 >= ax1 - 0.05 then f.side_a, f.side_b, f.wa, f.wb = "+x", "-x", ax1, bx0 else f.side_a, f.side_b, f.wa, f.wb = "-x", "+x", ax0, bx1 end
    f.lo, f.hi = math.max(az0, bz0), math.min(az1, bz1)
  elseif math.abs(gz) <= 0.05 and gx < -EPS then
    f = { axis = "z" }
    if bz0 >= az1 - 0.05 then f.side_a, f.side_b, f.wa, f.wb = "+z", "-z", az1, bz0 else f.side_a, f.side_b, f.wa, f.wb = "-z", "+z", az0, bz1 end
    f.lo, f.hi = math.max(ax0, bx0), math.min(ax1, bx1)
  end
  if not f then return nil end
  for _, e in ipairs({ { za, f.side_a }, { zb, f.side_b } }) do
    for _, s in ipairs(self:used_of(e[1], e[2])) do if s.hi > f.lo - 1.5 and s.lo < f.hi + 1.5 then return nil end end
  end
  local free = L.intersect(L.intersect(L.ranges_of(za, f.side_a, 0), L.ranges_of(zb, f.side_b, 0)), { { f.lo, f.hi } })
  local best = nil
  for _, s in ipairs(free) do if not best or s[2] - s[1] > best[2] - best[1] then best = s end end
  if not best or best[2] - best[1] < 4 - EPS then return nil end
  return f, best
end

function Comp:commit_join(za, zb)
  local f, s = self:join_span(za, zb)
  if not f then return false end
  self.version = (self.version or 0) + 1
  local top = math.min(za.pos[2] + za.height, zb.pos[2] + zb.height)
  local l = { type = "join", a = za.id, b = zb.id }
  for _, e in ipairs({ { za, f.side_a }, { zb, f.side_b } }) do
    local u = self:used_of(e[1], e[2])
    u[#u + 1] = { lo = s[1], hi = s[2], y0 = math.min(za.pos[2], zb.pos[2]), y1 = top, link = l }
  end
  self.G.links[#self.G.links + 1] = l
  if self.after_commit then self:after_commit(l, za, zb) end
  return true
end

-- A free link (routed by the resolver): reserve is an optional wall span {zone, side, lo, hi, y0, y1} kept clear of
-- classic links.
function Comp:commit_free(l, za, zb, reserve)
  self.version = (self.version or 0) + 1
  l.a, l.b = za.id, zb.id
  l.free_link = true
  if reserve then
    for _, r in ipairs(reserve) do
      local u = self:used_of(r.zone, r.side)
      u[#u + 1] = { lo = r.lo, hi = r.hi, y0 = r.y0, y1 = r.y1, link = l }
    end
  end
  self.G.links[#self.G.links + 1] = l
  if self.after_commit then self:after_commit(l, za, zb) end
  return true
end

-- Takes a link out of the books (its spans and footprint) and out of the graph.
function Comp:release(l)
  self.version = (self.version or 0) + 1
  for _, u in pairs(self.used) do
    for side, list in pairs(u) do
      local kept = {}
      for _, s in ipairs(list) do if s.link ~= l then kept[#kept + 1] = s end end
      u[side] = kept
    end
  end
  local kf = {}
  for _, f in ipairs(self.foot) do if f.link ~= l then kf[#kf + 1] = f end end
  self.foot = kf
end

function Comp:unlink(l)
  self:release(l)
  local kept = {}
  for _, x in ipairs(self.G.links) do if x ~= l then kept[#kept + 1] = x end end
  self.G.links = kept
end

function Comp:linked(za, zb)
  for _, l in ipairs(self.G.links) do
    if (l.a == za.id and l.b == zb.id) or (l.a == zb.id and l.b == za.id) then return true end
  end
  return false
end

-- ---------------------------------------------------------------------------------------------
-- Join styles: the links a zone z can attach to a placed zone N by, from their port levels
-- ---------------------------------------------------------------------------------------------

local function link_of(t, ya, yb, o)
  local l = { type = t, a_y = ya, b_y = yb }
  for k, v in pairs(o or {}) do l[k] = v end
  return l
end

-- Each option: {style, gap (from N's wall to z's), make = function() -> link (a = N unless swap), weight}. Same-level
-- pairs offer an abutting doorway or wide opening, a short corridor and (when asked) a join; storey pairs a drop
-- into a tall room and one kind of climb (the kinds the pair still lacks first). o._kt / o._lv: the kind table.
function Comp:options(N, z, o)
  local out = {}
  local rng, step = self.rng, self.step
  local allow = o.styles or { abut = true, wide = true, short = true, drop = true, climb = true }
  local kt, lv = o._kt, o._lv
  if not kt then kt, lv = self:kind_table() end
  local only = o.only_level and o.only_level[N]
  for _, oa in ipairs(N.levels) do
    for _, ob in ipairs(z.levels) do
      local ya, yb = N.pos[2] + oa, z.pos[2] + ob
      local d = yb - ya
      if (only and math.abs(oa - only) > EPS) or (o.only_zlevel and math.abs(ob - o.only_zlevel) > EPS) then
        -- (another level than the one asked for)
      elseif math.abs(d) <= DATA.max_step + EPS then
        local ha, hb = N.pos[2] + N.height - ya, z.pos[2] + z.height - yb
        if allow.wide and math.min(ha, hb) >= 6 and (not allow.abut or rng.chance(0.35)) then
          local w = rng.pick({ 5, 6, 6, 7, 8 })
          local h = math.min(math.floor(math.min(ha, hb) - 1), 6)
          out[#out + 1] = { style = "wide", gap = 1, weight = 1.8, oa = oa, ob = ob, make = function() return link_of("doorway", ya, yb, { width = w, height = h }) end }
        elseif allow.abut then
          local w = rng.pick({ 3, 3, 4 })
          out[#out + 1] = { style = "abut", gap = 1, weight = 2.0, oa = oa, ob = ob, make = function() return link_of("doorway", ya, yb, { width = w }) end }
        end
        -- (a short corridor leaves solid between rooms: loops round it are the ones players feel)
        if allow.short and (o.styles or rng.chance(0.7)) then
          local g = q2(rng.range(3, 6))
          local w = rng.pick(DATA.corridor_widths)
          out[#out + 1] = { style = "short", gap = g, weight = 1.4, oa = oa, ob = ob, make = function() return link_of("corridor", ya, yb, { width = w }) end }
        end
        if allow.long then
          local g = q2(rng.range(6, 10))
          local w = rng.pick(DATA.corridor_widths)
          out[#out + 1] = { style = "long", gap = g, weight = 0.1, oa = oa, ob = ob, make = function() return link_of("corridor", ya, yb, { width = w }) end }
        end
        if allow.join and oa == 0 and ob == 0 then
          out[#out + 1] = { style = "join", gap = 0, weight = 1.4, oa = 0, ob = 0, make = function() return { type = "join" } end }
        end
      elseif math.abs(d) <= step + 1 + EPS then
        local nk = kt[self:pair_key(lv(ya), lv(yb))] or {}
        -- a drop from the higher floor into a tall room through the abutting wall
        if allow.drop and math.abs(d) >= 1.5 and math.abs(d) <= DATA.drop_max then
          local hi_z, lo_z, hy, ly = N, z, ya, yb
          if d > 0 then hi_z, lo_z, hy, ly = z, N, yb, ya end
          local lo_off = (lo_z == N) and oa or ob
          if lo_off == 0 and hy + DATA.corridor_height <= lo_z.pos[2] + lo_z.height - EPS then
            out[#out + 1] = { style = "drop", gap = 1, weight = nk.drop and 0.5 or 1.8, swap = (hi_z == z), oa = (hi_z == N) and oa or 0, ob = (hi_z == z) and ob or 0,
              make = function() return link_of("drop", hy, ly, { width = 4 }) end }
          end
        end
        if allow.climb and math.abs(d) >= 1 then
          local rise = math.abs(d)
          local sw = rng.pick({ 3.5, 4, 4.5 })
          local kinds = {}
          if not nk.stair then kinds[#kinds + 1] = "stair" end
          if not nk.ramp then kinds[#kinds + 1] = "ramp" end
          if o.lifts and not nk.lift then kinds[#kinds + 1] = "lift" end
          local fresh = #kinds > 0
          if not fresh then kinds = o.lifts and { "stair", "ramp", "lift" } or { "stair", "ramp" } end
          local k = rng.pick(kinds)
          local gap = (k == "stair" and L.stair_run(rise) + 2) or (k == "ramp" and L.ramp_run(rise) + 2) or 9
          out[#out + 1] = { style = "climb", gap = gap + q2(rng.range(0, 1)), weight = fresh and 0.9 or 0.2, oa = oa, ob = ob,
            make = function() return link_of(k, ya, yb, { width = (k == "lift") and 4 or sw }) end }
        end
      end
    end
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- Vertical kinds per storey pair (the checker's "2 kinds of vertical link" rule, kept as the links are made)
-- ---------------------------------------------------------------------------------------------

function Comp:pair_key(ya, yb)
  local lo, hi = math.min(ya, yb), math.max(ya, yb)
  return string.format("%.0f/%.0f", math.floor(lo + 0.5), math.floor(hi + 0.5))
end

-- The kinds of vertical link joining each pair of floor levels (merged within 1 m, 2 m on open-air layouts, as
-- the checker merges them): {[pair_key] = {kind = true}}, and the merging function.
function Comp:kind_table()
  local G = self.G
  -- (kept while the zones and links stay the same: the closing asks for it for every candidate)
  local key = #G.zones .. "/" .. #G.links .. "/" .. (self.version or 0)
  if self.kt_key == key then return self.kt_val, self.kt_lv end
  local levels = L.floor_levels(G)
  local tol = self.open_air and 2 or 1
  local function lv(y)
    for _, x in ipairs(levels) do if math.abs(x - y) < tol then return x end end
    return y
  end
  local out = {}
  local function add(a, b, k)
    local key = self:pair_key(a, b)
    out[key] = out[key] or {}
    out[key][k] = true
  end
  local ids = {}
  for _, z in ipairs(G.zones) do ids[z.id] = z end
  for _, l in ipairs(G.links) do
    local za, zb = ids[l.a], ids[l.b]
    if za and zb and L.WALK[l.type] then
      local ya, yb = L.link_ys(l, za, zb)
      if math.abs(ya - yb) > 0.75 then add(lv(ya), lv(yb), l.type) end
    end
  end
  for _, z in ipairs(G.zones) do
    local A = LA[z.archetype]
    local zl = {}
    for _, off in ipairs(z.levels) do zl[#zl + 1] = z.pos[2] + off end
    if A.extra_levels then for _, off in ipairs(A.extra_levels(z)) do zl[#zl + 1] = z.pos[2] + off end end
    for i = 1, #zl do
      for j = i + 1, #zl do
        local a, b = lv(zl[i]), lv(zl[j])
        if math.abs(a - b) > 0.75 then
          -- (as the checker counts them: a summit's levels are "stair")
          if z.archetype == "split" or z.archetype == "tower" or z.archetype == "terrace" or z.archetype == "mound" then add(a, b, "stair") add(a, b, "ramp")
          elseif z.archetype == "sunken" then add(a, b, "stair") add(a, b, "drop")
          else add(a, b, "stair") end
        end
      end
    end
  end
  self.kt_key, self.kt_val, self.kt_lv = key, out, lv
  return out, lv
end

-- The vertical-rule shortfall: missing kinds per adjacent level pair, missing levels, bad spacing.
function Comp:level_deficit(need_levels)
  local G = self.G
  local levels = L.floor_levels(G)
  local d = 0
  if #levels < need_levels then d = d + 3 * (need_levels - #levels) end
  if G.gametype == "payload" then return d end
  local kt = self:kind_table()
  for i = 2, #levels do
    local s = levels[i] - levels[i - 1]
    if s < DATA.level_spacing[1] - 0.26 or s > DATA.level_spacing[2] + EPS then d = d + 3 end
    local k = kt[self:pair_key(levels[i - 1], levels[i])] or {}
    local nk = 0
    for _ in pairs(k) do nk = nk + 1 end
    if nk < 2 and s <= DATA.level_spacing[2] + EPS then d = d + (2 - nk) end
  end
  return d
end

-- ---------------------------------------------------------------------------------------------
-- Growth: attach a zone to the zones placed in its frame
-- ---------------------------------------------------------------------------------------------

-- Scores a candidate shape: contacts with further zones (loops), compactness, staggered walls, noise. ctx holds
-- the mass centre and scale for this placement.
function Comp:score(info, z, N, t, len, lo, hi, opt, o, ctx, near_zones)
  local s = opt.weight * (o.style_w or 1)
  local links = nil
  for _, p in ipairs(near_zones or self.G.zones) do
    local ip = self.info[p]
    if p ~= N and ip and ip.r and same_frame(ip.f, info.f) then
      local ax, g, ov = facing(info.r, ip.r)
      if ax then
        if g <= 1.6 and ov >= 4.5 + (z.chamfer or 0) + (p.chamfer or 0) then
          local zs
          if ax == "x" then zs = (info.r[1] > ip.r[1]) and "-x" or "+x" else zs = (info.r[2] > ip.r[2]) and "-z" or "+z" end
          local v = self:contact_value(z, zs, p, OPP[zs])
          if v >= 1 and (not o.ear_with or o.ear_with[p]) then links = links or {} links[#links + 1] = p end
          s = s + 1.5 * v * (o.contact_w or 1)
        elseif g > 1.6 and g < 3.5 and ov > 4 then
          s = s - 0.5
        end
      end
    end
  end
  local wx, wz = to_world(info.f, info.c[1], info.c[2])
  s = s - (o.compact_w or 1.6) * math.sqrt((wx - ctx.cx) ^ 2 + (wz - ctx.cz) ^ 2) / ctx.R
  -- staggered, not flush or centred on the wall it meets
  if len and opt.style ~= "join" then
    local f0, f1 = math.abs(t - lo) < 0.75, math.abs(t + len - hi) < 0.75
    if f0 and f1 then s = s - 1.5 elseif f0 or f1 then s = s - 0.5 end
    if math.abs((t + len / 2) - (lo + hi) / 2) < 1.25 then s = s - 0.8 end
  end
  if o.bias then s = s + o.bias(info, z, N) end
  return s + self.rng.float() * (o.noise or 0.7), links
end

-- What sharing a wall with p is worth to z: a doorway at a common port level (a loop), a drop into the taller
-- one, or nothing but a wall.
function Comp:contact_value(z, zs, p, ps)
  for _, oa in ipairs(z.levels) do
    for _, ob in ipairs(p.levels) do
      if math.abs((z.pos[2] + oa) - (p.pos[2] + ob)) <= DATA.max_step + EPS and
        L.widest(L.ranges_of(z, zs, oa)) >= 3 and L.widest(L.ranges_of(p, ps, ob)) >= 3 then return 2.0 end
    end
  end
  -- a drop from either one's port level (a split hall's raised half too) into the other's floor
  for _, e in ipairs({ { z, zs, p, ps }, { p, ps, z, zs } }) do
    local hi, hs, lo, ls = e[1], e[2], e[3], e[4]
    for _, oh in ipairs(hi.levels) do
      local yh = hi.pos[2] + oh
      local dy = yh - lo.pos[2]
      if dy >= 1.5 and dy <= DATA.drop_max and yh + DATA.corridor_height <= lo.pos[2] + lo.height - EPS and
        L.widest(L.ranges_of(hi, hs, oh)) >= 4 and L.widest(L.ranges_of(lo, ls, 0)) >= 4 then return 1.0 end
    end
  end
  return 0.1
end

-- Places zone z (sized, at its storey) next to one of targets (placed zones in frame th). o: styles, region
-- (shape -> bool), lifts, fixed (keep the orientation), sides, weights. Candidates are wall x join style x slide
-- x orientation, sampled within a budget; the clear ones are scored and one of the best few that its link fits
-- is taken. Commits the zone and its link; returns the link (true for a join) or nil when nothing fits.
function Comp:attach(z, th, targets, o)
  o = o or {}
  th = norm_rot(th)
  local rng = self.rng
  local A = LA[z.archetype]
  local orients = { { z.size[1], z.size[2], z.params.split } }
  if not o.fixed and z.size[1] ~= z.size[2] and z.size[2] >= A.min[1] and z.size[1] >= A.min[2] and
    z.size[2] <= A.max[1] and z.size[1] <= A.max[2] then
    orients[2] = { z.size[2], z.size[1], z.params.split }
  end
  -- a split-level zone may turn its raised half toward any side
  if not o.fixed and (z.archetype == "split" or z.archetype == "tower" or z.archetype == "terrace") then
    local n0 = #orients
    for i = 1, n0 do
      for _, sp in ipairs(SIDES) do
        if sp ~= orients[i][3] then orients[#orients + 1] = { orients[i][1], orients[i][2], sp } end
      end
    end
  end
  local cx, cz, area = self:centroid()
  local ctx = { cx = cx, cz = cz, R = math.sqrt(area + L.zone_area(z)) }
  o._kt, o._lv = self:kind_table()
  -- (a big mass: the ten targets nearest its centre, where a compact plan grows)
  if #targets > 10 then
    local tl = {}
    for _, N in ipairs(targets) do
      local nx, nz = L.centre_of(N)
      tl[#tl + 1] = { N = N, d = (nx - cx) ^ 2 + (nz - cz) ^ 2 }
    end
    for i = 2, #tl do
      local c, j = tl[i], i - 1
      while j >= 1 and tl[j].d > c.d do tl[j + 1] = tl[j] j = j - 1 end
      tl[j + 1] = c
    end
    targets = {}
    for i = 1, 10 do targets[i] = tl[i].N end
  end
  -- the zones of this frame, and the (target, orientation, option, side) combinations
  local frame = {}
  for _, p in ipairs(self.G.zones) do local ip = self.info[p] if ip and ip.r and same_frame(ip.f, th) then frame[#frame + 1] = p end end
  local combos = {}
  for _, N in ipairs(targets) do
    local iN = self.info[N]
    if iN and iN.r and same_frame(iN.f, th) then
      for oi, sz in ipairs(orients) do
        z.size[1], z.size[2], z.params.split = sz[1], sz[2], sz[3]
        for _, op in ipairs(self:options(N, z, o)) do
          local probe = op.make()
          local W = (op.style == "join") and 4 or L.port_width(probe)
          for _, side in ipairs(SIDES) do
            -- both walls must take the opening at its level (the archetype's own port ranges)
            if (not o.sides or o.sides[side]) and L.widest(L.ranges_of(N, side, op.oa)) >= W and
              L.widest(L.ranges_of(z, OPP[side], op.ob)) >= W then
              combos[#combos + 1] = { N = N, oi = oi, op = op, side = side }
            end
          end
        end
      end
    end
  end
  local slides = clamp(math.floor((o.budget or 360) / math.max(1, #combos)), 2, 12)
  local best, ears = {}, {}
  -- (broad phase: every placed shape and link footprint as a box in this frame, once; each combination keeps those
  -- within reach of the stretch it slides along)
  local obs, feet = {}, {}
  for _, p in ipairs(self.G.zones) do local ip = self.info[p] if ip then obs[#obs + 1] = { z = p, i = ip, b = self:box_in(ip, th) } end end
  for _, f in ipairs(self.foot) do feet[#feet + 1] = { f = f, b = self:box_in(f, th) } end
  local gapm = self.gap
  for _, cb in ipairs(combos) do
    local sz = orients[cb.oi]
    local w, d = sz[1], sz[2]
    local N, op, side = cb.N, cb.op, cb.side
    local r = self.info[N].r
    local lo, hi, len
    if side == "+x" or side == "-x" then lo, hi, len = r[2], r[4], d else lo, hi, len = r[1], r[3], w end
    local need = o.min_overlap or (((op.style == "join") and 4 or 3) + 3)
    local tmin, tmax = lo - len + need, hi - need
    -- a wall stretch the opening must land in (a road's level pads)
    local fo = o.focus and o.focus(N, side, op)
    if fo then tmin, tmax = math.max(tmin, fo[1] - len + need), math.min(tmax, fo[2] - need) end
    if tmax >= tmin then
      local K = math.max(1, math.min(slides - 1, math.floor((tmax - tmin) / 1.5)))
      local phase = rng.float()
      local ts = {}
      for k = 0, K do ts[#ts + 1] = q2(tmin + (tmax - tmin) * math.min(1, (k + ((k < K) and phase or 0)) / K)) end
      -- corner fits: slides that put z one wall away from another zone of the frame as well (interlocking)
      local pa, pb
      if side == "+x" then pa, pb = r[3] + op.gap, r[3] + op.gap + w
      elseif side == "-x" then pa, pb = r[1] - op.gap - w, r[1] - op.gap
      elseif side == "+z" then pa, pb = r[4] + op.gap, r[4] + op.gap + d
      else pa, pb = r[2] - op.gap - d, r[2] - op.gap end
      for _, P in ipairs(frame) do
        if P ~= N then
          local rp = self.info[P].r
          local q0, q1, a0, a1
          if side == "+x" or side == "-x" then q0, q1, a0, a1 = rp[1], rp[3], rp[2], rp[4] else q0, q1, a0, a1 = rp[2], rp[4], rp[1], rp[3] end
          if q0 < pb - 9 and q1 > pa + 9 then
            for _, tt in ipairs({ a1 + 1, a0 - 1 - len }) do
              if tt >= tmin - EPS and tt <= tmax + EPS then ts[#ts + 1] = q2(tt) end
            end
          end
        end
      end
      -- the box every slide stays inside, and what lies within a wall (or the score's 3.5 m) of it
      local tlo, thi = math.huge, -math.huge
      for _, t in ipairs(ts) do tlo, thi = math.min(tlo, t), math.max(thi, t) end
      local RB
      if side == "+x" or side == "-x" then RB = { pa, tlo, pb, thi + len } else RB = { tlo, pa, thi + len, pb } end
      local zo, fo2, sz2 = {}, {}, {}
      for _, e in ipairs(obs) do
        local g = box_gap(RB, e.b)
        if g < math.max(gapm, 3.6) then
          if g < gapm then zo[#zo + 1] = e end
          sz2[#sz2 + 1] = e.z
        end
      end
      for _, e in ipairs(feet) do if box_gap(RB, e.b) < DATA.min_wall then fo2[#fo2 + 1] = e.f end end
      for _, t in ipairs(ts) do
        local ccx, ccz
        if side == "+x" then ccx, ccz = r[3] + op.gap + w / 2, t + len / 2
        elseif side == "-x" then ccx, ccz = r[1] - op.gap - w / 2, t + len / 2
        elseif side == "+z" then ccx, ccz = t + len / 2, r[4] + op.gap + d / 2
        else ccx, ccz = t + len / 2, r[2] - op.gap - d / 2 end
        local info = { f = th, r = { ccx - w / 2, ccz - d / 2, ccx + w / 2, ccz + d / 2 }, c = { ccx, ccz } }
        local skip = (op.style == "join") and N or nil
        local clear = not o.region or o.region(info, z)
        if clear then
          for _, e in ipairs(zo) do if e.z ~= skip and near(info, e.i, gapm - EPS) then clear = false break end end
        end
        if clear then
          for _, f in ipairs(fo2) do if not (skip and (f.za == skip or f.zb == skip)) and near(info, f, DATA.min_wall - EPS) then clear = false break end end
        end
        if clear then
          local sc, cl = self:score(info, z, N, t, len, lo, hi, op, o, ctx, sz2)
          local item = { score = sc, info = info, N = N, op = op, oi = cb.oi, cl = cl }
          if cl then keep_best(ears, 14, item) end
          if not o.min_contacts or o.min_contacts == 0 then keep_best(best, 14, item) end
        end
      end
    end
  end
  -- the link must fit its walls: the best few that do. An ear (a place where z also meets a second zone it can
  -- link to, so every zone after the first few closes a loop as it lands) wins over a plain attachment.
  local function check(list, need_second)
    local valid = {}
    for _, c in ipairs(list) do
      local sz = orients[c.oi]
      z.size[1], z.size[2], z.params.split = sz[1], sz[2], sz[3]
      self:put(z, c.info)
      local ok
      if c.op.style == "join" then ok = self:join_span(c.N, z) ~= nil
      else
        local l = c.op.make()
        if c.op.swap then ok = self:classic_spans(l, z, c.N) ~= nil else ok = self:classic_spans(l, c.N, z) ~= nil end
      end
      if ok and need_second then
        ok = false
        self.info[z] = c.info
        for _, p in ipairs(c.cl) do if not ok and #self:pair_links(z, p, { lifts = o.lifts }) > 0 then ok = true end end
        self.info[z] = nil
      end
      if ok then valid[#valid + 1] = c if #valid >= 5 then break end end
    end
    return valid
  end
  local c = nil
  if o.ear ~= false then c = pick_best(rng, check(ears, true), o.margin) end
  if not c then c = pick_best(rng, check(best, false), o.margin) end
  z.size[1], z.size[2], z.params.split = orients[1][1], orients[1][2], orients[1][3]
  if not c then return nil end
  z.size[1], z.size[2], z.params.split = orients[c.oi][1], orients[c.oi][2], orients[c.oi][3]
  c.info.wp = rect_poly(th, c.info.r)
  finish(z, self.step)
  self:add(z, c.info)
  local result = true
  if c.op.style == "join" then
    self:commit_join(c.N, z)
  else
    local l = c.op.make()
    local ok
    if c.op.swap then ok = self:commit_classic(l, z, c.N) else ok = self:commit_classic(l, c.N, z) end
    if ok then result = l end
  end
  -- the ear's second link
  if o.ear ~= false and c.cl then
    for _, p in ipairs(c.cl) do
      local done = false
      for _, cand in ipairs(self:pair_links(z, p, { lifts = o.lifts })) do
        if self:try_add(cand) then done = true break end
      end
      if done then break end
    end
  end
  return result
end

-- ---------------------------------------------------------------------------------------------
-- The rules the loop closing keeps (a fast subset of kit.check_layout on the zone graph)
-- ---------------------------------------------------------------------------------------------

function Comp:area()
  local a = 0
  for _, z in ipairs(self.G.zones) do a = a + L.zone_area(z) end
  for _, f in ipairs(self.foot) do
    if f.r and L.WALK[f.link.type] then a = a + (f.r[3] - f.r[1]) * (f.r[4] - f.r[2]) end
  end
  for _, l in ipairs(self.G.links) do if l.free_link and L.WALK[l.type] then a = a + (l.est_len or 4) * L.port_width(l) end end
  return a
end

-- The shortfall against the graph rules: 0 when they all hold. rules(C, idx, adj) adds the gametype's own.
function Comp:deficit(rules, need_levels)
  local G = self.G
  local idx = L.index_zones(G)
  local adj = L.adjacency(G, idx)
  local n = #G.zones
  local d = 0
  for i, z in ipairs(G.zones) do
    local deg = #adj[i]
    if z.role == "nook" or z.role == "bay" then if deg < 1 then d = d + 3 end
    elseif deg < 2 then d = d + 3 * (2 - deg) end
  end
  local _, nc = L.components(n, adj, nil)
  d = d + 6 * (nc - 1)
  local area = self:area()
  local edges, internal = 0, 0
  for _, l in ipairs(G.links) do if L.WALK[l.type] then edges = edges + 1 end end
  for _, z in ipairs(G.zones) do internal = internal + (z.loops or 0) end
  local per = (G.size_class == "L") and DATA.cycle_per_1000_L or DATA.cycle_per_1000
  if G.gametype == "payload" then per = 0.5 end
  local need = per * area / 1000
  local rank = edges - n + nc + internal
  if rank < need - EPS then d = d + (need - rank) end
  for _, a in ipairs(L.articulation(G, idx, adj)) do
    if a.cut >= DATA.articulation_share * area - EPS then d = d + 2 end
  end
  local key = {}
  for i, z in ipairs(G.zones) do if z.role == "hub" or z.role == "hill" or z.role == "base" or z.key then key[#key + 1] = i end end
  for a = 1, #key do
    for b = a + 1, #key do
      local r = L.disjoint_routes(n, adj, key[a], key[b], 2)
      if r < 2 then d = d + 2 * (2 - r) end
    end
  end
  d = d + self:level_deficit(need_levels or 3)
  if rules then d = d + rules(self, idx, adj) end
  return d
end

-- ---------------------------------------------------------------------------------------------
-- Loop closing: candidate links between facing zones, added until the rules hold, the rest added and pruned
-- ---------------------------------------------------------------------------------------------

-- Classic link candidates between two placed rectangle zones of one frame.
function Comp:pair_links(za, zb, o)
  local out = {}
  local f = L.link_frame(za, zb)
  if not f or f.hi - f.lo < 5 then return out end
  -- (o.corridors = false: doorways, openings, drops and climbs only)
  local cor = o.corridors ~= false
  local styles = { abut = f.gap <= 1.6, wide = f.gap <= 1.6, short = cor and f.gap > 1.6 and f.gap <= (o.max_gap or 8), drop = f.gap <= 2.5,
    climb = true, long = cor and f.gap > 5 and f.gap <= (o.max_gap or 8) }
  local opts = self:options(za, zb, { styles = styles, lifts = o.lifts })
  for _, op in ipairs(opts) do
    if op.style ~= "join" then
      local l = op.make()
      local a, b = za, zb
      if op.swap then a, b = zb, za end
      if f.gap >= L.gap_need(l, a, b) - EPS and (op.style ~= "climb" or f.gap <= L.gap_need(l, a, b) + 6) then
        if op.style == "short" or op.style == "long" then l.type = (f.gap <= 1.6) and "doorway" or "corridor" end
        if self:classic_spans(l, a, b) then out[#out + 1] = { a = a, b = b, link = l, weight = op.weight, kind = "classic" } end
      end
    end
  end
  return out
end

-- Every candidate extra link: classic between facing pairs; free corridors / stairs between close zones of
-- different frames (or traced zones) when allowed.
function Comp:extra_candidates(o)
  o = o or {}
  local out = {}
  local zs = self.G.zones
  -- doorways from traced zones through the walls beside them
  for _, P in ipairs(zs) do
    if P.archetype == "poly" and self.info[P] then
      for _, c in ipairs(self:poly_contacts(P)) do
        if not (o.skip and o.skip(c.a, c.b)) then out[#out + 1] = c end
      end
    end
  end
  for i = 1, #zs do
    for j = i + 1, #zs do
      local za, zb = zs[i], zs[j]
      if not (o.skip and (o.skip(za, zb))) and not self:linked(za, zb) then
        local ia, ib = self.info[za], self.info[zb]
        local classic = {}
        if ia and ib and ia.r and ib.r and same_frame(ia.f, ib.f) then
          classic = self:pair_links(za, zb, o)
          for _, c in ipairs(classic) do out[#out + 1] = c end
        end
        if #classic == 0 and o.free and ia and ib then
          local d = sep(ia, ib, o.free_max or 18)
          if d >= 6 and d <= (o.free_max or 18) and self:clear_line(za, zb, 3) then
            local dy = math.abs(za.pos[2] - zb.pos[2])
            local t = (dy <= DATA.max_step + EPS) and "corridor" or ((dy <= self.step + 1) and self.rng.pick({ "stair", "ramp" }) or nil)
            if t == "corridor" and o.corridors == false then t = nil end
            if t and LA[za.archetype].class ~= "open_air" then
              local l = { type = t, a_y = za.pos[2], b_y = zb.pos[2], width = (t == "corridor") and self.rng.pick({ 4, 4.5 }) or 4, est_len = d + 3 }
              out[#out + 1] = { a = za, b = zb, link = l, weight = 0.4 - d / 40, kind = "free" }
            end
          end
        end
      end
    end
  end
  return out
end

-- True when the straight line between two zones' centres keeps m from every other zone (a free link between
-- them has a chance to route).
function Comp:clear_line(za, zb, m)
  local ax, az = L.centre_of(za)
  local bx, bz = L.centre_of(zb)
  local seg = { { ax, az }, { bx, bz } }
  for _, p in ipairs(self.G.zones) do
    if p ~= za and p ~= zb then
      local ip = self.info[p]
      if ip and L.poly_near(seg, wp_of(ip), m, nil, bb_of(ip)) then return false end
    end
  end
  return true
end

-- The routing context of the graph as it stands (resolved once per change of the zones and links; a free link
-- tried against it costs one route, not a resolve).
function Comp:route_ctx()
  if self.rctx and self.rctx_version == (self.version or 0) then return self.rctx end
  local G = self.G
  G.method = "free"
  for i, x in ipairs(G.links) do x.id = "l" .. i end
  self.rctx = L.route_ctx(G)
  self.rctx_version = self.version or 0
  return self.rctx
end

-- Books a routed free link's plan (its mitred quads) with the link footprints, so later placements keep clear.
function Comp:foot_free(P)
  if not (P and P.M and P.M.quads) then return end
  for _, q in ipairs(P.M.quads) do
    self.foot[#self.foot + 1] = { wp = L.ccw(q), link = P.link, za = P.za, zb = P.zb, free = true }
  end
end

-- Routes a free link against the graph: keeps it when it finds a clear route that route_ok accepts.
function Comp:test_free(l, za, zb)
  -- (a bounded number of trials per composition)
  self.free_trials = (self.free_trials or 0) + 1
  if self.free_trials > 24 then return false end
  local X = self:route_ctx()
  l.a, l.b = za.id, zb.id
  l.id = "l" .. (#self.G.links + 1)
  local P, errs = L.route_link(X, l)
  if #errs > 0 or not P.ok or not self:route_ok_P(P, l) then L.route_drop(X, P) return false end
  self:commit_free(l, za, zb)
  self:foot_free(P)
  -- (the context already holds the link)
  self.rctx_version = self.version
  return true
end

-- A generated free link keeps under 58 m, and a stair or ramp from a turned zone keeps to one straight run.
function Comp:route_ok_P(P, l)
  if P.mode == "free" and P.pts then
    -- (and no generated corridor runs a long way round)
    if (P.length or 0) > 58 then return false end
    if not self:bend_ok(P, l) then return false end
  end
  return true
end

-- STAIR-BEND RESTRICTION (one place): a bent stair or ramp from any zones (unturned, turned, traced; square or
-- diagonal turns) builds sealed under both build methods while its plan keeps within BEND_REACH metres of the origin
-- (measured: 40 bent stairs and ramps routed by test_free, 0 holes either way; the same set 450-850 m out leaked).
-- Beyond that reach it keeps to rectangle zones of the world frame and square turns.
local BEND_REACH = 250
local function within_reach(pts)
  for _, q in ipairs(pts or {}) do
    if math.abs(q[1] or q.x or 0) > BEND_REACH or math.abs(q[2] or q.z or 0) > BEND_REACH then return false end
  end
  return true
end
local function bends_ok(P)
  if within_reach(P.pts) then return true end
  if norm_rot(P.za.rot) ~= 0 or norm_rot(P.zb.rot) ~= 0 or P.za.archetype == "poly" or P.zb.archetype == "poly" then return false end
  for _, d in ipairs((P.M and P.M.dirs) or {}) do
    if math.abs(d[1]) > 1e-6 and math.abs(d[2]) > 1e-6 then return false end
  end
  return true
end
function Comp:bend_ok(P, l)
  if (l.type == "stair" or l.type == "ramp") and #P.pts > 2 and not bends_ok(P) then return false end
  return true
end

function Comp:route_ok(plan, l)
  for _, P in ipairs(plan.links) do
    if P.link == l and not self:route_ok_P(P, l) then return false end
  end
  return true
end

-- Tries to link zone z to one of the zones in pool (nearest first): a classic link to a zone it faces (a longer
-- corridor or climb than the loop closing takes), else a free corridor, stair or ramp routed around the mass.
function Comp:link_out(z, pool, o)
  local others = {}
  for _, p in ipairs(pool) do
    if p ~= z and p.role ~= "nook" and p.role ~= "bay" and not self:linked(z, p) and self.info[p] and self.info[z] then
      others[#others + 1] = { p = p, d = sep(self.info[z], self.info[p]) }
    end
  end
  for i = 2, #others do
    local c, j = others[i], i - 1
    while j >= 1 and others[j].d > c.d do others[j + 1] = others[j] j = j - 1 end
    others[j + 1] = c
  end
  for _, e in ipairs(others) do
    for _, c in ipairs(self:pair_links(z, e.p, { max_gap = 14, lifts = o.lifts })) do
      if self:try_add(c) then return true end
    end
  end
  local tries = 0
  for _, e in ipairs(others) do
    if e.d >= 2 and e.d <= 30 then
      -- the port levels of both zones: a corridor between levels within a step, else a stair or ramp
      for _, oa in ipairs(z.levels) do
        for _, ob in ipairs(e.p.levels) do
          local ya, yb = z.pos[2] + oa, e.p.pos[2] + ob
          local dy = math.abs(ya - yb)
          local t = (dy <= DATA.max_step + EPS) and "corridor" or ((dy >= 1 and dy <= self.step + 1) and self.rng.pick({ "stair", "ramp" }) or nil)
          if t and tries < (o.free_tries or 4) then
            tries = tries + 1
            local l = { type = t, a_y = ya, b_y = yb, width = 4, keep = true }
            if self:test_free(l, z, e.p) then return true end
          end
        end
      end
    end
  end
  return false
end

-- Moves a zone with one way in (a rectangle zone on one classic link) to a place where it meets a second zone
-- it can link to, and links it there; puts it back when no such place exists. Returns true when moved.
function Comp:relocate(z, o)
  local info = self.info[z]
  if not info or not info.r or z.role == "hub" or z.role == "hill" or z.kind == "core" then return false end
  local mine = {}
  for _, l in ipairs(self.G.links) do if l.a == z.id or l.b == z.id then mine[#mine + 1] = l end end
  if #mine ~= 1 or mine[1].type == "join" or mine[1].free_link then return false end
  local old = mine[1]
  local oldsize = { z.size[1], z.size[2], z.params.split }
  self:unlink(old)
  local kept = {}
  for _, x in ipairs(self.G.zones) do if x ~= z then kept[#kept + 1] = x end end
  self.G.zones = kept
  self.info[z] = nil
  local targets = {}
  for _, p in ipairs(self.G.zones) do if self.info[p] and self.info[p].r and same_frame(self.info[p].f, info.f) then targets[#targets + 1] = p end end
  local id = z.id
  local ok = self:attach(z, info.f, targets, { lifts = o.lifts, min_contacts = 1, budget = 700 })
  if ok then
    -- the second link: to a zone it now shares a wall with
    local linked = false
    for _, p in ipairs(self.G.zones) do
      if not linked and p ~= z and not self:linked(z, p) then
        for _, c in ipairs(self:pair_links(z, p, { lifts = o.lifts })) do
          if self:try_add(c) then linked = true break end
        end
      end
    end
    if linked then return true end
    -- no second link after all: undo the move
    for _, l in ipairs(self.G.links) do if l.a == z.id or l.b == z.id then self:unlink(l) end end
    local k2 = {}
    for _, x in ipairs(self.G.zones) do if x ~= z then k2[#k2 + 1] = x end end
    self.G.zones = k2
    self.info[z] = nil
  end
  z.id = id
  z.size[1], z.size[2], z.params.split = oldsize[1], oldsize[2], oldsize[3]
  finish(z, self.step)
  self:add(z, info)
  local za, zb = self:byid(old.a), self:byid(old.b)
  self:commit_classic(old, za, zb, old.offset)
  return false
end

-- A small zone with one way in takes a second doorway into the same neighbour (two ways in, nothing big behind
-- one zone). Returns true when made.
function Comp:second_door(z, o)
  if L.zone_area(z) > 0.08 * math.max(self:area(), self.G.target_area or 0) then return false end
  for _, l in ipairs(self.G.links) do
    if L.WALK[l.type] and (l.a == z.id or l.b == z.id) and not l.free_link and l.type ~= "join" then
      local p = self:byid((l.a == z.id) and l.b or l.a)
      if p and self.info[p] and self.info[p].r and self.info[z].r then
        for _, c in ipairs(self:pair_links(z, p, { lifts = o.lifts, max_gap = 14 })) do
          if self:try_add(c) then return true end
        end
      end
    end
  end
  return false
end

-- Gives zones that still have one way in another link, and the zones behind an articulation zone a second way
-- out. Returns the number of links made.
function Comp:rescue(o)
  o = o or {}
  local G = self.G
  local made = 0
  for _, z in ipairs(G.zones) do
    if z.role ~= "nook" and z.role ~= "bay" and self:degree(z) < 2 then
      if self:link_out(z, G.zones, o) or self:relocate(z, o) or self:second_door(z, o) then made = made + 1
      end
    end
  end
  -- storey pairs joined by one kind of vertical link: another kind between rooms on those levels
  local kt, lv = self:kind_table()
  local levels = L.floor_levels(G)
  for i = 2, #levels do
    local lo, hi = levels[i - 1], levels[i]
    local have = kt[self:pair_key(lo, hi)] or {}
    local nk = 0
    for _ in pairs(have) do nk = nk + 1 end
    if nk < 2 and hi - lo <= DATA.level_spacing[2] + EPS then
      local done = false
      local tries = 0
      for _, za in ipairs(G.zones) do
        for _, zb in ipairs(G.zones) do
          if not done and za ~= zb and not self:linked(za, zb) and za.role ~= "nook" and zb.role ~= "nook" and za.role ~= "bay" and zb.role ~= "bay" then
            for _, oa in ipairs(za.levels) do
              for _, ob in ipairs(zb.levels) do
                local ya, yb = za.pos[2] + oa, zb.pos[2] + ob
                if not done and math.abs(lv(ya) - lo) < 0.01 and math.abs(lv(yb) - hi) < 0.01 then
                  for _, c in ipairs(self:pair_links(za, zb, { max_gap = 14, lifts = o.lifts })) do
                    if not done and not have[c.link.type] and math.abs(c.link.a_y - c.link.b_y) > 0.75 and self:try_add(c) then done = true end
                  end
                  local d = (self.info[za] and self.info[zb]) and sep(self.info[za], self.info[zb], 24) or 99
                  if not done and tries < 3 and d >= 4 and d <= 24 then
                    for _, k in ipairs({ "ramp", "stair" }) do
                      if not done and not have[k] and tries < 3 then
                        tries = tries + 1
                        if self:test_free({ type = k, a_y = ya, b_y = yb, width = 4, keep = true }, za, zb) then done = true end
                      end
                    end
                  end
                end
              end
            end
          end
        end
      end
      if done then made = made + 1 kt, lv = self:kind_table() end
    end
  end
  for _ = 1, 4 do
    local idx = L.index_zones(G)
    local adj = L.adjacency(G, idx)
    local area = self:area()
    local cut = nil
    for _, a in ipairs(L.articulation(G, idx, adj)) do
      if a.cut >= DATA.articulation_share * area - EPS and (not cut or a.cut > cut.cut) then cut = a end
    end
    if not cut then break end
    local behind, rest = {}, {}
    local set = {}
    for _, id in ipairs(cut.behind) do set[id] = true end
    for _, z in ipairs(G.zones) do
      if set[z.id] then behind[#behind + 1] = z elseif z.id ~= cut.zone then rest[#rest + 1] = z end
    end
    local ok = false
    for _, z in ipairs(behind) do
      if not ok and z.role ~= "nook" and z.role ~= "bay" then ok = self:link_out(z, rest, { lifts = o.lifts, free_tries = 3 }) end
    end
    if not ok then break end
    made = made + 1
  end
  return made
end

function Comp:try_add(c)
  if c.kind == "free" then return self:commit_free(c.link, c.a, c.b, c.reserve) end
  local l = {}
  for k, v in pairs(c.link) do l[k] = v end
  if self:commit_classic(l, c.a, c.b) then c.made = l return true end
  return false
end

function Comp:degree(z)
  local n = 0
  for _, l in ipairs(self.G.links) do if L.WALK[l.type] and (l.a == z.id or l.b == z.id) then n = n + 1 end end
  return n
end

-- Adds candidates until the rules hold (greedy on the shortfall), then adds the rest within degree caps and
-- prunes optional links with the seeded stream while the rules hold. o: rules, need_levels, cap, p_keep,
-- required (link -> bool).
function Comp:close_loops(cands, o)
  local rng = self.rng
  local d = self:deficit(o.rules, o.need_levels)
  for _, c in ipairs(cands) do c.r = c.weight + rng.float() * 0.6 end
  local guard = 0
  while d > 0 and guard < 40 do
    guard = guard + 1
    local best, bd = nil, d
    -- the candidates worth a trial: those that touch a zone with one way in first, then the best weighted (a
    -- bounded number per round keeps the closing fast)
    local short = {}
    for _, z in ipairs(self.G.zones) do if z.role ~= "nook" and z.role ~= "bay" and self:degree(z) < 2 then short[z] = true end end
    local trial = {}
    for _, c in ipairs(cands) do if not c.done then c.pri = c.r + ((short[c.a] or short[c.b]) and 10 or 0) trial[#trial + 1] = c end end
    for i = 2, #trial do
      local c, j = trial[i], i - 1
      while j >= 1 and trial[j].pri < c.pri do trial[j + 1] = trial[j] j = j - 1 end
      trial[j + 1] = c
    end
    for i = 13, #trial do trial[i] = nil end
    for _, c in ipairs(trial) do
      if not c.done then
        if self:try_add(c) then
          local l = c.made or c.link
          local d2 = self:deficit(o.rules, o.need_levels) - c.r * 0.05
          self:unlink(l)
          if d2 < bd - 1e-9 then best, bd = c, d2 end
        else
          c.done = true
        end
      end
    end
    if not best then break end
    best.done = true
    if self:try_add(best) then best.kept = true end
    d = self:deficit(o.rules, o.need_levels)
  end
  -- richness: the remaining candidates (best first), within degree caps
  local rest = {}
  for _, c in ipairs(cands) do if not c.done then rest[#rest + 1] = c end end
  for i = 2, #rest do
    local c, j = rest[i], i - 1
    while j >= 1 and rest[j].r < c.r do rest[j + 1] = rest[j] j = j - 1 end
    rest[j + 1] = c
  end
  local cap = o.cap or 4
  -- (free links route round the mass: a couple at most beyond what the rules need, they are slow to route)
  local nfree = 0
  for _, c in ipairs(rest) do
    if (c.kind ~= "free" or (nfree < (o.max_free or 2) and rng.chance(o.p_free or 0.5))) and rng.chance(o.p_add or 0.7) and
      self:degree(c.a) < ((c.a.role == "hub" or c.a.role == "hill") and cap + 2 or cap) and
      self:degree(c.b) < ((c.b.role == "hub" or c.b.role == "hill") and cap + 2 or cap) then
      c.done = true
      if self:try_add(c) then c.kept = true if c.kind == "free" then nfree = nfree + 1 end end
    end
  end
  -- prune optional links while the rules hold
  if d <= 0 then d = self:deficit(o.rules, o.need_levels) end
  local list = {}
  for _, l in ipairs(self.G.links) do if not (o.required and o.required(l)) and l.type ~= "join" then list[#list + 1] = l end end
  rng.shuffle(list)
  for _, l in ipairs(list) do
    if rng.chance(o.p_prune or 0.45) then
      local saved = {}
      for _, u in pairs(self.used) do for side, sl in pairs(u) do for _, s in ipairs(sl) do if s.link == l then saved[#saved + 1] = { u, side, s } end end end end
      local sf = {}
      for _, f in ipairs(self.foot) do if f.link == l then sf[#sf + 1] = f end end
      local pos = nil
      for i, x in ipairs(self.G.links) do if x == l then pos = i end end
      self:unlink(l)
      if self:deficit(o.rules, o.need_levels) > d + 1e-9 then
        table.insert(self.G.links, pos, l)
        for _, e in ipairs(saved) do table.insert(e[1][e[2]], e[3]) end
        for _, f in ipairs(sf) do self.foot[#self.foot + 1] = f end
      end
    end
  end
  return self:deficit(o.rules, o.need_levels)
end

-- Routes the free links on the finished graph (the resolver, as kit.layout will) and drops those that find no
-- clear route; returns the number dropped.
function Comp:route_free(keep)
  local G = self.G
  G.method = "free"
  for i, l in ipairs(G.links) do l.id = "l" .. i end
  -- keep: the routes found are written into the links (ports and waypoints), so later resolves only check them
  local plan = L.resolve(G, nil, keep or false)
  -- the routes found are the free links' footprints from here on
  local kf = {}
  for _, f in ipairs(self.foot) do if not f.free then kf[#kf + 1] = f end end
  self.foot = kf
  for _, P in ipairs(plan.links) do if P.mode == "free" and P.ok then self:foot_free(P) end end
  local bad = {}
  local nbad = 0
  for _, P in ipairs(plan.links) do if not self:route_ok_P(P, P.link) then bad[P.link.id] = true nbad = nbad + 1 end end
  if #plan.errors == 0 and nbad == 0 then return 0, plan end
  for _, e in ipairs(plan.errors) do if e.link then bad[e.link] = true end end
  local kept, n = {}, 0
  for _, l in ipairs(G.links) do
    if bad[l.id] then n = n + 1 self:release(l) else kept[#kept + 1] = l end
  end
  G.links = kept
  for i, l in ipairs(G.links) do l.id = "l" .. i end
  return n, nil
end

-- ---------------------------------------------------------------------------------------------
-- Wings: a cluster in a turned frame tied to the body by a traced hinge room
-- ---------------------------------------------------------------------------------------------

-- The world outward normal and tangent of a rectangle zone's side, and the side's world end points.
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

-- The convex hull of plan points (counter-clockwise), for small point sets.
local function hull2(P)
  local pts = {}
  for _, p in ipairs(P) do pts[#pts + 1] = { p[1], p[2] } end
  for i = 2, #pts do
    local c, j = pts[i], i - 1
    while j >= 1 and (pts[j][1] > c[1] or (pts[j][1] == c[1] and pts[j][2] > c[2])) do pts[j + 1] = pts[j] j = j - 1 end
    pts[j + 1] = c
  end
  local function cr(o, a, b) return (a[1] - o[1]) * (b[2] - o[2]) - (a[2] - o[2]) * (b[1] - o[1]) end
  local lower, upper = {}, {}
  for _, p in ipairs(pts) do
    while #lower >= 2 and cr(lower[#lower - 1], lower[#lower], p) <= 1e-9 do table.remove(lower) end
    lower[#lower + 1] = p
  end
  for i = #pts, 1, -1 do
    local p = pts[i]
    while #upper >= 2 and cr(upper[#upper - 1], upper[#upper], p) <= 1e-9 do table.remove(upper) end
    upper[#upper + 1] = p
  end
  table.remove(lower)
  table.remove(upper)
  for _, p in ipairs(upper) do lower[#lower + 1] = p end
  return lower
end

-- Tries to hang a wing anchor W (unplaced, sized) at angle th off body zone B's side: a traced hinge room between
-- them (1 m off B's wall, reaching 0.25 m into W's wall), joined to W, a doorway into B. Returns the hinge zone or
-- nil. o: depth range, palette, role.
function Comp:hinge_wing(B, side, W, th, o)
  local rng = self.rng
  th = norm_rot(th)
  local n, a, b = side_geo(B, side)
  local tx, tz = b[1] - a[1], b[2] - a[2]
  local blen = math.sqrt(tx * tx + tz * tz)
  tx, tz = tx / blen, tz / blen
  -- the doorway span on B's wall: inside its port range at its floor
  local lo, hi = L.wall_span(B, side)
  local ranges = L.ranges_of(B, side, 0)
  local free = ranges
  for _, s in ipairs(self:used_of(B, side)) do if s.y0 < B.pos[2] + 4.5 and s.y1 > B.pos[2] - 0.5 then free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end end
  local spans = {}
  for _, s in ipairs(free) do if s[2] - s[1] >= 6 then spans[#spans + 1] = s end end
  if #spans == 0 then return nil end
  local sp = spans[rng.int(1, #spans)]
  -- the hinge's B edge: 6-10 m along the wall inside the span (in wall coordinates from a: s = along - lo or reversed)
  local elen = math.min(sp[2] - sp[1], even(rng.range(6, 10)))
  local u0 = q2(rng.range(sp[1], sp[2] - elen))
  -- (o.near_end: the hinge at the low or high end of the free stretch, leaving the rest of the wall)
  if o.near_end == "lo" then u0 = q2(sp[1] + rng.range(0, 1)) elseif o.near_end == "hi" then u0 = q2(sp[2] - elen - rng.range(0, 1)) end
  u0 = clamp(u0, sp[1], sp[2] - elen)
  local u1 = u0 + elen
  -- wall coordinate u -> distance from a along the side's direction (a -> b runs with u for +x / -z, against it for -x / +z)
  local function at_u(u, off)
    local s = (side == "+x" or side == "-z") and (u - lo) or (hi - u)
    return { a[1] + tx * s + n[1] * off, a[2] + tz * s + n[2] * off }
  end
  local P1, P2 = at_u(u0, 1), at_u(u1, 1)
  local mid = at_u((u0 + u1) / 2, 1)
  -- W's facing side: the one whose world normal is most against n
  local fside, fbest = nil, 2
  for _, s in ipairs(SIDES) do
    local wx, wz = to_world(th, DIRV[s][1], DIRV[s][2])
    local dot = wx * n[1] + wz * n[2]
    if dot < fbest then fside, fbest = s, dot end
  end
  if fbest > -0.45 then return nil end
  local depth = q2(rng.range(o.depth and o.depth[1] or 6, o.depth and o.depth[2] or 10))
  -- W's wall centre in world, then its local centre (slid along its wall)
  local mx, mz = mid[1] + n[1] * depth, mid[2] + n[2] * depth
  local lx, lz = to_local(th, mx, mz)
  local w, d = W.size[1], W.size[2]
  local along = (fside == "+x" or fside == "-x") and d or w
  local slide = q2(rng.range(-along / 4, along / 4))
  if o.near_end then
    -- (a loop wing: W reaches from the hinge toward the far end of B's wall, the hinge about 6 m from W's near end)
    local far = at_u((o.near_end == "lo") and hi or lo, 0)
    local near0 = at_u((o.near_end == "lo") and lo or hi, 0)
    local ax, az
    if fside == "+x" or fside == "-x" then ax, az = to_world(th, 0, 1) else ax, az = to_world(th, 1, 0) end
    local sg = ((far[1] - near0[1]) * ax + (far[2] - near0[2]) * az >= 0) and 1 or -1
    slide = sg * q2(math.max(0, along / 2 - 6))
  end
  local cx, cz
  if fside == "+x" then cx, cz = lx - w / 2, lz + slide
  elseif fside == "-x" then cx, cz = lx + w / 2, lz + slide
  elseif fside == "+z" then cx, cz = lx + slide, lz - d / 2
  else cx, cz = lx + slide, lz + d / 2 end
  cx, cz = q2(cx), q2(cz)
  local infoW = self:rect_info(W, th, cx, cz)
  self:put(W, infoW)
  if not self:fits(infoW) or (o.region and not o.region(infoW)) then return nil end
  -- the hinge's W edge: inside W's port range on fside, reaching 0.25 m into W
  local wr = L.ranges_of(W, fside, 0)
  local wlo, whi = L.wall_span(W, fside)
  local best = nil
  for _, r in ipairs(wr) do if r[2] - r[1] >= 7 and (not best or r[2] - r[1] > best[2] - best[1]) then best = r end end
  if not best then return nil end
  -- project the B edge's mid point on W's wall and centre the W edge near it
  local pl = { to_local(th, mid[1], mid[2]) }
  local pc = (fside == "+x" or fside == "-x") and pl[2] or pl[1]
  local wl = math.min(best[2] - best[1] - 2, even(rng.range(6, 11)))
  if wl < 4 then return nil end
  local c0 = clamp(pc - wl / 2, best[1] + 1, best[2] - 1 - wl)
  local wcoord = L.wall_coord(W, fside)
  -- a point of W's wall at along-wall coordinate u, 0.25 m into W
  local function wpt(u)
    local p
    if fside == "+x" or fside == "-x" then p = { wcoord + DIRV[fside][1] * -0.25, u } else p = { u, wcoord + DIRV[fside][2] * -0.25 } end
    local x, zz = to_world(th, p[1], p[2])
    return { x, zz }
  end
  local Q1, Q2 = wpt(c0), wpt(c0 + wl)
  local pts = hull2({ P1, P2, Q1, Q2 })
  if #pts ~= 4 then return nil end
  for i, p in ipairs(pts) do pts[i] = { snapv(p[1], 16), snapv(p[2], 16) } end
  if not L.simple(pts) or math.abs(L.area2(pts)) < 30 then return nil end
  -- the B edge's index in the outline
  local bedge = nil
  for i = 1, 4 do
    local p, q = pts[i], pts[i % 4 + 1]
    local function near(u, v) return math.abs(u[1] - v[1]) + math.abs(u[2] - v[2]) < 0.1 end
    if (near(p, P1) and near(q, P2)) or (near(p, P2) and near(q, P1)) then bedge = i end
  end
  if not bedge then return nil end
  local Hz = self:zone("poly", { role = o.role or "room", outline = pts, y = B.pos[2] + (o.hinge_dy or 0), palette = o.palette })
  local infoH = self:poly_info(pts)
  -- clear of everything but W (joined) and B (1 m off its wall)
  if not self:fits(infoH, { [W] = true }) or (o.region and not o.region(infoH)) then return nil end
  if near(infoW, self.info[B], self.gap - EPS) then return nil end
  -- the join must be one the resolver opens (one wall of W, inside its port range)
  if not self:join_ok(W, Hz) then return nil end
  local dw = math.min(rng.pick({ 3, 3, 4, 5 }), elen - 2.5)
  local function commit()
  self:put(W, infoW)
  self:add(Hz, infoH)
  self:add(W, infoW)
  finish(W, self.step)
  -- the join (W's span booked so classic links on that wall keep clear) and the doorway into B
  local jl = { type = "join", a = W.id, b = Hz.id }
  local uj = self:used_of(W, fside)
  uj[#uj + 1] = { lo = c0 - 0.25, hi = c0 + wl + 0.25, y0 = W.pos[2], y1 = W.pos[2] + W.height, link = jl }
  self.G.links[#self.G.links + 1] = jl
  local um = (u0 + u1) / 2
  local door = { type = "doorway", width = dw, a_side = side, b_side = bedge, a_at = at_u(um, 0), b_at = at_u(um, 1), a_y = B.pos[2], b_y = Hz.pos[2] }
  self:commit_free(door, B, Hz, { { zone = B, side = side, lo = um - dw / 2, hi = um + dw / 2, y0 = B.pos[2], y1 = B.pos[2] + 4 } })
  return Hz, fside
  end
  if o.dry then return { commit = commit, infoW = infoW, infoH = infoH } end
  return commit()
end

-- True when the resolver opens a join between zones a and b (checked on the pair alone).
function Comp:join_ok(a, b)
  local ia, ib = a.id, b.id
  a.id, b.id = "_a", "_b"
  local plan = L.resolve({ zones = { a, b }, links = { { type = "join", a = "_a", b = "_b", id = "_j" } }, gametype = self.G.gametype, class = self.G.class }, nil, false)
  a.id, b.id = ia, ib
  return #plan.errors == 0
end

-- A traced room between two walls of rectangle zones in any frames (a canted vestibule), joined to both: its
-- ends reach 0.25 m into each wall inside the wall's port range; it keeps clear of everything else. Returns the
-- zone or nil. o: palette, role, len (end length), max_gap, target (a world point both ends keep near).
function Comp:bridge_poly(A, sa, Bz, sb, o)
  o = o or {}
  local rng = self.rng
  if math.abs(A.pos[2] - Bz.pos[2]) > DATA.max_step + EPS then return nil end
  local na, a0, a1 = side_geo(A, sa)
  local nb, b0, b1 = side_geo(Bz, sb)
  if na[1] * nb[1] + na[2] * nb[2] > -0.3 then return nil end
  local ac = { (a0[1] + a1[1]) / 2, (a0[2] + a1[2]) / 2 }
  local bc = { (b0[1] + b1[1]) / 2, (b0[2] + b1[2]) / 2 }
  local gap = (bc[1] - ac[1]) * na[1] + (bc[2] - ac[2]) * na[2]
  if gap < 2.5 or gap > (o.max_gap or 14) then return nil end
  if (ac[1] - bc[1]) * nb[1] + (ac[2] - bc[2]) * nb[2] < 2.5 then return nil end
  -- an end on each wall: inside its free range, centred near the other wall's projection
  local function span(z, side, target)
    local free = L.ranges_of(z, side, 0)
    for _, s2 in ipairs(self:used_of(z, side)) do free = L.subtract(free, s2.lo - 1.5, s2.hi + 1.5) end
    local tx, tz = to_local(norm_rot(z.rot), target[1], target[2])
    local u = (side == "+x" or side == "-x") and tz or tx
    local len = o.len or even(rng.range(5, 8))
    local best = nil
    for _, r in ipairs(free) do
      if r[2] - r[1] >= len + 2 then
        local c = clamp(u, r[1] + 1 + len / 2, r[2] - 1 - len / 2)
        if not best or math.abs(c - u) < math.abs(best - u) then best = c end
      end
    end
    if not best then return nil end
    best = q2(best)
    local wc = L.wall_coord(z, side)
    local th = norm_rot(z.rot)
    local function pt(v)
      local p
      if side == "+x" or side == "-x" then p = { wc - DIRV[side][1] * 0.25, v } else p = { v, wc - DIRV[side][2] * 0.25 } end
      local x, zz = to_world(th, p[1], p[2])
      return { x, zz }
    end
    return { pt(best - len / 2), pt(best + len / 2) }, best - len / 2, best + len / 2
  end
  local ea, alo, ahi = span(A, sa, o.target or bc)
  if not ea then return nil end
  local eb, blo, bhi = span(Bz, sb, o.target or { (ea[1][1] + ea[2][1]) / 2, (ea[1][2] + ea[2][2]) / 2 })
  if not eb then return nil end
  local pts = hull2({ ea[1], ea[2], eb[1], eb[2] })
  if #pts ~= 4 then return nil end
  for i, q in ipairs(pts) do pts[i] = { snapv(q[1], 16), snapv(q[2], 16) } end
  if not L.simple(pts) or math.abs(L.area2(pts)) < 24 then return nil end
  local info = self:poly_info(pts)
  if not self:fits(info, { [A] = true, [Bz] = true }) or (o.region and not o.region(info)) then return nil end
  local y = math.max(A.pos[2], Bz.pos[2])
  local Pz = self:zone("poly", { role = o.role or "room", outline = pts, y = y, palette = o.palette or A.palette })
  if not self:join_ok(A, Pz) or not self:join_ok(Bz, Pz) then return nil end
  self:add(Pz, info)
  for _, e in ipairs({ { A, sa, alo, ahi }, { Bz, sb, blo, bhi } }) do
    local jl = { type = "join", a = e[1].id, b = Pz.id }
    local u = self:used_of(e[1], e[2])
    u[#u + 1] = { lo = e[3] - 0.25, hi = e[4] + 0.25, y0 = e[1].pos[2], y1 = e[1].pos[2] + e[1].height, link = jl }
    self.G.links[#self.G.links + 1] = jl
  end
  return Pz
end

-- A loop wing off wall side of rectangle zone B: room W in a frame turned a little from B's, its long wall facing
-- B's wall a few metres off, joined to B by a traced hinge room near one end of that wall (with a doorway into B) and
-- by a traced vestibule near the other (joined to both): the wing closes its loop on its own. Returns true when it
-- stands. o: angles, region, depth, tries.
function Comp:loop_wing(B, side, W, o)
  o = o or {}
  local rng = self.rng
  local bth = norm_rot(B.rot)
  for _ = 1, (o.tries or 6) do
    local n0 = #self.G.zones
    local th = norm_rot(bth + rng.pick(o.angles or { 15, -15, 22.5, -22.5, 30, -30 }))
    W.pos[2] = B.pos[2]
    finish(W, self.step)
    local ne = rng.pick({ "lo", "hi" })
    local Hz, fside = self:hinge_wing(B, side, W, th, { palette = W.palette, region = o.region, depth = o.depth or { 5, 8 }, near_end = ne })
    if Hz then
      -- the vestibule toward the far end of B's wall
      local _, a, b = side_geo(B, side)
      local lo_is_a = (side == "+x" or side == "-z")
      local far = ((ne == "lo") == lo_is_a) and b or a
      local nn = side_geo(B, side)
      local tgt = { far[1] + nn[1] * 3, far[2] + nn[2] * 3 }
      if self:bridge_poly(W, fside, B, side, { palette = W.palette, max_gap = 18, region = o.region, target = tgt }) then return true end
      while #self.G.zones > n0 do self:remove_zone(self.G.zones[#self.G.zones]) end
    end
    W.id, W.rot = nil, nil
  end
  return false
end

-- Grows a wing: members (sized rooms) in a frame turned from the body, the first hung off a body zone by a hinge
-- room, the rest attached in the wing's frame beside it, then the wing tied back to the body by a traced
-- vestibule (or a free corridor). A wing that cannot be tied back is taken down. Returns true when the wing stands.
function Comp:grow_wing(members, body, o)
  local rng = self.rng
  local anchor = members[1]
  for _ = 1, 6 do
    local n0 = #self.G.zones
    local th = rng.pick(o.angles or WING_ANGLES)
    local cx, cz = self:centroid()
    local hosts = {}
    for _, z in ipairs(body) do
      if z.archetype ~= "nook" and self.info[z] and self.info[z].r then
        for _, sd in ipairs(SIDES) do
          local nrm = side_geo(z, sd)
          local zx, zz = L.centre_of(z)
          local out = (zx - cx) * nrm[1] + (zz - cz) * nrm[2]
          -- a big room near the middle of the mass, on a wall that faces out of it
          local score = math.min(out, 0) * 0.3 - math.sqrt((zx - cx) ^ 2 + (zz - cz) ^ 2) * 0.4 + math.sqrt(L.zone_area(z)) * 0.3 + rng.float() * 8
          if out > -5 then hosts[#hosts + 1] = { z = z, side = sd, score = score } end
        end
      end
    end
    for i = 2, #hosts do
      local c, j = hosts[i], i - 1
      while j >= 1 and hosts[j].score < c.score do hosts[j + 1] = hosts[j] j = j - 1 end
      hosts[j + 1] = c
    end
    local host, plans = nil, {}
    for hi_ = 1, math.min(10, #hosts) do
      local h = hosts[hi_]
      anchor.pos[2] = h.z.pos[2]
      finish(anchor, self.step)
      local plan = self:hinge_wing(h.z, h.side, anchor, th, { palette = anchor.palette, dry = true, region = o.region })
      if plan then
        -- how well the wing can be tied back: body zones on its floor a short way off (not the host)
        local near = 0
        for _, bz in ipairs(body) do
          if bz ~= h.z and self.info[bz] and math.abs(bz.pos[2] - anchor.pos[2]) <= DATA.max_step + EPS then
            local d = sep(plan.infoW, self.info[bz], 16)
            if d >= 3 and d <= 16 then near = near + 1 end
          end
        end
        plan.score = near + rng.float()
        plan.host = h.z
        plans[#plans + 1] = plan
        if #plans >= 4 then break end
      end
    end
    local bestp = nil
    for _, pl in ipairs(plans) do if not bestp or pl.score > bestp.score then bestp = pl end end
    if bestp then
      anchor.pos[2] = bestp.host.pos[2]
      finish(anchor, self.step)
      bestp.commit()
      host = bestp.host
    end
    if host then
      local wplaced = { anchor }
      for k2 = 2, #members do
        local z = members[k2]
        z.pos[2] = anchor.pos[2] + ((rng.chance(0.3)) and 0.5 or 0)
        finish(z, self.step)
        -- beside the anchor, near the body (so the wing can close back onto it)
        if self:attach(z, th, wplaced, { lifts = o.lifts, compact_w = 2.4, region = o.region, styles = { abut = true, wide = true, join = rng.chance(0.3) } }) then
          wplaced[#wplaced + 1] = z
        end
      end
      -- the loop back: a vestibule from a wing room to a body zone
      local tries = {}
      for _, wz in ipairs(wplaced) do
        for _, bz in ipairs(body) do
          if self.info[bz] and self.info[bz].r and bz.archetype ~= "nook" then
            local d = sep(self.info[wz], self.info[bz], 22) + ((bz == host) and 8 or 0) - ((bz == o.prefer) and 4 or 0)
            -- (only walls on one floor that face each other: what a vestibule can join)
            if d >= 2.5 and d <= 22 and math.abs(wz.pos[2] - bz.pos[2]) <= DATA.max_step + EPS then
              for _, sw in ipairs(SIDES) do
                local nw = side_geo(wz, sw)
                for _, sb in ipairs(SIDES) do
                  local nb = side_geo(bz, sb)
                  if nw[1] * nb[1] + nw[2] * nb[2] <= -0.3 then
                    tries[#tries + 1] = { wz = wz, bz = bz, sw = sw, sb = sb, d = d + rng.float() * 3 + ((wz == anchor) and 4 or 0) }
                  end
                end
              end
            end
          end
        end
      end
      for i = 2, #tries do
        local c, j = tries[i], i - 1
        while j >= 1 and tries[j].d > c.d do tries[j + 1] = tries[j] j = j - 1 end
        tries[j + 1] = c
      end
      local closed = false
      for k2 = 1, (o.max_zones and #self.G.zones >= o.max_zones) and 0 or math.min(40, #tries) do
        local e = tries[k2]
        if self:bridge_poly(e.wz, e.sw, e.bz, e.sb, { palette = e.wz.palette, max_gap = 18, region = o.region }) then closed = true break end
      end
      if not closed then
        -- no vestibule fits: a link of any kind from a wing room to a body zone (a classic link, a doorway from a traced
        -- room, a corner room between the two frames, a corridor routed round the mass)
        local others = {}
        for _, bz in ipairs(body) do if bz ~= host and self.info[bz] then others[#others + 1] = bz end end
        closed = self:connect(wplaced, others, { lifts = o.lifts, rooms = self.zmax, region = o.region })
      end
      if closed then return true end
      -- a wing that cannot be tied back would hang off one room: take it down and try another bend
      while #self.G.zones > n0 do self:remove_zone(self.G.zones[#self.G.zones]) end
      for _, z in ipairs(members) do z.id = nil z.rot = nil end
    end
  end
  return false
end

-- ---------------------------------------------------------------------------------------------
-- Bays: traced apses and canted bays joined to a hall's wall (the hall's outline becomes T- or canted)
-- ---------------------------------------------------------------------------------------------

function Comp:bay(H, side, o)
  local rng = self.rng
  if H.archetype == "poly" or LA[H.archetype].open_air then return nil end
  local th = norm_rot(H.rot)
  local ranges = L.ranges_of(H, side, 0)
  local free = ranges
  for _, s in ipairs(self:used_of(H, side)) do free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end
  local spans = {}
  for _, s in ipairs(free) do if s[2] - s[1] >= 7 then spans[#spans + 1] = s end end
  if #spans == 0 then return nil end
  local sp = spans[rng.int(1, #spans)]
  local wlen = math.min(sp[2] - sp[1] - 0.5, even(rng.range(7, 12)))
  local u0 = q2(rng.range(sp[1] + 0.25, sp[2] - 0.25 - wlen))
  local u1 = u0 + wlen
  local depth = q2(rng.range(3, 5.5))
  local wc = L.wall_coord(H, side)
  local nx, nz = DIRV[side][1], DIRV[side][2]
  local shape = o.shape or rng.pick({ "apse", "trapezoid", "canted" })
  -- outline in H's local frame: (along u, out v) -> local point
  local function lp(u, v)
    if side == "+x" or side == "-x" then return { wc + nx * v, u } end
    return { u, wc + nz * v }
  end
  local uv
  if shape == "apse" then
    local c = math.min(depth - 1, wlen / 3)
    uv = { { u0, 0 }, { u1, 0 }, { u1, depth - c }, { u1 - c, depth }, { u0 + c, depth }, { u0, depth - c } }
  elseif shape == "trapezoid" then
    local c = math.min(depth * 0.8, wlen / 4)
    uv = { { u0, 0 }, { u1, 0 }, { u1 - c, depth }, { u0 + c, depth } }
  else
    local c = math.min(depth - 1, wlen / 3)
    if rng.chance(0.5) then uv = { { u0, 0 }, { u1, 0 }, { u1, depth }, { u0 + c, depth }, { u0, depth - c } }
    else uv = { { u0, 0 }, { u1, 0 }, { u1, depth - c }, { u1 - c, depth }, { u0, depth } } end
  end
  local pts = {}
  for i, q in ipairs(uv) do
    local p = lp(q[1], q[2])
    local x, zz = to_world(th, p[1], p[2])
    pts[i] = { snapv(x, 16), snapv(zz, 16) }
  end
  if not L.simple(pts) or math.abs(L.area2(pts)) < 18 then return nil end
  local info = self:poly_info(pts)
  if not self:fits(info, { [H] = true }) then return nil end
  local Bz = self:zone("poly", { role = o.role or "bay", outline = pts, y = H.pos[2] + (o.dy or 0), palette = H.palette })
  if not self:join_ok(H, Bz) then return nil end
  self:add(Bz, info)
  local jl = { type = "join", a = H.id, b = Bz.id }
  local u = self:used_of(H, side)
  u[#u + 1] = { lo = u0 - 0.25, hi = u1 + 0.25, y0 = H.pos[2], y1 = H.pos[2] + H.height, link = jl }
  self.G.links[#self.G.links + 1] = jl
  return Bz
end

-- ---------------------------------------------------------------------------------------------
-- Traced rooms in the corners of the mass: one edge a wall's thickness off each of two walls that meet at an
-- angle (two rooms of the body in an L, the body and a turned wing), a doorway through each, canted outside
-- ---------------------------------------------------------------------------------------------

-- A rectangle zone's wall as a plan line: at(c, g) = the world point at along-wall coordinate c (the zone frame's z
-- on its x walls, x on its z walls), g metres out; the world tangent (growing c) and outward normal.
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

-- The along-wall spans of z's side free for an opening at port level off (archetype ranges less booked openings).
function Comp:free_spans(z, side, off)
  local y0 = z.pos[2] + off
  local free = L.ranges_of(z, side, off)
  for _, s in ipairs(self:used_of(z, side)) do
    if s.y0 < y0 + 4.5 and s.y1 > y0 - 0.5 then free = L.subtract(free, s.lo - 1.5, s.hi + 1.5) end
  end
  return free
end

-- The door centre for a w m opening inside the stretch [a, b] of a wall, within its free spans (nearest the middle).
local function door_in(free, a, b, w)
  local best, bd = nil, math.huge
  local m = (a + b) / 2
  for _, s in ipairs(free) do
    local lo, hi = math.max(s[1], a + 0.75) + w / 2, math.min(s[2], b - 0.75) - w / 2
    if hi >= lo then
      local c = q4(clamp(m, lo, hi))
      if c < lo - EPS then c = c + 0.25 end
      if c > hi + EPS then c = c - 0.25 end
      if c >= lo - EPS and c <= hi + EPS and math.abs(c - m) < bd then best, bd = c, math.abs(c - m) end
    end
  end
  return best
end

-- The outline of a corner room between (A, sa) and (B, sb), or nil: v = {n0, la, m0, lb, bulge} (near cuts from the
-- corner, edge lengths, how far the outer edge bows out). Returns pts (counter-clockwise), the edge indices on A and
-- B, the along-wall stretches and the gap.
function Comp:corner_outline(A, sa, B, sb, v)
  local atA, tA, nA = wall_line(A, sa)
  local atB, tB, nB = wall_line(B, sb)
  local dot = nA[1] * nB[1] + nA[2] * nB[2]
  if dot > 0.6 or dot < -0.6 then return nil end
  -- (a sixteenth over a wall's thickness: the gap survives the plan being turned and snapped to the grid)
  local turned = norm_rot(A.rot) ~= 0 or norm_rot(B.rot) ~= 0
  local g = turned and 1.125 or 1.0625
  local den = tA[1] * tB[2] - tA[2] * tB[1]
  if math.abs(den) < 0.3 then return nil end
  local pA, pB = atA(0, g), atB(0, g)
  local dx, dz = pB[1] - pA[1], pB[2] - pA[2]
  local cC = (dx * tB[2] - dz * tB[1]) / den
  local eC = (dx * tA[2] - dz * tA[1]) / den
  -- the room lies along each wall on the side where it is in front of the other wall
  local sA = (tA[1] * nB[1] + tA[2] * nB[2]) > 0 and 1 or -1
  local sB = (tB[1] * nA[1] + tB[2] * nA[2]) > 0 and 1 or -1
  local alo, ahi = L.wall_span(A, sa)
  local blo, bhi = L.wall_span(B, sb)
  local function stretch(c0, s, n0, len, lo, hi)
    local a, b = c0 + s * n0, c0 + s * (n0 + len)
    if a > b then a, b = b, a end
    -- (the edge keeps to the wall's own extent)
    a, b = math.max(a, lo), math.min(b, hi)
    if b - a < 6 - EPS then return nil end
    return a, b
  end
  local a0, a1 = stretch(cC, sA, v[1], v[2], alo, ahi)
  if not a0 then return nil end
  local b0, b1 = stretch(eC, sB, v[3], v[4], blo, bhi)
  if not b0 then return nil end
  -- near ends first
  local an, af = a0, a1
  if sA < 0 then an, af = a1, a0 end
  local bn, bf = b0, b1
  if sB < 0 then bn, bf = b1, b0 end
  local QAn, QAf, QBn, QBf = atA(an, g), atA(af, g), atB(bn, g), atB(bf, g)
  local P = { QAn, QAf, QBf, QBn }
  if v[5] and v[5] > 0 then
    -- the outer edge bowed out: a pentagon
    local mx, mz = (QAf[1] + QBf[1]) / 2, (QAf[2] + QBf[2]) / 2
    local ex, ez = QBf[1] - QAf[1], QBf[2] - QAf[2]
    local el = math.sqrt(ex * ex + ez * ez)
    local ox, oz = ez / el, -ex / el
    local cx, cz = (QAn[1] + QBn[1]) / 2, (QAn[2] + QBn[2]) / 2
    if (mx - cx) * ox + (mz - cz) * oz < 0 then ox, oz = -ox, -oz end
    P[5] = { mx + ox * v[5] * el, mz + oz * v[5] * el }
  end
  for i, q in ipairs(P) do P[i] = { snapv(q[1], 16), snapv(q[2], 16) } end
  local pts = hull2(P)
  if #pts ~= #P or not L.simple(pts) then return nil end
  local function same(r, s) return math.abs(r[1] - s[1]) + math.abs(r[2] - s[2]) < 1e-6 end
  local function edge_of(p, q)
    for i = 1, #pts do
      local u, w = pts[i], pts[i % #pts + 1]
      if (same(u, p) and same(w, q)) or (same(u, q) and same(w, p)) then return i end
    end
    return nil
  end
  local ea, eb = edge_of(P[1], P[2]), edge_of(P[3], P[4])
  if not ea or not eb then return nil end
  return pts, ea, eb, { a0, a1 }, { b0, b1 }, g
end

-- Places a corner room between (A, sa) and (B, sb) from outline variant v (port levels oa, ob within a step): the
-- room and a doorway through each wall. Returns the zone or nil. o: palette, role, w, height, amin, amax, y.
function Comp:corner_room(A, sa, oa, B, sb, ob, v, o)
  o = o or {}
  local ya, yb = A.pos[2] + oa, B.pos[2] + ob
  if math.abs(ya - yb) > DATA.max_step + EPS then return nil end
  local pts, ea, eb, sa_, sb_, g = self:corner_outline(A, sa, B, sb, v)
  if not pts then return nil end
  local area = math.abs(L.area2(pts))
  if area < (o.amin or 40) or area > (o.amax or 460) then return nil end
  local info = self:poly_info(pts)
  if not self:fits(info) then return nil end
  local w = o.w or 3
  local da = door_in(self:free_spans(A, sa, oa), sa_[1], sa_[2], w)
  local db = door_in(self:free_spans(B, sb, ob), sb_[1], sb_[2], w)
  if not da or not db then return nil end
  local y = o.y or math.max(ya, yb)
  local Pz = self:zone("poly", { role = o.role or "room", outline = pts, y = y, palette = o.palette or A.palette })
  if o.height then Pz.want_height = o.height Pz.height = o.height end
  -- the doors are routed against the graph before anything is kept
  local X = self:route_ctx()
  self:add(Pz, info)
  local doors, Ps = {}, {}
  for _, e in ipairs({ { A, sa, oa, da, ea }, { B, sb, ob, db, eb } }) do
    local Z, side, off, c, edge = e[1], e[2], e[3], e[4], e[5]
    local at = wall_line(Z, side)
    local door = { type = "doorway", width = w, a_side = side, b_side = edge, a_at = at(c, 0), b_at = at(c, g), a_y = Z.pos[2] + off, b_y = y,
      a = Z.id, b = Pz.id, id = "l" .. (#self.G.links + #doors + 1) }
    local P, errs = L.route_link(X, door, 60)
    doors[#doors + 1] = { door = door, Z = Z, side = side, off = off, c = c }
    Ps[#Ps + 1] = P
    if #errs > 0 or not P.ok then
      for i = #Ps, 1, -1 do L.route_drop(X, Ps[i]) end
      X.plan.zones[Pz.id], X.plan.ports[Pz.id] = nil, nil
      self:remove_zone(Pz)
      self.rctx_version = self.version
      return nil
    end
  end
  for i, d in ipairs(doors) do
    self:commit_free(d.door, d.Z, Pz, { { zone = d.Z, side = d.side, lo = d.c - w / 2, hi = d.c + w / 2, y0 = d.Z.pos[2] + d.off, y1 = d.Z.pos[2] + d.off + 4 } })
    self:foot_free(Ps[i])
  end
  self.rctx_version = self.version
  return Pz
end

-- The corner rooms the mass has room for: every pair of rectangle zones (any frames) whose walls meet at an angle
-- with a corner in front of both. Returns candidates {A, sa, oa, B, sb, ob, v, score}, best first. o: amin, amax, area
-- (the target), pool (the zones that may host), region (shape -> bool), level (the lower port floor), reach.
function Comp:corner_candidates(o)
  local rng = self.rng
  local out = {}
  local cx, cz, mass = self:centroid()
  local R = math.sqrt(mass)
  local zs = {}
  for _, z in ipairs(o.pool or self.G.zones) do
    local iz = self.info[z]
    if iz and iz.r and z.role ~= "nook" and z.role ~= "bay" then zs[#zs + 1] = z end
  end
  local tgt = o.area or 220
  local s = math.sqrt(tgt * 2)
  -- (the wall pairs that meet at an angle first, cheaply; at most 48 of them are measured)
  local nrm = {}
  local function nrm_of(z, side)
    local t = nrm[z]
    if not t then t = {} nrm[z] = t end
    if not t[side] then local x, zz = to_world(norm_rot(z.rot), DIRV[side][1], DIRV[side][2]) t[side] = { x, zz } end
    return t[side]
  end
  local combos = {}
  for i = 1, #zs do
    for j = i + 1, #zs do
      local A, B = zs[i], zs[j]
      if box_gap(bb_of(self.info[A]), bb_of(self.info[B])) <= (o.reach or 24) then
        for _, sa in ipairs(SIDES) do
          for _, sb in ipairs(SIDES) do
            local na, nb = nrm_of(A, sa), nrm_of(B, sb)
            local dot = na[1] * nb[1] + na[2] * nb[2]
            if dot <= 0.6 and dot >= -0.6 then
              for _, oa in ipairs(A.levels) do
                for _, ob in ipairs(B.levels) do
                  local ya, yb = A.pos[2] + oa, B.pos[2] + ob
                  if math.abs(ya - yb) <= DATA.max_step + EPS and (not o.level or math.abs(math.min(ya, yb) - o.level) < 1.01) then
                    combos[#combos + 1] = { A = A, sa = sa, oa = oa, B = B, sb = sb, ob = ob }
                  end
                end
              end
            end
          end
        end
      end
    end
  end
  if #combos > 48 then
    rng.shuffle(combos)
    for k = #combos, 49, -1 do combos[k] = nil end
  end
  for _, cb in ipairs(combos) do
    local A, B, sa, sb, oa, ob = cb.A, cb.B, cb.sa, cb.sb, cb.oa, cb.ob
    if L.widest(L.ranges_of(A, sa, oa)) >= 6 and L.widest(L.ranges_of(B, sb, ob)) >= 6 then
      for _ = 1, (o.variants or 2) do
        local la, lb = even(s * rng.range(0.7, 1.3)), even(s * rng.range(0.7, 1.3))
        local bulge = o.bulge and rng.range(o.bulge[1], o.bulge[2]) or (rng.chance(0.5) and rng.range(0.12, 0.3) or 0)
        local v = { rng.pick({ 1.5, 2, 3, 4 }), la, rng.pick({ 1.5, 2, 3, 4 }), lb, bulge }
        local pts = self:corner_outline(A, sa, B, sb, v)
        if pts then
          local area = math.abs(L.area2(pts))
          if area >= (o.amin or 40) and area <= (o.amax or 460) then
            local info = self:poly_info(pts)
            if (not o.region or o.region(info)) and self:fits(info) then
              local x0, z0, x1, z1 = L.bbox(pts)
              local d = math.sqrt(((x0 + x1) / 2 - cx) ^ 2 + ((z0 + z1) / 2 - cz) ^ 2)
              local sc = -math.abs(area - tgt) / tgt * 2 - 1.2 * d / R + rng.float() * 0.8
              out[#out + 1] = { A = A, sa = sa, oa = oa, B = B, sb = sb, ob = ob, v = v, score = sc }
            end
          end
        end
      end
    end
  end
  for i = 2, #out do
    local c, j = out[i], i - 1
    while j >= 1 and out[j].score < c.score do out[j + 1] = out[j] j = j - 1 end
    out[j + 1] = c
  end
  return out
end

-- Places up to n corner rooms (the best candidates first, measured again after each). Returns the rooms placed.
-- o: as corner_candidates, plus height (candidate -> metres or nil), role.
function Comp:corner_rooms(n, o)
  local made = {}
  for _ = 1, n do
    if self.zmax and #self.G.zones >= self.zmax then break end
    local cands = self:corner_candidates(o)
    local done = nil
    for k = 1, math.min(8, #cands) do
      local c = cands[k]
      done = self:corner_room(c.A, c.sa, c.oa, c.B, c.sb, c.ob, c.v,
        { palette = c.A.palette, height = o.height and o.height(c), amin = o.amin, amax = o.amax, role = o.role })
      if done then break end
    end
    if not done then break end
    made[#made + 1] = done
  end
  return made
end

-- Free doorways between a traced zone and a rectangle zone whose wall runs parallel to one of its edges a wall's
-- thickness off (loop-closing candidates {a = the rectangle zone, b = the traced one, link, weight, kind, reserve}).
function Comp:poly_contacts(P)
  local out = {}
  local iP = self.info[P]
  local pts = iP and iP.wp
  if not pts then return out end
  for _, R in ipairs(self.G.zones) do
    local iR = self.info[R]
    if R ~= P and iR and iR.r and not self:linked(R, P) and R.role ~= "nook" and box_gap(bb_of(iR), bb_of(iP)) < 1.7 then
      for _, side in ipairs(SIDES) do
        local at, t, n = wall_line(R, side)
        local p0 = at(0, 0)
        for i = 1, #pts do
          local a, b = pts[i], pts[i % #pts + 1]
          local ex, ez = b[1] - a[1], b[2] - a[2]
          local el = math.sqrt(ex * ex + ez * ez)
          -- (a counter-clockwise outline: edge i's outward normal is (ez, -ex) / length)
          local nx, nz = ez / el, -ex / el
          if el >= 6 and nx * n[1] + nz * n[2] < -0.999 then
            local ga = (a[1] - p0[1]) * n[1] + (a[2] - p0[2]) * n[2]
            local gb = (b[1] - p0[1]) * n[1] + (b[2] - p0[2]) * n[2]
            if ga >= 0.95 and ga <= 1.6 and gb >= 0.95 and gb <= 1.6 then
              local ca = (a[1] - p0[1]) * t[1] + (a[2] - p0[2]) * t[2]
              local cb = (b[1] - p0[1]) * t[1] + (b[2] - p0[2]) * t[2]
              local lo, hi = math.min(ca, cb), math.max(ca, cb)
              for _, off in ipairs(R.levels) do
                local yr = R.pos[2] + off
                if math.abs(yr - P.pos[2]) <= DATA.max_step + EPS then
                  local w = 3
                  local c = door_in(self:free_spans(R, side, off), lo, hi, w)
                  if c then
                    local g = (ga + gb) / 2
                    local link = { type = "doorway", width = w, a_side = side, b_side = i, a_at = at(c, 0), b_at = at(c, g), a_y = yr, b_y = P.pos[2] }
                    out[#out + 1] = { a = R, b = P, link = link, weight = 1.6, kind = "free",
                      reserve = { { zone = R, side = side, lo = c - w / 2, hi = c + w / 2, y0 = yr, y1 = yr + 4 } } }
                  end
                end
              end
            end
          end
        end
      end
    end
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- Traced outlines for finished rooms: a rectangle room whose links are doorways, corridors, stairs and ramps
-- becomes a traced room of the same footprint less what its corners can spare (canted corners, a notch that makes
-- an L, a canted end); its links keep their openings, now on the outline's edges
-- ---------------------------------------------------------------------------------------------

local TRACE_LINKS = { doorway = true, corridor = true, stair = true, ramp = true }
-- the corners of a rectangle in its frame, counter-clockwise, with the walls that meet there (in, out)
local CORNERS = { { "x0", "z0", "-x", "-z" }, { "x1", "z0", "-z", "+x" }, { "x1", "z1", "+x", "+z" }, { "x0", "z1", "+z", "-x" } }

-- The outline of rectangle zone R with its corners cut where its walls have no openings (spans: wall -> {{lo, hi}}).
-- Returns the world points and, per wall, the index of the outline edge that lies on it; nil when no corner has room.
-- sym (a zone on the mirror axis x = 0): the cuts come in mirrored pairs, on the two corners of the wall sym names
-- ("-z" or "+z"), so the outline stays symmetric.
function Comp:traced_outline(R, spans, joined, sym)
  local rng = self.rng
  local x0, z0, x1, z1 = L.rect_of(R)
  local P = { x0 = x0, x1 = x1, z0 = z0, z1 = z1 }
  local len = { ["-z"] = x1 - x0, ["+z"] = x1 - x0, ["-x"] = z1 - z0, ["+x"] = z1 - z0 }
  -- the room a wall leaves free at one of its ends (coordinate e) before its first opening
  local function free_from(side, e, low_end)
    local f = len[side] * 0.45
    for _, s in ipairs(spans[side] or {}) do
      if low_end then f = math.min(f, s[1] - e - 1.25) else f = math.min(f, e - s[2] - 1.25) end
    end
    return f
  end
  -- along-wall coordinate of a corner on a wall, and whether it is the wall's low end
  local function at(side, cx, cz) if side == "-z" or side == "+z" then return cx, cx == x0 end return cz, cz == z0 end
  local cuts = {}
  local any = false
  local order = { 1, 2, 3, 4 }
  rng.shuffle(order)
  local budget = rng.int(1, 2)
  if sym then
    -- corners 1 and 2 (or 4 and 3) mirror each other: a cut a along the in wall and b along the out wall at one is
    -- b and a at the other
    local k1, k2 = 1, 2
    if sym == "+z" then k1, k2 = 4, 3 end
    local c1, c2 = CORNERS[k1], CORNERS[k2]
    local x1_, z1_ = P[c1[1]], P[c1[2]]
    local x2_, z2_ = P[c2[1]], P[c2[2]]
    local ea1, la1 = at(c1[3], x1_, z1_)
    local eb1, lb1 = at(c1[4], x1_, z1_)
    local ea2, la2 = at(c2[3], x2_, z2_)
    local eb2, lb2 = at(c2[4], x2_, z2_)
    local fa = math.min(free_from(c1[3], ea1, la1), free_from(c2[4], eb2, lb2))
    local fb = math.min(free_from(c1[4], eb1, lb1), free_from(c2[3], ea2, la2))
    if fa < 3 or fb < 3 then return nil end
    local a = q2(rng.range(3, math.min(fa, 9)))
    local b = q2(rng.range(3, math.min(fb, 9)))
    local kind = (rng.chance(0.5) and a >= 4 and b >= 4) and "notch" or "cant"
    cuts[k1] = { kind = kind, a = a, b = b }
    cuts[k2] = { kind = kind, a = b, b = a }
    any = true
    budget = 0
  end
  for _, k in ipairs(order) do
    local c = CORNERS[k]
    local cx, cz = P[c[1]], P[c[2]]
    local ea, la = at(c[3], cx, cz)
    local eb, lb = at(c[4], cx, cz)
    local fa, fb = free_from(c[3], ea, la), free_from(c[4], eb, lb)
    if budget > 0 and fa >= 3 and fb >= 3 then
      budget = budget - 1
      local a = q2(rng.range(3, math.min(fa, 9)))
      local b = q2(rng.range(3, math.min(fb, 9)))
      cuts[k] = { kind = (rng.chance(0.55) and a >= 4 and b >= 4) and "notch" or "cant", a = a, b = b }
      any = true
    end
  end
  if not any then return nil end
  -- the local outline: each corner as itself, two points (a cant) or three (a notch); the edge leaving a corner
  -- lies on its out wall
  local DIR = { ["-z"] = { 1, 0 }, ["+x"] = { 0, 1 }, ["+z"] = { -1, 0 }, ["-x"] = { 0, -1 } }
  -- (walls without a join step a sixteenth in: the room keeps more than a wall's thickness from its neighbours when
  -- the plan is turned and snapped to the grid)
  local inset = 1 / 16
  joined = joined or {}
  -- (a symmetric outline steps both of its side walls in alike)
  if sym then joined = { ["-x"] = joined["-x"] or joined["+x"], ["+x"] = joined["-x"] or joined["+x"], ["-z"] = joined["-z"], ["+z"] = joined["+z"] } end
  if not joined["-x"] then P.x0 = x0 + inset end
  if not joined["+x"] then P.x1 = x1 - inset end
  if not joined["-z"] then P.z0 = z0 + inset end
  if not joined["+z"] then P.z1 = z1 - inset end
  local pts, edge_of = {}, {}
  for k = 1, 4 do
    local c = CORNERS[k]
    local cx, cz = P[c[1]], P[c[2]]
    local din, dout = DIR[c[3]], DIR[c[4]]
    local cut = cuts[k]
    if not cut then
      pts[#pts + 1] = { cx, cz }
    else
      local pin = { cx - din[1] * cut.a, cz - din[2] * cut.a }
      local pout = { cx + dout[1] * cut.b, cz + dout[2] * cut.b }
      pts[#pts + 1] = pin
      if cut.kind == "notch" then pts[#pts + 1] = { pin[1] + dout[1] * cut.b, pin[2] + dout[2] * cut.b } end
      pts[#pts + 1] = pout
    end
    edge_of[c[4]] = #pts
  end
  local th = norm_rot(R.rot)
  for i, q in ipairs(pts) do local x, z = to_world(th, q[1], q[2]) pts[i] = { snapv(x, 16), snapv(z, 16) } end
  if not L.simple(pts) then return nil end
  return pts, edge_of
end

-- Turns rectangle room R into a traced room (see above) when its links allow; plan: the graph's resolved plan.
-- Returns true when done (the resolver routes every one of its links on the new outline).
-- o.symmetric: a zone on the mirror axis, traced with mirrored cuts on its wall o.symmetric names (its blue-side
-- openings, not made yet, are the mirror of its red-side ones).
function Comp:trace_room(R, plan, o)
  o = o or {}
  -- (turned rooms keep their rectangles: the wing reads by its turn)
  if R.archetype == "poly" or #R.levels ~= 1 or norm_rot(R.rot) ~= 0 then return false end
  local A = LA[R.archetype]
  if A.hub or A.spine or A.open_air or R.role == "hub" or R.role == "hill" or R.role == "base" or R.role == "nook" then return false end
  local mine = {}
  for _, l in ipairs(self.G.links) do
    if l.a == R.id or l.b == R.id then
      -- (a join stays a join when the other zone is traced too: a bay, a hinge room)
      if l.type == "join" then
        local other = self:byid((l.a == R.id) and l.b or l.a)
        if not other or other.archetype ~= "poly" then return false end
      elseif not TRACE_LINKS[l.type] then return false
      -- (see bend_ok: beyond its reach a bent stair or ramp keeps the room a rectangle)
      elseif (l.type == "stair" or l.type == "ramp") and type(l.path) == "table" and #l.path > 0 and not within_reach(l.path) then return false
      else mine[#mine + 1] = l end
    end
  end
  local spans, joined = {}, {}
  for _, p in ipairs(plan.ports[R.id] or {}) do
    if type(p.side) == "string" then
      spans[p.side] = spans[p.side] or {}
      table.insert(spans[p.side], { p.lo, p.hi })
      if p.join then joined[p.side] = true end
    end
  end
  if o.symmetric then
    -- (the openings the blue half will make: the mirror of the red ones)
    local ms = { ["+x"] = "-x", ["-x"] = "+x" }
    local add = {}
    for side, list in pairs(spans) do
      for _, sp in ipairs(list) do
        if ms[side] then add[#add + 1] = { ms[side], { sp[1], sp[2] } }
        else add[#add + 1] = { side, { -sp[2], -sp[1] } } end
      end
    end
    for _, e in ipairs(add) do spans[e[1]] = spans[e[1]] or {} table.insert(spans[e[1]], e[2]) end
  end
  local pts, edge_of = self:traced_outline(R, spans, joined, o.symmetric)
  if not pts then return false end
  -- the links on the outline's edges: classic ones become free links between the same two openings
  local saved = {}
  for _, l in ipairs(mine) do
    local s = {}
    for k, v in pairs(l) do s[k] = v end
    saved[l] = s
  end
  local byP = {}
  for _, P in ipairs(plan.links) do byP[P.link] = P end
  for _, l in ipairs(mine) do
    local P = byP[l]
    if not P or not P.ok then return false end
    local isa = l.a == R.id
    if P.mode == "classic" then
      local c = P.c
      local za, zb = P.za, P.zb
      local pa = L.wall_point(za, P.side_a, c)
      local pb = L.wall_point(zb, P.side_b, c)
      local ax, az = to_world(norm_rot(za.rot), pa[1], pa[2])
      local bx, bz = to_world(norm_rot(zb.rot), pb[1], pb[2])
      l.a_side, l.b_side = P.side_a, P.side_b
      l.a_at, l.b_at = { ax, az }, { bx, bz }
      l.offset = nil
    end
    local side = isa and l.a_side or l.b_side
    local e = (type(side) == "string") and edge_of[side] or nil
    if not e then
      for k, v in pairs(saved) do for kk in pairs(k) do k[kk] = nil end for kk, vv in pairs(v) do k[kk] = vv end end
      return false
    end
    if isa then l.a_side = e else l.b_side = e end
  end
  -- the zone itself
  local keep = { archetype = R.archetype, size = { R.size[1], R.size[2] }, pos = { R.pos[1], R.pos[2], R.pos[3] }, levels = R.levels, chamfer = R.chamfer,
    height = R.height, loops = R.loops, label = R.label, params = R.params, sky = R.sky, ground = R.ground, outline = R.outline, rot = R.rot }
  local x0, z0, x1, z1 = L.bbox(pts)
  R.archetype, R.outline, R.rot = "poly", pts, nil
  R.size = { x1 - x0, z1 - z0 }
  R.pos[1], R.pos[3] = (x0 + x1) / 2, (z0 + z1) / 2
  R.levels, R.chamfer, R.loops, R.params = { 0 }, 0, 0, {}
  R.label = "poly (" .. (R.role or "room") .. ")"
  -- (its routes, measured once on the new outline)
  local plan2 = L.resolve(self.G, nil, false)
  local bad = #plan2.errors > 0
  if not bad then
    for _, P2 in ipairs(plan2.links) do if (P2.link.a == R.id or P2.link.b == R.id) and not P2.ok then bad = true end end
  end
  if bad then
    for k, v in pairs(keep) do R[k] = v end
    R.pos = keep.pos
    for k, v in pairs(saved) do for kk in pairs(k) do k[kk] = nil end for kk, vv in pairs(v) do k[kk] = vv end end
    return false
  end
  self.info[R] = self:poly_info(pts)
  self.version = (self.version or 0) + 1
  return true, plan2
end

-- Traces up to n finished rectangle rooms (medium rooms, the seeded stream's order). Returns the number traced.
function Comp:trace_rooms(n, o)
  o = o or {}
  if n <= 0 then return 0 end
  local G = self.G
  for i, l in ipairs(G.links) do l.id = "l" .. i end
  local plan = L.resolve(G, nil, false)
  if #plan.errors > 0 then return 0 end
  local list = {}
  for _, z in ipairs(G.zones) do
    local a = L.zone_area(z)
    if z.archetype ~= "poly" and #z.levels == 1 and norm_rot(z.rot) == 0 and a >= 150 and a <= (o.amax or 520) and (not o.filter or o.filter(z)) then list[#list + 1] = z end
  end
  self.rng.shuffle(list)
  -- (the rooms the programme sized for it first)
  local first = {}
  for _, z in ipairs(list) do if z.trace then first[#first + 1] = z end end
  for _, z in ipairs(list) do if not z.trace then first[#first + 1] = z end end
  list = first
  local made = 0
  for _, z in ipairs(list) do
    if made >= n then break end
    local ok, plan2 = self:trace_room(z, plan)
    if ok then made = made + 1 plan = plan2 end
  end
  return made
end

-- ---------------------------------------------------------------------------------------------
-- A perimeter gallery: a traced L that wraps two sides of the mass a wall's thickness off, turning at one corner
-- ---------------------------------------------------------------------------------------------

-- Lays a long gallery (one traced zone, its two arms w m wide) along two sides of the box of the rectangle zones of
-- frame 0 at floor y, turning round one corner (its outer corner canted), with a doorway into a room beside each arm.
-- Returns the zone or nil. o: y, palette, width {lo, hi}, height, reach {lo, hi} (each arm's share of its side).
function Comp:perimeter_gallery(o)
  o = o or {}
  local rng = self.rng
  local y = o.y or 0
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, z in ipairs(self.G.zones) do
    local iz = self.info[z]
    if iz and iz.r and same_frame(iz.f, 0) then
      x0, z0, x1, z1 = math.min(x0, iz.r[1]), math.min(z0, iz.r[2]), math.max(x1, iz.r[3]), math.max(z1, iz.r[4])
    end
  end
  if x0 == math.huge then return nil end
  local mx, mz = (x0 + x1) / 2, (z0 + z1) / 2
  -- (a sixteenth over a wall's thickness off the outermost walls, as the corner rooms keep)
  local g = 1.0625
  local corners = { { 1, 1 }, { -1, 1 }, { 1, -1 }, { -1, -1 } }
  rng.shuffle(corners)
  for _, sg in ipairs(corners) do
    for _ = 1, 2 do
      local w = q2(rng.range(o.width and o.width[1] or 6, o.width and o.width[2] or 8))
      local ra, rb = o.reach or { 0.55, 0.95 }, o.reach or { 0.55, 0.95 }
      local la = q2((x1 - x0) * rng.range(ra[1], ra[2]) + g + w)
      local lb = q2((z1 - z0) * rng.range(rb[1], rb[2]) + g + w)
      local c = q2(math.min(w * 0.8, rng.range(2, 4)))
      -- the north-east L in the box's frame, then turned to the corner by reflection about the box's middle
      local ex, ez = x1 + g + w, z1 + g + w
      local L0 = { { ex - la, z1 + g }, { x1 + g, z1 + g }, { x1 + g, ez - lb }, { ex, ez - lb }, { ex, ez - c }, { ex - c, ez }, { ex - la, ez } }
      local pts = {}
      for i, q in ipairs(L0) do pts[i] = { snapv(mx + sg[1] * (q[1] - mx), 16), snapv(mz + sg[2] * (q[2] - mz), 16) } end
      pts = L.ccw(pts)
      if L.simple(pts) then
        local info = self:poly_info(pts)
        if (not o.region or o.region(info)) and self:fits(info) then
          local Pz = self:zone("poly", { role = "room", outline = pts, y = y, palette = o.palette })
          if o.height then Pz.want_height = o.height Pz.height = o.height end
          self:add(Pz, info)
          -- a doorway from a room beside each arm (two different rooms)
          local made, rooms = 0, {}
          local cands = self:poly_contacts(Pz)
          rng.shuffle(cands)
          for _, cd in ipairs(cands) do
            if made < 2 and not rooms[cd.a] and self:try_add(cd) then made = made + 1 rooms[cd.a] = true end
          end
          if made >= 2 then return Pz end
          self:remove_zone(Pz)
        end
      end
    end
  end
  return nil
end

-- ---------------------------------------------------------------------------------------------
-- Programmes and the gametype generators
-- ---------------------------------------------------------------------------------------------

-- An even size for an archetype near an area and aspect (w / d), clamped to the archetype; distinct from the
-- sizes already used (no two un-mirrored zones the same size).
local function size_for(A, area, aspect, taken)
  local w = math.sqrt(area * aspect)
  local d = area / w
  w = clamp(even(w), A.min[1], A.max[1])
  d = clamp(even(d), A.min[2], A.max[2])
  local function key(a, b) return math.min(a, b) .. "x" .. math.max(a, b) end
  local tries = 0
  while taken and taken[key(w, d)] and tries < 8 do
    tries = tries + 1
    if tries % 2 == 1 then
      if w + 2 <= A.max[1] then w = w + 2 elseif w - 2 >= A.min[1] then w = w - 2 end
    else
      if d - 2 >= A.min[2] then d = d - 2 elseif d + 2 <= A.max[2] then d = d + 2 end
    end
  end
  if taken then taken[key(w, d)] = true end
  return { w, d }
end

local function pick_arch(rng, pool, used, avoid)
  local cand = {}
  for _, a in ipairs(pool) do
    if (used[a] or 0) < DATA.repeat_max and not (avoid and avoid[a]) then cand[#cand + 1] = a end
  end
  if #cand == 0 then for _, a in ipairs(pool) do if (used[a] or 0) < DATA.repeat_max then cand[#cand + 1] = a end end end
  if #cand == 0 then cand = pool end
  local a = rng.pick(cand)
  used[a] = (used[a] or 0) + 1
  return a
end

-- The programme's shape for a rectangle zone of a kind: most plain (no chamfer), some with the archetype's, a few
-- with a large one; headroom by kind (tall halls, low small rooms and galleries).
local function style(z, kind, rng)
  if kind == "hub" then return z end
  -- (a centre zone of a team layout: plain or the archetype's corners, its own headroom)
  if kind == "centre" then z.want_chamfer = rng.chance(0.6) and 0 or nil return z end
  local w, d = z.size[1], z.size[2]
  if kind == "gallery" or kind == "small" or kind == "annex" then
    z.want_chamfer = 0
  else
    local r = rng.float()
    if r < 0.58 then z.want_chamfer = 0
    elseif r < 0.82 or z.archetype == "storage" then z.want_chamfer = nil
    else z.want_chamfer = q2(clamp(math.min(w, d) * rng.range(0.18, 0.26), 3, 7)) end
  end
  if z.archetype == "hall" then
    z.want_height = (w * d >= 340) and rng.pick({ 8, 9, 10, 10 }) or rng.pick({ 6, 6.5, 7 })
  elseif z.archetype == "colonnade" then
    z.want_height = (w * d >= 400) and rng.pick({ 9, 10 }) or 7
  elseif kind == "annex" and z.archetype ~= "nook" and z.archetype ~= "storage" then
    z.want_height = 5
  end
  return z
end

-- Adds one walk link between a zone of S1 and a zone of S2 (the nearest pairs first): a classic link between
-- facing walls, a doorway from a traced zone through a wall beside it, a corner room with a doorway into each (while
-- the zone budget has room), a free corridor, stair or ramp. Returns true when made. o: lifts, rooms (max zones).
function Comp:connect(S1, S2, o)
  o = o or {}
  local list = {}
  local inS2 = {}
  for _, b in ipairs(S2) do inS2[b] = true end
  for _, a in ipairs(S1) do
    for _, b in ipairs(S2) do
      if a ~= b and self.info[a] and self.info[b] and a.role ~= "nook" and b.role ~= "nook" then
        list[#list + 1] = { a = a, b = b, d = sep(self.info[a], self.info[b], 40) }
      end
    end
  end
  for i = 2, #list do
    local c, j = list[i], i - 1
    while j >= 1 and list[j].d > c.d do list[j + 1] = list[j] j = j - 1 end
    list[j + 1] = c
  end
  for _, e in ipairs(list) do
    local ia, ib = self.info[e.a], self.info[e.b]
    if e.d <= 15 and ia.r and ib.r and same_frame(ia.f, ib.f) then
      for _, c in ipairs(self:pair_links(e.a, e.b, { max_gap = 15, lifts = o.lifts })) do
        if self:try_add(c) then return true end
      end
    end
    if e.d <= 1.7 and (e.a.archetype == "poly") ~= (e.b.archetype == "poly") then
      local P, R = e.a, e.b
      if P.archetype ~= "poly" then P, R = e.b, e.a end
      for _, c in ipairs(self:poly_contacts(P)) do
        if c.a == R and self:try_add(c) then return true end
      end
    end
  end
  if o.rooms and #self.G.zones < o.rooms then
    local pool = {}
    for _, z in ipairs(S1) do pool[#pool + 1] = z end
    for _, z in ipairs(S2) do pool[#pool + 1] = z end
    local in1 = {}
    for _, z in ipairs(S1) do in1[z] = true end
    local cands = self:corner_candidates({ pool = pool, area = o.area or 150, amin = 40, amax = 360, region = o.region, variants = 2 })
    local tried = 0
    for _, c in ipairs(cands) do
      if tried < 6 and ((in1[c.A] and inS2[c.B]) or (in1[c.B] and inS2[c.A])) then
        tried = tried + 1
        local z = self:corner_room(c.A, c.sa, c.oa, c.B, c.sb, c.ob, c.v, { palette = c.A.palette, amin = 40, amax = 360 })
        if z then return true end
      end
    end
  end
  local tries = 0
  for _, e in ipairs(list) do
    if tries < (o.free_tries or 4) and e.d >= 2 and e.d <= 30 and self:clear_line(e.a, e.b, 2) then
      for _, oa in ipairs(e.a.levels) do
        for _, ob in ipairs(e.b.levels) do
          local ya, yb = e.a.pos[2] + oa, e.b.pos[2] + ob
          local dy = math.abs(ya - yb)
          local t = (dy <= DATA.max_step + EPS) and "corridor" or ((dy >= 1 and dy <= self.step + 1) and self.rng.pick({ "stair", "ramp" }) or nil)
          if t and tries < (o.free_tries or 4) then
            tries = tries + 1
            if self:test_free({ type = t, a_y = ya, b_y = yb, width = 4, keep = true }, e.a, e.b) then return true end
          end
        end
      end
    end
  end
  return false
end

-- Gives zones that still have one way in another, the zones behind an articulation zone a second way out (through
-- connect: classic links, doorways from traced zones, corner rooms, routed corridors and climbs), and runs the
-- gametype's own extra (o.extra(C) -> links made). Bounded: two passes. Returns the number of links made.
function Comp:repair(o)
  o = o or {}
  local G = self.G
  local made = 0
  for _ = 1, 2 do
    local before = made
    local list = {}
    for _, z in ipairs(G.zones) do list[#list + 1] = z end
    for _, z in ipairs(list) do
      if z.role ~= "nook" and z.role ~= "bay" and self.info[z] and self:degree(z) < 2 then
        local others = {}
        for _, p in ipairs(G.zones) do if p ~= z and not self:linked(z, p) then others[#others + 1] = p end end
        if self:connect({ z }, others, o) or self:relocate(z, o) then made = made + 1 end
      end
    end
    for _ = 1, 4 do
      local idx = L.index_zones(G)
      local adj = L.adjacency(G, idx)
      local area = self:area()
      local cut = nil
      for _, a in ipairs(L.articulation(G, idx, adj)) do
        if a.cut >= DATA.articulation_share * area - EPS and (not cut or a.cut > cut.cut) then cut = a end
      end
      if not cut then break end
      local behind, rest, set = {}, {}, {}
      for _, id in ipairs(cut.behind) do set[id] = true end
      for _, z in ipairs(G.zones) do
        if set[z.id] then behind[#behind + 1] = z elseif z.id ~= cut.zone then rest[#rest + 1] = z end
      end
      if not self:connect(behind, rest, o) then break end
      made = made + 1
    end
    if o.extra then made = made + o.extra(self) end
    if made == before then break end
  end
  return made
end

-- DM: a hub with a sunken court (a second and third on L) and rooms of graded sizes on three storeys, a two-storey
-- hall beside the hub that carries the storeys, a long gallery, small rooms, joined annexes, wings at an angle,
-- traced rooms in the corners the mass leaves and re-outlined rooms, bays, a reward nook.
-- kp (KOTH): {hill = archetype, approaches}: the hub is the hill (role "hill"), the ring of rooms is grown round it
-- and the first rooms meet it directly (its approaches).
local function compose_dm(G, rng, t, kp)
  local C = new_comp(G, rng, false)
  local B = G.budget
  local sc = G.size_class
  local step = G.step
  local themes = G.themes
  local lifts = (sc == "M" or sc == "L")
  local used, taken = {}, {}
  -- the hub(s)
  local nhubs = kp and 1 or B.hubs[1]
  local hub_arch = {}
  -- the storeys by construction: the (first) hub's sunken court gives the floor below the hub's (a stair and a drop
  -- inside it), a two-storey hall beside the hub the one above (its stairs, and a drop from its gallery ring into
  -- the court); further hubs are atria and arenas
  hub_arch[1] = kp and kp.hill or "sunken"
  used[hub_arch[1]] = 1
  for i = 2, nhubs do hub_arch[i] = pick_arch(rng, { "atrium", "arena" }, used) end
  -- the programme: counts by class
  local P = ({
    XS = { med = 1, wings = {}, polys = 1, small = 1, gal = 0, annex = 0, bays = 0 },
    S = { med = 2, wings = { 1 }, polys = 2, small = 0, gal = 1, annex = 0, bays = 1 },
    M = { med = 3, wings = { 1, 1 }, polys = 2, small = 1, gal = 1, annex = 1, bays = 1 },
    L = { med = 4, wings = { 2, 1 }, polys = 3, small = 2, gal = 2, annex = 1, bays = 1 },
  })[sc] or { med = 1, wings = { 1 }, polys = 2, small = 1, gal = 1, annex = 0, bays = 1 }
  -- (KOTH: big rooms round the hill; its hinge rooms are the small ones)
  -- (KOTH: big rooms round the hill and as many small ones (low side rooms, traced later, a gallery, the hinge
  -- rooms): the hill stands well above the median)
  if kp then P = { med = 3, wings = (sc == "S") and { 1 } or { 1, 1 }, polys = 2, small = (sc == "S") and 3 or 2, gal = 1, annex = 0, bays = 0 } end
  local ncore = 1
  local zmax = B.zones[2]
  local function total()
    local n = nhubs + ncore + P.med + P.small + P.gal + P.annex + P.polys
    for _, k in ipairs(P.wings) do n = n + k + 1 end
    return n
  end
  -- (trim to the zone budget: annexes, then medium rooms, galleries and small rooms, then wing rooms)
  while total() > zmax do
    if P.annex > 0 then P.annex = P.annex - 1
    elseif P.med > 0 then P.med = P.med - 1
    elseif P.polys > 2 then P.polys = P.polys - 1
    elseif P.small > 1 then P.small = P.small - 1
    elseif #P.wings > 0 and P.wings[#P.wings] > 1 then P.wings[#P.wings] = P.wings[#P.wings] - 1
    elseif P.gal > 1 then P.gal = P.gal - 1
    elseif #P.wings > 0 then P.wings[#P.wings] = nil
    else P.polys = P.polys - 1 end
  end
  while total() < B.zones[1] do P.med = P.med + 1 end
  C.zmax = zmax
  -- sizes: the target area spread over the programme (a hub about twice the mean room, galleries and small rooms
  -- fixed, traced rooms filling corners)
  local nwing = 0
  for _, k in ipairs(P.wings) do nwing = nwing + k end
  local fixed = P.gal * 260 + P.small * (kp and 300 or 85) + P.polys * 200 + #P.wings * 70 + P.annex * 150
  local slots = 2.0 * nhubs + ncore * 1.1 + P.med + nwing
  local mean = clamp((G.target_area * (kp and 0.9 or 1.0) - fixed) / slots, 220, kp and 1000 or 700)
  local hub = {}
  for i = 1, nhubs do
    local A = LA[hub_arch[i]]
    local ha = kp and 1600 or mean * rng.range(1.9, 2.3)
    -- (the hill at its archetype's full size: it stands well above every room round it)
    hub[i] = C:zone(hub_arch[i], { role = kp and "hill" or "hub", size = size_for(A, ha, kp and 1 or rng.range(0.85, 1.2), taken), y = 0,
      params = { depth = 4.5, split = rng.pick(SIDES) }, palette = { theme = themes[math.min(#themes, 3)], variant = i } })
    hub[i].kind = "hub"
    -- (its corners cut less or more: not every hub an octagon)
    if hub_arch[i] == "sunken" or hub_arch[i] == "atrium" then
      hub[i].want_chamfer = q2(math.min(hub[i].size[1], hub[i].size[2]) * rng.range(0.08, 0.2))
      finish(hub[i], step)
    end
  end
  local rooms = {}
  local function room(arch, kind, area, aspect, storey, o)
    o = o or {}
    local A = LA[arch]
    local z = C:zone(arch, { role = "room", size = size_for(A, area, aspect, taken), y = storey * step + (o.off or 0), params = { split = o.split or rng.pick(SIDES) },
      palette = { theme = themes[(storey % #themes) + 1], variant = 1 + (#rooms % 3) } })
    z.storey, z.kind = storey, kind
    style(z, kind, rng)
    finish(z, step)
    rooms[#rooms + 1] = z
    return z
  end
  -- the vertical core (see above)
  local core = room("gallery", "core", mean * rng.range(0.9, 1.3), rng.range(0.8, 1.25), 0)
  used.gallery = 1
  -- (DM M and L: one gallery is a long traced L round two sides of the mass, laid once the body stands)
  local perim = (not kp) and (sc == "M" or sc == "L") and P.gal > 0
  for k = 1, P.gal do
    if not (perim and k == 1) then
      local arch = "tunnel"
      used[arch] = (used[arch] or 0) + 1
      local a = rng.range(220, 300)
      room(arch, "gallery", a, 1 / rng.range(3.1, 4), 0)
    end
  end
  for _ = 1, P.small do
    if kp then room(pick_arch(rng, { "storage", "hall" }, used), "small", rng.range(220, 360), rng.range(0.7, 1.4), 0)
    else room("nook", "small", rng.range(64, 110), rng.range(0.8, 1.25), 0) end
  end
  -- medium rooms: sizes spread round the mean, about a third a storey up
  local weights = {}
  local nmed = P.med + nwing
  local w0, w1 = 0.55, 1.45
  if kp then w0, w1 = 0.5, 1.35 end
  for i = 1, nmed do weights[i] = w0 + (w1 - w0) * (i - 1) / math.max(1, nmed - 1) end
  rng.shuffle(weights)
  local pool = { "hall", "storage", "colonnade", "courtyard", "pit", "gallery", "hall", "storage" }
  local nup = (sc == "S" or sc == "XS") and 0 or math.max(1, math.floor(nmed * rng.range(0.25, 0.4) + 0.5))
  for i = 1, nmed do
    local arch = pick_arch(rng, pool, used)
    local storey = (i <= nup and arch ~= "gallery") and 1 or 0
    local aspect = rng.chance(0.5) and rng.range(1.2, 1.9) or rng.range(0.55, 0.85)
    room(arch, "medium", mean * weights[i], aspect, storey, { off = (rng.chance(0.35)) and 0.5 or 0 })
  end
  -- (KOTH: the programme sized from the area budget up front: the ring rooms grow, as far as their archetypes allow,
  -- until the plan's sum (traced corner rooms and hinge rooms at their target sizes) reaches the target area)
  if kp then
    local tpoly = clamp(mean * 0.45, 200, 300)
    local function planned()
      local a = L.zone_area(hub[1]) + P.polys * tpoly + #P.wings * 70
      for _, z in ipairs(rooms) do a = a + L.zone_area(z) end
      return a
    end
    for _ = 1, 4 do
      local short = math.max(G.target_area, G.budget.area[1] * 1.05) - planned()
      if short <= 0 then break end
      local grow = {}
      for _, z in ipairs(rooms) do
        local A = LA[z.archetype]
        if z.kind == "medium" and (z.size[1] < A.max[1] - EPS or z.size[2] < A.max[2] - EPS) then grow[#grow + 1] = z end
      end
      if #grow == 0 then break end
      -- (the big rooms grow first: the small ones stay small, the sizes keep their hierarchy)
      for i = 2, #grow do
        local c, j = grow[i], i - 1
        while j >= 1 and L.zone_area(grow[j]) < L.zone_area(c) do grow[j + 1] = grow[j] j = j - 1 end
        grow[j + 1] = c
      end
      for _, z in ipairs(grow) do
        if short > 0 then
          local a0 = L.zone_area(z)
          z.size = size_for(LA[z.archetype], a0 + short, z.size[1] / z.size[2], nil)
          finish(z, step)
          short = short - (L.zone_area(z) - a0)
        end
      end
    end
  end
  -- annexes: small rooms joined to a medium room on its floor (L and T plans), lower than it
  local annex_of = {}
  for _ = 1, P.annex do
    local hosts = {}
    for _, z in ipairs(rooms) do if z.kind == "medium" and not annex_of[z] and z.archetype ~= "courtyard" then hosts[#hosts + 1] = z end end
    if #hosts > 0 then
      local h = rng.pick(hosts)
      local arch = pick_arch(rng, { "nook", "storage" }, used)
      local a = room(arch, "annex", (arch == "nook") and rng.range(80, 140) or rng.range(170, 260), rng.range(0.6, 1.6), h.storey, { off = h.pos[2] - h.storey * step + (rng.chance(0.4) and 0.5 or 0) })
      if a.pos[2] - h.pos[2] > DATA.max_step then a.pos[2] = h.pos[2] finish(a, step) end
      annex_of[h] = a
      a.parent = h
    end
  end
  -- the wing rooms: storey 0 / 1 medium rooms from the smaller end
  local order = {}
  for _, z in ipairs(rooms) do if z.kind == "medium" then order[#order + 1] = z end end
  for i = 2, #order do
    local c, j = order[i], i - 1
    while j >= 1 and L.zone_area(order[j]) > L.zone_area(c) do order[j + 1] = order[j] j = j - 1 end
    order[j + 1] = c
  end
  local wing_rooms = {}
  for _, z in ipairs(order) do
    if #wing_rooms < nwing and not annex_of[z] then wing_rooms[#wing_rooms + 1] = z z.wing = true end
  end
  -- the rooms that will take traced outlines once the plan stands (canted corners, an L): sized for it, single-level
  -- archetypes on the hub's floor
  -- (KOTH keeps its big rooms; its tracing takes them as they are)
  local ntr = kp and 0 or (({ XS = 1, S = 2, M = 3, L = 4 })[sc] or 2)
  for _, z in ipairs(order) do
    if ntr > 0 and not z.wing and not annex_of[z] then
      ntr = ntr - 1
      z.trace = true
      -- (an archetype small enough for it)
      local A = LA[z.archetype]
      if #A.levels ~= 1 or z.archetype == "courtyard" or A.min[1] * A.min[2] > 330 then
        used[z.archetype] = math.max(0, (used[z.archetype] or 1) - 1)
        z.archetype = pick_arch(rng, { "hall", "storage", "colonnade" }, used)
        z.label = nil
        style(z, "medium", rng)
      end
      z.size = size_for(LA[z.archetype], clamp(L.zone_area(z), 260, 400), z.size[1] / z.size[2], taken)
      finish(z, step)
    end
  end
  -- growth of the body: the hub, the core, then the rest by storey and size (big first); an annex comes right
  -- after its host
  C:add(hub[1], C:rect_info(hub[1], 0, 0, 0))
  local placed = { hub[1] }
  -- the core: the two-storey hall drops from its gallery ring into the hub's court through the wall they share,
  -- and opens into the hub by a doorway on its floor
  if not C:attach(core, 0, { hub[1] }, { lifts = lifts, budget = 900, styles = { drop = true }, only_zlevel = step, ear = false }) then return nil end
  placed[#placed + 1] = core
  for _, c in ipairs(C:pair_links(core, hub[1], {})) do
    if c.link.type == "doorway" and C:try_add(c) then break end
  end
  local queue = {}
  for i = 2, nhubs do queue[#queue + 1] = hub[i] end
  local rest = {}
  for _, z in ipairs(rooms) do if z ~= core and not z.wing and z.kind ~= "annex" then rest[#rest + 1] = z end end
  for i = 2, #rest do
    local c, j = rest[i], i - 1
    local function key(z) return (z.storey or 0) * 10000 - L.zone_area(z) end
    while j >= 1 and key(rest[j]) > key(c) do rest[j + 1] = rest[j] j = j - 1 end
    rest[j + 1] = c
  end
  for _, z in ipairs(rest) do queue[#queue + 1] = z end
  local qi = 0
  while qi < #queue do
    qi = qi + 1
    local z = queue[qi]
    local ok = nil
    if kp and C:degree(hub[1]) < kp.approaches then
      -- KOTH: rooms meet the hill until it has its approaches, each where it meets a second zone too (no spoke with
      -- one way in)
      ok = C:attach(z, 0, { hub[1] }, { lifts = lifts, budget = 700, min_contacts = 1, styles = { abut = true, wide = true, drop = true, climb = true } })
      -- (one way in only while the hill lacks its fewest approaches: the loop closing gives it a second)
      if not ok and C:degree(hub[1]) < DATA.koth_approaches[1] then
        ok = C:attach(z, 0, { hub[1] }, { lifts = lifts, budget = 700, styles = { abut = true, wide = true, drop = true, climb = true } })
      end
    end
    if not ok then ok = C:attach(z, 0, placed, { lifts = lifts, budget = 420 }) end
    -- nothing at a storey it can reach yet: it comes down a storey; nowhere to go at its size: a smaller room
    local tries = 0
    while not ok and tries < 2 do
      tries = tries + 1
      local s0 = z.storey or 0
      if s0 > 0 then
        z.storey = s0 - 1
        z.pos[2] = z.storey * step + ((z.pos[2] % 1 > 0.25) and 0.5 or 0)
      else
        local A = LA[z.archetype]
        z.size = { clamp(even(z.size[1] * 0.8), A.min[1], A.max[1]), clamp(even(z.size[2] * 0.8), A.min[2], A.max[2]) }
      end
      finish(z, step)
      ok = C:attach(z, 0, placed, { lifts = lifts, budget = 600 })
    end
    if ok then
      placed[#placed + 1] = z
      -- (a room left with one way in takes a second now: a link to another zone it faces, else a second door into
      -- its neighbour)
      if C:degree(z) < 2 then
        local done = false
        for _, p in ipairs(placed) do
          if not done and p ~= z and p.role ~= "hill" and not C:linked(z, p) and C.info[p] and C.info[p].r and same_frame(C.info[p].f, 0) then
            for _, c in ipairs(C:pair_links(z, p, { lifts = lifts, max_gap = 14 })) do
              if C:try_add(c) then done = true break end
            end
          end
        end
        if not done then C:second_door(z, { lifts = lifts }) end
      end
      local a = annex_of[z]
      if a then
        a.pos[2] = z.pos[2] + ((a.pos[2] > z.pos[2]) and 0.5 or 0)
        finish(a, step)
        if C:attach(a, 0, { z }, { styles = { join = true }, budget = 300, ear = true }) then placed[#placed + 1] = a end
      end
    end
  end
  -- the perimeter gallery (a tunnel gallery when no corner takes it)
  if perim and #G.zones < zmax then
    local pg = C:perimeter_gallery({ y = 0, height = rng.pick({ 4.5, 5, 5.5 }), palette = { theme = themes[1], variant = 2 } })
    if pg then
      pg.kind = "gallery"
      placed[#placed + 1] = pg
    else
      used.tunnel = (used.tunnel or 0) + 1
      local z = room("tunnel", "gallery", rng.range(220, 300), 1 / rng.range(3.1, 4), 0)
      if C:attach(z, 0, placed, { lifts = lifts, budget = 500 }) then placed[#placed + 1] = z end
    end
  end
  -- wings
  local wi = 1
  for w = 1, #P.wings do
    local members = {}
    for k2 = 1, P.wings[w] do if wing_rooms[wi] then members[#members + 1] = wing_rooms[wi] end wi = wi + 1 end
    if #members > 0 and not C:grow_wing(members, placed, { lifts = lifts }) then
      -- no room for a wing: its rooms join the body instead
      for _, z in ipairs(members) do
        z.rot = nil
        if C:attach(z, 0, placed, { lifts = lifts }) then placed[#placed + 1] = z end
      end
    end
  end
  -- rooms toward the target area while the zone budget has room (one place kept for a bay)
  local guard = 0
  local aim = G.target_area * (kp and 1.1 or 1.0)
  while C:area() < aim and #G.zones < zmax - ((P.bays > 0) and 1 or 0) - math.max(1, P.polys - 1) and guard < 4 do
    guard = guard + 1
    local need = aim - C:area()
    local arch = pick_arch(rng, pool, used)
    if #LA[arch].levels > 1 then arch = pick_arch(rng, { "hall", "storage", "courtyard", "pit", "colonnade" }, used) end
    local z = room(arch, "medium", clamp(need, 250, mean * 1.4), rng.chance(0.5) and rng.range(1.2, 1.8) or rng.range(0.55, 0.85), 0)
    if not C:attach(z, 0, placed, { lifts = lifts, budget = 500 }) then
      local A = LA[z.archetype]
      z.size = { clamp(even(z.size[1] * 0.75), A.min[1], A.max[1]), clamp(even(z.size[2] * 0.75), A.min[2], A.max[2]) }
      finish(z, step)
      if not C:attach(z, 0, placed, { lifts = lifts, budget = 500 }) then break end
    end
    placed[#placed + 1] = z
  end
  -- traced rooms in the corners the mass leaves (one small)
  local np = P.polys
  if not kp then
    C:corner_rooms(1, { area = rng.range(70, 100), amin = 40, amax = 110, height = function() return rng.pick({ 4.5, 5 }) end })
    np = np - 1
  end
  local tgt = kp and clamp(mean * 0.45, 200, 300) or clamp(mean * 0.8, 200, 380)
  C:corner_rooms(np, { area = tgt, amin = 120, amax = 450, bulge = { 0.15, 0.45 } })
  -- (KOTH: rooms while the plan falls short of the area budget and the zone budget has room)
  if kp then
    local floor_ = math.max(G.budget.area[1] * 1.04, G.target_area * 0.9)
    local guard2 = 0
    while C:area() < floor_ and #G.zones < zmax - ((P.bays > 0) and 1 or 0) and guard2 < 3 do
      guard2 = guard2 + 1
      local arch = pick_arch(rng, { "hall", "storage", "courtyard", "pit", "colonnade" }, used)
      local z = room(arch, "medium", clamp(floor_ - C:area() + 150, 300, mean * 1.4), rng.chance(0.5) and rng.range(1.2, 1.8) or rng.range(0.55, 0.85), 0)
      if not C:attach(z, 0, placed, { lifts = lifts, budget = 500 }) then
        local A = LA[z.archetype]
        z.size = { clamp(even(z.size[1] * 0.75), A.min[1], A.max[1]), clamp(even(z.size[2] * 0.75), A.min[2], A.max[2]) }
        finish(z, step)
        if not C:attach(z, 0, placed, { lifts = lifts, budget = 500 }) then break end
      end
      placed[#placed + 1] = z
    end
    -- (and small side rooms while the zone budget has room and the hill is not yet 2.5 times the median room: the
    -- size hierarchy)
    local function median()
      local a = {}
      for _, z in ipairs(G.zones) do a[#a + 1] = L.zone_area(z) end
      for i = 2, #a do
        local c, j = a[i], i - 1
        while j >= 1 and a[j] > c do a[j + 1] = a[j] j = j - 1 end
        a[j + 1] = c
      end
      return a[math.floor((#a + 1) / 2)]
    end
    guard2 = 0
    while L.zone_area(hub[1]) < 2.6 * median() and #G.zones < zmax and guard2 < 3 do
      guard2 = guard2 + 1
      local z = room(pick_arch(rng, { "storage", "hall" }, used), "small", rng.range(180, 300), rng.range(0.7, 1.4), 0)
      local ok = C:attach(z, 0, placed, { lifts = lifts, budget = 500, min_contacts = 1 })
      if not ok then
        ok = C:attach(z, 0, placed, { lifts = lifts, budget = 400 })
        if ok and C:degree(z) < 2 then C:second_door(z, { lifts = lifts }) end
      end
      if not ok then break end
      placed[#placed + 1] = z
    end
  end
  -- the reward nook
  if not kp or rng.chance(0.4) then
    local nz_ = C:zone("nook", { role = "nook", size = size_for(LA.nook, rng.range(64, 100), 1, taken), y = 0,
      palette = { theme = themes[1], variant = 3 } })
    local hosts = {}
    for _, z in ipairs(G.zones) do if z.archetype ~= "poly" and z.role ~= "hub" and C.info[z].r then hosts[#hosts + 1] = z end end
    if #G.zones < zmax then
      for _, h in ipairs(hosts) do
        nz_.pos[2] = h.pos[2]
        if C:attach(nz_, C.info[h].f, { h }, { styles = { abut = true } }) then break end
      end
    end
  end
  -- bays on halls
  local halls = {}
  for _, z in ipairs(G.zones) do
    if z.role ~= "nook" and z.archetype ~= "poly" and z.archetype ~= "nook" and z.archetype ~= "tunnel" and C.info[z].r then halls[#halls + 1] = z end
  end
  rng.shuffle(halls)
  local made = 0
  for _, h in ipairs(halls) do
    if made >= P.bays or #G.zones >= zmax then break end
    local sides = { "+x", "-x", "+z", "-z" }
    rng.shuffle(sides)
    for _, s in ipairs(sides) do
      if C:bay(h, s, {}) then made = made + 1 break end
    end
  end
  C.zmax = zmax
  return C
end

-- The loop closing and spawn groups shared by DM (and the indoor KOTH ring).
local function finish_dm(C, t, fo)
  fo = fo or {}
  local G = C.G
  local sc = G.size_class
  local lifts = (sc == "M" or sc == "L")
  local rules = fo.rules
  local cands = C:extra_candidates({ free = true, free_max = 18, lifts = lifts, skip = fo.skip })
  local d = C:close_loops(cands, { need_levels = 3, cap = 5, p_add = 0.9, p_prune = 0.2, rules = rules,
    required = function(l) return l.type == "join" or l.keep end })
  local dropped = C:route_free()
  if dropped > 0 then
    local more = C:extra_candidates({ free = false, lifts = lifts, skip = fo.skip })
    d = C:close_loops(more, { need_levels = 3, cap = 5, p_add = 0, p_prune = 0, rules = rules, required = function() return true end })
  end
  if C:deficit(rules, 3) > 0 then C:repair({ lifts = lifts, rooms = C.zmax, extra = fo.extra }) end
  if C:deficit(rules, 3) > 0 then C:rescue({ lifts = lifts }) end
  C:route_free(true)
  -- zones left with no way in go; zones left with one way in through a join (bays, annexes) become the reward nook
  -- (one per 1,500 m2) or go
  local nooks = 0
  for _, z in ipairs(G.zones) do if z.role == "nook" then nooks = nooks + 1 end end
  local list = {}
  for _, z in ipairs(G.zones) do list[#list + 1] = z end
  for _, z in ipairs(list) do
    if z.role ~= "hub" and z.role ~= "hill" and C:degree(z) == 0 then C:remove_zone(z)
    elseif z.role == "bay" or (z.role == "room" and z.kind == "annex") then
      if C:degree(z) >= 2 then z.role = "room"
      elseif nooks < math.max(1, math.floor(C:area() / DATA.nook_area)) then z.role = "nook" nooks = nooks + 1
      elseif C:degree(z) <= 1 then C:remove_zone(z) end
    end
  end
  -- traced outlines for some finished rooms, up to the class's count of traced primary rooms
  local have = 0
  for _, z in ipairs(G.zones) do
    local a = L.zone_area(z)
    if z.archetype == "poly" and a >= 120 and a <= 450 then have = have + 1 end
  end
  local want = fo.traced or (({ XS = 1, S = 2, M = 3, L = 4 })[sc] or 2)
  C:trace_rooms(want - have, { amax = fo.trace_max, filter = function(z) return z.kind == "medium" or (fo.trace_small and z.kind == "small") end })
  return d
end

-- Zone ids in order (z1, z2, ...) after zones were dropped; links and objectives follow.
local function renumber(G)
  -- (the composer's own bookkeeping leaves the zones)
  for _, z in ipairs(G.zones) do
    z.storey, z.centre, z.partner, z.partner_of, z.kind, z.wing, z.parent, z.trace = nil, nil, nil, nil, nil, nil, nil, nil
    z.want_chamfer, z.want_height = nil, nil
  end
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
  for _, z in ipairs(G.zones) do if z.mirror_of then z.mirror_of = map[z.mirror_of] or z.mirror_of end end
end

local function gen_dm(G, rng, t)
  if G.class == "open_air" then return false end
  local C = compose_dm(G, rng, t)
  if not C then return true end
  finish_dm(C, t)
  local B = G.budget
  local per = (B.per_spawn[1] + B.per_spawn[2]) / 2
  local total = clamp(math.floor(G.target_area / per + 0.5), 4, 40)
  L.spread_spawns(G, total, function(z) return z.role ~= "nook" end)
  renumber(G)
  return true
end

-- KOTH: the hill's approaches (5-8 walk links into it).
local function koth_rules(hill)
  return function(C, idx, adj)
    local i = idx[hill.id]
    local ap = i and #adj[i] or 0
    if ap < DATA.koth_approaches[1] then return 2 * (DATA.koth_approaches[1] - ap) end
    if ap > DATA.koth_approaches[2] then return 2 * (ap - DATA.koth_approaches[2]) end
    return 0
  end
end

-- KOTH: the hill (a court sunk 4.5 m) at the middle, a ring of rooms of different sizes grown round it (the
-- first ones meeting it directly), a wing at an angle, loops closed round the ring.
local function gen_koth(G, rng, t)
  if G.class == "open_air" then return false end
  local kp = { hill = "sunken", approaches = 8 }
  local C = compose_dm(G, rng, t, kp)
  if not C then return true end
  local hill = nil
  for _, z in ipairs(G.zones) do if z.role == "hill" then hill = z end end
  -- (the hill's approaches: links into it from ring rooms it does not meet yet)
  local function extra(C2)
    local n = 0
    while C2:degree(hill) < DATA.koth_approaches[1] do
      local ring = {}
      for _, z in ipairs(G.zones) do if z ~= hill and not C2:linked(z, hill) then ring[#ring + 1] = z end end
      if not C2:connect({ hill }, ring, { rooms = C2.zmax }) then break end
      n = n + 1
    end
    return n
  end
  finish_dm(C, t, { rules = koth_rules(hill), extra = extra, traced = 2, trace_max = 460, trace_small = true })
  local radius = math.max(6, math.min(hill.size[1], hill.size[2]) / 2 - 7)
  G.objectives[#G.objectives + 1] = { kind = "hill", zone = hill.id, radius = radius }
  local per = (G.budget.per_spawn[1] + G.budget.per_spawn[2]) / 2
  local total = clamp(math.floor(G.target_area / per + 0.5), 6, 24)
  local list = {}
  for _, z in ipairs(G.zones) do if z.role ~= "hill" and z.role ~= "nook" then list[#list + 1] = z end end
  local each = math.max(1, math.floor(total / #list))
  for _, z in ipairs(list) do
    G.objectives[#G.objectives + 1] = { kind = "spawns", id = "s" .. (#G.objectives + 1), zone = z.id, count = each }
  end
  renumber(G)
  return true
end

-- ---------------------------------------------------------------------------------------------
-- Team modes: the red half composed, the blue half its mirror across x = 0
-- ---------------------------------------------------------------------------------------------

local MIRROR_SIDE = { ["+x"] = "-x", ["-x"] = "+x", ["+z"] = "+z", ["-z"] = "-z" }

-- Books on a centre zone (on the axis) the span the blue twin of a red link will take: the mirrored span on the
-- mirrored wall.
function Comp:book_mirror(cz, side, lo, hi, y0, y1, l)
  local ms = MIRROR_SIDE[side]
  local u = self:used_of(cz, ms)
  if side == "+x" or side == "-x" then u[#u + 1] = { lo = lo, hi = hi, y0 = y0, y1 = y1, link = l }
  else u[#u + 1] = { lo = -hi, hi = -lo, y0 = y0, y1 = y1, link = l } end
end

-- After a red link lands on a centre zone, its blue twin's span is booked too (C.mirror on).
function Comp:after_commit(l, za, zb)
  if not self.mirror then return end
  for _, z in ipairs({ za, zb }) do
    local other = (z == za) and zb or za
    if z.centre and not other.centre then
      for side, list in pairs(self.used[z] or {}) do
        for _, s2 in ipairs(list) do
          if s2.link == l and not s2.mirrored then
            self:book_mirror(z, side, s2.lo, s2.hi, s2.y0, s2.y1, l)
            local u = self:used_of(z, MIRROR_SIDE[side])
            u[#u].mirrored = true
          end
        end
      end
    end
  end
end

-- The blue twin of a red zone.
local function mirror_zone(z)
  local m = { archetype = z.archetype, role = z.role, lane = z.lane, team = "blue", segment = z.segment, params = {}, mirror_of = z.id,
    pos = { -z.pos[1], z.pos[2], z.pos[3] }, size = { z.size[1], z.size[2] }, levels = L.copy_list(z.levels), chamfer = z.chamfer,
    height = z.height, sky = z.sky, ground = z.ground, loops = z.loops, label = z.label }
  for k, v in pairs(z.params or {}) do m.params[k] = v end
  if m.params.split then m.params.split = MIRROR_SIDE[m.params.split] end
  if m.params.heading then m.params.heading = MIRROR_SIDE[m.params.heading] end
  if z.rot and norm_rot(z.rot) ~= 0 then m.rot = norm_rot(-z.rot) end
  if z.outline then
    m.outline = {}
    for i, q in ipairs(z.outline) do m.outline[i] = { -q[1], q[2] } end
  end
  local p = z.palette or {}
  m.palette = { theme = p.theme, variant = p.variant, team = "blue" }
  return m
end

-- The blue twin of a link (twin ids in tw; centre zones keep theirs).
local function mirror_link(l, za, zb, tw)
  local m = {}
  for k, v in pairs(l) do m[k] = v end
  m.a, m.b = tw[l.a] or l.a, tw[l.b] or l.b
  local function mside(z, s)
    if s == nil then return nil end
    if type(s) == "number" then
      -- (a traced zone on the axis is its own mirror: the twin opening is on the mirrored edge)
      if z.centre and z.outline then
        local o = z.outline
        local n = #o
        local a, b = o[s], o[s % n + 1]
        local function same(p, q) return math.abs(p[1] + q[1]) < 1e-6 and math.abs(p[2] - q[2]) < 1e-6 end
        for j = 1, n do
          local c, d = o[j], o[j % n + 1]
          if same(c, b) and same(d, a) then return j end
        end
      end
      return s
    end
    return MIRROR_SIDE[s]
  end
  m.a_side, m.b_side = mside(za, l.a_side), mside(zb, l.b_side)
  local function mp(q) if q == nil then return nil end return { -(q[1] or q.x), q[2] or q.z } end
  m.a_at, m.b_at = mp(l.a_at), mp(l.b_at)
  if l.path then m.path = {} for i, q in ipairs(l.path) do m.path[i] = mp(q) end end
  if l.offset and l.type ~= "join" then
    local f = L.link_frame(za, zb)
    if f and f.axis == "z" then m.offset = -l.offset end
  end
  return m
end

-- Mirrors the red half (zones with team red, their links and the links from them to the centre zones).
function Comp:mirror_half()
  local G = self.G
  local tw = {}
  local reds = {}
  for _, z in ipairs(G.zones) do if z.team == "red" then reds[#reds + 1] = z end end
  local byid = {}
  for _, z in ipairs(G.zones) do byid[z.id] = z end
  for _, z in ipairs(reds) do
    local m = mirror_zone(z)
    m.id = "b" .. z.id
    tw[z.id] = m.id
    local info
    local iz = self.info[z]
    if iz.r then info = { f = norm_rot(-(iz.f or 0)), r = { -iz.r[3], iz.r[2], -iz.r[1], iz.r[4] }, c = { -iz.c[1], iz.c[2] } }
      info.wp = rect_poly(info.f, info.r)
    else info = self:poly_info(m.outline) end
    G.zones[#G.zones + 1] = m
    self.info[m] = info
    byid[m.id] = m
  end
  local n = #G.links
  for i = 1, n do
    local l = G.links[i]
    local za, zb = byid[l.a], byid[l.b]
    if za.team == "red" or zb.team == "red" then
      G.links[#G.links + 1] = mirror_link(l, za, zb, tw)
    end
  end
  return tw
end

-- CTF: three centre zones on the axis (the hub, a high crossing a storey up, a low flank a storey down; one of the
-- two narrow and deep, the other about the hub's width and shallow), the lane core of the red half laid out so every
-- lane link has its facing walls and its gap before anything else is grown, then a spawn room with two doors, a
-- turned gallery wing looped back onto the base, a flag alcove; loops closed, then mirrored for blue.
--
-- The lane core (v: metres toward the high side):
--               [ high lane: a long gallery ][ high ]
--   [  base  ][ mid lane ][        hub        ]
--         (a climb's run)
--         [ low flank lane ][   low   ]
-- The base (a two-storey gallery) abuts the mid lane room, its gallery ring opens into the high lane room through
-- the wall they share, and a stair or ramp runs down to the low lane room across the gap the climb needs.
local function compose_ctf(G, rng, t)
  local C = new_comp(G, rng, false)
  C.mirror = true
  local step = G.step
  local themes = G.themes
  local used, taken = {}, {}
  local neutral = themes[3] or themes[1]
  -- (the rooms scale with the target area: the floor per spawn stays in the class's range)
  local sc = clamp(G.target_area / 6200, 0.85, 1.12)
  -- the hub: its corners cut less or more
  local hub_arch = rng.pick({ "arena", "sunken", "atrium" })
  used[hub_arch] = 1
  local hub = C:zone(hub_arch, { role = "hub", size = size_for(LA[hub_arch], rng.range(950, 1200), rng.range(0.85, 1.0), taken), y = 0,
    params = { depth = 4.5 }, palette = { theme = neutral, variant = 2 } })
  if hub_arch ~= "arena" then
    hub.want_chamfer = q2(math.min(hub.size[1], hub.size[2]) * rng.range(0.08, 0.26))
    finish(hub, step)
  end
  hub.centre = true
  hub.lane = 1
  C:add(hub, C:rect_info(hub, 0, 0, 0))
  local dir = rng.chance(0.5) and 1 or -1
  -- The centre zones: straight above and below the hub on the axis, joined to it by the kinds asked for (the first
  -- whose gap fits). narrow: a deep zone well inside the hub's width, else one about its width and shallow.
  local function centre(arch_pool, storey, lane, role, side, narrow, kinds, area, avoid)
    local arch = pick_arch(rng, arch_pool, used, avoid and { [avoid] = true })
    local A = LA[arch]
    local hw = hub.size[1]
    local w, d
    if narrow then
      -- (at least `narrow` metres wide, so its lane room reaches it; an arena keeps the middle of its walls for its
      -- ramps, the openings go either side)
      w = math.max(even(hw * rng.range(0.58, 0.74)), even(narrow + 1))
      if hub.archetype == "arena" then w = math.max(w, 24) end
      w = clamp(w, A.min[1], A.max[1])
      d = clamp(even(area / w), A.min[2], A.max[2])
    else
      w = clamp(even(hw * rng.range(0.92, 1.06)), A.min[1], A.max[1])
      d = clamp(even(rng.range(12, 15)), A.min[2], A.max[2])
    end
    local z = C:zone(arch, { role = role, size = { w, d }, y = storey * step + ((rng.chance(0.3)) and 0.5 or 0),
      params = { split = rng.pick({ "+z", "-z" }) }, palette = { theme = neutral, variant = (lane == 0) and 1 or 3 } })
    taken[math.min(w, d) .. "x" .. math.max(w, d)] = true
    z.centre, z.lane = true, lane
    style(z, "centre", rng)
    finish(z, step)
    local hr = C.info[hub].r
    for _, k in ipairs(kinds) do
      local hi_z, lo_z = hub, z
      if z.pos[2] > hub.pos[2] then hi_z, lo_z = z, hub end
      local rise = hi_z.pos[2] - lo_z.pos[2]
      local gap = nil
      if k == "drop" then
        if rise >= 1.5 and rise <= DATA.drop_max and hi_z.pos[2] + DATA.corridor_height <= lo_z.pos[2] + lo_z.height - EPS then gap = 1 end
      elseif k == "stair" then gap = L.stair_run(rise) + 2 + q2(rng.range(0, 1))
      else gap = L.ramp_run(rise) + 2 + q2(rng.range(0, 1)) end
      if gap then
        local cz = (side > 0) and (hr[4] + gap + d / 2) or (hr[2] - gap - d / 2)
        local info = C:rect_info(z, 0, 0, cz)
        if C:fits(info) then
          C:put(z, info)
          local function mk()
            if k == "drop" then return link_of("drop", hi_z.pos[2], lo_z.pos[2], { width = 4, keep = true }), hi_z, lo_z end
            return link_of(k, hub.pos[2], z.pos[2], { width = 4, keep = true }), hub, z
          end
          local l, a, b = mk()
          local cs = C:classic_spans(l, a, b)
          if cs then
            C:add(z, info)
            if C:commit_classic(l, a, b, 0) then return z, k end
            -- the axis is taken (an arena's ramps): a pair of openings either side of it
            for _, sp in ipairs(cs) do
              local c = math.max(sp[1], L.port_width(l) / 2 + 0.8)
              if c <= sp[2] then
                local l2 = mk()
                if C:commit_classic(l, a, b, -c) and C:commit_classic(l2, a, b, c) then return z, k end
              end
            end
            C:remove_zone(z)
          end
        end
      end
    end
    return nil
  end
  -- the lane rooms (sized in the core below): a wide room on the mid lane, a long gallery on the high lane, a low
  -- flank room on the low lane; the base a two-storey gallery
  local function red(arch_pool, storey, lane, role, area, aspect, variant, avoid)
    local arch = pick_arch(rng, arch_pool, used, avoid)
    local A = LA[arch]
    local z = C:zone(arch, { role = role, team = "red", size = size_for(A, area, aspect or rng.range(0.7, 1.4), taken), y = storey * step + ((rng.chance(0.3)) and 0.5 or 0),
      params = { split = rng.pick(SIDES) }, palette = { theme = themes[(math.abs(storey) % #themes) + 1], variant = variant or (1 + lane), team = "red" } })
    z.lane = lane
    z.storey = storey
    style(z, (arch == "tunnel") and "gallery" or "medium", rng)
    finish(z, step)
    return z
  end
  local mid = red({ "hall", "colonnade" }, 0, 1, "room", 380 * sc, nil, nil, { [hub.archetype] = true })
  mid.pos[2] = 0
  local high = red({ "hall", "colonnade" }, 1, 0, "room", 330 * sc)
  high.want_chamfer = 0
  local low = red({ "storage", "hall" }, -1, 2, "flank", 300 * sc)
  low.want_chamfer = 0
  if low.archetype == "hall" then low.want_height = rng.pick({ 5, 5.5 }) end
  local MA, HA, LoA = LA[mid.archetype], LA[high.archetype], LA[low.archetype]
  local mw = math.max(MA.min[1], rng.pick({ 16, 18 }))
  -- (the storey pairs get two kinds of vertical link by construction: the base's gallery stairs and the hub's link
  -- to the high zone (a drop or a ramp) above the hub's floor; the hub's link to the low zone and the base's climb
  -- to the low lane room (two different kinds, or any two with a sunken hub's own stair and drop) below it)
  -- (a narrow centre zone keeps its lane room within the archetype's length: the room reaches from over the base's
  -- end, 7 m past the mid lane room, to the centre zone's wall)
  local narrow_high = rng.chance(0.7)
  local function narrow_min(A) return hub.size[1] + 2 * (mw + 1 + 7 - A.max[1]) + 1 end
  local hk = rng.chance(0.5) and { "drop", "ramp" } or { "ramp", "drop" }
  local ch, k_hi = centre({ "courtyard", "storage", "colonnade", "hall" }, 1, 0, "room", dir, narrow_high and narrow_min(HA), hk, rng.range(300, 420), high.archetype)
  if not ch then return nil end
  high.pos[2] = ch.pos[2]
  finish(high, step)
  local lk = { "drop", "stair", "ramp" }
  rng.shuffle(lk)
  local cl, k_lo = centre({ "pit", "storage", "courtyard", "hall" }, -1, 2, "flank", -dir, (not narrow_high) and narrow_min(LoA), lk, rng.range(260, 360), low.archetype)
  if not cl then return nil end
  low.pos[2] = cl.pos[2]
  finish(low, step)
  -- (a lane room long enough to reach its centre zone's wall from 7 m over the base's end: a longer archetype when
  -- the one picked is too short)
  local hbx0 = C.info[hub].r[1]
  for _, e in ipairs({ { high, ch }, { low, cl } }) do
    local z, cz = e[1], e[2]
    local need = (C.info[cz].r[1] - 1) - (hbx0 - 2 - mw) + 7
    if LA[z.archetype].max[1] < need then
      for _, a in ipairs({ "hall", "colonnade" }) do
        if LA[z.archetype].max[1] < need and LA[a].max[1] >= need and (used[a] or 0) < DATA.repeat_max and a ~= cz.archetype then
          used[z.archetype] = math.max(0, (used[z.archetype] or 1) - 1)
          used[a] = (used[a] or 0) + 1
          z.archetype, z.label = a, nil
          if z == low then z.want_height = rng.pick({ 5, 5.5 }) end
          finish(z, step)
        end
      end
    end
  end
  HA, LoA = LA[high.archetype], LA[low.archetype]
  -- the red half: x <= -0.5 (its mirror keeps a wall's thickness from it)
  local function region(info) local r = info.r if r then return r[3] <= -0.5 + EPS end local x0, z0, x1 = L.bbox(info.wp) return x1 <= -0.5 + EPS end
  C.region = region
  local base = C:zone("gallery", { role = "base", team = "red", size = { 20, 20 }, y = 0, params = {}, palette = { theme = themes[1], variant = 2, team = "red" } })
  base.lane = 1
  used.gallery = (used.gallery or 0) + 1
  if rng.chance(0.5) then base.want_chamfer = 0 end
  -- the base's climb to the low lane room: a kind the low storey pair still lacks
  local k_base = rng.pick({ "stair", "ramp" })
  if hub_arch ~= "sunken" and k_lo ~= "drop" then k_base = (k_lo == "stair") and "ramp" or "stair" end
  -- (plan rectangles in v: dir * z)
  local function vrect(r) if dir > 0 then return { r[1], r[2], r[3], r[4] } end return { r[1], -r[4], r[3], -r[2] } end
  local function fits_arch(A, w, d) return w >= A.min[1] - EPS and w <= A.max[1] + EPS and d >= A.min[2] - EPS and d <= A.max[2] + EPS end
  -- a zone sized to the rectangle (x0, v0)-(x1, v1), when it keeps its clearances: its shape, or nil
  local function rect_at(z, x0, v0, x1, v1)
    if not fits_arch(LA[z.archetype], x1 - x0, v1 - v0) then return nil end
    z.size = { x1 - x0, v1 - v0 }
    finish(z, step)
    local info = C:rect_info(z, 0, (x0 + x1) / 2, dir * (v0 + v1) / 2)
    if not region(info) or not C:fits(info) then
      return nil
    end
    return info
  end
  -- a value in [a, b] (half metres), or nil
  local function within(a, b) if b < a - EPS then return nil end return q2(rng.range(a, math.max(a, b))) end
  local hb, chr, clr = vrect(C.info[hub].r), vrect(C.info[ch].r), vrect(C.info[cl].r)
  local rise_lo = base.pos[2] - low.pos[2]
  local G2 = ((k_base == "stair") and L.stair_run(rise_lo) or L.ramp_run(rise_lo)) + 2
  local aM = 380 * sc
  local function core()
    local placed = {}
    local function undo() for i = #placed, 1, -1 do C:remove_zone(placed[i]) end return false end
    local function put(z, info) C:add(z, info) placed[#placed + 1] = z end
    local function link(l, a, b) l.keep = true return C:commit_classic(l, a, b) end
    -- the mid lane room against the hub's red wall, slid along it
    -- (within the hub's depth: the centre zones beyond it may reach past its wall)
    local md = clamp(even(aM / mw), MA.min[2], math.min(MA.max[2], hb[4] - hb[2]))
    local mv0 = q2((hb[2] + hb[4]) / 2 - md / 2 + rng.range(-0.45, 0.45) * math.abs(hb[4] - hb[2] - md))
    local iM = rect_at(mid, hb[1] - 1 - mw, mv0, hb[1] - 1, mv0 + md)
    if not iM then return false end
    put(mid, iM)
    local hm = math.min(mid.height, hub.height)
    local lm = (hm >= 7 and rng.chance(0.5)) and link_of("doorway", 0, 0, { width = rng.pick({ 5, 6, 7 }), height = math.min(math.floor(hm - 1), 6) }) or
      link_of("doorway", 0, 0, { width = rng.pick({ 3, 4 }) })
    if not link(lm, hub, mid) then return undo() end
    local m = vrect(iM.r)
    local bx1 = m[1] - 1
    local hx1, lx1 = chr[1] - 1, clr[1] - 1
    -- arithmetic first (cheap): the walls the five lane links need, then the placements and the links themselves
    for _ = 1, 30 do
      local bw = even(rng.range(18, 24))
      local bx0 = bx1 - bw
      local bcx = (bx0 + bx1) / 2
      -- (flags 50-115 m apart)
      if bcx < -56 then bw = 18 bx0 = bx1 - bw bcx = (bx0 + bx1) / 2 end
      -- the low lane room's reach (from under the base's east end to the low zone's wall) and the climb's run
      local lx0 = bx1 - math.min(q2(rng.range(7, math.min(13, bw - 4))), LoA.max[1] - (lx1 - bx1))
      local gap = G2 + q2(rng.range(0, 1))
      local ltop = m[2] - 1
      if lx1 > hb[1] - 1 + EPS then ltop = math.min(ltop, hb[2] - 1) end
      -- the high lane room: from over the base's east end (7-13 m) to the high zone's wall, on the base's high side
      -- (the base at most 30 m deep down to the climb)
      local hx0 = bx1 - math.min(q2(rng.range(7, math.min(13, bw - 4))), HA.max[1] - (hx1 - bx1))
      local blo = m[4]
      if hx1 > hb[1] - 1 + EPS then blo = math.max(blo, hb[4]) end
      local bv1 = within(blo, math.min(chr[4] - 8, blo + 5, ltop + gap + 30))
      -- (deep enough to reach 7 m along the high zone's wall when that zone lies a climb's run away)
      local hd = math.max(even(rng.range(HA.min[2], HA.min[2] + 4)), even((chr[2] + 7) - ((bv1 or 0) + 1) + 1))
      local ok = bv1 ~= nil and bx1 - hx0 >= 7 - EPS and bx1 - lx0 >= 7 - EPS and bcx >= -56
      local hv0, hv1
      if ok then
        hv0 = bv1 + 1
        hv1 = math.max(hv0 + hd, chr[2] + 7)
        ok = fits_arch(HA, hx1 - hx0, hv1 - hv0)
      end
      -- the low lane room's far wall: the climb's run below the base (18-30 m deep), its depth reaching past the low
      -- zone's wall by 7 m
      local lv1, lod = nil, nil
      if ok then
        lv1 = within(math.max(clr[2] + 7, bv1 - gap - 30), math.min(ltop, m[4] - 8 - gap, bv1 - gap - 18))
        if lv1 then
          lod = math.max(even(rng.range(LoA.min[2], LoA.min[2] + 4)), even(lv1 - (clr[4] - 7) + 1))
          if lod > LoA.max[2] + EPS then lv1 = nil end
        end
      end
      if lv1 and fits_arch(LoA, lx1 - lx0, lod) then
        local lv0 = lv1 - lod
        local bv0 = lv1 + gap
        local iB = rect_at(base, bx0, bv0, bx1, bv1)
        if iB then
          put(base, iB)
          local iH = rect_at(high, hx0, hv0, hx1, hv1)
          if iH then
            put(high, iH)
            local iL = rect_at(low, lx0, lv0, lx1, lv1)
            if iL then
              put(low, iL)
              local hh = math.min(high.height, ch.pos[2] + ch.height - high.pos[2])
              if link(link_of("doorway", 0, 0, { width = rng.pick({ 3, 4 }) }), mid, base) and
                link(link_of("doorway", base.pos[2] + step, high.pos[2], { width = rng.pick({ 3, 4 }) }), base, high) and
                link(link_of("doorway", high.pos[2], ch.pos[2], (hh >= 7 and rng.chance(0.5)) and { width = rng.pick({ 5, 6 }), height = 6 } or { width = rng.pick({ 3, 4 }) }), high, ch) and
                link(link_of(k_base, base.pos[2], low.pos[2], { width = rng.pick({ 3.5, 4, 4.5 }) }), base, low) and
                link(link_of("doorway", low.pos[2], cl.pos[2], { width = rng.pick({ 3, 4 }) }), low, cl) then
                return true
              end
              -- (a link found no room on its walls: take the three back and try other proportions)
              for i = #placed, 2, -1 do C:remove_zone(placed[i]) placed[i] = nil end
            else
              C:remove_zone(high) placed[#placed] = nil
              C:remove_zone(base) placed[#placed] = nil
            end
          else
            C:remove_zone(base) placed[#placed] = nil
          end
        end
      end
    end
    return undo()
  end
  local done = false
  for _ = 1, 6 do
    if core() then done = true break end
    -- other proportions for the mid lane room
    aM = 380 * sc * rng.range(0.85, 1.15)
  end
  if not done then return nil end
  C.zmax = 11
  -- a spawn room with two doors: a traced room in an angle of the core, else a room placed where it meets two
  -- zones, else one with a second door into its neighbour
  local pool = { base, mid, high, low }
  local sp = C:corner_rooms(1, { area = rng.range(170, 260) * sc, amin = 120, amax = 340, region = region, pool = pool, role = "spawn" })[1]
  if sp then sp.team = "red" sp.lane = nil
  else
    sp = red({ "hall", "storage", "colonnade", "courtyard" }, 0, 1, "spawn", rng.range(200, 280) * sc)
    sp.lane = nil
    local ok = false
    for _, y in ipairs({ base.pos[2] + step, base.pos[2] }) do
      if not ok then
        sp.pos[2] = y
        finish(sp, step)
        ok = C:attach(sp, 0, pool, { region = region, min_contacts = 1, budget = 700, styles = { abut = true, wide = true } })
      end
    end
    if not ok then
      sp.pos[2] = 0
      finish(sp, step)
      if not C:attach(sp, 0, pool, { region = region, contact_w = 1.5, styles = { abut = true, wide = true, climb = true, drop = true } }) then return nil end
      if C:degree(sp) < 2 then C:second_door(sp, {}) end
    end
  end
  -- the wing: a long gallery turned off the red half (its blue twin turns the other way), hung on a hinge room off
  -- the base or a lane room and tied back to the half (a traced vestibule joined to the base makes it a compound
  -- room)
  local body = {}
  for _, z in ipairs(G.zones) do if z.team == "red" and z.role ~= "nook" then body[#body + 1] = z end end
  local gal = red({ "tunnel" }, 0, 1, "room", rng.range(240, 320) * sc, 1 / rng.range(3.1, 4))
  gal.want_chamfer = 0
  gal.lane = nil
  finish(gal, step)
  -- (a loop wing off the base's outer wall first, then off its other walls or the outer walls of the high and low
  -- lane rooms (a wing a storey up or down), then any wing the half has room for)
  local hosts = { { base, "-x" } }
  local vs = (dir > 0) and "+z" or "-z"
  local others = { { base, vs }, { base, OPP[vs] }, { high, vs }, { low, OPP[vs] } }
  rng.shuffle(others)
  for _, h in ipairs(others) do hosts[#hosts + 1] = h end
  local winged = false
  for i, h in ipairs(hosts) do
    if not winged then
      gal.pos[2] = h[1].pos[2]
      winged = C:loop_wing(h[1], h[2], gal, { region = region, tries = (i == 1) and 6 or 3 })
    end
  end
  if not winged then gal.pos[2] = 0 finish(gal, step) end
  if not winged and not C:grow_wing({ gal }, body, { region = region, max_zones = C.zmax, prefer = base }) then
    gal.rot = nil
    gal.id = nil
    if #G.zones < C.zmax then C:attach(gal, 0, body, { region = region, min_contacts = 1, styles = { abut = true, wide = true } }) end
  end
  -- the flag alcove: a traced bay on a free wall of the base (the base reads as a compound room), while the zone
  -- budget has room
  local sides = { "-x", "+z", "-z" }
  rng.shuffle(sides)
  for _, s in ipairs(sides) do
    if #G.zones >= C.zmax then break end
    local bz = C:bay(base, s, { role = "nook" })
    if bz then bz.team = "red" break end
  end
  return C, base, { hub, ch, cl }
end

-- The CTF rules on the red half: three node-disjoint routes from the base to three different centre zones (the
-- mirror makes them three lanes), base exits on two heights, a lane at another height or through a flank.
-- The node-disjoint routes (up to 3) from the base to the centre zones (a sink joined to each).
local function ctf_lanes(C, base, centres, idx, adj)
  local G = C.G
  local n = #G.zones
  if not idx then idx = L.index_zones(G) adj = L.adjacency(G, idx) end
  local adj2 = {}
  for i = 1, n do adj2[i] = {} for _, e in ipairs(adj[i]) do adj2[i][#adj2[i] + 1] = e end end
  adj2[n + 1] = {}
  for _, cz in ipairs(centres) do
    local i = idx[cz.id]
    if i then adj2[i][#adj2[i] + 1] = { n + 1 } adj2[n + 1][#adj2[n + 1] + 1] = { i } end
  end
  return L.disjoint_routes(n + 1, adj2, idx[base.id], n + 1, 3)
end

local function ctf_rules(base, centres)
  return function(C, idx, adj)
    local G = C.G
    local d = 0
    local r = ctf_lanes(C, base, centres, idx, adj)
    if r < 3 then d = d + 3 * (3 - r) end
    -- base exits on two heights
    local hs, nh, ex = {}, 0, 0
    for _, l in ipairs(G.links) do
      if L.WALK[l.type] and (l.a == base.id or l.b == base.id) then
        ex = ex + 1
        local za, zb = C:byid(l.a), C:byid(l.b)
        local ya, yb = L.link_ys(l, za, zb)
        local y = (l.a == base.id) and ya or yb
        local k = string.format("%.0f", y)
        if not hs[k] then hs[k] = true nh = nh + 1 end
      end
    end
    if ex < 3 then d = d + (3 - ex) end
    if nh < 2 then d = d + 1 end
    return d
  end
end

local function gen_ctf(G, rng, t)
  if G.class == "open_air" then return false end
  local C, base, centres = compose_ctf(G, rng, t)
  if not C then return true end
  -- loop closing on the red half (and the centre)
  local function skip(za, zb) return za.team == "blue" or zb.team == "blue" or (za.centre and zb.centre) end
  local rules = ctf_rules(base, centres)
  local cands = C:extra_candidates({ free = false, skip = skip, corridors = false })
  C:close_loops(cands, { need_levels = 3, cap = 4, p_add = 0.6, p_prune = 0.35, rules = rules,
    required = function(l) return l.type == "join" or l.keep end })
  C:route_free()
  -- (the third lane: a link from the base, or from a room next to it, to a centre zone or a lane room it does not
  -- meet yet)
  local function lanes(C2)
    local n, tries = 0, 0
    while ctf_lanes(C2, base, centres) < 3 and tries < 6 do
      local from = { base }
      for _, z in ipairs(G.zones) do if z.team == "red" and z ~= base and C2:linked(z, base) then from[#from + 1] = z end end
      local done = false
      for _, z in ipairs(from) do
        if not done and tries < 6 then
          local cs = {}
          for _, c in ipairs(G.zones) do
            if (c.centre or (c.team == "red" and c.lane and c ~= base and c ~= z and c.role ~= "spawn")) and not C2:linked(z, c) then cs[#cs + 1] = c end
          end
          if #cs > 0 then
            tries = tries + 1
            local before = ctf_lanes(C2, base, centres)
            if C2:connect({ z }, cs, { free_tries = 2, rooms = C2.zmax, region = C2.region }) then
              n = n + 1
              if ctf_lanes(C2, base, centres) > before then done = true end
            end
          end
        end
      end
      if not done then break end
    end
    return n
  end
  if C:deficit(rules, 3) > 0 then C:repair({ rooms = C.zmax, region = C.region, extra = lanes }) end
  if C:deficit(rules, 3) > 0 then C:rescue({}) end
  C:route_free(true)
  -- traced outlines for up to two rooms of the half, and for a centre zone mirrored cuts on its outer corners (the
  -- middle is no column of boxes)
  C:trace_rooms(2, { filter = function(z) return z.team == "red" and z.role ~= "base" end })
  for i, l in ipairs(G.links) do l.id = "l" .. i end
  local plan = L.resolve(G, nil, false)
  if #plan.errors == 0 then
    local cs = { centres[2], centres[3] }
    rng.shuffle(cs)
    for _, z in ipairs(cs) do
      local ok, plan2 = C:trace_room(z, plan, { symmetric = (z.pos[3] > 0) and "+z" or "-z" })
      if ok then plan = plan2 break end
    end
  end
  -- the blue half (the traced rooms made on the red side are red)
  for _, z in ipairs(G.zones) do if not z.centre and not z.team then z.team = "red" end end
  C:mirror_half()
  C:route_free(true)
  -- flags and spawns
  local base_b = nil
  for _, z in ipairs(G.zones) do if z.mirror_of == base.id then base_b = z end end
  G.objectives[#G.objectives + 1] = { kind = "flag", team = "red", zone = base.id }
  G.objectives[#G.objectives + 1] = { kind = "flag", team = "blue", zone = base_b.id }
  local per = (G.budget.per_spawn[1] + G.budget.per_spawn[2]) / 2
  local team_total = clamp(math.floor(G.target_area / per / 2 + 0.5), 4, 16)
  for _, team in ipairs({ "red", "blue" }) do
    local list = {}
    for _, z in ipairs(G.zones) do if z.team == team and (z.role == "spawn" or z.role == "base") then list[#list + 1] = z end end
    local each = math.floor(team_total / #list)
    local extra = team_total - each * #list
    for i, z in ipairs(list) do
      G.objectives[#G.objectives + 1] = { kind = "spawns", id = "s" .. (#G.objectives + 1), zone = z.id, team = team, count = each + ((i <= extra) and 1 or 0) }
    end
  end
  renumber(G)
  return true
end

L.generators.dm = gen_dm
L.generators.ctf = gen_ctf
L.generators.koth = gen_koth
L.compose = { new_comp = new_comp, Comp = Comp }

end

return install
