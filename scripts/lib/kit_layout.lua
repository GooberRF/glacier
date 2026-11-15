-- Glacier design kit: layout graphs (installed into the kit by kit.lua; use it as local kit = ged.import("kit")).
--
-- Layout first: brief -> graph -> check -> build -> grade (docs/internal/DESIGN-QUALITY-SPEC.md §c.1-1, §(h)).
--   * kit.layout{gametype, players, class, seed, ...} returns a plain, editable table: zones (archetype, plan,
--     floor level, palette, role), typed links (corridor, doorway, stair, ramp, drop, bridge, window, lift, road),
--     objectives (spawn groups, flags, the hill, the payload cart and checkpoints) and the size budget.
--     The same seed always gives the same graph.
--   * kit.check_layout(graph) lists every violated c.1-1 rule, naming the zone or link and the rule.
--   * kit.layout_preview(graph) draws the plan as text (and SVG) before any brush exists.
--   * kit.build_layout(graph) builds it: zone shells, link air, zone interiors, link solids, details, lights,
--     then spawns and objectives (including the payload cart, handler, checkpoints and spawn hand-off).
--
-- The rules come from the exemplar study: scale by player class (§a.1 rows), loops and no dead ends (§a.2),
-- 3+ floor levels 4.5-10 m apart, team modes mirrored and keyed by palette, CTF lanes and flag spacing,
-- KOTH hill approaches, and the payload escort-genre principles of §(h). Numbers live in the DATA and BUDGET
-- tables below, never in per-level code.
--
-- This module returns one function, install(kit, internals), which kit.lua calls after kit_struct and kit_space.
-- Generation uses its own seeded stream (the kit never reads rng); building is deterministic for a given graph.

local function install(kit, I)
local fail, check_keys, number, positive, boolean = I.fail, I.check_keys, I.number, I.positive, I.boolean
local vec, fmt, EPS = I.vec, I.fmt, I.EPS
local hull, box_points, member = I.hull, I.box_points, I.member
local SP = I.space_parts

-- ---------------------------------------------------------------------------------------------
-- Data (spec §a.1 / §a.2 / §c.1-1 / §(h); the generator aims at the exemplar p25-p75 rows)
-- ---------------------------------------------------------------------------------------------

local LAYOUT = { generators = {} } -- the internals the layout generators build on (filled at the end of this module)

local DATA = {
  step = 5,                    -- metres between floor levels (c.1-1: levels 4.5-10 m apart)
  payload_step = 4.5,          -- payload legs climb 4.5 m over a 34 m graded stretch (13%: an uphill push; the level pads at the road's ends do not count)
  level_spacing = { 4.5, 10 },
  corridor_height = 4.5,       -- corridors 4-5 m high (c.1-2)
  corridor_widths = { 4, 4.5, 5, 5.5 },
  door_width = 3, door_height = 4,
  riser = 0.25, tread = 0.375, -- stairs: 34 degrees, a 5 m level in 7.5 m (binary-exact steps keep the CSG on the grid)
  ramp_slope = 0.5,
  drop_max = 8,                -- one-way drops (walk graph: drops of up to 8 m)
  max_step = 1,                -- a corridor may meet a floor this much higher (one step at the door; kit.limits.max_riser)
  min_wall = 1,                -- solid between spaces that stay separate
  nook_area = 1500,            -- at most one reward nook per 1,500 m2
  articulation_share = 0.10,   -- no articulation zone with >= 10% of the area behind it
  cycle_per_1000 = 1.0,        -- cycle rank >= 1 per 1,000 m2 (DM-L 1.5)
  cycle_per_1000_L = 1.5,
  repeat_max = 2,              -- no archetype more than twice
  keep_depth = 4.5,            -- a port's approach kept clear inside its zone
  ctf_flags = { 50, 115 },     -- metres between the flags
  ctf_balance = 0.9,
  koth_approaches = { 5, 8 },
  koth_relief = { 3, 5 },      -- the hill sunken or raised 3-5 m
  payload_segments = { 3, 5 },
  payload_boundary_routes = 3,
  payload_max_grade = 0.15,
  payload_outdoor_share = 0.3,
  payload_area_min = 10000,    -- P07
  open_air_void_depth = 40,
  chasm_depth = 8,             -- a bridge outside an open-air layout spans a chasm this deep under its deck (link.depth)
  pit_depth = 10,              -- a pit zone's chasm: deeper than drop_max, so it is crossed or skirted, never entered
}

-- Size budgets: playable floor and floor per spawn are the §a.1 rows; zones and hubs are §c.1-1.
-- XS is §(g) open question 2: 2-4 players play in 1,500-3,000 m2.
local BUDGET = {
  dm = {
    XS = { area = { 1500, 3000 }, per_spawn = { 250, 500 }, zones = { 5, 7 }, hubs = { 1, 1 } },
    S = { area = { 3300, 4900 }, per_spawn = { 430, 610 }, zones = { 5, 9 }, hubs = { 1, 1 } },
    M = { area = { 4400, 6400 }, per_spawn = { 300, 510 }, zones = { 8, 14 }, hubs = { 1, 1 } },
    L = { area = { 4600, 8000 }, per_spawn = { 165, 265 }, zones = { 12, 20 }, hubs = { 2, 3 } },
  },
  ctf = { area = { 5000, 8200 }, per_spawn = { 265, 410 }, zones = { 12, 20 }, hubs = { 1, 3 } },
  koth = { area = { 7400, 10000 }, per_spawn = { 340, 660 }, zones = { 8, 14 }, hubs = { 1, 1 } },
  -- Payload (§h): 3-4 x the DM-M / CTF floor medians, 3-5 segments each a mid-size DM area.
  payload = { area = { 12000, 24000 }, per_spawn = { 180, 800 }, zones = { 12, 40 }, hubs = { 0, 0 } },
}

local THEMES = {
  dm = { "industrial", "bunker", "admin" },
  ctf = { "bunker", "industrial", "admin" },
  koth = { "industrial", "marsbase", "admin" },
  payload = { "industrial", "admin", "cave", "marsbase", "bunker" },
}

local SIDES = { "+x", "-x", "+z", "-z" }
local OPP = { ["+x"] = "-x", ["-x"] = "+x", ["+z"] = "-z", ["-z"] = "+z" }
local DIRV = { ["+x"] = { 1, 0 }, ["-x"] = { -1, 0 }, ["+z"] = { 0, 1 }, ["-z"] = { 0, -1 } }
local LINK_TYPES = { corridor = true, doorway = true, stair = true, ramp = true, drop = true, bridge = true, window = true, lift = true, road = true, join = true }
local WALK = { corridor = true, doorway = true, stair = true, ramp = true, drop = true, bridge = true, lift = true, road = true, join = true }
local CART_LINK = { road = true, bridge = true, doorway = true, corridor = true, join = true } -- links the payload cart can run along
-- The link types that may leave the facing-walls geometry (a free link: any heading, bends, any zone frames).
local FREE_TYPES = { corridor = true, doorway = true, road = true, bridge = true, stair = true, ramp = true }

-- ---------------------------------------------------------------------------------------------
-- Small helpers
-- ---------------------------------------------------------------------------------------------

local function clamp(x, a, b) if x < a then return a elseif x > b then return b end return x end
local function q2(x) return math.floor(x * 2 + 0.5) / 2 end
local function q4(x) return math.floor(x * 4 + 0.5) / 4 end
local function even(x) return math.floor(x / 2 + 0.5) * 2 end

-- A seeded Park-Miller stream; the kit never reads rng, so a graph depends on its seed only.
local function new_rng(seed)
  local s = (math.floor(math.abs(seed)) % 2147483646) + 1
  local r = {}
  for _ = 1, 3 do s = (s * 16807) % 2147483647 end
  function r.float() s = (s * 16807) % 2147483647 return (s - 1) / 2147483646 end
  function r.range(a, b) return a + (b - a) * r.float() end
  function r.int(a, b)
    if b <= a then return a end
    local k = a + math.floor(r.float() * (b - a + 1))
    if k > b then k = b end
    return k
  end
  function r.chance(p) return r.float() < p end
  function r.pick(list) return list[r.int(1, #list)] end
  function r.shuffle(list)
    for i = #list, 2, -1 do local j = r.int(1, i) list[i], list[j] = list[j], list[i] end
    return list
  end
  return r
end

local function copy_list(t) local o = {} for i, v in ipairs(t) do o[i] = v end return o end

-- ---------------------------------------------------------------------------------------------
-- Plan geometry: zone frames (zone.rot), polygon zones (archetype "poly") and polygon tests
-- ---------------------------------------------------------------------------------------------

local function snapv(v, k) return math.floor(v * k + 0.5) / k end

-- A yaw in [0, 360): 0 faces +z, positive turns toward +x (the API's yaw convention).
local function norm_rot(r)
  if type(r) ~= "number" then return 0 end
  r = r % 360
  if r < 1e-9 or r > 360 - 1e-9 then return 0 end
  return r
end
local function zone_rot(z) if z.archetype == "poly" then return 0 end return norm_rot(z.rot) end
local function same_rot(a, b) local d = math.abs(a - b) return d < 1e-6 or math.abs(d - 360) < 1e-6 end

-- World = R(rot) . local about the world origin; local = R(-rot) . world (plan x, z).
local function to_world(rot, x, z)
  if rot == 0 then return x, z end
  local r = math.rad(rot)
  local c, s = math.cos(r), math.sin(r)
  return x * c + z * s, -x * s + z * c
end
local function to_local(rot, x, z)
  if rot == 0 then return x, z end
  local r = math.rad(rot)
  local c, s = math.cos(r), math.sin(r)
  return x * c - z * s, x * s + z * c
end
-- The yaw of a plan direction (dx, dz) in degrees (0 = +z, 90 = +x).
local function yaw_of(dx, dz) return math.deg(math.atan2(dx, dz)) end
local function ang_diff(a, b) local d = math.abs(norm_rot(a) - norm_rot(b)) return math.min(d, 360 - d) end

local function area2(p)
  local s = 0
  for i = 1, #p do local a, b = p[i], p[i % #p + 1] s = s + a[1] * b[2] - b[1] * a[2] end
  return s / 2
end
local function ccw(p)
  if area2(p) >= 0 then return p end
  local r = {}
  for i = #p, 1, -1 do r[#r + 1] = p[i] end
  return r
end
local function bbox(p)
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, q in ipairs(p) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
  return x0, z0, x1, z1
end
local function inside(p, x, z) return SP.inside(p, x, z) end

-- The world outline of a poly zone: its points as {x, z} in the order given (edge i runs from point i to point
-- i + 1), on the 1/16 m grid; nil when the outline is not a list of 3+ points.
local function poly_of(z)
  local o = z.outline
  if type(o) ~= "table" or #o < 3 then return nil end
  local out = {}
  for i, q in ipairs(o) do
    if type(q) ~= "table" then return nil end
    local x, zz = q[1] or q.x, q[2] or q.z
    if type(x) ~= "number" or type(zz) ~= "number" then return nil end
    out[i] = { snapv(x, 16), snapv(zz, 16) }
  end
  return out
end

-- +1 when a poly zone's outline runs counter-clockwise, -1 clockwise (its edges' outward side follows).
local function poly_sign(p) return area2(p) >= 0 and 1 or -1 end

-- Edge i of a polygon: start, end, unit direction, length and outward normal.
local function poly_edge(p, i)
  local a, b = p[i], p[i % #p + 1]
  local ex, ez = b[1] - a[1], b[2] - a[2]
  local L = math.sqrt(ex * ex + ez * ez)
  local tx, tz = ex / L, ez / L
  local s = poly_sign(p)
  return a, b, { tx, tz }, L, { s * tz, -s * tx }
end

-- The centre of a rectangle zone's plan in its own frame: R(-rot) . pos, on the 1/16 m grid (pos itself at rot 0).
local function local_centre(z)
  local rot = zone_rot(z)
  if rot == 0 then return z.pos[1], z.pos[3] end
  local x, zz = to_local(rot, z.pos[1], z.pos[3])
  return snapv(x, 16), snapv(zz, 16)
end

-- A zone's plan rectangle in its own frame (a poly zone: its outline's world bounds).
local function rect_of(z)
  if z.archetype == "poly" then
    local p = poly_of(z)
    if p then return bbox(p) end
  end
  local w, d = z.size[1], z.size[2]
  local cx, cz = local_centre(z)
  return cx - w / 2, cz - d / 2, cx + w / 2, cz + d / 2
end

local function zone_area(z)
  if z.archetype == "poly" then
    local p = poly_of(z)
    if p then return math.abs(area2(p)) end
  end
  return z.size[1] * z.size[2]
end

-- A zone's plan in world coordinates (counter-clockwise): the turned rectangle, or a poly zone's outline.
local function zone_poly(z)
  if z.archetype == "poly" then
    local p = poly_of(z)
    if p then return ccw(p) end
  end
  local x0, z0, x1, z1 = rect_of(z)
  local rot = zone_rot(z)
  local out = {}
  for i, q in ipairs({ { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }) do
    local x, zz = to_world(rot, q[1], q[2])
    out[i] = { x, zz }
  end
  return out
end

-- Segment tests in the plan.
local function cross2(o, a, b) return (a[1] - o[1]) * (b[2] - o[2]) - (a[2] - o[2]) * (b[1] - o[1]) end
local function seg_hit(a, b, c, d)
  local d1, d2, d3, d4 = cross2(a, b, c), cross2(a, b, d), cross2(c, d, a), cross2(c, d, b)
  if ((d1 > 1e-12 and d2 < -1e-12) or (d1 < -1e-12 and d2 > 1e-12)) and ((d3 > 1e-12 and d4 < -1e-12) or (d3 < -1e-12 and d4 > 1e-12)) then return true end
  return false
end
local function pt_seg(p, a, b)
  local ex, ez = b[1] - a[1], b[2] - a[2]
  local L2 = ex * ex + ez * ez
  local t = L2 > 0 and clamp(((p[1] - a[1]) * ex + (p[2] - a[2]) * ez) / L2, 0, 1) or 0
  local x, z = a[1] + ex * t - p[1], a[2] + ez * t - p[2]
  return math.sqrt(x * x + z * z)
end
local function seg_seg(a, b, c, d)
  if seg_hit(a, b, c, d) then return 0 end
  return math.min(pt_seg(a, c, d), pt_seg(b, c, d), pt_seg(c, a, b), pt_seg(d, a, b))
end

-- The plan distance between two polygons (0 when they overlap or touch).
local function poly_dist(A, B, limit)
  local ax0, az0, ax1, az1 = bbox(A)
  local bx0, bz0, bx1, bz1 = bbox(B)
  local gx, gz = math.max(bx0 - ax1, ax0 - bx1, 0), math.max(bz0 - az1, az0 - bz1, 0)
  local far = math.sqrt(gx * gx + gz * gz)
  if far > (limit or 50) then return far end
  local best = math.huge
  for i = 1, #A do
    local a, b = A[i], A[i % #A + 1]
    for j = 1, #B do
      local d = seg_seg(a, b, B[j], B[j % #B + 1])
      if d < best then best = d end
      if best <= 0 then return 0 end
    end
  end
  -- no edges meet: one polygon holds the other, or they are apart
  if inside(B, A[1][1], A[1][2]) or inside(A, B[1][1], B[1][2]) then return 0 end
  return best
end

-- A polygon's plan bounds as a box {x0, z0, x1, z1}.
local function bbox_box(p) local x0, z0, x1, z1 = bbox(p) return { x0, z0, x1, z1 } end

-- The plan gap between two boxes (0 when they overlap).
local function box_gap(a, b)
  local gx, gz = math.max(b[1] - a[3], a[1] - b[3], 0), math.max(b[2] - a[4], a[2] - b[4], 0)
  return math.sqrt(gx * gx + gz * gz)
end

-- True when two polygons come closer than m (poly_dist(A, B, m) < m, without measuring what is far): boxes and
-- edge pairs whose boxes keep m apart are passed over. ba / bb: the polygons' boxes when known.
local function poly_near(A, B, m, ba, bb)
  ba = ba or bbox_box(A)
  bb = bb or bbox_box(B)
  if box_gap(ba, bb) >= m + 1e-9 then return false end
  local nb = #B
  local m2 = m + 1e-9
  for i = 1, #A do
    local a, b = A[i], A[i % #A + 1]
    local ex0, ez0, ex1, ez1 = math.min(a[1], b[1]), math.min(a[2], b[2]), math.max(a[1], b[1]), math.max(a[2], b[2])
    if not (ex0 > bb[3] + m2 or ex1 < bb[1] - m2 or ez0 > bb[4] + m2 or ez1 < bb[2] - m2) then
      for j = 1, nb do
        local c, d = B[j], B[j % nb + 1]
        local gx = math.max(math.min(c[1], d[1]) - ex1, ex0 - math.max(c[1], d[1]), 0)
        local gz = math.max(math.min(c[2], d[2]) - ez1, ez0 - math.max(c[2], d[2]), 0)
        if gx * gx + gz * gz < m2 * m2 and seg_seg(a, b, c, d) < m then return true end
      end
    end
  end
  if inside(B, A[1][1], A[1][2]) or inside(A, B[1][1], B[1][2]) then return true end
  return false
end

-- The intervals of the plan segment a -> b (as distances from a) that lie inside polygon P.
local function seg_inside(P, a, b)
  local ex, ez = b[1] - a[1], b[2] - a[2]
  local L = math.sqrt(ex * ex + ez * ez)
  if L < 1e-9 then return {} end
  local ts = { 0, L }
  for i = 1, #P do
    local c, d = P[i], P[i % #P + 1]
    local den = ex * (d[2] - c[2]) - ez * (d[1] - c[1])
    if math.abs(den) > 1e-12 then
      local t = ((c[1] - a[1]) * (d[2] - c[2]) - (c[2] - a[2]) * (d[1] - c[1])) / den
      local u = ((c[1] - a[1]) * ez - (c[2] - a[2]) * ex) / den
      if t > 0 and t < 1 and u >= 0 and u <= 1 then ts[#ts + 1] = t * L end
    end
  end
  table.sort(ts)
  local out = {}
  for i = 1, #ts - 1 do
    local m = (ts[i] + ts[i + 1]) / 2
    if ts[i + 1] - ts[i] > 1e-6 and inside(P, a[1] + ex / L * m, a[2] + ez / L * m) then
      if #out > 0 and math.abs(out[#out][2] - ts[i]) < 1e-6 then out[#out][2] = ts[i + 1] else out[#out + 1] = { ts[i], ts[i + 1] } end
    end
  end
  return out
end

-- The distance from a plan point to a polygon's boundary.
local function edge_dist(P, x, z)
  local best = math.huge
  for i = 1, #P do best = math.min(best, pt_seg({ x, z }, P[i], P[i % #P + 1])) end
  return best
end

-- True when a polygon is simple (no two non-adjacent edges touch or cross).
local function simple(P)
  local n = #P
  for i = 1, n do
    for j = i + 2, n do
      if not (i == 1 and j == n) then
        if seg_seg(P[i], P[i % n + 1], P[j], P[j % n + 1]) < 1e-6 then return false end
      end
    end
  end
  return true
end

local function has_level(z, off)
  for _, l in ipairs(z.levels or { 0 }) do if math.abs(l - off) < 0.01 then return true end end
  return false
end

local function intersect(A, B)
  local out = {}
  for _, a in ipairs(A) do
    for _, b in ipairs(B) do
      local lo, hi = math.max(a[1], b[1]), math.min(a[2], b[2])
      if hi > lo + EPS then out[#out + 1] = { lo, hi } end
    end
  end
  table.sort(out, function(p, q) return p[1] < q[1] end)
  return out
end

local function subtract(A, lo, hi)
  local out = {}
  for _, a in ipairs(A) do
    if hi <= a[1] or lo >= a[2] then out[#out + 1] = a
    else
      if lo > a[1] + EPS then out[#out + 1] = { a[1], lo } end
      if hi < a[2] - EPS then out[#out + 1] = { hi, a[2] } end
    end
  end
  return out
end

-- The along-wall interval of a zone side (z for the x walls, x for the z walls).
local function wall_span(z, side)
  local x0, z0, x1, z1 = rect_of(z)
  if side == "+x" or side == "-x" then return z0, z1 end
  return x0, x1
end

local function wall_coord(z, side)
  local x0, z0, x1, z1 = rect_of(z)
  return (side == "+x" and x1) or (side == "-x" and x0) or (side == "+z" and z1) or z0
end

-- ---------------------------------------------------------------------------------------------
-- The zone archetype library of the layout (built from kit primitives; every one accepts ports on its
-- walls at its levels, so links can attach where the graph says)
-- ---------------------------------------------------------------------------------------------

local LA, LORDER = {}, {}
local function def(name, d)
  d.name = name
  LA[name] = d
  LORDER[#LORDER + 1] = name
end

-- Default port ranges: every wall at every level of the zone, minus the chamfered corners.
local function default_ranges(z, side, off)
  if not has_level(z, off) then return {} end
  local lo, hi = wall_span(z, side)
  local c = (z.chamfer or 0) + 0.75
  if hi - lo - 2 * c < 2 then return {} end
  return { { lo + c, hi - c } }
end

local function ranges_of(z, side, off)
  local A = LA[z.archetype]
  if A and A.ranges then return A.ranges(z, side, off) end
  return default_ranges(z, side, off)
end

-- Ranges minus a central span (arena ramps, mound ramps) on some walls.
local function ranges_minus_centre(z, side, off, sides, half)
  local r = default_ranges(z, side, off)
  for _, s in ipairs(sides) do
    if s == side then
      local lo, hi = wall_span(z, side)
      local m = (lo + hi) / 2
      r = subtract(r, m - half, m + half)
    end
  end
  return r
end

-- A split zone (split, tower, terrace): the half toward params.split is one step up. Ports at the base on
-- the low end wall, one step up on the high end wall; the side walls take ports on their own half only
-- (open-air terrace) or none (split, tower: their connectors stand in the middle band).
local function split_ranges(z, side, off, side_walls)
  local up = z.params and z.params.split or "+x"
  local step = z.levels[2] or 0
  local x0, z0, x1, z1 = rect_of(z)
  if off == 0 then
    if side == OPP[up] then
      local r = default_ranges(z, side, 0)
      if not side_walls then
        -- the stair and the ramp up stand a quarter of the width either side of the middle
        local lo, hi = wall_span(z, side)
        local m, q = (lo + hi) / 2, (hi - lo) / 4
        r = subtract(r, m - q - 2.75, m - q + 2.75)
        r = subtract(r, m + q - 2.75, m + q + 2.75)
      end
      return r
    end
  elseif math.abs(off - step) < 0.01 then
    if side == up then return default_ranges(z, side, 0) end
  else
    return {}
  end
  if not side_walls or side == up or side == OPP[up] then return {} end
  local c = (z.chamfer or 0) + 0.75
  local lo, hi = wall_span(z, side)
  local mid = (up == "+x" or up == "-x") and (x0 + x1) / 2 or (z0 + z1) / 2
  local positive_up = (up == "+x" or up == "+z")
  local low_is_lo = positive_up
  if off == 0 then
    if low_is_lo then return { { lo + c, mid - 3 } } else return { { mid + 3, hi - c } } end
  else
    if low_is_lo then return { { mid + 3, hi - c } } else return { { lo + c, mid - 3 } } end
  end
end

local function std_height(area)
  if area < 100 then return clamp(q2(3.5 + 2.5 * math.sqrt(area / 100)), 3.5, 6) end
  if area <= 400 then return clamp(q2(6 + 3 * (area - 100) / 300), 6, 9) end
  return 9
end

-- Indoor archetypes ---------------------------------------------------------------------------

def("hall", {
  class = "indoor", min = { 16, 12 }, max = { 32, 32 }, levels = { 0 }, loops = 0, chamfer = 2,
  summary = "A hall with chamfered corners, a raised clerestory bay, a 1 m dais and pilasters on its blank walls.",
  height = function(z) return clamp(std_height(zone_area(z)), 6, 8) end,
})

def("storage", {
  class = "indoor", min = { 14, 12 }, max = { 26, 26 }, levels = { 0 }, loops = 1, chamfer = 1.5,
  summary = "A 4.5 m storage bay with a raised high bay, two-tier racks as cover and ceiling beams.",
  height = function() return 4.5 end,
})

def("colonnade", {
  class = "indoor", min = { 18, 14 }, max = { 34, 34 }, levels = { 0 }, loops = 2, chamfer = 2,
  summary = "A coffered hall with two rows of columns along its long axis.",
  height = function(z) return clamp(std_height(zone_area(z)), 7, 9) end,
})

def("split", {
  class = "indoor", min = { 24, 24 }, max = { 32, 32 }, levels = { 0, "step" }, loops = 1, chamfer = 2, tall = true,
  summary = "A split-level hall: one half a full level up, joined by a stair and a ramp.",
  height = function(z, step) return step + 5 end,
  ranges = function(z, side, off) return split_ranges(z, side, off, false) end,
})

def("gallery", {
  class = "indoor", min = { 18, 18 }, max = { 30, 30 }, levels = { 0, "step" }, loops = 2, chamfer = 2, tall = true,
  summary = "A two-storey hall with a 3 m gallery ring a level up, reached by two stairs.",
  height = function(z, step) return step + 6 end,
})

def("atrium", {
  class = "indoor", min = { 26, 26 }, max = { 36, 36 }, levels = { 0, "step" }, loops = 3, hub = true, tall = true,
  summary = "A 14 m octagonal atrium (a hub at least twice the corridor height) with a gallery ring, two stairs and a truss catwalk across.",
  chamfer = function(z) return q2(math.min(z.size[1], z.size[2]) * 0.22) end,
  height = function(z, step) return step + 9 end,
})

def("arena", {
  class = "indoor", min = { 28, 28 }, max = { 38, 38 }, levels = { 0 }, loops = 2, hub = true, chamfer = 4,
  summary = "An arena hall after dm12: a raised centre bay, a 10 x 10 x 3 m central block with a tunnel through it and ramps onto it, four columns.",
  height = function() return 9 end,
  ranges = function(z, side, off) return ranges_minus_centre(z, side, off, { "+z", "-z" }, 4) end,
})

def("sunken", {
  class = "indoor", min = { 30, 30 }, max = { 40, 40 }, levels = { 0 }, loops = 1, hub = true, hill = true, tall = true,
  summary = "A hall around a court sunk 4.5 m (after E22): a 6 m ring walkway, stairs down, cover in the court, a lantern above it.",
  chamfer = function(z) return q2(math.min(z.size[1], z.size[2]) * 0.2) end,
  height = function() return 10 end,
  extra_levels = function(z) return { -(z.params and z.params.depth or 4.5) } end,
})

def("mound", {
  class = "indoor", min = { 32, 32 }, max = { 40, 40 }, levels = { 0 }, loops = 1, hub = true, hill = true, tall = true,
  summary = "A hall around a raised hill: a 4.5 m octagonal plinth with ramps and stairs up and low walls on top.",
  chamfer = function(z) return q2(math.min(z.size[1], z.size[2]) * 0.2) end,
  height = function() return 12 end,
  ranges = function(z, side, off) return ranges_minus_centre(z, side, off, SIDES, 4) end,
  extra_levels = function(z) return { (z.params and z.params.depth or 4.5) } end,
})

def("courtyard", {
  class = "indoor", min = { 20, 18 }, max = { 32, 28 }, levels = { 0 }, loops = 1, sky = true, tall = true, chamfer = 3,
  summary = "A courtyard under the sky (11 m walls) with a raised planter and low cover walls.",
  height = function() return 11 end,
})

def("pit", {
  class = "indoor", min = { 22, 26 }, max = { 34, 34 }, levels = { 0 }, loops = 1, chamfer = 3,
  summary = "A room around a 10 m chasm with a plank bridge across it and a walkway round its rim (no way down: the bridge is the short way over).",
  height = function() return 8 end,
})

def("tunnel", {
  class = "indoor", min = { 8, 18 }, max = { 12, 34 }, levels = { 0 }, loops = 0, chamfer = 1, flank = true,
  summary = "A low flank tunnel (4.5 m, beamed) with wall posts.",
  height = function() return 4.5 end,
})

def("nook", {
  class = "indoor", min = { 8, 8 }, max = { 12, 12 }, levels = { 0 }, loops = 0, chamfer = 0, nook = true, repeatable = true,
  summary = "A small reward nook (one entrance).",
  height = function() return 4 end,
})

-- Open-air archetypes -------------------------------------------------------------------------

def("isle", {
  class = "open_air", min = { 18, 18 }, max = { 34, 34 }, levels = { 0 }, loops = 0, open_air = true,
  summary = "A floating octagonal island with a rocky underside, kerb-and-rail edges and rock cover.",
  chamfer = function(z) return q2(math.min(z.size[1], z.size[2]) * 0.18) end,
})

def("deck", {
  class = "open_air", min = { 18, 16 }, max = { 34, 30 }, levels = { 0 }, loops = 1, open_air = true, chamfer = 3,
  summary = "An open deck with edge rails, a canopy on posts and a deckhouse.",
})

def("tower", {
  class = "open_air", min = { 24, 24 }, max = { 34, 34 }, levels = { 0, "step" }, loops = 1, open_air = true, chamfer = 2,
  summary = "A deck with a raised upper deck a level up (stair and ramp) and a 26 m lattice mast landmark.",
  ranges = function(z, side, off) return split_ranges(z, side, off, false) end,
})

def("ring", {
  class = "open_air", min = { 26, 26 }, max = { 38, 38 }, levels = { 0 }, loops = 1, open_air = true, hub = true, chamfer = 3,
  summary = "A ring platform around a central drop, railed on both edges (a loop in the sky).",
})

def("terrace", {
  class = "open_air", min = { 24, 18 }, max = { 36, 30 }, levels = { 0, "step" }, loops = 1, open_air = true, chamfer = 2,
  summary = "Two floating decks a level apart, joined across a gap by a flying stair and a sloped plank bridge.",
  ranges = function(z, side, off) return split_ranges(z, side, off, true) end,
})

def("hangar", {
  class = "open_air", min = { 26, 22 }, max = { 36, 32 }, levels = { 0 }, loops = 1, open_air = true, hub = true, chamfer = 2,
  summary = "A deck carrying a roofed hangar open at both ends (an indoor fight space in the sky).",
})

def("summit", {
  class = "open_air", min = { 32, 32 }, max = { 40, 40 }, levels = { 0 }, loops = 1, open_air = true, hub = true, hill = true, chamfer = 6,
  summary = "A floating hill: an octagonal deck around a 4.5 m plinth with ramps and stairs up and low walls on top.",
  ranges = function(z, side, off) return ranges_minus_centre(z, side, off, SIDES, 4) end,
  extra_levels = function(z) return { (z.params and z.params.depth or 4.5) } end,
})

def("gantry", {
  class = "open_air", min = { 14, 18 }, max = { 24, 34 }, levels = { 0 }, loops = 1, open_air = true, chamfer = 1.5,
  summary = "A long deck under a truss gantry on posts, with crate stacks along it as cover.",
})

def("outpost", {
  class = "open_air", min = { 16, 16 }, max = { 28, 28 }, levels = { 0 }, loops = 1, open_air = true, chamfer = 2,
  summary = "A deck around a small bunker with doors on two sides (a loop through it) and low walls at the edges.",
})

def("plaza", {
  class = "open_air", min = { 20, 20 }, max = { 34, 34 }, levels = { 0 }, loops = 1, open_air = true,
  summary = "A broad floating plaza: a raised central dais, low walls around it and lamp posts.",
  chamfer = function(z) return q2(math.min(z.size[1], z.size[2]) * 0.15) end,
})

def("mast", {
  class = "open_air", min = { 18, 18 }, max = { 30, 30 }, levels = { 0 }, loops = 1, open_air = true, chamfer = 3,
  summary = "A deck around a 25 m lattice mast (a landmark seen from the other platforms), crates at its foot.",
})

def("garden", {
  class = "open_air", min = { 18, 18 }, max = { 30, 30 }, levels = { 0 }, loops = 0, open_air = true, chamfer = 4,
  summary = "A rocky floating garden: boulders and low rock walls as cover, railed edges.",
})

-- Payload archetypes --------------------------------------------------------------------------

-- The direction the cart travels through a road or finale zone (params.heading, default "+z"): it enters on the
-- opposite side and, for a road, leaves on the heading side.
local function heading_of(z)
  local h = z.params and z.params.heading
  if h == "+x" or h == "-x" or h == "+z" or h == "-z" then return h end
  return "+z"
end

-- A road zone's frame along its heading: s = metres from the entry wall (0) to the exit wall (len), across = the
-- other plan axis. params.south / params.north are the level pads at the entry and exit ends, params.rise the climb.
local function road_frame(z)
  local p = z.params or {}
  local h = heading_of(z)
  local x0, z0, x1, z1 = rect_of(z)
  local F = { heading = h, rise = p.rise or 0, ls = p.south or 10, ln = p.north or 10 }
  if h == "+z" or h == "-z" then F.axis, F.a0, F.a1, F.c0, F.c1 = "z", z0, z1, x0, x1
  else F.axis, F.a0, F.a1, F.c0, F.c1 = "x", x0, x1, z0, z1 end
  F.sign = (h == "+z" or h == "+x") and 1 or -1
  F.len, F.wid = F.a1 - F.a0, F.c1 - F.c0
  F.centre = (F.c0 + F.c1) / 2
  F.entry, F.exit = OPP[h], h
  -- The plan-axis coordinate s metres from the entry wall, and the world (x, z) of (s, across).
  function F.at(s) if F.sign > 0 then return F.a0 + s end return F.a1 - s end
  function F.xz(s, c) local a = F.at(s) if F.axis == "z" then return c, a end return a, c end
  -- An axis-aligned world rect from an s range and an across range.
  function F.rect(s0, s1, c0, c1)
    local a, b = F.at(s0), F.at(s1)
    if a > b then a, b = b, a end
    if F.axis == "z" then return c0, a, c1, b end
    return a, c0, b, c1
  end
  return F
end

def("road", {
  class = "payload", min = { 8, 32 }, max = { 14, 64 }, levels = { 0 }, loops = 0, sky = true, spine = true, repeatable = true,
  summary = "A payload road under the sky on any heading (params.heading; size = {width, length} for +z/-z, {length, width} for +x/-x): level pads at both ends (params.south at the entry, params.north at the exit), graded between (params.rise, up to 15%), barriers along the edges; tunnel legs are roofed.",
  height = function(z) return (z.params and z.params.tunnel) and 6.5 or 8 end,
  -- size bounds as {w, d} for the zone's heading (the length runs along it)
  dims = function(z, b) if road_frame(z).axis == "x" then return { b[2], b[1] } end return b end,
  ranges = function(z, side, off)
    local F = road_frame(z)
    local rise = F.rise
    if side == F.entry then return off == 0 and { { F.c0 + 0.5, F.c1 - 0.5 } } or {} end
    if side == F.exit then return math.abs(off - rise) < 0.01 and { { F.c0 + 0.5, F.c1 - 0.5 } } or {} end
    local out = {}
    local function span(s0, s1) local a, b = F.at(s0), F.at(s1) if a > b then a, b = b, a end out[#out + 1] = { a, b } end
    if off == 0 then span(1, F.ls - 1) end
    if math.abs(off - rise) < 0.01 then span(F.len - F.ln + 1, F.len - 1) end
    table.sort(out, function(a, b) return a[1] < b[1] end)
    return out
  end,
})

def("yard", {
  class = "payload", min = { 24, 24 }, max = { 36, 36 }, levels = { 0 }, loops = 2, sky = true, spine = true, repeatable = true, tall = true, chamfer = 3,
  summary = "A checkpoint yard under the sky (12 m walls): low walls and crate stacks as cover off the cart lane, a raised dais.",
  height = function() return 12 end,
})

def("finale", {
  class = "payload", min = { 36, 36 }, max = { 46, 46 }, levels = { 0, "step" }, loops = 2, sky = true, spine = true, tall = true,
  summary = "The fortified finale: an octagonal court with a defender terrace on three sides, two stairs up and the final pit with a cart ramp; the cart enters on the side opposite params.heading (default \"+z\": from -z).",
  chamfer = function(z) return q2(math.min(z.size[1], z.size[2]) * 0.2) end,
  height = function(z, step) return step + 11 end,
  -- the cart enters on the side opposite params.heading (default "+z": from -z); the terrace takes the other three
  ranges = function(z, side, off)
    local entry = OPP[heading_of(z)]
    if off == 0 then return side == entry and default_ranges(z, side, 0) or {} end
    if side == entry then return {} end
    return default_ranges(z, side, 0)
  end,
})

def("start", {
  class = "payload", min = { 16, 12 }, max = { 24, 20 }, levels = { 0 }, loops = 0, chamfer = 2,
  summary = "The attackers' starting spawn hall.",
  height = function() return 6 end,
})

def("overlook", {
  class = "payload", min = { 14, 16 }, max = { 24, 40 }, levels = { 0 }, loops = 1, sky = true, chamfer = 2, tall = true, repeatable = true,
  summary = "High ground under the sky: a terrace with cover walls and a covered position on posts, overlooking the track.",
  height = function() return 10 end,
})

-- Traced spaces --------------------------------------------------------------------------------

def("poly", {
  class = "traced", min = { 4, 4 }, max = { 64, 64 }, levels = { 0 }, loops = 0, chamfer = 0, repeatable = true, poly = true,
  summary = "A traced space of any simple polygon plan: zone.outline lists 3-64 world points {x, z} (either winding; pos = {x, floor, z}, size and pos x/z follow from the outline). A room with a raised ceiling bay when it is big enough, or a yard under the sky (zone.sky = true); low cover on its clear floor. Free links attach on any edge (a_side / b_side = the edge's number: edge i runs from point i to point i + 1); join it to a neighbour to make one space.",
  height = function(z) if z.sky then return 10 end return clamp(std_height(zone_area(z)), 4, 9) end,
})

-- ---------------------------------------------------------------------------------------------
-- Link geometry (shared by the generator, the checker and the builder)
-- ---------------------------------------------------------------------------------------------

local function stair_run(rise) return math.floor(math.abs(rise) / DATA.riser + 0.5) * DATA.tread end
local function ramp_run(rise) return math.abs(rise) / DATA.ramp_slope end

-- The default clear height of a link's opening (graph: a bridge outside an open-air layout spans a chasm cut to
-- the sky, as tall as a cart road).
local function link_height(l, graph)
  if l.height then return l.height end
  if l.type == "road" then return l.tunnel and 6.5 or 8 end
  if l.type == "bridge" and graph and graph.class ~= "open_air" then return 8 end
  if l.type == "doorway" then return DATA.door_height end
  if l.type == "window" then return 1.5 end
  return DATA.corridor_height
end

-- Geometry between two zones: the separating axis, the walls and their shared span.
local function link_frame(za, zb)
  if za.archetype == "poly" or zb.archetype == "poly" then return nil, "a poly zone takes free links only" end
  if not same_rot(zone_rot(za), zone_rot(zb)) then return nil, "their rot differs" end
  local ax0, az0, ax1, az1 = rect_of(za)
  local bx0, bz0, bx1, bz1 = rect_of(zb)
  local gx = math.max(bx0 - ax1, ax0 - bx1)
  local gz = math.max(bz0 - az1, az0 - bz1)
  if gx > EPS and gz > EPS then return nil, "they are separated on both x and z (diagonal)" end
  if gx <= EPS and gz <= EPS then return nil, "their plans overlap or touch" end
  local f = {}
  if gx > EPS then
    f.axis, f.gap = "x", gx
    if bx0 >= ax1 then f.side_a, f.side_b, f.wa, f.wb = "+x", "-x", ax1, bx0 else f.side_a, f.side_b, f.wa, f.wb = "-x", "+x", ax0, bx1 end
    f.lo, f.hi = math.max(az0, bz0), math.min(az1, bz1)
  else
    f.axis, f.gap = "z", gz
    if bz0 >= az1 then f.side_a, f.side_b, f.wa, f.wb = "+z", "-z", az1, bz0 else f.side_a, f.side_b, f.wa, f.wb = "-z", "+z", az0, bz1 end
    f.lo, f.hi = math.max(ax0, bx0), math.min(ax1, bx1)
  end
  return f
end

-- A zone's plan centre in world coordinates.
local function centre_of(z)
  if z.archetype == "poly" then
    local p = poly_of(z)
    if p then local x0, z0, x1, z1 = bbox(p) return (x0 + x1) / 2, (z0 + z1) / 2 end
  end
  return z.pos[1], z.pos[3]
end

-- The wall of a rectangle zone that faces another zone best (its own sides).
local function facing_side(z, other)
  local ox, oz = centre_of(other)
  local lx, lz = to_local(zone_rot(z), ox, oz)
  local cx, cz = local_centre(z)
  local dx, dz = (lx - cx) / math.max(1, z.size[1]), (lz - cz) / math.max(1, z.size[2])
  if math.abs(dx) > math.abs(dz) then return dx > 0 and "+x" or "-x" end
  return dz > 0 and "+z" or "-z"
end

-- The floor a link meets on zone z (joined to other) when it gives no a_y / b_y: the zone's floor, or a graded
-- road's top floor (pos.y + rise) at its exit wall (side: the wall the link leaves by, when the link names it).
local function end_floor(z, other, side)
  if z.archetype == "road" and other and type(z.params) == "table" and (z.params.rise or 0) ~= 0 then
    local s = side
    if s == nil then
      local f = link_frame(z, other)
      if f then s = f.side_a elseif other.pos and other.size then s = facing_side(z, other) end
    end
    if s == heading_of(z) then return z.pos[2] + z.params.rise end
  end
  return z.pos[2]
end

-- The port floors of a link on its two zones: a_y / b_y, else end_floor.
local function link_ys(l, za, zb)
  if l.type == "join" then return za.pos[2], zb.pos[2] end
  return l.a_y or end_floor(za, zb, l.a_side), l.b_y or end_floor(zb, za, l.b_side)
end

-- How far apart two zones must be for a link of this type (the rise from the port floors; without zones, from
-- a_y and b_y).
local function gap_need(l, za, zb)
  local ya, yb = l.a_y or 0, l.b_y or 0
  if za and zb then ya, yb = link_ys(l, za, zb) end
  local rise = math.abs(ya - yb)
  local t = l.type
  if t == "stair" then return stair_run(rise) + 2 end
  if t == "ramp" then return ramp_run(rise) + 2 end
  if t == "lift" then return (l.width or 4) + 5 end
  if t == "bridge" then return math.max(4, rise * 1.2 + 2) end
  if t == "drop" or t == "window" or t == "doorway" or t == "road" then return 0.5 end
  return 1
end

-- The opening width a link needs on its zones' walls.
local function port_width(l)
  local w = l.width or (l.type == "doorway" and DATA.door_width) or (l.type == "road" and 8) or 4.5
  if l.type == "bridge" then return w + 1 end
  return w
end

-- The port floor offsets a link uses on its two zones (b's for a drop is its landing floor).
local function port_offsets(l, za, zb)
  local ya, yb = link_ys(l, za, zb)
  return ya - za.pos[2], yb - zb.pos[2]
end

-- The vertical band of the opening on each wall: {y0, y1} at a and at b.
local function port_bands(l, za, zb, graph)
  local H = link_height(l, graph)
  local ay, by = link_ys(l, za, zb)
  if l.type == "window" then
    local sill = math.max(ay, by) + 1.6
    return { sill, sill + H }, { sill, sill + H }
  elseif l.type == "drop" then
    return { ay, ay + H }, { ay, ay + H }
  elseif l.type == "stair" or l.type == "ramp" or l.type == "lift" then
    return { ay, ay + H }, { by, by + H }
  end
  return { ay, ay + H }, { by, by + H }
end

-- ---------------------------------------------------------------------------------------------
-- Free links and joins (shared by the resolver, the checker, the preview and the builder)
-- ---------------------------------------------------------------------------------------------

-- A link that asks to be routed freely: a path, or a side or point for one of its ports.
local function wants_free(l) return l.path ~= nil or l.a_side ~= nil or l.b_side ~= nil or l.a_at ~= nil or l.b_at ~= nil end

-- The vertical extent a zone fills: floor to ceiling, with sunken courts, pits and a finale's pit below, and an
-- open-air platform's underside and masts.
local function zone_band(z)
  local A = LA[z.archetype]
  if A and A.open_air then return z.pos[2] - 14, z.pos[2] + 30 end
  local lo, hi = z.pos[2], z.pos[2] + (z.height or 6)
  if A and A.extra_levels then for _, off in ipairs(A.extra_levels(z)) do lo = math.min(lo, z.pos[2] + off) end end
  if z.archetype == "pit" then lo = z.pos[2] - DATA.pit_depth elseif z.archetype == "finale" then lo = z.pos[2] - 2.5 end
  if z.archetype == "road" and type(z.params) == "table" and type(z.params.rise) == "number" then
    if z.params.rise > 0 then hi = hi + z.params.rise else lo = lo + z.params.rise end
  end
  return lo, hi
end

-- The local plan point of a rectangle zone's wall at along-wall coordinate c.
local function wall_point(z, side, c)
  local w = wall_coord(z, side)
  if side == "+x" or side == "-x" then return { w, c } end
  return { c, w }
end

-- The angle in degrees between two plan directions (unit vectors).
local function angle_of(u, v) return math.deg(math.acos(clamp(u[1] * v[1] + u[2] * v[2], -1, 1))) end

-- A polyline of half width hw as mitred quads: each joint's left (L) and right (R) point is computed once on the
-- 1/1024 m grid and shared by the segments meeting there. straight[i] = {s0, s1}: the stretch of segment i (metres
-- from its start point) where it has its full width. Returns nil and the reason when a segment is too short for
-- the turns at its ends.
local function mitre(pts, hw)
  local n = #pts
  local dirs, lens = {}, {}
  for i = 1, n - 1 do
    local dx, dz = pts[i + 1][1] - pts[i][1], pts[i + 1][2] - pts[i][2]
    local L = math.sqrt(dx * dx + dz * dz)
    if L < 1e-6 then return nil, "two points of its path coincide" end
    dirs[i], lens[i] = { dx / L, dz / L }, L
  end
  local Lp, Rp, over, turns = {}, {}, {}, {}
  for i = 1, n do
    local p = pts[i]
    if i == 1 or i == n then
      local d = dirs[(i == 1) and 1 or (n - 1)]
      Lp[i] = { p[1] - d[2] * hw, p[2] + d[1] * hw }
      Rp[i] = { p[1] + d[2] * hw, p[2] - d[1] * hw }
      over[i], turns[i] = 0, 0
    else
      local d1, d2 = dirs[i - 1], dirs[i]
      local th = math.acos(clamp(d1[1] * d2[1] + d1[2] * d2[2], -1, 1))
      turns[i] = math.deg(th)
      if turns[i] > 179 then return nil, "it turns back on itself at path point " .. (i - 1) end
      local mx, mz = -d1[2] - d2[2], d1[1] + d2[1]
      local ml = math.sqrt(mx * mx + mz * mz)
      mx, mz = mx / ml, mz / ml
      local k = hw / (mx * -d2[2] + mz * d2[1])
      Lp[i] = { p[1] + mx * k, p[2] + mz * k }
      Rp[i] = { p[1] - mx * k, p[2] - mz * k }
      over[i] = hw * math.tan(th / 2)
    end
    Lp[i] = { snapv(Lp[i][1], 1024), snapv(Lp[i][2], 1024) }
    Rp[i] = { snapv(Rp[i][1], 1024), snapv(Rp[i][2], 1024) }
  end
  local M = { pts = pts, dirs = dirs, lens = lens, L = Lp, R = Rp, turns = turns, quads = {}, straight = {}, hw = hw, length = 0 }
  for i = 1, n - 1 do
    local d = dirs[i]
    local dl = (Lp[i + 1][1] - Lp[i][1]) * d[1] + (Lp[i + 1][2] - Lp[i][2]) * d[2]
    local dr = (Rp[i + 1][1] - Rp[i][1]) * d[1] + (Rp[i + 1][2] - Rp[i][2]) * d[2]
    if dl < 0.25 or dr < 0.25 then
      return nil, "segment " .. i .. " (" .. fmt(lens[i]) .. " m) is too short for the turns at its ends (it needs " ..
        fmt(over[i] + over[i + 1] + 0.25) .. " m)"
    end
    M.quads[i] = ccw({ Rp[i], Rp[i + 1], Lp[i + 1], Lp[i] })
    M.straight[i] = { over[i], lens[i] - over[i + 1] }
    M.length = M.length + lens[i]
  end
  return M
end

-- Segment k of a mitred polyline shortened by a metres at its start and b at its end (its plan quad).
local function trim_quad(M, k, a, b)
  local d = M.dirs[k]
  local function mv(p, s) return { p[1] + d[1] * s, p[2] + d[2] * s } end
  return ccw({ mv(M.R[k], a), mv(M.R[k + 1], -b), mv(M.L[k + 1], -b), mv(M.L[k], a) })
end

-- The left and right points of a mitred polyline's segment k at s metres from its start point (on its side lines).
local function side_points(M, k, s)
  local d = M.dirs[k]
  local function on(A, B)
    local a = (A[1] - M.pts[k][1]) * d[1] + (A[2] - M.pts[k][2]) * d[2]
    local b = (B[1] - M.pts[k][1]) * d[1] + (B[2] - M.pts[k][2]) * d[2]
    local f = (s - a) / (b - a)
    return { A[1] + (B[1] - A[1]) * f, A[2] + (B[2] - A[2]) * f }
  end
  return on(M.L[k], M.L[k + 1]), on(M.R[k], M.R[k + 1])
end

-- Resolves every link: frame, centre along the walls, ports. With rng, missing offsets are chosen at random in
-- the free span (the generator); without, the centre of the widest free span (deterministic builds of edited
-- graphs). Joins are resolved first (their spans follow from the plan), then the classic links (two facing walls
-- of rectangle zones of one rot), then the free links (any heading, routed clear of the zones and the links
-- resolved before them). Returns plan = {zones, links[i] = {link, za, zb, mode ("classic", "free" or "join"),
-- ok, f, c, W, ba, bb, pa, pb, side_a, side_b, pts, M ...}, ports[zone_id] = {...}, foot, errors}.
-- resolver(graph, rng, keep) is the resolver's state with its link routines (X.join, X.classic, X.free, X.one);
-- resolve runs it over the graph's links; route_ctx / route_link / route_drop route single links against it.
local function resolver(graph, rng, keep)
  local plan = { zones = {}, links = {}, ports = {}, errors = {}, foot = {}, wpoly = {} }
  for _, z in ipairs(graph.zones or {}) do
    if type(z) == "table" and z.id ~= nil then plan.zones[z.id] = z plan.ports[z.id] = {} end
  end
  -- the wall spans in use per zone and side (a rectangle zone's "+x" ..., a poly zone's edge number)
  local used = {}
  local function used_of(id, side)
    local u = used[id]
    if not u then u = {} used[id] = u end
    local s = u[side]
    if not s then s = {} u[side] = s end
    return s
  end
  local function err(l, rule, msg)
    plan.errors[#plan.errors + 1] = { severity = "error", rule = rule, link = l.id, message = "link " .. tostring(l.id) .. ": " .. msg }
  end
  local wbox = {}
  local function wpoly(z)
    local p = plan.wpoly[z.id]
    if not p then p = zone_poly(z) plan.wpoly[z.id] = p wbox[z.id] = bbox_box(p) end
    return p, wbox[z.id]
  end
  local function add_port(z, p) table.insert(plan.ports[z.id], p) return p end

  -- Classic links ---------------------------------------------------------------------------------
  -- final = false: a free-method graph tries the classic geometry first and routes the link freely when it fails.
  local function classic(P, final)
    local l, za, zb = P.link, P.za, P.zb
    local f, why = link_frame(za, zb)
    if not f then
      if final then err(l, "geometry", "zones " .. za.id .. " and " .. zb.id .. " cannot be joined: " .. why .. ".") end
      return false
    end
    local W = port_width(l)
    local need = gap_need(l, za, zb)
    if f.gap < need - EPS then
      if not final then return false end
      err(l, "geometry", "the " .. fmt(f.gap) .. " m gap between " .. za.id .. " and " .. zb.id .. " is too short for a " .. l.type ..
        " (needs " .. fmt(need) .. " m).")
    end
    local oa, ob = port_offsets(l, za, zb)
    local ra = ranges_of(za, f.side_a, oa)
    local rb = ranges_of(zb, f.side_b, ob)
    local free = intersect(intersect(ra, rb), { { f.lo, f.hi } })
    local ba, bb = port_bands(l, za, zb, graph)
    for _, s in ipairs(used_of(za.id, f.side_a)) do
      if s.y0 < ba[2] + 0.5 and s.y1 > ba[1] - 0.5 then free = subtract(free, s.lo - 1.5, s.hi + 1.5) end
    end
    for _, s in ipairs(used_of(zb.id, f.side_b)) do
      if s.y0 < bb[2] + 0.5 and s.y1 > bb[1] - 0.5 then free = subtract(free, s.lo - 1.5, s.hi + 1.5) end
    end
    -- Centre positions: each free span shrunk by half the width.
    local centres = {}
    for _, s in ipairs(free) do
      if s[2] - s[1] >= W - EPS then centres[#centres + 1] = { s[1] + W / 2, s[2] - W / 2 } end
    end
    local c = nil
    if l.offset ~= nil then
      for _, s in ipairs(centres) do if l.offset >= s[1] - EPS and l.offset <= s[2] + EPS then c = l.offset end end
      if c == nil and not rng then
        if not final then return false end
        err(l, "geometry", "its offset " .. fmt(l.offset) .. " does not fit a free " .. fmt(W) .. " m span on both walls (" ..
          za.id .. " " .. f.side_a .. " at " .. fmt(oa) .. " m, " .. zb.id .. " " .. f.side_b .. " at " .. fmt(ob) .. " m).")
      end
    end
    if c == nil and #centres > 0 then
      if rng then
        -- l.prefer = -1 / +1 keeps the opening in the low / high half of the shared span when it can
        local pool = centres
        if l.prefer then
          local mid = (f.lo + f.hi) / 2
          local half = {}
          for _, s in ipairs(centres) do
            local lo, hi = s[1], s[2]
            if l.prefer < 0 then hi = math.min(hi, mid - 1) else lo = math.max(lo, mid + 1) end
            if hi >= lo then half[#half + 1] = { lo, hi } end
          end
          if #half > 0 then pool = half end
        end
        local s = pool[rng.int(1, #pool)]
        c = q4(rng.range(s[1], s[2]))
        c = clamp(c, s[1], s[2])
      else
        local best, bw = nil, -1
        for _, s in ipairs(centres) do if s[2] - s[1] > bw then best, bw = s, s[2] - s[1] end end
        c = q4((best[1] + best[2]) / 2)
        c = clamp(c, best[1], best[2])
      end
    end
    if c == nil then
      if not final then return false end
      P.f, P.W, P.ba, P.bb = f, W, ba, bb
      if l.offset == nil or rng then
        err(l, "geometry", "no free " .. fmt(W) .. " m span on both walls (" .. za.id .. " " .. f.side_a .. " at level +" .. fmt(oa) ..
          " m, " .. zb.id .. " " .. f.side_b .. " at level +" .. fmt(ob) .. " m); move the zones so their walls overlap more, or change the levels.")
      end
      return false
    end
    -- the passage between the two walls keeps a wall's thickness from every other zone
    if f.gap > 1.25 then
      local a, b = math.min(f.wa, f.wb), math.max(f.wa, f.wb)
      local q = (f.axis == "x") and { { a, c - W / 2 }, { b, c - W / 2 }, { b, c + W / 2 }, { a, c + W / 2 } } or
        { { c - W / 2, a }, { c + W / 2, a }, { c + W / 2, b }, { c - W / 2, b } }
      local rot = zone_rot(za)
      for i, p in ipairs(q) do local x, zz = to_world(rot, p[1], p[2]) q[i] = { x, zz } end
      local qb = bbox_box(q)
      for _, z in ipairs(graph.zones) do
        if z ~= za and z ~= zb and type(z) == "table" and z.id ~= nil and poly_near(q, wpoly(z), DATA.min_wall - 0.01, qb, wbox[z.id]) then
          if not final then return false end
          err(l, "geometry", "its passage between " .. za.id .. " and " .. zb.id .. " runs through zone " .. z.id .. " or within " .. fmt(DATA.min_wall) ..
            " m of it; move the zones, or give the link a path around it.")
          return false
        end
      end
    end
    P.f, P.W, P.ba, P.bb = f, W, ba, bb
    P.mode, P.ok, P.c = "classic", true, c
    P.side_a, P.side_b = f.side_a, f.side_b
    if keep then l.offset = c end
    local ua, ub = used_of(za.id, f.side_a), used_of(zb.id, f.side_b)
    ua[#ua + 1] = { lo = c - W / 2, hi = c + W / 2, y0 = P.ba[1], y1 = P.ba[2] }
    ub[#ub + 1] = { lo = c - W / 2, hi = c + W / 2, y0 = P.bb[1], y1 = P.bb[2] }
    local fya, fyb = link_ys(l, za, zb)
    local pa = { side = f.side_a, lo = c - W / 2, hi = c + W / 2, y0 = P.ba[1], y1 = P.ba[2], link = l, wall = f.wa, floor = fya,
      pt = wall_point(za, f.side_a, c), nrm = { DIRV[f.side_a][1], DIRV[f.side_a][2] } }
    local pb = { side = f.side_b, lo = c - W / 2, hi = c + W / 2, y0 = P.bb[1], y1 = P.bb[2], link = l, wall = f.wb, floor = fyb,
      pt = wall_point(zb, f.side_b, c), nrm = { DIRV[f.side_b][1], DIRV[f.side_b][2] } }
    if l.type == "drop" then pb.drop_to = fyb end
    table.insert(plan.ports[za.id], pa)
    table.insert(plan.ports[zb.id], pb)
    P.pa, P.pb = pa, pb
    -- its plan footprint in world coordinates (free links keep clear of it)
    local a, b = math.min(f.wa, f.wb), math.max(f.wa, f.wb)
    local x0, z0, x1, z1
    if f.axis == "x" then x0, x1, z0, z1 = a, b, c - W / 2, c + W / 2 else x0, x1, z0, z1 = c - W / 2, c + W / 2, a, b end
    local rot = zone_rot(za)
    local poly = {}
    for i, q in ipairs({ { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }) do
      local x, zz = to_world(rot, q[1], q[2])
      poly[i] = { x, zz }
    end
    local lo = math.min(fya, fyb)
    if l.type == "bridge" and graph.class ~= "open_air" then lo = lo - (l.depth or DATA.chasm_depth) end
    plan.foot[#plan.foot + 1] = { poly = poly, band = { lo, math.max(fya, fyb) + link_height(l, graph) }, link = l, id = l.id }
    return true
  end

  -- Joins -----------------------------------------------------------------------------------------
  local function join(P)
    local l, za, zb = P.link, P.za, P.zb
    P.mode = "join"
    local names = za.id .. " and " .. zb.id
    if math.abs(za.pos[2] - zb.pos[2]) > DATA.max_step + EPS then
      err(l, "level", "the join between " .. names .. " has floors " .. fmt(math.abs(za.pos[2] - zb.pos[2])) ..
        " m apart; joined zones are one space (floors within " .. fmt(DATA.max_step) .. " m).")
      return
    end
    local bot = math.max(za.pos[2], zb.pos[2])
    local top = math.min(za.pos[2] + (za.height or 6), zb.pos[2] + (zb.height or 6))
    P.y0, P.y1 = bot, top
    P.ext, P.ext_quads = nil, {}
    local function narrow(w)
      err(l, "geometry", "the join between " .. names .. " shares only " .. fmt(w) .. " m of boundary (an opening of at least 3 m).")
    end
    local function apart()
      err(l, "geometry", "zones " .. names .. " do not touch or overlap along one wall (a join opens two zones that touch or overlap into one space; zones that are not joined stand " ..
        fmt(DATA.min_wall) .. " m apart).")
    end
    local ra, rb = za.archetype ~= "poly", zb.archetype ~= "poly"
    if ra and rb then
      if not same_rot(zone_rot(za), zone_rot(zb)) then
        err(l, "geometry", "the join between " .. names .. " needs both rectangle zones at one rot, wall to wall (trace one as a poly zone to join across frames).")
        return
      end
      local ax0, az0, ax1, az1 = rect_of(za)
      local bx0, bz0, bx1, bz1 = rect_of(zb)
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
      if not f then apart() return end
      local free = intersect(intersect(ranges_of(za, f.side_a, 0), ranges_of(zb, f.side_b, 0)), { { f.lo, f.hi } })
      for _, e in ipairs({ { za, f.side_a }, { zb, f.side_b } }) do
        for _, s in ipairs(used_of(e[1].id, e[2])) do if s.y0 < top and s.y1 > bot then free = subtract(free, s.lo - 1.5, s.hi + 1.5) end end
      end
      local best = nil
      for _, s in ipairs(free) do if not best or s[2] - s[1] > best[2] - best[1] then best = s end end
      if not best or best[2] - best[1] < 3 - EPS then narrow(best and (best[2] - best[1]) or 0) return end
      local lo, hi = best[1], best[2]
      for _, e in ipairs({ { za, f.side_a, f.wa }, { zb, f.side_b, f.wb } }) do
        local u = used_of(e[1].id, e[2])
        u[#u + 1] = { lo = lo, hi = hi, y0 = bot, y1 = top }
        add_port(e[1], { side = e[2], lo = lo, hi = hi, y0 = e[1].pos[2], y1 = top, link = l, wall = e[3], floor = e[1].pos[2], join = true,
          pt = wall_point(e[1], e[2], (lo + hi) / 2), nrm = { DIRV[e[2]][1], DIRV[e[2]][2] } })
      end
      P.side_a, P.side_b = f.side_a, f.side_b
      P.ext = { zone = za, axis = f.axis, w0 = math.min(f.wa, f.wb) - 0.5, w1 = math.max(f.wa, f.wb) + 0.5, lo = lo, hi = hi }
      P.span = hi - lo
      P.ok = true
      return
    end
    if ra or rb then
      -- a rectangle zone and a poly zone: the poly must reach over one wall of the rectangle
      local Hz, Q = za, zb
      if not ra then Hz, Q = zb, za end
      local rot = zone_rot(Hz)
      local q = {}
      for i, p in ipairs(wpoly(Q)) do local x, zz = to_local(rot, p[1], p[2]) q[i] = { x, zz } end
      local x0, z0, x1, z1 = rect_of(Hz)
      local found = {}
      for _, side in ipairs(SIDES) do
        local function probe(dl)
          local a, b = nil, nil
          if side == "+x" then a, b = { x1 + dl, z0 }, { x1 + dl, z1 }
          elseif side == "-x" then a, b = { x0 - dl, z0 }, { x0 - dl, z1 }
          elseif side == "+z" then a, b = { x0, z1 + dl }, { x1, z1 + dl }
          else a, b = { x0, z0 - dl }, { x1, z0 - dl } end
          local base = (side == "+x" or side == "-x") and z0 or x0
          local out = {}
          for _, iv in ipairs(seg_inside(q, a, b)) do out[#out + 1] = { base + iv[1], base + iv[2] } end
          return out
        end
        -- (the opening's air box runs 0.5 m into the poly: the span is where the poly covers the wall line out to
        -- 0.5 m, a quarter metre in from the poly's walls at each end, so no end of the box lies on or along one of
        -- the poly's walls; an air box ending on another brush's oblique face leaves an open seam in the compile)
        local best = nil
        for _, s in ipairs(intersect(probe(0.05), probe(0.5))) do
          if s[2] - s[1] > 0.75 and (not best or s[2] - s[1] > best[2] - best[1]) then best = s end
        end
        if best then found[#found + 1] = { side, { best[1] + 0.25, best[2] - 0.25 } } end
      end
      if #found == 0 then apart() return end
      if #found > 1 then
        err(l, "geometry", "the join between " .. names .. " runs round a corner of " .. Hz.id .. " (its " .. found[1][1] .. " and " .. found[2][1] ..
          " walls); the shared boundary must lie on one wall of a rectangle zone.")
        return
      end
      local side, lo, hi = found[1][1], found[1][2][1], found[1][2][2]
      if hi - lo < 3 - EPS then narrow(hi - lo) return end
      local inside_range = false
      local rr = ranges_of(Hz, side, 0)
      for _, r in ipairs(rr) do if lo >= r[1] - 0.05 and hi <= r[2] + 0.05 then inside_range = true end end
      if not inside_range then
        local w0 = wall_span(Hz, side)
        local txt = {}
        for _, r in ipairs(rr) do txt[#txt + 1] = fmt(r[1] - w0) .. "-" .. fmt(r[2] - w0) .. " m" end
        err(l, "geometry", "the join between " .. names .. " opens " .. fmt(lo - w0) .. "-" .. fmt(hi - w0) .. " m along " .. Hz.id .. "'s " .. side ..
          " wall (measured from its corner), outside the openings that wall takes (" .. (#txt > 0 and table.concat(txt, ", ") or "none") .. ").")
        return
      end
      local w = wall_coord(Hz, side)
      local u = used_of(Hz.id, side)
      u[#u + 1] = { lo = lo, hi = hi, y0 = bot, y1 = top }
      add_port(Hz, { side = side, lo = lo, hi = hi, y0 = Hz.pos[2], y1 = top, link = l, wall = w, floor = Hz.pos[2], join = true,
        pt = wall_point(Hz, side, (lo + hi) / 2), nrm = { DIRV[side][1], DIRV[side][2] } })
      local p0, p1, pm = wall_point(Hz, side, lo), wall_point(Hz, side, hi), wall_point(Hz, side, (lo + hi) / 2)
      local ax, az = to_world(rot, p0[1], p0[2])
      local bx, bz = to_world(rot, p1[1], p1[2])
      local mx, mz = to_world(rot, pm[1], pm[2])
      local nx, nz = to_world(rot, DIRV[side][1], DIRV[side][2])
      -- the poly's edge nearest the opening carries its port
      local qp = poly_of(Q)
      local edge, ed = 1, math.huge
      for i = 1, #qp do local d = pt_seg({ mx, mz }, qp[i], qp[i % #qp + 1]) if d < ed then edge, ed = i, d end end
      add_port(Q, { side = edge, seg = { { ax, az }, { bx, bz } }, pt = { mx, mz }, nrm = { -nx, -nz }, lo = 0, hi = hi - lo, y0 = Q.pos[2], y1 = top,
        link = l, floor = Q.pos[2], join = true })
      if Hz == za then P.side_a, P.side_b = side, edge else P.side_a, P.side_b = edge, side end
      P.ext = { zone = Hz, axis = (side == "+x" or side == "-x") and "x" or "z", w0 = w - 0.5, w1 = w + 0.5, lo = lo, hi = hi }
      P.span = hi - lo
      P.ok = true
      return
    end
    -- two poly zones: shared stretches of antiparallel edges on one line, else the boundary of one inside the other
    local pa, pb = poly_of(za), poly_of(zb)
    local segs = {}
    for i = 1, #pa do
      local a, _, t, L, n = poly_edge(pa, i)
      for j = 1, #pb do
        local c, d, u = poly_edge(pb, j)
        if t[1] * u[1] + t[2] * u[2] < -0.9998 then
          local dc = math.abs((c[1] - a[1]) * n[1] + (c[2] - a[2]) * n[2])
          local dd = math.abs((d[1] - a[1]) * n[1] + (d[2] - a[2]) * n[2])
          if dc < 0.05 and dd < 0.05 then
            local sc = (c[1] - a[1]) * t[1] + (c[2] - a[2]) * t[2]
            local sd = (d[1] - a[1]) * t[1] + (d[2] - a[2]) * t[2]
            local s0, s1 = math.max(0, math.min(sc, sd)), math.min(L, math.max(sc, sd))
            if s1 - s0 > 0.1 then segs[#segs + 1] = { i = i, j = j, s0 = s0, s1 = s1, a = a, t = t, n = n } end
          end
        end
      end
    end
    if #segs == 0 then
      if poly_dist((wpoly(za)), (wpoly(zb))) > 0 then apart() return end
      local B = wpoly(zb)
      for i = 1, #pa do
        local a, b, t, _, n = poly_edge(pa, i)
        for _, iv in ipairs(seg_inside(B, a, b)) do segs[#segs + 1] = { i = i, s0 = iv[1], s1 = iv[2], a = a, t = t, n = n, overlap = true } end
      end
    end
    local total = 0
    for _, s in ipairs(segs) do total = total + (s.s1 - s.s0) end
    if total < 3 - EPS then if total <= 0 then apart() else narrow(total) end return end
    for _, s in ipairs(segs) do
      local p0 = { s.a[1] + s.t[1] * s.s0, s.a[2] + s.t[2] * s.s0 }
      local p1 = { s.a[1] + s.t[1] * s.s1, s.a[2] + s.t[2] * s.s1 }
      local pm = { (p0[1] + p1[1]) / 2, (p0[2] + p1[2]) / 2 }
      local n = s.n
      local ua = used_of(za.id, s.i)
      ua[#ua + 1] = { lo = s.s0, hi = s.s1, y0 = bot, y1 = top }
      add_port(za, { side = s.i, seg = { p0, p1 }, pt = pm, nrm = { n[1], n[2] }, lo = s.s0, hi = s.s1, y0 = za.pos[2], y1 = top, link = l, floor = za.pos[2], join = true })
      local j = s.j
      if not j then
        j, s.jd = 1, math.huge
        for k = 1, #pb do local d = pt_seg(pm, pb[k], pb[k % #pb + 1]) if d < s.jd then j, s.jd = k, d end end
      end
      local c, _, u = poly_edge(pb, j)
      local l0 = (p0[1] - c[1]) * u[1] + (p0[2] - c[2]) * u[2]
      local l1 = (p1[1] - c[1]) * u[1] + (p1[2] - c[2]) * u[2]
      local ub = used_of(zb.id, j)
      ub[#ub + 1] = { lo = math.min(l0, l1), hi = math.max(l0, l1), y0 = bot, y1 = top }
      add_port(zb, { side = j, seg = { p0, p1 }, pt = pm, nrm = { -n[1], -n[2] }, lo = math.min(l0, l1), hi = math.max(l0, l1), y0 = zb.pos[2], y1 = top,
        link = l, floor = zb.pos[2], join = true })
      if not s.overlap then
        -- an air strip across the shared line, half a metre into each zone (the two outlines need not meet exactly)
        P.ext_quads[#P.ext_quads + 1] = ccw({ { p0[1] - n[1] * 0.5, p0[2] - n[2] * 0.5 }, { p1[1] - n[1] * 0.5, p1[2] - n[2] * 0.5 },
          { p1[1] + n[1] * 0.5, p1[2] + n[2] * 0.5 }, { p0[1] + n[1] * 0.5, p0[2] + n[2] * 0.5 } })
      end
    end
    P.side_a, P.side_b = segs[1].i, segs[1].j
    P.span = total
    P.ok = true
  end

  -- Free links ------------------------------------------------------------------------------------
  local function bands_meet(s, band) return s.y0 < band[2] + 0.5 and s.y1 > band[1] - 0.5 end

  -- The walls (rectangle zones) or edges (poly zones) of z that take a w m opening at port level off: {side, ivs}
  -- with ivs the intervals its centre may take (along the wall in the zone's frame, along the edge from its start).
  local function openings(z, off, W, band, only)
    local out = {}
    if z.archetype == "poly" then
      if not has_level(z, off) then return out end
      local p = poly_of(z)
      for i = 1, #p do
        if only == nil or only == i then
          local _, _, _, L = poly_edge(p, i)
          if L > 1.5 + EPS then
            local free = { { 0.75, L - 0.75 } }
            for _, s in ipairs(used_of(z.id, i)) do if bands_meet(s, band) then free = subtract(free, s.lo - 0.75, s.hi + 0.75) end end
            local ivs = {}
            for _, s in ipairs(free) do if s[2] - s[1] >= W - EPS then ivs[#ivs + 1] = { s[1] + W / 2, s[2] - W / 2 } end end
            if #ivs > 0 then out[#out + 1] = { side = i, ivs = ivs } end
          end
        end
      end
      return out
    end
    for _, side in ipairs(SIDES) do
      if only == nil or only == side then
        local free = ranges_of(z, side, off)
        for _, s in ipairs(used_of(z.id, side)) do if bands_meet(s, band) then free = subtract(free, s.lo - 1.5, s.hi + 1.5) end end
        local ivs = {}
        for _, s in ipairs(free) do if s[2] - s[1] >= W - EPS then ivs[#ivs + 1] = { s[1] + W / 2, s[2] - W / 2 } end end
        if #ivs > 0 then out[#out + 1] = { side = side, ivs = ivs } end
      end
    end
    return out
  end

  -- The world point, world outward normal, local point and local normal of the opening at c on (z, side).
  local function port_geo(z, side, c)
    if z.archetype == "poly" then
      local a, _, t, _, n = poly_edge(poly_of(z), side)
      local pt = { a[1] + t[1] * c, a[2] + t[2] * c }
      return pt, n, pt, n
    end
    local lp = wall_point(z, side, c)
    local rot = zone_rot(z)
    local wx, wz = to_world(rot, lp[1], lp[2])
    local nx, nz = to_world(rot, DIRV[side][1], DIRV[side][2])
    return { wx, wz }, { nx, nz }, lp, { DIRV[side][1], DIRV[side][2] }
  end
  -- The along-wall (or along-edge) coordinate of world point q's projection on (z, side).
  local function project(z, side, q)
    if z.archetype == "poly" then
      local a, _, t = poly_edge(poly_of(z), side)
      return (q[1] - a[1]) * t[1] + (q[2] - a[2]) * t[2]
    end
    local lx, lz = to_local(zone_rot(z), q[1], q[2])
    if side == "+x" or side == "-x" then return lz end
    return lx
  end
  local function nearest(ivs, c)
    local best, bd = nil, math.huge
    for _, iv in ipairs(ivs) do
      local v = clamp(c, iv[1], iv[2])
      if math.abs(v - c) < bd then best, bd = v, math.abs(v - c) end
    end
    return best
  end
  local function place(z, o, q)
    local c = nearest(o.ivs, snapv(project(z, o.side, q), 16))
    local pt, n, lp, ln = port_geo(z, o.side, c)
    return { side = o.side, ivs = o.ivs, c = c, pt = pt, n = n, lp = lp, ln = ln }
  end
  local function ranked(z, opts, at, tgt)
    local list = {}
    for _, o in ipairs(opts) do
      local k = place(z, o, at or tgt)
      if at then
        k.score = -math.sqrt((at[1] - k.pt[1]) ^ 2 + (at[2] - k.pt[2]) ^ 2)
      else
        local dx, dz = tgt[1] - k.pt[1], tgt[2] - k.pt[2]
        local L = math.sqrt(dx * dx + dz * dz)
        k.score = L > 1e-9 and (dx * k.n[1] + dz * k.n[2]) / L or 0
      end
      list[#list + 1] = k
    end
    -- best facing first (a stable insertion: the walls keep their order on ties)
    for i = 2, #list do
      local c, j = list[i], i - 1
      while j >= 1 and list[j].score < c.score - 1e-9 do list[j + 1] = list[j] j = j - 1 end
      list[j + 1] = c
    end
    return list
  end
  local function pt2(v) if type(v) == "table" then return { v[1] or v.x, v[2] or v.z } end return nil end
  local function snapp(p) return { snapv(p[1], 1024), snapv(p[2], 1024) } end
  local function ahead(p, n, k) return snapp({ p[1] + n[1] * k, p[2] + n[2] * k }) end

  -- What a free link's plan would run into: other zones (closer than a wall), its own zones away from its ports,
  -- and other links on the same storey.
  -- (broad phase: the route's box and each quad's box keep the polygon tests to what lies within a wall of it)
  local function conflicts(P, M, band, trim_a, trim_b)
    local nq = #M.quads
    local qbs = {}
    local rb = nil
    for k, q in ipairs(M.quads) do
      local b = bbox_box(q)
      qbs[k] = b
      if not rb then rb = { b[1], b[2], b[3], b[4] }
      else rb[1], rb[2], rb[3], rb[4] = math.min(rb[1], b[1]), math.min(rb[2], b[2]), math.max(rb[3], b[3]), math.max(rb[4], b[4]) end
    end
    local need0 = DATA.min_wall - 0.01
    for _, z in ipairs(graph.zones) do
      local zl, zh = zone_band(z)
      if band[1] < zh + DATA.min_wall and band[2] > zl - DATA.min_wall then
        local zp, zb_ = wpoly(z)
        if box_gap(rb, zb_) < need0 + 1e-9 then
          local own_a, own_b = z == P.za, z == P.zb
          for k, q in ipairs(M.quads) do
            local first, last = own_a and k == 1, own_b and k == nq
            local poly, need, pb = q, need0, qbs[k]
            if first or last then
              -- the stub leaving the wall: only coming back into the zone counts
              poly, need = trim_quad(M, k, first and (trim_a or 0.05) or 0, last and (trim_b or 0.05) or 0), 1e-3
              pb = nil
            end
            if poly_near(poly, zp, need, pb, zb_) then
              if own_a or own_b then return "it runs back into its own zone " .. z.id .. " or within " .. fmt(DATA.min_wall) .. " m of it" end
              return "it enters zone " .. z.id .. " or passes within " .. fmt(DATA.min_wall) .. " m of it"
            end
          end
        end
      end
    end
    for _, f in ipairs(plan.foot) do
      if f.link ~= P.link and band[1] < f.band[2] + DATA.min_wall and band[2] > f.band[1] - DATA.min_wall then
        local fb = f.box
        if not fb then fb = bbox_box(f.poly) f.box = fb end
        if box_gap(rb, fb) < need0 + 1e-9 then
          for k, q in ipairs(M.quads) do
            if poly_near(q, f.poly, need0, qbs[k], fb) then
              return "it crosses link " .. tostring(f.id) .. " or runs within " .. fmt(DATA.min_wall) .. " m of it on the same storey"
            end
          end
        end
      end
    end
    return nil
  end

  local function free(P)
    local l, za, zb = P.link, P.za, P.zb
    P.mode, P.free = "free", true
    if not FREE_TYPES[l.type] then
      local why = (za.archetype == "poly" or zb.archetype == "poly") and "a poly zone takes free links only" or
        (not same_rot(zone_rot(za), zone_rot(zb))) and "the zones' rot differs" or "the link gives a path or a port side"
      err(l, "geometry", "a " .. l.type .. " cannot be a free link (" .. why .. "): drops, lifts and windows join two facing walls of rectangle zones of one rot; free links are corridors, doorways, roads, bridges, stairs and ramps.")
      return
    end
    local open_air = graph.class == "open_air"
    local straight = (l.type == "bridge") or open_air
    local W = port_width(l)
    local hw = W / 2
    P.W = W
    local ya, yb = link_ys(l, za, zb)
    P.ya, P.yb = ya, yb
    local oa, ob = ya - za.pos[2], yb - zb.pos[2]
    P.ba, P.bb = port_bands(l, za, zb, graph)
    local H = link_height(l, graph)
    local band = { math.min(ya, yb), math.max(ya, yb) + H }
    if l.type == "bridge" and not open_air then band[1] = band[1] - (l.depth or DATA.chasm_depth) end
    P.band = band
    local explicit = l.path ~= nil
    local wps = {}
    if type(l.path) == "table" then for i, q in ipairs(l.path) do wps[i] = pt2(q) end end
    local at_a, at_b = pt2(l.a_at), pt2(l.b_at)
    local cax, caz = centre_of(za)
    local cbx, cbz = centre_of(zb)
    local tgt_a = wps[1] or at_b or { cbx, cbz }
    local tgt_b = wps[#wps] or at_a or { cax, caz }
    local opts_a = openings(za, oa, W, P.ba, l.a_side)
    local opts_b = openings(zb, ob, W, P.bb, l.b_side)
    for _, e in ipairs({ { opts_a, za, oa, l.a_side }, { opts_b, zb, ob, l.b_side } }) do
      if #e[1] == 0 then
        local where = (type(e[4]) == "number") and (" edge " .. e[4]) or (e[4] and (" " .. e[4] .. " wall") or "")
        err(l, "geometry", "the port has no room: " .. e[2].id .. "'s" .. (where ~= "" and where or " walls") .. " take no free " .. fmt(W) ..
          " m opening at level +" .. fmt(e[3]) .. " m (corners, other ports and the archetype's own openings are kept clear).")
        return
      end
    end
    local rise = math.abs(ya - yb)
    local run = (l.type == "stair") and stair_run(rise) or ((l.type == "ramp") and ramp_run(rise) or 0)
    local s = hw + DATA.min_wall
    local function aligned(ka, kb)
      local dx, dz = kb.pt[1] - ka.pt[1], kb.pt[2] - ka.pt[2]
      return ka.n[1] * kb.n[1] + ka.n[2] * kb.n[2] < -0.9999 and dx * ka.n[1] + dz * ka.n[2] > 0.5 and math.abs(dx * ka.n[2] - dz * ka.n[1]) < 0.02
    end
    -- Line two ports on facing parallel walls up across the gap (a straight link square to both walls).
    local function align(ka, kb)
      local spans = {}
      for _, iv in ipairs(kb.ivs) do
        local p0 = port_geo(zb, kb.side, iv[1])
        local p1 = port_geo(zb, kb.side, iv[2])
        local u0, u1 = project(za, ka.side, p0), project(za, ka.side, p1)
        spans[#spans + 1] = { math.min(u0, u1), math.max(u0, u1) }
      end
      local ov = intersect(ka.ivs, spans)
      if #ov == 0 then return nil end
      local c = nearest(ov, ka.c)
      local snapped = snapv(c, 16)
      for _, iv in ipairs(ov) do if snapped >= iv[1] - EPS and snapped <= iv[2] + EPS then c = snapped end end
      local pt, n, lp, ln = port_geo(za, ka.side, c)
      local a2 = { side = ka.side, ivs = ka.ivs, c = c, pt = pt, n = n, lp = lp, ln = ln }
      local b2 = place(zb, kb, pt)
      if aligned(a2, b2) then return a2, b2 end
      -- (a turned wall: the opening square across from it, off the 1/16 m steps along the wall)
      local cb = nearest(kb.ivs, project(zb, kb.side, pt))
      local pb, nb, lpb, lnb = port_geo(zb, kb.side, cb)
      b2 = { side = kb.side, ivs = kb.ivs, c = cb, pt = pb, n = nb, lp = lpb, ln = lnb }
      if aligned(a2, b2) then return a2, b2 end
      return nil
    end
    local function routes(ka, kb)
      local Pa, na, Pb, nb = ka.pt, ka.n, kb.pt, kb.n
      if straight then return { { Pa, Pb } } end
      local function on_ray(p, n, q)
        local dx, dz = q[1] - p[1], q[2] - p[2]
        return dx * n[1] + dz * n[2] >= 1 - 1e-6 and math.abs(dx * n[2] - dz * n[1]) < 1e-3
      end
      if explicit then
        if #wps == 0 then
          if aligned(ka, kb) then return { { Pa, Pb } } end
          return { { Pa, ahead(Pa, na, s), ahead(Pb, nb, s), Pb } }
        end
        -- (a waypoint within a metre of a stub's end is that stub's end, e.g. a path written back by a resolve)
        local function far_from(p, q) return (p[1] - q[1]) ^ 2 + (p[2] - q[2]) ^ 2 >= 1 end
        local pts = { Pa }
        if not on_ray(Pa, na, wps[1]) and far_from(ahead(Pa, na, s), wps[1]) then pts[#pts + 1] = ahead(Pa, na, s) end
        for _, q in ipairs(wps) do pts[#pts + 1] = q end
        if not on_ray(Pb, nb, wps[#wps]) and far_from(ahead(Pb, nb, s), wps[#wps]) then pts[#pts + 1] = ahead(Pb, nb, s) end
        pts[#pts + 1] = Pb
        return { pts }
      end
      local out = {}
      if aligned(ka, kb) then out[#out + 1] = { Pa, Pb } end
      local A1, B1 = ahead(Pa, na, s), ahead(Pb, nb, s)
      local dx, dz = B1[1] - A1[1], B1[2] - A1[2]
      local dl = math.sqrt(dx * dx + dz * dz)
      local straight2, turning = nil, 0
      if dl >= 1 then
        local d = { dx / dl, dz / dl }
        local ta, tb = angle_of(na, d), angle_of(d, { -nb[1], -nb[2] })
        if ta <= 60 + EPS and tb <= 60 + EPS then straight2, turning = { Pa, A1, B1, Pb }, ta + tb end
      end
      -- one bend where the two stub lines meet
      local bend = nil
      local den = na[1] * nb[2] - na[2] * nb[1]
      if math.abs(den) > 1e-6 then
        local ex, ez = Pb[1] - Pa[1], Pb[2] - Pa[2]
        local t = (ex * nb[2] - ez * nb[1]) / den
        local u = (ex * na[2] - ez * na[1]) / den
        if t >= s - EPS and u >= s - EPS then bend = { Pa, ahead(Pa, na, t), Pb } end
      end
      -- the straight run first, unless it zigzags (turns much more than the single bend would)
      if straight2 and bend and turning > angle_of(na, { -nb[1], -nb[2] }) + 10 then out[#out + 1] = bend out[#out + 1] = straight2
      else
        if straight2 then out[#out + 1] = straight2 end
        if bend then out[#out + 1] = bend end
      end
      -- dog-legs: out along each normal, then across
      local D = (Pb[1] - Pa[1]) * na[1] + (Pb[2] - Pa[2]) * na[2]
      local E = (Pa[1] - Pb[1]) * nb[1] + (Pa[2] - Pb[2]) * nb[2]
      for _, t in ipairs({ s, D / 2, D - s, s + 6 }) do
        for _, u in ipairs({ s, E / 2, E - s, s + 6 }) do
          if t >= s - EPS and u >= s - EPS then out[#out + 1] = { Pa, ahead(Pa, na, t), ahead(Pb, nb, u), Pb } end
        end
      end
      return out
    end
    local function evaluate(ka, kb, pts)
      -- segment lengths and turns first (their messages name the path's own fault), then the mitred shape
      local dirs = {}
      for i = 1, #pts - 1 do
        local dx, dz = pts[i + 1][1] - pts[i][1], pts[i + 1][2] - pts[i][2]
        local L = math.sqrt(dx * dx + dz * dz)
        if L < 1 - EPS then return nil, "segment " .. i .. " is only " .. fmt(L) .. " m long (at least 1 m)" end
        dirs[i] = { dx / L, dz / L }
      end
      for i = 2, #pts - 1 do
        local t = angle_of(dirs[i - 1], dirs[i])
        if t > 90 + 0.01 then return nil, "it turns " .. fmt(t) .. " degrees at path point " .. (i - 1) .. " (at most 90: add a waypoint to split the turn)" end
      end
      local M, why = mitre(pts, hw)
      if not M then return nil, why end
      if straight then
        local d = M.dirs[1]
        local ta, tb = angle_of(ka.n, d), angle_of(kb.n, { -d[1], -d[2] })
        if ta > 45 + EPS or tb > 45 + EPS then
          return nil, "it leaves " .. (ta > 45 + EPS and za.id or zb.id) .. "'s wall " .. fmt(math.max(ta, tb)) .. " degrees off square (a " .. l.type .. " runs straight, at most 45 degrees off square)"
        end
        local need = 2
        if l.type == "bridge" then need = math.max(4, rise * 1.2 + 2) elseif l.type == "ramp" then need = rise + 2 end
        if M.lens[1] < need - EPS then return nil, "its " .. fmt(M.lens[1]) .. " m span is too short for a " .. l.type .. " (needs " .. fmt(need) .. " m)" end
        for _, e in ipairs({ { ka, ta }, { kb, tb } }) do
          local extra = hw / math.cos(math.rad(e[2])) - hw
          local fits = false
          for _, iv in ipairs(e[1].ivs) do if e[1].c - extra >= iv[1] - EPS and e[1].c + extra <= iv[2] + EPS then fits = true end end
          if not fits then return nil, "its opening widens to " .. fmt(W + 2 * extra) .. " m at the oblique crossing, which its wall does not take there" end
        end
      elseif run > 0 then
        local best = 0
        for _, st in ipairs(M.straight) do best = math.max(best, st[2] - st[1]) end
        if best < run + 1 - EPS then
          return nil, "its longest straight segment is " .. fmt(best) .. " m, too short for the " .. fmt(run) .. " m " .. l.type .. " (it needs " .. fmt(run + 1) .. " m)"
        end
      end
      return M
    end
    -- the plan test of a route that has the right shape
    local function clear_of(ka, kb, M)
      local trim_a, trim_b = 0.05, 0.05
      if straight then
        local d = M.dirs[1]
        trim_a = 0.05 + hw * math.tan(math.rad(angle_of(ka.n, d)))
        trim_b = 0.05 + hw * math.tan(math.rad(angle_of(kb.n, { -d[1], -d[2] })))
      end
      return conflicts(P, M, band, trim_a, trim_b)
    end
    local la = ranked(za, opts_a, at_a, tgt_a)
    local lb = ranked(zb, opts_b, at_b, tgt_b)
    local pairs_ = {}
    local na_, nb_ = math.min(explicit and 1 or 3, #la), math.min(explicit and 1 or 3, #lb)
    for i = 1, na_ do for j = 1, nb_ do pairs_[#pairs_ + 1] = { i, j } end end
    for i = 2, #pairs_ do
      local c, j = pairs_[i], i - 1
      while j >= 1 and (pairs_[j][1] + pairs_[j][2] > c[1] + c[2]) do pairs_[j + 1] = pairs_[j] j = j - 1 end
      pairs_[j + 1] = c
    end
    local chosen, why1, whyc = nil, nil, nil
    for _, pr in ipairs(pairs_) do
      local ka, kb = la[pr[1]], lb[pr[2]]
      if not explicit then
        if not at_b then kb = place(zb, kb, ka.pt) end
        if not at_a then ka = place(za, ka, kb.pt) end
        if not at_a and not at_b and ka.n[1] * kb.n[1] + ka.n[2] * kb.n[2] < -0.9999 then
          local a2, b2 = align(ka, kb)
          if a2 then ka, kb = a2, b2 end
        end
      end
      if straight then
        -- an oblique crossing widens the opening: keep each port that much further from its range's ends
        for _ = 1, 3 do
          local dx, dz = kb.pt[1] - ka.pt[1], kb.pt[2] - ka.pt[2]
          local L = math.sqrt(dx * dx + dz * dz)
          if L < 1e-6 then break end
          local d = { dx / L, dz / L }
          local function refit(k, z, dir)
            local th = math.min(60, angle_of(k.n, dir))
            local extra = hw / math.cos(math.rad(th)) - hw
            local ivs = {}
            for _, iv in ipairs(k.ivs) do if iv[2] - iv[1] >= 2 * extra then ivs[#ivs + 1] = { iv[1] + extra, iv[2] - extra } end end
            if #ivs == 0 then return k end
            local c = nearest(ivs, k.c)
            if math.abs(c - k.c) < 1e-6 then return k end
            local pt, n, lp, ln = port_geo(z, k.side, c)
            return { side = k.side, ivs = k.ivs, c = c, pt = pt, n = n, lp = lp, ln = ln }
          end
          ka = refit(ka, za, d)
          kb = refit(kb, zb, { -d[1], -d[2] })
        end
      end
      -- port variants: as placed, and each port moved to where its stub line meets the other's (one clean bend);
      -- of the routes that work, the one that turns least (a bend counts 10 degrees) wins
      local variants = { { ka, kb } }
      -- (ports placed near given points on facing parallel walls: also lined up square across the gap, which a plan
      -- moved or turned and snapped to the grid needs)
      if not explicit and (at_a or at_b) and ka.n[1] * kb.n[1] + ka.n[2] * kb.n[2] < -0.9999 and not aligned(ka, kb) then
        local a2, b2 = align(ka, kb)
        if a2 then variants[#variants + 1] = { a2, b2 } end
      end
      if not explicit and not straight then
        local D = math.sqrt((kb.pt[1] - ka.pt[1]) ^ 2 + (kb.pt[2] - ka.pt[2]) ^ 2)
        for _, f in ipairs({ 1, 0.5 }) do
          if not at_a then variants[#variants + 1] = { place(za, ka, { kb.pt[1] + kb.n[1] * D * f, kb.pt[2] + kb.n[2] * D * f }), kb } end
          if not at_b then variants[#variants + 1] = { ka, place(zb, kb, { ka.pt[1] + ka.n[1] * D * f, ka.pt[2] + ka.n[2] * D * f }) } end
        end
      end
      local cands = {}
      -- (a route the port variants repeat is measured once: the first of equals is the one taken)
      local seen = {}
      for _, v in ipairs(variants) do
        for _, pts in ipairs(routes(v[1], v[2])) do
          local key = {}
          for i, q in ipairs(pts) do key[i] = string.format("%.17g %.17g", q[1], q[2]) end
          key = table.concat(key, ";")
          if not seen[key] then
            seen[key] = true
            local M, why = evaluate(v[1], v[2], pts)
            if M then
              local score = 10 * (#pts - 2)
              for i = 2, #pts - 1 do score = score + M.turns[i] end
              cands[#cands + 1] = { v[1], v[2], pts, M, score, #cands }
            end
            why1 = why1 or why
          end
        end
      end
      -- stable insertion by score (few candidates)
      for i = 2, #cands do
        local c, j = cands[i], i - 1
        while j >= 1 and cands[j][5] > c[5] + 1e-6 do cands[j + 1] = cands[j] j = j - 1 end
        cands[j + 1] = c
      end
      for _, c in ipairs(cands) do
        -- (a trial route has a budget of plan tests: P.budget)
        if P.budget then
          P.budget = P.budget - 1
          if P.budget < 0 then break end
        end
        local why = clear_of(c[1], c[2], c[4])
        if not why then chosen = c break end
        whyc = whyc or why
      end
      if chosen or (P.budget and P.budget < 0) then break end
    end
    if not chosen then
      why1 = whyc or why1
      if explicit then
        err(l, "geometry", "its path from " .. za.id .. " to " .. zb.id .. " does not work: " .. (why1 or "no route") .. ".")
      else
        err(l, "geometry", "no clear route for the " .. l.type .. " from " .. za.id .. " to " .. zb.id .. ": " .. (why1 or "no route") ..
          (straight and "; a " .. l.type .. " here runs straight from wall to wall: move the zones so two of their walls face each other." or
            "; give it a path (waypoints {x, z} in world metres) around what is in the way, or move the zones."))
      end
      return
    end
    local ka, kb, pts, M = chosen[1], chosen[2], chosen[3], chosen[4]
    P.pts, P.M, P.hw, P.length = pts, M, hw, M.length
    P.side_a, P.side_b = ka.side, kb.side
    P.straight = straight
    local function half_of(k, d)
      if not straight then return hw end
      return hw / math.cos(math.rad(angle_of(k.n, d)))
    end
    local d1, dn = M.dirs[1], M.dirs[#M.dirs]
    local function port(z, k, half, bnd, floor)
      local p = { side = k.side, lo = k.c - half, hi = k.c + half, y0 = bnd[1], y1 = bnd[2], link = l, floor = floor, free = true,
        pt = k.lp, nrm = k.ln, world = k.pt, wnrm = k.n }
      if z.archetype == "poly" then
        local _, _, t = poly_edge(poly_of(z), k.side)
        p.seg = { { k.pt[1] - t[1] * half, k.pt[2] - t[2] * half }, { k.pt[1] + t[1] * half, k.pt[2] + t[2] * half } }
      else
        p.wall = wall_coord(z, k.side)
      end
      add_port(z, p)
      local u = used_of(z.id, k.side)
      u[#u + 1] = { lo = p.lo, hi = p.hi, y0 = bnd[1], y1 = bnd[2] }
      return p
    end
    P.pa = port(za, ka, half_of(ka, d1), P.ba, ya)
    P.pb = port(zb, kb, half_of(kb, { -dn[1], -dn[2] }), P.bb, yb)
    for _, q in ipairs(M.quads) do plan.foot[#plan.foot + 1] = { poly = q, band = band, link = l, id = l.id } end
    P.ok = true
    if keep then
      l.a_side, l.b_side = ka.side, kb.side
      l.a_at, l.b_at = { ka.pt[1], ka.pt[2] }, { kb.pt[1], kb.pt[2] }
      local inner = {}
      for i = 2, #pts - 1 do inner[#inner + 1] = { pts[i][1], pts[i][2] } end
      -- (a straight route keeps no path: its ports line up again wherever the plan is moved or turned)
      if #inner > 0 or l.path ~= nil then l.path = inner end
    end
  end

  local X = { graph = graph, plan = plan, used = used, join = join, free = free }
  -- A link that is not a join: classic, true when it needs a free route after all (the classic geometry failed on
  -- a free-method graph, or it is free to begin with).
  function X.classic(P)
    local l, za, zb = P.link, P.za, P.zb
    local rect = za.archetype ~= "poly" and zb.archetype ~= "poly"
    if rect and same_rot(zone_rot(za), zone_rot(zb)) and not wants_free(l) then
      if graph.method == "free" and FREE_TYPES[l.type] then return not classic(P, false) end
      classic(P, true)
      return false
    end
    return true
  end
  -- One link on its own: a join, a classic link, or (failing the classic geometry on a free graph) a free route.
  function X.one(P)
    if not (P.za and P.zb and P.za ~= P.zb) then return end
    if P.link.type == "join" then join(P) return end
    if X.classic(P) then free(P) end
  end
  return X
end

local function resolve(graph, rng, keep)
  local X = resolver(graph, rng, keep)
  local plan = X.plan
  -- Joins, then classic links, then free links (each in list order).
  for i, l in ipairs(graph.links or {}) do
    local za, zb = plan.zones[l.a], plan.zones[l.b]
    plan.links[i] = { link = l, za = za, zb = zb, index = i }
  end
  for _, P in ipairs(plan.links) do
    if P.za and P.zb and P.za ~= P.zb and P.link.type == "join" then X.join(P) end
  end
  local frees = {}
  for _, P in ipairs(plan.links) do
    if P.za and P.zb and P.za ~= P.zb and P.link.type ~= "join" and X.classic(P) then frees[#frees + 1] = P end
  end
  for _, P in ipairs(frees) do X.free(P) end
  return plan, X
end

-- A routing context: the graph resolved once (resolve without rng, keep off), to route single links against.
local function route_ctx(graph)
  local _, X = resolve(graph, nil, false)
  return X
end

-- Routes one more link l against a routing context, as resolve would route it after the context's links: returns
-- P (the plan entry: mode, ok, ports, pts, length ...) and the error messages for it. Unless it is dropped with
-- route_drop, the link stays in the context (its ports, wall spans and footprint keep later links clear). budget:
-- at most this many candidate routes are tested against the plan (a trial that gives up early reports no route).
local function route_link(X, l, budget)
  local plan = X.plan
  local function zone(id)
    local z = plan.zones[id]
    if z then return z end
    for _, q in ipairs(X.graph.zones) do
      if q.id == id then plan.zones[id] = q plan.ports[id] = plan.ports[id] or {} return q end
    end
    return nil
  end
  local za, zb = zone(l.a), zone(l.b)
  local P = { link = l, za = za, zb = zb, index = #plan.links + 1, budget = budget }
  local snap = { errors = #plan.errors, foot = #plan.foot, ports = {}, used = {} }
  for _, z in ipairs({ za, zb }) do
    if z then
      snap.ports[z.id] = #plan.ports[z.id]
      local u = X.used[z.id]
      local lens = {}
      if u then for side, list in pairs(u) do lens[side] = #list end end
      snap.used[z.id] = lens
    end
  end
  P.snap = snap
  X.one(P)
  plan.links[#plan.links + 1] = P
  local errs = {}
  for i = snap.errors + 1, #plan.errors do errs[#errs + 1] = plan.errors[i].message end
  return P, errs
end

-- Takes the last link routed with route_link back out of its context.
local function route_drop(X, P)
  local plan, snap = X.plan, P.snap
  if plan.links[#plan.links] == P then plan.links[#plan.links] = nil end
  local function cut(list, n) for i = #list, n + 1, -1 do list[i] = nil end end
  cut(plan.errors, snap.errors)
  cut(plan.foot, snap.foot)
  for id, n in pairs(snap.ports) do cut(plan.ports[id], n) end
  for id, lens in pairs(snap.used) do
    local u = X.used[id]
    if u then for side, list in pairs(u) do cut(list, lens[side] or 0) end end
  end
end

-- ---------------------------------------------------------------------------------------------
-- Graph analysis (zone graph of the walk links)
-- ---------------------------------------------------------------------------------------------

local function index_zones(graph)
  local idx, list = {}, {}
  for i, z in ipairs(graph.zones) do idx[z.id] = i list[i] = z end
  return idx, list
end

-- Undirected adjacency of the walk links: adj[i] = list of {j, link}.
local function adjacency(graph, idx)
  local adj = {}
  for i = 1, #graph.zones do adj[i] = {} end
  for _, l in ipairs(graph.links) do
    local a, b = idx[l.a], idx[l.b]
    if a and b and a ~= b and WALK[l.type] then
      adj[a][#adj[a] + 1] = { b, l }
      adj[b][#adj[b] + 1] = { a, l }
    end
  end
  return adj
end

local function components(n, adj, skip)
  local comp, k = {}, 0
  for s = 1, n do
    if comp[s] == nil and s ~= skip then
      k = k + 1
      local stack = { s }
      comp[s] = k
      while #stack > 0 do
        local u = table.remove(stack)
        for _, e in ipairs(adj[u]) do
          local v = e[1]
          if v ~= skip and comp[v] == nil then comp[v] = k stack[#stack + 1] = v end
        end
      end
    end
  end
  return comp, k
end

-- Node-disjoint route count between zones s and t (unit node capacities, parallel links count apart).
local function disjoint_routes(n, adj, s, t, cap)
  if s == t then return cap end
  -- Node split: in = i, out = n + i. Edge capacities in a dense table keyed "u,v".
  local capm, nbr = {}, {}
  local function add(u, v, c)
    local k = u .. "," .. v
    if capm[k] == nil then
      capm[k] = 0
      nbr[u] = nbr[u] or {}
      nbr[u][#nbr[u] + 1] = v
      nbr[v] = nbr[v] or {}
      nbr[v][#nbr[v] + 1] = u
      if capm[v .. "," .. u] == nil then capm[v .. "," .. u] = 0 end
    end
    capm[k] = capm[k] + c
  end
  for i = 1, n do add(i, n + i, (i == s or i == t) and cap or 1) end
  local ec = 2 * n
  for u = 1, n do
    for _, e in ipairs(adj[u]) do
      local v = e[1]
      if u < v or true then
        -- each undirected link once per direction through its own edge node so parallel links count apart
        ec = ec + 1
        add(n + u, ec, 1)
        add(ec, v, 1)
      end
    end
  end
  local flow = 0
  local src, dst = n + s, t
  while flow < cap do
    local prev = { [src] = src }
    local queue, head = { src }, 1
    while head <= #queue and prev[dst] == nil do
      local u = queue[head]
      head = head + 1
      for _, v in ipairs(nbr[u] or {}) do
        if prev[v] == nil and capm[u .. "," .. v] > 0 then prev[v] = u queue[#queue + 1] = v end
      end
    end
    if prev[dst] == nil then break end
    local v = dst
    while v ~= src do
      local u = prev[v]
      capm[u .. "," .. v] = capm[u .. "," .. v] - 1
      capm[v .. "," .. u] = capm[v .. "," .. u] + 1
      v = u
    end
    flow = flow + 1
  end
  return flow
end

-- Articulation zones: for each, the area cut off behind it (all but its biggest remaining part).
local function articulation(graph, idx, adj)
  local n = #graph.zones
  local out = {}
  local _, base = components(n, adj, nil)
  for v = 1, n do
    local comp, k = components(n, adj, v)
    if k > base then
      local area = {}
      for i = 1, n do if i ~= v and comp[i] then area[comp[i]] = (area[comp[i]] or 0) + zone_area(graph.zones[i]) end end
      local big, total = 0, 0
      for c = 1, k do big = math.max(big, area[c] or 0) total = total + (area[c] or 0) end
      local behind = {}
      local bigc = nil
      for c = 1, k do if (area[c] or 0) == big and bigc == nil then bigc = c end end
      for i = 1, n do if i ~= v and comp[i] and comp[i] ~= bigc then behind[#behind + 1] = graph.zones[i].id end end
      out[#out + 1] = { zone = graph.zones[v].id, cut = total - big, behind = behind }
    end
  end
  return out
end

-- Shortest walk distances (zone centre to centre along links) from zone s.
local function walk_dist(graph, adj, s)
  local n = #graph.zones
  local d, done = {}, {}
  for i = 1, n do d[i] = math.huge end
  d[s] = 0
  for _ = 1, n do
    local u, best = nil, math.huge
    for i = 1, n do if not done[i] and d[i] < best then u, best = i, d[i] end end
    if not u then break end
    done[u] = true
    local zu = graph.zones[u]
    for _, e in ipairs(adj[u]) do
      local zv = graph.zones[e[1]]
      local dx, dy, dz = zv.pos[1] - zu.pos[1], zv.pos[2] - zu.pos[2], zv.pos[3] - zu.pos[3]
      local w = math.sqrt(dx * dx + dy * dy + dz * dz)
      if d[u] + w < d[e[1]] then d[e[1]] = d[u] + w end
    end
  end
  return d
end

local function layout_area(graph, plan)
  local a = 0
  for _, z in ipairs(graph.zones) do a = a + zone_area(z) end
  if plan then
    for _, P in ipairs(plan.links) do
      if P.mode == "free" and P.ok and WALK[P.link.type] then
        a = a + P.length * (P.link.type == "bridge" and P.W - 1 or P.W)
      elseif P.mode ~= "free" and P.f and P.W and WALK[P.link.type] then
        a = a + P.f.gap * (P.link.type == "bridge" and P.W - 1 or P.W)
      end
    end
  end
  return a
end

local function spawn_total(graph)
  local n = 0
  for _, o in ipairs(graph.objectives or {}) do if o.kind == "spawns" then n = n + (o.count or 0) end end
  return n
end

-- The distinct floor levels (zone floors, port levels, sunken courts), merged within 1 m (2 m on open-air layouts,
-- whose platforms float at varied heights within a storey).
local function floor_levels(graph)
  local tol = (graph.class == "open_air") and 2 or 1
  local ys = {}
  for _, z in ipairs(graph.zones) do
    for _, off in ipairs(z.levels or { 0 }) do ys[#ys + 1] = z.pos[2] + off end
    local A = LA[z.archetype]
    if A and A.extra_levels then for _, off in ipairs(A.extra_levels(z)) do ys[#ys + 1] = z.pos[2] + off end end
  end
  table.sort(ys)
  local out = {}
  for _, y in ipairs(ys) do if #out == 0 or y - out[#out] > tol then out[#out + 1] = y end end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- kit.check_layout
-- ---------------------------------------------------------------------------------------------

-- The bridge links that serve no route (the checker's intent rule, the grader's G27 reading of a redundant bridge):
-- measured on the zone graph (zone centres, walkable links), a bridge whose zones the other links already join with a
-- walk at most 1.25x their straight distance + 8 m. Payload layouts want several routes over each boundary (P05), so
-- parallel crossings there are routes in their own right. Returns {link, walk, direct}[] in link order.
local function redundant_bridges(graph)
  local out = {}
  if graph.gametype == "payload" then return out end
  local byid = {}
  for _, z in ipairs(graph.zones) do byid[z.id] = z end
  local function dist(a, b)
    local ax, az = centre_of(a)
    local bx, bz = centre_of(b)
    local dx, dy, dz = ax - bx, (a.pos[2] or 0) - (b.pos[2] or 0), az - bz
    return math.sqrt(dx * dx + dy * dy + dz * dz)
  end
  for _, br in ipairs(graph.links) do
    if br.type == "bridge" and byid[br.a] and byid[br.b] then
      local d = { [br.a] = 0 }
      local done, via = {}, {}
      while true do
        local u, du = nil, math.huge
        for k, v in pairs(d) do if not done[k] and v < du then u, du = k, v end end
        if u == nil or u == br.b then break end
        done[u] = true
        for _, l in ipairs(graph.links) do
          local parallel = (l.a == br.a and l.b == br.b) or (l.a == br.b and l.b == br.a)
          if l ~= br and not parallel and WALK[l.type or ""] and (l.a == u or l.b == u) then
            local v = l.a == u and l.b or l.a
            if byid[v] then
              local nd = du + dist(byid[u], byid[v])
              if d[v] == nil or nd < d[v] then d[v], via[v] = nd, { l, u } end
            end
          end
        end
      end
      local direct = dist(byid[br.a], byid[br.b])
      if d[br.b] ~= nil and d[br.b] <= 1.25 * direct + 8 then
        -- (the other route, as its links)
        local path, v = {}, br.b
        while via[v] do path[#path + 1] = via[v][1] v = via[v][2] end
        out[#out + 1] = { link = br, walk = d[br.b], direct = direct, path = path }
      end
    end
  end
  return out
end

-- A composed graph's bridges, settled before it is routed. The generators compose crossings by geometry; a bridge the
-- redundant-bridge rule would flag (the other links already join its zones almost as directly) is settled, one at a
-- time, the first way that works:
--   * it goes, when both of its zones keep two other ways in;
--   * a bridge on the short way round goes instead, when both of that bridge's zones keep two ways in and the graph is
--     left with fewer redundant bridges (in a triangle of crossings, the flagged one is then the only way across);
--   * it becomes the climb it really is: a stair in the bridge's own route, between floors a level or more apart;
--   * else it stays, and the checker judges the graph.
local function settle_bridges(G)
  local byid = {}
  for _, z in ipairs(G.zones) do byid[z.id] = z end
  local function degrees(without)
    local deg = {}
    for _, o in ipairs(G.links) do
      if o ~= without and WALK[o.type or ""] then deg[o.a] = (deg[o.a] or 0) + 1 deg[o.b] = (deg[o.b] or 0) + 1 end
    end
    return deg
  end
  local function without(link)
    local kept = {}
    for _, o in ipairs(G.links) do if o ~= link then kept[#kept + 1] = o end end
    return kept
  end
  local keep = {}
  for _ = 1, #G.links do
    local list = redundant_bridges(G)
    local r = nil
    for _, e in ipairs(list) do if not keep[e.link] then r = e break end end
    if not r then break end
    local l = r.link
    local deg = degrees(l)
    local settled = false
    if (deg[l.a] or 0) >= 2 and (deg[l.b] or 0) >= 2 then
      G.links = without(l)
      settled = true
    else
      for _, q in ipairs(r.path or {}) do
        if q.type == "bridge" then
          local dq = degrees(q)
          if (dq[q.a] or 0) >= 2 and (dq[q.b] or 0) >= 2 then
            local saved = G.links
            G.links = without(q)
            if #redundant_bridges(G) < #list then settled = true break end
            G.links = saved
          end
        end
      end
    end
    if not settled then
      local ya, yb = link_ys(l, byid[l.a], byid[l.b])
      if math.abs(ya - yb) >= (G.step or DATA.step) - 0.5 - EPS then
        l.type = "stair"
        l.style = nil
      else
        keep[l] = true
      end
    end
  end
  for i, l in ipairs(G.links) do l.id = "l" .. i end
end

local function violation(list, severity, rule, msg, zone, link)
  list[#list + 1] = { severity = severity, rule = rule, message = msg, zone = zone, link = link }
end

local function check(graph)
  local V = {}
  if type(graph) ~= "table" or type(graph.zones) ~= "table" or type(graph.links) ~= "table" then
    violation(V, "error", "schema", "the graph must be a table with zones and links (a kit.layout result).")
    return V, nil
  end
  graph.objectives = graph.objectives or {}
  local step = graph.step or DATA.step
  -- Schema.
  local ids = {}
  for i, z in ipairs(graph.zones) do
    if type(z) ~= "table" or type(z.id) ~= "string" then violation(V, "error", "schema", "zone " .. i .. " needs a string id.")
    else
      if ids[z.id] then violation(V, "error", "schema", "zone id " .. z.id .. " is used twice.", z.id) end
      ids[z.id] = z
      local A = LA[z.archetype or ""]
      local poly_bad = nil
      if A and A.poly then
        -- a traced zone: its outline is the plan; pos x/z and size follow from it when missing
        local o = z.outline
        if type(o) ~= "table" or #o < 3 or #o > 64 then poly_bad = "needs outline = 3 to 64 world points {x, z}"
        else
          for k, q in ipairs(o) do
            if type(q) ~= "table" or type(q[1] or q.x) ~= "number" or type(q[2] or q.z) ~= "number" then poly_bad = "outline[" .. k .. "] must be a point {x, z}" break end
          end
        end
        local p = (poly_bad == nil) and poly_of(z) or nil
        if p then
          for k = 1, #p do
            local q, r = p[k], p[k % #p + 1]
            if math.abs(q[1] - r[1]) + math.abs(q[2] - r[2]) < 0.25 then poly_bad = "outline points " .. k .. " and " .. (k % #p + 1) .. " are closer than 0.25 m" break end
          end
          if not poly_bad and not simple(p) then poly_bad = "the outline is not a simple polygon (its edges cross or touch; list the points in order around the plan)" end
          if not poly_bad and math.abs(area2(p)) < 16 then poly_bad = "the outline encloses less than 16 m2" end
        end
        if poly_bad == nil and (type(z.pos) ~= "table" or type(z.pos[2]) ~= "number") then poly_bad = "needs pos = {x, floor, z} (the floor height; x and z may be left out)" end
        if poly_bad == nil then
          local x0, z0, x1, z1 = bbox(p)
          if type(z.pos[1]) ~= "number" then z.pos[1] = (x0 + x1) / 2 end
          if type(z.pos[3]) ~= "number" then z.pos[3] = (z0 + z1) / 2 end
          if type(z.size) ~= "table" then z.size = { x1 - x0, z1 - z0 } end
          if z.rot ~= nil and norm_rot(z.rot) ~= 0 then poly_bad = "takes no rot (its outline is in world coordinates)" end
        end
      end
      if A ~= nil and z.rot ~= nil and type(z.rot) ~= "number" then
        violation(V, "error", "schema", "zone " .. z.id .. ": rot must be a yaw in degrees (got " .. tostring(z.rot) .. ").", z.id)
      end
      if A == nil then violation(V, "error", "schema", "zone " .. z.id .. ": unknown archetype '" .. tostring(z.archetype) .. "' (kit.layout_archetypes{} lists them).", z.id)
      elseif poly_bad then
        violation(V, "error", "schema", "zone " .. z.id .. " (poly) " .. poly_bad .. ".", z.id)
      elseif type(z.pos) ~= "table" or type(z.size) ~= "table" or type(z.pos[1]) ~= "number" or type(z.size[2]) ~= "number" then
        violation(V, "error", "schema", "zone " .. z.id .. " needs pos = {x, y, z} and size = {w, d}.", z.id)
      else
        local mn = A.dims and A.dims(z, A.min) or A.min
        if not A.poly and (z.size[1] < mn[1] - EPS or z.size[2] < mn[2] - EPS) then
          violation(V, "error", "size", "zone " .. z.id .. " (" .. z.archetype .. ") is " .. fmt(z.size[1]) .. " x " .. fmt(z.size[2]) ..
            " m; the archetype needs at least " .. fmt(mn[1]) .. " x " .. fmt(mn[2]) .. " m" ..
            (A.dims and (" (heading " .. heading_of(z) .. ")") or "") .. ".", z.id)
        end
        if z.params ~= nil and type(z.params) ~= "table" then
          violation(V, "error", "schema", "zone " .. z.id .. ": params must be a table.", z.id)
        elseif z.params and z.params.heading ~= nil and not DIRV[z.params.heading] then
          violation(V, "error", "schema", "zone " .. z.id .. ": params.heading must be \"+x\", \"-x\", \"+z\" or \"-z\" (got " .. tostring(z.params.heading) .. ").", z.id)
        end
        if z.levels == nil then
          -- the archetype's port levels; a graded road also takes ports at its top floor
          z.levels = {}
          for _, lv in ipairs(A.levels) do z.levels[#z.levels + 1] = (lv == "step") and step or lv end
          if z.archetype == "road" and type(z.params) == "table" and (z.params.rise or 0) ~= 0 then z.levels = { 0, z.params.rise } end
        end
        -- a hand-made zone without a height or chamfer gets its archetype's (as kit.layout gives them)
        if z.height == nil and A.height then z.height = A.height(z, step) end
        if z.chamfer == nil then
          if type(A.chamfer) == "function" then z.chamfer = A.chamfer(z) else z.chamfer = A.chamfer or 0 end
        end
      end
    end
  end
  for i, l in ipairs(graph.links) do
    if type(l) ~= "table" then violation(V, "error", "schema", "link " .. i .. " must be a table.")
    else
      l.id = l.id or ("l" .. i)
      if not LINK_TYPES[l.type or ""] then violation(V, "error", "schema", "link " .. l.id .. ": unknown type '" .. tostring(l.type) ..
        "' (corridor, doorway, stair, ramp, drop, bridge, window, lift, road, join).", nil, l.id) end
      if not ids[l.a or ""] or not ids[l.b or ""] then violation(V, "error", "schema", "link " .. l.id .. " joins unknown zones (" ..
        tostring(l.a) .. ", " .. tostring(l.b) .. ").", nil, l.id)
      elseif l.a == l.b then violation(V, "error", "schema", "link " .. l.id .. " joins zone " .. l.a .. " to itself.", nil, l.id)
      else
        -- free-link fields: path = waypoints {x, z}; a_side / b_side = a wall ("+x" ...) or a poly zone's edge number;
        -- a_at / b_at = a world point {x, z} the port sits near
        local function point_ok(q) return type(q) == "table" and type(q[1] or q.x) == "number" and type(q[2] or q.z) == "number" end
        if l.path ~= nil then
          local ok = type(l.path) == "table" and #l.path <= 64
          if ok then for _, q in ipairs(l.path) do if not point_ok(q) then ok = false end end end
          if not ok then violation(V, "error", "schema", "link " .. l.id .. ": path must list up to 64 waypoints {x, z} (world metres).", nil, l.id) end
        end
        for _, e in ipairs({ { "a_side", l.a_side, ids[l.a] }, { "b_side", l.b_side, ids[l.b] } }) do
          if e[2] ~= nil then
            local z = e[3]
            if z.archetype == "poly" then
              local np = type(z.outline) == "table" and #z.outline or 0
              if type(e[2]) ~= "number" or e[2] ~= math.floor(e[2]) or e[2] < 1 or e[2] > np then
                violation(V, "error", "schema", "link " .. l.id .. ": " .. e[1] .. " must be an edge number 1-" .. np .. " of poly zone " .. z.id .. " (edge i runs from outline point i to i + 1).", nil, l.id)
              end
            elseif not DIRV[e[2]] then
              violation(V, "error", "schema", "link " .. l.id .. ": " .. e[1] .. " must be \"+x\", \"-x\", \"+z\" or \"-z\" (zone " .. z.id .. "'s own walls).", nil, l.id)
            end
          end
        end
        for _, e in ipairs({ { "a_at", l.a_at }, { "b_at", l.b_at } }) do
          if e[2] ~= nil and not point_ok(e[2]) then violation(V, "error", "schema", "link " .. l.id .. ": " .. e[1] .. " must be a world point {x, z}.", nil, l.id) end
        end
      end
    end
  end
  for _, v in ipairs(V) do if v.severity == "error" then return V, nil end end

  -- Geometry: plans apart, links resolvable, levels valid.
  local zs = graph.zones
  local joined = {}
  for _, l in ipairs(graph.links) do if l.type == "join" then joined[l.a .. "\0" .. l.b] = true joined[l.b .. "\0" .. l.a] = true end end
  local wp, wb = {}, {}
  for i, z in ipairs(zs) do wp[i] = zone_poly(z) wb[i] = bbox_box(wp[i]) end
  for i = 1, #zs do
    for j = i + 1, #zs do
      local a, b = zs[i], zs[j]
      local m = (LA[a.archetype].open_air and LA[b.archetype].open_air) and 2 or DATA.min_wall
      local close = false
      if a.archetype ~= "poly" and b.archetype ~= "poly" and zone_rot(a) == 0 and zone_rot(b) == 0 then
        local ax0, az0, ax1, az1 = rect_of(a)
        local bx0, bz0, bx1, bz1 = rect_of(b)
        close = ax0 < bx1 + m - EPS and bx0 < ax1 + m - EPS and az0 < bz1 + m - EPS and bz0 < az1 + m - EPS
      else
        close = poly_near(wp[i], wp[j], m - EPS, wb[i], wb[j])
      end
      if close and not joined[a.id .. "\0" .. b.id] then
        violation(V, "error", "geometry", "zones " .. a.id .. " and " .. b.id .. " overlap or stand closer than " .. fmt(m) .. " m.", a.id)
      end
    end
  end
  for _, l in ipairs(graph.links) do
    local za, zb = ids[l.a], ids[l.b]
    if l.type ~= "join" then
    local oa, ob = port_offsets(l, za, zb)
    if l.type ~= "drop" and l.type ~= "window" and not has_level(za, oa) then
      violation(V, "error", "level", "link " .. l.id .. ": a_y " .. fmt(za.pos[2] + oa) .. " is not a port level of " .. za.id ..
        " (floor " .. fmt(za.pos[2]) .. ", levels +" .. table.concat(za.levels, ", +") .. ").", nil, l.id)
    end
    if l.type ~= "window" and not has_level(zb, ob) then
      violation(V, "error", "level", "link " .. l.id .. ": b_y " .. fmt(zb.pos[2] + ob) .. " is not a port level of " .. zb.id ..
        " (floor " .. fmt(zb.pos[2]) .. ", levels +" .. table.concat(zb.levels, ", +") .. ").", nil, l.id)
    end
    local lya, lyb = link_ys(l, za, zb)
    local rise = math.abs(lya - lyb)
    if (l.type == "corridor" or l.type == "doorway" or l.type == "road") and rise > DATA.max_step + EPS then
      violation(V, "error", "level", "link " .. l.id .. " is a " .. l.type .. " between floors " .. fmt(rise) .. " m apart (a step of at most " ..
        DATA.max_step .. " m at the door): use a stair, ramp, drop or lift.", nil, l.id)
    end
    if (l.type == "stair" or l.type == "ramp" or l.type == "lift") and rise < 1 then
      violation(V, "error", "level", "link " .. l.id .. " is a " .. l.type .. " with no rise: use a corridor.", nil, l.id)
    end
    if l.type == "drop" then
      local ya, yb = lya, lyb
      if ya <= yb + 1 then violation(V, "error", "level", "link " .. l.id .. " is a drop but a (" .. fmt(ya) .. ") is not above b (" .. fmt(yb) .. ").", nil, l.id) end
      if ya - yb > DATA.drop_max + EPS then violation(V, "error", "level", "link " .. l.id .. " drops " .. fmt(ya - yb) .. " m (at most " .. DATA.drop_max .. ").", nil, l.id) end
      if ya + link_height(l, graph) > zb.pos[2] + (zb.height or 0) + EPS then
        violation(V, "error", "level", "link " .. l.id .. ": " .. zb.id .. " (height " .. fmt(zb.height or 0) .. " m) is too low for a drop opening " ..
          fmt(ya - zb.pos[2]) .. " m up; drop into a tall zone (atrium, gallery, split, courtyard, yard).", nil, l.id)
      end
    end
    if l.type == "bridge" and not (LA[za.archetype].open_air and LA[zb.archetype].open_air) then
      -- Outside open-air layouts a bridge spans a chasm cut to the sky between two floors at one level: payload only
      -- (a cart bridge, or a footbridge beside the track).
      if graph.gametype ~= "payload" or graph.class == "open_air" then
        violation(V, "error", "type", "link " .. l.id .. " is a bridge between zones that are not both open-air platforms (bridges over a chasm are for payload layouts).", nil, l.id)
      elseif rise > DATA.max_step + EPS then
        violation(V, "error", "level", "link " .. l.id .. " is a chasm bridge between floors " .. fmt(rise) .. " m apart; a bridge over a chasm joins floors at one level (a step of at most " ..
          DATA.max_step .. " m).", nil, l.id)
      end
    end
    if rise > step + ((graph.class == "open_air") and 2 or 1) + EPS and l.type ~= "window" and l.type ~= "drop" then
      violation(V, "error", "level", "link " .. l.id .. " climbs " .. fmt(rise) .. " m; links span one level (" .. fmt(step) .. " m) at most.", nil, l.id)
    end
    end
  end
  -- The intent layer: a bridge link joins two zones that are otherwise worse-connected. Measured on the zone graph
  -- (zone centres, walkable links): when the other links already join its zones with a walk at most 1.25x the
  -- straight distance + 8 m, the bridge serves no route (the grader's G27 reading of a redundant bridge).
  for _, r in ipairs(redundant_bridges(graph)) do
    local br = r.link
    violation(V, "rule", "purpose", "link " .. br.id .. " is a bridge between " .. br.a .. " and " .. br.b .. ", which the other links already join with a " ..
      fmt(r.walk) .. " m walk (" .. fmt(r.direct) .. " m apart): a bridge must join places that are otherwise far apart; drop it or join zones the layout keeps apart.", nil, br.id)
  end
  local plan = resolve(graph, nil, false)
  for _, e in ipairs(plan.errors) do V[#V + 1] = e end
  for _, v in ipairs(V) do if v.severity == "error" then return V, plan end end

  -- Design rules (c.1-1).
  local idx = index_zones(graph)
  local adj = adjacency(graph, idx)
  local n = #zs
  local area = layout_area(graph, plan)
  for _, P in ipairs(plan.links) do
    if P.mode == "free" and P.ok and (P.link.type == "corridor" or P.link.type == "doorway") and P.length > 60 + EPS then
      violation(V, "rule", "link", "link " .. P.link.id .. " is a " .. fmt(P.length) .. " m " .. P.link.type ..
        " (free corridors run at most 60 m: put a zone on the way).", nil, P.link.id)
    end
  end
  local nooks = 0
  for i, z in ipairs(zs) do
    local deg = #adj[i]
    if z.role == "nook" then
      nooks = nooks + 1
      if deg < 1 then violation(V, "rule", "degree", "nook " .. z.id .. " has no walk link.", z.id) end
    elseif deg < 2 then
      violation(V, "rule", "degree", "zone " .. z.id .. " has " .. deg .. " walk link(s): every zone needs at least 2 (no dead ends), except reward nooks.", z.id)
    end
  end
  local nook_max = math.max(1, math.floor(area / DATA.nook_area))
  if nooks > nook_max then
    violation(V, "rule", "nooks", nooks .. " reward nooks in " .. fmt(area) .. " m2: at most one per " .. DATA.nook_area .. " m2 (" .. nook_max .. ").")
  end
  local _, ncomp = components(n, adj, nil)
  if ncomp > 1 then violation(V, "rule", "connected", "the walk links leave " .. ncomp .. " separate parts.") end
  for _, a in ipairs(articulation(graph, idx, adj)) do
    if a.cut >= DATA.articulation_share * area - EPS then
      violation(V, "rule", "articulation", "zone " .. a.zone .. " is the only way to " .. table.concat(a.behind, ", ") .. " (" .. fmt(a.cut) ..
        " m2, " .. string.format("%.0f", 100 * a.cut / area) .. "% of the area): add a second route around it.", a.zone)
    end
  end
  local edges, internal = 0, 0
  for _, l in ipairs(graph.links) do if WALK[l.type] then edges = edges + 1 end end
  for _, z in ipairs(zs) do internal = internal + (z.loops or LA[z.archetype].loops or 0) end
  local rank = edges - n + ncomp + internal
  local per = (graph.size_class == "L") and DATA.cycle_per_1000_L or DATA.cycle_per_1000
  -- payload: a linear track whose legs each loop (grader P03 per segment), so half the density over the whole map
  if graph.gametype == "payload" then per = 0.5 end
  local need_rank = per * area / 1000
  if rank < need_rank - EPS then
    violation(V, "rule", "loops", "cycle rank " .. rank .. " (" .. (edges - n + ncomp) .. " graph loops + " .. internal .. " inside zones) is below " ..
      string.format("%.1f", need_rank) .. " for " .. fmt(area) .. " m2 (" .. per .. " per 1,000 m2): add links that close loops.")
  end
  -- Hubs and objectives: two node-disjoint routes between every pair.
  local key = {}
  for i, z in ipairs(zs) do if z.role == "hub" or z.role == "hill" or z.role == "base" then key[#key + 1] = i end end
  for _, o in ipairs(graph.objectives) do
    if (o.kind == "flag" or o.kind == "hill" or o.kind == "checkpoint") and idx[o.zone] then
      local dup = false
      for _, k in ipairs(key) do if k == idx[o.zone] then dup = true end end
      if not dup then key[#key + 1] = idx[o.zone] end
    end
  end
  for a = 1, #key do
    for b = a + 1, #key do
      local r = disjoint_routes(n, adj, key[a], key[b], 2)
      if r < 2 then
        violation(V, "rule", "routes", "zones " .. zs[key[a]].id .. " and " .. zs[key[b]].id .. " (hubs/objectives) have " .. r ..
          " node-disjoint route(s); they need 2.", zs[key[a]].id)
      end
    end
  end
  -- Floor levels.
  local levels = floor_levels(graph)
  local need_levels = graph.gametype == "payload" and 2 or 3
  if #levels < need_levels then
    violation(V, "rule", "levels", #levels .. " floor level(s) (" .. table.concat(levels, ", ") .. "): a layout needs at least " .. need_levels .. ".")
  end
  for i = 2, #levels do
    local s = levels[i] - levels[i - 1]
    if s < DATA.level_spacing[1] - 0.26 or s > DATA.level_spacing[2] + EPS then
      if graph.gametype ~= "payload" then
        violation(V, "rule", "levels", "floor levels " .. fmt(levels[i - 1]) .. " and " .. fmt(levels[i]) .. " are " .. fmt(s) ..
          " m apart (storeys sit 4.5-10 m apart).")
      end
    end
  end
  -- Vertical links of 2+ kinds between adjacent levels (zone stairs inside multi-level archetypes count as "stair").
  local tol = (graph.class == "open_air") and 2 or 1
  for i = 2, #levels do
    local lo, hi = levels[i - 1], levels[i]
    local kinds, nk = {}, 0
    local function add(k) if not kinds[k] then kinds[k] = true nk = nk + 1 end end
    for _, l in ipairs(graph.links) do
      local za, zb = ids[l.a], ids[l.b]
      local ya, yb = link_ys(l, za, zb)
      if WALK[l.type] and math.abs(math.min(ya, yb) - lo) < tol and math.abs(math.max(ya, yb) - hi) < tol and math.abs(ya - yb) > 0.75 then add(l.type) end
    end
    for _, z in ipairs(zs) do
      local A = LA[z.archetype]
      local zl = {}
      for _, off in ipairs(z.levels) do zl[#zl + 1] = z.pos[2] + off end
      if A.extra_levels then for _, off in ipairs(A.extra_levels(z)) do zl[#zl + 1] = z.pos[2] + off end end
      local has_lo, has_hi = false, false
      for _, y in ipairs(zl) do if math.abs(y - lo) < tol then has_lo = true end if math.abs(y - hi) < tol then has_hi = true end end
      if has_lo and has_hi then
        if z.archetype == "split" or z.archetype == "tower" or z.archetype == "terrace" then add("stair") add("ramp")
        elseif z.archetype == "mound" then add("stair") add("ramp")
        elseif z.archetype == "sunken" then add("stair") add("drop")
        else add("stair") end
      end
    end
    if nk < 2 and hi - lo <= DATA.level_spacing[2] + EPS and graph.gametype ~= "payload" then
      violation(V, "rule", "vertical", "levels " .. fmt(lo) .. " and " .. fmt(hi) .. " are joined by " .. nk ..
        " kind(s) of vertical link; use at least 2 kinds (stair, ramp, drop, lift, bridge).")
    end
  end
  -- Archetype repetition (mirrored twins count once; spine pieces and nooks are exempt).
  local count = {}
  for _, z in ipairs(zs) do
    if not z.mirror_of and not LA[z.archetype].repeatable then count[z.archetype] = (count[z.archetype] or 0) + 1 end
  end
  for _, name in ipairs(LORDER) do
    if (count[name] or 0) > DATA.repeat_max then
      violation(V, "rule", "variety", "archetype " .. name .. " is used " .. count[name] .. " times (at most " .. DATA.repeat_max .. ").")
    end
  end
  for i, z in ipairs(zs) do
    if not LA[z.archetype].repeatable then
      local same = 0
      for _, e in ipairs(adj[i]) do if zs[e[1]].archetype == z.archetype and zs[e[1]].mirror_of ~= z.id and z.mirror_of ~= zs[e[1]].id then same = same + 1 end end
      if same >= 2 then violation(V, "rule", "variety", "zone " .. z.id .. " and two of its neighbours are all " .. z.archetype .. " (three in a row).", z.id) end
    end
  end
  -- Mirror symmetry: team modes only, and keyed by palette.
  local team = graph.gametype == "ctf" or graph.gametype == "koth" or graph.gametype == "payload"
  local cx = 0
  for _, z in ipairs(zs) do cx = cx + z.pos[1] end
  cx = cx / math.max(1, n)
  local mirrored, pairs_ok = n > 2, true
  for _, z in ipairs(zs) do
    local found = false
    for _, o in ipairs(zs) do
      if o.archetype == z.archetype and math.abs((2 * cx - z.pos[1]) - o.pos[1]) < 1.5 and math.abs(z.pos[3] - o.pos[3]) < 1.5 and
        ang_diff(zone_rot(o), -zone_rot(z)) < 1 then
        found = true
        if o ~= z and team and o.palette and z.palette and o.palette.team == z.palette.team and o.palette.theme == z.palette.theme and
          o.palette.variant == z.palette.variant then pairs_ok = false end
      end
    end
    if not found then mirrored = false end
  end
  if mirrored and not team then
    violation(V, "rule", "symmetry", "the layout is mirror-symmetric; mirror symmetry is for team modes only.")
  elseif mirrored and not pairs_ok then
    violation(V, "rule", "symmetry", "mirrored zones share a palette; key the two halves by palette (team = red / blue).")
  end

  -- Gametype rules.
  local function obj(kind, team_) local o = {} for _, x in ipairs(graph.objectives) do if x.kind == kind and (team_ == nil or x.team == team_) then o[#o + 1] = x end end return o end
  if graph.gametype == "ctf" then
    local rf, bf = obj("flag", "red")[1], obj("flag", "blue")[1]
    if not rf or not bf or not idx[rf.zone] or not idx[bf.zone] then
      violation(V, "rule", "ctf", "a CTF layout needs a red and a blue flag objective in zones.")
    else
      local ra, ba = idx[rf.zone], idx[bf.zone]
      local lanes = disjoint_routes(n, adj, ra, ba, 3)
      if lanes < 3 then violation(V, "rule", "ctf", "the bases " .. rf.zone .. " and " .. bf.zone .. " are joined by " .. lanes .. " lane(s); CTF needs 3.", rf.zone) end
      local za, zb = zs[ra], zs[ba]
      local dx, dz = za.pos[1] - zb.pos[1], za.pos[3] - zb.pos[3]
      local dist = math.sqrt(dx * dx + dz * dz)
      if dist < DATA.ctf_flags[1] or dist > DATA.ctf_flags[2] then
        violation(V, "rule", "ctf", "the flags are " .. fmt(dist) .. " m apart (" .. DATA.ctf_flags[1] .. "-" .. DATA.ctf_flags[2] .. " m).", rf.zone)
      end
      -- A lane at another height or through a flank tunnel.
      local varied = false
      for _, z in ipairs(zs) do
        if z.lane ~= nil and (math.abs(z.pos[2] - za.pos[2]) > 1 or LA[z.archetype].flank) then varied = true end
      end
      if not varied then violation(V, "rule", "ctf", "no lane runs at another height or through a flank tunnel.") end
      -- Walk balance: each team's spawns to the enemy flag.
      local dR, dB = walk_dist(graph, adj, ba), walk_dist(graph, adj, ra)
      local function mean(team_, d)
        local s, k = 0, 0
        for _, o in ipairs(obj("spawns", team_)) do if idx[o.zone] then s = s + d[idx[o.zone]] * (o.count or 1) k = k + (o.count or 1) end end
        return k > 0 and s / k or nil
      end
      local mr, mb = mean("red", dR), mean("blue", dB)
      if mr and mb and mr < math.huge and mb < math.huge then
        local bal = math.min(mr, mb) / math.max(mr, mb)
        if bal < DATA.ctf_balance then violation(V, "rule", "ctf", "spawn-to-enemy-flag walk balance " .. string.format("%.2f", bal) .. " (at least " .. DATA.ctf_balance .. ").") end
      else
        violation(V, "rule", "ctf", "both teams need spawn groups that reach the enemy flag.")
      end
      for _, f in ipairs({ rf, bf }) do
        local zi = idx[f.zone]
        local hs, nh, ex = {}, 0, 0
        for _, P in ipairs(plan.links) do
          if P.pa and WALK[P.link.type] then
            local y = nil
            if P.za == zs[zi] then y = P.pa.floor elseif P.zb == zs[zi] then y = P.pb.floor end
            if y then
              ex = ex + 1
              local k = string.format("%.0f", y)
              if not hs[k] then hs[k] = true nh = nh + 1 end
            end
          end
        end
        if ex < 3 or nh < 2 then
          violation(V, "rule", "ctf", "base " .. f.zone .. " has " .. ex .. " exit(s) on " .. nh .. " height(s); each base needs 3 exits on 2 heights.", f.zone)
        end
      end
    end
  elseif graph.gametype == "koth" then
    local h = obj("hill")[1]
    if not h or not idx[h.zone] then violation(V, "rule", "koth", "a KOTH layout needs a hill objective in a zone.")
    else
      local hz = zs[idx[h.zone]]
      local ap = #adj[idx[h.zone]]
      if ap < DATA.koth_approaches[1] or ap > DATA.koth_approaches[2] then
        violation(V, "rule", "koth", "the hill " .. h.zone .. " has " .. ap .. " approaches (" .. DATA.koth_approaches[1] .. "-" .. DATA.koth_approaches[2] .. ").", h.zone)
      end
      local relief = (hz.params and hz.params.depth) or 0
      if not LA[hz.archetype].hill or relief < DATA.koth_relief[1] - EPS or relief > DATA.koth_relief[2] + EPS then
        violation(V, "rule", "koth", "the hill zone " .. h.zone .. " must be sunken or raised " .. DATA.koth_relief[1] .. "-" .. DATA.koth_relief[2] ..
          " m (archetype sunken or mound).", h.zone)
      end
    end
  elseif graph.gametype == "payload" then
    local cps = obj("checkpoint")
    local nseg = #cps
    if nseg < DATA.payload_segments[1] or nseg > DATA.payload_segments[2] then
      violation(V, "rule", "payload", nseg .. " checkpoints; a payload layout has " .. DATA.payload_segments[1] .. "-" .. DATA.payload_segments[2] .. ".")
    end
    -- Boundaries: at least 3 links from each segment into the next.
    for s = 1, nseg - 1 do
      local cross = 0
      for _, l in ipairs(graph.links) do
        local za, zb = ids[l.a], ids[l.b]
        if WALK[l.type] and za.segment and zb.segment and math.min(za.segment, zb.segment) == s and math.max(za.segment, zb.segment) == s + 1 then cross = cross + 1 end
      end
      if cross < DATA.payload_boundary_routes then
        violation(V, "rule", "payload", "the boundary after segment " .. s .. " has " .. cross .. " entry route(s); every gate needs the cart route plus a flank and a high route (3).")
      end
    end
    local cart = obj("cart")[1]
    if not cart then violation(V, "rule", "payload", "a payload layout needs a cart objective (its spine zones in order).")
    else
      local turned, uphill = false, false
      local prev = nil
      local spine = cart.zones or {}
      -- The world outward direction of a zone's wall (or a poly zone's edge).
      local function side_dir(z, side)
        if type(side) == "number" then local _, _, _, _, nn = poly_edge(poly_of(z), side) return nn end
        local x, zz = to_world(zone_rot(z), DIRV[side][1], DIRV[side][2])
        return { x, zz }
      end
      -- The cart's way between two consecutive spine zones: a road, bridge, doorway, corridor or join between them;
      -- its ports give the side the cart leaves one zone by and enters the next by (any heading).
      local sides = {}
      for i = 1, #spine do sides[i] = {} end
      for i = 1, #spine - 1 do
        local a, b = spine[i], spine[i + 1]
        if ids[a] and ids[b] then
          local found = nil
          for _, P in ipairs(plan.links) do
            local l = P.link
            if P.ok and CART_LINK[l.type] and ((l.a == a and l.b == b) or (l.a == b and l.b == a)) then found = P break end
          end
          if not found then
            violation(V, "rule", "payload", "the cart cannot go from " .. a .. " to " .. b .. ": consecutive cart zones need a road, bridge, doorway, corridor or join between them.", a)
          else
            local fwd = found.link.a == a
            sides[i].exit = fwd and found.side_a or found.side_b
            sides[i + 1].entry = fwd and found.side_b or found.side_a
            -- a free road that bends turns the track
            if found.mode == "free" and found.M and ang_diff(yaw_of(found.M.dirs[1][1], found.M.dirs[1][2]),
              yaw_of(found.M.dirs[#found.M.dirs][1], found.M.dirs[#found.M.dirs][2])) >= 20 - EPS then turned = true end
          end
        end
      end
      for i, id in ipairs(spine) do
        local z = ids[id]
        local sd = sides[i] or {}
        if z and z.archetype == "road" then
          local F = road_frame(z)
          local run = F.len - F.ls - F.ln
          local g = run > 0 and math.abs(F.rise) / run or 0
          if g > DATA.payload_max_grade + EPS then
            violation(V, "rule", "payload", "road " .. id .. " climbs " .. fmt(math.abs(F.rise)) .. " m over its " .. fmt(run) .. " m graded stretch (" .. fmt(F.len) ..
              " m less the " .. fmt(F.ls) .. " and " .. fmt(F.ln) .. " m level pads): " .. string.format("%.0f", 100 * g) .. "% (at most 15%).", id)
          end
          if F.rise > 0 then uphill = true end
          for _, e in ipairs({ { "entry", "enters", F.entry }, { "exit", "leaves", F.exit } }) do
            if sd[e[1]] and sd[e[1]] ~= e[3] then
              violation(V, "rule", "payload", "the cart " .. e[2] .. " road " .. id .. " by its " .. tostring(sd[e[1]]) .. " side, but a road heading " .. F.heading .. " is " ..
                (e[1] == "entry" and "entered" or "left") .. " by its " .. e[3] .. " end: set the road's params.heading to the way the cart travels.", id)
            end
          end
          -- world heading and centre line
          local rot = zone_rot(z)
          local hx, hz = to_world(rot, DIRV[F.heading][1], DIRV[F.heading][2])
          local cx, cz = to_world(rot, F.xz(0, F.centre))
          local yaw = yaw_of(hx, hz)
          if prev then
            local off = math.abs((cx - prev.cx) * hz - (cz - prev.cz) * hx)
            if ang_diff(prev.yaw, yaw) >= 20 - EPS or off > 3 then turned = true end
          end
          prev = { yaw = yaw, cx = cx, cz = cz }
        elseif z then
          -- a zone the cart crosses at an angle turns the track
          if sd.entry and sd.exit then
            local en, ex = side_dir(z, sd.entry), side_dir(z, sd.exit)
            if angle_of({ -en[1], -en[2] }, ex) >= 20 - EPS then turned = true end
          end
          if z.archetype == "finale" and sd.entry and sd.entry ~= OPP[heading_of(z)] then
            violation(V, "rule", "payload", "the cart enters the finale " .. id .. " by its " .. tostring(sd.entry) .. " side, but the finale's cart ramp faces " .. OPP[heading_of(z)] ..
              ": set its params.heading to " .. tostring(OPP[sd.entry] or "the side the cart enters by") .. ".", id)
          end
        end
      end
      if not uphill then violation(V, "rule", "payload", "the cart track never climbs; uphill pushes are the tense moments.") end
      if not turned then violation(V, "rule", "payload", "the cart track never turns; turns break defender sightlines.") end
      local last = cart.zones and ids[cart.zones[#cart.zones]]
      if not last or last.archetype ~= "finale" then violation(V, "rule", "payload", "the track should end in a fortified finale zone.") end
    end
    local sky_area = 0
    for _, z in ipairs(zs) do if LA[z.archetype].sky or z.sky then sky_area = sky_area + zone_area(z) end end
    if sky_area < DATA.payload_outdoor_share * area then
      violation(V, "rule", "payload", "only " .. string.format("%.0f", 100 * sky_area / area) .. "% of the floor is outdoors; payload maps are partly outdoors.")
    end
    if area < DATA.payload_area_min then violation(V, "rule", "payload", "a payload layout of " .. fmt(area) .. " m2 is too small (at least " .. DATA.payload_area_min .. ").") end
    for _, o in ipairs(obj("spawns")) do
      if idx[o.zone] and #adj[idx[o.zone]] < 2 then violation(V, "rule", "payload", "spawn zone " .. o.zone .. " needs at least 2 exits.", o.zone) end
    end
    -- The hand-off: each checkpoint links the attacker and defender groups that are live while it is the target
    -- (Alpine enables a checkpoint's linked respawns only then, and never switches a respawn no checkpoint links).
    local owner = {}
    for i, cp in ipairs(cps) do
      local att, defn = false, false
      for _, sid in ipairs(cp.spawns or {}) do
        owner[sid] = (owner[sid] or 0) + 1
        for _, o in ipairs(obj("spawns")) do if o.id == sid then if o.role == "attack" then att = true elseif o.role == "defend" then defn = true end end end
      end
      if not att or not defn then violation(V, "rule", "payload", "checkpoint " .. i .. " must link an attacker and a defender spawn group (the groups live while it is the cart's target).", cp.zone) end
    end
    for _, o in ipairs(obj("spawns")) do
      if (owner[o.id or ""] or 0) == 0 then
        violation(V, "rule", "payload", "spawn group " .. tostring(o.id) .. " is linked to no checkpoint: Alpine never switches it off (link it to the checkpoint it serves).", o.zone)
      end
    end
  end
  if graph.class == "open_air" then
    for _, z in ipairs(zs) do
      if not LA[z.archetype].open_air then violation(V, "rule", "open_air", "zone " .. z.id .. " (" .. z.archetype .. ") is not an open-air platform.", z.id) end
    end
    for _, l in ipairs(graph.links) do
      if l.type == "corridor" or l.type == "doorway" then violation(V, "rule", "open_air", "link " .. l.id .. ": platforms join by bridges, stairs or lifts, not " .. l.type .. "s.", nil, l.id) end
    end
  end

  -- Budget.
  local b = graph.budget
  if type(b) == "table" and b.area then
    if area < b.area[1] * 0.9 or area > b.area[2] * 1.1 then
      violation(V, "rule", "budget", "layout area " .. fmt(area) .. " m2 is outside the " .. graph.size_class .. " budget " .. b.area[1] .. "-" .. b.area[2] .. " m2.")
    end
    if b.zones and (n < b.zones[1] or n > b.zones[2]) then
      violation(V, "rule", "budget", n .. " zones; the " .. (graph.size_class or "") .. " class has " .. b.zones[1] .. "-" .. b.zones[2] .. ".")
    end
    local hubs = 0
    for _, z in ipairs(zs) do if z.role == "hub" or z.role == "hill" then hubs = hubs + 1 end end
    if b.hubs and (hubs < b.hubs[1] or hubs > b.hubs[2]) then
      violation(V, "rule", "budget", hubs .. " hub(s); the class has " .. b.hubs[1] .. "-" .. b.hubs[2] .. ".")
    end
    local sp = spawn_total(graph)
    if sp > 0 and b.per_spawn then
      local per = area / sp
      if per < b.per_spawn[1] * 0.8 or per > b.per_spawn[2] * 1.25 then
        violation(V, "rule", "budget", string.format("%.0f", per) .. " m2 per spawn (" .. sp .. " spawns); the class aims at " .. b.per_spawn[1] .. "-" .. b.per_spawn[2] .. ".")
      end
    end
  end
  return V, plan
end

---@kit kit.check_layout{graph} → violation[] — Checks a layout graph against the c.1-1 rules before anything is built: schema and geometry (zones apart, every link fits its walls, levels and gaps), degree >= 2 except reward nooks, no articulation zone with >= 10% of the area behind it, cycle rank per 1,000 m2, 2 node-disjoint routes between hubs and objectives, 3+ floor levels 4.5-10 m apart joined by 2+ kinds of vertical link, archetype variety, mirror symmetry only for team modes, and the CTF / KOTH / payload / open-air rules and the size budget. A link without a_y / b_y meets each zone at its floor (a graded road's top floor at its exit end). Payload tracks run on any heading: a road zone's params.heading is the way the cart travels through it (entered on the opposite end), the cart turns inside the zones between roads, consecutive cart zones need a road, bridge, doorway or corridor between them, and a bridge outside open-air layouts spans a chasm between two floors at one level. Free-form plans: zone.rot turns a zone's plan (its sides stay its own), a poly zone's outline is its world plan, a link that is not straight between facing walls of two rectangle zones of one rot is free (path waypoints, a_side / b_side, a_at / b_at, or routed clear of the zones and links), and a join opens two touching zones into one space; the checker reports overlaps and clearances in world coordinates, free links that bend more than 90 degrees, run under 1 m a segment, enter or pass within 1 m of a zone or cross another link on its storey, or lack the straight a stair or ramp needs, and joins that do not touch. A hand-made zone without levels, height or chamfer gets its archetype's. Each violation is {severity ("error": cannot build; "rule": a design rule), rule, message, zone?, link?}.
---@opt graph: table — Required: a kit.layout graph (possibly edited).
function kit.check_layout(t)
  check_keys("kit.check_layout", t, { "graph" })
  if t.graph == nil then fail("kit.check_layout", "'graph' is required (a kit.layout result).") end
  local V = check(t.graph)
  return V
end

-- ---------------------------------------------------------------------------------------------
-- Generation
-- ---------------------------------------------------------------------------------------------

local function size_class(players)
  if players <= 4 then return "XS" elseif players <= 8 then return "S" elseif players <= 16 then return "M" end
  return "L"
end

-- Archetype levels as metres for a step.
local function levels_for(A, step)
  local out = {}
  for _, l in ipairs(A.levels) do out[#out + 1] = (l == "step") and step or l end
  return out
end

local function chamfer_for(A, z)
  if type(A.chamfer) == "function" then return A.chamfer(z) end
  return A.chamfer or 0
end

local function height_for(A, z, step)
  if A.height then return A.height(z, step) end
  return 0
end

-- Finishes a zone from its archetype: levels, chamfer, height, palette defaults.
local function finish_zone(z, step)
  local A = LA[z.archetype]
  z.levels = levels_for(A, step)
  if z.archetype == "road" and z.params and (z.params.rise or 0) ~= 0 then z.levels = { 0, z.params.rise } end
  z.chamfer = chamfer_for(A, z)
  z.height = height_for(A, z, step)
  z.sky = A.sky or nil
  if A.open_air or A.sky then z.ground = z.ground or "brush" end
  z.loops = A.loops
  z.label = z.label or (z.archetype .. (z.role and (" (" .. z.role .. ")") or ""))
  return z
end

local function widest(r) local w = 0 for _, s in ipairs(r) do w = math.max(w, s[2] - s[1]) end return w end

-- Spawn groups spread over zones by area (DM, KOTH).
local function spread_spawns(G, total, filter)
  local list = {}
  for _, z in ipairs(G.zones) do if filter(z) then list[#list + 1] = z end end
  -- biggest first, ties by id (an insertion sort on the areas, measured once)
  local ar = {}
  for i, z in ipairs(list) do ar[i] = { z = z, a = zone_area(z) } end
  for i = 2, #ar do
    local c, j = ar[i], i - 1
    while j >= 1 and (ar[j].a < c.a or (ar[j].a == c.a and ar[j].z.id > c.z.id)) do ar[j + 1] = ar[j] j = j - 1 end
    ar[j + 1] = c
  end
  for i, e in ipairs(ar) do list[i] = e.z end
  local counts = {}
  local n = #list
  local share = math.max(1, math.ceil(n * 0.75))
  for i = 1, n do counts[i] = (i <= share) and 1 or 0 end
  local left = total - math.min(total, share)
  local i = 1
  while left > 0 and n > 0 do
    counts[i] = counts[i] + 1
    left = left - 1
    i = i % math.min(n, share) + 1
  end
  for k, z in ipairs(list) do
    if counts[k] > 0 then
      G.objectives[#G.objectives + 1] = { kind = "spawns", id = "s" .. (#G.objectives + 1), zone = z.id, count = counts[k] }
    end
  end
end

local function themes_from(t, gametype)
  local themes = copy_list(THEMES[gametype] or THEMES.dm)
  local style = t.style
  if type(style) == "table" and type(style.themes) == "table" and #style.themes > 0 then themes = copy_list(style.themes)
  elseif type(style) == "table" and type(style.theme) == "string" then table.insert(themes, 1, style.theme) end
  if type(t.brief) == "string" then
    local b = t.brief:lower()
    local found = {}
    for _, k in ipairs({ { "industrial", "industrial" }, { "factory", "industrial" }, { "bunker", "bunker" }, { "concrete", "bunker" },
      { "admin", "admin" }, { "office", "admin" }, { "tech", "admin" }, { "cave", "cave" }, { "canyon", "cave" }, { "mine", "cave" },
      { "rock", "cave" }, { "mars", "marsbase" } }) do
      if b:find(k[1], 1, true) then
        local dup = false
        for _, f in ipairs(found) do if f == k[2] then dup = true end end
        if not dup then found[#found + 1] = k[2] end
      end
    end
    for i = #found, 1, -1 do table.insert(themes, 1, found[i]) end
  end
  local out, seen = {}, {}
  for _, th in ipairs(themes) do if not seen[th] then seen[th] = true out[#out + 1] = th end end
  return out
end

-- DM / KOTH / CTF archetype pools.
local POOL = {
  indoor = { "hall", "storage", "colonnade", "split", "gallery", "courtyard", "pit", "tunnel" },
  hub = { "atrium", "arena", "sunken" },
  open_air = { "isle", "deck", "garden", "gantry", "outpost", "plaza", "mast" },
  open_air_hub = { "ring", "hangar" },
}

---@kit kit.layout{gametype?, players?, class?, brief?, seed?, area?, style?, step?, segments?} → graph — Generates a layout graph before any brush: zones (archetype, plan, floor level, palette, role, ground), typed links, objectives (spawn groups, flags, the hill, the payload cart and checkpoints with the spawn hand-off) and the size budget from the player class (DESIGN-QUALITY-SPEC §a.1, §c.1-1, §h). A plain table: edit it, then kit.check_layout, kit.layout_preview and kit.build_layout. The same seed gives the same graph.
---@opt gametype: string = "dm" — "dm", "ctf", "koth" or "payload".
---@opt players: integer = 8 — Intended players: 2-4 an arena (XS), up to 8 S, up to 16 M, more L; payload 12+ adds segments.
---@opt class: string = "standard" — "standard" (indoor, with courtyards) or "open_air" (floating platforms, sky bridges and decks); payload is its own class.
---@opt brief: string = none — A one-line brief; theme words (industrial, bunker, concrete, admin, tech, cave, canyon, mine, mars) set the zone palettes.
---@opt seed: integer = 1 — The layout's seed.
---@opt area: number = the class budget's middle — Target layout floor area in m2 (zones plus links).
---@opt style: table = none — {theme = "bunker"} or {themes = {...}}: palette themes in order of use.
---@opt step: number = 5 (payload 4.5) — Metres between floor levels (4.5-10).
---@opt segments: integer = by players (3-5) — Payload segments (checkpoints).
function kit.layout(t)
  local api = "kit.layout"
  t = t or {}
  check_keys(api, t, { "gametype", "players", "class", "brief", "seed", "area", "style", "step", "segments" })
  local gametype = t.gametype or "dm"
  if gametype ~= "dm" and gametype ~= "ctf" and gametype ~= "koth" and gametype ~= "payload" then
    fail(api, "'gametype' must be \"dm\", \"ctf\", \"koth\" or \"payload\" (got " .. tostring(gametype) .. ").")
  end
  local players = number(api, "players", t.players, gametype == "payload" and 12 or 8)
  if players < 2 or players ~= math.floor(players) then fail(api, "'players' must be a whole number of at least 2.") end
  local class = t.class or (gametype == "payload" and "payload" or "standard")
  if gametype == "payload" then
    if class ~= "payload" and class ~= "standard" then fail(api, "payload is its own class (omit 'class').") end
    class = "payload"
  elseif class ~= "standard" and class ~= "open_air" then
    fail(api, "'class' must be \"standard\" or \"open_air\" (got " .. tostring(class) .. ").")
  end
  local seed = number(api, "seed", t.seed, 1)
  if seed ~= math.floor(seed) then fail(api, "'seed' must be an integer.") end
  if t.brief ~= nil and type(t.brief) ~= "string" then fail(api, "'brief' must be a string.") end
  if t.style ~= nil and type(t.style) ~= "table" then fail(api, "'style' must be a table, e.g. {theme = \"bunker\"}.") end
  local step = number(api, "step", t.step, gametype == "payload" and DATA.payload_step or DATA.step)
  if step < DATA.level_spacing[1] - EPS or step > DATA.level_spacing[2] + EPS then fail(api, "'step' must be 4.5-10 m (storeys sit 4.5-10 m apart).") end
  if math.abs(step / DATA.riser - math.floor(step / DATA.riser + 0.5)) > EPS then fail(api, "'step' must be a whole number of 0.25 m risers.") end
  local sc = size_class(players)
  local budget = nil
  if gametype == "dm" then budget = BUDGET.dm[sc] else budget = BUDGET[gametype] end
  local B = {}
  for k, v in pairs(budget) do B[k] = v end
  if gametype ~= "dm" then
    B.zones = BUDGET[gametype].zones
    if sc == "L" and gametype ~= "payload" then B.hubs = { 1, 3 } end
  end
  local base_seed = seed * 7919 + #gametype * 104729 + (class == "open_air" and 31 or 0) + players * 13
  local rng = new_rng(base_seed)
  local target = nil
  if t.area ~= nil then target = positive(api, "area", t.area)
  else
    local lo, hi = B.area[1], B.area[2]
    local f = nil
    if gametype == "dm" then f = rng.range(0.35, 0.6)
    else f = clamp((players - 4) / 28, 0.15, 0.85) * 0.6 + rng.range(0.1, 0.25) end
    target = lo + (hi - lo) * f
  end
  local themes = themes_from(t, gametype)
  -- Seeded attempts: the first graph that meets every rule wins, else the one with the fewest violations.
  local best, best_score, best_v = nil, math.huge, nil
  for attempt = 0, 11 do
    local arng = new_rng(base_seed + attempt * 999331)
    local G = {
      kind = "layout", version = 1, gametype = gametype, class = class, players = players, size_class = sc, seed = seed,
      brief = t.brief, style = t.style, step = step, target_area = math.floor(target + 0.5), attempt = attempt,
      budget = { area = { B.area[1], B.area[2] }, per_spawn = { B.per_spawn[1], B.per_spawn[2] }, zones = { B.zones[1], B.zones[2] },
        hubs = { B.hubs[1], B.hubs[2] }, target = math.floor(target + 0.5),
        cycle_per_1000 = (sc == "L") and DATA.cycle_per_1000_L or DATA.cycle_per_1000 },
      zones = {}, links = {}, objectives = {}, segments = t.segments, themes = themes,
    }
    if gametype == "payload" then G.budget.area = { DATA.payload_area_min + 2000, BUDGET.payload.area[2] } end
    -- The generator of the gametype (kit_compose.lua: indoor DM, CTF and KOTH; kit_compose_open.lua: their open-air
    -- class; kit_compose_payload.lua: payload) composes the graph; the resolver then routes and fits every link.
    local gen = LAYOUT.generators[gametype]
    if not gen or gen(G, arng, t) ~= true then fail(api, "no layout generator handles gametype " .. gametype .. " (class " .. class .. ").") end
    -- (every bridge the generator composed must join places that are otherwise far apart, before routing)
    settle_bridges(G)
    G.method = "free"
    local plan = resolve(G, arng, true)
    -- Links that still find no room on their walls are dropped (the rules below then judge the graph).
    if #plan.errors > 0 then
      local bad = {}
      G.dropped = {}
      for _, e in ipairs(plan.errors) do if e.link then bad[e.link] = true G.dropped[#G.dropped + 1] = e.message end end
      local kept = {}
      for _, l in ipairs(G.links) do if not bad[l.id] then kept[#kept + 1] = l end end
      G.links = kept
      for i, l in ipairs(G.links) do l.id = "l" .. i end
    end
    for _, z in ipairs(G.zones) do
      -- The per-zone dressing hook (a population pass reads it): the zone theme and a density.
      z.dress = { theme = z.palette and z.palette.theme or themes[1], density = (z.role == "hub" or z.role == "hill") and 1.2 or 1 }
    end
    -- (the checker's plan gives the area: one resolve fewer per attempt)
    local V, cplan = check(G)
    G.area = layout_area(G, cplan or resolve(G, nil, false))
    G.levels = floor_levels(G)
    local errors, rules = 0, 0
    for _, v in ipairs(V) do if v.severity == "error" then errors = errors + 1 else rules = rules + 1 end end
    local score = errors * 100 + rules
    if score < best_score then best, best_score, best_v = G, score, V end
    if score == 0 then break end
  end
  best.attempt = nil
  best.notes = {}
  for _, m in ipairs(best.dropped or {}) do best.notes[#best.notes + 1] = "dropped " .. m end
  best.dropped = nil
  for _, v in ipairs(best_v or {}) do best.notes[#best.notes + 1] = v.message end
  return best
end

---@kit kit.layout_archetypes{} → archetype[] — The zone archetypes kit.layout and kit.build_layout use: {name, class ("indoor", "open_air", "payload" or "traced" for the poly zone), min = {w, d} (kit.check_layout refuses smaller zones; a road's {w, d} turns with its heading), max = {w, d} (the sizes kit.layout generates; larger hand-made zones are allowed), levels (port levels; "step" = one level up), loops (inside the zone), summary}.
function kit.layout_archetypes(t)
  if t ~= nil then check_keys("kit.layout_archetypes", t, {}) end
  local out = {}
  for i, n in ipairs(LORDER) do
    local A = LA[n]
    out[i] = { name = n, class = A.class, min = { A.min[1], A.min[2] }, max = { A.max[1], A.max[2] }, levels = copy_list(A.levels),
      loops = A.loops, summary = A.summary, hub = A.hub or false, hill = A.hill or false }
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- kit.layout_preview: a plan before any brush
-- ---------------------------------------------------------------------------------------------

local LINK_CHAR = { corridor = "=", doorway = "=", stair = "s", ramp = "r", drop = "v", bridge = "b", window = "~", lift = "l", road = "#", join = "+" }

-- A resolved link as a world polyline (its centre line from wall to wall) and its width, for drawing.
local function link_line(P)
  if not P.ok then return nil end
  if P.mode == "free" then return P.pts, P.W end
  if P.mode == "join" then return nil end
  local g = P.f
  local rot = zone_rot(P.za)
  local ax, az, bx, bz
  if g.axis == "x" then ax, az, bx, bz = g.wa, P.c, g.wb, P.c else ax, az, bx, bz = P.c, g.wa, P.c, g.wb end
  local x0, z0 = to_world(rot, ax, az)
  local x1, z1 = to_world(rot, bx, bz)
  return { { x0, z0 }, { x1, z1 } }, P.W
end

---@kit kit.layout_preview{graph, scale?} → preview — A plan of a layout graph as text (north = +z up, one character per scale metres): zones filled with their level digit and labelled with their letter (turned zones and traced poly zones drawn as their world outlines), links drawn along their routes with their type's character (= corridor/doorway, s stair, r ramp, v drop, b bridge, ~ window, l lift, # cart road, + join), objectives marked (R/B flags, H hill, C checkpoints, S spawns). Returns {text, svg, legend[], scale, bounds}.
---@opt graph: table — Required: a kit.layout graph.
---@opt scale: number = fits about 100 columns — Metres per character.
function kit.layout_preview(t)
  local api = "kit.layout_preview"
  check_keys(api, t, { "graph", "scale" })
  local G = t.graph
  if type(G) ~= "table" or type(G.zones) ~= "table" then fail(api, "'graph' must be a kit.layout graph.") end
  local polys = {}
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for i, z in ipairs(G.zones) do
    -- a poly zone left without pos x/z or size draws from its outline
    if z.archetype == "poly" and type(z.pos) == "table" and poly_of(z) then
      local a, b, c, d = bbox(poly_of(z))
      if type(z.pos[1]) ~= "number" then z.pos[1] = (a + c) / 2 end
      if type(z.pos[3]) ~= "number" then z.pos[3] = (b + d) / 2 end
      if type(z.size) ~= "table" then z.size = { c - a, d - b } end
    end
    polys[i] = zone_poly(z)
    local a, b, c, d = bbox(polys[i])
    x0, z0, x1, z1 = math.min(x0, a), math.min(z0, b), math.max(x1, c), math.max(z1, d)
  end
  if x0 == math.huge then return { text = "(empty layout)", svg = "", legend = {}, scale = 1 } end
  x0, z0, x1, z1 = x0 - 4, z0 - 4, x1 + 4, z1 + 4
  local scale = t.scale and positive(api, "scale", t.scale) or math.max(1, math.ceil(math.max(x1 - x0, (z1 - z0) * 2) / 100))
  local cols = math.ceil((x1 - x0) / scale)
  local rows = math.ceil((z1 - z0) / (scale * 2))
  local grid = {}
  for r = 1, rows do grid[r] = {} for c = 1, cols do grid[r][c] = " " end end
  local function put(x, z, ch)
    local c = math.floor((x - x0) / scale) + 1
    local r = rows - math.floor((z - z0) / (scale * 2))
    if r >= 1 and r <= rows and c >= 1 and c <= cols then grid[r][c] = ch end
  end
  local levels = floor_levels(G)
  local function level_digit(y)
    local best, bd = 0, math.huge
    for i, l in ipairs(levels) do if math.abs(l - y) < bd then best, bd = i - 1, math.abs(l - y) end end
    return tostring(best % 10)
  end
  local letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
  local legend = {}
  for i, z in ipairs(G.zones) do
    local p = polys[i]
    local a, b, c, d = bbox(p)
    local ch = level_digit(z.pos[2])
    -- every character cell whose centre lies in the zone's world plan
    local c0, c1 = math.floor((a - x0) / scale) + 1, math.floor((c - x0) / scale) + 1
    local r0, r1 = rows - math.floor((d - z0) / (scale * 2)), rows - math.floor((b - z0) / (scale * 2))
    for r = math.max(1, r0), math.min(rows, r1) do
      for col = math.max(1, c0), math.min(cols, c1) do
        local x, zz = x0 + (col - 0.5) * scale, z0 + (rows - r + 0.5) * scale * 2
        if inside(p, x, zz) then grid[r][col] = ch end
      end
    end
    local L = letters:sub(((i - 1) % #letters) + 1, ((i - 1) % #letters) + 1)
    local cx, cz = centre_of(z)
    put(cx, cz, L)
    local rot = zone_rot(z)
    local shape = (z.archetype == "poly") and string.format("%d-point outline, %.0f m2", #p, zone_area(z)) or string.format("%gx%g m", z.size[1], z.size[2])
    legend[#legend + 1] = string.format("%s %s %s%s %s%s, floor %g m (levels +%s)%s", L, z.id, z.archetype, z.role and (" [" .. z.role .. "]") or "",
      shape, rot ~= 0 and string.format(", rot %g", rot) or "", z.pos[2], table.concat(z.levels or { 0 }, ",+"),
      z.palette and (", " .. (z.palette.theme or "") .. (z.palette.team and ("/" .. z.palette.team) or "")) or "")
  end
  local plan = resolve(G, nil, false)
  local drawn = {}
  for _, P in ipairs(plan.links) do
    if P.ok then
      local ch = LINK_CHAR[P.link.type] or "?"
      local line, W = link_line(P)
      if P.mode == "join" then
        for _, port in ipairs(plan.ports[P.za.id] or {}) do
          if port.link == P.link then
            local a, b = nil, nil
            if port.seg then a, b = port.seg[1], port.seg[2]
            else
              local rot = zone_rot(P.za)
              local p0, p1 = wall_point(P.za, port.side, port.lo), wall_point(P.za, port.side, port.hi)
              a, b = { to_world(rot, p0[1], p0[2]) }, { to_world(rot, p1[1], p1[2]) }
            end
            line = { a, b }
          end
        end
        W = 0
      end
      if line then
        for k = 1, #line - 1 do
          local a, b = line[k], line[k + 1]
          local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
          local n = math.max(1, math.ceil(L / (scale * 0.5)))
          for j = 0, n do put(a[1] + (b[1] - a[1]) * j / n, a[2] + (b[2] - a[2]) * j / n, ch) end
        end
        drawn[#drawn + 1] = { P = P, line = line, W = W }
      end
      local ya, yb = link_ys(P.link, P.za, P.zb)
      legend[#legend + 1] = string.format("  %s %s %s-%s %g m wide, y %g -> %g%s", tostring(P.link.id or ("l" .. P.index)), P.link.type, P.link.a, P.link.b, P.W or P.span or 0, ya, yb,
        (P.mode == "free") and string.format(", free, %.0f m, %d bend(s)", P.length or 0, math.max(0, #P.pts - 2)) or ((P.mode == "join") and string.format(", %.1f m open", P.span or 0) or ""))
    end
  end
  local byid = {}
  for _, z in ipairs(G.zones) do byid[z.id] = z end
  for _, o in ipairs(G.objectives or {}) do
    local z = byid[o.zone or ""]
    if z then
      local ch = (o.kind == "flag" and (o.team == "red" and "R" or "B")) or (o.kind == "hill" and "H") or (o.kind == "checkpoint" and "C") or (o.kind == "spawns" and "S") or nil
      local cx, cz = centre_of(z)
      if ch then
        local ox, oz = 0, 0
        if o.kind == "spawns" then ox, oz = to_world(zone_rot(z), z.size[1] * 0.25, z.size[2] * 0.25) end
        put(cx + ox, cz + oz, ch)
      end
      legend[#legend + 1] = "  " .. o.kind .. (o.team and (" " .. o.team) or "") .. (o.count and (" x" .. o.count) or "") .. " in " .. o.zone ..
        (o.group and (" (" .. o.group .. ")") or "")
    end
  end
  local lines = {}
  lines[1] = string.format("PLAN %s %s, %d zones, %d links, %.0f m2; 1 char = %g m (x) x %g m (z); north (+z) up; digits = floor level 0-%d (%s m)",
    G.gametype or "?", G.class or "", #G.zones, #G.links, layout_area(G, plan), scale, scale * 2, #levels - 1, table.concat(levels, "/"))
  for r = 1, rows do lines[#lines + 1] = table.concat(grid[r]) end
  for _, l in ipairs(legend) do lines[#lines + 1] = l end
  -- An SVG of the same plan (1 px per 0.5 m).
  local px = 2
  local function X(x) return (x - x0) * px end
  local function Y(z) return (z1 - z) * px end
  local sv = { string.format('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d">',
    math.ceil((x1 - x0) * px), math.ceil((z1 - z0) * px), math.ceil((x1 - x0) * px), math.ceil((z1 - z0) * px)) }
  sv[#sv + 1] = '<rect width="100%" height="100%" fill="#222"/>'
  local shade = { "#6b8", "#8ab", "#ca8", "#c88", "#a8c", "#aaa" }
  for i, z in ipairs(G.zones) do
    local li = tonumber(level_digit(z.pos[2])) + 1
    local pts = {}
    for _, q in ipairs(polys[i]) do pts[#pts + 1] = string.format("%.1f,%.1f", X(q[1]), Y(q[2])) end
    sv[#sv + 1] = string.format('<polygon points="%s" fill="%s" stroke="#111"/>', table.concat(pts, " "), shade[((li - 1) % #shade) + 1])
    local cx, cz = centre_of(z)
    sv[#sv + 1] = string.format('<text x="%.1f" y="%.1f" font-size="12" text-anchor="middle">%s %s</text>', X(cx), Y(cz), z.id, z.archetype)
  end
  for _, e in ipairs(drawn) do
    local pts = {}
    for _, q in ipairs(e.line) do pts[#pts + 1] = string.format("%.1f,%.1f", X(q[1]), Y(q[2])) end
    local ty = e.P.link.type
    local color = (ty == "corridor" and "#ddd") or (ty == "road" and "#eee") or (ty == "join" and "#fff") or "#fd6"
    sv[#sv + 1] = string.format('<polyline points="%s" fill="none" stroke="%s" stroke-width="%.1f" stroke-linejoin="miter"/>', table.concat(pts, " "), color,
      math.max(2, (e.W or 0) * px))
  end
  sv[#sv + 1] = "</svg>"
  return { text = table.concat(lines, "\n"), svg = table.concat(sv, "\n"), legend = legend, scale = scale,
    bounds = { min = vec(x0, 0, z0), max = vec(x1, 0, z1) } }
end

-- ---------------------------------------------------------------------------------------------
-- Building
-- ---------------------------------------------------------------------------------------------

local function palette_of(z)
  local p = z.palette or {}
  return kit.palette{ theme = p.theme or "industrial", variant = p.variant or 1, team = p.team }
end

-- A zone's build context.
local function zone_ctx(gname, z, plan)
  local R = { z = z, id = z.id, name = gname .. "_" .. z.id, A = LA[z.archetype] }
  R.x0, R.z0, R.x1, R.z1 = rect_of(z)
  R.w, R.d = z.size[1], z.size[2]
  if z.archetype == "poly" then
    -- a traced zone is built where it is traced: its frame is the world's
    R.poly = poly_of(z)
    R.w, R.d = R.x1 - R.x0, R.z1 - R.z0
    R.area = zone_area(z)
    -- its middle: the centre of its biggest convex piece (inside the plan whatever its shape)
    local best, bx, bz = -1, (R.x0 + R.x1) / 2, (R.z0 + R.z1) / 2
    for _, piece in ipairs(SP.convex_pieces(ccw(R.poly)) or {}) do
      local a = math.abs(area2(piece))
      if a > best then
        best, bx, bz = a, 0, 0
        for _, q in ipairs(piece) do bx, bz = bx + q[1] / #piece, bz + q[2] / #piece end
      end
    end
    R.cx, R.cz = bx, bz
  else
    R.cx, R.cz = local_centre(z)
  end
  -- Everything a zone builds is built in its own frame (an axis-aligned plan) and turned into place by rot.
  R.rot = zone_rot(z)
  function R.W(x, zz) return to_world(R.rot, x, zz) end
  R.y, R.h, R.c = z.pos[2], z.height or 6, z.chamfer or 0
  R.step = z.levels[2] or 5
  R.pal = palette_of(z)
  R.textures = R.pal.textures
  R.trim = { texture = R.pal.trims.post }
  R.ports = plan.ports[z.id] or {}
  R.solids, R.spots, R.parts, R.brushes, R.warnings = {}, {}, {}, {}, {}
  R.flights, R.rails = {}, {} -- the zone's stair records (for their stringers and handrails) and its open edges {a, b, y} (zone frame)
  R.lane = nil
  return R
end

-- The sign that makes brush:rotate_about("y", ROT_SIGN * yaw) turn local +z toward (sin yaw, cos yaw), i.e. world =
-- R(rot) . local as to_world computes it.
local ROT_SIGN = 1

-- Turns the brushes list[from + 1 ..] by yaw degrees about the vertical through (px, pz) (the world origin by default).
local function turn_list(list, from, yaw, px, py, pz)
  if yaw == 0 or list == nil then return end
  for i = (from or 0) + 1, #list do list[i]:rotate_about("y", ROT_SIGN * yaw, px or 0, py or 0, pz or 0) end
end

-- The brushes of a list of kit records ({brushes} or {brush}) and plain brushes.
local function rec_brushes(recs)
  local out = {}
  for _, r in ipairs(recs) do
    if type(r) == "table" then
      if type(r.brushes) == "table" then for _, b in ipairs(r.brushes) do out[#out + 1] = b end
      elseif r.brush then out[#out + 1] = r.brush end
    elseif r ~= nil then out[#out + 1] = r end
  end
  return out
end

-- Walls take buried solids: a box a zone builds that ends on one of its walls runs BURY metres into it. A face of
-- it on the wall's plane would lie on the room's own wall face, and in a turned frame two brushes' faces on one
-- oblique plane never coincide exactly (their world vertices are rounded apart), which leaves open seams in the
-- compile under either build method. Buried, the solid crosses the wall instead. One rule for every frame, so a
-- zone builds the same brushes at any rot; never across a port's opening (the passage stays clear).
local BURY = 0.25

-- The per-corner UVs of a brush's faces carried over to its reshaped corners: each face's texture mapping is
-- affine in the face's plane, so a moved corner takes the UV the old mapping gives its new position.
local function face_frames(b)
  local list = {}
  for i, f in ipairs(b.faces) do list[i] = { pts = f.vertices, uvs = f.uvs, n = f.normal } end
  return list
end

local function keep_uvs(b, before)
  for _, f in ipairs(b.faces) do
    -- the face of the old shape on the same side (a reshaped box keeps its six normals)
    local n, old = f.normal, nil
    for _, o in ipairs(before) do if o.n.x * n.x + o.n.y * n.y + o.n.z * n.z > 0.999 then old = o end end
    local new = f.vertices
    if old and #old.pts == #new and #new >= 3 then
      local p0, uv0 = old.pts[1], old.uvs[1]
      -- the two corners spanning the largest triangle with the first
      local best, j1, j2 = -1, 2, 3
      for a = 2, #old.pts - 1 do
        for c = a + 1, #old.pts do
          local ax, ay, az = old.pts[a].x - p0.x, old.pts[a].y - p0.y, old.pts[a].z - p0.z
          local cx, cy, cz = old.pts[c].x - p0.x, old.pts[c].y - p0.y, old.pts[c].z - p0.z
          local nx, ny, nz = ay * cz - az * cy, az * cx - ax * cz, ax * cy - ay * cx
          local ar = nx * nx + ny * ny + nz * nz
          if ar > best then best, j1, j2 = ar, a, c end
        end
      end
      local e1 = { old.pts[j1].x - p0.x, old.pts[j1].y - p0.y, old.pts[j1].z - p0.z }
      local e2 = { old.pts[j2].x - p0.x, old.pts[j2].y - p0.y, old.pts[j2].z - p0.z }
      local g11 = e1[1] * e1[1] + e1[2] * e1[2] + e1[3] * e1[3]
      local g12 = e1[1] * e2[1] + e1[2] * e2[2] + e1[3] * e2[3]
      local g22 = e2[1] * e2[1] + e2[2] * e2[2] + e2[3] * e2[3]
      local det = g11 * g22 - g12 * g12
      if det > 1e-12 then
        local u1, u2 = old.uvs[j1], old.uvs[j2]
        local list = {}
        for k, q in ipairs(new) do
          local d = { q.x - p0.x, q.y - p0.y, q.z - p0.z }
          local r1 = e1[1] * d[1] + e1[2] * d[2] + e1[3] * d[3]
          local r2 = e2[1] * d[1] + e2[2] * d[2] + e2[3] * d[3]
          local s = (r1 * g22 - r2 * g12) / det
          local t = (r2 * g11 - r1 * g12) / det
          list[k] = { uv0.u + s * (u1.u - uv0.u) + t * (u2.u - uv0.u), uv0.v + s * (u1.v - uv0.v) + t * (u2.v - uv0.v) }
        end
        f:set_uvs(list)
      end
    end
  end
end

-- True when no port opening of the zone's wall side meets the span a0..a1 along it at heights y0..y1.
local function wall_free(R, side, a0, a1, y0, y1)
  for _, p in ipairs(R.ports) do
    if p.side == side and a0 < p.hi + 0.5 and a1 > p.lo - 0.5 and y0 < p.y1 and y1 > (p.drop_to or p.y0) - 0.3 then return false end
  end
  return true
end

-- Buries the axis-aligned solid boxes R.brushes[from + 1 ..] that end on the zone's walls (the zone's own frame,
-- before its turn).
local function bury_walls(R, from)
  if R.poly or R.A.open_air then return end
  for i = from + 1, #R.brushes do
    local b = R.brushes[i]
    if not b.air and b.mover == nil then
      local fs = b.faces
      local boxy = #fs == 6
      if boxy then
        local r = b.rot
        boxy = math.abs(r.yaw) < 1e-6 and math.abs(r.pitch) < 1e-6 and math.abs(r.roll) < 1e-6
      end
      if boxy then
        for _, f in ipairs(fs) do
          local n = f.normal
          if math.abs(n.x) + math.abs(n.y) + math.abs(n.z) > 1.0001 then boxy = false end
        end
      end
      if boxy then
        local bb = b.bounds
        local x0, y0, z0, x1, y1, z1 = bb.min.x, bb.min.y, bb.min.z, bb.max.x, bb.max.y, bb.max.z
        local function on(v, w, len) return math.abs(v - w) < 0.01 and len > 0.05 end
        local ex0 = (on(x0, R.x0, x1 - x0) and wall_free(R, "-x", z0, z1, y0, y1)) and BURY or 0
        local ex1 = (on(x1, R.x1, x1 - x0) and wall_free(R, "+x", z0, z1, y0, y1)) and BURY or 0
        local ez0 = (on(z0, R.z0, z1 - z0) and wall_free(R, "-z", x0, x1, y0, y1)) and BURY or 0
        local ez1 = (on(z1, R.z1, z1 - z0) and wall_free(R, "+z", x0, x1, y0, y1)) and BURY or 0
        if ex0 + ex1 + ez0 + ez1 > 0 then
          local before = face_frames(b)
          local w, d = x1 - x0, z1 - z0
          b:scale((w + ex0 + ex1) / w, 1, (d + ez0 + ez1) / d)
          b:move((ex1 - ex0) / 2, 0, (ez1 - ez0) / 2)
          -- back onto exact coordinates (the scale leaves float noise), then the old texture mapping
          geo:snap{ brushes = b, grid = 1 / 1024 }
          keep_uvs(b, before)
        end
      end
    end
  end
end

-- A flight of kit.stairs whose steps are one-riser slabs, each from its nosing back past the top of the flight: the
-- same solid as full-height steps, but neighbouring steps meet only on level planes (exact in any frame) instead of
-- on a vertical riser plane, which a turned flight cannot hold exactly, and the flight's top end lies inside what it
-- climbs onto instead of on its face. Built along an axis dir (before any turn); the steps keep their UVs.
local function flight(t)
  local rec = kit.stairs(t)
  local d = DIRV[t.dir]
  local riser = t.riser or kit.defaults.riser
  -- (the slabs reach a quarter metre past the top into what the flight climbs onto, never ending on its face)
  local back = ((d[1] ~= 0) and rec.top.x or rec.top.z) + (d[1] + d[2]) * BURY
  for _, b in ipairs(rec.brushes) do
    local part = b:tags()["kit.part"] or ""
    if not b.air and not b.detail and part:match("^step%d+$") then
      local bb = b.bounds
      local h = bb.max.y - bb.min.y
      local a0, a1 = (d[1] ~= 0) and bb.min.x or bb.min.z, (d[1] ~= 0) and bb.max.x or bb.max.z
      local front = (d[1] + d[2] > 0) and a0 or a1
      local len = math.abs(back - front)
      if h > riser + 1e-6 or len > (a1 - a0) + 1e-6 then
        local before = face_frames(b)
        local f = len / (a1 - a0)
        b:scale(d[1] ~= 0 and f or 1, riser / h, d[2] ~= 0 and f or 1)
        local nb = b.bounds
        local mid = (front + back) / 2
        b:move(d[1] ~= 0 and (mid - (nb.min.x + nb.max.x) / 2) or 0, (bb.max.y - riser / 2) - (nb.min.y + nb.max.y) / 2,
          d[2] ~= 0 and (mid - (nb.min.z + nb.max.z) / 2) or 0)
        geo:snap{ brushes = b, grid = 1 / 1024 }
        keep_uvs(b, before)
      end
    end
  end
  return rec
end

-- A port's approach rectangle inside its zone (x0, z0, x1, z1) and its height band.
local function keepout(R, p, depth)
  depth = depth or DATA.keep_depth
  local a0, a1 = p.lo - 1, p.hi + 1
  local w = p.wall
  if p.side == "+x" then return w - depth, a0, w, a1 end
  if p.side == "-x" then return w, a0, w + depth, a1 end
  if p.side == "+z" then return a0, w - depth, a1, w end
  return a0, w, a1, w + depth
end

-- The approach of a port on a poly zone's edge: its opening grown 1 m each way along the edge, depth metres inward.
local function keepout_poly(p, depth)
  local a, b = p.seg[1], p.seg[2]
  local ex, ez = b[1] - a[1], b[2] - a[2]
  local L = math.max(1e-6, math.sqrt(ex * ex + ez * ez))
  local tx, tz = ex / L, ez / L
  local n = p.nrm
  local a1, b1 = { a[1] - tx, a[2] - tz }, { b[1] + tx, b[2] + tz }
  return ccw({ a1, b1, { b1[1] - n[1] * depth, b1[2] - n[2] * depth }, { a1[1] - n[1] * depth, a1[2] - n[2] * depth } })
end

-- True when a box inside the zone stays clear of the cart lane.
local function lane_clear(R, x0, z0, x1, z1)
  if R.lane then
    for _, seg in ipairs(R.lane) do
      -- seg = {ax, az, bx, bz, half}: distance from the box centre to the lane segment
      local cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
      local ex, ez = seg[3] - seg[1], seg[4] - seg[2]
      local L2 = ex * ex + ez * ez
      local tt = L2 > 0 and clamp(((cx - seg[1]) * ex + (cz - seg[2]) * ez) / L2, 0, 1) or 0
      local px, pz = seg[1] + ex * tt, seg[2] + ez * tt
      local r = math.max(x1 - x0, z1 - z0) / 2
      if math.sqrt((cx - px) ^ 2 + (cz - pz) ^ 2) < seg[5] + r then return false end
    end
  end
  return true
end

-- True when a box inside the zone stays clear of every port approach (and of the cart lane).
local function clear(R, x0, z0, x1, z1, y0, y1)
  for _, p in ipairs(R.ports) do
    local py0 = p.drop_to or p.y0
    if p.seg then
      if y0 < p.y1 and y1 > py0 - 0.3 and poly_dist(keepout_poly(p, DATA.keep_depth), { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }) < 1e-6 then return false end
    else
      local kx0, kz0, kx1, kz1 = keepout(R, p, p.drop_to and 6 or DATA.keep_depth)
      if x0 < kx1 and x1 > kx0 and z0 < kz1 and z1 > kz0 and y0 < p.y1 and y1 > py0 - 0.3 then return false end
    end
  end
  return lane_clear(R, x0, z0, x1, z1)
end

local function add_brush(R, b) if b then R.brushes[#R.brushes + 1] = b end return b end
local function add_rec(R, rec)
  if type(rec) == "table" then
    if type(rec.brushes) == "table" then for _, b in ipairs(rec.brushes) do R.brushes[#R.brushes + 1] = b end
    elseif rec.brush then R.brushes[#R.brushes + 1] = rec.brush end
    if type(rec.warnings) == "table" then for _, w in ipairs(rec.warnings) do R.warnings[#R.warnings + 1] = w end end
  end
  return rec
end

-- Every structural coordinate the layout emits sits on the 1/16 m grid: near-coincident planes from
-- unrelated pieces (0.4 m treads against 1.8 m cover, say) are what open hairline leaks in a shared BSP.
local function g16(v) return math.floor(v * 16 + 0.5) / 16 end

local function box(R, part, x0, y0, z0, x1, y1, z1, o)
  o = o or {}
  x0, y0, z0, x1, y1, z1 = g16(x0), g16(y0), g16(z0), g16(x1), g16(y1), g16(z1)
  local opt = { air = o.air, detail = o.detail }
  if o.texture then opt.texture = o.texture elseif o.textures then opt.textures = o.textures else opt.textures = R.textures end
  return add_brush(R, hull("layout", R.name, part, box_points(x0, y0, z0, x1, y1, z1), opt))
end

-- A solid standing in the zone: built, and remembered so spots keep clear of it.
local function solid(R, part, x0, y0, z0, x1, y1, z1, o)
  R.solids[#R.solids + 1] = { x0, z0, x1, z1, y0, y1 }
  return box(R, part, x0, y0, z0, x1, y1, z1, o)
end

local function note_solid(R, x0, z0, x1, z1, y0, y1) R.solids[#R.solids + 1] = { math.min(x0, x1), math.min(z0, z1), math.max(x0, x1), math.max(z0, z1), y0, y1 } end

local function stairs(R, part, x, y, z, dir, rise, width)
  local run = stair_run(rise)
  local dx, dz = DIRV[dir][1], DIRV[dir][2]
  note_solid(R, x - (dz ~= 0 and width / 2 or 0), z - (dx ~= 0 and width / 2 or 0), x + dx * run + (dz ~= 0 and width / 2 or 0), z + dz * run + (dx ~= 0 and width / 2 or 0), y, y + rise)
  local rec = add_rec(R, flight{ name = R.name .. "_" .. part, pos = { x, y, z }, dir = dir, rise = rise, width = width, tread = DATA.tread,
    group = false, textures = R.textures })
  R.flights[#R.flights + 1] = rec
  return rec
end

local function ramp(R, part, x, y, z, dir, rise, width, run)
  run = run or ramp_run(rise)
  local dx, dz = DIRV[dir][1], DIRV[dir][2]
  note_solid(R, x - (dz ~= 0 and width / 2 or 0), z - (dx ~= 0 and width / 2 or 0), x + dx * run + (dz ~= 0 and width / 2 or 0), z + dz * run + (dx ~= 0 and width / 2 or 0), y, y + rise)
  return add_rec(R, kit.ramp{ name = R.name .. "_" .. part, pos = { x, y, z }, dir = dir, rise = rise, run = run, width = width, group = false, textures = R.textures })
end

local function ports_at(R, side, y)
  local out = {}
  for _, p in ipairs(R.ports) do if p.side == side and (y == nil or math.abs(p.floor - y) < 0.5 or (p.drop_to and math.abs(p.drop_to - y) < 0.5)) then out[#out + 1] = p end end
  return out
end

-- The sides of the zone without any port (at any level), quietest first.
local function quiet_sides(R)
  local out = {}
  for _, s in ipairs(SIDES) do out[#out + 1] = { s, #ports_at(R, s) } end
  table.sort(out, function(a, b) if a[2] ~= b[2] then return a[2] < b[2] end return a[1] < b[1] end)
  local list = {}
  for _, e in ipairs(out) do list[#list + 1] = e[1] end
  return list, out
end

-- Floor spots on a grid inside the zone, clear of ports and solids.
local function grid_spots(R, y, x0, z0, x1, z1, pitch)
  pitch = pitch or 4
  local nx = math.max(1, math.floor((x1 - x0) / pitch))
  local nz = math.max(1, math.floor((z1 - z0) / pitch))
  for i = 0, nx do
    for k = 0, nz do
      local x = x0 + (x1 - x0) * (nx > 0 and i / nx or 0.5)
      local z = z0 + (z1 - z0) * (nz > 0 and k / nz or 0.5)
      R.spots[#R.spots + 1] = { x, y, z }
    end
  end
end

local function shaped(R, o)
  o.name = o.name or (R.name .. "_room")
  o.textures = o.textures or R.textures
  local rec = add_rec(R, kit.shaped_room(o))
  R.room = R.room or rec
  return rec
end

local function basic_shell(R, ceiling, extra)
  local o = { pos = { R.cx, R.y, R.cz }, size = { R.w, R.d }, chamfer = R.c, height = R.h, ceiling = ceiling }
  for k, v in pairs(extra or {}) do o[k] = v end
  return shaped(R, o)
end

-- A floating slab polygon (open air), in world coordinates, with its rocky underside.
local function floating(R, part, poly, top, thick, under)
  local F = I.frame("kit.build_layout", { 0, 0, 0 }, "+z")
  local ctx = SP.new_ctx("kit.build_layout", R.name, F, R.w, R.d, { textures = R.textures, trim_texture = R.trim.texture })
  SP.floating(ctx, part, poly, top, thick, under)
  for _, b in ipairs(ctx.brushes) do R.brushes[#R.brushes + 1] = b end
end

-- Edge treatment along a world polygon's edges (kerb, posts, rail), leaving the port spans and the given
-- skip predicate open.
local function edges(R, poly, y, skip)
  local n = #poly
  for i = 1, n do
    local a, b = poly[i], poly[i % n + 1]
    local ex, ez = b[1] - a[1], b[2] - a[2]
    local L = math.sqrt(ex * ex + ez * ez)
    if L > 0.5 and not (skip and skip(a, b)) then
      local ux, uz = ex / L, ez / L
      local nx, nz = -uz, ux -- inward for a CCW polygon
      -- Cut the edge at the port spans on it.
      local cuts = { { 0, L } }
      for _, p in ipairs(R.ports) do
        if math.abs(p.floor - y) < 0.5 then
          local on = nil
          if p.side == "+x" or p.side == "-x" then on = math.abs(ux) < 0.01 and math.abs(a[1] - p.wall) < 0.6
          else on = math.abs(uz) < 0.01 and math.abs(a[2] - p.wall) < 0.6 end
          if on then
            local s0 = ((p.side == "+x" or p.side == "-x") and (p.lo - a[2]) or (p.lo - a[1])) / ((p.side == "+x" or p.side == "-x") and uz or ux)
            local s1 = ((p.side == "+x" or p.side == "-x") and (p.hi - a[2]) or (p.hi - a[1])) / ((p.side == "+x" or p.side == "-x") and uz or ux)
            local lo, hi = math.min(s0, s1) - 0.3, math.max(s0, s1) + 0.3
            local nc = {}
            for _, c in ipairs(cuts) do
              for _, piece in ipairs(subtract({ c }, lo, hi)) do nc[#nc + 1] = piece end
            end
            cuts = nc
          end
        end
      end
      for k, c in ipairs(cuts) do
        local l = c[2] - c[1]
        if l > 0.6 then
          local p0 = { a[1] + ux * c[1], a[2] + uz * c[1] }
          local p1 = { a[1] + ux * c[2], a[2] + uz * c[2] }
          local q0 = { p0[1] + nx * 0.45, p0[2] + nz * 0.45 }
          local q1 = { p1[1] + nx * 0.45, p1[2] + nz * 0.45 }
          local pts = {}
          for _, q in ipairs({ p0, p1, q1, q0 }) do pts[#pts + 1] = { q[1], y, q[2] } pts[#pts + 1] = { q[1], y + 0.25, q[2] } end
          add_brush(R, hull("layout", R.name, "kerb" .. i .. "_" .. k, pts, { detail = true, texture = R.trim.texture }))
          local np = math.max(1, math.floor(l / 2.5 + 0.5))
          for j = 0, np do
            local f = j / np
            local px, pz = (q0[1] + (q1[1] - q0[1]) * f), (q0[2] + (q1[2] - q0[2]) * f)
            add_brush(R, member("layout", R.name, "post" .. i .. "_" .. k .. "_" .. j, { px, y + 0.25, pz }, { px, y + 1.1, pz }, 0.12, 0.12, R.trim))
          end
          add_brush(R, member("layout", R.name, "rail" .. i .. "_" .. k, { q0[1], y + 1.05, q0[2] }, { q1[1], y + 1.05, q1[2] }, 0.1, 0.1, R.trim))
        end
      end
    end
  end
end

local function rect_poly(x0, z0, x1, z1) return { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } } end
local function chamfered(x0, z0, x1, z1, c) return SP.snap16(SP.chamfer(rect_poly(x0, z0, x1, z1), c)) end

-- Pilasters on the zone's blank walls (no ports).
local function blank_pilasters(R, spacing)
  if not R.room then return end
  local list, counts = quiet_sides(R)
  local sides = {}
  for _, e in ipairs(counts) do if e[2] == 0 then sides[#sides + 1] = e[1] end end
  if #sides > 0 then add_rec(R, kit.pilasters{ name = R.name .. "_pilasters", room = R.room, sides = sides, spacing = spacing or 4, depth = 0.4, width = 0.8, texture = R.trim.texture }) end
end

-- Low cover blocks on a lattice inside a rectangle (KOTH hill cover, yards).
local function cover_blocks(R, part, y, x0, z0, x1, z1, pitch, h, skip_centre)
  local n = 0
  local nx = math.max(1, math.floor((x1 - x0) / pitch))
  local nz = math.max(1, math.floor((z1 - z0) / pitch))
  for i = 0, nx do
    for k = 0, nz do
      local x = x0 + (x1 - x0) * (i / nx)
      local z = z0 + (z1 - z0) * (k / nz)
      local along_x = ((i + k) % 2 == 0)
      local hw, hd = along_x and 1.25 or 0.625, along_x and 0.625 or 1.25
      local centre = skip_centre and math.abs(x - R.cx) < 3 and math.abs(z - R.cz) < 3
      if not centre and clear(R, x - hw - 1.2, z - hd - 1.2, x + hw + 1.2, z + hd + 1.2, y, y + h) then
        local ok = true
        for _, s in ipairs(R.solids) do
          if x - hw - 1.5 < s[3] and x + hw + 1.5 > s[1] and z - hd - 1.5 < s[4] and z + hd + 1.5 > s[2] and y < s[6] + 0.1 and y + h > s[5] then ok = false end
        end
        if ok then
          n = n + 1
          solid(R, part .. n, x - hw, y, z - hd, x + hw, y + h, z + hd, { detail = true })
        end
      end
    end
  end
  return n
end

-- True when a footprint keeps clear (by margin) of the solids already standing in the zone.
local function free_of_solids(R, x0, z0, x1, z1, y0, y1, margin)
  margin = margin or 0.5
  for _, so in ipairs(R.solids) do
    if x0 - margin < so[3] and x1 + margin > so[1] and z0 - margin < so[4] and z1 + margin > so[2] and y0 < so[6] and y1 > so[5] then return false end
  end
  return true
end

-- A big room becomes two spaces (exemplar median space 55-160 m2, spec a.1): a wall across its short axis with a
-- doorway near each end, so the halves loop through each other and no single 8 m cell cuts one off.
local function partition(R, min_area)
  if R.w * R.d < (min_area or 330) then return false end
  local long_x = R.w >= R.d
  local L = long_x and R.w or R.d
  local lo, hi = (long_x and R.z0 or R.x0), (long_x and R.z1 or R.x1)
  if hi - lo < 13 then return false end
  for _, f in ipairs({ 0, -0.1, 0.1, -0.18, 0.18 }) do
    local at = q2((long_x and R.cx or R.cz) + f * L)
    local a0, a1 = at - 0.5, at + 0.5
    local x0, z0, x1, z1 = (long_x and a0 or lo), (long_x and lo or a0), (long_x and a1 or hi), (long_x and hi or a1)
    if clear(R, x0, z0, x1, z1, R.y, R.y + 3) and free_of_solids(R, x0, z0, x1, z1, R.y, R.y + R.h) then
      local d1, d2 = q2(lo + R.c + 2.75), q2(hi - R.c - 2.75)
      local segs = { { lo, d1 - 1.5 }, { d1 + 1.5, d2 - 1.5 }, { d2 + 1.5, hi } }
      for i, sg in ipairs(segs) do
        if sg[2] - sg[1] > 0.25 then
          if long_x then solid(R, "partition" .. i, a0, R.y, sg[1], a1, R.y + R.h, sg[2])
          else solid(R, "partition" .. i, sg[1], R.y, a0, sg[2], R.y + R.h, a1) end
        end
      end
      -- keep the doorways clear of the features placed after it
      for _, d in ipairs({ d1, d2 }) do
        if long_x then note_solid(R, a0 - 1.5, d - 1.5, a1 + 1.5, d + 1.5, R.y, R.y + 0.01) else note_solid(R, d - 1.5, a0 - 1.5, d + 1.5, a1 + 1.5, R.y, R.y + 0.01) end
      end
      R.partition = { long_x = long_x, at = at }
      return true
    end
  end
  return false
end

-- Recipes ------------------------------------------------------------------------------------

local RECIPES = {}

RECIPES.hall = {
  shell = function(R)
    basic_shell(R, "stepped", { rise = 3, inset = math.max(3, math.min(R.w, R.d) * 0.25) })
  end,
  inner = function(R)
    partition(R, 260)
    local sides = quiet_sides(R)
    for _, side in ipairs(sides) do
      local x0, z0, x1, z1 = nil, nil, nil, nil
      local depth = 3
      if side == "+x" then x0, x1, z0, z1 = R.x1 - depth, R.x1, R.cz - math.min(6, R.d / 2 - R.c - 1), R.cz + math.min(6, R.d / 2 - R.c - 1)
      elseif side == "-x" then x0, x1, z0, z1 = R.x0, R.x0 + depth, R.cz - math.min(6, R.d / 2 - R.c - 1), R.cz + math.min(6, R.d / 2 - R.c - 1)
      elseif side == "+z" then z0, z1, x0, x1 = R.z1 - depth, R.z1, R.cx - math.min(6, R.w / 2 - R.c - 1), R.cx + math.min(6, R.w / 2 - R.c - 1)
      else z0, z1, x0, x1 = R.z0, R.z0 + depth, R.cx - math.min(6, R.w / 2 - R.c - 1), R.cx + math.min(6, R.w / 2 - R.c - 1) end
      if clear(R, x0, z0, x1, z1, R.y, R.y + 1) and free_of_solids(R, x0, z0, x1, z1, R.y, R.y + 1, 1) then
        note_solid(R, x0, z0, x1, z1, R.y, R.y + 1)
        add_rec(R, kit.floor_section{ name = R.name .. "_dais", room = R.room, min = { x0, R.y, z0 }, max = { x1, R.y, z1 }, rise = 1, textures = R.textures })
        R.spots[#R.spots + 1] = { (x0 + x1) / 2, R.y + 1, (z0 + z1) / 2 }
        break
      end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5)
  end,
  -- (with the room detail pass on, its wall sets take the place of the plain pilasters)
  late = function(R) if not (R.detail and R.detail.walls) then blank_pilasters(R, 4) end end,
  light_h = function(R) return R.h - 1 end,
}

RECIPES.storage = {
  shell = function(R)
    local long_x = R.w >= R.d
    local region = nil
    if long_x then region = { 0.5, -R.d / 2 + 2, R.w / 2 - 2, R.d / 2 - 2 } else region = { -R.w / 2 + 2, 0.5, R.w / 2 - 2, R.d / 2 - 2 } end
    basic_shell(R, "stepped", { rise = 3.5, region = region })
    R.long_x = long_x
  end,
  inner = function(R)
    partition(R, 280)
    -- Two-tier racks in the low bay (cover), on posts.
    local long_x = R.long_x
    for i, off in ipairs({ -0.22, 0.22 }) do
      local x0, z0, x1, z1 = nil, nil, nil, nil
      if long_x then
        x0, x1 = R.x0 + 3.5, R.cx - 2
        local z = R.cz + off * R.d
        z0, z1 = z - 0.6, z + 0.6
      else
        z0, z1 = R.z0 + 3.5, R.cz - 2
        local x = R.cx + off * R.w
        x0, x1 = x - 0.6, x + 0.6
      end
      if x1 - x0 > 2 and z1 - z0 > 1 and clear(R, x0 - 0.5, z0 - 0.5, x1 + 0.5, z1 + 0.5, R.y, R.y + 3) and free_of_solids(R, x0, z0, x1, z1, R.y, R.y + 3, 1) then
        note_solid(R, x0, z0, x1, z1, R.y, R.y + 2.6)
        for k, hgt in ipairs({ 1.2, 2.5 }) do box(R, "rack" .. i .. "_" .. k, x0, R.y + hgt - 0.12, z0, x1, R.y + hgt, z1, { detail = true, texture = R.trim.texture }) end
        local posts = long_x and { { x0 + 0.1, z0 + 0.1 }, { x0 + 0.1, z1 - 0.1 }, { x1 - 0.1, z0 + 0.1 }, { x1 - 0.1, z1 - 0.1 } } or
          { { x0 + 0.1, z0 + 0.1 }, { x1 - 0.1, z0 + 0.1 }, { x0 + 0.1, z1 - 0.1 }, { x1 - 0.1, z1 - 0.1 } }
        for j, p in ipairs(posts) do add_brush(R, member("layout", R.name, "rack" .. i .. "_post" .. j, { p[1], R.y, p[2] }, { p[1], R.y + 2.5, p[2] }, 0.12, 0.12, R.trim)) end
        -- a crate block under the low shelf: real cover
        solid(R, "rack" .. i .. "_crate", x0 + 0.3, R.y, z0 + 0.1, math.min(x1 - 0.3, x0 + 2.3), R.y + 1.0, z1 - 0.1, { detail = true })
      end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5)
  end,
  late = function(R)
    local n = math.max(1, math.floor((R.long_x and R.w or R.d) / 2 / 4))
    for i = 1, n do
      if R.long_x then
        local x = R.x0 + 2 + (i - 0.5) * ((R.w / 2 - 2) / n)
        add_rec(R, kit.beam{ name = R.name .. "_beam" .. i, from = { x, R.y + 4.1, R.z0 + 0.75 }, to = { x, R.y + 4.1, R.z1 - 0.75 }, size = 0.4, texture = R.trim.texture })
      else
        local z = R.z0 + 2 + (i - 0.5) * ((R.d / 2 - 2) / n)
        add_rec(R, kit.beam{ name = R.name .. "_beam" .. i, from = { R.x0 + 0.75, R.y + 4.1, z }, to = { R.x1 - 0.75, R.y + 4.1, z }, size = 0.4, texture = R.trim.texture })
      end
    end
  end,
  light_h = function() return 3.6 end,
}

RECIPES.colonnade = {
  shell = function(R) basic_shell(R, "coffered", {}) end,
  inner = function(R)
    partition(R, 300)
    local long_x = R.w >= R.d
    local L = long_x and R.w or R.d
    local S = long_x and R.d or R.w
    local n = math.max(2, math.floor((L - 8) / 5) + 1)
    local k = 0
    for _, side in ipairs({ -1, 1 }) do
      for i = 0, n - 1 do
        local a = -(L / 2 - 4) + i * ((L - 8) / (n - 1))
        local b = side * S / 4
        local x, z = long_x and (R.cx + a) or (R.cx + b), long_x and (R.cz + b) or (R.cz + a)
        if clear(R, x - 0.8, z - 0.8, x + 0.8, z + 0.8, R.y, R.y + R.h) and free_of_solids(R, x - 0.6, z - 0.6, x + 0.6, z + 0.6, R.y, R.y + R.h, 1) then
          k = k + 1
          note_solid(R, x - 0.6, z - 0.6, x + 0.6, z + 0.6, R.y, R.y + R.h)
          add_rec(R, kit.pillar_row{ name = R.name .. "_column" .. k, from = { x, R.y, z }, count = 1, height = R.h, size = 1.2, shape = "cylinder", detail = true, group = false, texture = R.trim.texture })
        end
      end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5)
  end,
  light_h = function(R) return R.h - 1.2 end,
}

-- The raised half of a split zone and its connectors (split and tower share it).
local function split_inner(R, open_air)
  local up = R.z.params and R.z.params.split or "+x"
  local s = R.step
  local x0, z0, x1, z1 = R.x0, R.z0, R.x1, R.z1
  local mx, mz = R.cx, R.cz
  local hx0, hz0, hx1, hz1 = nil, nil, nil, nil
  if up == "+x" then hx0, hz0, hx1, hz1 = mx, z0, x1, z1
  elseif up == "-x" then hx0, hz0, hx1, hz1 = x0, z0, mx, z1
  elseif up == "+z" then hx0, hz0, hx1, hz1 = x0, mz, x1, z1
  else hx0, hz0, hx1, hz1 = x0, z0, x1, mz end
  if open_air then
    -- the upper deck: a solid block on the slab (its own rocky look is the slab below)
    solid(R, "upper", hx0, R.y, hz0, hx1, R.y + s, hz1)
  else
    solid(R, "upper", hx0, R.y, hz0, hx1, R.y + s, hz1)
  end
  local run = stair_run(s)
  local along_x = (up == "+x" or up == "-x")
  local sign = (up == "+x" or up == "+z") and 1 or -1
  local edge = along_x and mx or mz
  local foot = edge - sign * run
  local cross = along_x and R.cz or R.cx
  local span = along_x and R.d or R.w
  local sa, sb = cross - span / 4, cross + span / 4
  if along_x then
    stairs(R, "up_stairs", foot, R.y, sa, up, s, 3.5)
    ramp(R, "up_ramp", edge - sign * ramp_run(s), R.y, sb, up, s, 3.5)
  else
    stairs(R, "up_stairs", sa, R.y, foot, up, s, 3.5)
    ramp(R, "up_ramp", sb, R.y, edge - sign * ramp_run(s), up, s, 3.5)
  end
  -- the raised half's edge drops a level (railed by the detail pass where nothing climbs onto it; a tower's deck has its own)
  if not open_air then
    if along_x then R.rails[#R.rails + 1] = { { edge, z0 }, { edge, z1 }, R.y + s }
    else R.rails[#R.rails + 1] = { { x0, edge }, { x1, edge }, R.y + s } end
  end
  -- spots: low half and the raised half
  local lx0, lz0, lx1, lz1 = nil, nil, nil, nil
  if up == "+x" then lx0, lz0, lx1, lz1 = x0, z0, mx - run - 1, z1
  elseif up == "-x" then lx0, lz0, lx1, lz1 = mx + run + 1, z0, x1, z1
  elseif up == "+z" then lx0, lz0, lx1, lz1 = x0, z0, x1, mz - run - 1
  else lx0, lz0, lx1, lz1 = x0, mz + run + 1, x1, z1 end
  grid_spots(R, R.y, lx0 + 2.5, lz0 + 2.5, lx1 - 2.5, lz1 - 2.5)
  grid_spots(R, R.y + s, hx0 + 2.5, hz0 + 2.5, hx1 - 2.5, hz1 - 2.5)
  R.objective_spot = { (lx0 + lx1) / 2, R.y, (lz0 + lz1) / 2 }
end

RECIPES.split = {
  shell = function(R) basic_shell(R, "flat", {}) end,
  inner = function(R) split_inner(R, false) end,
  light_h = function(R) return R.h - 1 end,
}

-- A gallery band (detail slabs) a level up along a convex outline, with posts and two stairs.
local function gallery_band(R, outline, bw, y_top, posts)
  local inner = I.inset_convex(outline, bw)
  if not inner or #inner ~= #outline then
    -- fall back to four rectangular strips
    inner = nil
  end
  if inner then
    for i = 1, #outline do
      local q = { outline[i], outline[i % #outline + 1], inner[i % #outline + 1], inner[i] }
      add_brush(R, I.prism("layout", R.name, "gallery" .. i, q, y_top - 0.5, y_top, { detail = true, textures = R.textures }))
    end
  else
    box(R, "gallery_s", R.x0, y_top - 0.5, R.z0, R.x1, y_top, R.z0 + bw, { detail = true })
    box(R, "gallery_n", R.x0, y_top - 0.5, R.z1 - bw, R.x1, y_top, R.z1, { detail = true })
    box(R, "gallery_w", R.x0, y_top - 0.5, R.z0 + bw, R.x0 + bw, y_top, R.z1 - bw, { detail = true })
    box(R, "gallery_e", R.x1 - bw, y_top - 0.5, R.z0 + bw, R.x1, y_top, R.z1 - bw, { detail = true })
    inner = rect_poly(R.x0 + bw, R.z0 + bw, R.x1 - bw, R.z1 - bw)
  end
  -- the band's inner edge drops to the floor below: the detail pass rails it (open where a stair or catwalk lands)
  for i = 1, #inner do R.rails[#R.rails + 1] = { inner[i], inner[i % #inner + 1], y_top } end
  if posts then
    local k = 0
    for i = 1, #inner do
      local a, b = inner[i], inner[i % #inner + 1]
      local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
      local np = math.max(1, math.floor(L / 6))
      for j = 0, np - 1 do
        local f = (j + 0.5) / np
        local px, pz = a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f
        if clear(R, px - 0.6, pz - 0.6, px + 0.6, pz + 0.6, R.y, y_top) then
          k = k + 1
          note_solid(R, px - 0.3, pz - 0.3, px + 0.3, pz + 0.3, R.y, y_top)
          add_brush(R, member("layout", R.name, "gpost" .. k, { px, R.y, pz }, { px, y_top - 0.5, pz }, 0.45, 0.45, R.trim))
        end
      end
    end
  end
  return inner
end

-- Posts under a gallery band's inner edge every ~6 m, clear of the floor ports and the stairs.
local function gallery_posts(R, inner, y_top)
  local k = 0
  for i = 1, #inner do
    local a, b = inner[i], inner[i % #inner + 1]
    local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
    local np = math.max(1, math.floor(L / 6))
    for j = 0, np - 1 do
      local f = (j + 0.5) / np
      local px, pz = a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f
      local ok = clear(R, px - 0.6, pz - 0.6, px + 0.6, pz + 0.6, R.y, y_top - 1)
      for _, so in ipairs(R.solids) do if px > so[1] - 0.8 and px < so[3] + 0.8 and pz > so[2] - 0.8 and pz < so[4] + 0.8 then ok = false end end
      if ok then
        k = k + 1
        note_solid(R, px - 0.3, pz - 0.3, px + 0.3, pz + 0.3, R.y, y_top)
        add_brush(R, member("layout", R.name, "gpost" .. k, { px, R.y, pz }, { px, y_top - 0.5, pz }, 0.45, 0.45, R.trim))
      end
    end
  end
end

-- Stairs up to a gallery band, perpendicular to the walls with the fewest floor ports (at most one per wall).
local function gallery_stairs(R, bw, rise, count)
  local run = stair_run(rise)
  local placed = 0
  local order = {}
  for _, side in ipairs(SIDES) do order[#order + 1] = { side, #ports_at(R, side, R.y) } end
  table.sort(order, function(p, q) if p[2] ~= q[2] then return p[2] < q[2] end return p[1] < q[1] end)
  local used_sides = {}
  for _, e in ipairs(order) do
    local side = e[1]
    if placed >= (count or 2) then break end
    if not used_sides[side] then
      local axis = (side == "+x" or side == "-x") and "x" or "z"
      local lo, hi = wall_span(R.z, side)
      local margin = R.c + bw + 2
      local mid = (lo + hi) / 2
      local cands = { mid }
      for k2 = 1, 6 do
        cands[#cands + 1] = mid - k2 * 1.5
        cands[#cands + 1] = mid + k2 * 1.5
      end
      for _, a in ipairs(cands) do
        a = q4(a)
        if a - 1.5 > lo + margin and a + 1.5 < hi - margin then
          local wall = wall_coord(R.z, side)
          local inward = (side == "+x" or side == "+z") and -1 or 1
          local foot_d = bw + run
          local fx, fz = nil, nil
          if axis == "x" then fx, fz = wall + inward * foot_d, a else fx, fz = a, wall + inward * foot_d end
          local bx0, bz0, bx1, bz1 = nil, nil, nil, nil
          if axis == "x" then bx0, bx1, bz0, bz1 = math.min(wall + inward * bw, fx), math.max(wall + inward * bw, fx), a - 1.5, a + 1.5
          else bz0, bz1, bx0, bx1 = math.min(wall + inward * bw, fz), math.max(wall + inward * bw, fz), a - 1.5, a + 1.5 end
          if clear(R, bx0 - 0.5, bz0 - 0.5, bx1 + 0.5, bz1 + 0.5, R.y, R.y + rise - 1) then
            local free = true
            for _, so in ipairs(R.solids) do if bx0 - 1 < so[3] and bx1 + 1 > so[1] and bz0 - 1 < so[4] and bz1 + 1 > so[2] then free = false end end
            if free then
              stairs(R, "stairs_" .. side:sub(2) .. (side:sub(1, 1) == "+" and "p" or "m"), fx, R.y, fz, side, rise, 3)
              placed = placed + 1
              used_sides[side] = true
              break
            end
          end
        end
      end
    end
  end
  if placed == 0 then R.warnings[#R.warnings + 1] = R.name .. ": no room for a stair up to the gallery; only its ports reach it." end
  return placed
end

RECIPES.gallery = {
  shell = function(R) basic_shell(R, "beamed", {}) end,
  inner = function(R)
    local bw = 3
    local outline = R.room.outline
    local inner = gallery_band(R, outline, bw, R.y + R.step, false)
    gallery_stairs(R, bw, R.step, 2)
    gallery_posts(R, inner, R.y + R.step)
    grid_spots(R, R.y, R.x0 + bw + 1.5, R.z0 + bw + 1.5, R.x1 - bw - 1.5, R.z1 - bw - 1.5)
    for _, s in ipairs({ { R.cx, R.z0 + bw / 2 }, { R.cx, R.z1 - bw / 2 }, { R.x0 + bw / 2, R.cz }, { R.x1 - bw / 2, R.cz } }) do
      R.spots[#R.spots + 1] = { s[1], R.y + R.step, s[2] }
    end
  end,
  light_h = function(R) return R.h - 1.5 end,
}

RECIPES.atrium = {
  shell = function(R) basic_shell(R, "flat", {}) end,
  inner = function(R)
    local bw = 3
    local outline = R.room.outline
    local inner = gallery_band(R, outline, bw, R.y + R.step, false)
    gallery_stairs(R, bw, R.step, 2)
    gallery_posts(R, inner, R.y + R.step)
    -- A truss catwalk across the void at gallery level, along the longer axis, off the centre.
    local long_x = R.w >= R.d
    local off = (R.z.params and R.z.params.catwalk) or 3.5
    if long_x then
      local z = R.cz + off
      if clear(R, R.x0 + bw - 0.5, z - 2, R.x1 - bw + 0.5, z + 2, R.y + R.step - 1, R.y + R.step + 3) then
        add_rec(R, kit.bridge{ purpose = "quiet", name = R.name .. "_catwalk", from = { R.x0 + bw - 0.5, R.y + R.step + 0.125, z }, to = { R.x1 - bw + 0.5, R.y + R.step + 0.125, z },
          width = 3, style = "truss", depth = 1.6, landmark = false, group = false, textures = R.textures, frame_texture = R.trim.texture })
      end
    else
      local x = R.cx + off
      if clear(R, x - 2, R.z0 + bw - 0.5, x + 2, R.z1 - bw + 0.5, R.y + R.step - 1, R.y + R.step + 3) then
        add_rec(R, kit.bridge{ purpose = "quiet", name = R.name .. "_catwalk", from = { x, R.y + R.step + 0.125, R.z0 + bw - 0.5 }, to = { x, R.y + R.step + 0.125, R.z1 - bw + 0.5 },
          width = 3, style = "truss", depth = 1.6, landmark = false, group = false, textures = R.textures, frame_texture = R.trim.texture })
      end
    end
    -- low cover in the hall floor
    cover_blocks(R, "cover", R.y, R.cx - R.w / 4, R.cz - R.d / 4, R.cx + R.w / 4, R.cz + R.d / 4, 7, 1.3, true)
    grid_spots(R, R.y, R.x0 + bw + 2, R.z0 + bw + 2, R.x1 - bw - 2, R.z1 - bw - 2)
    local _ = inner
  end,
  light_h = function(R) return R.h - 2 end,
}

RECIPES.arena = {
  shell = function(R) basic_shell(R, "stepped", { rise = 4, inset = 7 }) end,
  inner = function(R)
    solid(R, "block", R.cx - 5, R.y, R.cz - 5, R.cx + 5, R.y + 3, R.cz + 5)
    box(R, "tunnel", R.cx - 5.5, R.y, R.cz - 1.5, R.cx + 5.5, R.y + 2.5, R.cz + 1.5, { air = true })
    ramp(R, "ramp_s", R.cx, R.y, R.cz - 11, "+z", 3, 3, 6)
    ramp(R, "ramp_n", R.cx, R.y, R.cz + 11, "-z", 3, 3, 6)
    local q = math.min(R.w, R.d) / 2 - 7
    local k = 0
    for _, s in ipairs({ { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } }) do
      local x, z = R.cx + s[1] * q, R.cz + s[2] * q
      if clear(R, x - 0.8, z - 0.8, x + 0.8, z + 0.8, R.y, R.y + R.h) then
        k = k + 1
        note_solid(R, x - 0.6, z - 0.6, x + 0.6, z + 0.6, R.y, R.y + R.h)
        add_rec(R, kit.pillar_row{ name = R.name .. "_column" .. k, from = { x, R.y, z }, count = 1, height = R.h, size = 1.2, shape = "cylinder", detail = true, group = false, texture = R.trim.texture })
      end
    end
    grid_spots(R, R.y, R.x0 + 3, R.z0 + 3, R.x1 - 3, R.z1 - 3, 5)
    R.spots[#R.spots + 1] = { R.cx + 3, R.y + 3, R.cz + 3 }
    R.spots[#R.spots + 1] = { R.cx - 3, R.y + 3, R.cz - 3 }
    R.objective_spot = { R.cx, R.y + 3, R.cz + 3 }
  end,
  light_h = function(R) return R.h - 1 end,
}

-- The sunken court: ring band, stairs down, cover in the court (KOTH hill and DM hub).
RECIPES.sunken = {
  shell = function(R)
    local depth = R.z.params and R.z.params.depth or 4.5
    R.depth = depth
    local rw = 6
    R.rw = rw
    local cx0, cz0, cx1, cz1 = R.x0 + rw, R.z0 + rw, R.x1 - rw, R.z1 - rw
    shaped(R, { pos = { R.cx, R.y - depth, R.cz }, size = { R.w, R.d }, chamfer = R.c, height = R.h + depth, ceiling = "stepped", rise = 4,
      region = { cx0 - R.cx + 1, cz0 - R.cz + 1, cx1 - R.cx - 1, cz1 - R.cz - 1 } })
  end,
  inner = function(R)
    local depth, rw = R.depth, R.rw
    local yb = R.y - depth
    local cx0, cz0, cx1, cz1 = R.x0 + rw, R.z0 + rw, R.x1 - rw, R.z1 - rw
    solid(R, "ring_s", R.x0, yb, R.z0, R.x1, R.y, cz0)
    solid(R, "ring_n", R.x0, yb, cz1, R.x1, R.y, R.z1)
    -- (the side pieces run half a metre into the end pieces: no two pieces meet face to face, which a turned
    -- frame cannot hold exactly)
    solid(R, "ring_w", R.x0, yb, cz0 - 0.5, cx0, R.y, cz1 + 0.5)
    solid(R, "ring_e", cx1, yb, cz0 - 0.5, R.x1, R.y, cz1 + 0.5)
    R.solids = {} -- the ring is floor, not an obstacle
    local run = stair_run(depth)
    local off = R.z.params and R.z.params.stair_offset or 4
    stairs(R, "stairs_s", R.cx - off, yb, cz0 + run, "-z", depth, 3)
    stairs(R, "stairs_n", R.cx + off, yb, cz1 - run, "+z", depth, 3)
    stairs(R, "stairs_w", cx0 + run, yb, R.cz + off, "-x", depth, 3)
    stairs(R, "stairs_e", cx1 - run, yb, R.cz - off, "+x", depth, 3)
    -- Cover every 5-6 m in the court (E22), the centre left open for the hill.
    cover_blocks(R, "cover", yb, cx0 + 3, cz0 + 3, cx1 - 3, cz1 - 3, 5.5, 1.4, true)
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z0 + 3.5)
    grid_spots(R, R.y, R.x0 + 2.5, R.z1 - 3.5, R.x1 - 2.5, R.z1 - 2.5)
    R.objective_spot = { R.cx, yb, R.cz }
    R.court = { cx0, cz0, cx1, cz1, yb } -- (its rim stays open: players drop into the court from every side)
  end,
  light_h = function(R) return R.h - 1.5 end,
}

local function mound_inner(R)
    local rise = R.z.params and R.z.params.depth or 4.5
    local p = math.min(R.w, R.d) - 2 * (ramp_run(rise) + 1)
    p = math.max(12, p)
    local poly = chamfered(R.cx - p / 2, R.cz - p / 2, R.cx + p / 2, R.cz + p / 2, p * 0.25)
    add_brush(R, I.prism("layout", R.name, "plinth", poly, R.y, R.y + rise, { textures = R.textures }))
    note_solid(R, R.cx - p / 2, R.cz - p / 2, R.cx + p / 2, R.cz + p / 2, R.y, R.y + rise)
    local rr = ramp_run(rise)
    ramp(R, "ramp_w", R.cx - p / 2 - rr, R.y, R.cz, "+x", rise, 4, rr)
    ramp(R, "ramp_e", R.cx + p / 2 + rr, R.y, R.cz, "-x", rise, 4, rr)
    local sr = stair_run(rise)
    stairs(R, "stairs_s", R.cx, R.y, R.cz - p / 2 - sr, "+z", rise, 3.5)
    stairs(R, "stairs_n", R.cx, R.y, R.cz + p / 2 + sr, "-z", rise, 3.5)
    -- Low walls on top: cover for the hill.
    local top = R.y + rise
    local k = 0
    for _, s in ipairs({ { 1, 1 }, { -1, 1 }, { -1, -1 }, { 1, -1 } }) do
      k = k + 1
      local x, z = R.cx + s[1] * p * 0.2, R.cz + s[2] * p * 0.2
      if k % 2 == 1 then solid(R, "wall" .. k, x - 1.5, top, z - 0.3, x + 1.5, top + 1.2, z + 0.3, { detail = true }) else solid(R, "wall" .. k, x - 0.3, top, z - 1.5, x + 0.3, top + 1.2, z + 1.5, { detail = true }) end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5, 5)
    R.objective_spot = { R.cx, top, R.cz }
end

RECIPES.mound = {
  shell = function(R) basic_shell(R, "stepped", { rise = 3, inset = 10 }) end,
  inner = mound_inner,
  light_h = function(R) return R.h - 1.5 end,
}


RECIPES.courtyard = {
  shell = function(R)
    local rec = basic_shell(R, "flat", {})
    I.sky_up(rec.brushes)
    R.sky = true
  end,
  inner = function(R)
    partition(R, 300)
    if clear(R, R.cx - 3.5, R.cz - 2.5, R.cx + 3.5, R.cz + 2.5, R.y, R.y + 1) and free_of_solids(R, R.cx - 3.5, R.cz - 2.5, R.cx + 3.5, R.cz + 2.5, R.y, R.y + 1, 0.5) then
      note_solid(R, R.cx - 3, R.cz - 2, R.cx + 3, R.cz + 2, R.y, R.y + 0.75)
      add_rec(R, kit.floor_section{ name = R.name .. "_planter", room = R.room, min = { R.cx - 3, R.y, R.cz - 2 }, max = { R.cx + 3, R.y, R.cz + 2 }, rise = 0.75, textures = R.textures })
    end
    local k = 0
    for _, s in ipairs({ { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } }) do
      local x, z = R.cx + s[1] * R.w / 4, R.cz + s[2] * R.d / 4
      local along_x = (s[1] * s[2] > 0)
      local hw, hd = along_x and 1.75 or 0.375, along_x and 0.375 or 1.75
      if clear(R, x - hw - 1, z - hd - 1, x + hw + 1, z + hd + 1, R.y, R.y + 1.3) and free_of_solids(R, x - hw, z - hd, x + hw, z + hd, R.y, R.y + 1.3, 1) then
        k = k + 1
        solid(R, "wall" .. k, x - hw, R.y, z - hd, x + hw, R.y + 1.3, z + hd, { detail = true })
      end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5)
    R.objective_spot = { R.cx, R.y + 0.75, R.cz }
  end,
  light_h = function(R) return 7 end,
}

RECIPES.pit = {
  shell = function(R) basic_shell(R, "stepped", { rise = 3, inset = 6 }) end,
  inner = function(R)
    local m = 6
    local px0, pz0, px1, pz1 = R.x0 + m, R.z0 + m, R.x1 - m, R.z1 - m
    -- A chasm, not a room below: deeper than a survivable drop (DATA.drop_max), so it has no floor to fight on and no
    -- stairs into it; the plank bridge is the short way across and the rim the long way round. Its air is not the
    -- zone's floor (no kit.zone tag): spawns, items and dressing by zone name keep to the rim.
    local depth = DATA.pit_depth
    local pit = add_rec(R, kit.floor_section{ name = R.name .. "_pit", min = { px0, R.y, pz0 }, max = { px1, R.y, pz1 }, y = R.y, rise = -depth, textures = R.textures })
    R.untagged = R.untagged or {}
    for _, b in ipairs((type(pit) == "table" and pit.brushes) or {}) do R.untagged[b.uid] = true end
    -- the plank bridge across the pit along x
    add_rec(R, kit.bridge{ purpose = "quiet", name = R.name .. "_bridge", from = { px0 - 1, R.y + 0.125, R.cz }, to = { px1 + 1, R.y + 0.125, R.cz }, width = 3, style = "plank",
      landmark = false, group = false, texture = R.trim.texture })
    -- the rim and the deck's sides drop into the chasm: the detail pass rails them (open where the deck lands)
    for _, e in ipairs({ { { px0, pz0 }, { px1, pz0 } }, { { px1, pz0 }, { px1, pz1 } }, { { px1, pz1 }, { px0, pz1 } }, { { px0, pz1 }, { px0, pz0 } } }) do
      R.rails[#R.rails + 1] = { e[1], e[2], R.y }
    end
    for _, sz in ipairs({ -1.5, 1.5 }) do R.rails[#R.rails + 1] = { { px0 - 1, R.cz + sz }, { px1 + 1, R.cz + sz }, R.y + 0.125 } end
    note_solid(R, px0, pz0, px1, pz1, R.y - depth, R.y)
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z0 + 3.5)
    grid_spots(R, R.y, R.x0 + 2.5, R.z1 - 3.5, R.x1 - 2.5, R.z1 - 2.5)
    R.objective_spot = { R.x0 + 3, R.y, R.cz }
  end,
  light_h = function(R) return R.h - 1 end,
}

RECIPES.tunnel = {
  shell = function(R) basic_shell(R, "beamed", {}) end,
  inner = function(R) grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5) end,
  late = function(R)
    if R.detail and R.detail.walls then return end -- (the room detail pass lines the walls instead)
    local long_x = R.w >= R.d
    local L = long_x and R.w or R.d
    local n = math.max(1, math.floor(L / 6))
    local k = 0
    for i = 1, n - 1 do
      local a = -L / 2 + i * L / n
      for _, side in ipairs({ -1, 1 }) do
        local x, z = nil, nil
        if long_x then x, z = R.cx + a, (side < 0) and (R.z0 + 0.35) or (R.z1 - 0.35) else x, z = (side < 0) and (R.x0 + 0.35) or (R.x1 - 0.35), R.cz + a end
        if clear(R, x - 0.6, z - 0.6, x + 0.6, z + 0.6, R.y, R.y + R.h) then
          k = k + 1
          add_rec(R, kit.beam{ name = R.name .. "_post" .. k, from = { x, R.y, z }, to = { x, R.y + R.h, z }, size = 0.5, texture = R.trim.texture })
        end
      end
    end
  end,
  light_h = function(R) return R.h - 0.8 end,
}

RECIPES.nook = {
  shell = function(R)
    local rec = add_rec(R, kit.room{ name = R.name .. "_room", pos = { R.cx, R.y, R.cz }, size = { R.w, R.h, R.d }, textures = R.textures })
    R.room = rec
  end,
  inner = function(R)
    R.spots[#R.spots + 1] = { R.cx, R.y, R.cz }
    R.objective_spot = { R.cx, R.y, R.cz }
  end,
  light_h = function(R) return R.h - 0.8 end,
}

-- Open air: slabs over the void. The zone's floor is the slab top.
local function slab_shell(R, under)
  local poly = chamfered(R.x0, R.z0, R.x1, R.z1, R.c)
  floating(R, "slab", poly, R.y, 1.5, under or 8)
  R.outline = poly
  return poly
end

RECIPES.isle = {
  shell = function(R) slab_shell(R, 12) end,
  inner = function(R)
    local k = 0
    for _, s in ipairs({ { -1, 0.3 }, { 1, -0.3 }, { 0.3, 1 } }) do
      local x, z = R.cx + s[1] * R.w * 0.22, R.cz + s[2] * R.d * 0.22
      if clear(R, x - 2.5, z - 2.5, x + 2.5, z + 2.5, R.y, R.y + 2) then
        k = k + 1
        local pts = {}
        local st = I.stream((R.z.params and R.z.params.seed or 1) * 31 + k)
        for j = 0, 7 do
          local a = j / 8 * 2 * math.pi
          local r = 1.4 + st() * 0.8
          pts[#pts + 1] = { x + math.cos(a) * r, R.y, z + math.sin(a) * r }
          pts[#pts + 1] = { x + math.cos(a) * r * 0.6, R.y + 1.3 + st() * 0.6, z + math.sin(a) * r * 0.6 }
        end
        note_solid(R, x - 2.2, z - 2.2, x + 2.2, z + 2.2, R.y, R.y + 2)
        add_brush(R, hull("layout", R.name, "rock" .. k, pts, { detail = true, textures = R.textures }))
      end
    end
    grid_spots(R, R.y, R.x0 + R.c * 0.5 + 2, R.z0 + R.c * 0.5 + 2, R.x1 - R.c * 0.5 - 2, R.z1 - R.c * 0.5 - 2)
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 6 end,
}

RECIPES.garden = RECIPES.isle

RECIPES.plaza = {
  shell = function(R) slab_shell(R, 9) end,
  inner = function(R)
    local h = math.min(R.w, R.d) * 0.18
    if clear(R, R.cx - h - 1, R.cz - h - 1, R.cx + h + 1, R.cz + h + 1, R.y, R.y + 1) then
      solid(R, "dais", R.cx - h, R.y, R.cz - h, R.cx + h, R.y + 0.75, R.cz + h)
      R.spots[#R.spots + 1] = { R.cx, R.y + 0.75, R.cz }
    end
    cover_blocks(R, "wall", R.y, R.cx - R.w / 3, R.cz - R.d / 3, R.cx + R.w / 3, R.cz + R.d / 3, 8, 1.2, true)
    local k = 0
    for _, s in ipairs({ { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } }) do
      local x, z = R.cx + s[1] * (R.w / 2 - R.c * 0.6 - 2), R.cz + s[2] * (R.d / 2 - R.c * 0.6 - 2)
      if clear(R, x - 0.5, z - 0.5, x + 0.5, z + 0.5, R.y, R.y + 4) and free_of_solids(R, x - 0.3, z - 0.3, x + 0.3, z + 0.3, R.y, R.y + 4, 0.5) then
        k = k + 1
        note_solid(R, x - 0.3, z - 0.3, x + 0.3, z + 0.3, R.y, R.y + 4)
        add_rec(R, kit.beam{ name = R.name .. "_lamp" .. k, from = { x, R.y, z }, to = { x, R.y + 4, z }, size = 0.3, texture = R.trim.texture })
      end
    end
    grid_spots(R, R.y, R.x0 + R.c * 0.5 + 2, R.z0 + R.c * 0.5 + 2, R.x1 - R.c * 0.5 - 2, R.z1 - R.c * 0.5 - 2)
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 5 end,
}

RECIPES.mast = {
  shell = function(R) slab_shell(R, 8) end,
  inner = function(R)
    if clear(R, R.cx - 3, R.cz - 3, R.cx + 3, R.cz + 3, R.y, R.y + 25) then
      note_solid(R, R.cx - 2.5, R.cz - 2.5, R.cx + 2.5, R.cz + 2.5, R.y, R.y + 25)
      add_rec(R, kit.landmark{ purpose = "quiet", name = R.name .. "_mast", pos = { R.cx, R.y, R.cz }, height = 25, base = 5, style = "pylon", group = false, texture = R.trim.texture })
    end
    local k = 0
    for _, s in ipairs({ { 5, 1.5 }, { -5, -1.5 }, { 1.5, -5 }, { -1.5, 5 } }) do
      local x, z = R.cx + s[1], R.cz + s[2]
      if clear(R, x - 1.5, z - 1.5, x + 1.5, z + 1.5, R.y, R.y + 1.5) and free_of_solids(R, x - 1, z - 1, x + 1, z + 1, R.y, R.y + 1.5, 0.5) then
        k = k + 1
        solid(R, "crate" .. k, x - 1, R.y, z - 1, x + 1, R.y + 1.5, z + 1, { detail = true })
      end
    end
    grid_spots(R, R.y, R.x0 + R.c + 2, R.z0 + R.c + 2, R.x1 - R.c - 2, R.z1 - R.c - 2)
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 5 end,
}

RECIPES.gantry = {
  shell = function(R) slab_shell(R, 6) end,
  inner = function(R)
    local long_x = R.w >= R.d
    local L = long_x and R.w or R.d
    local half = (long_x and R.d or R.w) / 2 - 1.5
    local a0, a1 = -L / 2 + 2, L / 2 - 2
    local function P(a, b, y) if long_x then return { R.cx + a, y, R.cz + b } end return { R.cx + b, y, R.cz + a } end
    add_rec(R, kit.truss{ name = R.name .. "_gantry", from = P(a0, 0, R.y + 5.5), to = P(a1, 0, R.y + 5.5), depth = 1.5, width = 2 * half,
      group = false, texture = R.trim.texture })
    for _, a in ipairs({ a0, a1 }) do
      for _, b in ipairs({ -half, half }) do
        local p0 = P(a, b, R.y)
        if clear(R, p0[1] - 0.5, p0[3] - 0.5, p0[1] + 0.5, p0[3] + 0.5, R.y, R.y + 5) then
          note_solid(R, p0[1] - 0.3, p0[3] - 0.3, p0[1] + 0.3, p0[3] + 0.3, R.y, R.y + 5.5)
          add_rec(R, kit.beam{ name = R.name .. "_leg" .. #R.brushes, from = p0, to = P(a, b, R.y + 5.5), size = 0.5, texture = R.trim.texture })
        end
      end
    end
    local k = 0
    for _, f in ipairs({ -0.25, 0.25 }) do
      for _, b in ipairs({ -half * 0.45, half * 0.45 }) do
        local c = P(f * L, (f > 0) and b or -b, R.y)
        if clear(R, c[1] - 2, c[3] - 2, c[1] + 2, c[3] + 2, R.y, R.y + 2) and free_of_solids(R, c[1] - 1.5, c[3] - 1.5, c[1] + 1.5, c[3] + 1.5, R.y, R.y + 2, 1) then
          k = k + 1
          solid(R, "crates" .. k, c[1] - 1.25, R.y, c[3] - 1, c[1] + 1.25, R.y + 1.5, c[3] + 1, { detail = true })
        end
      end
    end
    grid_spots(R, R.y, R.x0 + R.c + 2, R.z0 + R.c + 2, R.x1 - R.c - 2, R.z1 - R.c - 2)
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 4.5 end,
}

RECIPES.outpost = {
  shell = function(R) slab_shell(R, 7) end,
  inner = function(R)
    local hx, hz = 3.5, 2.5
    local long_x = R.w >= R.d
    if not long_x then hx, hz = 2.5, 3.5 end
    local x0, z0, x1, z1 = R.cx - hx, R.cz - hz, R.cx + hx, R.cz + hz
    if clear(R, x0 - 1, z0 - 1, x1 + 1, z1 + 1, R.y, R.y + 4) then
      solid(R, "bunker", x0, R.y, z0, x1, R.y + 4, z1)
      box(R, "bunker_air", x0 + 0.5, R.y, z0 + 0.5, x1 - 0.5, R.y + 3, z1 - 0.5, { air = true })
      if long_x then
        box(R, "bunker_door_w", x0 - 0.25, R.y, R.cz - 1.25, x0 + 0.75, R.y + 2.75, R.cz + 1.25, { air = true })
        box(R, "bunker_door_e", x1 - 0.75, R.y, R.cz - 1.25, x1 + 0.25, R.y + 2.75, R.cz + 1.25, { air = true })
      else
        box(R, "bunker_door_s", R.cx - 1.25, R.y, z0 - 0.25, R.cx + 1.25, R.y + 2.75, z0 + 0.75, { air = true })
        box(R, "bunker_door_n", R.cx - 1.25, R.y, z1 - 0.75, R.cx + 1.25, R.y + 2.75, z1 + 0.25, { air = true })
      end
      R.spots[#R.spots + 1] = { R.cx, R.y, R.cz }
    end
    cover_blocks(R, "wall", R.y, R.x0 + R.c + 2, R.z0 + R.c + 2, R.x1 - R.c - 2, R.z1 - R.c - 2, 7, 1.2, false)
    grid_spots(R, R.y, R.x0 + R.c + 2, R.z0 + R.c + 2, R.x1 - R.c - 2, R.z1 - R.c - 2)
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 5 end,
}

RECIPES.summit = {
  shell = function(R) slab_shell(R, 12) end,
  inner = mound_inner,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 7 end,
}

RECIPES.deck = {
  shell = function(R) slab_shell(R, 6) end,
  inner = function(R)
    -- A canopy on posts over a free corner, and a deckhouse in another.
    local spots = { { R.cx - R.w / 4, R.cz - R.d / 4 }, { R.cx + R.w / 4, R.cz + R.d / 4 }, { R.cx - R.w / 4, R.cz + R.d / 4 }, { R.cx + R.w / 4, R.cz - R.d / 4 } }
    local canopy, house = false, false
    for _, s in ipairs(spots) do
      if not canopy and clear(R, s[1] - 4, s[2] - 3, s[1] + 4, s[2] + 3, R.y, R.y + 4.5) then
        canopy = true
        note_solid(R, s[1] - 4, s[2] - 3, s[1] + 4, s[2] + 3, R.y, R.y + 4)
        add_rec(R, kit.platform{ name = R.name .. "_canopy", pos = { s[1], R.y, s[2] }, size = { 8, 6 }, height = 4, thickness = 0.3, supports = true, detail = true, group = false, texture = R.trim.texture })
      elseif canopy and not house and clear(R, s[1] - 4.5, s[2] - 3.5, s[1] + 4.5, s[2] + 3.5, R.y, R.y + 5) then
        house = true
        local hx0, hz0, hx1, hz1 = s[1] - 3.5, s[2] - 2.5, s[1] + 3.5, s[2] + 2.5
        solid(R, "house", hx0, R.y, hz0, hx1, R.y + 4.5, hz1)
        box(R, "house_air", hx0 + 0.5, R.y, hz0 + 0.5, hx1 - 0.5, R.y + 3.5, hz1 - 0.5, { air = true })
        local dirx = (R.cx > s[1]) and 1 or -1
        if dirx > 0 then box(R, "house_door", hx1 - 0.75, R.y, s[2] - 1.5, hx1 + 0.25, R.y + 3, s[2] + 1.5, { air = true })
        else box(R, "house_door", hx0 - 0.25, R.y, s[2] - 1.5, hx0 + 0.75, R.y + 3, s[2] + 1.5, { air = true }) end
      end
    end
    grid_spots(R, R.y, R.x0 + R.c + 1.5, R.z0 + R.c + 1.5, R.x1 - R.c - 1.5, R.z1 - R.c - 1.5)
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 6 end,
}

RECIPES.tower = {
  shell = function(R) slab_shell(R, 10) end,
  inner = function(R)
    split_inner(R, true)
    local up = R.z.params and R.z.params.split or "+x"
    local x, z = nil, nil
    if up == "+x" then x, z = R.x1 - 4, R.z1 - 4 elseif up == "-x" then x, z = R.x0 + 4, R.z0 + 4
    elseif up == "+z" then x, z = R.x0 + 4, R.z1 - 4 else x, z = R.x1 - 4, R.z0 + 4 end
    add_rec(R, kit.landmark{ purpose = "quiet", name = R.name .. "_mast", pos = { x, R.y + R.step, z }, height = 26, base = 4, style = "pylon", group = false, texture = R.trim.texture })
    note_solid(R, x - 2.5, z - 2.5, x + 2.5, z + 2.5, R.y + R.step, R.y + R.step + 26)
  end,
  late = function(R)
    edges(R, R.outline, R.y)
    -- rails round the upper deck's open edges
    local up = R.z.params and R.z.params.split or "+x"
    local s = R.step
    local hx0, hz0, hx1, hz1 = nil, nil, nil, nil
    if up == "+x" then hx0, hz0, hx1, hz1 = R.cx, R.z0, R.x1, R.z1 elseif up == "-x" then hx0, hz0, hx1, hz1 = R.x0, R.z0, R.cx, R.z1
    elseif up == "+z" then hx0, hz0, hx1, hz1 = R.x0, R.cz, R.x1, R.z1 else hx0, hz0, hx1, hz1 = R.x0, R.z0, R.x1, R.cz end
    local saved = R.ports
    R.ports = {}
    for _, p in ipairs(saved) do R.ports[#R.ports + 1] = p end
    edges(R, rect_poly(hx0 + 0.01, hz0 + 0.01, hx1 - 0.01, hz1 - 0.01), R.y + s, function(a, b)
      -- only the inner (split) edge, which the stair and ramp meet: leave it open
      local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
      if up == "+x" or up == "-x" then return math.abs(mx - R.cx) < 0.5 end
      return math.abs(mz - R.cz) < 0.5
    end)
    R.ports = saved
  end,
  light_h = function(R) return R.step + 6 end,
}

RECIPES.ring = {
  shell = function(R)
    -- One slab and one tapered underside over the whole plan, the square hole carved through both by an air box
    -- (slab pieces meeting on the corner diagonals would put two brushes' faces on one oblique plane whenever the
    -- ring is oblong or turned, which the compile cannot hold exactly)
    local hole = g16(math.min(R.w, R.d) * 0.18) * 2
    local ix0, iz0, ix1, iz1 = g16(R.cx - hole / 2), g16(R.cz - hole / 2), g16(R.cx + hole / 2), g16(R.cz + hole / 2)
    R.hole = { ix0, iz0, ix1, iz1 }
    R.outline = rect_poly(R.x0, R.z0, R.x1, R.z1)
    floating(R, "slab", R.outline, R.y, 1.5, 6)
    box(R, "hole", ix0, R.y - 9, iz0, ix1, R.y + 1, iz1, { air = true })
  end,
  inner = function(R)
    local h = R.hole
    local k = 0
    for _, s in ipairs({ { R.cx, (R.z0 + h[2]) / 2 }, { R.cx, (R.z1 + h[4]) / 2 }, { (R.x0 + h[1]) / 2, R.cz }, { (R.x1 + h[3]) / 2, R.cz } }) do
      if clear(R, s[1] - 2, s[2] - 2, s[1] + 2, s[2] + 2, R.y, R.y + 1.4) then
        k = k + 1
        solid(R, "cover" .. k, s[1] - 1.2, R.y, s[2] - 0.5, s[1] + 1.2, R.y + 1.3, s[2] + 0.5, { detail = true })
      end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, h[2] - 1.5)
    grid_spots(R, R.y, R.x0 + 2.5, h[4] + 1.5, R.x1 - 2.5, R.z1 - 2.5)
    R.objective_spot = { R.cx, R.y, (R.z0 + h[2]) / 2 }
  end,
  late = function(R)
    edges(R, R.outline, R.y)
    local h = R.hole
    -- the inner edge, wound the other way so "inward" faces the deck
    edges(R, { { h[1], h[2] }, { h[1], h[4] }, { h[3], h[4] }, { h[3], h[2] } }, R.y)
  end,
  light_h = function() return 6 end,
}

RECIPES.terrace = {
  shell = function(R)
    local up = R.z.params and R.z.params.split or "+x"
    local gap = 4
    local lo, hi = nil, nil
    if up == "+x" then lo, hi = { R.x0, R.z0, R.cx - gap / 2, R.z1 }, { R.cx + gap / 2, R.z0, R.x1, R.z1 }
    elseif up == "-x" then lo, hi = { R.cx + gap / 2, R.z0, R.x1, R.z1 }, { R.x0, R.z0, R.cx - gap / 2, R.z1 }
    elseif up == "+z" then lo, hi = { R.x0, R.z0, R.x1, R.cz - gap / 2 }, { R.x0, R.cz + gap / 2, R.x1, R.z1 }
    else lo, hi = { R.x0, R.cz + gap / 2, R.x1, R.z1 }, { R.x0, R.z0, R.x1, R.cz - gap / 2 } end
    R.lo_rect, R.hi_rect = lo, hi
    floating(R, "low", chamfered(lo[1], lo[2], lo[3], lo[4], R.c), R.y, 1.5, 6)
    floating(R, "high", chamfered(hi[1], hi[2], hi[3], hi[4], R.c), R.y + R.step, 1.5, 8)
  end,
  inner = function(R)
    local up = R.z.params and R.z.params.split or "+x"
    local along_x = (up == "+x" or up == "-x")
    local lo, hi = R.lo_rect, R.hi_rect
    local s = R.step
    -- a flying stair and a sloped plank across the gap, each meeting both decks
    local cross = along_x and R.cz or R.cx
    local span = along_x and R.d or R.w
    local a, b = cross - span / 4, cross + span / 4
    local sign = (up == "+x" or up == "+z") and 1 or -1
    local e0 = along_x and (sign > 0 and lo[3] or lo[1]) or (sign > 0 and lo[4] or lo[2])
    local e1 = along_x and (sign > 0 and hi[1] or hi[3]) or (sign > 0 and hi[2] or hi[4])
    local run = stair_run(s)
    local foot = e0 - sign * (run - math.abs(e1 - e0)) -- the stair starts on the low deck so its top lands on the high one
    if along_x then
      stairs(R, "fly_stairs", foot, R.y, a, up, s, 3)
      add_rec(R, kit.bridge{ purpose = "quiet", name = R.name .. "_plank", from = { e0 - sign * 6, R.y + 0.125, b }, to = { e1 + sign * 1, R.y + s + 0.125, b }, width = 3, style = "plank", landmark = false, group = false, texture = R.trim.texture })
    else
      stairs(R, "fly_stairs", a, R.y, foot, up, s, 3)
      add_rec(R, kit.bridge{ purpose = "quiet", name = R.name .. "_plank", from = { b, R.y + 0.125, e0 - sign * 6 }, to = { b, R.y + s + 0.125, e1 + sign * 1 }, width = 3, style = "plank", landmark = false, group = false, texture = R.trim.texture })
    end
    grid_spots(R, R.y, lo[1] + 2.5, lo[2] + 2.5, lo[3] - 2.5, lo[4] - 2.5)
    grid_spots(R, R.y + s, hi[1] + 2.5, hi[2] + 2.5, hi[3] - 2.5, hi[4] - 2.5)
    R.objective_spot = { (lo[1] + lo[3]) / 2, R.y, (lo[2] + lo[4]) / 2 }
  end,
  late = function(R)
    local up = R.z.params and R.z.params.split or "+x"
    local along_x = (up == "+x" or up == "-x")
    local lo, hi = R.lo_rect, R.hi_rect
    local function gap_edge(rect)
      return function(a, b)
        local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
        if along_x then return math.abs(mx - R.cx) < 3 end
        return math.abs(mz - R.cz) < 3
      end
    end
    edges(R, chamfered(lo[1], lo[2], lo[3], lo[4], R.c), R.y, gap_edge(lo))
    edges(R, chamfered(hi[1], hi[2], hi[3], hi[4], R.c), R.y + R.step, gap_edge(hi))
  end,
  light_h = function(R) return R.step + 5 end,
}

RECIPES.hangar = {
  shell = function(R) slab_shell(R, 8) end,
  inner = function(R)
    local long_x = R.w >= R.d
    local hw, hd = (long_x and R.w * 0.6 or R.w * 0.5) / 2, (long_x and R.d * 0.5 or R.d * 0.6) / 2
    local x0, z0, x1, z1 = R.cx - hw, R.cz - hd, R.cx + hw, R.cz + hd
    local H = 7
    solid(R, "shed", x0, R.y, z0, x1, R.y + H, z1)
    box(R, "shed_air", x0 + 0.75, R.y, z0 + 0.75, x1 - 0.75, R.y + H - 1, z1 - 0.75, { air = true })
    if long_x then
      box(R, "shed_end_w", x0 - 0.25, R.y, z0 + 0.75, x0 + 0.75, R.y + H - 1, z1 - 0.75, { air = true })
      box(R, "shed_end_e", x1 - 0.75, R.y, z0 + 0.75, x1 + 0.25, R.y + H - 1, z1 - 0.75, { air = true })
    else
      box(R, "shed_end_s", x0 + 0.75, R.y, z0 - 0.25, x1 - 0.75, R.y + H - 1, z0 + 0.75, { air = true })
      box(R, "shed_end_n", x0 + 0.75, R.y, z1 - 0.75, x1 - 0.75, R.y + H - 1, z1 + 0.25, { air = true })
    end
    -- crates inside the shed
    solid(R, "crate1", R.cx - 3, R.y, R.cz - 1, R.cx - 1.5, R.y + 1.5, R.cz + 0.5, { detail = true })
    solid(R, "crate2", R.cx + 1.5, R.y, R.cz - 0.5, R.cx + 3, R.y + 1.5, R.cz + 1, { detail = true })
    R.solids[#R.solids + 1] = { x0, z0, x1, z1, R.y, R.y + H }
    grid_spots(R, R.y, R.x0 + R.c + 2, R.z0 + R.c + 2, R.x1 - R.c - 2, R.z1 - R.c - 2)
    R.spots[#R.spots + 1] = { R.cx - (long_x and hw * 0.6 or 0), R.y, R.cz - (long_x and 0 or hd * 0.6) }
    R.spots[#R.spots + 1] = { R.cx + (long_x and hw * 0.6 or 0), R.y, R.cz + (long_x and 0 or hd * 0.6) }
    R.shed = { x0, z0, x1, z1, H }
  end,
  late = function(R) edges(R, R.outline, R.y) end,
  light_h = function() return 5.5 end,
}

-- Payload pieces.
RECIPES.road = {
  shell = function(R)
    local F = road_frame(R.z)
    local p = R.z.params or {}
    local H = R.h
    local ys, yn = R.y, R.y + F.rise
    local s1, s2 = F.ls, F.len - F.ln
    -- One air hull from s = sa to sb, its floor ya -> yb, across the full width.
    local function piece(part, sa, sb, ya, yb)
      local pts = {}
      for _, e in ipairs({ { sa, ya }, { sb, yb } }) do
        for _, c in ipairs({ F.c0, F.c1 }) do
          local x, z = F.xz(e[1], c)
          pts[#pts + 1] = { x, e[2], z }
          pts[#pts + 1] = { x, e[2] + H, z }
        end
      end
      return add_brush(R, hull("layout", R.name, part, pts, { air = true, textures = R.textures }))
    end
    local b1 = piece("south", 0, s1, ys, ys)
    local b2 = piece("grade", s1, s2, ys, yn)
    local b3 = piece("north", s2, F.len, yn, yn)
    if not p.tunnel then I.sky_up({ b1, b2, b3 }) R.sky = true end
    R.room = { kind = "room", name = R.name, min = vec(R.x0, math.min(ys, yn), R.z0), max = vec(R.x1, math.max(ys, yn) + H, R.z1), floor_y = ys, brushes = { b1, b2, b3 } }
    R.F, R.ys, R.yn, R.s1, R.s2 = F, ys, yn, s1, s2
  end,
  inner = function(R)
    -- Barriers along the graded stretch (cover), alternating sides, the cart lane kept clear.
    local F = R.F
    local lane = 3.2
    local n = math.floor((R.s2 - R.s1 - 2) / 6)
    local function y_at(s) return R.ys + (R.yn - R.ys) * clamp((s - R.s1) / (R.s2 - R.s1), 0, 1) end
    for i = 0, n - 1 do
      local s = R.s1 + 2 + i * 6
      local left = (i % 2 == 0)
      local c0 = left and (F.c0 + 0.3) or (F.c1 - 0.9)
      if (F.wid / 2 - lane) >= 0.9 then
        local pts = {}
        for _, e in ipairs({ { s, y_at(s) }, { s + 3, y_at(s + 3) } }) do
          for _, c in ipairs({ c0, c0 + 0.6 }) do
            local x, z = F.xz(e[1], c)
            pts[#pts + 1] = { x, e[2] - 0.3, z }
            pts[#pts + 1] = { x, e[2] + 1.0, z }
          end
        end
        add_brush(R, hull("layout", R.name, "barrier" .. i, pts, { texture = R.trim.texture }))
      end
    end
    local x0, z0, x1, z1 = F.rect(2, R.s1 - 1, F.c0 + 1.5, F.c1 - 1.5)
    grid_spots(R, R.ys, x0, z0, x1, z1, 3)
  end,
  light_h = function(R) return R.h - 1 end,
}

RECIPES.yard = {
  shell = function(R)
    local rec = basic_shell(R, "flat", {})
    I.sky_up(rec.brushes)
    R.sky = true
  end,
  inner = function(R)
    -- Cover off the cart lane: low walls and crate stacks on a jittered lattice; a raised dais at a side.
    cover_blocks(R, "cover", R.y, R.x0 + 4, R.z0 + 4, R.x1 - 4, R.z1 - 4, 6, 1.4, true)
    local sides = quiet_sides(R)
    for _, side in ipairs(sides) do
      local x0, z0, x1, z1 = nil, nil, nil, nil
      if side == "+x" then x0, x1, z0, z1 = R.x1 - 3.5, R.x1, R.cz - 6, R.cz + 6
      elseif side == "-x" then x0, x1, z0, z1 = R.x0, R.x0 + 3.5, R.cz - 6, R.cz + 6
      else break end
      if clear(R, x0, z0, x1, z1, R.y, R.y + 1) then
        note_solid(R, x0, z0, x1, z1, R.y, R.y + 1)
        add_rec(R, kit.floor_section{ name = R.name .. "_dais", room = R.room, min = { x0, R.y, z0 }, max = { x1, R.y, z1 }, rise = 1, textures = R.textures })
        break
      end
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5)
    R.objective_spot = { R.cx, R.y, R.cz }
  end,
  light_h = function() return 9 end,
}

-- A zone's local frame for a heading: local +z is the way the cart travels (entering on local -z), local x across.
-- hw / hd are the local half width and depth; w(lx, lz) -> world x, z; rect -> a world rect; dir -> a world side.
local function zone_frame(R, heading)
  local F = { heading = heading }
  local cx, cz = R.cx, R.cz
  if heading == "+x" or heading == "-x" then F.hw, F.hd = R.d / 2, R.w / 2 else F.hw, F.hd = R.w / 2, R.d / 2 end
  function F.w(lx, lz)
    if heading == "+z" then return cx + lx, cz + lz
    elseif heading == "-z" then return cx - lx, cz - lz
    elseif heading == "+x" then return cx + lz, cz - lx end
    return cx - lz, cz + lx
  end
  function F.l(x, z)
    local dx, dz = x - cx, z - cz
    if heading == "+z" then return dx, dz
    elseif heading == "-z" then return -dx, -dz
    elseif heading == "+x" then return -dz, dx end
    return dz, -dx
  end
  function F.rect(lx0, lz0, lx1, lz1)
    local ax, az = F.w(lx0, lz0)
    local bx, bz = F.w(lx1, lz1)
    return math.min(ax, bx), math.min(az, bz), math.max(ax, bx), math.max(az, bz)
  end
  function F.dir(d)
    local v = DIRV[d]
    local x, z = F.w(v[1], v[2])
    x, z = x - cx, z - cz
    if math.abs(x) > math.abs(z) then return x > 0 and "+x" or "-x" end
    return z > 0 and "+z" or "-z"
  end
  return F
end

RECIPES.finale = {
  shell = function(R)
    local rec = basic_shell(R, "flat", {})
    I.sky_up(rec.brushes)
    R.sky = true
  end,
  inner = function(R)
    local s = R.step
    local bw = 6
    local F = zone_frame(R, heading_of(R.z))
    local hw, hd = F.hw, F.hd
    local outline = R.room.outline
    local inner = I.inset_convex(outline, bw)
    -- The terrace on every edge but the entry side.
    if inner and #inner == #outline then
      for i = 1, #outline do
        local a, b = outline[i], outline[i % #outline + 1]
        local _, mz = F.l((a[1] + b[1]) / 2, (a[2] + b[2]) / 2)
        if not (mz < -hd + 0.5) then
          local q = { a, b, inner[i % #outline + 1], inner[i] }
          add_brush(R, I.prism("layout", R.name, "terrace" .. i, q, R.y, R.y + s, { textures = R.textures }))
        end
      end
    end
    local function lsolid(lx0, lz0, lx1, lz1) local x0, z0, x1, z1 = F.rect(lx0, lz0, lx1, lz1) note_solid(R, x0, z0, x1, z1, R.y, R.y + s) end
    lsolid(-hw, -hd + bw, -hw + bw, hd)
    lsolid(hw - bw, -hd + bw, hw, hd)
    lsolid(-hw, hd - bw, hw, hd)
    -- The final pit with the cart ramp down from the entry side.
    local pd = 2.5
    local ph = 7
    local pz0 = -ph + 2
    local px0, pz0w, px1, pz1w = F.rect(-ph, -ph + 2, ph, ph + 2)
    add_rec(R, kit.floor_section{ name = R.name .. "_pit", min = { px0, R.y, pz0w }, max = { px1, R.y, pz1w }, y = R.y, rise = -pd, textures = R.textures })
    local cart_c = R.cart_c or 0
    local rx, rz = F.w(clamp(cart_c, -ph + 4.5, ph - 4.5), pz0 + 8)
    ramp(R, "cart_ramp", rx, R.y - pd, rz, F.dir("-z"), pd, 8, 8)
    -- Stairs up to the terrace on the two side bands (attackers' way up).
    local run = stair_run(s)
    local sx, sz = F.w(-hw + bw + run, -2)
    stairs(R, "stairs_w", sx, R.y, sz, F.dir("-x"), s, 3)
    sx, sz = F.w(hw - bw - run, 2)
    stairs(R, "stairs_e", sx, R.y, sz, F.dir("+x"), s, 3)
    -- Parapets along the terrace's inner edges (detail), open where the stairs arrive.
    if inner and #inner == #outline then
      for i = 1, #outline do
        local a, b = inner[i], inner[i % #outline + 1]
        local mx, mz = F.l((a[1] + b[1]) / 2, (a[2] + b[2]) / 2)
        if not (mz < -hd + bw + 0.5) and math.abs(mz) > 5.5 or (math.abs(mx) < hw - bw - 0.5 and mz > 0) then
          if mz > -hd + bw + 0.5 then
            add_brush(R, member("layout", R.name, "parapet" .. i, { a[1], R.y + s + 0.55, a[2] }, { b[1], R.y + s + 0.55, b[2] }, 0.4, 1.1, R.trim))
          end
        end
      end
    end
    local gx0, gz0, gx1, gz1 = F.rect(-ph - 4, -hd + 2.5, ph + 4, pz0 - 2)
    grid_spots(R, R.y, gx0, gz0, gx1, gz1)
    gx0, gz0, gx1, gz1 = F.rect(-hw + 2, hd - bw + 1, hw - 2, hd - 1.5)
    grid_spots(R, R.y + s, gx0, gz0, gx1, gz1)
    local ox, oz = F.w(0, 2)
    R.objective_spot = { ox, R.y - pd, oz }
  end,
  light_h = function(R) return R.step + 6 end,
}

RECIPES.start = {
  shell = function(R) basic_shell(R, "stepped", { rise = 2, inset = 3 }) end,
  inner = function(R)
    solid(R, "crate1", R.cx - 1, R.y, R.cz - 0.75, R.cx + 1, R.y + 1.3, R.cz + 0.75, { detail = true })
    grid_spots(R, R.y, R.x0 + 2, R.z0 + 2, R.x1 - 2, R.z1 - 2, 3)
  end,
  light_h = function(R) return R.h - 1 end,
}

RECIPES.overlook = {
  shell = function(R)
    local rec = basic_shell(R, "flat", {})
    I.sky_up(rec.brushes)
    R.sky = true
  end,
  inner = function(R)
    cover_blocks(R, "cover", R.y, R.x0 + 3.5, R.z0 + 3.5, R.x1 - 3.5, R.z1 - 3.5, 6.5, 1.3, false)
    local long_x = R.w >= R.d
    local cx, cz = R.cx, R.cz
    if clear(R, cx - 3, cz - 3, cx + 3, cz + 3, R.y, R.y + 3.5) then
      note_solid(R, cx - 3, cz - 3, cx + 3, cz + 3, R.y, R.y + 3.5)
      add_rec(R, kit.platform{ name = R.name .. "_canopy", pos = { cx, R.y, cz }, size = long_x and { 6, 4 } or { 4, 6 }, height = 3.5, thickness = 0.3, supports = true, detail = true, group = false, texture = R.trim.texture })
    end
    grid_spots(R, R.y, R.x0 + 2.5, R.z0 + 2.5, R.x1 - 2.5, R.z1 - 2.5)
  end,
  light_h = function() return 6 end,
}

-- A traced space: the room (or yard) is its outline; a raised ceiling bay over the biggest convex piece indoors;
-- low cover blocks on a lattice of its clear floor, kept 2 m in from the walls and off the port approaches and the
-- cart lane; floor spots on a 4 m grid.
RECIPES.poly = {
  shell = function(R)
    local foot = {}
    for i, q in ipairs(R.poly) do foot[i] = { q[1], q[2] } end
    local rec = shaped(R, { pos = { 0, R.y, 0 }, footprint = foot, height = R.h })
    if R.z.sky then
      I.sky_up(rec.brushes)
      R.sky = true
    elseif R.area >= 120 then
      local biggest, best = nil, -1
      for _, piece in ipairs(rec.pieces or {}) do local a = math.abs(area2(piece)) if a > best then biggest, best = piece, a end end
      local raised = biggest and I.inset_convex(biggest, math.max(2.5, math.sqrt(best) * 0.22)) or nil
      if raised and math.abs(area2(raised)) >= 30 then
        local rise = math.max(2, q2(R.h * 0.4))
        add_brush(R, I.prism("layout", R.name, "raised", SP.snap16(raised), R.y, R.y + R.h + rise, { air = true, textures = R.textures }))
      end
    end
  end,
  inner = function(R)
    local p = R.poly
    local function clear_floor(x, z, m) return inside(p, x, z) and edge_dist(p, x, z) >= m end
    -- the plan's own frame: local x along its longest wall (the first of walls within 5 cm of it), so a traced zone
    -- is dressed alike however its outline is turned
    local q = ccw(p)
    local n, longest = #q, 0
    for i = 1, n do local a, b = q[i], q[i % n + 1] longest = math.max(longest, math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)) end
    local ux, uz = 1, 0
    for i = 1, n do
      local a, b = q[i], q[i % n + 1]
      local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
      if L >= longest - 0.05 then ux, uz = (b[1] - a[1]) / L, (b[2] - a[2]) / L break end
    end
    local function W(lx, lz) return lx * ux - lz * uz, lx * uz + lz * ux end
    local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
    for _, v in ipairs(q) do
      local lx, lz = v[1] * ux + v[2] * uz, -v[1] * uz + v[2] * ux
      x0, z0, x1, z1 = math.min(x0, lx), math.min(z0, lz), math.max(x1, lx), math.max(z1, lz)
    end
    -- a rectangle of the frame (centre lx, lz, half sizes) as a world quad
    local function quad(lx, lz, hw, hd)
      local out = {}
      for i, c in ipairs({ { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } }) do local x, z = W(lx + c[1] * hw, lz + c[2] * hd) out[i] = { x, z } end
      return out
    end
    -- clear of every port approach (a quad's test of clear())
    local function clear_quad(Q, y0, y1)
      local bx0, bz0, bx1, bz1 = bbox(Q)
      for _, port in ipairs(R.ports) do
        if port.seg then
          local py0 = port.drop_to or port.y0
          if y0 < port.y1 and y1 > py0 - 0.3 and poly_dist(keepout_poly(port, DATA.keep_depth), Q) < 1e-6 then return false end
        elseif not clear(R, bx0, bz0, bx1, bz1, y0, y1) then return false end
      end
      return lane_clear(R, bx0, bz0, bx1, bz1)
    end
    -- cover: blocks on a 7 m lattice (alternating along and across the frame), at most one per 60 m2
    local pitch = 7
    local nx, nz = math.max(0, math.floor((x1 - x0 - 4) / pitch)), math.max(0, math.floor((z1 - z0 - 4) / pitch))
    local ox, oz = (x0 + x1) / 2 - nx * pitch / 2, (z0 + z1) / 2 - nz * pitch / 2
    local cap, k = math.floor(R.area / 60), 0
    for i = 0, nx do
      for j = 0, nz do
        local x, z = ox + i * pitch, oz + j * pitch
        local along_x = ((i + j) % 2 == 0)
        local hw, hd = along_x and 1.25 or 0.625, along_x and 0.625 or 1.25
        local ok = k < cap
        local block = quad(x, z, hw, hd)
        for _, c in ipairs(block) do
          if ok and not clear_floor(c[1], c[2], 2) then ok = false end
        end
        if ok and clear_quad(quad(x, z, hw + 1.2, hd + 1.2), R.y, R.y + 1.4) then
          local bx0, bz0, bx1, bz1 = bbox(block)
          if free_of_solids(R, bx0, bz0, bx1, bz1, R.y, R.y + 1.4, 1.5) then
            k = k + 1
            note_solid(R, bx0, bz0, bx1, bz1, R.y, R.y + 1.4)
            add_brush(R, I.prism("layout", R.name, "cover" .. k, ccw(SP.snap16(block)), R.y, R.y + 1.4, { detail = true, textures = R.textures }))
          end
        end
      end
    end
    -- spots on a 4 m grid of the frame inside the outline, 2 m from its walls
    local gx = x0 + ((x1 - x0) % 4) / 2
    while gx <= x1 + EPS do
      local gz = z0 + ((z1 - z0) % 4) / 2
      while gz <= z1 + EPS do
        local wx, wz = W(gx, gz)
        if clear_floor(wx, wz, 2) then R.spots[#R.spots + 1] = { wx, R.y, wz } end
        gz = gz + 4
      end
      gx = gx + 4
    end
    -- the objective spot: the middle of the biggest convex piece
    local biggest, best = nil, -1
    for _, piece in ipairs((R.room and R.room.pieces) or {}) do local a = math.abs(area2(piece)) if a > best then biggest, best = piece, a end end
    if biggest then
      local cx, cz = 0, 0
      for _, q in ipairs(biggest) do cx, cz = cx + q[1], cz + q[2] end
      R.objective_spot = { cx / #biggest, R.y, cz / #biggest }
      if #R.spots == 0 then R.spots[1] = { R.objective_spot[1], R.y, R.objective_spot[3] } end
    end
  end,
  -- Wall articulation indoors: pilasters about every 4.5 m along the traced walls at least 9 m long, 1.5 m clear of
  -- corners and 2 m clear of every port, each buried a quarter metre in its wall (no face on the wall's oblique plane).
  late = function(R)
    if R.sky or (R.detail and R.detail.walls) then return end -- (the room detail pass lines the walls instead)
    local p = ccw(R.poly)
    local n = #p
    for i = 1, n do
      local a, b = p[i], p[i % n + 1]
      local ex, ez = b[1] - a[1], b[2] - a[2]
      local L = math.sqrt(ex * ex + ez * ez)
      if L >= 9 then
        local tx, tz = ex / L, ez / L
        local nx, nz = -tz, tx -- inward for a CCW plan
        local count = math.max(1, math.floor((L - 3) / 4.5 + 0.5))
        for k = 1, count do
          local s = 1.5 + (L - 3) * (k - 0.5) / count
          local cx, cz = a[1] + tx * s, a[2] + tz * s
          local ok = true
          for _, port in ipairs(R.ports) do
            local q = port.world or (port.seg and { (port.seg[1][1] + port.seg[2][1]) / 2, (port.seg[1][2] + port.seg[2][2]) / 2 })
            local half = port.seg and math.sqrt((port.seg[2][1] - port.seg[1][1]) ^ 2 + (port.seg[2][2] - port.seg[1][2]) ^ 2) / 2 or 2
            if q and math.sqrt((q[1] - cx) ^ 2 + (q[2] - cz) ^ 2) < half + 2.4 then ok = false end
          end
          -- (no pilaster where the wall is open: a join to a neighbour)
          if ok and R.in_other and R.in_other(cx - nx * 0.75, cz - nz * 0.75) then ok = false end
          if ok then
            local quad = {}
            for j, uv in ipairs({ { -0.4, -0.25 }, { 0.4, -0.25 }, { 0.4, 0.4 }, { -0.4, 0.4 } }) do
              quad[j] = { cx + tx * uv[1] + nx * uv[2], cz + tz * uv[1] + nz * uv[2] }
            end
            add_brush(R, I.prism("layout", R.name, "pilaster" .. i .. "_" .. k, ccw(quad), R.y, R.y + R.h, { detail = true, texture = R.trim.texture }))
          end
        end
      end
    end
  end,
  light_h = function(R) return R.h - 1 end,
}

-- Link builders -------------------------------------------------------------------------------

local function link_textures(P)
  local pal = palette_of(P.za)
  return pal.textures, pal.trims.post
end

-- An axis box between the walls: s0..s1 along the link's axis, c +/- W/2 across.
local function axis_box(P, s0, s1, y0, y1, half)
  local a, b = math.min(s0, s1), math.max(s0, s1)
  if P.f.axis == "x" then return a, y0, P.c - half, b, y1, P.c + half end
  return P.c - half, y0, a, P.c + half, y1, b
end

local function link_air(P, gname, part, s0, s1, y0, y1, half, textures)
  local x0, yy0, z0, x1, yy1, z1 = axis_box(P, s0, s1, y0, y1, half)
  x0, yy0, z0, x1, yy1, z1 = g16(x0), g16(yy0), g16(z0), g16(x1), g16(yy1), g16(z1)
  return hull("layout", gname .. "_" .. P.link.id, part, box_points(x0, yy0, z0, x1, yy1, z1), { air = true, textures = textures })
end

local function link_solid(P, gname, part, s0, s1, y0, y1, half, textures)
  local x0, yy0, z0, x1, yy1, z1 = axis_box(P, s0, s1, y0, y1, half)
  x0, yy0, z0, x1, yy1, z1 = g16(x0), g16(yy0), g16(z0), g16(x1), g16(yy1), g16(z1)
  return hull("layout", gname .. "_" .. P.link.id, part, box_points(x0, yy0, z0, x1, yy1, z1), { textures = textures })
end

-- Phase 1 (air): every link type's air volume.
local function build_link_air(P, gname, out, open_air)
  local l, f = P.link, P.f
  local tex = link_textures(P)
  local W = P.W
  local H = link_height(l, { class = open_air and "open_air" or "standard" })
  local ya, yb = link_ys(l, P.za, P.zb)
  local wa, wb = f.wa, f.wb
  local dir = (wb > wa) and 1 or -1
  P.dir = dir
  local brushes = {}
  local function add(b) brushes[#brushes + 1] = b end
  if open_air then
    out[#out + 1] = brushes
    return brushes
  end
  if l.type == "corridor" or l.type == "doorway" or l.type == "road" or l.type == "drop" then
    -- a corridor between floors up to a step apart runs at the lower floor; the higher room's floor is the step
    local y0 = (l.type == "drop") and ya or math.min(ya, yb)
    local y1 = (l.type == "drop") and (ya + H) or (math.max(ya, yb) + H)
    local b = link_air(P, gname, "air", wa, wb, y0, y1, W / 2, tex)
    add(b)
    if l.type == "road" and not l.tunnel then I.sky_up({ b }) end
  elseif l.type == "bridge" then
    -- A chasm under the deck, the link's width, depth metres below the floors and open to the sky (unless tunnel).
    local depth = l.depth or DATA.chasm_depth
    local b = link_air(P, gname, "chasm", wa, wb, math.min(ya, yb) - depth, math.max(ya, yb) + H, W / 2, tex)
    add(b)
    if not l.tunnel then I.sky_up({ b }) end
  elseif l.type == "window" then
    add(link_air(P, gname, "slot", wa, wb, P.ba[1], P.ba[2], W / 2, tex))
  elseif l.type == "stair" or l.type == "ramp" then
    local low_is_a = ya < yb
    local wl, wh = low_is_a and wa or wb, low_is_a and wb or wa
    local yl, yh = math.min(ya, yb), math.max(ya, yb)
    local rise = yh - yl
    local run = (l.type == "stair") and stair_run(rise) or ramp_run(rise)
    local G = math.abs(wh - wl)
    local d = (wh > wl) and 1 or -1
    local flat = math.max(0.5, q4((G - run) / 2))
    if flat + run > G then flat = math.max(0, G - run) end
    P.stair = { wl = wl, wh = wh, yl = yl, yh = yh, rise = rise, run = run, flat = flat, d = d }
    if flat > 0.01 then add(link_air(P, gname, "low", wl, wl + d * flat, yl, yl + H, W / 2, tex)) end
    add(link_air(P, gname, "climb", wl + d * flat, wh, yl, yh + H, W / 2, tex))
  elseif l.type == "lift" then
    local low_is_a = ya < yb
    local wl, wh = low_is_a and wa or wb, low_is_a and wb or wa
    local yl, yh = math.min(ya, yb), math.max(ya, yb)
    local d = (wh > wl) and 1 or -1
    local G = math.abs(wh - wl)
    local s0 = wl + d * q4((G - W) / 2)
    local s1 = s0 + d * W
    P.lift = { wl = wl, wh = wh, yl = yl, yh = yh, s0 = s0, s1 = s1, d = d }
    add(link_air(P, gname, "low", wl, s0, yl, yl + H, W / 2 - 0.25, tex))
    add(link_air(P, gname, "shaft", s0, s1, yl, yh + H, W / 2, tex))
    add(link_air(P, gname, "high", s1, wh, yh, yh + H, W / 2 - 0.25, tex))
  end
  out[#out + 1] = brushes
  return brushes
end

-- Phase 2 (solids and structure): steps, ramps, landings, lifts, bridges.
local function build_link_solid(P, gname, open_air)
  local l, f = P.link, P.f
  local tex, trim = link_textures(P)
  local W = P.W
  local ya, yb = link_ys(l, P.za, P.zb)
  local name = gname .. "_" .. l.id
  local made = {}
  local dname = { x = { [1] = "+x", [-1] = "-x" }, z = { [1] = "+z", [-1] = "-z" } }
  local function at(s, y)
    if f.axis == "x" then return { s, y, P.c } end
    return { P.c, y, s }
  end
  if open_air then
    local wa, wb = f.wa, f.wb
    local d = (wb > wa) and 1 or -1
    if l.type == "bridge" or l.type == "corridor" or l.type == "doorway" or l.type == "ramp" then
      local from, to = at(wa - d * 1, ya + 0.125), at(wb + d * 1, yb + 0.125)
      local span = math.abs(wb - wa)
      made[#made + 1] = kit.bridge{ purpose = "quiet", name = name, from = from, to = to, width = W - 1, style = l.style or "truss", group = false,
        landmark = span >= 12 and math.abs(ya - yb) < 0.1, textures = tex, frame_texture = trim }
    elseif l.type == "stair" then
      local low_is_a = ya < yb
      local wl, wh = low_is_a and wa or wb, low_is_a and wb or wa
      local yl, yh = math.min(ya, yb), math.max(ya, yb)
      local dd = (wh > wl) and 1 or -1
      local rise = yh - yl
      local run = stair_run(rise)
      local G = math.abs(wh - wl)
      -- The flight starts on the low deck when the gap is shorter than the run; a landing slab spans the rest.
      local start = wl - dd * math.max(0, run - G + 1)
      made[#made + 1] = flight{ name = name .. "_flight", pos = at(start, yl), dir = dname[f.axis][dd], rise = rise, width = W - 1, tread = DATA.tread, group = false, textures = tex }
      local top = start + dd * run
      if math.abs(wh - top) > 0.1 and (wh - top) * dd > 0 then
        made[#made + 1] = { brush = link_solid(P, gname, "landing", top, wh + dd * 0.5, yh - 0.5, yh, (W - 1) / 2, tex) }
      end
      -- rails on both sides of the flight
      for _, side in ipairs({ -1, 1 }) do
        local o = side * ((W - 1) / 2 + 0.05)
        local p0 = f.axis == "x" and { start, yl + 1.05, P.c + o } or { P.c + o, yl + 1.05, start }
        local p1 = f.axis == "x" and { top, yh + 1.05, P.c + o } or { P.c + o, yh + 1.05, top }
        made[#made + 1] = { brush = member("layout", name, "rail" .. side, p0, p1, 0.1, 0.1, { texture = trim }) }
      end
    elseif l.type == "lift" then
      -- (open-air lifts are not generated)
    end
    return made
  end
  if l.type == "bridge" then
    -- The deck across the chasm, 1 m into each zone (a cart bridge: give the link the road's width).
    local wa, wb = f.wa, f.wb
    local d = (wb > wa) and 1 or -1
    made[#made + 1] = kit.bridge{ purpose = "quiet", name = name, from = at(wa - d * 1, ya + 0.125), to = at(wb + d * 1, yb + 0.125), width = W - 1, style = l.style or "truss",
      group = false, landmark = false, textures = tex, frame_texture = trim }
    return made
  end
  -- Door jambs where a link meets a zone: a 3 m doorway into each room (exemplar doorways 2.5-3.7 m), so the
  -- rooms read (and measure) as separate spaces joined by doors, not one floor. Like every solid in a passage they
  -- reach a quarter metre into its side walls (no face of theirs on the passage's own side planes, which a turned
  -- frame cannot hold exactly).
  if (l.type == "corridor" or l.type == "stair" or l.type == "ramp" or l.type == "drop") and W >= DATA.door_width + 1 then
    local dw = DATA.door_width
    local y0, y1 = math.min(ya, yb), math.max(ya, yb) + link_height(l)
    if l.type == "drop" then y0, y1 = ya, ya + link_height(l) end
    local d = (f.wb > f.wa) and 1 or -1
    for i, e in ipairs({ { f.wa, d }, { f.wb, -d } }) do
      -- (set back 1/16 m from the wall: the jamb never lies on the wall plane, which a turned frame cannot hold exactly)
      local s0, s1 = e[1] + e[2] / 16, e[1] + e[2] * 0.5
      for j, side in ipairs({ -1, 1 }) do
        local inner = side * dw / 2
        local outer = side * (W / 2 + BURY)
        local a0, a1 = math.min(inner, outer), math.max(inner, outer)
        local x0, z0, x1, z1
        if f.axis == "x" then x0, x1, z0, z1 = math.min(s0, s1), math.max(s0, s1), P.c + a0, P.c + a1
        else x0, x1, z0, z1 = P.c + a0, P.c + a1, math.min(s0, s1), math.max(s0, s1) end
        made[#made + 1] = { brush = hull("layout", name, "jamb" .. i .. j, box_points(g16(x0), g16(y0), g16(z0), g16(x1), g16(y1), g16(z1)), { textures = tex }) }
      end
    end
  end
  if (l.type == "stair" or l.type == "ramp") and P.stair then
    local S = P.stair
    local foot = S.wl + S.d * S.flat
    if l.type == "stair" then
      made[#made + 1] = flight{ name = name .. "_stairs", pos = at(foot, S.yl), dir = dname[f.axis][S.d], rise = S.rise, width = W + 2 * BURY, tread = DATA.tread, group = false, textures = tex }
    else
      made[#made + 1] = kit.ramp{ name = name .. "_ramp", pos = at(foot, S.yl), dir = dname[f.axis][S.d], rise = S.rise, run = S.run, width = W + 2 * BURY, group = false, textures = tex }
    end
    -- the flight and its landing are as wide as the passage plus a quarter metre in each side wall; the landing
    -- overlaps the flight's top and runs a quarter metre past the high zone's wall under its floor
    local top = foot + S.d * S.run
    if (S.wh - top) * S.d > 0.05 then
      made[#made + 1] = { brush = link_solid(P, gname, "landing", top - S.d * 0.125, S.wh + S.d * BURY, S.yl, S.yh, W / 2 + BURY, tex) }
    end
  elseif l.type == "lift" and P.lift then
    local L = P.lift
    local mid = (L.s0 + L.s1) / 2
    local half = W / 2 - 0.4
    local c = at(mid, L.yl)
    local plat = level.brush{ shape = "box", pos = { c[1], L.yl + 0.125, c[3] }, size = { 2 * half, 0.25, 2 * half }, textures = tex,
      tags = { kit = "layout", ["kit.name"] = name, ["kit.part"] = "lift" } }
    -- the platform turns with the zones' frame before it becomes a mover; the triggers sit at turned points
    local rot = zone_rot(P.za)
    turn_list({ plat }, 0, rot)
    c[1], c[3] = to_world(rot, c[1], c[3])
    local mover = level.create_mover{ name = name .. "_lift", brushes = { plat }, props = { movement_type = "Ping Pong Once" },
      tags = { kit = "layout", ["kit.name"] = name } }
    local travel = math.max(2, (L.yh - L.yl) / 2.5)
    mover.start_keyframe:set_many{ name = name .. "_lift_bottom", travel_time_to_next = travel, return_travel = travel }
    mover:add_keyframe{ offset = { 0, L.yh - L.yl, 0 }, props = { name = name .. "_lift_top", pause_time = 2, travel_time_to_next = travel, return_travel = travel } }
    local resets = 2 * travel + 2
    level.place{ kind = "trigger", name = name .. "_lift_call_bottom", pos = { c[1], L.yl + 1.2, c[3] },
      props = { shape = "Sphere", sphere_radius = half, resets_after = resets, activated_by = "Players" }, links = { mover }, tags = { kit = "layout", ["kit.name"] = name } }
    local top_at = at(L.s1 + L.d * 1.5, L.yh)
    top_at[1], top_at[3] = to_world(rot, top_at[1], top_at[3])
    level.place{ kind = "trigger", name = name .. "_lift_call_top", pos = { top_at[1], L.yh + 1.2, top_at[3] },
      props = { shape = "Sphere", sphere_radius = 1.5, resets_after = resets, activated_by = "Players", use_key_required = true }, links = { mover }, tags = { kit = "layout", ["kit.name"] = name } }
    made[#made + 1] = { brush = plat, mover = mover, turned = true }
  end
  return made
end

-- Lights along a link (corridors one every ~5 m).
local function link_lights(P, gname, color)
  local l, f = P.link, P.f
  if l.type == "window" or l.type == "bridge" then return {} end
  local lights = {}
  local H = link_height(l)
  local wa, wb = f.wa, f.wb
  local len = math.abs(wb - wa)
  if len < 2 then return {} end
  local n = math.max(1, math.floor(len / 5 + 0.5))
  local ya, yb = link_ys(l, P.za, P.zb)
  for i = 1, n do
    local s = wa + (wb - wa) * (i - 0.5) / n
    local y = math.max(ya, yb) + H - 0.8
    if l.type == "stair" or l.type == "ramp" or l.type == "lift" then
      local S = P.stair or P.lift
      local frac = (s - wa) / (wb - wa)
      local lowpart = S and S.flat and ((S.wl < S.wh) and (s < S.wl + S.flat) or (S.wl > S.wh) and (s > S.wl - S.flat))
      if lowpart then y = math.min(ya, yb) + H - 0.8 end
      local _ = frac
    end
    if l.type == "drop" then y = ya + H - 0.8 end
    if l.type == "road" then y = ya + (l.tunnel and 5.5 or 6) end
    local pos = f.axis == "x" and { s, y, P.c } or { P.c, y, s }
    pos[1], pos[3] = to_world(zone_rot(P.za), pos[1], pos[3])
    lights[#lights + 1] = level.place{ kind = "light", name = gname .. "_" .. l.id .. "_light" .. i, pos = pos,
      props = { color = color or kit.defaults.light_color, size_range = 9 }, tags = { kit = "layout", ["kit.name"] = gname .. "_" .. l.id } }
  end
  return lights
end

-- Free links ------------------------------------------------------------------------------------

-- A point on the line through A and B at s metres from A, measured along direction d (A and B on one side line).
local function along_line(A, B, d, s)
  local L = (B[1] - A[1]) * d[1] + (B[2] - A[2]) * d[2]
  local f = s / L
  return { A[1] + (B[1] - A[1]) * f, A[2] + (B[2] - A[2]) * f }
end

-- The floors of a free link's pieces: the climb (stair, ramp) on its longest straight segment, low floor before it
-- and high floor after it; every other link at its lower floor (a step of up to 1 m at the higher zone's wall).
local function free_layout(P, H)
  if P.layout then return P.layout end
  local l, M = P.link, P.M
  local ya, yb = P.ya, P.yb
  local yl, yh = math.min(ya, yb), math.max(ya, yb)
  local Lo = { yl = yl, yh = yh, H = H, top = yh + H }
  if (l.type == "stair" or l.type == "ramp") and yh - yl > 0.01 then
    local rise = yh - yl
    local run = (l.type == "stair") and stair_run(rise) or ramp_run(rise)
    local k, best = 1, -1
    for i, st in ipairs(M.straight) do if st[2] - st[1] > best + EPS then k, best = i, st[2] - st[1] end end
    local st = M.straight[k]
    local fwd = ya < yb -- climbing along the path's order (a -> b)
    local foot, top
    if fwd then foot = math.max(st[1], q4(st[1] + (st[2] - st[1] - run) / 2)) top = foot + run
    else foot = math.min(st[2], q4(st[2] - (st[2] - st[1] - run) / 2)) top = foot - run end
    Lo.k, Lo.fwd, Lo.foot, Lo.top_s, Lo.run, Lo.rise = k, fwd, foot, top, run, rise
  end
  P.layout = Lo
  return Lo
end

-- The floor and ceiling of segment k of a free link at s metres from its start.
local function free_span(P, Lo, k, s)
  if not Lo.k then return Lo.yl, Lo.top end
  local before = (k < Lo.k)
  if k == Lo.k then
    local low = Lo.fwd and (s < Lo.foot) or ((not Lo.fwd) and s > Lo.foot)
    if low then return Lo.yl, Lo.yl + Lo.H end
    return Lo.yl, Lo.yh + Lo.H
  end
  local low = (before and Lo.fwd) or ((not before) and not Lo.fwd)
  if low then return Lo.yl, Lo.yl + Lo.H end
  return Lo.yh, Lo.yh + Lo.H
end

-- Phase "link air" of a free link: one air prism per segment on the shared mitre points, a mouth half a metre into
-- each zone at that zone's port floor; a chasm bridge's deep cut starts where its full width has left the wall.
local function build_free_air(P, gname, open_air)
  local l, M = P.link, P.M
  local out = {}
  if open_air then return out end
  local tex = link_textures(P)
  local name = gname .. "_" .. l.id
  local H = link_height(l, { class = "standard" })
  local function prism(part, poly, y0, y1)
    out[#out + 1] = I.prism("layout", name, part, ccw(poly), g16(y0), g16(y1), { air = true, textures = tex })
    return out[#out]
  end
  local n = #M.quads
  local d1, dn = M.dirs[1], M.dirs[n]
  if l.type == "bridge" then
    local depth = l.depth or DATA.chasm_depth
    local ya, yb = P.ya, P.yb
    local top = math.max(ya, yb) + H
    local len = M.lens[1]
    local ta, tb = angle_of(P.pa.wnrm, d1), angle_of(P.pb.wnrm, { -d1[1], -d1[2] })
    local ea, eb = 0.5 + P.hw * math.tan(math.rad(ta)), 0.5 + P.hw * math.tan(math.rad(tb))
    local LA0, RA0 = M.L[1], M.R[1]
    local LB0, RB0 = M.L[2], M.R[2]
    local function at(s) return along_line(LA0, LB0, d1, s), along_line(RA0, RB0, d1, s) end
    local la, ra = at(-0.5)
    local lb, rb = at(ea)
    local lc, rc = at(len - eb)
    local ld, rd = at(len + 0.5)
    prism("bank_a", { la, ra, rb, lb }, ya, top)
    prism("chasm", { lb, rb, rc, lc }, math.min(ya, yb) - depth, top)
    prism("bank_b", { lc, rc, rd, ld }, yb, top)
    if not l.tunnel then I.sky_up(out) end
    return out
  end
  local Lo = free_layout(P, H)
  -- No air face may sit on a zone's wall line: the mouths reach from half a metre inside each zone to a quarter
  -- metre outside it, and the passage starts and ends there.
  local function ends_of(k)
    local a0, a1, b0, b1
    if k == 1 then a0, a1 = side_points(M, 1, 0.25) else a0, a1 = M.L[k], M.R[k] end
    if k == n then b0, b1 = side_points(M, n, M.lens[n] - 0.25) else b0, b1 = M.L[k + 1], M.R[k + 1] end
    return a0, a1, b0, b1
  end
  for k = 1, n do
    local La, Ra, Lb, Rb = ends_of(k)
    if Lo.k == k then
      local Ls, Rs = side_points(M, k, Lo.foot)
      local y0a, y1a = free_span(P, Lo, k, Lo.foot - (Lo.fwd and 0.1 or -0.1))
      local y0b, y1b = free_span(P, Lo, k, Lo.foot + (Lo.fwd and 0.1 or -0.1))
      prism("seg" .. k .. "a", { La, Ra, Rs, Ls }, Lo.fwd and y0a or y0b, Lo.fwd and y1a or y1b)
      prism("seg" .. k .. "b", { Ls, Rs, Rb, Lb }, Lo.fwd and y0b or y0a, Lo.fwd and y1b or y1a)
    else
      local y0, y1 = free_span(P, Lo, k, 0)
      prism("seg" .. k, { La, Ra, Rb, Lb }, y0, y1)
    end
  end
  local _, yt0 = free_span(P, Lo, 1, 0)
  local _, ytn = free_span(P, Lo, n, M.lens[n])
  local ma_l, ma_r = side_points(M, 1, -0.5)
  local mc_l, mc_r = side_points(M, 1, 0.25)
  local mb_l, mb_r = side_points(M, n, M.lens[n] + 0.5)
  local md_l, md_r = side_points(M, n, M.lens[n] - 0.25)
  prism("mouth_a", { ma_l, ma_r, mc_r, mc_l }, P.ya, math.max(yt0, P.ya + H))
  prism("mouth_b", { md_l, md_r, mb_r, mb_l }, P.yb, math.max(ytn, P.yb + H))
  if l.type == "road" and not l.tunnel then I.sky_up(out) end
  return out
end

-- Phase "link solids" of a free link: door jambs at both ends (in each zone's frame, just behind its wall), the
-- stair or ramp on its flight segment and the landing behind it, or a bridge deck.
local function build_free_solid(P, gname, open_air)
  local l, M = P.link, P.M
  local tex, trim = link_textures(P)
  local name = gname .. "_" .. l.id
  local W, hw = P.W, P.hw
  local made = {}
  local function add(rec) if rec then made[#made + 1] = rec end return rec end
  local ya, yb = P.ya, P.yb
  local yl, yh = math.min(ya, yb), math.max(ya, yb)
  local d1 = M.dirs[1]
  if l.type == "bridge" or (open_air and (l.type == "corridor" or l.type == "doorway" or l.type == "ramp")) then
    local a, b = M.pts[1], M.pts[#M.pts]
    add(kit.bridge{ name = name, from = { a[1] - d1[1], ya + 0.125, a[2] - d1[2] }, to = { b[1] + d1[1], yb + 0.125, b[2] + d1[2] }, width = W - 1,
      style = l.style or "truss", group = false, landmark = open_air and M.lens[1] >= 12 and math.abs(ya - yb) < 0.1, textures = tex, frame_texture = trim })
    return made
  end
  if open_air and l.type == "stair" then
    -- a flying stair from just inside the low platform's edge (further onto it when the gap is shorter than the run), built
    -- along +z at the low port and turned onto the link's heading
    local fwd = ya < yb
    local Pl = fwd and M.pts[1] or M.pts[#M.pts]
    local dc = fwd and d1 or { -d1[1], -d1[2] }
    local G = M.lens[1]
    local rise = yh - yl
    local run = stair_run(rise)
    -- (always at least a quarter metre onto the low platform: a first step whose front stood on the platform's
    -- turned edge would put its face on that edge's oblique plane)
    local start = -math.max(BURY, run - G + 1)
    local top = start + run
    local list = {}
    local st = flight{ name = name .. "_flight", pos = { Pl[1], yl, Pl[2] + start }, dir = "+z", rise = rise, width = W - 1, tread = DATA.tread, group = false, textures = tex }
    for _, b in ipairs(rec_brushes({ st })) do list[#list + 1] = b end
    if G - top > 0.1 then
      -- (1/16 m wider each side than the flight: the landing overlaps the top step, and once turned two overlapping
      -- side faces on one oblique plane would not coincide exactly)
      local lw = (W - 1) / 2 + 1 / 16
      list[#list + 1] = hull("layout", name, "landing", box_points(Pl[1] - lw, yh - 0.5, Pl[2] + top, Pl[1] + lw, yh, Pl[2] + G + 0.5), { textures = tex })
    end
    for _, side in ipairs({ -1, 1 }) do
      local o = side * ((W - 1) / 2 + 0.05)
      list[#list + 1] = member("layout", name, "rail" .. side, { Pl[1] + o, yl + 1.05, Pl[2] + start }, { Pl[1] + o, yh + 1.05, Pl[2] + top }, 0.1, 0.1, { texture = trim })
    end
    turn_list(list, 0, yaw_of(dc[1], dc[2]), Pl[1], yl, Pl[2])
    add({ brushes = list })
    return made
  end
  -- Door jambs: a 3 m doorway into each zone (as classic corridors have), just behind the zone's wall.
  if (l.type == "corridor" or l.type == "stair" or l.type == "ramp") and W >= DATA.door_width + 1 then
    local dw = DATA.door_width
    local y0, y1 = yl, yh + link_height(l)
    for i, e in ipairs({ { P.za, P.pa }, { P.zb, P.pb } }) do
      local z, p = e[1], e[2]
      local list = {}
      local c = (p.lo + p.hi) / 2
      for j, side in ipairs({ -1, 1 }) do
        local inner, outer = side * dw / 2, side * (W / 2 + 0.25)
        local a0, a1 = math.min(inner, outer), math.max(inner, outer)
        if z.archetype == "poly" then
          local _, _, t, _, nn = poly_edge(poly_of(z), p.side)
          local q = {}
          -- set back a quarter metre from the traced wall (an oblique wall is never exactly on the jamb's plane)
          for k, v in ipairs({ { a0, 0.25 }, { a1, 0.25 }, { a1, 0.75 }, { a0, 0.75 } }) do
            q[k] = { p.pt[1] + t[1] * v[1] + nn[1] * v[2], p.pt[2] + t[2] * v[1] + nn[2] * v[2] }
          end
          list[#list + 1] = I.prism("layout", name, "jamb" .. i .. j, ccw(q), g16(y0), g16(y1), { textures = tex })
        else
          local w = p.wall
          local out_s = (p.side == "+x" or p.side == "+z") and 1 or -1
          -- (set back 1/16 m from the wall plane, which a turned frame cannot hold exactly)
          local s0, s1 = math.min(w + out_s / 16, w + out_s * 0.5), math.max(w + out_s / 16, w + out_s * 0.5)
          local x0, z0, x1, z1
          if p.side == "+x" or p.side == "-x" then x0, x1, z0, z1 = s0, s1, c + a0, c + a1 else x0, x1, z0, z1 = c + a0, c + a1, s0, s1 end
          list[#list + 1] = hull("layout", name, "jamb" .. i .. j, box_points(g16(x0), g16(y0), g16(z0), g16(x1), g16(y1), g16(z1)), { textures = tex })
        end
      end
      if z.archetype ~= "poly" then turn_list(list, 0, zone_rot(z)) end
      add({ brushes = list })
    end
  end
  if (l.type == "stair" or l.type == "ramp") and yh - yl > 0.01 then
    local Lo = free_layout(P, link_height(l))
    local k = Lo.k
    local d = M.dirs[k]
    local dc = Lo.fwd and d or { -d[1], -d[2] }
    local Q = M.pts[k]
    local fx, fz = Q[1] + d[1] * Lo.foot, Q[2] + d[2] * Lo.foot
    -- built along +z at the foot, 0.25 m wider each side than the passage (buried in its walls), then turned
    local rec
    if l.type == "stair" then
      rec = flight{ name = name .. "_stairs", pos = { fx, yl, fz }, dir = "+z", rise = Lo.rise, width = W + 0.5, tread = DATA.tread, group = false, textures = tex }
    else
      rec = kit.ramp{ name = name .. "_ramp", pos = { fx, yl, fz }, dir = "+z", rise = Lo.rise, run = Lo.run, width = W + 0.5, group = false, textures = tex }
    end
    local list = rec_brushes({ rec })
    turn_list(list, 0, yaw_of(dc[1], dc[2]), fx, yl, fz)
    add({ brushes = list })
    -- The landing: the rest of the flight segment at the high floor (overlapping the top tread). No face of it may
    -- lie on an oblique plane of the passage air (planes of two brushes through different points never coincide
    -- exactly): it is a quarter metre wider each side than the passage, like the flight, and its far end reaches
    -- half a metre past the mitre under the next segment's floor (solid there), or a quarter metre past the zone's
    -- wall under the zone's floor at the passage's end.
    local len = M.lens[k]
    local n = #M.quads
    local s0, s1
    if Lo.fwd then s0, s1 = Lo.top_s - 0.125, len else s0, s1 = 0, Lo.top_s + 0.125 end
    if s1 - s0 > 0.05 then
      local e = 0.125 / hw
      -- a pair of side points pushed out along their own line to the buried width, then moved by m along the segment
      local function out(Lq, Rq, m)
        m = m or 0
        local dx, dz = Lq[1] - Rq[1], Lq[2] - Rq[2]
        return { Lq[1] + dx * e + d[1] * m, Lq[2] + dz * e + d[2] * m }, { Rq[1] - dx * e + d[1] * m, Rq[2] - dz * e + d[2] * m }
      end
      local poly
      if Lo.fwd then
        local Ls, Rs = out(side_points(M, k, s0))
        local Le, Re
        if k == n then Le, Re = out(side_points(M, k, len + 0.25)) else Le, Re = out(M.L[k + 1], M.R[k + 1], 0.5) end
        poly = { Ls, Rs, Re, Le }
      else
        local Ls, Rs = out(side_points(M, k, s1))
        local Lb, Rb
        if k == 1 then Lb, Rb = out(side_points(M, k, -0.25)) else Lb, Rb = out(M.L[k], M.R[k], -0.5) end
        poly = { Lb, Rb, Rs, Ls }
      end
      add({ brush = I.prism("layout", name, "landing", ccw(poly), g16(yl), g16(yh), { textures = tex }) })
    end
  end
  return made
end

-- Lights along a free link every ~5 m of its path (corridors, stairs, ramps, roads; not bridges).
local function free_lights(P, gname, color)
  local l, M = P.link, P.M
  if l.type == "bridge" then return {} end
  local lights = {}
  local H = link_height(l)
  local Lo = free_layout(P, H)
  local n = math.max(1, math.floor(M.length / 5 + 0.5))
  for i = 1, n do
    local s = M.length * (i - 0.5) / n
    local k = 1
    while k < #M.lens and s > M.lens[k] do s = s - M.lens[k] k = k + 1 end
    local _, top = free_span(P, Lo, k, s)
    local y = top - 0.8
    if l.type == "road" then y = Lo.yl + (l.tunnel and 5.5 or 6) end
    local q, d = M.pts[k], M.dirs[k]
    lights[#lights + 1] = level.place{ kind = "light", name = gname .. "_" .. l.id .. "_light" .. i, pos = { q[1] + d[1] * s, y, q[2] + d[2] * s },
      props = { color = color or kit.defaults.light_color, size_range = 9 }, tags = { kit = "layout", ["kit.name"] = gname .. "_" .. l.id } }
  end
  return lights
end

-- A join's opening: an air box across the shared wall in the rectangle zone's frame, or air strips across the
-- shared line of two traced zones (half a metre into each), from the higher floor to the lower ceiling.
local function build_join(P, gname)
  local name = gname .. "_" .. P.link.id
  local tex = link_textures(P)
  local out = {}
  local sky = (P.za.sky or LA[P.za.archetype].sky) and (P.zb.sky or LA[P.zb.archetype].sky)
  if P.ext then
    local e = P.ext
    local x0, z0, x1, z1
    if e.axis == "x" then x0, x1, z0, z1 = e.w0, e.w1, e.lo, e.hi else x0, x1, z0, z1 = e.lo, e.hi, e.w0, e.w1 end
    out[#out + 1] = hull("layout", name, "join", box_points(g16(x0), g16(P.y0), g16(z0), g16(x1), g16(P.y1), g16(z1)), { air = true, textures = tex })
    turn_list(out, 0, zone_rot(e.zone))
  end
  for i, q in ipairs(P.ext_quads or {}) do
    out[#out + 1] = I.prism("layout", name, "join" .. i, q, g16(P.y0), g16(P.y1), { air = true, textures = tex })
  end
  if sky then I.sky_up(out) end
  return out
end

-- Spots: farthest-point picks from a zone's candidates, clear of solids.
local function pick_spots(R, count, y_pref)
  local cand = {}
  -- only spots well inside the zone's plan (chamfered corners cut the grid's outer points)
  local plan = (R.room and R.room.outline) or R.outline
  local inner = (not R.poly) and plan and (I.inset_convex(plan, 1.2) or plan) or nil
  for _, s in ipairs(R.spots) do
    local ok = clear(R, s[1] - 1.2, s[3] - 1.2, s[1] + 1.2, s[3] + 1.2, s[2], s[2] + 2)
    if ok and inner and not I.inside(inner, s[1], s[3]) then ok = false end
    if ok and R.poly and not (inside(R.poly, s[1], s[3]) and edge_dist(R.poly, s[1], s[3]) >= 1.2) then ok = false end
    if ok then
      for _, so in ipairs(R.solids) do
        if s[1] > so[1] - 1.3 and s[1] < so[3] + 1.3 and s[3] > so[2] - 1.3 and s[3] < so[4] + 1.3 and s[2] < so[6] - 0.1 and s[2] + 2 > so[5] then ok = false end
      end
    end
    if ok then cand[#cand + 1] = s end
  end
  if #cand == 0 then return {} end
  local picked = {}
  -- start from the candidate farthest from the zone centre (a corner, out of the middle)
  local best, bd = 1, -1
  for i, s in ipairs(cand) do
    local d = (s[1] - R.cx) ^ 2 + (s[3] - R.cz) ^ 2 + (y_pref and math.abs(s[2] - y_pref) * -50 or 0)
    if d > bd then best, bd = i, d end
  end
  picked[1] = cand[best]
  while #picked < count and #picked < #cand do
    local bi, bdist = nil, -1
    for i, s in ipairs(cand) do
      local dmin = math.huge
      for _, p in ipairs(picked) do
        local d = (s[1] - p[1]) ^ 2 + (s[3] - p[3]) ^ 2 + 4 * (s[2] - p[2]) ^ 2
        if d < dmin then dmin = d end
      end
      if dmin > bdist then bi, bdist = i, dmin end
    end
    if bi == nil or bdist <= 0.01 then break end
    picked[#picked + 1] = cand[bi]
  end
  return picked
end

-- ---------------------------------------------------------------------------------------------
-- The room detail pass (kit.build_layout detail = ...): what a mapper adds once a zone's shell stands. Every part
-- comes from kit_modules (library modules, else its hand-authored stand-ins), run in the zone's own frame (each wall in
-- a frame along it), so turned and traced zones are detailed like square ones.
-- ---------------------------------------------------------------------------------------------

-- The detail style of a zone's palette theme (kit_modules' style families): timber mine supports in rock palettes.
local DETAIL_STYLE = { industrial = "industrial", bunker = "bunker", admin = "admin", cave = "mine", marsbase = "mars_base" }
-- Archetypes whose roof takes beams or trusses: plain or stepped ceilings. Beamed and coffered shells (gallery,
-- tunnel, colonnade), the storage bay's own beams, small rooms, yards and open-air decks are left as they are.
local DETAIL_CEILING = { hall = true, pit = true, arena = true, split = true, atrium = true, sunken = true, mound = true, start = true, poly = true }
local DETAIL_PARTS = { "walls", "frames", "ceilings", "railings", "stairs", "rocks" }

-- The detail option of kit.build_layout: nil (off) or {walls, frames, ceilings, railings, stairs, rocks, style?, seed}.
local function detail_options(api, v, G)
  if v == false then return nil end
  local D = { walls = true, frames = true, ceilings = true, railings = true, stairs = true, rocks = true,
    seed = (type(G.seed) == "number" and G.seed == math.floor(G.seed)) and G.seed or 1 }
  if v == nil or v == true then return D end
  if type(v) ~= "table" then
    fail(api, "'detail' must be true, false or a table {walls, frames, ceilings, railings, stairs, rocks, style, seed}.", "detail = false skips the room detail pass; detail = {ceilings = false} keeps the rest.")
  end
  check_keys(api .. " detail", v, { "walls", "frames", "ceilings", "railings", "stairs", "rocks", "style", "seed" })
  for _, k in ipairs(DETAIL_PARTS) do if v[k] ~= nil then D[k] = boolean(api, "detail." .. k, v[k], true) end end
  if v.style ~= nil then
    -- (the style is read the way every detail function reads it: an unknown one fails with the list)
    I.detail_kit.new_ctx(api, "detail", { style = v.style }, "detail")
    D.style = v.style
  end
  if v.seed ~= nil then
    local s = number(api, "detail.seed", v.seed)
    if s ~= math.floor(s) then fail(api, "'detail.seed' must be an integer (got " .. fmt(s) .. ").") end
    D.seed = s
  end
  return D
end

-- The zone's plan in its own frame, counter-clockwise (traced zones are built in the world frame).
local function zone_plan(R)
  if R.poly then return ccw(R.poly) end
  if R.room and type(R.room.outline) == "table" and #R.room.outline >= 3 then return ccw(R.room.outline) end
  return ccw(chamfered(R.x0, R.z0, R.x1, R.z1, R.c))
end

-- True when the zone (or any of its air) is open to the sky.
local function zone_sky(R)
  if R.sky or R.A.sky or R.A.open_air then return true end
  for _, b in ipairs(R.brushes) do
    if b.air then
      for _, f in ipairs(b:faces_facing("up")) do if f.show_sky then return true end end
    end
  end
  return false
end

-- The open-air intervals {bottom, top} of the world column (x, z) between ylo and yhi (bounds to 1/64 m), at least
-- `tall` metres high.
local function column_air(pr, x, z, ylo, yhi, tall)
  local out = {}
  local step = 0.5
  local function refine(a, b, want_air) -- a: not want_air, b: want_air
    for _ = 1, 6 do
      local m = (a + b) / 2
      if pr:air_exact(x, m, z) == want_air then b = m else a = m end
    end
    return b
  end
  local prev = pr:air_exact(x, ylo, z)
  local start = prev and ylo or nil
  local y = ylo + step
  while y <= yhi + 1e-9 do
    local a = pr:air_exact(x, y, z)
    if a and not prev then start = refine(y - step, y, true) end
    if not a and prev then
      local top = refine(y, y - step, true)
      if top - start >= tall then out[#out + 1] = { g16(start), g16(top) } end
      start = nil
    end
    prev = a
    y = y + step
  end
  return out
end

-- The runs of a wall edge (the current frame: the edge along +x from 0 to L, the room on +z) with one floor and one
-- ceiling: {t0, t1, floor, top}, sampled about every 1.5 m 0.35 m in front of the wall, run ends bisected to ~5 cm.
local function wall_runs(DK, pr, L, ylo, yhi)
  local function at(t)
    local wx, wz = DK.xf_w(t, 0.35)
    return column_air(pr, wx, wz, ylo, yhi, 2.6)
  end
  local function goes_on(t, run)
    -- the run's floor and ceiling go on at t (four point tests instead of a whole column)
    local wx, wz = DK.xf_w(t, 0.35)
    return pr:air_exact(wx, run.floor + 0.05, wz) and not pr:air_exact(wx, run.floor - 0.06, wz)
      and pr:air_exact(wx, run.top - 0.05, wz) and not pr:air_exact(wx, run.top + 0.06, wz)
  end
  local function edge(lo, hi, run, present_at_hi)
    -- the t between lo and hi where run appears (present_at_hi) or ends
    for _ = 1, 5 do
      local m = (lo + hi) / 2
      local p = goes_on(m, run)
      if p == present_at_hi then hi = m else lo = m end
    end
    return present_at_hi and hi or lo
  end
  local ts = {}
  local n = math.max(1, math.floor((L - 0.8) / 1.5 + 0.5))
  for k = 0, n do ts[#ts + 1] = 0.4 + (L - 0.8) * (n > 0 and k / n or 0.5) end
  local open, done, prev_t = {}, {}, nil
  for _, t in ipairs(ts) do
    local ivs = at(t)
    local still = {}
    local used = {}
    for _, run in ipairs(open) do
      local hit = false
      for j, iv in ipairs(ivs) do
        if not used[j] and math.abs(iv[1] - run.floor) < 0.06 and math.abs(iv[2] - run.top) < 0.3 then used[j], hit = true, true break end
      end
      if hit then run.last = t still[#still + 1] = run
      else run.t1 = edge(run.last, t, run, false) done[#done + 1] = run end
    end
    for j, iv in ipairs(ivs) do
      if not used[j] then
        local run = { floor = iv[1], top = iv[2], last = t }
        run.t0 = prev_t and edge(prev_t, t, run, true) or 0
        still[#still + 1] = run
      end
    end
    open, prev_t = still, t
  end
  for _, run in ipairs(open) do run.t1 = L done[#done + 1] = run end
  return done
end

-- The world opening key kit.frame_opening tags a frame with (one frame per opening, from whichever side comes first).
local function opening_key_world(DK, o)
  local T = o.thickness or 1
  local wx, wz = DK.xf_w(o.pos[1] + o.nx * T / 2, o.pos[3] + o.nz * T / 2)
  local function q(v) return math.floor(v * 4 + 0.5) / 4 end
  return string.format("%g,%g,%g", q(wx), q(o.pos[2]), q(wz))
end

-- Drop-away runs along a world leg a -> b at floor height y: where one side holds floor at y and the other falls
-- away (no floor within 1.5 m below), as {s0, s1, side (+1: the walkable side is to the left of a -> b)}.
local function drop_runs(pr, a, b, y)
  local dx, dz = b[1] - a[1], b[2] - a[2]
  local L = math.sqrt(dx * dx + dz * dz)
  if L < 1 then return {} end
  local ux, uz = dx / L, dz / L
  local nx, nz = -uz, ux
  local function floor_at(x, z) return pr:air_exact(x, y + 0.5, z) and not pr:air_exact(x, y - 0.15, z) end
  local function drop_at(x, z) return pr:air_exact(x, y + 0.05, z) and pr:air_exact(x, y - 0.5, z) and pr:air_exact(x, y - 1.5, z) end
  local runs, cur = {}, nil
  local step = 0.25
  local k, nk = 0, math.floor(L / step)
  while k <= nk do
    local s = math.min(L, k * step)
    local px, pz = a[1] + ux * s, a[2] + uz * s
    local side = 0
    if floor_at(px + nx * 0.45, pz + nz * 0.45) and drop_at(px - nx * 0.45, pz - nz * 0.45) then side = 1
    elseif floor_at(px - nx * 0.45, pz - nz * 0.45) and drop_at(px + nx * 0.45, pz + nz * 0.45) then side = -1 end
    if cur and cur.side == side and side ~= 0 then cur.s1 = s
    else
      if cur and cur.side ~= 0 then runs[#runs + 1] = cur end
      cur = { s0 = s, s1 = s, side = side }
    end
    k = k + 1
  end
  if cur and cur.side ~= 0 then runs[#runs + 1] = cur end
  local out = {}
  for _, r in ipairs(runs) do
    if r.s1 - r.s0 >= 1.0 then out[#out + 1] = { r.s0 + 0.1, r.s1 - 0.1, r.side, ux = ux, uz = uz, L = L } end
  end
  return out
end

-- One zone's detail. Returns {parts, by_part, roles, skipped, fallback, notes, brushes, style}.
local function detail_zone(api, R, D, DK, framed)
  local PB = ged.import("kit_probe")
  local out = { parts = 0, by_part = {}, roles = {}, skipped = 0, fallback = false, notes = {}, brushes = {} }
  local style = D.style or DETAIL_STYLE[R.pal.theme] or "industrial"
  out.style = style
  local wall_tex = R.textures and R.textures.wall or nil
  local function new(fn, part)
    local ctx = DK.new_ctx(api, fn, { style = style, seed = D.seed }, R.name .. "_" .. part)
    ctx.quiet = true -- (a stand-in part is reported in report.detail.notes, not logged as a warning)
    if wall_tex then
      ctx.wall_texture = wall_tex
      ctx.material = wall_tex:lower():match("^(%a+_)")
      ctx.wall_family = DK.family(wall_tex)
      ctx.wall_brightness = modules.available and modules.texture_brightness(wall_tex) or nil
    end
    return ctx
  end
  local function merge(key, ctx)
    local r = DK.finish(ctx)
    for _, e in ipairs(r.placed) do
      out.parts = out.parts + 1
      out.by_part[key] = (out.by_part[key] or 0) + 1
      out.roles[e.role] = (out.roles[e.role] or 0) + 1
    end
    for _, b in ipairs(r.brushes) do out.brushes[#out.brushes + 1] = b end
    out.skipped = out.skipped + #r.skipped
    if r.fallback then out.fallback = true end
    if r.note and r.note ~= "library modules" then out.notes[#out.notes + 1] = key .. ": " .. r.note end
  end
  local open_air = R.A.open_air
  local rocky = (R.pal.theme == "cave")
  local sky = zone_sky(R)
  local pr = PB.shared()

  -- 1. Walls and their openings: each wall of the plan in a frame along it, split into runs of one floor and ceiling
  -- (a gallery wall is two storeys, a raised half's wall starts a level up). Frames first, so pilasters keep off them.
  local pilaster_pts = {}
  if not open_air and (D.walls or D.frames) then
    local plan = zone_plan(R)
    local n = #plan
    local ylo, yhi = R.y - 0.3, R.y + R.h + (R.F and R.F.rise or 0) + 1
    local depth = R.poly and math.max(3, math.sqrt(R.area or 100) / 2) or math.max(3, math.min(R.w, R.d) / 2)
    -- a corner under 120 degrees takes one pilaster: the wall after it starts half a bay in
    local far_start = {}
    for i = 1, n do
      local p0, p1, p2 = plan[(i - 2) % n + 1], plan[i], plan[i % n + 1]
      local ax, az, bx, bz = p1[1] - p0[1], p1[2] - p0[2], p2[1] - p1[1], p2[2] - p1[2]
      local la, lb = math.sqrt(ax * ax + az * az), math.sqrt(bx * bx + bz * bz)
      if la > 1e-6 and lb > 1e-6 then
        local turn = math.deg(math.acos(clamp((ax * bx + az * bz) / (la * lb), -1, 1)))
        far_start[i] = (180 - turn) < 120
      end
    end
    local walls = {}
    local fctx = new("frame_opening", "frames")
    local door_top = nil
    for i = 1, n do
      local a, b = plan[i], plan[i % n + 1]
      local ex, ez = b[1] - a[1], b[2] - a[2]
      local L = math.sqrt(ex * ex + ez * ez)
      if L >= 1.5 then
        local ux, uz = ex / L, ez / L
        local ax, az = to_world(R.rot, a[1], a[2])
        local xf = { rot = (R.rot + math.deg(math.atan2(-uz, ux))) % 360, px = ax, pz = az }
        DK.set_frame(xf.rot, xf.px, xf.pz)
        local runs = wall_runs(DK, pr, L, ylo, yhi)
        table.sort(runs, function(p, q) if p.floor ~= q.floor then return p.floor < q.floor end return p.t0 < q.t0 end)
        local read = {}
        for _, run in ipairs(runs) do
          if run.t1 - run.t0 >= 1.5 then
            local s = { name = R.name, kind = "room", x0 = run.t0, x1 = run.t1, z0 = 0, z1 = depth, y0 = run.floor, floor_y = run.floor, y1 = run.top }
            local storey = string.format("%g/%g", run.floor, run.top)
            -- (a wall is read along its whole length at each storey's heights, once: a doorway where the floor in
            -- front of it changes, a step at the door, ends a run, and a run alone sees only part of it)
            local whole = { name = R.name, kind = "room", x0 = 0, x1 = L, z0 = 0, z1 = depth, y0 = run.floor, floor_y = run.floor, y1 = run.top }
            if D.frames and not read[storey] then
              read[storey] = true
              for _, o in ipairs(DK.openings_of(whole, { "-z" })) do
                -- one frame per opening, from whichever zone reaches it first (the two sides of a stepped doorway
                -- read different sills: matched by plan position and a storey's height)
                local T = o.thickness or 1
                local mx, mz = DK.xf_w(o.pos[1] + o.nx * T / 2, o.pos[3] + o.nz * T / 2)
                local seen = false
                for _, f in ipairs(framed) do
                  if math.abs(f[1] - mx) < 1.0 and math.abs(f[3] - mz) < 1.0 and math.abs(f[2] - o.pos[2]) < 1.6 then seen = true break end
                end
                -- (the top of a lower storey's tall doorway, seen from a gallery above it, is no opening of this storey)
                if not seen and (o.height < 1.5 or (o.sill or 0) < 0.05 and pr:air_exact(mx, o.pos[2] - 0.3, mz)) then seen = true end
                local key = opening_key_world(DK, o)
                if not seen and not DK.framed_already(key) then
                  framed[#framed + 1] = { mx, o.pos[2], mz }
                  -- a door, or a window; an opening high in this wall with floor beyond it at its sill is a way through
                  -- (a drop from the far side): framed as a door, never with a window's sill across it
                  o.kind = "door"
                  if (o.sill or 0) > 0.6 then
                    local fx, fz = DK.xf_w(o.pos[1] + o.nx * (T + 0.5), o.pos[3] + o.nz * (T + 0.5))
                    local y = o.pos[2]
                    if not (pr:air_exact(fx, y + 0.5, fz) and not pr:air_exact(fx, y - 0.15, fz)) then o.kind = "window" end
                  end
                  DK.frame_one(fctx, o, key)
                end
                local top = o.pos[2] + o.height
                if not door_top or top > door_top then door_top = top end
              end
            end
            -- pilasters on the long walls of a storey that reaches the roof; a storey under a gallery takes its trims only
            walls[#walls + 1] = { xf = xf, s = s, far = { a = (run.t0 < 0.3) and far_start[i] or false, b = false },
              pilasters = L >= 4 and run.top >= R.y + R.h - 0.6, whole = whole, profile_key = i .. "/" .. storey }
          end
        end
      end
    end
    DK.set_frame(nil)
    merge("frames", fctx)
    -- (rock walls keep their own face: no pilasters or trims on a cave's rock)
    if D.walls and not rocky and #walls > 0 then
      local wctx = new("wall_detail", "walls")
      local hs = {}
      for _, w in ipairs(walls) do
        local k = math.floor((w.s.y1 - w.s.floor_y) * 2 + 0.5) / 2
        hs[k] = (hs[k] or 0) + (w.s.x1 - w.s.x0)
      end
      local main_h, best = nil, -1
      for k, len in pairs(hs) do if len > best + 1e-9 or (math.abs(len - best) <= 1e-9 and k < main_h) then main_h, best = k, len end end
      -- (pilasters at least 5 m apart; no mid-height band: a ledge halfway up every wall is standing room on its top,
      -- which the walk measure reads as floor)
      local res = DK.walls_impl(wctx, walls, { pilasters = true, base = true, top = true, band = false, panels = true, min_pitch = 5 }, main_h, door_top)
      pilaster_pts = res.pilasters
      merge("walls", wctx)
    end
  end

  -- 2. Ceiling structure: beams (trusses in tall wide rooms) across the short way, on the pilasters where they stand,
  -- wall to wall under a roof — never where the zone is open to the sky, never where a chord ends in an open side.
  if D.ceilings and not open_air and not sky and DETAIL_CEILING[R.z.archetype] and R.h >= 4.4 then
    local plan = zone_plan(R)
    -- the beam frame: the zone's frame (a traced zone's: along its longest wall), turned so beams run along its x
    local rot0 = R.rot
    if R.poly then
      local longest, lu, lz_ = -1, 1, 0
      for i = 1, #plan do
        local a, b = plan[i], plan[i % #plan + 1]
        local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
        if L > longest + 1e-6 then longest, lu, lz_ = L, (b[1] - a[1]) / L, (b[2] - a[2]) / L end
      end
      rot0 = math.deg(math.atan2(-lz_, lu)) % 360
    end
    local function in_frame(rot)
      DK.set_frame(rot, 0, 0)
      local poly, x0, z0, x1, z1 = {}, math.huge, math.huge, -math.huge, -math.huge
      for i, q in ipairs(plan) do
        local wx, wz = to_world(R.rot, q[1], q[2])
        local lx, lz = DK.xf_l(wx, wz)
        poly[i] = { lx, lz }
        x0, z0, x1, z1 = math.min(x0, lx), math.min(z0, lz), math.max(x1, lx), math.max(z1, lz)
      end
      return poly, x0, z0, x1, z1
    end
    local poly, x0, z0, x1, z1 = in_frame(rot0)
    if (x1 - x0) > (z1 - z0) + 1e-6 then poly, x0, z0, x1, z1 = in_frame((rot0 + 90) % 360) end
    local y1 = R.y + R.h
    local floor_hi = R.y
    for _, lv in ipairs(R.z.levels or {}) do if type(lv) == "number" then floor_hi = math.max(floor_hi, R.y + lv) end end
    local cctx = new("ceiling_detail", "ceiling")
    -- beam lines: the pilasters' positions along the long axis, else a regular pitch
    local zs = {}
    for _, p in ipairs(pilaster_pts) do
      local _, lz = DK.xf_l(p[1], p[2])
      if lz > z0 + 0.8 and lz < z1 - 0.8 then zs[#zs + 1] = lz end
    end
    table.sort(zs)
    local lines_z = {}
    for _, v in ipairs(zs) do if #lines_z == 0 or v - lines_z[#lines_z] >= 2.5 then lines_z[#lines_z + 1] = v end end
    if #lines_z == 0 then
      local Lz = z1 - z0
      local pitch = clamp(math.max(4, R.h * 0.5), 3, 8)
      local nb = math.max(1, math.floor(Lz / pitch + 0.5))
      for k = 1, nb - 1 do lines_z[#lines_z + 1] = z0 + Lz * k / nb end
      if nb == 1 then lines_z[1] = (z0 + z1) / 2 end
    end
    local lines = {}
    local maxspan = 0
    for _, zc in ipairs(lines_z) do
      -- the chord of the plan along x at zc
      local xs = {}
      for i = 1, #poly do
        local a, b = poly[i], poly[i % #poly + 1]
        if (a[2] - zc) * (b[2] - zc) < 0 then xs[#xs + 1] = a[1] + (b[1] - a[1]) * (zc - a[2]) / (b[2] - a[2]) end
      end
      table.sort(xs)
      local pr2 = PB.shared()
      -- the ceiling over a point of the chord (where solid starts above the base ceiling's underside), nil when open
      local function ceil_at(x)
        local wx, wz = DK.xf_w(x, zc)
        local y = y1 - 0.4
        if not pr2:air_exact(wx, y, wz) then return nil end
        while y < y1 + 8 do
          local yn = y + 0.25
          if not pr2:air_exact(wx, yn, wz) then
            local lo, hi = y, yn
            for _ = 1, 5 do local m = (lo + hi) / 2 if pr2:air_exact(wx, m, wz) then lo = m else hi = m end end
            return g16(hi)
          end
          y = yn
        end
        return nil
      end
      for k = 1, #xs - 1, 2 do
        local xa, xb = xs[k] + 0.02, xs[k + 1] - 0.02
        if xb - xa >= 3 then
          -- A beam sits flush under the ceiling it hangs from (nothing to stand on above it): along the chord, the
          -- longest stretch of one ceiling height — the raised bay of a stepped roof, else the whole chord — wall to
          -- wall or bay wall to bay wall.
          local samples = {}
          local nsm = math.max(2, math.floor((xb - xa - 0.5) / 0.75) + 1)
          for q = 0, nsm - 1 do
            local x = xa + 0.25 + (xb - xa - 0.5) * q / (nsm - 1)
            samples[#samples + 1] = { x, ceil_at(x) }
          end
          local best, cur = nil, nil
          for _, smp in ipairs(samples) do
            if smp[2] and cur and math.abs(smp[2] - cur.c) <= 0.05 then cur.x1, cur.n = smp[1], cur.n + 1
            else
              if cur and (not best or cur.x1 - cur.x0 > best.x1 - best.x0) then best = cur end
              cur = smp[2] and { x0 = smp[1], x1 = smp[1], c = smp[2], n = 1 } or nil
            end
          end
          if cur and (not best or cur.x1 - cur.x0 > best.x1 - best.x0) then best = cur end
          if best then
            -- its ends: the chord's end, else the step in the ceiling (bisected)
            local function step_at(inside, outside)
              for _ = 1, 5 do
                local m = (inside + outside) / 2
                local c = ceil_at(m)
                if c and math.abs(c - best.c) <= 0.05 then inside = m else outside = m end
              end
              return inside
            end
            local s0 = (best.x0 <= xa + 0.26) and xa or step_at(best.x0, best.x0 - 0.75)
            local s1 = (best.x1 >= xb - 0.26) and xb or step_at(best.x1, best.x1 + 0.75)
            if s1 - s0 >= 3 then
              -- both ends on a wall (solid just past each end under the ceiling), never on an open side
              local wa1, wa2 = DK.xf_w(s0 - 0.25, zc)
              local wb1, wb2 = DK.xf_w(s1 + 0.25, zc)
              if not pr2:air_exact(wa1, best.c - 0.3, wa2) and not pr2:air_exact(wb1, best.c - 0.3, wb2) then
                lines[#lines + 1] = { s0, s1, zc, best.c }
                maxspan = math.max(maxspan, s1 - s0)
              end
            end
          end
        end
      end
    end
    if #lines > 0 then
      local top = y1
      for _, ln in ipairs(lines) do top = math.max(top, ln[4]) end
      local kind = (top - R.y >= 8 and maxspan >= 10) and "trusses" or "beams"
      DK.beams_impl(cctx, lines, y1, floor_hi, kind, top - R.y)
    end
    DK.set_frame(nil)
    merge("ceiling", cctx)
  end

  -- 3. Railings on the edges the zone declared that drop away (gallery bands, a raised half, a chasm's rim and the
  -- deck across it), open where a stair, ramp or deck lands.
  if D.railings and #R.rails > 0 then
    local rctx = new("railing", "railings")
    for _, e in ipairs(R.rails) do
      local ax, az = to_world(R.rot, e[1][1], e[1][2])
      local bx, bz = to_world(R.rot, e[2][1], e[2][2])
      for _, run in ipairs(drop_runs(pr, { ax, az }, { bx, bz }, e[3])) do
        -- a frame along the run: +x along a -> b, the walkable side on +z (left) or -z
        local sx, sz = ax + run.ux * run[1], az + run.uz * run[1]
        DK.set_frame(math.deg(math.atan2(-run.uz, run.ux)) % 360, sx, sz)
        DK.railing_impl(api, rctx, { { 0, e[3], 0 }, { run[2] - run[1], e[3], 0 } }, run[3] > 0 and "+z" or "-z", nil)
      end
    end
    DK.set_frame(nil)
    merge("railings", rctx)
  end

  -- 4. Stringers and handrails on the zone's own flights (in the zone's frame, where each runs along an axis).
  if D.stairs and #R.flights > 0 then
    local sctx = new("stair_detail", "stairs")
    DK.set_frame(R.rot, 0, 0)
    for _, rec in ipairs(R.flights) do DK.stair_impl(api, { stairs = rec }, sctx) end
    DK.set_frame(nil)
    merge("stairs", sctx)
  end

  -- 5. Rock formations at the walls of rock spaces (the generative fill's engine space keeps doorways, routes and
  -- the cart lane clear and holds the walk budget).
  if D.rocks and rocky and not open_air then
    local keep = {}
    for _, seg in ipairs(R.lane or {}) do
      local len = math.sqrt((seg[3] - seg[1]) ^ 2 + (seg[4] - seg[2]) ^ 2)
      local nk = math.max(1, math.ceil(len / 2))
      for k = 0, nk do
        local x, z = R.W(seg[1] + (seg[3] - seg[1]) * k / nk, seg[2] + (seg[4] - seg[2]) * k / nk)
        keep[#keep + 1] = { x, R.y, z, r = seg[5] + 1 }
      end
    end
    local area = R.area or (R.w * R.d)
    local rd = DK.quietly(kit.rock_detail, { space = R.name, style = "cave", seed = D.seed, count = clamp(math.floor(area / 150 + 0.5), 2, 6),
      keep = (#keep > 0) and keep or nil, name = R.name .. "_rocks" })
    if rd.fallback and rd.note then out.notes[#out.notes + 1] = "rocks: " .. rd.note end
    for _, e in ipairs(rd.placed) do
      out.parts = out.parts + 1
      out.by_part.rocks = (out.by_part.rocks or 0) + 1
      out.roles[e.role] = (out.roles[e.role] or 0) + 1
    end
    for _, b in ipairs(rd.brushes) do out.brushes[#out.brushes + 1] = b end
    if rd.fallback then out.fallback = true end
  end

  -- What stands out on the floor (rock formations) is kept clear by the spawns picked after it (the zone's frame).
  -- Wall parts, frames, railings and stair finishes hug walls and edges the spawn spots already keep off; their boxes,
  -- on a turned or traced wall, would cover the room's middle.
  for _, b in ipairs(out.brushes) do
    local bb = b.bounds
    if bb.min.y < R.y + 2.2 and b:tags().kit == "rock_detail" then
      local lx0, lz0, lx1, lz1 = math.huge, math.huge, -math.huge, -math.huge
      for _, c in ipairs({ { bb.min.x, bb.min.z }, { bb.max.x, bb.min.z }, { bb.min.x, bb.max.z }, { bb.max.x, bb.max.z } }) do
        local lx, lz = to_local(R.rot, c[1], c[2])
        lx0, lz0, lx1, lz1 = math.min(lx0, lx), math.min(lz0, lz), math.max(lx1, lx), math.max(lz1, lz)
      end
      note_solid(R, lx0, lz0, lx1, lz1, bb.min.y, bb.max.y)
    end
  end
  return out
end

---@kit kit.build_layout{graph, name?, lights?, strict?, force?, populate?, detail?} → layout_report — Builds a layout graph: kit.check_layout first (errors refuse; design-rule violations warn, or refuse with strict = true), then in CSG time order the zone shells (and the open-air void), every link's air, the zones' interiors (stairs, galleries, courts, cover, ...), the links' steps, ramps, landings, lifts and bridges (outside open-air layouts a bridge's deck spans a chasm cut to the sky, link.depth metres deep, default 8), details, the room detail pass (detail: wall trims and pilasters, door frames, ceiling beams, railings, stair finishes, rock formations), lights, and the objectives: spawn groups, CTF flags, the KOTH hill (a Capture_Point_Handler with its trigger) and the payload cart (One Way mover on the track on any heading, Payload_Handler, checkpoints on its keyframes, each linking the attacker and defender spawns that are live while it is the target). A turned zone (rot) is built in its own frame and turned into place about the world origin; free links are built along their routes (one air prism per segment on shared mitre points, half a metre into each zone, the stair or ramp on the longest straight, door jambs set 1/16 m behind the wall planes); joins open the shared boundary. Every brush of a zone is tagged kit.zone = the zone's name (<name>_<zone id>), and that name works as a room name in kit.dress, kit.items and kit.spawn_points. Returns {name, zones[] (id, archetype, name, min, max (world bounds), outline (world plan), rot, floor_y, spots (world), brushes, room, detail), links[] (id, type, a, b, mode (classic, free or join), width, offset, a_side, b_side, path (a free link's route in world metres), length), spawns, objectives, warnings[], violations[], area, detail ({zones, parts, by_part, roles, fallback, notes}: what the detail pass placed)}.
---@opt graph: table — Required: a kit.layout graph (possibly edited).
---@opt name: string = "L" — Prefix of every part's name (and its kit.name tags).
---@opt lights: boolean = true — Place lights in every zone and link.
---@opt strict: boolean = false — Refuse to build when any design rule is violated (errors always refuse).
---@opt force: boolean = false — Build even with check errors that do not stop the geometry (use with care).
---@opt populate: function = none — Called as populate(zone_report, zone, graph) after each zone is built: the hook for a population pass (dressing, items, spawns) per zone (theme and density in zone.dress).
---@opt detail: boolean|table = true — The room detail pass, run on every zone once the geometry stands (before lights, spawns and populate): pilasters, base and top trims on the long walls, frames on the doorways and windows, beams or trusses under roofed ceilings (never under open sky), railings on gallery, raised-floor, pit and bridge edges that drop away, stringers and handrails on the zones' stairs, rock formations in rock spaces — chosen by the zone's archetype and palette theme (industrial, bunker, admin, mine timber in cave palettes, mars base), from the module library when it is present (one donor set per level and style) else hand-authored parts, kept off doorways, routes, stairs, spawn spots, the cart lane and bridge decks and inside the walk budget. false skips it; a table turns parts off or on: {walls, frames, ceilings, railings, stairs, rocks} (booleans, default true), style (one detail style for every zone) and seed (default the graph's seed). The report's detail field counts what was placed.
function kit.build_layout(t)
  local api = "kit.build_layout"
  check_keys(api, t, { "graph", "name", "lights", "strict", "force", "populate", "detail" })
  local G = t.graph
  if type(G) ~= "table" then fail(api, "'graph' is required (a kit.layout result).") end
  local gname = t.name or "L"
  if type(gname) ~= "string" or gname == "" then fail(api, "'name' must be a non-empty string.") end
  if t.populate ~= nil and type(t.populate) ~= "function" then fail(api, "'populate' must be a function(zone_report, zone, graph).") end
  local V, plan = check(G)
  local errors, rules = {}, {}
  for _, v in ipairs(V) do if v.severity == "error" then errors[#errors + 1] = v.message else rules[#rules + 1] = v.message end end
  if #errors > 0 and not (t.force and plan) then
    fail(api, "the layout has " .. #errors .. " error(s): " .. table.concat(errors, " "), "Fix the graph (kit.check_layout lists each violation) and build again.")
  end
  if #rules > 0 and boolean(api, "strict", t.strict, false) then
    fail(api, "the layout breaks " .. #rules .. " design rule(s): " .. table.concat(rules, " "), "Fix the graph, or build without strict.")
  end
  for _, m in ipairs(rules) do log.warn(api .. ": " .. m) end
  local open_air = G.class == "open_air"
  local want_lights = boolean(api, "lights", t.lights, true)
  local D = detail_options(api, t.detail, G)
  local report = { kind = "layout_report", name = gname, zones = {}, links = {}, objectives = {}, warnings = {}, violations = V, spawns = 0 }
  for _, m in ipairs(rules) do report.warnings[#report.warnings + 1] = m end

  level.props:set("multiplayer", true)
  local Rs = {}
  for _, z in ipairs(G.zones) do
    local R = zone_ctx(gname, z, plan)
    R.detail = D
    Rs[#Rs + 1] = R
  end
  -- Every brush of a zone answers to the zone's name (kit.zone), except a chasm's air, which is no floor of it.
  local function tag_zone(R)
    for _, b in ipairs(R.brushes) do
      if not (R.untagged and R.untagged[b.uid]) then b:tag("kit.zone", R.name) end
    end
  end
  -- Where a zone's wall is open (a join): a point just outside it lies in another zone's world plan.
  local wplans = {}
  for _, R in ipairs(Rs) do wplans[#wplans + 1] = { R.id, zone_poly(R.z) } end
  for _, R in ipairs(Rs) do
    local id = R.id
    R.in_other = function(x, z)
      for _, e in ipairs(wplans) do if e[1] ~= id and e[2] and inside(e[2], x, z) then return true end end
      return false
    end
  end
  -- The cart lane through payload yards and roads (cover keeps clear of it).
  local cart = nil
  for _, o in ipairs(G.objectives or {}) do if o.kind == "cart" then cart = o end end
  local byid = {}
  for _, R in ipairs(Rs) do byid[R.id] = R end
  local cart_pts = nil
  local plan_of_link = {}
  for _, P in ipairs(plan.links) do plan_of_link[P.link] = P end
  if cart then
    cart_pts = {}
    local spine = cart.zones or {}
    -- The port where the cart crosses between zone R and the spine zone other_id (any side, any heading).
    local function cart_port(R, other_id)
      if other_id == nil then return nil end
      for _, p in ipairs(R.ports) do
        local l = p.link
        if CART_LINK[l.type] and ((l.a == R.id and l.b == other_id) or (l.b == R.id and l.a == other_id)) then return p end
      end
      return nil
    end
    -- A point depth metres inside a wall of R, at coordinate m along that wall.
    local function wall_pt(R, side, m, depth)
      if side == "+x" then return R.x1 - depth, m elseif side == "-x" then return R.x0 + depth, m
      elseif side == "+z" then return m, R.z1 - depth end
      return m, R.z0 + depth
    end
    -- A point depth metres inside the zone from a port's opening (the port's own point and outward normal).
    local function port_pt(p, depth) return p.pt[1] - p.nrm[1] * depth, p.pt[2] - p.nrm[2] * depth end
    -- Cart points are worked out in the zone's frame and kept in world coordinates.
    local function push(R, x, y, z)
      local wx, wz = R.W(x, z)
      cart_pts[#cart_pts + 1] = { wx, y, wz }
    end
    for i, id in ipairs(spine) do
      local R = byid[id]
      if R then
        local entry, exit = cart_port(R, spine[i - 1]), cart_port(R, spine[i + 1])
        -- along a free link from the previous spine zone: its bends, in the order the cart meets them
        local FP = entry and plan_of_link[entry.link]
        if FP and FP.mode == "free" and FP.pts then
          local y = math.min(FP.ya, FP.yb)
          local list = {}
          for k = 2, #FP.pts - 1 do list[#list + 1] = FP.pts[k] end
          if FP.link.a == R.id then local r = {} for k = #list, 1, -1 do r[#r + 1] = list[k] end list = r end
          for _, q in ipairs(list) do cart_pts[#cart_pts + 1] = { q[1], y, q[2] } end
        end
        R.lane = {}
        if R.z.archetype == "road" then
          local F = road_frame(R.z)
          local ys, yn = R.y, R.y + F.rise
          for _, e in ipairs({ { 1, ys }, { F.ls, ys }, { F.len - F.ln, yn }, { F.len - 1, yn } }) do
            local x, z = F.xz(e[1], F.centre)
            push(R, x, e[2], z)
          end
          local ax, az = F.xz(0, F.centre)
          local bx, bz = F.xz(F.len, F.centre)
          R.lane[1] = { ax, az, bx, bz, 3.2 }
        elseif R.z.archetype == "finale" then
          local F = zone_frame(R, heading_of(R.z))
          local ex, ez, wx, wz
          if entry then
            ex, ez = port_pt(entry, 2)
            wx, wz = port_pt(entry, 0)
          else
            ex, ez = F.w(0, -F.hd + 2)
            wx, wz = F.w(0, -F.hd)
          end
          local lx = F.l(ex, ez)
          R.cart_c = lx
          local ey = R.y
          local cxp = clamp(lx, -7 + 4.5, 7 - 4.5)
          local pz0 = -7 + 2
          push(R, ex, ey, ez)
          local x, z = F.w(cxp, pz0)
          push(R, x, ey, z)
          R.lane[1] = { wx, wz, x, z, 5 }
          x, z = F.w(cxp, pz0 + 8)
          push(R, x, ey - 2.5, z)
          x, z = F.w(0, 2)
          push(R, x, ey - 2.5, z)
          R.checkpoint_at = #cart_pts
        else
          -- Through the zone's centre: in at the entry port, out at the exit port (a turn when they are not opposite).
          local ey = R.y
          local ex, ez, ewx, ewz, xx, xz, xwx, xwz
          if entry then
            ex, ez = port_pt(entry, 2)
            ewx, ewz = port_pt(entry, 0)
          elseif exit and not R.poly then
            local m = (exit.side == "+x" or exit.side == "-x") and R.cz or R.cx
            ex, ez = wall_pt(R, OPP[exit.side], m, 2)
            ewx, ewz = wall_pt(R, OPP[exit.side], m, 0)
          elseif R.poly then
            ex, ez = R.cx, R.cz
            ewx, ewz = R.cx, R.cz
          else
            ex, ez = wall_pt(R, "-z", R.cx, 2)
            ewx, ewz = wall_pt(R, "-z", R.cx, 0)
          end
          if exit then
            xx, xz = port_pt(exit, 2)
            xwx, xwz = port_pt(exit, 0)
          elseif R.poly or (entry and type(entry.side) ~= "string") then
            xx, xz = R.cx, R.cz
            xwx, xwz = R.cx, R.cz
          else
            local side = entry and OPP[entry.side] or "+z"
            local m = entry and (entry.lo + entry.hi) / 2 or R.cx
            xx, xz = wall_pt(R, side, m, 2)
            xwx, xwz = wall_pt(R, side, m, 0)
          end
          push(R, ex, ey, ez)
          push(R, R.cx, ey, R.cz)
          R.checkpoint_at = #cart_pts
          push(R, xx, ey, xz)
          R.lane[1] = { ewx, ewz, R.cx, R.cz, 5 }
          R.lane[2] = { R.cx, R.cz, xwx, xwz, 5 }
        end
      end
    end
  end

  -- 1. The open-air void (before every solid it holds).
  local void = nil
  if open_air then
    local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
    local y0, y1 = math.huge, -math.huge
    for _, z in ipairs(G.zones) do
      local a, b, c, d = bbox(zone_poly(z))
      x0, z0, x1, z1 = math.min(x0, a), math.min(z0, b), math.max(x1, c), math.max(z1, d)
      y0, y1 = math.min(y0, z.pos[2]), math.max(y1, z.pos[2] + (z.levels[#z.levels] or 0))
    end
    void = hull("layout", gname .. "_void", "void", box_points(x0 - 30, y0 - DATA.open_air_void_depth, z0 - 30, x1 + 30, y1 + 40, z1 + 30),
      { air = true, texture = kit.defaults.sky_texture })
    void:box_map{ tile = 64 }
    for _, f in ipairs(void.faces) do f:set_flag("show_sky", true) end
  end
  -- Each zone step builds in the zone's own frame, then turns what it built by the zone's rot about the world
  -- origin (one pivot for every frame, so walls that meet in a frame still meet after the turn).
  local function zone_step(R, fn)
    if not fn then return end
    local n0 = #R.brushes
    fn(R)
    bury_walls(R, n0)
    turn_list(R.brushes, n0, R.rot)
  end
  -- 2. Zone shells.
  for _, R in ipairs(Rs) do zone_step(R, RECIPES[R.z.archetype].shell) end
  -- 3. Link air (classic links in their zones' frame, free links and joins where they are).
  local airs = {}
  for _, P in ipairs(plan.links) do
    if P.ok and P.mode == "classic" then turn_list(build_link_air(P, gname, airs, open_air), 0, zone_rot(P.za))
    elseif P.ok and P.mode == "free" then build_free_air(P, gname, open_air)
    elseif P.ok and P.mode == "join" then build_join(P, gname) end
  end
  -- The sky box (after the shells and links, so it lands beyond them).
  local needs_sky = open_air
  for _, R in ipairs(Rs) do if R.sky then needs_sky = true end end
  for _, P in ipairs(plan.links) do if (P.link.type == "road" or P.link.type == "bridge") and not P.link.tunnel then needs_sky = true end end
  if needs_sky then I.ensure_sky(gname) end
  -- 4. Zone interiors.
  for _, R in ipairs(Rs) do zone_step(R, RECIPES[R.z.archetype].inner) end
  -- 5. Link solids.
  for _, P in ipairs(plan.links) do
    if P.ok and P.mode == "classic" then
      local rot = zone_rot(P.za)
      for _, m in ipairs(build_link_solid(P, gname, open_air)) do
        if not (type(m) == "table" and m.turned) then turn_list(rec_brushes({ m }), 0, rot) end
      end
    elseif P.ok and P.mode == "free" then
      build_free_solid(P, gname, open_air)
    end
  end
  -- 6. Details.
  for _, R in ipairs(Rs) do zone_step(R, RECIPES[R.z.archetype].late) end
  -- 6b. The room detail pass, once every zone and link stands (so it sees each doorway and neighbour as built) and
  -- before the lights, spawns and population that plan around it.
  if D and I.detail_kit then
    local sum = { zones = 0, parts = 0, by_part = {}, roles = {}, fallback = false, notes = {} }
    for _, R in ipairs(Rs) do tag_zone(R) end
    local framed = {}
    -- (zones in an order that depends on the zones alone, lowest floor and largest plan first: a doorway two zones
    -- share is framed from the same side wherever the layout repeats itself, a mirrored CTF half included)
    local order = {}
    for i, R in ipairs(Rs) do order[i] = { R = R, i = i, a = R.area or (R.w * R.d) } end
    table.sort(order, function(p, q)
      if math.abs(p.R.y - q.R.y) > 1e-6 then return p.R.y < q.R.y end
      if math.abs(p.a - q.a) > 1e-6 then return p.a > q.a end
      return p.i < q.i
    end)
    for _, e in ipairs(order) do
      local R = e.R
      local zd = detail_zone(api, R, D, I.detail_kit, framed)
      for _, b in ipairs(zd.brushes) do b:tag("kit.zone", R.name) end
      R.detail_report = { parts = zd.parts, by_part = zd.by_part, roles = zd.roles, skipped = zd.skipped, fallback = zd.fallback, style = zd.style }
      if zd.parts > 0 then sum.zones = sum.zones + 1 end
      sum.parts = sum.parts + zd.parts
      for k, v in pairs(zd.by_part) do sum.by_part[k] = (sum.by_part[k] or 0) + v end
      for k, v in pairs(zd.roles) do sum.roles[k] = (sum.roles[k] or 0) + v end
      if zd.fallback then sum.fallback = true end
      for _, m in ipairs(zd.notes) do
        local seen = false
        for _, e in ipairs(sum.notes) do if e == m then seen = true break end end
        if not seen and #sum.notes < 12 then sum.notes[#sum.notes + 1] = m end
      end
    end
    report.detail = sum
  end
  -- 7. Lights.
  local nlights = 0
  if want_lights then
    for _, R in ipairs(Rs) do
      local rec = RECIPES[R.z.archetype]
      local h = rec.light_h and rec.light_h(R) or (R.h - 1)
      local spacing = (R.A.open_air and 7) or ((R.w * R.d > 500) and 8 or 6.5)
      local floor_y = R.y
      if R.poly then
        -- a traced zone: a grid per convex piece of its plan, about every 6 m, each light reaching the floor halfway
        -- to its neighbours (a tall traced room is lit to its floor, not only under its lights)
        local sp = R.A.open_air and spacing or 6
        local reach = 1.3 * math.sqrt(h * h + sp * sp / 2)
        for k, piece in ipairs((R.room and R.room.pieces) or { ccw(R.poly) }) do
          local inset = I.inset_convex(piece, 1.5)
          if inset then
            local ls = SP.plan_lights(k == 1 and R.name or (R.name .. "_" .. k), inset, floor_y, h, sp, nil)
            for _, l in ipairs(ls) do if (l:get("size_range") or 0) < reach then l:set("size_range", q2(reach)) end end
            nlights = nlights + #ls
          end
        end
      else
        local poly = (R.room and R.room.outline) or R.outline or rect_poly(R.x0, R.z0, R.x1, R.z1)
        local inset = I.inset_convex(poly, 1.5) or poly
        -- the lights go in after the turn, on the zone's world plan
        if R.rot ~= 0 then
          local w = {}
          for i, q in ipairs(inset) do local x, zz = R.W(q[1], q[2]) w[i] = { x, zz } end
          inset = w
        end
        local lights = SP.plan_lights(R.name, inset, floor_y, h, spacing, nil)
        nlights = nlights + #lights
      end
    end
    for _, P in ipairs(plan.links) do
      if P.ok and P.mode == "classic" and not open_air then nlights = nlights + #link_lights(P, gname, nil)
      elseif P.ok and P.mode == "free" and not open_air then nlights = nlights + #free_lights(P, gname, nil) end
    end
  end

  -- 8. Objectives: spawns, flags, hill, payload.
  local spawn_objs = {}
  local first_spawn = nil
  for _, o in ipairs(G.objectives or {}) do
    if o.kind == "spawns" then
      local R = byid[o.zone or ""]
      if R then
        local props = {}
        if o.team == "red" then props = { red_team = true, blue_team = false } elseif o.team == "blue" then props = { red_team = false, blue_team = true } end
        local spots = pick_spots(R, o.count or 1)
        if #spots < (o.count or 1) then report.warnings[#report.warnings + 1] = R.name .. ": room for " .. #spots .. " of " .. (o.count or 1) .. " spawns." end
        local list = {}
        for i, s in ipairs(spots) do
          local sx, sz = R.W(s[1], s[3])
          local sp = level.place{ kind = "respawn", name = gname .. "_" .. (o.id or "s") .. "_" .. i, pos = { sx, s[2] + kit.defaults.spawn_height, sz }, props = props,
            tags = { kit = "layout", ["kit.name"] = R.name } }
          -- face along a route out: toward the nearest port at the spawn's level, else the zone centre
          local tx, tz = R.cx, R.cz
          local bd = math.huge
          for _, p in ipairs(R.ports) do
            if math.abs(p.floor - s[2]) < 1 then
              local px, pz = p.pt[1], p.pt[2]
              local d = (px - s[1]) ^ 2 + (pz - s[3]) ^ 2
              if d < bd and d > 9 then bd, tx, tz = d, px, pz end
            end
          end
          local turn_to = math.abs(tx - s[1]) + math.abs(tz - s[3]) > 0.1
          tx, tz = R.W(tx, tz)
          if turn_to then sp:look_at(tx, s[2] + kit.defaults.spawn_height, tz) end
          list[#list + 1] = sp
          first_spawn = first_spawn or { sx, s[2], sz, tx, tz, turn_to }
        end
        spawn_objs[o.id or ("s" .. #spawn_objs)] = list
        report.spawns = report.spawns + #list
        report.objectives[#report.objectives + 1] = { kind = "spawns", id = o.id, zone = o.zone, team = o.team, count = #list }
      end
    end
  end
  if first_spawn then
    local ps = level.player_start{ pos = { first_spawn[1], first_spawn[2] + kit.defaults.spawn_height, first_spawn[3] }, yaw = 0 }
    if first_spawn[6] then
      ps:look_at(first_spawn[4], first_spawn[2] + kit.defaults.spawn_height, first_spawn[5])
    end
  end
  for _, o in ipairs(G.objectives or {}) do
    local R = byid[o.zone or ""]
    local s = R and (R.objective_spot or { R.cx, R.y, R.cz })
    if s then
      local x, z = R.W(s[1], s[3])
      s = { x, s[2], z }
    end
    if o.kind == "flag" and R then
      local it = level.place{ kind = "item", class = o.team == "red" and "flag_red" or "flag_blue", name = gname .. "_flag_" .. o.team,
        pos = { s[1], s[2] + kit.defaults.item_height, s[3] }, tags = { kit = "layout", ["kit.name"] = R.name } }
      report.objectives[#report.objectives + 1] = { kind = "flag", team = o.team, zone = o.zone, uid = it.uid, pos = it.pos }
    elseif o.kind == "hill" and R then
      local radius = o.radius or 8
      local trig = level.place{ kind = "trigger", name = gname .. "_hill_zone", pos = { s[1], s[2] + 2, s[3] },
        props = { shape = "Sphere", sphere_radius = radius, activated_by = "Players" }, tags = { kit = "layout", ["kit.name"] = R.name } }
      local ev = level.place{ kind = "event", class = "Capture_Point_Handler", name = gname .. "_hill", pos = { s[1], s[2] + 1, s[3] },
        props = { point_name = "Hill" }, links = { trig }, tags = { kit = "layout", ["kit.name"] = R.name } }
      report.objectives[#report.objectives + 1] = { kind = "hill", zone = o.zone, uid = ev.uid, trigger = trig.uid, pos = ev.pos, radius = radius }
    end
  end
  if cart and cart_pts and #cart_pts >= 2 then
    level.alpine:set("legacy_movers", false)
    local start = cart_pts[1]
    local body = level.brush{ shape = "box", pos = { start[1], start[2] + 0.75, start[3] }, size = { 2, 1.2, 3 }, detail = true,
      textures = { floor = "mtl_grittymetal.tga", wall = "mtl_hazardstripe01.tga", ceiling = "mtl_grittymetal.tga" },
      tags = { kit = "layout", ["kit.name"] = gname .. "_cart" } }
    local mover = level.create_mover{ name = gname .. "_cart", brushes = { body }, props = { movement_type = "One Way" }, tags = { kit = "layout", ["kit.name"] = gname .. "_cart" } }
    local kfs = { mover.start_keyframe }
    for i = 2, #cart_pts do
      local p, q = cart_pts[i - 1], cart_pts[i]
      local dist = math.sqrt((q[1] - p[1]) ^ 2 + (q[2] - p[2]) ^ 2 + (q[3] - p[3]) ^ 2)
      local tt = math.max(1, dist / 1.5)
      kfs[i - 1]:set_many{ travel_time_to_next = tt, return_travel = tt }
      kfs[i] = mover:add_keyframe{ pos = { q[1], q[2] + 0.75, q[3] }, props = { travel_time_to_next = 1, return_travel = 1 } }
    end
    local handler = level.place{ kind = "event", class = "Payload_Handler", name = gname .. "_payload", pos = { start[1], start[2] + 2, start[3] },
      props = { attacking_team = (cart.attacking_team == "red") and "Red" or "Blue", push_radius = 5 }, links = { mover }, tags = { kit = "layout", ["kit.name"] = gname .. "_cart" } }
    report.objectives[#report.objectives + 1] = { kind = "cart", uid = handler.uid, keyframes = #kfs }
    -- Checkpoints on the yard keyframes, in order, each linking the spawns that are live while it is the target.
    for _, o in ipairs(G.objectives or {}) do
      if o.kind == "checkpoint" then
        local R = byid[o.zone or ""]
        if R and R.checkpoint_at and kfs[R.checkpoint_at] then
          local kf = kfs[R.checkpoint_at]
          local cp = level.place{ kind = "event", class = "Payload_Checkpoint", name = gname .. "_checkpoint" .. o.index, pos = { kf.pos.x, kf.pos.y + 1.5, kf.pos.z },
            props = { checkpoint_name = "Checkpoint " .. string.char(64 + o.index) }, tags = { kit = "layout", ["kit.name"] = R.name } }
          kf:set("triggered_event_uid", cp.uid)
          local linked = {}
          for _, sid in ipairs(o.spawns or {}) do for _, sp in ipairs(spawn_objs[sid] or {}) do linked[#linked + 1] = sp end end
          if #linked > 0 then cp:link_many(linked) end
          report.objectives[#report.objectives + 1] = { kind = "checkpoint", index = o.index, zone = o.zone, uid = cp.uid, spawns = #linked }
        else
          report.warnings[#report.warnings + 1] = "checkpoint " .. tostring(o.index) .. ": its zone " .. tostring(o.zone) .. " is not on the cart track."
        end
      end
    end
  end

  -- 9. Reports and the population hook. Every brush of a zone carries kit.zone = the zone's name, so kit.dress,
  -- kit.items and kit.spawn_points take that name (report.zones[i].name) like any kit room's.
  for _, R in ipairs(Rs) do
    tag_zone(R)
    -- the zone's world plan (chamfers included) and bounds; spots and the room record in world coordinates
    local outline = nil
    if R.poly then outline = ccw(R.poly)
    else
      outline = {}
      for i, q in ipairs(chamfered(R.x0, R.z0, R.x1, R.z1, R.c)) do local x, zz = R.W(q[1], q[2]) outline[i] = { x, zz } end
    end
    local bx0, bz0, bx1, bz1 = bbox(outline)
    if R.rot == 0 and not R.poly then bx0, bz0, bx1, bz1 = R.x0, R.z0, R.x1, R.z1 end
    local spots, room = R.spots, R.room
    if R.rot ~= 0 then
      spots = {}
      for i, s in ipairs(R.spots) do local x, zz = R.W(s[1], s[3]) spots[i] = { x, s[2], zz } end
      if type(R.room) == "table" then
        room = {}
        for k, v in pairs(R.room) do room[k] = v end
        local function wpoly_(p)
          if type(p) ~= "table" then return p end
          local o = {}
          for i, q in ipairs(p) do local x, zz = R.W(q[1], q[2]) o[i] = { x, zz } end
          return o
        end
        room.outline = wpoly_(R.room.outline)
        if type(R.room.pieces) == "table" then room.pieces = {} for i, pc in ipairs(R.room.pieces) do room.pieces[i] = wpoly_(pc) end end
        room.raised = wpoly_(R.room.raised)
        room.min, room.max = vec(bx0, R.room.min and R.room.min.y or R.y, bz0), vec(bx1, R.room.max and R.room.max.y or (R.y + R.h), bz1)
        local cx, cz = R.W(R.cx, R.cz)
        room.pos = vec(cx, R.y, cz)
        room.center = vec((bx0 + bx1) / 2, R.y + R.h / 2, (bz0 + bz1) / 2)
        room.size = vec(bx1 - bx0, R.h, bz1 - bz0)
        room.rot = R.rot
      end
    end
    local zr = { id = R.id, archetype = R.z.archetype, role = R.z.role, name = R.name, min = vec(bx0, R.y, bz0), max = vec(bx1, R.y + R.h, bz1),
      floor_y = R.y, levels = copy_list(R.z.levels), brushes = #R.brushes, spots = spots, dress = R.z.dress, sky = R.sky or false,
      ports = #R.ports, warnings = R.warnings, room = room, outline = outline, rot = R.rot, detail = R.detail_report }
    for _, w in ipairs(R.warnings) do report.warnings[#report.warnings + 1] = w end
    report.zones[#report.zones + 1] = zr
    if t.populate then t.populate(zr, R.z, G) end
  end
  for _, P in ipairs(plan.links) do
    local lr = { id = P.link.id, type = P.link.type, a = P.link.a, b = P.link.b, width = P.W, offset = P.c, mode = P.mode,
      a_side = P.side_a, b_side = P.side_b }
    if P.mode == "free" and P.pts then
      lr.path = {}
      for i, q in ipairs(P.pts) do lr.path[i] = { q[1], q[2] } end
      lr.length = P.length
    elseif P.mode == "join" then
      lr.width = P.span
    end
    report.links[#report.links + 1] = lr
  end
  report.lights = nlights
  report.area = layout_area(G, plan)
  return report
end

-- The internals the layout generators (kit_compose.lua, kit_compose_open.lua, kit_compose_payload.lua) compose with:
-- data tables, the archetype library, link geometry, the resolver, the checker and the zone helpers.
-- LAYOUT.generators[gametype] = function(G, rng, t) -> true once it has composed G (kit.layout requires one per gametype).
for k, v in pairs({
  DATA = DATA, BUDGET = BUDGET, THEMES = THEMES, POOL = POOL, LA = LA, LORDER = LORDER, SIDES = SIDES, OPP = OPP, DIRV = DIRV,
  new_rng = new_rng, clamp = clamp, q2 = q2, q4 = q4, even = even, copy_list = copy_list,
  rect_of = rect_of, zone_area = zone_area, has_level = has_level, intersect = intersect, subtract = subtract,
  wall_span = wall_span, wall_coord = wall_coord, ranges_of = ranges_of, widest = widest, std_height = std_height,
  heading_of = heading_of, road_frame = road_frame, stair_run = stair_run, ramp_run = ramp_run,
  link_height = link_height, link_frame = link_frame, link_ys = link_ys, gap_need = gap_need, port_width = port_width,
  resolve = resolve, check = check, layout_area = layout_area, floor_levels = floor_levels,
  route_ctx = route_ctx, route_link = route_link, route_drop = route_drop,
  size_class = size_class, levels_for = levels_for, finish_zone = finish_zone, spread_spawns = spread_spawns,
  -- zone frames, traced zones and plan geometry (world = R(rot) . local; poly outlines in world metres)
  FREE_TYPES = FREE_TYPES, norm_rot = norm_rot, zone_rot = zone_rot, same_rot = same_rot, to_world = to_world, to_local = to_local,
  yaw_of = yaw_of, ang_diff = ang_diff, angle_of = angle_of, local_centre = local_centre, centre_of = centre_of, facing_side = facing_side,
  zone_poly = zone_poly, poly_of = poly_of, poly_edge = poly_edge, poly_dist = poly_dist, poly_near = poly_near, box_gap = box_gap,
  bbox_box = bbox_box, seg_inside = seg_inside, edge_dist = edge_dist,
  inside = inside, area2 = area2, bbox = bbox, ccw = ccw, simple = simple, wall_point = wall_point, zone_band = zone_band,
  mitre = mitre, wants_free = wants_free,
  -- port geometry and the walk-graph analysis the checker uses
  WALK = WALK, CART_LINK = CART_LINK, port_bands = port_bands, port_offsets = port_offsets,
  index_zones = index_zones, adjacency = adjacency, components = components, disjoint_routes = disjoint_routes,
  articulation = articulation, walk_dist = walk_dist,
}) do LAYOUT[k] = v end
I.layout = LAYOUT

end

return install
