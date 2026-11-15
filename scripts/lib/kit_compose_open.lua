-- Glacier design kit: the open-air composer (installed into the kit by kit.lua after kit_compose.lua).
--
-- kit.layout{class = "open_air"} for DM, CTF and KOTH is composed here: floating platforms at free angles and
-- distances round a hub, joined by straight bridges, flying stairs and sloped ramps that leave their walls at an
-- angle. The module wraps the generators kit_compose.lua registers in I.layout.generators (other classes pass
-- through) and builds on its composition state (I.layout.compose: the books of used wall spans, link footprints,
-- classic links, mirroring and the rule deficit).
--
-- The method is construction, not search:
--   * a programme per gametype: platforms with roles, archetypes and sizes in a hierarchy (a hub well above the
--     mean, a big deck, long thin catwalks, small perches, a two-storey tower), floors on three storeys with
--     half-metre offsets within a storey, grouped into clusters;
--   * a cluster's anchor hangs off the hub (KOTH: the hill; CTF: the centre platforms on the axis) by one straight
--     link computed from numbers: a port on a hub wall, a heading up to 34 degrees off square, a length, and the
--     anchor turned so that one of its walls meets the link up to 34 degrees off square. The anchor's position and
--     yaw follow; a few seeded samples are tested for clearance (box tests first, polygons only when the boxes come
--     close) and the one nearest the cluster's bearing is kept;
--   * a cluster's other platforms share the anchor's yaw and stand beside it across a short classic bridge or
--     stair, so a cluster reads as one group turned against the next;
--   * loops: for every pair of platforms close enough, the walls that face each other are searched for a straight
--     bridge, stair or ramp that meets both walls within the angle a straight link takes and keeps clear of the
--     platforms and of the links on its storey; links are added until kit.check_layout's rules hold (degree,
--     articulation, cycle rank, two routes between key zones, two kinds of vertical link per storey pair, the
--     gametype rules) and a few more for richness;
--   * CTF composes the red half (x < 0) off the centre platforms on the axis and mirrors it for blue.
--
-- This module returns one function, install(kit, internals). Generation uses the seeded stream it is handed, so the
-- same seed always gives the same graph.

local function install(kit, I)
local L = I.layout
local DATA, LA = L.DATA, L.LA
local SIDES, OPP = L.SIDES, L.OPP
local clamp, even = L.clamp, L.even
local to_world, to_local, norm_rot, zone_rot = L.to_world, L.to_local, L.norm_rot, L.zone_rot
local EPS = I.EPS
local _ = kit

-- ---------------------------------------------------------------------------------------------
-- Constants and small helpers
-- ---------------------------------------------------------------------------------------------

local SIDE_YAW = { ["+z"] = 0, ["+x"] = 90, ["-z"] = 180, ["-x"] = 270 }
local MAX_TURN = 40                           -- a straight link meets each wall at most this far off square (the resolver takes 45)
local COS_MAX = math.cos(math.rad(MAX_TURN))
local ZONE_GAP = 2.5                          -- platforms stand this far apart (the rule is 2 m)
local LINK_GAP = 1.3                          -- links keep this far from platforms and from the links on their storey (1 m)
local LINK_H = DATA.corridor_height           -- an open-air link's clear height (its band on the storey)
-- The turns of a cluster's frame from the platform it hangs off (degrees, modulo 90 what the eye reads).
local TAUS = { 25, 30, 35, 40, 45, 50, 55, -25, -30, -35, -40, -45, -50, -55, 15, -15, 0 }
-- Platform archetypes by part in the programme.
local P_HUB = { "ring", "hangar" }
local P_BIG = { "plaza", "deck" }
local P_MED = { "isle", "garden", "mast", "outpost", "deck", "plaza" }
local P_SMALL = { "outpost", "mast", "garden", "isle" }

local function snapv(v, k) return math.floor(v * k + 0.5) / k end
local function dir_of(yaw) local r = math.rad(yaw) return math.sin(r), math.cos(r) end
local function signed(a) a = a % 360 if a > 180 then a = a - 360 end return a end
local function split_level(z) return z.archetype == "tower" or z.archetype == "terrace" end

local function rect_world(th, r)
  local out = {}
  local q = { { r[1], r[2] }, { r[3], r[2] }, { r[3], r[4] }, { r[1], r[4] } }
  for i = 1, 4 do local x, z = to_world(th, q[i][1], q[i][2]) out[i] = { x, z } end
  return out
end
local function box_of(p)
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, q in ipairs(p) do
    if q[1] < x0 then x0 = q[1] end
    if q[1] > x1 then x1 = q[1] end
    if q[2] < z0 then z0 = q[2] end
    if q[2] > z1 then z1 = q[2] end
  end
  return { x0, z0, x1, z1 }
end
-- A lower bound of the plan distance between two boxes (negative when they overlap).
local function box_gap(a, b) return math.max(b[1] - a[3], a[1] - b[3], b[2] - a[4], a[2] - b[4]) end

-- Insertion sort by key (descending): MoonSharp's table.sort with a comparator is slow.
local function sort_desc(list, key)
  for i = 2, #list do
    local c, j = list[i], i - 1
    while j >= 1 and list[j][key] < c[key] do list[j + 1] = list[j] j = j - 1 end
    list[j + 1] = c
  end
  return list
end

-- An even size for an archetype near an area and aspect (w / d), clamped to the archetype; distinct from the
-- sizes already used.
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

-- A hub platform's size.
local function hub_size(A, area, aspect, taken) return size_for(A, area, aspect, taken) end

local function pick_arch(rng, pool, used)
  local cand = {}
  for _, a in ipairs(pool) do if (used[a] or 0) < DATA.repeat_max then cand[#cand + 1] = a end end
  if #cand == 0 then cand = pool end
  local a = rng.pick(cand)
  used[a] = (used[a] or 0) + 1
  return a
end

-- ---------------------------------------------------------------------------------------------
-- The composition state: kit_compose's books, plus each platform's world outline and every link's footprint
-- ---------------------------------------------------------------------------------------------

-- The kinds of vertical link joining each pair of floor levels, exactly as kit.check_layout counts them (levels
-- merged within 2 m; split-level platforms count a stair and a ramp, other multi-level zones a stair).
local function open_kinds(self)
  local G = self.G
  local key = #G.zones .. "/" .. #G.links .. "/" .. (self.version or 0)
  if self.kt_key == key then return self.kt_val, self.kt_lv end
  local levels = L.floor_levels(G)
  local function lv(y)
    for _, x in ipairs(levels) do if math.abs(x - y) < 2 then return x end end
    return y
  end
  local out = {}
  local function add(a, b, k)
    local kk = self:pair_key(a, b)
    out[kk] = out[kk] or {}
    out[kk][k] = true
  end
  local ids = {}
  for _, z in ipairs(G.zones) do ids[z.id] = z end
  for _, l in ipairs(G.links) do
    local za, zb = ids[l.a], ids[l.b]
    if za and zb and L.WALK[l.type] then
      local ya, yb = L.link_ys(l, za, zb)
      if math.abs(ya - yb) > 0.75 then add(lv(math.min(ya, yb)), lv(math.max(ya, yb)), l.type) end
    end
  end
  for _, z in ipairs(G.zones) do
    local A = LA[z.archetype]
    local zl = {}
    for _, off in ipairs(z.levels) do zl[#zl + 1] = z.pos[2] + off end
    if A.extra_levels then for _, off in ipairs(A.extra_levels(z)) do zl[#zl + 1] = z.pos[2] + off end end
    for i = 1, #zl do
      for j = i + 1, #zl do
        local a, b = lv(math.min(zl[i], zl[j])), lv(math.max(zl[i], zl[j]))
        if b - a > 0.75 then
          if split_level(z) or z.archetype == "split" or z.archetype == "mound" then add(a, b, "stair") add(a, b, "ramp")
          elseif z.archetype == "sunken" then add(a, b, "stair") add(a, b, "drop")
          else add(a, b, "stair") end
        end
      end
    end
  end
  self.kt_key, self.kt_val, self.kt_lv = key, out, lv
  return out, lv
end

local function new_open(G, rng)
  local C = L.compose.new_comp(G, rng, true)
  C.geo, C.lf, C.nid = {}, {}, 0
  C.kind_table = open_kinds
  return C
end

-- Adds a zone at a placed shape (info from rect_info) and records its world outline.
local function place(C, z, info)
  C.nid = C.nid + 1
  z.id = z.id or ("o" .. C.nid)
  C:add(z, info)
  local p = rect_world(info.f, info.r)
  C.geo[z] = { poly = p, box = box_of(p) }
end

-- A lower bound of the plan distance between two rectangles (4 points each, any yaw): the largest gap between
-- their projections on the four edge normals (negative when they overlap).
local function sat_gap(P, Q)
  local best = -math.huge
  for k = 1, 4 do
    local R = (k <= 2) and P or Q
    local i = (k % 2 == 1) and 1 or 2
    local ex, ez = R[i + 1][1] - R[i][1], R[i + 1][2] - R[i][2]
    local el = math.sqrt(ex * ex + ez * ez)
    if el > 1e-9 then
      local nx, nz = -ez / el, ex / el
      local p0, p1, q0, q1 = math.huge, -math.huge, math.huge, -math.huge
      for j = 1, 4 do
        local v = P[j][1] * nx + P[j][2] * nz
        if v < p0 then p0 = v end
        if v > p1 then p1 = v end
        local w = Q[j][1] * nx + Q[j][2] * nz
        if w < q0 then q0 = w end
        if w > q1 then q1 = w end
      end
      local g = math.max(q0 - p1, p0 - q1)
      if g > best then best = g end
    end
  end
  return best
end

-- True when rectangles P and Q keep m apart (box and projection tests first; the exact distance only when those
-- cannot tell).
local function apart(P, bp, Q, bq, m)
  if box_gap(bp, bq) >= m then return true end
  local g = sat_gap(P, Q)
  if g >= m then return true end
  if g < 0 then return false end
  return L.poly_dist(P, Q, m + 1) >= m
end

-- True when a plan rectangle keeps m from every placed zone (but skip_a and skip_b).
local function zones_clear(C, poly, box, m, skip_a, skip_b)
  for _, p in ipairs(C.G.zones) do
    if p ~= skip_a and p ~= skip_b then
      local g = C.geo[p]
      if g and not apart(poly, box, g.poly, g.box, m) then return false end
    end
  end
  return true
end

-- True when a plan rectangle keeps m from every link footprint whose band meets band (all links when band is nil).
local function links_clear(C, poly, box, band, m)
  for _, f in ipairs(C.lf) do
    if (band == nil or (band[1] < f.band[2] + m and band[2] > f.band[1] - m)) and not apart(poly, box, f.poly, f.box, m) then return false end
  end
  return true
end

-- Team modes: the red half keeps to x < 0 with room for its mirror.
local function in_region(C, box, link)
  local lim = nil
  if link then lim = C.xmax_link else lim = C.xmax_zone end
  return lim == nil or box[3] <= lim
end

-- ---------------------------------------------------------------------------------------------
-- Ports and straight links
-- ---------------------------------------------------------------------------------------------

local function port_pt(z, side, c)
  local lp = L.wall_point(z, side, c)
  return to_world(zone_rot(z), lp[1], lp[2])
end
local function normal(z, side) return dir_of(zone_rot(z) + SIDE_YAW[side]) end

-- The opening spans of z's wall at port level off for a link on band (its archetype's ranges less the spans of
-- the links already on that wall); a centre zone of a team mode keeps its blue half for the mirror.
local function free_spans(C, z, side, off, band)
  local free = L.ranges_of(z, side, off)
  if C.mirror and z.centre then
    if side == "+x" then return {} end
    if side == "+z" or side == "-z" then free = L.intersect(free, { { -1e9, -0.8 } }) end
  end
  for _, s in ipairs(C:used_of(z, side)) do
    if s.y0 < band[2] + 0.5 and s.y1 > band[1] - 0.5 then free = L.subtract(free, s.lo - 1.6, s.hi + 1.6) end
  end
  return free
end

-- The opening spans of an unplaced zone's wall, relative to its centre.
local function rel_spans(z, side, off)
  local p1, p3, rot = z.pos[1], z.pos[3], z.rot
  z.pos[1], z.pos[3], z.rot = 0, 0, nil
  local r = L.ranges_of(z, side, off)
  z.pos[1], z.pos[3], z.rot = p1, p3, rot
  return r
end

local function fits(spans, c, half)
  for _, s in ipairs(spans) do if c - half >= s[1] - EPS and c + half <= s[2] + EPS then return true end end
  return false
end

-- A port centre (on the 1/16 m grid) in spans with half a width of room either side, or nil.
local function sample_c(rng, spans, half)
  local ok, tot = {}, 0
  for _, s in ipairs(spans) do
    local a, b = math.ceil((s[1] + half) * 16) / 16, math.floor((s[2] - half) * 16) / 16
    if b >= a then ok[#ok + 1] = { a, b } tot = tot + (b - a) + 0.5 end
  end
  if #ok == 0 then return nil end
  local r = rng.float() * tot
  for _, s in ipairs(ok) do
    local w = (s[2] - s[1]) + 0.5
    if r <= w then return clamp(snapv(s[1] + (s[2] - s[1]) * rng.float(), 16), s[1], s[2]) end
    r = r - w
  end
  return ok[#ok][1]
end

-- Evenly spread port centres in spans (a few per span).
local function spread_c(spans, half)
  local out = {}
  for _, s in ipairs(spans) do
    local a, b = math.ceil((s[1] + half) * 16) / 16, math.floor((s[2] - half) * 16) / 16
    if b >= a then
      if b - a < 1.5 then out[#out + 1] = clamp(snapv((a + b) / 2, 16), a, b)
      else for _, f in ipairs({ 0, 0.25, 0.5, 0.75, 1 }) do out[#out + 1] = clamp(snapv(a + (b - a) * f, 16), a, b) end end
    end
  end
  return out
end

-- A straight link between two world ports with outward normals: its heading, length and the cosines of the
-- angles at both walls; nil when it meets a wall more than MAX_TURN off square.
local function straight_pts(ax, az, nax, naz, bx, bz, nbx, nbz, loose)
  local dx, dz = bx - ax, bz - az
  local len = math.sqrt(dx * dx + dz * dz)
  if len < 1 then return nil end
  dx, dz = dx / len, dz / len
  local cA = dx * nax + dz * naz
  local cB = -(dx * nbx + dz * nbz)
  if not loose and (cA < COS_MAX or cB < COS_MAX) then return nil end
  return { ax = ax, az = az, bx = bx, bz = bz, dx = dx, dz = dz, len = len, cA = cA, cB = cB }
end
local function straight(za, sa, ca, zb, sb, cb, loose)
  local ax, az = port_pt(za, sa, ca)
  local bx, bz = port_pt(zb, sb, cb)
  local nax, naz = normal(za, sa)
  local nbx, nbz = normal(zb, sb)
  return straight_pts(ax, az, nax, naz, bx, bz, nbx, nbz, loose)
end
local function quad_of(g, hw)
  local px, pz = -g.dz * hw, g.dx * hw
  return { { g.ax - px, g.az - pz }, { g.bx - px, g.bz - pz }, { g.bx + px, g.bz + pz }, { g.ax + px, g.az + pz } }
end

-- The span (min, max) a straight link of type t over a rise needs: a level bridge 5-28 m, a sloped bridge or a
-- ramp at most about 25 degrees, a flying stair whose flight fits the gap.
local function span_need(t, rise)
  if rise <= 1 then return 5, 28 end
  if t == "stair" then local r = L.stair_run(rise) return r + 1.5, r + 8 end
  local n = math.max(rise * 1.2 + 2, 2.1 * rise)
  if t == "ramp" then n = math.max(rise + 2, 2.1 * rise) end
  return n, n + 14
end

local function new_link(C, t, ya, yb)
  local rng = C.rng
  local l = { type = t, a_y = ya, b_y = yb }
  -- (sky bridges carry truss sides: a platform reads as its own space, the bridge as the way between)
  if t == "bridge" then l.width = rng.pick({ 3, 3.5, 4 }) l.style = "truss"
  else l.width = rng.pick({ 3.5, 4 }) end
  return l
end

-- The port level pairs between a placed zone N and zone z (z's floor set): {oa, ob, ya, yb, r}, rises up to a
-- storey and 2 m (a level link takes a step of up to 1 m).
local function level_pairs(C, N, z, o)
  local out = {}
  for _, oa in ipairs(N.levels) do
    if not (o and o.level) or math.abs(oa - o.level) < EPS then
      for _, ob in ipairs(z.levels) do
        if not (o and o.zlevel) or math.abs(ob - o.zlevel) < EPS then
          local ya, yb = N.pos[2] + oa, z.pos[2] + ob
          local r = math.abs(ya - yb)
          if r <= C.step + 2 - EPS and (r <= 1 + EPS or r >= 2) and not (o and o.flat and r > 1 + EPS) then
            out[#out + 1] = { oa = oa, ob = ob, ya = ya, yb = yb, r = r }
          end
        end
      end
    end
  end
  return out
end

-- The kind of a vertical link between floors ya and yb: one the storey pair still lacks first.
local function pick_kind(C, ya, yb, o)
  local kt, lv = C:kind_table()
  local have = kt[C:pair_key(lv(math.min(ya, yb)), lv(math.max(ya, yb)))] or {}
  local miss = {}
  for _, k in ipairs((o and o.kinds) or { "stair", "ramp", "bridge" }) do if not have[k] then miss[#miss + 1] = k end end
  if #miss > 0 then return C.rng.pick(miss) end
  return C.rng.pick((o and o.kinds) or { "stair", "stair", "bridge", "ramp" })
end

-- Footprints: a link's plan quad (in its own heading frame for kit_compose's books) and its band.
local function add_foot(C, l, za, zb, poly, band, f, r)
  C.lf[#C.lf + 1] = { l = l, poly = poly, box = box_of(poly), band = band }
  if f then C.foot[#C.foot + 1] = { f = f, r = r, wp = poly, link = l, za = za, zb = zb } end
end

-- Commits a straight free link from port (za, sa, ca) to (zb, sb, cb) with geometry g.
local function commit_free(C, l, za, sa, ca, zb, sb, cb, g)
  local hw = L.port_width(l) / 2
  l.a_side, l.b_side = sa, sb
  l.a_at, l.b_at = { g.ax, g.az }, { g.bx, g.bz }
  local ha, hb = hw / g.cA, hw / g.cB
  local res = {
    { zone = za, side = sa, lo = ca - ha, hi = ca + ha, y0 = l.a_y, y1 = l.a_y + LINK_H },
    { zone = zb, side = sb, lo = cb - hb, hi = cb + hb, y0 = l.b_y, y1 = l.b_y + LINK_H },
  }
  C:commit_free(l, za, zb, res)
  local q = quad_of(g, hw)
  local th = norm_rot(L.yaw_of(g.dx, g.dz))
  local r = { math.huge, math.huge, -math.huge, -math.huge }
  for _, p in ipairs(q) do
    local x, z = to_local(th, p[1], p[2])
    r = { math.min(r[1], x), math.min(r[2], z), math.max(r[3], x), math.max(r[4], z) }
  end
  add_foot(C, l, za, zb, q, { math.min(l.a_y, l.b_y), math.max(l.a_y, l.b_y) + LINK_H }, th, r)
end

-- Commits a classic link (kit_compose chooses its offset) and records its footprint.
local function commit_classic(C, l, za, zb, offset)
  local n = #C.foot
  if not C:commit_classic(l, za, zb, offset) then return false end
  local band = { math.min(l.a_y, l.b_y), math.max(l.a_y, l.b_y) + LINK_H }
  if #C.foot > n then
    local f = C.foot[#C.foot]
    C.lf[#C.lf + 1] = { l = l, poly = f.wp, box = box_of(f.wp), band = band }
  end
  return true
end

-- Takes a link out of the graph and out of every book.
local function unlink(C, l)
  C:unlink(l)
  local kept = {}
  for _, f in ipairs(C.lf) do if f.l ~= l then kept[#kept + 1] = f end end
  C.lf = kept
end

-- Takes a zone (and its links) out.
local function remove(C, z)
  local mine = {}
  for _, l in ipairs(C.G.links) do if l.a == z.id or l.b == z.id then mine[#mine + 1] = l end end
  for _, l in ipairs(mine) do unlink(C, l) end
  C:remove_zone(z)
  C.geo[z] = nil
end

local function degree(C, z)
  local n = 0
  for _, l in ipairs(C.G.links) do if L.WALK[l.type] and (l.a == z.id or l.b == z.id) then n = n + 1 end end
  return n
end

-- ---------------------------------------------------------------------------------------------
-- Links between placed platforms
-- ---------------------------------------------------------------------------------------------

-- The best link between two placed zones, or nil: a classic bridge, stair or ramp across facing walls of one
-- frame, else a straight free one between walls that face each other. o: kinds, maxlen, flat.
-- Returns {kind = "classic" | "free", a, b, l, ...}.
local function find_link(C, za, zb, o)
  o = o or {}
  local rng = C.rng
  local LP = level_pairs(C, za, zb, o)
  if #LP == 0 then return nil end
  -- classic: two rectangle zones of one frame, facing walls
  if L.same_rot(zone_rot(za), zone_rot(zb)) then
    local f = L.link_frame(za, zb)
    if f and f.gap <= (o.maxlen or 26) then
      for _, lp in ipairs(LP) do
        local kinds = { "bridge" }
        if lp.r > 1 then
          kinds = {}
          local miss = pick_kind(C, lp.ya, lp.yb, o)
          for _, k in ipairs({ miss, "stair", "ramp", "bridge" }) do
            local lo = (k == "stair") and (L.stair_run(lp.r) + 2) or ((k == "ramp") and (L.ramp_run(lp.r) + 2) or math.max(lp.r * 1.2 + 2, 2.1 * lp.r))
            if f.gap >= lo - EPS and f.gap <= lo + 8 then kinds[#kinds + 1] = k end
          end
        elseif f.gap < 4 then kinds = {}
        end
        for _, k in ipairs(kinds) do
          local l = new_link(C, k, lp.ya, lp.yb)
          if C:classic_spans(l, za, zb) then return { kind = "classic", a = za, b = zb, l = l, len = f.gap } end
        end
      end
    end
  end
  local cax, caz = L.centre_of(za)
  local cbx, cbz = L.centre_of(zb)
  local best = {}
  local tries = {}
  for _, lp in ipairs(LP) do
    if lp.r <= 1 then tries[#tries + 1] = { lp, "bridge" }
    else
      -- the kind the storey pair lacks first, then the others
      local k1 = pick_kind(C, lp.ya, lp.yb, o)
      tries[#tries + 1] = { lp, k1 }
      for _, k in ipairs((o and o.kinds) or { "stair", "ramp", "bridge" }) do if k ~= k1 then tries[#tries + 1] = { lp, k, 0.3 } end end
    end
  end
  for _, tr in ipairs(tries) do
    local lp, t, pen = tr[1], tr[2], tr[3] or 0
    local l = new_link(C, t, lp.ya, lp.yb)
    local hw = L.port_width(l) / 2
    local hmax = hw / COS_MAX
    local lo, hi = span_need(t, lp.r)
    if o.maxlen then hi = math.min(hi, o.maxlen) end
    local band_a, band_b = { lp.ya, lp.ya + LINK_H }, { lp.yb, lp.yb + LINK_H }
    for _, sa in ipairs(SIDES) do
      local nax, naz = normal(za, sa)
      if nax * (cbx - cax) + naz * (cbz - caz) > 0 then
        local spa = free_spans(C, za, sa, lp.oa, band_a)
        local pa = spread_c(spa, hmax)
        if #pa > 0 then
          local wa = {}
          for i, c in ipairs(pa) do local x, z = port_pt(za, sa, c) wa[i] = { x, z } end
          for _, sb in ipairs(SIDES) do
            local nbx, nbz = normal(zb, sb)
            if nbx * (cax - cbx) + nbz * (caz - cbz) > 0 and nax * nbx + naz * nbz < -0.3 then
              local spb = free_spans(C, zb, sb, lp.ob, band_b)
              local pb = spread_c(spb, hmax)
              for j, cb in ipairs(pb) do
                local bx, bz = port_pt(zb, sb, cb)
                for i, ca in ipairs(pa) do
                  local g = straight_pts(wa[i][1], wa[i][2], nax, naz, bx, bz, nbx, nbz)
                  if g and g.len >= lo - EPS and g.len <= hi + EPS and fits(spa, ca, hw / g.cA) and fits(spb, cb, hw / g.cB) then
                    local sc = -((1 - g.cA) + (1 - g.cB)) * 4 - g.len / 30 - pen + rng.float() * 0.05
                    local item = { score = sc, g = g, sa = sa, sb = sb, ca = ca, cb = cb, l = l, lp = lp, hw = hw }
                    -- keep the best six
                    if #best < 6 or sc > best[#best].score then
                      best[#best + 1] = item
                      sort_desc(best, "score")
                      if #best > 6 then best[#best] = nil end
                    end
                  end
                end
              end
            end
          end
        end
      end
    end
  end
  for _, c in ipairs(best) do
    local q = quad_of(c.g, c.hw)
    local qb = box_of(q)
    local band = { math.min(c.lp.ya, c.lp.yb), math.max(c.lp.ya, c.lp.yb) + LINK_H }
    if in_region(C, qb, true) and zones_clear(C, q, qb, LINK_GAP, za, zb) and links_clear(C, q, qb, band, LINK_GAP) then
      local l = {}
      for k, v in pairs(c.l) do l[k] = v end
      return { kind = "free", a = za, b = zb, l = l, sa = c.sa, sb = c.sb, ca = c.ca, cb = c.cb, g = c.g, q = q, qb = qb, band = band, len = c.g.len }
    end
  end
  return nil
end

-- True while a candidate still fits (other links may have taken its wall spans or its way since it was found).
local function still_ok(C, c)
  if c.kind == "classic" then return C:classic_spans(c.l, c.a, c.b) ~= nil end
  local hw = L.port_width(c.l) / 2
  if not fits(free_spans(C, c.a, c.sa, c.l.a_y - c.a.pos[2], { c.l.a_y, c.l.a_y + LINK_H }), c.ca, hw / c.g.cA) then return false end
  if not fits(free_spans(C, c.b, c.sb, c.l.b_y - c.b.pos[2], { c.l.b_y, c.l.b_y + LINK_H }), c.cb, hw / c.g.cB) then return false end
  return links_clear(C, c.q, c.qb, c.band, LINK_GAP)
end

-- Commits a candidate from find_link; returns the link made (a fresh copy each time) or nil.
local function commit_cand(C, c)
  local l = {}
  for k, v in pairs(c.l) do l[k] = v end
  l.a, l.b = c.a.id, c.b.id
  if c.kind == "classic" then
    if commit_classic(C, l, c.a, c.b) then return l end
    return nil
  end
  commit_free(C, l, c.a, c.sa, c.ca, c.b, c.sb, c.cb, c.g)
  return l
end

-- ---------------------------------------------------------------------------------------------
-- Growth: a platform hung off a placed one by a constructed straight link, or beside it in its frame
-- ---------------------------------------------------------------------------------------------

-- The score a placement adds for o.target (a world point z should stand near) and o.away (one it keeps from).
local function place_score(z, o)
  local s = 0
  if o.target then s = s - math.sqrt((z.pos[1] - o.target[1]) ^ 2 + (z.pos[3] - o.target[2]) ^ 2) / 8 end
  if o.away then s = s + math.sqrt((z.pos[1] - o.away[1]) ^ 2 + (z.pos[3] - o.away[2]) ^ 2) / 25 end
  if o.score then s = s + o.score(z) end
  return s
end

-- Commits the best placement of z (cands with score), then the links o.also (to every zone listed) and o.also_any
-- (to one of them) ask for; a placement whose further links do not fit is taken back and the next one tried (at
-- most o.trials). Returns z's first link or nil.
local function settle(C, z, N, cands, o, commit_one)
  if #cands == 0 then return nil end
  sort_desc(cands, "score")
  local tested = 0
  for _, c in ipairs(cands) do
    if tested >= (o.trials or 4) then break end
    tested = tested + 1
    z.id = nil
    local l = commit_one(c)
    local ok = l ~= nil
    for _, Y in ipairs(o.also or {}) do
      if ok then
        local k = find_link(C, z, Y, { maxlen = o.also_len })
        if k then commit_cand(C, k) else ok = false end
      end
    end
    if ok and o.also_any then
      local any = false
      for _, Y in ipairs(o.also_any) do
        if not any and Y ~= z and C.geo[Y] and not C:linked(z, Y) then
          local k = find_link(C, z, Y, { maxlen = o.also_len })
          if k then commit_cand(C, k) any = true end
        end
      end
      ok = any
    end
    if ok then return l end
    if C.geo[z] then remove(C, z) end
    z.id = nil
  end
  return nil
end

-- Hangs zone z (sized, its floor set, unplaced) off placed zone N by one straight link built from numbers: a port
-- on one of N's walls, a heading off that wall's normal, a length, and z turned so one of its walls meets the link
-- off square by a seeded angle (the frame turn tau). o: sides (a set of N's sides), bearing (the world bearing from
-- N's centre z should lie at), lens ({lo, hi} for a level bridge), rot (a fixed yaw for z), tau (turns to pick
-- from) or eps ({lo, hi}: z meets its link this far off square), kinds, level / zlevel (port levels), flat, tries,
-- enough (stop sampling with this many placements), target / away / score(z) (added to the score), also (zones z
-- must link to as well, all of them) and also_any (z must link to one of them): those are tried in a trial commit
-- of the best placements (at most trials). Returns the link or nil.
local function grow_free(C, z, N, o)
  o = o or {}
  local rng = C.rng
  local LP = level_pairs(C, N, z, o)
  if #LP == 0 then return nil end
  local thN = zone_rot(N)
  local cxN, czN = L.centre_of(N)
  local w, d = z.size[1], z.size[2]
  local cands = {}
  local sides = {}
  for _, s in ipairs(SIDES) do if not o.sides or o.sides[s] then sides[#sides + 1] = s end end
  for _ = 1, o.tries or 30 do
    local lp = LP[rng.int(1, #LP)]
    local t = (lp.r <= 1) and "bridge" or pick_kind(C, lp.ya, lp.yb, o)
    local l = new_link(C, t, lp.ya, lp.yb)
    local hw = L.port_width(l) / 2
    local hmax = hw / COS_MAX
    local lo, hi = span_need(t, lp.r)
    if lp.r <= 1 and o.lens then lo, hi = math.max(lo, o.lens[1]), math.max(math.max(lo, o.lens[1]), o.lens[2]) end
    -- N's side: one whose normal lies within 60 degrees of the bearing when one is asked for
    local pool = sides
    if o.bearing then
      pool = {}
      for _, s in ipairs(sides) do if L.ang_diff(thN + SIDE_YAW[s], o.bearing) <= 62 then pool[#pool + 1] = s end end
      if #pool == 0 then pool = sides end
    end
    local S = pool[rng.int(1, #pool)]
    local spans = free_spans(C, N, S, lp.oa, { lp.ya, lp.ya + LINK_H })
    local ca = sample_c(rng, spans, hmax)
    if ca then
      local ny = thN + SIDE_YAW[S]
      local dmax = MAX_TURN - 3
      local delta
      if o.bearing then delta = clamp(signed(o.bearing - ny) + rng.range(-10, 10), -dmax, dmax)
      else delta = rng.range(-dmax, dmax) end
      local phi = ny + delta
      local len = rng.range(lo, hi)
      local ax, az = port_pt(N, S, ca)
      local dx, dz = dir_of(phi)
      local bx, bz = ax + dx * len, az + dz * len
      -- z's port wall, its yaw, its port along that wall
      local Sp = SIDES[rng.int(1, 4)]
      if split_level(z) then z.params.split = (lp.ob == 0) and OPP[Sp] or Sp end
      local zsp = rel_spans(z, Sp, lp.ob)
      local th
      if o.rot ~= nil then th = norm_rot(o.rot)
      else
        local eps
        if o.eps then eps = rng.range(o.eps[1], o.eps[2])
        else eps = clamp((o.tau or TAUS)[rng.int(1, #(o.tau or TAUS))] - delta, -dmax, dmax) end
        th = norm_rot(snapv(phi + 180 + eps - SIDE_YAW[Sp], 0.2))
      end
      local u = sample_c(rng, zsp, hmax)
      if u then
        local blx, blz = to_local(th, bx, bz)
        local cx, cz
        if Sp == "+x" then cx, cz = blx - w / 2, blz - u
        elseif Sp == "-x" then cx, cz = blx + w / 2, blz - u
        elseif Sp == "+z" then cx, cz = blx - u, blz - d / 2
        else cx, cz = blx - u, blz + d / 2 end
        cx, cz = snapv(cx, 16), snapv(cz, 16)
        local info = C:rect_info(z, th, cx, cz)
        C:put(z, info)
        local cb = ((Sp == "+x" or Sp == "-x") and cz or cx) + u
        local g = straight(N, S, ca, z, Sp, cb)
        if g and g.len >= lo - EPS and fits(spans, ca, hw / g.cA) and fits(zsp, u, hw / g.cB) then
          local poly = rect_world(th, info.r)
          local box = box_of(poly)
          if in_region(C, box, false) and zones_clear(C, poly, box, ZONE_GAP) and links_clear(C, poly, box, nil, LINK_GAP) then
            local q = quad_of(g, hw)
            local qb = box_of(q)
            local band = { math.min(lp.ya, lp.yb), math.max(lp.ya, lp.yb) + LINK_H }
            if in_region(C, qb, true) and zones_clear(C, q, qb, LINK_GAP, N) and links_clear(C, q, qb, band, LINK_GAP) then
              local sc = rng.float() * 0.6 + place_score(z, o)
              if o.bearing then sc = sc - L.ang_diff(L.yaw_of(z.pos[1] - cxN, z.pos[3] - czN), o.bearing) / 20 end
              cands[#cands + 1] = { score = sc, info = info, l = l, S = S, Sp = Sp, ca = ca, cb = cb, g = g, split = z.params.split }
            end
          end
        end
        z.rot = nil
      end
    end
    if #cands >= (o.enough or 10) then break end
  end
  return settle(C, z, N, cands, o, function(c)
    z.params.split = c.split
    place(C, z, c.info)
    local l = c.l
    l.a, l.b = N.id, z.id
    commit_free(C, l, N, c.S, c.ca, z, c.Sp, c.cb, c.g)
    return l
  end)
end

-- Places zone z beside placed zone N in N's frame, across a gap a classic bridge, stair or ramp spans. o: sides (a
-- set of N's sides), away (a world point z should keep away from), tries, kinds, flat. Returns the link or nil.
local function grow_frame(C, z, N, o)
  o = o or {}
  local rng = C.rng
  local LP = level_pairs(C, N, z, o)
  if #LP == 0 then return nil end
  local th = zone_rot(N)
  local x0, z0, x1, z1 = L.rect_of(N)
  local A = LA[z.archetype]
  local orients = { { z.size[1], z.size[2] } }
  if z.size[1] ~= z.size[2] and z.size[2] >= A.min[1] and z.size[1] >= A.min[2] and z.size[2] <= A.max[1] and z.size[1] <= A.max[2] then
    orients[2] = { z.size[2], z.size[1] }
  end
  local sides = {}
  for _, s in ipairs(SIDES) do if not o.sides or o.sides[s] then sides[#sides + 1] = s end end
  local cands = {}
  for _ = 1, o.tries or 24 do
    local lp = LP[rng.int(1, #LP)]
    local t = "bridge"
    if lp.r > 1 then t = pick_kind(C, lp.ya, lp.yb, o) end
    local l = new_link(C, t, lp.ya, lp.yb)
    local gap
    if lp.r <= 1 then gap = rng.range(4.5, 9)
    elseif t == "stair" then gap = L.stair_run(lp.r) + 2 + rng.range(0, 2.5)
    elseif t == "ramp" then gap = L.ramp_run(lp.r) + 2 + rng.range(0, 2)
    else gap = math.max(lp.r * 1.2 + 2, 2.1 * lp.r) + rng.range(0, 2) end
    gap = math.ceil(gap * 2) / 2
    local S = sides[rng.int(1, #sides)]
    local orr = orients[rng.int(1, #orients)]
    z.size[1], z.size[2] = orr[1], orr[2]
    if split_level(z) then z.params.split = (lp.ob == 0) and S or OPP[S] end
    L.finish_zone(z, C.step)
    local wz, dz = z.size[1], z.size[2]
    local need = L.port_width(l) + 2 * math.max(z.chamfer or 0, N.chamfer or 0) + 3
    local alongN = (S == "+x" or S == "-x") and (z1 - z0) or (x1 - x0)
    local alongZ = (S == "+x" or S == "-x") and dz or wz
    local amid = (S == "+x" or S == "-x") and (z0 + z1) / 2 or (x0 + x1) / 2
    local tmax = (alongN + alongZ) / 2 - need
    if tmax >= 0 then
      local tt = math.floor(rng.range(-tmax, tmax) * 2 + 0.5) / 2
      local cx, cz
      if S == "+x" then cx, cz = x1 + gap + wz / 2, amid + tt
      elseif S == "-x" then cx, cz = x0 - gap - wz / 2, amid + tt
      elseif S == "+z" then cx, cz = amid + tt, z1 + gap + dz / 2
      else cx, cz = amid + tt, z0 - gap - dz / 2 end
      local info = C:rect_info(z, th, cx, cz)
      local poly = rect_world(th, info.r)
      local box = box_of(poly)
      if in_region(C, box, false) and zones_clear(C, poly, box, ZONE_GAP) and links_clear(C, poly, box, nil, LINK_GAP) then
        C:put(z, info)
        if C:classic_spans(l, N, z) then
          local sc = rng.float() * 0.6 + place_score(z, o)
          cands[#cands + 1] = { score = sc, info = info, l = l, size = { wz, dz }, split = z.params.split }
        end
        z.rot = nil
      end
    end
  end
  return settle(C, z, N, cands, o, function(c)
    z.size[1], z.size[2] = c.size[1], c.size[2]
    z.params.split = c.split
    L.finish_zone(z, C.step)
    place(C, z, c.info)
    c.l.a, c.l.b = N.id, z.id
    if not commit_classic(C, c.l, N, z) then return nil end
    return c.l
  end)
end

-- ---------------------------------------------------------------------------------------------
-- Loops: links between placed platforms until the rules hold
-- ---------------------------------------------------------------------------------------------

-- Candidate links between every pair of placed platforms close enough (skip(za, zb) leaves a pair out).
local function candidates(C, o)
  local out = {}
  local zs = C.G.zones
  for i = 1, #zs do
    for j = i + 1, #zs do
      local za, zb = zs[i], zs[j]
      local ga, gb = C.geo[za], C.geo[zb]
      if ga and gb and not (o.skip and o.skip(za, zb)) and not C:linked(za, zb) and box_gap(ga.box, gb.box) <= (o.reach or 28) then
        local c = find_link(C, za, zb, { maxlen = o.maxlen })
        if c then out[#out + 1] = c end
      end
    end
  end
  return out
end

-- Adds candidates until the rules hold (greedy on the shortfall: each round tries the most promising few), then a
-- few more for richness within degree caps. Returns the remaining shortfall.
local function close_loops(C, rules, o)
  o = o or {}
  local rng = C.rng
  local d = C:deficit(rules, 3)
  local cands = candidates(C, o)
  for _, c in ipairs(cands) do c.r = rng.float() end
  local guard = 0
  while d > 0 and guard < 24 do
    guard = guard + 1
    local short = {}
    for _, z in ipairs(C.G.zones) do if degree(C, z) < 2 then short[z] = true end end
    local trial = {}
    for _, c in ipairs(cands) do
      if not c.done then
        c.pri = c.r + ((short[c.a] or short[c.b]) and 10 or 0) + (o.prefer and o.prefer(c) or 0)
        trial[#trial + 1] = c
      end
    end
    sort_desc(trial, "pri")
    local best, bd = nil, d
    local tried = 0
    for _, c in ipairs(trial) do
      if tried >= 8 then break end
      if still_ok(C, c) then
        tried = tried + 1
        local l = commit_cand(C, c)
        if l then
          local d2 = C:deficit(rules, 3) - c.r * 0.01
          unlink(C, l)
          if d2 < bd - 1e-9 then best, bd = c, d2 end
        else c.done = true end
      else c.done = true end
    end
    if not best then break end
    best.done = true
    commit_cand(C, best)
    d = C:deficit(rules, 3)
  end
  -- richness: a few more of the rest, within degree caps, those that close the longest loops first (the pairs
  -- furthest apart along the links)
  local rest = {}
  for _, c in ipairs(cands) do if not c.done then rest[#rest + 1] = c end end
  if #rest > 0 then
    local idx = L.index_zones(C.G)
    local adj = L.adjacency(C.G, idx)
    for _, c in ipairs(rest) do
      local s, t = idx[c.a.id], idx[c.b.id]
      local dist, queue, head = { [s] = 0 }, { s }, 1
      while head <= #queue and dist[t] == nil do
        local u = queue[head]
        head = head + 1
        for _, e in ipairs(adj[u]) do if dist[e[1]] == nil then dist[e[1]] = dist[u] + 1 queue[#queue + 1] = e[1] end end
      end
      c.hops = (dist[t] or 99) + rng.float()
    end
    sort_desc(rest, "hops")
  end
  local cap = o.cap or 4
  local added = 0
  for _, c in ipairs(rest) do
    if added < (o.extra or 2) and rng.chance(o.p_add or 0.5) and degree(C, c.a) < cap and degree(C, c.b) < cap and
      not (o.extra_ok and not o.extra_ok(c)) and still_ok(C, c) then
      if commit_cand(C, c) then added = added + 1 end
    end
  end
  return C:deficit(rules, 3)
end

local bend -- (a platform between two others: defined with the programmes' helpers below)

-- Commits a link taken out before (its zones in place again): a free link at the ports it had, a classic one at
-- its offset.
local function recommit(C, l)
  local za, zb = C:byid(l.a), C:byid(l.b)
  if not (za and zb) then return false end
  if l.a_side then
    local function along(z, side, at)
      local lx, lz = to_local(zone_rot(z), at[1], at[2])
      return snapv(((side == "+x" or side == "-x") and lz or lx), 16)
    end
    local ca, cb = along(za, l.a_side, l.a_at), along(zb, l.b_side, l.b_at)
    local g = straight(za, l.a_side, ca, zb, l.b_side, cb, true)
    if not g then return false end
    commit_free(C, l, za, l.a_side, ca, zb, l.b_side, cb, g)
    return true
  end
  return commit_classic(C, l, za, zb, l.offset)
end

-- Gives every platform with fewer than two links another one: a link to a platform within reach, else the
-- platform moves to a bend between two platforms that stand close together (out from the hub), else it goes back
-- where it was. o: skip(za, zb) (pairs left out), hub. Returns how many are left short.
local function rescue(C, rules, o)
  o = o or {}
  local left = 0
  local zs = {}
  for _, z in ipairs(C.G.zones) do zs[#zs + 1] = z end
  for _, z in ipairs(zs) do
    if C.geo[z] and degree(C, z) < 2 and z.role ~= "hub" and z.role ~= "hill" and z.role ~= "base" and not z.centre then
      local best = nil
      for _, p in ipairs(C.G.zones) do
        if p ~= z and not C:linked(z, p) and not (o.skip and o.skip(z, p)) then
          local c = find_link(C, z, p, { maxlen = 32 })
          if c and (not best or c.len < best.len) then best = c end
        end
      end
      if best then commit_cand(C, best)
      elseif o.hub then
        -- move it: between two platforms that stand close together
        local info, y0, split0 = C.info[z], z.pos[2], z.params.split
        local keep = {}
        for _, l in ipairs(C.G.links) do if l.a == z.id or l.b == z.id then keep[#keep + 1] = l end end
        local id = z.id
        remove(C, z)
        local hx, hz = L.centre_of(o.hub)
        local cand = {}
        local list = C.G.zones
        for i = 1, #list do
          for j = i + 1, #list do
            local P, Q = list[i], list[j]
            if C.geo[P] and C.geo[Q] and not (o.skip and (o.skip(z, P) or o.skip(z, Q))) then
              local g = box_gap(C.geo[P].box, C.geo[Q].box)
              if g <= 30 then cand[#cand + 1] = { P = P, Q = Q, score = -g + C.rng.float() } end
            end
          end
        end
        sort_desc(cand, "score")
        local rz = math.sqrt(z.size[1] ^ 2 + z.size[2] ^ 2) / 2
        local moved = false
        for k = 1, math.min(6, #cand) do
          if not moved then
            local P, Q = cand[k].P, cand[k].Q
            local mx, mz = (P.pos[1] + Q.pos[1]) / 2, (P.pos[3] + Q.pos[3]) / 2
            local dx, dz = mx - hx, mz - hz
            local d = math.max(1, math.sqrt(dx * dx + dz * dz))
            z.pos[2] = ((P.storey == Q.storey) and (P.storey or 0) or 0) * C.step
            L.finish_zone(z, C.step)
            moved = bend(C, z, P, Q, { target = { hx + dx / d * (d + rz + 6), hz + dz / d * (d + rz + 6) }, tries = 30, jitter = 8 })
          end
        end
        if not moved then
          -- back where it was, with the links it had
          z.id = id
          z.pos[2], z.params.split = y0, split0
          L.finish_zone(z, C.step)
          place(C, z, info)
          for _, l in ipairs(keep) do recommit(C, l) end
          left = left + 1
        end
      else left = left + 1 end
    end
  end
  return left
end

-- Takes out platforms still left with one link (never a hub, hill, base or centre platform) while the zone budget
-- allows and the rules' shortfall does not grow: a dead end is worse than one platform less.
local function prune_dead_ends(C, rules)
  local B = C.G.budget
  local list = {}
  for _, z in ipairs(C.G.zones) do list[#list + 1] = z end
  for _, z in ipairs(list) do
    if C.geo[z] and degree(C, z) < 2 and z.role ~= "hub" and z.role ~= "hill" and z.role ~= "base" and not z.centre and
      #C.G.zones - ((C.mirror and z.team == "red") and 2 or 1) >= B.zones[1] then
      local d0 = C:deficit(rules, 3)
      local info, keep = C.info[z], {}
      for _, l in ipairs(C.G.links) do if l.a == z.id or l.b == z.id then keep[#keep + 1] = l end end
      remove(C, z)
      if C:deficit(rules, 3) > d0 - 1 then
        place(C, z, info)
        for _, l in ipairs(keep) do recommit(C, l) end
      end
    end
  end
end

-- Palettes: every platform but the hub (or hill) takes its own theme and variant while the combinations last, so
-- the platforms read apart from each other.
local function spread_palettes(C, keep)
  local combos = {}
  for _, th in ipairs(C.G.themes) do for v = 1, 3 do combos[#combos + 1] = { th, v } end end
  C.rng.shuffle(combos)
  local i = 0
  for _, z in ipairs(C.G.zones) do
    if z ~= keep then
      i = i % #combos + 1
      z.palette = { theme = combos[i][1], variant = combos[i][2] }
    end
  end
end

-- Zone ids in order (z1, z2, ...); links and objectives follow; the composer's own fields leave the graph.
local function renumber(G)
  for _, z in ipairs(G.zones) do z.storey, z.centre, z.cluster = nil, nil, nil end
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

-- ---------------------------------------------------------------------------------------------
-- Programmes
-- ---------------------------------------------------------------------------------------------

-- Floors: storey s at s * step, the first platform of a storey on the storey line, the others 0-1 m above it
-- (more floor-height classes, still one storey for the rules).
local function floor_y(C, storey, first)
  if first then return storey * C.step end
  return storey * C.step + C.rng.pick({ 0, 0.5, 0.5, 1 })
end

local function new_zone(C, arch, o)
  local z = C:zone(arch, o)
  z.storey = o.storey
  return z
end

-- Spoke storeys round the hub in bearing order: -1 and +1 present, the hub's storey between them.
local function storey_ring(rng, k)
  local base = ({ [1] = { 1 }, [2] = { 1, -1 }, [3] = { 1, 0, -1 }, [4] = { 1, 0, -1, 0 }, [5] = { 1, 0, -1, 0, 1 },
    [6] = { 1, 0, -1, -1, 0, 1 } })[k] or { 1, 0, -1, 0, 1, 0, -1 }
  local out = {}
  local sh = rng.int(0, k - 1)
  local rev = rng.chance(0.5)
  for i = 1, k do
    local j = ((i - 1 + sh) % k) + 1
    if rev then j = k - j + 1 end
    out[i] = base[j] or 0
  end
  return out
end

-- The storey of a platform between two others: theirs when they agree (or one toward the middle storey), either
-- when they differ by one, the middle when they differ by two.
local function between(rng, s1, s2)
  if s1 == s2 then
    if rng.chance(0.4) then return (s1 == 0) and rng.pick({ -1, 1 }) or 0 end
    return s1
  end
  if math.abs(s1 - s2) == 1 then return rng.pick({ s1, s2 }) end
  return 0
end

-- The side of placed zone A whose outward normal points most toward world point (x, z).
local function facing_side(A, x, z)
  local cx, cz = L.centre_of(A)
  local best, bv = nil, -math.huge
  for _, s in ipairs(SIDES) do
    local nx, nz = normal(A, s)
    local v = nx * (x - cx) + nz * (z - cz)
    if v > bv then best, bv = s, v end
  end
  return best
end

-- The port centre on (A, side) nearest the projection of world point (x, z), inside spans with half a width of
-- room (on the 1/16 m grid), or nil.
local function project_c(A, side, spans, half, x, z)
  local lx, lz = to_local(zone_rot(A), x, z)
  local c = (side == "+x" or side == "-x") and lz or lx
  local best, bd = nil, math.huge
  for _, s in ipairs(spans) do
    local a, b = math.ceil((s[1] + half) * 16) / 16, math.floor((s[2] - half) * 16) / 16
    if b >= a then
      local v = clamp(snapv(c, 16), a, b)
      if math.abs(v - c) < bd then best, bd = v, math.abs(v - c) end
    end
  end
  return best
end

-- Places zone z near world point o.target between placed zones A1 and A2, turned so that one of its walls faces
-- A1 and another faces A2, with a straight link to each (each meeting both its walls within the angle a straight
-- link takes). A split-level platform takes the two links on its low and high walls. o: target, jitter (m),
-- tries. Returns true when placed.
function bend(C, z, A1, A2, o)
  local rng = C.rng
  local split = split_level(z)
  local LP1 = level_pairs(C, A1, z, {})
  local LP2 = level_pairs(C, z, A2, {})
  if #LP1 == 0 or #LP2 == 0 then return false end
  local cands = {}
  local memo1, memo2 = {}, {}
  local jit = o.jitter or 6
  for _ = 1, o.tries or 40 do
    local lp1 = LP1[rng.int(1, #LP1)]
    local opts2 = {}
    for _, p in ipairs(LP2) do if (not split) or math.abs(p.oa - lp1.ob) > EPS then opts2[#opts2 + 1] = p end end
    local lp2 = (#opts2 > 0) and opts2[rng.int(1, #opts2)] or nil
    if lp2 then
      local t1 = (lp1.r <= 1) and "bridge" or pick_kind(C, lp1.ya, lp1.yb)
      local t2 = (lp2.r <= 1) and "bridge" or pick_kind(C, lp2.ya, lp2.yb)
      local l1, l2 = new_link(C, t1, lp1.ya, lp1.yb), new_link(C, t2, lp2.ya, lp2.yb)
      local hw1, hw2 = L.port_width(l1) / 2, L.port_width(l2) / 2
      local hm1, hm2 = hw1 / COS_MAX, hw2 / COS_MAX
      local lo1, hi1 = span_need(t1, lp1.r)
      local lo2, hi2 = span_need(t2, lp2.r)
      local mx, mz = o.target[1] + rng.range(-jit, jit), o.target[2] + rng.range(-jit, jit)
      local s1, s2 = facing_side(A1, mx, mz), facing_side(A2, mx, mz)
      local k1, k2 = s1 .. lp1.oa, s2 .. lp2.ob
      memo1[k1] = memo1[k1] or free_spans(C, A1, s1, lp1.oa, { lp1.ya, lp1.ya + LINK_H })
      memo2[k2] = memo2[k2] or free_spans(C, A2, s2, lp2.ob, { lp2.yb, lp2.yb + LINK_H })
      local sp1, sp2 = memo1[k1], memo2[k2]
      local p1c = project_c(A1, s1, sp1, hm1, mx, mz)
      local p2c = project_c(A2, s2, sp2, hm2, mx, mz)
      if p1c and p2c then
        local ax, az = port_pt(A1, s1, p1c)
        local bx, bz = port_pt(A2, s2, p2c)
        -- z's yaw: the pair of its walls that faces the two ports best (opposite walls on a split-level platform)
        local y1 = L.yaw_of(ax - mx, az - mz)
        local y2 = L.yaw_of(bx - mx, bz - mz)
        local best, bdev = nil, math.huge
        for _, w1 in ipairs(SIDES) do
          for _, w2 in ipairs(SIDES) do
            if w1 ~= w2 and ((not split) or w2 == OPP[w1]) then
              local th1, th2 = y1 - SIDE_YAW[w1], y2 - SIDE_YAW[w2]
              local dv = signed(th2 - th1)
              if math.abs(dv) < bdev then best, bdev = { w1, w2, th1 + dv / 2 }, math.abs(dv) end
            end
          end
        end
        -- o.tilt: turn the platform a seeded angle off the best fit (it reads turned against its neighbours)
        local tilt = 0
        if o.tilt then tilt = (rng.chance(0.5) and 1 or -1) * rng.range(o.tilt[1], o.tilt[2]) end
        if best and bdev / 2 + math.abs(tilt) <= MAX_TURN - 4 then
          local w1, w2 = best[1], best[2]
          local th = norm_rot(snapv(best[3] + tilt + rng.range(-6, 6), 0.2))
          if split then
            if math.abs(lp1.ob) < EPS then z.params.split = w2 else z.params.split = w1 end
          end
          local lx, lz = to_local(th, mx, mz)
          local info = C:rect_info(z, th, snapv(lx, 16), snapv(lz, 16))
          C:put(z, info)
          -- the links' geometry first (cheap), the clearance tests after
          local zs1 = free_spans(C, z, w1, lp1.ob, { lp1.yb, lp1.yb + LINK_H })
          local zs2 = free_spans(C, z, w2, lp2.oa, { lp2.ya, lp2.ya + LINK_H })
          local c1 = project_c(z, w1, zs1, hm1, ax, az)
          local c2 = project_c(z, w2, zs2, hm2, bx, bz)
          if c1 and c2 then
            -- the end ports slide toward the platform's ports (straighter links)
            local qx, qz = port_pt(z, w1, c1)
            p1c = project_c(A1, s1, sp1, hm1, qx, qz) or p1c
            local rx, rz = port_pt(z, w2, c2)
            p2c = project_c(A2, s2, sp2, hm2, rx, rz) or p2c
            local g1 = straight(A1, s1, p1c, z, w1, c1)
            local g2 = straight(z, w2, c2, A2, s2, p2c)
            if g1 and g2 and g1.len >= lo1 - EPS and g1.len <= hi1 + EPS and g2.len >= lo2 - EPS and g2.len <= hi2 + EPS and
              fits(sp1, p1c, hw1 / g1.cA) and fits(zs1, c1, hw1 / g1.cB) and fits(zs2, c2, hw2 / g2.cA) and fits(sp2, p2c, hw2 / g2.cB) then
              local poly = rect_world(th, info.r)
              local box = box_of(poly)
              local q1, q2 = quad_of(g1, hw1), quad_of(g2, hw2)
              local b1, b2 = box_of(q1), box_of(q2)
              local band1 = { math.min(lp1.ya, lp1.yb), math.max(lp1.ya, lp1.yb) + LINK_H }
              local band2 = { math.min(lp2.ya, lp2.yb), math.max(lp2.ya, lp2.yb) + LINK_H }
              local sep_ok = not (band1[1] < band2[2] + LINK_GAP and band1[2] > band2[1] - LINK_GAP) or apart(q1, b1, q2, b2, LINK_GAP)
              if sep_ok and in_region(C, box, false) and in_region(C, b1, true) and in_region(C, b2, true) and
                zones_clear(C, poly, box, ZONE_GAP) and links_clear(C, poly, box, nil, LINK_GAP) and
                zones_clear(C, q1, b1, LINK_GAP, A1) and zones_clear(C, q2, b2, LINK_GAP, A2) and
                links_clear(C, q1, b1, band1, LINK_GAP) and links_clear(C, q2, b2, band2, LINK_GAP) then
                local sc = rng.float() * 0.6 + place_score(z, o) - (4 - g1.cA - g1.cB - g2.cA - g2.cB) * 2
                cands[#cands + 1] = { score = sc, info = info, split = z.params.split, l1 = l1, l2 = l2, s1 = s1, s2 = s2, p1c = p1c, p2c = p2c,
                  w1 = w1, w2 = w2, c1 = c1, c2 = c2, g1 = g1, g2 = g2 }
              end
            end
          end
          z.rot = nil
        end
      end
    end
    if #cands >= (o.enough or 5) then break end
  end
  if #cands == 0 then return false end
  sort_desc(cands, "score")
  local c = cands[1]
  z.params.split = c.split
  z.id = nil
  place(C, z, c.info)
  c.l1.a, c.l1.b = A1.id, z.id
  commit_free(C, c.l1, A1, c.s1, c.p1c, z, c.w1, c.c1, c.g1)
  c.l2.a, c.l2.b = z.id, A2.id
  commit_free(C, c.l2, z, c.w2, c.c2, A2, c.s2, c.p2c, c.g2)
  return true
end

-- Fills the gap between two consecutive spokes A1 and A2 round the hub with platform z linked to both: a bend
-- out from the hub (z clear of it), then further out, then z beside A1 in its frame or hung off either spoke with a
-- second link to the other. Returns true when placed.
local function fill_gap(C, z, A1, A2, hub, tilt)
  local hx, hz = L.centre_of(hub)
  local mx, mz = (A1.pos[1] + A2.pos[1]) / 2, (A1.pos[3] + A2.pos[3]) / 2
  local dx, dz = mx - hx, mz - hz
  local d = math.max(1, math.sqrt(dx * dx + dz * dz))
  local rh = math.sqrt(hub.size[1] ^ 2 + hub.size[2] ^ 2) / 2
  local rz = math.sqrt(z.size[1] ^ 2 + z.size[2] ^ 2) / 2
  -- (far enough out that the loop round hub, spokes and filler encloses open sky, not a sliver)
  local r = math.max(d + 6, rh + rz + 7)
  for k, extra in ipairs({ 0, 8, 16, 24 }) do
    local target = { hx + dx / d * (r + extra), hz + dz / d * (r + extra) }
    if bend(C, z, A1, A2, { target = target, tries = (k == 1) and 40 or 28, jitter = 6 + extra / 2, tilt = tilt }) then return true end
  end
  -- no room for z: the two link directly when they can (the rim stays closed; z goes elsewhere)
  if not C:linked(A1, A2) then
    local c = find_link(C, A1, A2, { maxlen = 34 })
    if c then commit_cand(C, c) return false end
  end
  local target = { hx + dx / d * (r + 4), hz + dz / d * (r + 4) }
  local function toward(A) return L.yaw_of(target[1] - A.pos[1], target[2] - A.pos[3]) end
  if grow_free(C, z, A1, { target = target, bearing = toward(A1), also = { A2 }, also_len = 30, trials = 3, tries = 24 }) then return true end
  return false
end

-- Hangs platform z outward off rim platform P, linked to one of P's rim neighbours as well when it can be.
local function hang_out(C, z, P, nbrs, centre)
  local o = { away = centre, also_any = nbrs, also_len = 30, trials = 4, tries = 30 }
  if grow_free(C, z, P, o) then return true end
  o.also_any = nil
  o.trials = 1
  if grow_frame(C, z, P, o) then return true end
  return grow_free(C, z, P, o) ~= nil
end

-- A wheel round a hub: spokes (each hung off the hub at its bearing, on links of different lengths), rim
-- platforms filling the gaps between consecutive spokes (each linked to both), extras hung outward off the rim.
-- spokes / fillers / extras: unplaced zones with their floors set; o: lens (per spoke), spoke_opts(i) -> extra
-- options for spoke i. Returns the rim (placed zones in bearing order).
local function wheel(C, hub, spokes, fillers, extras, o)
  local rng = C.rng
  local k = #spokes
  local hx, hz = L.centre_of(hub)
  local b0 = rng.range(0, 360)
  local placed = {}
  for i, a in ipairs(spokes) do
    local bearing = b0 + (i - 1) * 360 / k + rng.range(-12, 12)
    -- (a spoke meets its link nearly square, so its side walls face along the rim toward its neighbours)
    local so = { bearing = bearing, lens = o.lens[(i - 1) % #o.lens + 1], tries = 32, eps = { -12, 12 } }
    if o.spoke_opts then for kk, v in pairs(o.spoke_opts(i, a)) do so[kk] = v end end
    local ok = grow_free(C, a, hub, so)
    if not ok then so.lens = { 5, 24 } so.tries = 48 ok = grow_free(C, a, hub, so) end
    if not ok and not so.must then
      for _, p in ipairs(placed) do if not ok then ok = grow_free(C, a, p, { tries = 24, away = { hx, hz } }) end end
    end
    if ok then placed[#placed + 1] = a end
  end
  -- the rim: a filler in each gap, in bearing order
  local rim = {}
  local fi = 1
  for i, a in ipairs(placed) do
    rim[#rim + 1] = a
    local b = placed[i % #placed + 1]
    local z = fillers[fi]
    if z and b ~= a then
      do
        local s = between(rng, a.storey or 0, b.storey or 0)
        if z.archetype == "tower" then s = math.min(a.storey or 0, b.storey or 0) end
        z.storey = s
        z.pos[2] = s * C.step + rng.pick({ 0, 0.5, 1 })
        L.finish_zone(z, C.step)
      end
      if fill_gap(C, z, a, b, hub) then rim[#rim + 1] = z fi = fi + 1 end
    end
  end
  -- fillers left over (gaps that took none) and extras: outward off the rim
  local rest = {}
  for j = fi, #fillers do rest[#rest + 1] = fillers[j] end
  for _, z in ipairs(extras) do rest[#rest + 1] = z end
  for j, z in ipairs(rest) do
    if #rim == 0 then break end
    local ri = ((j - 1) * 3) % #rim + 1
    local P = rim[ri]
    local Q = rim[(ri % #rim) + 1]
    local nbrs = { Q, rim[((ri - 2) % #rim) + 1] }
    do
      local s = (Q ~= P) and between(rng, P.storey or 0, Q.storey or 0) or (P.storey or 0)
      if z.archetype == "tower" then s = math.min(P.storey or 0, (Q.storey or P.storey or 0)) end
      z.storey = s
      z.pos[2] = s * C.step + rng.pick({ 0, 0.5, 1 })
      L.finish_zone(z, C.step)
    end
    -- a second ring: outward of two neighbouring rim platforms, linked to both
    local done = false
    local rz = math.sqrt(z.size[1] ^ 2 + z.size[2] ^ 2) / 2
    for _, pq in ipairs({ { P, Q }, { rim[((ri - 2) % #rim) + 1], P } }) do
      local A, Bz = pq[1], pq[2]
      if not done and A ~= Bz then
        local mx, mz = (A.pos[1] + Bz.pos[1]) / 2, (A.pos[3] + Bz.pos[3]) / 2
        local dx, dz = mx - hx, mz - hz
        local d = math.max(1, math.sqrt(dx * dx + dz * dz))
        for _, extra in ipairs({ 6, 14 }) do
          if not done then done = bend(C, z, A, Bz, { target = { hx + dx / d * (d + rz + extra), hz + dz / d * (d + rz + extra) }, tries = 36, jitter = 8 }) end
        end
      end
    end
    if not done then hang_out(C, z, P, nbrs, { hx, hz }) end
    if C.geo[z] then z.spare = true end
  end
  return rim
end

-- ---------------------------------------------------------------------------------------------
-- DM: a hub (two on L), spokes at their own angles round it, rim platforms between them, extras outward
-- ---------------------------------------------------------------------------------------------

local function compose_dm(G, rng)
  local C = new_open(G, rng)
  local sc = G.size_class
  local B = G.budget
  local themes = G.themes
  local range = ({ XS = { 6, 6 }, S = { 7, 8 }, M = { 9, 11 }, L = { 14, 15 } })[sc] or { 7, 8 }
  local n = clamp(rng.int(range[1], range[2]), B.zones[1], math.min(B.zones[2], 14))
  local nhubs = (sc == "L") and 2 or 1
  local others = n - nhubs
  -- the programme: kinds with area weights
  local slots = {}
  local function slot(kind, wgt) slots[#slots + 1] = { kind = kind, w = wgt } end
  slot("tower", 1.25)
  slot("big", 1.5)
  slot("catwalk", 1.0)
  if others >= 8 then slot("catwalk", 0.95) end
  slot("small", 0.55)
  if others >= 7 then slot("small", 0.6) end
  if others >= 11 then slot("small", 0.6) end
  while #slots < others do slot("medium", rng.range(0.75, 1.1)) end
  while #slots > others do table.remove(slots) end
  local wsum = 2.8 + ((nhubs == 2) and 2.0 or 0)
  for _, s in ipairs(slots) do wsum = wsum + s.w end
  local mean = G.target_area * 0.84 / wsum
  local used, taken = {}, {}
  local hub_arch = rng.pick(P_HUB)
  used[hub_arch] = 1
  local hub = new_zone(C, hub_arch, { role = "hub", storey = 0, y = 0, size = hub_size(LA[hub_arch], mean * 2.8, rng.range(0.85, 1.15), taken),
    params = {}, palette = { theme = themes[math.min(#themes, 3)], variant = 1 } })
  local hub2 = nil
  if nhubs == 2 then
    local a2 = (hub_arch == "ring") and "hangar" or "ring"
    used[a2] = 1
    hub2 = new_zone(C, a2, { role = "hub", storey = 0, y = 0, size = hub_size(LA[a2], mean * 2.0, rng.range(0.85, 1.15), taken),
      params = {}, palette = { theme = themes[math.min(#themes, 3)], variant = 2 } })
    hub2.storey = 0
  end
  local zs = {}
  for _, s in ipairs(slots) do
    local arch, aspect
    if s.kind == "tower" then arch = "tower" used.tower = (used.tower or 0) + 1 aspect = rng.range(0.9, 1.1)
    elseif s.kind == "big" then arch = pick_arch(rng, P_BIG, used) aspect = rng.range(0.8, 1.25)
    elseif s.kind == "catwalk" then arch = "gantry" used.gantry = (used.gantry or 0) + 1 aspect = 1 / rng.range(2.0, 2.4)
    elseif s.kind == "small" then arch = pick_arch(rng, P_SMALL, used) aspect = rng.range(0.85, 1.2)
    else arch = pick_arch(rng, P_MED, used) aspect = rng.range(0.7, 1.4) end
    local z = new_zone(C, arch, { role = "room", size = size_for(LA[arch], mean * s.w, aspect, taken), params = { split = rng.pick(SIDES) },
      palette = { theme = themes[1], variant = 1 } })
    z.kind = s.kind
    zs[#zs + 1] = z
  end
  -- spokes: the biggest platforms that are neither the tower nor catwalks (and the second hub)
  local k = (others <= 7) and 3 or 4
  local order = {}
  for _, z in ipairs(zs) do
    local a = L.zone_area(z) + rng.float()
    if z.kind == "catwalk" or z.kind == "tower" then a = a - 1e5 end
    order[#order + 1] = { z = z, a = a }
  end
  sort_desc(order, "a")
  local spokes, rest = {}, {}
  for i, e in ipairs(order) do if i <= k then spokes[#spokes + 1] = e.z else rest[#rest + 1] = e.z end end
  local ring = storey_ring(rng, #spokes + (hub2 and 1 or 0))
  if hub2 then
    -- the second hub takes a place of the hub's storey in the ring
    local zeros = {}
    for i, v in ipairs(ring) do if v == 0 then zeros[#zeros + 1] = i end end
    table.insert(spokes, zeros[rng.int(1, #zeros)], hub2)
  end
  local first = { [0] = true }
  for i, a in ipairs(spokes) do
    a.storey = ring[i]
    a.pos[2] = floor_y(C, ring[i], not first[ring[i]])
    first[ring[i]] = true
    L.finish_zone(a, C.step)
    if a ~= hub2 then a.palette = { theme = themes[((ring[i] + 1) % #themes) + 1], variant = 1 + (i % 3) } end
  end
  -- fillers: one per gap (the tower, the catwalks first), the rest hang outward
  local fillers, extras = {}, {}
  for _, z in ipairs(rest) do if z.kind == "tower" then fillers[#fillers + 1] = z end end
  for _, z in ipairs(rest) do if z.kind == "catwalk" then fillers[#fillers + 1] = z end end
  for _, z in ipairs(rest) do if z.kind ~= "tower" and z.kind ~= "catwalk" then fillers[#fillers + 1] = z end end
  while #fillers > #spokes do extras[#extras + 1] = table.remove(fillers) end
  rng.shuffle(fillers)
  for i, z in ipairs(fillers) do z.palette = { theme = themes[(i % #themes) + 1], variant = 1 + ((i + 1) % 3) } end
  for i, z in ipairs(extras) do z.palette = { theme = themes[((i + 1) % #themes) + 1], variant = 1 + (i % 3) } end
  -- the hub at the origin at a seeded yaw, the wheel round it
  place(C, hub, C:rect_info(hub, rng.int(0, 17) * 5, 0, 0))
  C.hub = hub
  local lens = { { 10, 14 }, { 13, 19 }, { 16, 24 }, { 11, 16 }, { 14, 21 } }
  rng.shuffle(lens)
  wheel(C, hub, spokes, fillers, extras, { lens = lens, spoke_opts = function(_, a) if a == hub2 then return { lens = { 18, 26 }, flat = true } end return {} end })
  return C
end

local function finish_spawns_dm(G)
  local B = G.budget
  local per = (B.per_spawn[1] + B.per_spawn[2]) / 2
  local total = clamp(math.floor(G.target_area / per + 0.5), 4, 40)
  L.spread_spawns(G, total, function() return true end)
end

local function gen_dm(G, rng)
  local C = compose_dm(G, rng)
  if not C then return true end
  close_loops(C, nil, { cap = 4, extra = 3, p_add = 0.7 })
  if C:deficit(nil, 3) > 0 then rescue(C, nil, { hub = C.hub }) close_loops(C, nil, { extra = 0 }) end
  prune_dead_ends(C, nil)
  spread_palettes(C, C.hub)
  for _, z in ipairs(G.zones) do z.kind = nil end
  finish_spawns_dm(G)
  renumber(G)
  return true
end

-- ---------------------------------------------------------------------------------------------
-- KOTH: the floating hill, approach platforms on its walls, rim platforms between them, outer platforms
-- ---------------------------------------------------------------------------------------------

local function koth_rules(hill)
  return function(C, idx, adj)
    local i = idx[hill.id]
    local ap = i and #adj[i] or 0
    if ap < DATA.koth_approaches[1] then return 2 * (DATA.koth_approaches[1] - ap) end
    if ap > DATA.koth_approaches[2] then return 2 * (ap - DATA.koth_approaches[2]) end
    return 0
  end
end

local function gen_koth(G, rng)
  local C = new_open(G, rng)
  local themes = G.themes
  local B = G.budget
  -- the hill, its approaches, a rim platform in every gap between approaches, and a spare one outward
  local napp = rng.pick({ 5, 6, 6 })
  local n = clamp(1 + 2 * napp + rng.int(0, 1), B.zones[1], B.zones[2])
  local nrest = n - 1 - napp
  local used, taken = { summit = 1 }, {}
  local wsum = 2.6 + napp * 1.0 + nrest * 0.85
  local mean = G.target_area * 0.84 / wsum
  local hill = new_zone(C, "summit", { role = "hill", storey = 0, y = 0, size = size_for(LA.summit, mean * 2.6, rng.range(0.9, 1.1), taken),
    params = { depth = 4.5 }, palette = { theme = themes[3] or themes[1], variant = 1 } })
  local th0 = rng.int(0, 17) * 5
  place(C, hill, C:rect_info(hill, th0, 0, 0))
  local pool = { "deck", "plaza", "isle", "garden", "mast", "outpost", "gantry" }
  local function platform(arch, wgt, i)
    local aspect = (arch == "gantry") and (1 / rng.range(1.9, 2.3)) or rng.range(0.75, 1.3)
    return new_zone(C, arch, { role = "room", size = size_for(LA[arch], mean * wgt, aspect, taken), params = { split = rng.pick(SIDES) },
      palette = { theme = themes[(i % 2) + 1], variant = 1 + (i % 3) } })
  end
  -- approaches: on the hill's walls, either side of its plinth ramps, spread round
  local apps = {}
  for i = 1, napp do apps[i] = platform(pick_arch(rng, pool, used), rng.range(0.9, 1.2), i) end
  local ring = storey_ring(rng, napp)
  local first = { [0] = true }
  for i, a in ipairs(apps) do
    a.storey = ring[i]
    a.pos[2] = floor_y(C, ring[i], not first[ring[i]])
    first[ring[i]] = true
    L.finish_zone(a, C.step)
  end
  local fillers = {}
  local tower = platform("tower", 1.0, 0)
  used.tower = 1
  fillers[1] = tower
  for j = 2, nrest do fillers[j] = platform(pick_arch(rng, pool, used), rng.range(0.7, 0.95), j) end
  local extras = {}
  while #fillers > napp do extras[#extras + 1] = table.remove(fillers) end
  rng.shuffle(fillers)
  local lens = { { 10, 15 }, { 12, 18 }, { 14, 22 }, { 11, 16 }, { 16, 24 }, { 12, 17 } }
  rng.shuffle(lens)
  -- (each approach meets the hill at its port level; the hill's walls take two each)
  wheel(C, hill, apps, fillers, extras, { lens = lens, spoke_opts = function() return { level = 0, must = true } end })
  local rules = koth_rules(hill)
  close_loops(C, rules, { cap = 4, extra = 3, p_add = 0.7, extra_ok = function(c) return c.a ~= hill and c.b ~= hill end })
  if C:deficit(rules, 3) > 0 then rescue(C, rules, { hub = hill }) close_loops(C, rules, { extra = 0 }) end
  prune_dead_ends(C, rules)
  spread_palettes(C, hill)
  local radius = math.max(6, math.min(hill.size[1], hill.size[2]) / 2 - 7)
  G.objectives[#G.objectives + 1] = { kind = "hill", zone = hill.id, radius = radius }
  local per = (B.per_spawn[1] + B.per_spawn[2]) / 2
  local total = clamp(math.floor(G.target_area / per + 0.5), 6, 24)
  local list = {}
  for _, z in ipairs(G.zones) do if z.role ~= "hill" then list[#list + 1] = z end end
  -- the groups share the total (the bigger platforms take the remainder)
  local each = math.max(1, math.floor(total / math.max(1, #list)))
  local left = total - each * #list
  local order = {}
  for _, z in ipairs(list) do order[#order + 1] = { z = z, a = L.zone_area(z) } end
  sort_desc(order, "a")
  for _, e in ipairs(order) do
    local c = each
    if left > 0 then c = c + 1 left = left - 1 end
    G.objectives[#G.objectives + 1] = { kind = "spawns", id = "s" .. (#G.objectives + 1), zone = e.z.id, count = c }
  end
  renumber(G)
  return true
end

-- ---------------------------------------------------------------------------------------------
-- CTF: centre platforms on the axis, the red half composed round its base, mirrored for blue
-- ---------------------------------------------------------------------------------------------

-- The CTF rules on the red half: three node-disjoint routes from the base to three different centre zones (the
-- mirror makes them three lanes), base exits on two heights.
local function ctf_rules(base, centres)
  return function(C, idx, adj)
    local G = C.G
    local n = #G.zones
    local adj2 = {}
    for i = 1, n do adj2[i] = {} for _, e in ipairs(adj[i]) do adj2[i][#adj2[i] + 1] = e end end
    adj2[n + 1] = {}
    for _, cz in ipairs(centres) do
      local i = idx[cz.id]
      if i then adj2[i][#adj2[i] + 1] = { n + 1 } adj2[n + 1][#adj2[n + 1] + 1] = { i } end
    end
    local d = 0
    local r = L.disjoint_routes(n + 1, adj2, idx[base.id], n + 1, 3)
    if r < 3 then d = d + 3 * (3 - r) end
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

local function compose_ctf(G, rng)
  local C = new_open(G, rng)
  C.mirror = true
  local step = C.step
  local themes = G.themes
  local used, taken = {}, {}
  local neutral = themes[3] or themes[1]
  local nextra = 0
  local wsum = 2.2 + 1.1 + 1.0 + 2 * (1.7 + 0.75 * 2 + 1.0 * 3 + 0.8 * nextra)
  local mean = G.target_area * 0.72 / wsum
  -- the centre column: the hub, a high platform a storey up on one side, a low one a storey down on the other
  local hub_arch = rng.pick(P_HUB)
  used[hub_arch] = 1
  local hub = new_zone(C, hub_arch, { role = "hub", storey = 0, y = 0, size = hub_size(LA[hub_arch], mean * 3.8, rng.range(0.9, 1.1), taken),
    params = {}, palette = { theme = neutral, variant = 2 } })
  hub.centre, hub.lane = true, 1
  place(C, hub, C:rect_info(hub, 0, 0, 0))
  C.hub = hub
  local hi_dir = rng.chance(0.5) and 1 or -1
  local function centre(pool, storey, lane, dir, wgt, kinds)
    local arch = pick_arch(rng, pool, used)
    local A = LA[arch]
    local sz = size_for(A, mean * wgt, rng.range(1.1, 1.5), taken)
    local z = new_zone(C, arch, { role = (lane == 2) and "flank" or "room", storey = storey, y = storey * step, size = sz,
      params = { split = "+z" }, palette = { theme = neutral, variant = (lane == 0) and 1 or 3 } })
    z.centre, z.lane = true, lane
    local hr = C.info[hub].r
    for _, k in ipairs(kinds) do
      local l = new_link(C, k, 0, storey * step)
      local gap = (k == "stair") and (L.stair_run(step) + 2 + rng.range(0.5, 2)) or (math.max(step * 1.2 + 2, 2.1 * step, (k == "ramp") and L.ramp_run(step) + 2 or 0) + rng.range(0, 1.5))
      gap = math.ceil(gap * 2) / 2
      local d = z.size[2]
      local cz = (dir > 0) and (hr[4] + gap + d / 2) or (hr[2] - gap - d / 2)
      local info = C:rect_info(z, 0, 0, cz)
      local poly = rect_world(0, info.r)
      if zones_clear(C, poly, box_of(poly), ZONE_GAP) then
        C:put(z, info)
        if C:classic_spans(l, hub, z) then
          place(C, z, info)
          l.a, l.b = hub.id, z.id
          if commit_classic(C, l, hub, z, 0) then return z end
          remove(C, z)
          z.id = nil
        end
      end
    end
    return nil
  end
  local first_kind = rng.pick({ "stair", "ramp", "bridge" })
  local other = { stair = { "ramp", "bridge" }, ramp = { "stair", "bridge" }, bridge = { "stair", "ramp" } }
  local ch = centre({ "deck", "plaza", "mast", "isle" }, 1, 0, hi_dir, 1.1, { first_kind, "stair" })
  local cl = centre({ "garden", "isle", "outpost", "deck" }, -1, 2, -hi_dir, 1.0, { rng.pick(other[first_kind]), "stair", "bridge" })
  if not ch or not cl then return nil end
  -- (the red half keeps the centre's archetypes for the centre: a red twin pair beside one would be three in a row)
  used[ch.archetype], used[cl.archetype], used[hub.archetype] = 2, 2, 2
  -- the red half (x < 0)
  C.xmax_zone, C.xmax_link = -1.25, -0.65
  local function red(pool, storey, lane, role, wgt, variant)
    local arch = pick_arch(rng, pool, used)
    local z = new_zone(C, arch, { role = role, team = "red", storey = storey, y = storey * step + ((storey ~= 0 and rng.chance(0.35)) and 0.5 or 0),
      size = size_for(LA[arch], mean * wgt, rng.range(0.75, 1.35), taken), params = { split = rng.pick(SIDES) },
      palette = { theme = themes[(math.abs(storey) % #themes) + 1], variant = variant or (1 + (lane or 1)), team = "red" } })
    z.lane = lane
    return z
  end
  -- the base: a terrace at the end of the axis, its high half toward the high lane, far enough out for the flags
  local base = new_zone(C, "terrace", { role = "base", team = "red", storey = 0, y = 0, size = { clamp(even(mean * 1.7 / 28), 24, 30), 28 },
    params = { split = (hi_dir > 0) and "+z" or "-z" }, palette = { theme = themes[1], variant = 2, team = "red" } })
  base.lane = 1
  used.terrace = 1
  -- (turned a few degrees off the axis, so the bases do not sit square to the map)
  local bx, bz, bth = -snapv(rng.range(40, 48), 2), snapv(rng.range(-4, 4), 2), norm_rot(snapv(rng.range(-12, 12), 0.2))
  local blx, blz = to_local(bth, bx, bz)
  local binfo = C:rect_info(base, bth, snapv(blx, 16), snapv(blz, 16))
  place(C, base, binfo)
  -- the lanes: the high and the low lane through a platform each (linked to the base and to their centre
  -- platform), the middle one a long bridge from the base to the hub, or through a platform when it does not fit
  local function lane(z, ctr)
    local tx, tz = (base.pos[1] + ctr.pos[1]) / 2, (base.pos[3] + ctr.pos[3]) / 2
    if bend(C, z, base, ctr, { target = { tx, tz }, tries = 60, jitter = 7, tilt = { 14, 26 } }) then return true end
    if bend(C, z, base, ctr, { target = { tx - 4, tz + (tz - base.pos[3]) * 0.3 }, tries = 60, jitter = 9, tilt = { 8, 20 } }) then return true end
    if bend(C, z, base, ctr, { target = { tx, tz }, tries = 40, jitter = 7 }) then return true end
    local o = { target = { tx, tz }, also = { ctr }, also_len = 30, trials = 6, tries = 40 }
    if grow_free(C, z, base, o) then return true end
    o.also = { base }
    return grow_free(C, z, ctr, o) ~= nil
  end
  local mid = red({ "deck", "plaza", "isle", "garden", "mast" }, 0, 1, "room", 1.0)
  local high = red({ "deck", "plaza", "isle", "garden", "mast", "gantry" }, 1, 0, "room", 1.0)
  high.pos[2] = ch.pos[2]
  L.finish_zone(high, step)
  local low = red({ "garden", "isle", "gantry", "outpost", "mast" }, -1, 2, "flank", 1.0)
  low.pos[2] = cl.pos[2]
  L.finish_zone(low, step)
  if not lane(high, ch) then return nil end
  if not lane(low, cl) then return nil end
  local direct = find_link(C, base, hub, { maxlen = 26, flat = true })
  if direct then commit_cand(C, direct) end
  if not direct then
    mid.pos[2] = 0
    L.finish_zone(mid, step)
    if not lane(mid, hub) then return nil end
  end
  -- spawn platforms beside the base, each between the base and a lane platform (outward of them)
  local sp1 = red({ "outpost", "deck", "garden", "isle", "mast", "plaza" }, 1, 0, "spawn", 0.75)
  sp1.pos[2] = step
  L.finish_zone(sp1, step)
  local sp2 = red({ "outpost", "deck", "garden", "isle", "mast", "plaza" }, rng.pick({ 0, -1 }), 2, "spawn", 0.75)
  local lanes = { high, low }
  if not direct then lanes[3] = mid end
  for i, sp in ipairs({ sp1, sp2 }) do
    local made = false
    local order = (i == 1) and { high, low, mid } or { low, high, mid }
    for _, P in ipairs(order) do
      if not made and C.geo[P] then made = fill_gap(C, sp, base, P, hub, { 10, 28 }) end
    end
    if not made then
      local o = { also_any = lanes, also_len = 30, tries = 30, trials = 4, away = { 0, 0 } }
      made = grow_free(C, sp, base, o)
    end
    if made then lanes[#lanes + 1] = sp end
  end
  -- the middle platform (when the middle lane went straight to the hub) fills another gap round the base
  if direct then
    mid.lane = nil
    mid.pos[2] = rng.pick({ 0, 0.5 })
    L.finish_zone(mid, step)
    local made = false
    for _, pq in ipairs({ { base, high }, { base, low }, { sp1, high }, { sp2, low }, { base, sp1 }, { base, sp2 } }) do
      if not made and C.geo[pq[1]] and C.geo[pq[2]] then made = fill_gap(C, mid, pq[1], pq[2], hub, { 10, 28 }) end
    end
  end
  -- more red platforms while the zone budget asks for them (a platform that found no place above is replaced):
  -- each in a gap between two red platforms (or a red one and the centre), linked to both
  local function nred() local n = 0 for _, p in ipairs(G.zones) do if p.team == "red" then n = n + 1 end end return n end
  local want = math.max(nextra + 6, math.ceil((G.budget.zones[1] - 3) / 2))
  local guard = 0
  while nred() < want and guard < 4 do
    guard = guard + 1
    local z = red({ "deck", "plaza", "isle", "garden", "mast", "outpost", "gantry" }, rng.pick({ 0, 1, -1 }), nil, "room", 0.8)
    local reds = {}
    for _, p in ipairs(G.zones) do if p.team == "red" then reds[#reds + 1] = p end end
    local pairs_ = {}
    for i = 1, #reds do
      for j = i + 1, #reds do
        if C:linked(reds[i], reds[j]) then pairs_[#pairs_ + 1] = { reds[i], reds[j] } end
      end
      for _, c in ipairs({ ch, cl, hub }) do if C:linked(reds[i], c) then pairs_[#pairs_ + 1] = { reds[i], c } end end
    end
    rng.shuffle(pairs_)
    local made = false
    for k = 1, math.min(6, #pairs_) do
      if not made then
        z.pos[2] = ((pairs_[k][1].storey == pairs_[k][2].storey) and (pairs_[k][1].storey or 0) or 0) * step
        L.finish_zone(z, step)
        made = fill_gap(C, z, pairs_[k][1], pairs_[k][2], hub, { 10, 28 })
      end
    end
    if not made then
      rng.shuffle(reds)
      for _, p in ipairs(reds) do if not made then made = grow_free(C, z, p, { also_any = reds, also_len = 30, tries = 24, trials = 3 }) ~= nil end end
    end
  end
  return C, base, { hub, ch, cl }
end

local function gen_ctf(G, rng)
  local C, base, centres = compose_ctf(G, rng)
  if not C then return true end
  local function skip(za, zb) return za.team == "blue" or zb.team == "blue" or (za.centre and zb.centre) end
  local rules = ctf_rules(base, centres)
  close_loops(C, rules, { skip = skip, cap = 4, extra = 1 })
  if C:deficit(rules, 3) > 0 then rescue(C, rules, { skip = skip, hub = C.hub }) close_loops(C, rules, { skip = skip, extra = 0 }) end
  prune_dead_ends(C, rules)
  C:mirror_half()
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

-- ---------------------------------------------------------------------------------------------
-- Registration: open-air layouts compose here; every other class goes to the generator registered before
-- ---------------------------------------------------------------------------------------------

local OPEN = { dm = gen_dm, ctf = gen_ctf, koth = gen_koth }
for gt, gen in pairs(OPEN) do
  local prev = L.generators[gt]
  L.generators[gt] = function(G, rng, t)
    if G.class == "open_air" then return gen(G, rng, t) end
    if prev then return prev(G, rng, t) end
    return false
  end
end

end

return install
