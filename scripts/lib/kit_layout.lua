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
}

-- Size budgets: playable floor and floor per spawn are the §a.1 rows; zones and hubs are §c.1-1.
-- XS is §(g) open question 2: 2-4 players play in 1,500-3,000 m2.
local BUDGET = {
  dm = {
    XS = { area = { 1500, 3000 }, per_spawn = { 250, 500 }, zones = { 5, 7 }, hubs = { 1, 1 }, grid = { 3, 2 } },
    S = { area = { 3300, 4900 }, per_spawn = { 430, 610 }, zones = { 5, 9 }, hubs = { 1, 1 }, grid = { 3, 3 } },
    M = { area = { 4400, 6400 }, per_spawn = { 300, 510 }, zones = { 8, 14 }, hubs = { 1, 1 }, grid = { 4, 3 } },
    L = { area = { 4600, 8000 }, per_spawn = { 165, 265 }, zones = { 12, 20 }, hubs = { 2, 3 }, grid = { 5, 4 } },
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
local LINK_TYPES = { corridor = true, doorway = true, stair = true, ramp = true, drop = true, bridge = true, window = true, lift = true, road = true }
local WALK = { corridor = true, doorway = true, stair = true, ramp = true, drop = true, bridge = true, lift = true, road = true }
local CART_LINK = { road = true, bridge = true, doorway = true, corridor = true } -- links the payload cart can run along

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

local function rect_of(z)
  local w, d = z.size[1], z.size[2]
  return z.pos[1] - w / 2, z.pos[3] - d / 2, z.pos[1] + w / 2, z.pos[3] + d / 2
end

local function zone_area(z) return z.size[1] * z.size[2] end

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
  local mid = (up == "+x" or up == "-x") and z.pos[1] or z.pos[3]
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
  summary = "A room around a 4 m pit with a plank bridge across it and stairs down on both sides.",
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

-- The floor a link meets on zone z (joined to other) when it gives no a_y / b_y: the zone's floor, or a graded
-- road's top floor (pos.y + rise) at its exit wall.
local function end_floor(z, other)
  if z.archetype == "road" and other and type(z.params) == "table" and (z.params.rise or 0) ~= 0 then
    local f = link_frame(z, other)
    if f and f.side_a == heading_of(z) then return z.pos[2] + z.params.rise end
  end
  return z.pos[2]
end

-- The port floors of a link on its two zones: a_y / b_y, else end_floor.
local function link_ys(l, za, zb)
  return l.a_y or end_floor(za, zb), l.b_y or end_floor(zb, za)
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

-- Resolves every link: frame, centre along the walls, ports. With rng, missing offsets are chosen at random in
-- the free span (the generator); without, the centre of the widest free span (deterministic builds of edited
-- graphs). Returns plan = {zones, links[i] = {link, za, zb, f, c, W, ba, bb}, ports[zone_id] = {...}, errors}.
local function resolve(graph, rng, keep)
  local plan = { zones = {}, links = {}, ports = {}, errors = {} }
  for _, z in ipairs(graph.zones or {}) do
    if type(z) == "table" and z.id ~= nil then plan.zones[z.id] = z plan.ports[z.id] = {} end
  end
  local used = {}
  local function used_of(id, side)
    local k = id .. side
    used[k] = used[k] or {}
    return used[k]
  end
  local function err(l, rule, msg)
    plan.errors[#plan.errors + 1] = { severity = "error", rule = rule, link = l.id, message = "link " .. tostring(l.id) .. ": " .. msg }
  end
  for i, l in ipairs(graph.links or {}) do
    local za, zb = plan.zones[l.a], plan.zones[l.b]
    local P = { link = l, za = za, zb = zb, index = i }
    plan.links[i] = P
    if za and zb and za ~= zb then
      local f, why = link_frame(za, zb)
      if not f then
        err(l, "geometry", "zones " .. za.id .. " and " .. zb.id .. " cannot be joined: " .. why .. ".")
      else
        P.f = f
        local W = port_width(l)
        P.W = W
        local need = gap_need(l, za, zb)
        if f.gap < need - EPS then
          err(l, "geometry", "the " .. fmt(f.gap) .. " m gap between " .. za.id .. " and " .. zb.id .. " is too short for a " .. l.type ..
            " (needs " .. fmt(need) .. " m).")
        end
        local oa, ob = port_offsets(l, za, zb)
        local ra = ranges_of(za, f.side_a, oa)
        local rb = nil
        if l.type == "drop" then rb = ranges_of(zb, f.side_b, ob) else rb = ranges_of(zb, f.side_b, ob) end
        local free = intersect(intersect(ra, rb), { { f.lo, f.hi } })
        P.ba, P.bb = port_bands(l, za, zb, graph)
        for _, s in ipairs(used_of(za.id, f.side_a)) do
          if s.y0 < P.ba[2] + 0.5 and s.y1 > P.ba[1] - 0.5 then free = subtract(free, s.lo - 1.5, s.hi + 1.5) end
        end
        for _, s in ipairs(used_of(zb.id, f.side_b)) do
          if s.y0 < P.bb[2] + 0.5 and s.y1 > P.bb[1] - 0.5 then free = subtract(free, s.lo - 1.5, s.hi + 1.5) end
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
          if l.offset == nil or rng then
            err(l, "geometry", "no free " .. fmt(W) .. " m span on both walls (" .. za.id .. " " .. f.side_a .. " at level +" .. fmt(oa) ..
              " m, " .. zb.id .. " " .. f.side_b .. " at level +" .. fmt(ob) .. " m); move the zones so their walls overlap more, or change the levels.")
          end
        else
          P.c = c
          if keep then l.offset = c end
          local ua, ub = used_of(za.id, f.side_a), used_of(zb.id, f.side_b)
          ua[#ua + 1] = { lo = c - W / 2, hi = c + W / 2, y0 = P.ba[1], y1 = P.ba[2] }
          ub[#ub + 1] = { lo = c - W / 2, hi = c + W / 2, y0 = P.bb[1], y1 = P.bb[2] }
          local fya, fyb = link_ys(l, za, zb)
          local pa = { side = f.side_a, lo = c - W / 2, hi = c + W / 2, y0 = P.ba[1], y1 = P.ba[2], link = l, wall = f.wa, floor = fya }
          local pb = { side = f.side_b, lo = c - W / 2, hi = c + W / 2, y0 = P.bb[1], y1 = P.bb[2], link = l, wall = f.wb, floor = fyb }
          if l.type == "drop" then pb.drop_to = fyb end
          table.insert(plan.ports[za.id], pa)
          table.insert(plan.ports[zb.id], pb)
          P.pa, P.pb = pa, pb
        end
      end
    end
  end
  return plan
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
      if P.f and P.W and WALK[P.link.type] then a = a + P.f.gap * (P.link.type == "bridge" and P.W - 1 or P.W) end
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
      if A == nil then violation(V, "error", "schema", "zone " .. z.id .. ": unknown archetype '" .. tostring(z.archetype) .. "' (kit.layout_archetypes{} lists them).", z.id)
      elseif type(z.pos) ~= "table" or type(z.size) ~= "table" or type(z.pos[1]) ~= "number" or type(z.size[2]) ~= "number" then
        violation(V, "error", "schema", "zone " .. z.id .. " needs pos = {x, y, z} and size = {w, d}.", z.id)
      else
        local mn = A.dims and A.dims(z, A.min) or A.min
        if z.size[1] < mn[1] - EPS or z.size[2] < mn[2] - EPS then
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
        "' (corridor, doorway, stair, ramp, drop, bridge, window, lift, road).", nil, l.id) end
      if not ids[l.a or ""] or not ids[l.b or ""] then violation(V, "error", "schema", "link " .. l.id .. " joins unknown zones (" ..
        tostring(l.a) .. ", " .. tostring(l.b) .. ").", nil, l.id) end
    end
  end
  for _, v in ipairs(V) do if v.severity == "error" then return V, nil end end

  -- Geometry: plans apart, links resolvable, levels valid.
  local zs = graph.zones
  for i = 1, #zs do
    for j = i + 1, #zs do
      local a, b = zs[i], zs[j]
      local ax0, az0, ax1, az1 = rect_of(a)
      local bx0, bz0, bx1, bz1 = rect_of(b)
      local m = (LA[a.archetype].open_air and LA[b.archetype].open_air) and 2 or DATA.min_wall
      if ax0 < bx1 + m - EPS and bx0 < ax1 + m - EPS and az0 < bz1 + m - EPS and bz0 < az1 + m - EPS then
        violation(V, "error", "geometry", "zones " .. a.id .. " and " .. b.id .. " overlap or stand closer than " .. fmt(m) .. " m.", a.id)
      end
    end
  end
  for _, l in ipairs(graph.links) do
    local za, zb = ids[l.a], ids[l.b]
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
  local plan = resolve(graph, nil, false)
  for _, e in ipairs(plan.errors) do V[#V + 1] = e end
  for _, v in ipairs(V) do if v.severity == "error" then return V, plan end end

  -- Design rules (c.1-1).
  local idx = index_zones(graph)
  local adj = adjacency(graph, idx)
  local n = #zs
  local area = layout_area(graph, plan)
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
      if o.archetype == z.archetype and math.abs((2 * cx - z.pos[1]) - o.pos[1]) < 1.5 and math.abs(z.pos[3] - o.pos[3]) < 1.5 then
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
      -- The cart's way between two consecutive spine zones: a road, bridge, doorway or corridor joining them; its
      -- walls give the side the cart leaves one zone by and enters the next by (any heading).
      local sides = {}
      for i = 1, #spine do sides[i] = {} end
      for i = 1, #spine - 1 do
        local a, b = spine[i], spine[i + 1]
        if ids[a] and ids[b] then
          local found = nil
          for _, P in ipairs(plan.links) do
            local l = P.link
            if P.f and P.c and CART_LINK[l.type] and ((l.a == a and l.b == b) or (l.a == b and l.b == a)) then found = P break end
          end
          if not found then
            violation(V, "rule", "payload", "the cart cannot go from " .. a .. " to " .. b .. ": consecutive cart zones need a road, bridge, doorway or corridor between them.", a)
          else
            local fwd = found.link.a == a
            sides[i].exit = fwd and found.f.side_a or found.f.side_b
            sides[i + 1].entry = fwd and found.f.side_b or found.f.side_a
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
              violation(V, "rule", "payload", "the cart " .. e[2] .. " road " .. id .. " by its " .. sd[e[1]] .. " side, but a road heading " .. F.heading .. " is " ..
                (e[1] == "entry" and "entered" or "left") .. " by its " .. e[3] .. " end: set the road's params.heading to the way the cart travels.", id)
            end
          end
          if prev and (prev.heading ~= F.heading or math.abs(prev.centre - F.centre) > 3) then turned = true end
          prev = { heading = F.heading, centre = F.centre }
        elseif z then
          -- a zone the cart crosses at an angle turns the track
          if sd.entry and sd.exit and sd.exit ~= OPP[sd.entry] then turned = true end
          if z.archetype == "finale" and sd.entry and sd.entry ~= OPP[heading_of(z)] then
            violation(V, "rule", "payload", "the cart enters the finale " .. id .. " by its " .. sd.entry .. " side, but the finale's cart ramp faces " .. OPP[heading_of(z)] ..
              ": set its params.heading to " .. OPP[sd.entry] .. ".", id)
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

---@kit kit.check_layout{graph} → violation[] — Checks a layout graph against the c.1-1 rules before anything is built: schema and geometry (zones apart, every link fits its walls, levels and gaps), degree >= 2 except reward nooks, no articulation zone with >= 10% of the area behind it, cycle rank per 1,000 m2, 2 node-disjoint routes between hubs and objectives, 3+ floor levels 4.5-10 m apart joined by 2+ kinds of vertical link, archetype variety, mirror symmetry only for team modes, and the CTF / KOTH / payload / open-air rules and the size budget. A link without a_y / b_y meets each zone at its floor (a graded road's top floor at its exit end). Payload tracks run on any heading: a road zone's params.heading is the way the cart travels through it (entered on the opposite end), the cart turns inside the zones between roads, consecutive cart zones need a road, bridge, doorway or corridor between them, and a bridge outside open-air layouts spans a chasm between two floors at one level. A hand-made zone without levels, height or chamfer gets its archetype's. Each violation is {severity ("error": cannot build; "rule": a design rule), rule, message, zone?, link?}.
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

-- Sizes a zone for an area: aspect ratio from the stream, clamped to the archetype, even metres.
local function size_zone(A, area, rng, elongate)
  local ar = rng.range(0.8, 1.25)
  if elongate then ar = elongate end
  local w = math.sqrt(area * ar)
  local d = area / w
  w = clamp(even(w), A.min[1], A.max[1])
  d = clamp(even(d), A.min[2], A.max[2])
  return { w, d }
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

-- Picks an archetype from choices, at most repeat_max uses and never the same as the listed neighbours.
local function pick_arch(rng, choices, used, avoid)
  local cand = {}
  for _, a in ipairs(choices) do
    local ok = (used[a] or 0) < DATA.repeat_max or LA[a].repeatable
    for _, b in ipairs(avoid or {}) do if b == a then ok = false end end
    if ok then cand[#cand + 1] = a end
  end
  if #cand == 0 then
    for _, a in ipairs(choices) do if (used[a] or 0) < DATA.repeat_max or LA[a].repeatable then cand[#cand + 1] = a end end
  end
  if #cand == 0 then cand = copy_list(choices) end
  local a = rng.pick(cand)
  used[a] = (used[a] or 0) + 1
  return a
end

local function widest(r) local w = 0 for _, s in ipairs(r) do w = math.max(w, s[2] - s[1]) end return w end

-- The best pair of port offsets for an edge between two zones on their facing walls: the smallest climb
-- (0 or one step), among the levels whose walls take ports. Returns oa, ob, diff (b - a) or nil.
local function level_pair(za, zb, side_a, side_b, step, rng, prefer_climb, slack)
  local best, bd = nil, math.huge
  local opts = {}
  for _, oa in ipairs(za.levels) do
    for _, ob in ipairs(zb.levels) do
      if widest(ranges_of(za, side_a, oa)) >= 4.5 and widest(ranges_of(zb, side_b, ob)) >= 4.5 then
        local diff = (zb.pos[2] + ob) - (za.pos[2] + oa)
        if math.abs(diff) <= step + (slack or 0.5) + EPS then opts[#opts + 1] = { oa, ob, diff } end
      end
    end
  end
  if #opts == 0 then return nil end
  for _, o in ipairs(opts) do
    local score = math.abs(o[3]) + (rng and rng.float() * 0.01 or 0)
    if prefer_climb then score = -score end
    if score < bd then best, bd = o, score end
  end
  return best[1], best[2], best[3]
end

-- A grid layout: cells -> zones, candidate edges -> links, then pruning, link kinds, gaps and positions.
-- cfg = {cols, rows, cells = {[key] = {c, r, role, choices, level, weight, team, mirror_of, palette, lane, params}},
--        edges = {{ka, kb, required?, parallel?}}, target_area, prune, kinds, open_air, mirror_cols}
local function grid_layout(G, rng, cfg)
  local step = G.step
  local cells = cfg.cells
  local keys = {}
  for r = 0, cfg.rows - 1 do for c = 0, cfg.cols - 1 do local k = c .. "," .. r if cells[k] then keys[#keys + 1] = k end end end
  -- 1. Archetypes and sizes (mirrored cells copy their twin afterwards).
  local used = {}
  local wsum = 0
  for _, k in ipairs(keys) do wsum = wsum + (cells[k].weight or 1) end
  local per = cfg.target_area * 0.84 / wsum
  local zones = {}
  -- The neighbours each cell must reach: the side they lie on and their level.
  local nbrs = {}
  for _, e in ipairs(cfg.edges) do
    local ca, cb = cells[e[1]], cells[e[2]]
    if ca and cb then
      local sa = (cb.c > ca.c and "+x") or (cb.c < ca.c and "-x") or (cb.r > ca.r and "+z") or "-z"
      nbrs[e[1]] = nbrs[e[1]] or {}
      nbrs[e[2]] = nbrs[e[2]] or {}
      table.insert(nbrs[e[1]], { side = sa, level = cb.level or 0 })
      table.insert(nbrs[e[2]], { side = OPP[sa], level = ca.level or 0 })
    end
  end
  local function fits(z, k)
    for _, n in ipairs(nbrs[k] or {}) do
      local ok = false
      for _, oa in ipairs(z.levels) do
        if math.abs(z.pos[2] + oa - n.level * step) <= step + (cfg.open_air and 1.5 or 0.5) + EPS and widest(ranges_of(z, n.side, oa)) >= 5 then ok = true end
      end
      if not ok then return false end
    end
    return true
  end
  for _, k in ipairs(keys) do
    local cell = cells[k]
    if not cell.mirror_of then
      local avoid = {}
      for _, dk in ipairs({ (cell.c - 1) .. "," .. cell.r, cell.c .. "," .. (cell.r - 1), (cell.c + 1) .. "," .. cell.r, cell.c .. "," .. (cell.r + 1) }) do
        if zones[dk] then avoid[#avoid + 1] = zones[dk].archetype end
      end
      -- Floors sit on their level or half a metre above it (a step at the door): more floor-height classes.
      local off = (cell.offset ~= nil) and cell.offset or ((cell.role == "hub" or cell.role == "hill" or cell.role == "base" or cell.role == "span"
        or cell.role == "checkpoint" or cell.auto_level) and 0 or (cfg.open_air and rng.pick({ 0, 0.5, 1, 1.5 }) or (rng.chance(0.45) and 0.5 or 0)))
      -- The archetype (and a split's raised side) that reaches every neighbour, in random order of the choices.
      local order = {}
      if cell.archetype then order[1] = cell.archetype
      else
        local first = pick_arch(rng, cell.choices, used, avoid)
        used[first] = used[first] - 1
        order[1] = first
        local rest = {}
        for _, a in ipairs(cell.choices) do if a ~= first and ((used[a] or 0) < DATA.repeat_max or LA[a].repeatable) then rest[#rest + 1] = a end end
        rng.shuffle(rest)
        for _, a in ipairs(rest) do order[#order + 1] = a end
      end
      local z = nil
      local found = false
      for _, a in ipairs(order) do
        local A = LA[a]
        local splits = { (cell.params and cell.params.split) or "+x" }
        if A.ranges == nil or not (a == "split" or a == "tower" or a == "terrace") then splits = { splits[1] }
        elseif not cell.fixed_split then for _, d in ipairs(SIDES) do if d ~= splits[1] then splits[#splits + 1] = d end end end
        for _, sp in ipairs(splits) do
          local cand = { archetype = a, role = cell.role, cell = { cell.c, cell.r }, team = cell.team, lane = cell.lane, params = {}, palette = cell.palette }
          if cell.params then for pk, pv in pairs(cell.params) do cand.params[pk] = pv end end
          cand.params.split = sp
          cand.size = cell.size and { math.max(cell.size[1], A.min[1]), math.max(cell.size[2], A.min[2]) } or size_zone(A, per * (cell.weight or 1), rng, cell.elongate)
          cand.pos = { 0, (cell.level or 0) * step + off, 0 }
          finish_zone(cand, step)
          if z == nil then z = cand end
          if fits(cand, k) then z, found = cand, true break end
        end
        if found then break end
      end
      if not found and not cell.archetype then
        local uni = cfg.open_air and { "isle", "deck", "garden", "gantry", "outpost", "plaza", "mast" } or { "hall", "colonnade", "storage", "courtyard", "pit" }
        table.sort(uni, function(p, q) local up, uq = used[p] or 0, used[q] or 0 if up ~= uq then return up < uq end return p < q end)
        for _, a in ipairs(uni) do
          local A = LA[a]
          local cand = { archetype = a, role = cell.role, cell = { cell.c, cell.r }, team = cell.team, lane = cell.lane, params = {}, palette = cell.palette }
          if cell.params then for pk, pv in pairs(cell.params) do cand.params[pk] = pv end end
          cand.size = { clamp(z.size[1], A.min[1], A.max[1]), clamp(z.size[2], A.min[2], A.max[2]) }
          cand.pos = { 0, z.pos[2], 0 }
          finish_zone(cand, step)
          if fits(cand, k) then z = cand break end
        end
      end
      used[z.archetype] = (used[z.archetype] or 0) + 1
      zones[k] = z
    end
  end
  for _, k in ipairs(keys) do
    local cell = cells[k]
    if cell.mirror_of then
      local t = zones[cell.mirror_of]
      local z = { archetype = t.archetype, role = cell.role or t.role, cell = { cell.c, cell.r }, team = cell.team, lane = t.lane,
        size = { t.size[1], t.size[2] }, pos = { 0, t.pos[2], 0 }, params = {}, palette = cell.palette, twin = cell.mirror_of }
      for pk, pv in pairs(t.params or {}) do z.params[pk] = pv end
      if z.params.split then z.params.split = (z.params.split == "+x" and "-x") or (z.params.split == "-x" and "+x") or z.params.split end
      zones[k] = z
    end
  end
  for _, k in ipairs(keys) do finish_zone(zones[k], step) end
  -- Hubs on "auto" take the base level that reaches the most neighbours within one step.
  for _, k in ipairs(keys) do
    local cell, z = cells[k], zones[k]
    if cell.auto_level then
      local best, bn = 0, -1
      for b = cfg.min_level or 0, cfg.max_level or 2 do
        local n = 0
        for _, dk in ipairs({ (cell.c - 1) .. "," .. cell.r, cell.c .. "," .. (cell.r - 1), (cell.c + 1) .. "," .. cell.r, cell.c .. "," .. (cell.r + 1) }) do
          local o = zones[dk]
          if o then
            local ok = false
            for _, oa in ipairs(z.levels) do for _, ob in ipairs(o.levels) do
              if math.abs(b * step + oa - (o.pos[2] + ob)) <= step + EPS then ok = true end
            end end
            if ok then n = n + 1 end
          end
        end
        if n > bn then best, bn = b, n end
      end
      z.pos[2] = best * step
    end
  end

  -- 2. Area fit: one rescale of the free zones toward the target.
  local total = 0
  for _, k in ipairs(keys) do total = total + zone_area(zones[k]) end
  local s = math.sqrt(cfg.target_area * 0.84 / total)
  if math.abs(s - 1) > 0.05 then
    for _, k in ipairs(keys) do
      local z = zones[k]
      local A = LA[z.archetype]
      if not cells[k].size then
        z.size = { clamp(even(z.size[1] * s), A.min[1], A.max[1]), clamp(even(z.size[2] * s), A.min[2], A.max[2]) }
      end
      finish_zone(z, step)
    end
    for _, k in ipairs(keys) do
      local cell = cells[k]
      if cell.mirror_of then zones[k].size = { zones[cell.mirror_of].size[1], zones[cell.mirror_of].size[2] } finish_zone(zones[k], step) end
    end
  end

  -- 3. Column widths and row depths; provisional positions (the gaps are sized in step 7).
  local function place(colgap, rowgap)
    local cw, rd = {}, {}
    for c = 0, cfg.cols - 1 do cw[c] = 0 end
    for r = 0, cfg.rows - 1 do rd[r] = 0 end
    for _, k in ipairs(keys) do
      local z, cell = zones[k], cells[k]
      cw[cell.c] = math.max(cw[cell.c], z.size[1])
      rd[cell.r] = math.max(rd[cell.r], z.size[2])
    end
    if cfg.mirror_cols then
      for c = 0, cfg.cols - 1 do local m = cfg.cols - 1 - c local v = math.max(cw[c], cw[m]) cw[c], cw[m] = v, v end
    end
    local X, Z = {}, {}
    local x = 0
    for c = 0, cfg.cols - 1 do
      if cw[c] == 0 then cw[c] = 12 end
      X[c] = x + cw[c] / 2
      x = x + cw[c] + (colgap[c] or 0)
    end
    local zz = 0
    for r = 0, cfg.rows - 1 do
      if rd[r] == 0 then rd[r] = 12 end
      Z[r] = zz + rd[r] / 2
      zz = zz + rd[r] + (rowgap[r] or 0)
    end
    local ox, oz = x - (colgap[cfg.cols - 1] or 0), zz - (rowgap[cfg.rows - 1] or 0)
    for _, k in ipairs(keys) do
      local z, cell = zones[k], cells[k]
      z.pos[1] = X[cell.c] - ox / 2 + (cell.jx or 0)
      z.pos[3] = Z[cell.r] - oz / 2 + (cell.jz or 0)
      z.home = { q2(X[cell.c] - ox / 2), q2(Z[cell.r] - oz / 2 + (cell.align_z or 0)) }
    end
    return cw, rd
  end

  -- Jitter inside the cell slack (mirrored twins mirror it), kept small so walls stay overlapping.
  local cw0, rd0 = place({}, {})
  for _, k in ipairs(keys) do
    local cell, z = cells[k], zones[k]
    if not cell.mirror_of and not cell.fixed then
      local sx = math.min(cell.jx_range or 3, (cw0[cell.c] - z.size[1]) / 2)
      local sz = math.min(3, (rd0[cell.r] - z.size[2]) / 2)
      cell.jx = (cell.centre_x or cfg.mirror_cols and cell.c == (cfg.cols - 1) / 2) and 0 or q2(rng.range(-sx, sx))
      cell.jz = q2(rng.range(-sz, sz))
      if cell.align then
        -- pinned to one end of its row band
        local slack = (rd0[cell.r] - z.size[2]) / 2
        cell.align_z = q2((cell.align == "south") and -slack or slack)
        cell.jz = cell.align_z
      end
    end
  end
  for _, k in ipairs(keys) do
    local cell = cells[k]
    if cell.mirror_of then cell.jx = -(cells[cell.mirror_of].jx or 0) cell.jz = cells[cell.mirror_of].jz or 0 end
  end

  -- 4. Candidate edges with their port levels.
  local function compute_edges()
    local out = {}
    for ci, e in ipairs(cfg.edges) do
      local za, zb = zones[e[1]], zones[e[2]]
      if za and zb then
        local ca, cb = cells[e[1]], cells[e[2]]
        local side_a = (cb.c > ca.c and "+x") or (cb.c < ca.c and "-x") or (cb.r > ca.r and "+z") or "-z"
        local oa, ob, diff = level_pair(za, zb, side_a, OPP[side_a], step, rng, e.climb, cfg.open_air and 1.5 or 0.5)
        if oa ~= nil then
          out[#out + 1] = { ka = e[1], kb = e[2], za = za, zb = zb, oa = oa, ob = ob, diff = diff, required = e.required,
            parallel = e.parallel, side = side_a, twin = e.twin, kind = e.kind, id = #out + 1, cfg = ci }
        end
      end
    end
    return out
  end
  local edges = compute_edges()
  -- A zone that reaches fewer than two neighbours swaps to an archetype with ports on every wall.
  do
    local twinned = {}
    for _, k in ipairs(keys) do if cells[k].mirror_of then twinned[cells[k].mirror_of] = true end end
    local deg = {}
    for _, k in ipairs(keys) do deg[k] = 0 end
    for _, e in ipairs(edges) do deg[e.ka] = deg[e.ka] + 1 deg[e.kb] = deg[e.kb] + 1 end
    local changed = false
    local fallback = cfg.open_air and { "isle", "deck", "garden", "gantry", "outpost", "plaza", "mast" } or { "hall", "colonnade", "storage", "courtyard", "pit", "tunnel" }
    for _, k in ipairs(keys) do
      local cell, z = cells[k], zones[k]
      if deg[k] < 2 and cell.role ~= "nook" and not cell.archetype and not cell.mirror_of and not twinned[k] then
        local old, old_size = z.archetype, { z.size[1], z.size[2] }
        for _, a in ipairs(fallback) do
          if a ~= old and ((used[a] or 0) < DATA.repeat_max) then
            local A = LA[a]
            z.archetype = a
            z.size = { clamp(old_size[1], A.min[1], A.max[1]), clamp(old_size[2], A.min[2], A.max[2]) }
            finish_zone(z, step)
            if fits(z, k) then
              used[old] = math.max(0, (used[old] or 1) - 1)
              used[a] = (used[a] or 0) + 1
              cell.jx, cell.jz = 0, 0
              changed = true
              break
            end
            z.archetype, z.size = old, old_size
            finish_zone(z, step)
          end
        end
      end
    end
    if changed then edges = compute_edges() end
  end

  -- 5. Pruning: drop optional edges at random while degree, 2-connectivity and the loop target hold.
  local function graph_ok(es)
    local deg, adjk = {}, {}
    for _, k in ipairs(keys) do deg[k] = 0 adjk[k] = {} end
    for _, e in ipairs(es) do
      deg[e.ka] = deg[e.ka] + 1 deg[e.kb] = deg[e.kb] + 1
      adjk[e.ka][#adjk[e.ka] + 1] = e.kb adjk[e.kb][#adjk[e.kb] + 1] = e.ka
    end
    for _, k in ipairs(keys) do
      if cells[k].role ~= "nook" and deg[k] < 2 then return false end
      if deg[k] < 1 then return false end
    end
    -- connected after removing any one non-nook zone (no articulation zones beyond nooks)
    for _, skip in ipairs(keys) do
      local seen, stack, cnt = { [skip] = true }, {}, 0
      local start = nil
      for _, k in ipairs(keys) do if k ~= skip and cells[k].role ~= "nook" then start = k break end end
      if start then
        stack[1] = start seen[start] = true
        while #stack > 0 do
          local u = table.remove(stack)
          cnt = cnt + 1
          for _, v in ipairs(adjk[u]) do if not seen[v] then seen[v] = true stack[#stack + 1] = v end end
        end
        local need = 0
        for _, k in ipairs(keys) do if k ~= skip and (cells[k].role ~= "nook") then need = need + 1 end end
        local got = 0
        for _, k in ipairs(keys) do if k ~= skip and cells[k].role ~= "nook" and seen[k] then got = got + 1 end end
        if got < need then return false end
      end
    end
    local internal = 0
    for _, k in ipairs(keys) do internal = internal + (zones[k].loops or 0) end
    if #es - #keys + 1 + internal < cfg.min_rank then return false end
    -- the zones' own loops are estimates: the links alone keep at least 60% of the target
    if #es - #keys + 1 < math.ceil(cfg.min_rank * 0.6) then return false end
    return true
  end
  if cfg.prune and cfg.prune > 0 then
    local order = {}
    for i, e in ipairs(edges) do if not e.required and not e.twin then order[#order + 1] = i end end
    rng.shuffle(order)
    local removed = {}
    for _, i in ipairs(order) do
      if rng.chance(cfg.prune) then
        local trial = {}
        for j, e in ipairs(edges) do
          if j ~= i and not removed[j] and not (removed[e.twin_of or -1]) and not (e.twin_of == i) then trial[#trial + 1] = e end
        end
        if graph_ok(trial) then
          removed[i] = true
          for j, e in ipairs(edges) do if e.twin_of == i then removed[j] = true end end
        end
      end
    end
    local kept = {}
    for j, e in ipairs(edges) do if not removed[j] then kept[#kept + 1] = e end end
    edges = kept
  end

  -- 6. Link kinds: climbs alternate kinds per level pair (stair first, then ramp / drop / lift / bridge).
  local per_pair = {}
  local links = {}
  for _, e in ipairs(edges) do
    local n_par = e.parallel or 1
    for p = 1, n_par do
      local l = { a = e.ka, b = e.kb, a_y = e.za.pos[2] + e.oa, b_y = e.zb.pos[2] + e.ob, edge = e, par = p }
      if e.kind then l.type = e.kind
      elseif math.abs(e.diff) <= 0.5 + EPS then
        l.type = cfg.open_air and "bridge" or "corridor"
      else
        local lo, hi = math.min(l.a_y, l.b_y), math.max(l.a_y, l.b_y)
        local pk = lo .. "/" .. hi
        per_pair[pk] = (per_pair[pk] or 0) + 1
        local nth = per_pair[pk]
        local low_zone = (l.a_y < l.b_y) and e.za or e.zb
        local high_y = hi
        local can_drop = not cfg.open_air and LA[low_zone.archetype].tall and (high_y + DATA.corridor_height <= low_zone.pos[2] + low_zone.height - 0.5)
        local kinds = nil
        if cfg.open_air then
          kinds = { "bridge", "stair", "bridge", "stair" }
        else
          kinds = { "stair", can_drop and "drop" or "ramp", "ramp", can_drop and "drop" or "stair" }
          if cfg.lifts and nth == 3 then kinds[3] = "lift" end
        end
        local t = kinds[((nth - 1) % #kinds) + 1]
        if nth > #kinds then t = rng.pick(kinds) end
        l.type = t
        if t == "drop" and l.a_y < l.b_y then l.a, l.b, l.a_y, l.b_y = l.b, l.a, l.b_y, l.a_y end
      end
      if l.type == "corridor" then l.width = rng.pick(DATA.corridor_widths) end
      if l.type == "bridge" then l.width = rng.pick({ 3, 3.5, 4 }) l.style = rng.pick({ "truss", "plank", "girder", "truss" }) end
      if l.type == "stair" or l.type == "ramp" then l.width = rng.pick({ 3.5, 4, 4.5 }) end
      if l.type == "drop" then l.width = 4 end
      if l.type == "lift" then l.width = 4 end
      links[#links + 1] = l
    end
  end
  -- Every pair of adjacent levels gets at least two kinds of vertical connection (zone stairs count): a second
  -- climb becomes a ramp (or a drop into a tall zone) when the pair has only one kind.
  do
    local pairs_k = {}
    local function lv(y) return math.floor(y / step + 0.25) end
    local function key(a, b) return math.min(lv(a), lv(b)) .. "/" .. math.max(lv(a), lv(b)) end
    local function kinds_of(k) pairs_k[k] = pairs_k[k] or { n = 0 } return pairs_k[k] end
    local function add(k, t) local e = kinds_of(k) if not e[t] then e[t] = true e.n = e.n + 1 end end
    for _, k in ipairs(keys) do
      local z = zones[k]
      if #z.levels > 1 then
        local kk = key(z.pos[2], z.pos[2] + z.levels[2])
        if z.archetype == "split" or z.archetype == "tower" or z.archetype == "terrace" then add(kk, "stair") add(kk, "ramp")
        else add(kk, "stair") end
      end
      if z.archetype == "sunken" then add(key(z.pos[2] - 4.5, z.pos[2]), "stair") add(key(z.pos[2] - 4.5, z.pos[2]), "drop") end
      if z.archetype == "mound" then add(key(z.pos[2], z.pos[2] + 4.5), "stair") add(key(z.pos[2], z.pos[2] + 4.5), "ramp") end
    end
    for _, l in ipairs(links) do
      -- (mirror twins copy their twin's kind below, so they do not count here)
      if math.abs(l.a_y - l.b_y) > 0.5 and l.type ~= "window" and not l.edge.twin then add(key(l.a_y, l.b_y), l.type) end
    end
    for _, l in ipairs(links) do
      local k = key(l.a_y, l.b_y)
      local e = pairs_k[k]
      if e and e.n < 2 and math.abs(l.a_y - l.b_y) > 0.5 and not l.edge.kind and not l.edge.twin then
        local alt = cfg.open_air and "stair" or "ramp"
        if l.type == alt then alt = cfg.open_air and "bridge" or "stair" end
        if not e[alt] then
          l.type = alt
          if alt == "ramp" or alt == "stair" then l.width = l.width or 4 end
          if alt == "bridge" then l.width = 3.5 l.style = l.style or "plank" end
          add(k, alt)
        end
      end
    end
  end
  -- Mirror twins take their twin's kind and width.
  for _, l in ipairs(links) do
    if l.edge.twin then
      for _, m in ipairs(links) do
        if m.edge.cfg == l.edge.twin and m.par == l.par then
          l.type, l.width, l.style = m.type, m.width, m.style
          if (m.a_y > m.b_y) ~= (l.a_y > l.b_y) and l.type == "drop" then l.a, l.b, l.a_y, l.b_y = l.b, l.a, l.b_y, l.a_y end
        end
      end
    end
  end

  -- 7. Gaps per column and row boundary from what crosses them.
  local colgap, rowgap = {}, {}
  for c = 0, cfg.cols - 2 do colgap[c] = q2(rng.range(cfg.gap[1], cfg.gap[2])) end
  for r = 0, cfg.rows - 2 do rowgap[r] = q2(rng.range(cfg.gap[1], cfg.gap[2])) end
  for _, l in ipairs(links) do
    local ca, cb = cells[l.a], cells[l.b]
    local need = gap_need(l) + 1
    if ca.r == cb.r then
      local c = math.min(ca.c, cb.c)
      colgap[c] = math.max(colgap[c], need)
    else
      local r = math.min(ca.r, cb.r)
      rowgap[r] = math.max(rowgap[r], need)
    end
  end
  if cfg.mirror_cols then
    for c = 0, cfg.cols - 2 do local m = cfg.cols - 2 - c local v = math.max(colgap[c], colgap[m]) colgap[c], colgap[m] = v, v end
  end
  for c = 0, cfg.cols - 2 do colgap[c] = q2(colgap[c]) end
  for r = 0, cfg.rows - 2 do rowgap[r] = q2(rowgap[r]) end
  place(colgap, rowgap)
  -- Gap widening where the jitter ate into a link's need.
  for _, k in ipairs(keys) do zones[k].pos[1] = q2(zones[k].pos[1]) zones[k].pos[3] = q2(zones[k].pos[3]) end

  -- 8. Emit into the graph with ids.
  local id_of = {}
  local order = {}
  for _, k in ipairs(keys) do order[#order + 1] = k end
  for i, k in ipairs(order) do
    local z = zones[k]
    z.id = cfg.prefix and (cfg.prefix .. i) or ("z" .. (#G.zones + 1))
    id_of[k] = z.id
    G.zones[#G.zones + 1] = z
  end
  for _, k in ipairs(order) do local z = zones[k] if z.twin then z.mirror_of = id_of[z.twin] z.twin = nil end end
  for _, l in ipairs(links) do
    l.a, l.b = id_of[l.a], id_of[l.b]
    l.edge, l.par = nil, nil
    l.id = "l" .. (#G.links + 1)
    G.links[#G.links + 1] = l
  end
  return zones, id_of, cells
end

-- Assigns link offsets (random in the free spans) and, when a link does not fit, re-centres its zones' jitter.
local function fix_offsets(G, rng)
  for pass = 1, 4 do
    for _, l in ipairs(G.links) do l.offset = nil end
    local plan = resolve(G, rng, true)
    if #plan.errors == 0 then return plan end
    -- First narrow the failing links to their minimum width, then re-centre their zones.
    local narrowed = false
    if pass == 1 then
      for _, e in ipairs(plan.errors) do
        for _, l in ipairs(G.links) do
          if l.id == e.link and l.width and l.type ~= "road" then
            local min = (l.type == "bridge") and 3 or 3.5
            if l.width > min then l.width = min narrowed = true end
          end
        end
      end
    end
    -- Centre the zones of each failing link in their cells (drop jitter) and retry.
    local byid = {}
    for _, z in ipairs(G.zones) do byid[z.id] = z end
    for _, e in ipairs(narrowed and {} or plan.errors) do
      for _, l in ipairs(G.links) do
        if l.id == e.link then
          for _, zid in ipairs({ l.a, l.b }) do
            local z = byid[zid]
            if z and z.home then z.pos[1], z.pos[3] = z.home[1], z.home[2] end
          end
        end
      end
    end
  end
  for _, l in ipairs(G.links) do l.offset = nil end
  return resolve(G, rng, true)
end

-- Mirrors offsets of twin links (team modes): a link along x keeps its z, a link along z mirrors its x about the
-- axis; links of the centre column sit on the axis.
local function mirror_offsets(G, axis_x)
  local byid = {}
  for _, z in ipairs(G.zones) do byid[z.id] = z end
  local twin = {}
  for _, z in ipairs(G.zones) do if z.mirror_of then twin[z.id] = z.mirror_of twin[z.mirror_of] = z.id end end
  local used = {}
  for _, l in ipairs(G.links) do
    local za, zb = byid[l.a], byid[l.b]
    if za.mirror_of or zb.mirror_of then
      local ta, tb = twin[l.a] or l.a, twin[l.b] or l.b
      for _, m in ipairs(G.links) do
        if not used[m] and m ~= l and m.type == l.type and ((m.a == ta and m.b == tb) or (m.a == tb and m.b == ta))
          and not byid[m.a].mirror_of and not byid[m.b].mirror_of then
          used[m] = true
          local f = link_frame(za, zb)
          if f and m.offset then
            if f.axis == "x" then l.offset = m.offset else l.offset = q4(2 * axis_x - m.offset) end
          end
          break
        end
      end
    elseif not twin[l.a] and not twin[l.b] then
      local f = link_frame(za, zb)
      if f and f.axis == "z" and math.abs(za.pos[1] - axis_x) < 0.01 and math.abs(zb.pos[1] - axis_x) < 0.01 then l.offset = axis_x end
    end
  end
end

local function finalize(G)
  G.area = layout_area(G, resolve(G, nil, false))
  G.levels = floor_levels(G)
  return G
end

-- Spawn groups spread over zones by area (DM, KOTH).
local function spread_spawns(G, total, filter)
  local list = {}
  for _, z in ipairs(G.zones) do if filter(z) then list[#list + 1] = z end end
  table.sort(list, function(a, b) if zone_area(a) ~= zone_area(b) then return zone_area(a) > zone_area(b) end return a.id < b.id end)
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

local function gen_dm(G, rng, t)
  local B = G.budget
  local cols, rows = B.grid[1], B.grid[2]
  local open = G.class == "open_air"
  local nz = rng.int(B.zones[1], math.min(B.zones[2], cols * rows))
  if open then nz = math.min(nz, 14) end
  -- Cells: start full, drop corner cells at random down to nz.
  local cells, keys = {}, {}
  for r = 0, rows - 1 do for c = 0, cols - 1 do local k = c .. "," .. r cells[k] = { c = c, r = r } keys[#keys + 1] = k end end
  local hubs = {}
  local hc, hr = math.floor((cols - 1) / 2), math.floor((rows - 1) / 2)
  hubs[1] = hc .. "," .. hr
  if B.hubs[1] >= 2 then hubs[2] = math.min(cols - 1, hc + 2) .. "," .. math.min(rows - 1, hr + 1) end
  local removable = {}
  for _, k in ipairs(keys) do
    local c = cells[k]
    local corner = (c.c == 0 or c.c == cols - 1) and (c.r == 0 or c.r == rows - 1)
    local edge = (c.c == 0 or c.c == cols - 1 or c.r == 0 or c.r == rows - 1)
    local hub = false
    for _, h in ipairs(hubs) do if h == k then hub = true end end
    if not hub and (corner or (edge and cols * rows - nz > 4)) then removable[#removable + 1] = k end
  end
  rng.shuffle(removable)
  local present = cols * rows
  local nook_slots = {}
  -- Removing a cell must leave every remaining cell with two neighbours (no zone born a dead end).
  local function nbr_count(c, r)
    local n = 0
    for _, d in ipairs({ { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } }) do if cells[(c + d[1]) .. "," .. (r + d[2])] then n = n + 1 end end
    return n
  end
  for _, k in ipairs(removable) do
    if present > nz and cells[k] then
      local c0 = cells[k]
      cells[k] = nil
      local ok = true
      for _, d in ipairs({ { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } }) do
        local nk = (c0.c + d[1]) .. "," .. (c0.r + d[2])
        if cells[nk] and nbr_count(c0.c + d[1], c0.r + d[2]) < 2 then ok = false end
      end
      if ok then present = present - 1 nook_slots[#nook_slots + 1] = k else cells[k] = c0 end
    end
  end
  -- Levels: a gradient across the grid plus noise, split in thirds (low, middle, high).
  local ang = rng.range(0, 2 * math.pi)
  local vals = {}
  for _, k in ipairs(keys) do
    local c = cells[k]
    if c then vals[#vals + 1] = { k, math.cos(ang) * c.c + math.sin(ang) * c.r + rng.range(-0.45, 0.45) } end
  end
  table.sort(vals, function(a, b) return a[2] < b[2] end)
  for i, v in ipairs(vals) do cells[v[1]].level = math.min(2, math.floor((i - 1) * 3 / #vals)) end
  -- Hubs sit on the ground level BEFORE the smoothing below, so their neighbours come down to within one level of
  -- them (forcing a hub down afterwards left level-2 neighbours two storeys above it: unlinkable, and the lost hub
  -- links cost small open-air layouts their loops).
  for _, h in ipairs(hubs) do if cells[h] then cells[h].level = 0 end end
  -- Neighbours never sit two levels apart (a link climbs one level).
  for _ = 1, 6 do
    local changed = false
    for _, k in ipairs(keys) do
      local c = cells[k]
      if c then
        for _, d in ipairs({ { 1, 0 }, { 0, 1 } }) do
          local o = cells[(c.c + d[1]) .. "," .. (c.r + d[2])]
          if o and math.abs(c.level - o.level) >= 2 then
            if c.level > o.level then c.level = c.level - 1 else o.level = o.level - 1 end
            changed = true
          end
        end
      end
    end
    if not changed then break end
  end
  for _, h in ipairs(hubs) do if cells[h] then cells[h].level = 0 end end
  local themes = G.themes
  for _, k in ipairs(keys) do
    local c = cells[k]
    if c then
      c.role = "room"
      c.choices = open and POOL.open_air or POOL.indoor
      c.palette = { theme = themes[(c.level % #themes) + 1], variant = 1 + ((c.c + c.r) % 3) }
      c.params = { split = rng.pick({ "+x", "-x", "+z", "-z" }) }
    end
  end
  for i, h in ipairs(hubs) do
    if cells[h] then
      cells[h].role = "hub"
      cells[h].choices = open and POOL.open_air_hub or POOL.hub
      cells[h].weight = 1.9
      cells[h].palette = { theme = themes[math.min(#themes, 3)], variant = i }
      cells[h].params = { depth = 4.5 }
      cells[h].auto_level = true
    end
  end
  -- Edges: every orthogonal neighbour pair; hubs get a second (parallel) link to two neighbours.
  local edges = {}
  for _, k in ipairs(keys) do
    local c = cells[k]
    if c then
      for _, d in ipairs({ { 1, 0 }, { 0, 1 } }) do
        local nk = (c.c + d[1]) .. "," .. (c.r + d[2])
        if cells[nk] then
          local par = nil
          if (c.role == "hub" or cells[nk].role == "hub") and not open and rng.chance(0.5) then par = 2 end
          edges[#edges + 1] = { k, nk, parallel = par }
        end
      end
    end
  end
  -- A reward nook in one empty cell beside the grid, when the area allows it.
  local nooks = 0
  local max_nooks = math.floor(G.target_area / DATA.nook_area)
  if not open then
    for _, k in ipairs(nook_slots) do
      if nooks < math.min(1, max_nooks) then
        local sc = { k:match("^(%-?%d+),(%-?%d+)$") }
        local c, r = tonumber(sc[1]), tonumber(sc[2])
        local nb = nil
        for _, d in ipairs({ { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } }) do
          local nk = (c + d[1]) .. "," .. (r + d[2])
          if cells[nk] and cells[nk].role ~= "hub" and not nb then nb = nk end
        end
        if nb then
          cells[k] = { c = c, r = r, role = "nook", archetype = "nook", weight = 0.18, level = cells[nb].level,
            palette = { theme = cells[nb].palette.theme, variant = 3 } }
          edges[#edges + 1] = { k, nb, required = true }
          nooks = nooks + 1
        end
      end
    end
  end
  grid_layout(G, rng, {
    cols = cols, rows = rows, cells = cells, edges = edges, target_area = G.target_area, prune = open and 0 or 0.4,
    min_rank = math.ceil((G.size_class == "L" and DATA.cycle_per_1000_L or DATA.cycle_per_1000) * G.target_area / 1000),
    gap = open and { 12, 18 } or { 6, 11 }, open_air = open, lifts = (G.size_class == "L" or G.size_class == "M") and not open,
  })
  -- Spawns: by the per-spawn budget, over 75% of the zones (never the nook).
  local per = (B.per_spawn[1] + B.per_spawn[2]) / 2
  local total = clamp(math.floor(G.target_area / per + 0.5), 4, 40)
  spread_spawns(G, total, function(z) return z.role ~= "nook" end)
end

local function gen_ctf(G, rng, t)
  local open = G.class == "open_air"
  local cols, rows = 5, 3
  local cells = {}
  local themes = G.themes
  -- Lanes: row 0 the high lane, row 1 the middle (bases), row 2 the flank (tunnel) lane.
  -- the high lane a level up, the flank lane a level down: three storeys (spec a.2: a lane at another height)
  local lane_level = { [0] = 1, [1] = 0, [2] = -1 }
  local centre_high = false
  local used = {}
  local function cell(c, r, o)
    o.c, o.r = c, r
    cells[c .. "," .. r] = o
    return o
  end
  local red_cols = { 0, 1 }
  for _, c in ipairs(red_cols) do
    for r = 0, rows - 1 do
      local o = { level = lane_level[r], lane = r, team = "red", palette = { theme = themes[(c % #themes) + 1], variant = 1 + r, team = "red" } }
      -- Sizes keep the flags 50-115 m apart: the lanes run along x, so zones are narrow in x and deep in z.
      local deep = rng.pick({ 22, 24, 26 })
      if c == 0 and r == 1 then
        o.role = "base"
        o.choices = open and { "tower", "terrace" } or { "gallery" }
        o.size = open and { 24, 26 } or { 18, 26 }
        o.fixed = true
        -- the base's upper deck faces the high lane (row 0, toward -z)
        o.params = { split = "-z" }
        o.fixed_split = true
      elseif c == 0 then
        o.role = "spawn"
        o.choices = open and { "deck", "isle", "garden", "outpost", "gantry" } or { "hall", "storage", "colonnade", "courtyard" }
        o.size = { 16, 20 }
      elseif r == 2 then
        o.role = "flank"
        o.choices = open and { "garden", "isle", "gantry" } or { "tunnel", "storage" }
        o.size = { open and 18 or 12, deep }
      else
        o.role = "room"
        o.choices = open and { "isle", "deck", "garden", "gantry", "outpost" } or { "hall", "colonnade", "pit", "courtyard" }
        o.size = { 18, deep }
      end
      o.params = o.params or { split = rng.pick({ "+x", "-x" }) }
      cell(c, r, o)
    end
  end
  -- Centre column: the mid hub, the high crossing and the low flank, neutral palettes.
  cell(2, 0, { level = centre_high and 2 or 1, lane = 0, role = "room", centre_x = true, size = { 18, 22 },
    choices = open and { "deck", "garden", "isle", "outpost" } or { "hall", "colonnade", "courtyard" }, palette = { theme = themes[3] or themes[1], variant = 1 } })
  cell(2, 1, { level = 0, lane = 1, role = "hub", centre_x = true, params = { depth = 4.5 }, size = open and { 26, 30 } or { 28, 30 },
    choices = open and { "ring", "hangar" } or { "arena", "sunken", "atrium" }, palette = { theme = themes[3] or themes[1], variant = 2 } })
  cell(2, 2, { level = -1, lane = 2, role = "flank", centre_x = true, size = { 14, 24 },
    choices = open and { "garden", "isle", "gantry" } or { "tunnel", "storage", "pit" }, palette = { theme = themes[3] or themes[1], variant = 3 } })
  -- Blue half mirrors the red one.
  for _, c in ipairs(red_cols) do
    for r = 0, rows - 1 do
      local src = cells[c .. "," .. r]
      cell(cols - 1 - c, r, { mirror_of = c .. "," .. r, role = src.role, lane = r, team = "blue",
        palette = { theme = src.palette.theme, variant = src.palette.variant, team = "blue" } })
    end
  end
  -- Edges: three lanes along x, base cross links, centre cross links; blue twins of red edges.
  local edges = {}
  local function edge(a, b, o) o = o or {} o[1], o[2] = a, b edges[#edges + 1] = o return #edges end
  local function mk(c, r) return c .. "," .. r end
  local function mirror_key(k) local c, r = k:match("^(%d+),(%d+)$") return (cols - 1 - tonumber(c)) .. "," .. r end
  local red_edges = {}
  for r = 0, rows - 1 do
    red_edges[#red_edges + 1] = edge(mk(0, r), mk(1, r), { required = true })
    red_edges[#red_edges + 1] = edge(mk(1, r), mk(2, r), { required = true })
  end
  red_edges[#red_edges + 1] = edge(mk(0, 1), mk(0, 0), { required = true })
  red_edges[#red_edges + 1] = edge(mk(0, 1), mk(0, 2), { required = true })
  red_edges[#red_edges + 1] = edge(mk(1, 0), mk(1, 1), { required = true })
  if rng.chance(0.6) then red_edges[#red_edges + 1] = edge(mk(1, 1), mk(1, 2), {}) end
  edge(mk(2, 0), mk(2, 1), { required = true })
  edge(mk(2, 1), mk(2, 2), { required = true })
  for _, i in ipairs(red_edges) do
    local e = edges[i]
    local o = { required = e.required, climb = e.climb, twin = i }
    edge(mirror_key(e[1]), mirror_key(e[2]), o)
  end
  grid_layout(G, rng, {
    cols = cols, rows = rows, cells = cells, edges = edges, target_area = G.target_area, prune = 0, min_rank = 0,
    gap = open and { 6, 7 } or { 5, 7 }, open_air = open, mirror_cols = true,
  })
  -- Twin edges: grid_layout recorded twin as the red edge index; link twins share kinds there.
  -- Flags in the bases; spawns in each team's base and spawn rooms.
  local base_r, base_b = nil, nil
  for _, z in ipairs(G.zones) do
    if z.role == "base" and z.team == "red" then base_r = z end
    if z.role == "base" and z.team == "blue" then base_b = z end
  end
  G.objectives[#G.objectives + 1] = { kind = "flag", team = "red", zone = base_r.id }
  G.objectives[#G.objectives + 1] = { kind = "flag", team = "blue", zone = base_b.id }
  local per = (G.budget.per_spawn[1] + G.budget.per_spawn[2]) / 2
  local team_total = clamp(math.floor(G.target_area / per / 2 + 0.5), 4, 16)
  for _, team in ipairs({ "red", "blue" }) do
    local list = {}
    for _, z in ipairs(G.zones) do if z.team == team and (z.role == "spawn" or z.role == "base") then list[#list + 1] = z end end
    table.sort(list, function(a, b) return a.id < b.id end)
    local each = math.floor(team_total / #list)
    local extra = team_total - each * #list
    for i, z in ipairs(list) do
      G.objectives[#G.objectives + 1] = { kind = "spawns", id = "s" .. (#G.objectives + 1), zone = z.id, team = team, count = each + ((i <= extra) and 1 or 0) }
    end
  end
end

local function gen_koth(G, rng, t)
  local open = G.class == "open_air"
  local cells = {}
  local themes = G.themes
  for r = 0, 2 do
    for c = 0, 2 do
      local k = c .. "," .. r
      if c == 1 and r == 1 then
        local mode = rng.chance(0.6) and "sunken" or "mound"
        cells[k] = { c = c, r = r, role = "hill", level = 0, weight = 2.0,
          archetype = open and "summit" or mode, params = { depth = 4.5 }, palette = { theme = themes[3] or themes[1], variant = 1 } }
      else
        local corner = (c ~= 1 and r ~= 1)
        cells[k] = { c = c, r = r, role = "room", level = corner and rng.int(0, 1) or rng.int(0, 1),
          choices = open and POOL.open_air or POOL.indoor,
          palette = { theme = themes[((c + r) % 2) + 1], variant = 1 + ((c * 3 + r) % 3) }, params = { split = rng.pick(SIDES) } }
      end
    end
  end
  -- Make sure some ring zones sit a level up (3 floor levels with the sunken court or the plinth).
  local up = 0
  for _, k in ipairs({ "0,0", "1,0", "2,0", "2,1", "2,2", "1,2", "0,2", "0,1" }) do if cells[k].level == 1 then up = up + 1 end end
  if up < 2 then cells["2,0"].level = 1 cells["0,2"].level = 1 end
  if up > 6 then cells["1,0"].level = 0 cells["1,2"].level = 0 end
  -- A raised hill (or a sky ring) adds no storey below: two corners go a level higher still, their
  -- ring neighbours a level up, so the layout keeps three storeys.
  if cells["1,1"].archetype ~= "sunken" then
    for _, k in ipairs({ "0,0", "2,2" }) do cells[k].level = 2 end
    for _, k in ipairs({ "1,0", "0,1", "2,1", "1,2" }) do cells[k].level = 1 end
  end
  -- Floating rings: the corners keep whole storeys; the four decks around the hill float half a metre apart
  -- (0, 0.5, 1, 1.5 m) so the floor has real height variety, not one flat ring.
  if open then
    for _, k in ipairs({ "0,0", "2,0", "0,2", "2,2" }) do cells[k].offset = 0 end
    for i, k in ipairs({ "1,0", "2,1", "1,2", "0,1" }) do cells[k].offset = (i - 1) * 0.5 end
  end
  local edges = {}
  local ring = { "0,0", "1,0", "2,0", "2,1", "2,2", "1,2", "0,2", "0,1" }
  for i = 1, #ring do edges[#edges + 1] = { ring[i], ring[i % #ring + 1], required = true } end
  local approaches = rng.int(6, 8)
  local spokes = { "1,0", "2,1", "1,2", "0,1" }
  rng.shuffle(spokes)
  for i, k in ipairs(spokes) do
    edges[#edges + 1] = { k, "1,1", required = true, parallel = (i <= approaches - 4) and 2 or 1 }
  end
  grid_layout(G, rng, {
    cols = 3, rows = 3, cells = cells, edges = edges, target_area = G.target_area, prune = 0, min_rank = 0,
    gap = open and { 12, 18 } or { 7, 11 }, open_air = open,
  })
  local hill = nil
  for _, z in ipairs(G.zones) do if z.role == "hill" then hill = z end end
  local radius = math.max(6, math.min(hill.size[1], hill.size[2]) / 2 - 7)
  G.objectives[#G.objectives + 1] = { kind = "hill", zone = hill.id, radius = radius }
  local per = (G.budget.per_spawn[1] + G.budget.per_spawn[2]) / 2
  local total = clamp(math.floor(G.target_area / per + 0.5), 6, 24)
  local list = {}
  for _, z in ipairs(G.zones) do if z.role ~= "hill" then list[#list + 1] = z end end
  -- Alternate teams around the ring so both sides start the same distance from the hill.
  table.sort(list, function(a, b) return a.id < b.id end)
  local each = math.max(1, math.floor(total / #list))
  for i, z in ipairs(list) do
    G.objectives[#G.objectives + 1] = { kind = "spawns", id = "s" .. (#G.objectives + 1), zone = z.id, count = each }
  end
end

-- Payload: a start row, then per segment a span row (road) and a checkpoint row (yard; the finale last), with a
-- flank column and a high-ground column that swap sides every segment.
local function gen_payload(G, rng, t)
  local themes = G.themes
  local players = G.players
  local nseg = 3 + ((players >= 12) and 1 or 0) + ((players >= 20) and 1 or 0)
  nseg = clamp(nseg, DATA.payload_segments[1], DATA.payload_segments[2])
  if G.segments then nseg = clamp(G.segments, 3, 5) end
  local step = G.step
  local cells = {}
  local rows = 1 + 2 * nseg
  -- Segment levels: climbs of one step (at least one), level legs, at most one downhill pay-off.
  local lv = { [0] = 0 }
  local downs = 0
  local ups = 0
  for s = 1, nseg do
    local roll = rng.float()
    local d = nil
    if s == 1 then d = (roll < 0.5) and 1 or 0
    elseif roll < 0.5 then d = 1
    elseif roll < 0.8 or downs >= 1 or lv[s - 1] == 0 then d = 0
    else d = -1 end
    if d == -1 then downs = downs + 1 end
    if d == 1 then ups = ups + 1 end
    lv[s] = math.max(0, lv[s - 1] + d)
  end
  if ups == 0 then lv[nseg] = lv[nseg - 1] + 1 end
  local tunnel = rng.int(2, nseg)
  -- Columns: 0 and 2 alternate flank (indoor, at the track level) and high ground (a level up).
  local flank_pool = { "hall", "storage", "colonnade", "gallery", "pit", "split", "tunnel" }
  -- High ground: open terraces and courtyards under the sky, or the upper floor of a building.
  local high_pool = { "overlook", "courtyard", "hall", "colonnade", "gallery" }
  local used = {}
  cells["1,0"] = { c = 1, r = 0, role = "spawn", archetype = "start", level = 0, segment = 1, fixed = true,
    palette = { theme = themes[1], variant = 2, team = "blue" } }
  local first_flank_col = rng.chance(0.5) and 0 or 2
  cells[first_flank_col .. ",0"] = { c = first_flank_col, r = 0, role = "staging", archetype = "hall", level = 0, segment = 1,
    palette = { theme = themes[1], variant = 3, team = "blue" }, weight = 0.7 }
  used.hall = 1
  local edges = {}
  local function E(a, b, o) o = o or {} o[1], o[2] = a, b edges[#edges + 1] = o end
  E("1,0", "1,1", { required = true, kind = "doorway" })
  E("1,0", first_flank_col .. ",0", { required = true })
  for s = 1, nseg do
    local theme = themes[((s - 1) % #themes) + 1]
    local rs, rc = 2 * s - 1, 2 * s
    local flank_col = ((s % 2 == 1) == (first_flank_col == 0)) and 0 or 2
    local high_col = 2 - flank_col
    local y_in, y_out = lv[s - 1], lv[s]
    -- The road: level stretches at both ends (side doors), a grade of at most 15% between.
    local rise = (y_out - y_in) * step
    local road_len = (rise ~= 0) and (24 + even(math.abs(rise) / 0.14 + 1)) or rng.pick({ 36, 40, 44 })
    cells["1," .. rs] = { c = 1, r = rs, role = "span", archetype = "road", level = y_in, segment = s, jx_range = 7,
      params = { heading = "+z", rise = rise, south = 12, north = 12, tunnel = (s == tunnel) or nil }, palette = { theme = theme, variant = 1 },
      size = { rng.pick({ 10, 10, 12 }), road_len } }
    local last = (s == nseg)
    cells["1," .. rc] = { c = 1, r = rc, role = "checkpoint", archetype = last and "finale" or "yard", level = y_out, segment = s,
      palette = { theme = last and "bunker" or theme, variant = 2 }, weight = last and 1.8 or 1.25 }
    -- The span row's side zones sit at the road's two ends: the flank meets the low end, the high ground the top.
    cells[flank_col .. "," .. rs] = { c = flank_col, r = rs, role = "flank", level = y_in, segment = s, align = "south",
      choices = flank_pool, palette = { theme = theme, variant = 3 }, params = { split = "+z" },
      size = { rng.pick({ 18, 20, 22 }), math.min(road_len - 6, rng.pick({ 26, 28, 30 })) } }
    cells[flank_col .. "," .. rc] = { c = flank_col, r = rc, role = "flank", level = y_out, segment = s,
      choices = flank_pool, palette = { theme = theme, variant = 1 }, params = { split = "+z" }, size = { rng.pick({ 18, 20, 22 }), rng.pick({ 24, 26 }) } }
    cells[high_col .. "," .. rs] = { c = high_col, r = rs, role = "high", choices = high_pool, level = y_in + 1, segment = s, align = "north",
      palette = { theme = theme, variant = 2 }, params = { split = "+z" }, size = { rng.pick({ 18, 20 }), math.min(road_len - 6, rng.pick({ 24, 28, 32 })) } }
    cells[high_col .. "," .. rc] = { c = high_col, r = rc, role = "high", archetype = (s % 2 == 1) and "overlook" or nil, choices = high_pool,
      level = y_out + 1, segment = s, palette = { theme = theme, variant = 3 }, size = { rng.pick({ 18, 20 }), rng.pick({ 24, 26 }) } }
    -- Spine, flank and high routes along the segment, and cross links between the columns.
    E("1," .. rs, "1," .. rc, { required = true, kind = "road" })
    E(flank_col .. "," .. rs, flank_col .. "," .. rc, { required = true })
    E(high_col .. "," .. rs, high_col .. "," .. rc, { required = true })
    E(flank_col .. "," .. rs, "1," .. rs, { required = true })
    E("1," .. rs, high_col .. "," .. rs, { required = true })
    E(flank_col .. "," .. rc, "1," .. rc, { required = true, parallel = 2 })
    E("1," .. rc, high_col .. "," .. rc, { required = true, kind = (not last) and "drop" or nil })
    if not last then
      E(high_col .. "," .. rc, "1," .. rc, { kind = "stair" })
    end
    -- Into the next segment: the cart gate, and both side columns.
    if s > 1 then
      local prc = 2 * (s - 1)
      E("1," .. prc, "1," .. rs, { required = true, kind = "road" })
      E("0," .. prc, "0," .. rs, { required = true })
      E("2," .. prc, "2," .. rs, { required = true })
    else
      E(first_flank_col .. ",0", flank_col .. ",1", { required = true })
    end
  end
  -- Archetype choice for flanks happens in grid_layout (choices); the span-row flank is long.
  grid_layout(G, rng, {
    cols = 3, rows = rows, cells = cells, edges = edges, target_area = G.target_area, prune = 0, min_rank = 0, gap = { 7, 10 },
  })
  -- Drop links from the overlooks into the yard were set as kind "drop": orient high -> low.
  local halves = {}
  local byid = {}
  for _, z in ipairs(G.zones) do byid[z.id] = z end
  for _, l in ipairs(G.links) do
    local za, zb = byid[l.a], byid[l.b]
    if l.type == "drop" then
      if l.a_y < l.b_y then l.a, l.b, l.a_y, l.b_y = l.b, l.a, l.b_y, l.a_y end
      -- the overlook's floor is the high port; the yard floor is the landing
      l.width = 4
    end
    if l.type == "road" then l.width = math.min(za.size[1], zb.size[1], 9) - 1 l.sky = true end
    -- The checkpoint splits its row at the yard's centre: the side rooms keep a way into the yard on each
    -- side of that line, so neither half of a side room is a dead end of its segment.
    if za.cell[2] == zb.cell[2] and za.cell[2] % 2 == 0 and za.cell[2] > 0 and (za.cell[1] == 1 or zb.cell[1] == 1) then
      local key = (za.id < zb.id) and (za.id .. zb.id) or (zb.id .. za.id)
      halves[key] = (halves[key] or 0) + 1
      l.prefer = (halves[key] % 2 == 1) and -1 or 1
    end
    if l.type == "stair" and math.abs(l.a_y - l.b_y) < EPS then l.type = "corridor" l.width = 4.5 end
    za.segment = za.segment or 1
  end
  -- Segment index per zone from the cells.
  for _, z in ipairs(G.zones) do
    local cell = cells[z.cell[1] .. "," .. z.cell[2]]
    z.segment = cell.segment
    if z.archetype == "road" then z.params.tunnel = cell.params.tunnel end
  end
  -- Objectives: the cart along the spine, checkpoints at the yards, spawn groups with the hand-off.
  local spine = {}
  for r = 1, rows - 1 do
    for _, z in ipairs(G.zones) do if z.cell[1] == 1 and z.cell[2] == r then spine[#spine + 1] = z.id end end
  end
  G.objectives[#G.objectives + 1] = { kind = "cart", zones = spine, attacking_team = "blue" }
  local function zone_at(c, r) for _, z in ipairs(G.zones) do if z.cell[1] == c and z.cell[2] == r then return z end end end
  local start = zone_at(1, 0)
  local staging = zone_at(first_flank_col, 0)
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
  local first = { spawns(start, "blue", "attack", 6, "cp1"), spawns(staging, "blue", "attack", 2, "cp1") }
  local flank_cp = {}
  for s = 1, nseg do
    local flank_col = ((s % 2 == 1) == (first_flank_col == 0)) and 0 or 2
    flank_cp[s] = zone_at(flank_col, 2 * s)
  end
  first[3] = spawns(flank_cp[1], "red", "defend", 6, "cp1")
  for s = 1, nseg do
    local yard = zone_at(1, 2 * s)
    local cp = { kind = "checkpoint", index = s, zone = yard.id, spawns = {} }
    if s == 1 then cp.spawns = first
    else
      cp.spawns[1] = spawns(flank_cp[s - 1], "blue", "attack", 6, "cp" .. s)
      cp.spawns[2] = spawns(flank_cp[s], "red", "defend", 6, "cp" .. s)
    end
    G.objectives[#G.objectives + 1] = cp
  end
  G.segments = nseg
end

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
        hubs = { B.hubs[1], B.hubs[2] }, grid = B.grid, target = math.floor(target + 0.5),
        cycle_per_1000 = (sc == "L") and DATA.cycle_per_1000_L or DATA.cycle_per_1000 },
      zones = {}, links = {}, objectives = {}, segments = t.segments, themes = themes,
    }
    if gametype == "payload" then G.budget.area = { DATA.payload_area_min + 2000, BUDGET.payload.area[2] } end
    if gametype == "dm" then gen_dm(G, arng, t)
    elseif gametype == "ctf" then gen_ctf(G, arng, t)
    elseif gametype == "koth" then gen_koth(G, arng, t)
    else gen_payload(G, arng, t) end
    -- Fit every link on its walls (zones of a link that does not fit go back to their cell centres).
    local plan = fix_offsets(G, arng)
    if gametype == "ctf" then
      local cxs = 0
      for _, z in ipairs(G.zones) do if z.role == "hub" then cxs = z.pos[1] end end
      mirror_offsets(G, cxs)
      plan = resolve(G, nil, false)
      if #plan.errors > 0 then
        for _, e in ipairs(plan.errors) do for _, l in ipairs(G.links) do if l.id == e.link then l.offset = nil end end end
        plan = resolve(G, nil, true)
      end
    end
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
      z.home = nil
      -- The per-zone dressing hook (a population pass reads it): the zone theme and a density.
      z.dress = { theme = z.palette and z.palette.theme or themes[1], density = (z.role == "hub" or z.role == "hill") and 1.2 or 1 }
    end
    finalize(G)
    local V = check(G)
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

---@kit kit.layout_archetypes{} → archetype[] — The zone archetypes kit.layout and kit.build_layout use: {name, class ("indoor", "open_air" or "payload"), min = {w, d} (kit.check_layout refuses smaller zones; a road's {w, d} turns with its heading), max = {w, d} (the sizes kit.layout generates; larger hand-made zones are allowed), levels (port levels; "step" = one level up), loops (inside the zone), summary}.
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

local LINK_CHAR = { corridor = "=", doorway = "=", stair = "s", ramp = "r", drop = "v", bridge = "b", window = "~", lift = "l", road = "#" }

---@kit kit.layout_preview{graph, scale?} → preview — A plan of a layout graph as text (north = +z up, one character per scale metres): zones filled with their level digit and labelled with their letter, links drawn with their type's character (= corridor/doorway, s stair, r ramp, v drop, b bridge, ~ window, l lift, # cart road), objectives marked (R/B flags, H hill, C checkpoints, S spawns). Returns {text, svg, legend[], scale, bounds}.
---@opt graph: table — Required: a kit.layout graph.
---@opt scale: number = fits about 100 columns — Metres per character.
function kit.layout_preview(t)
  local api = "kit.layout_preview"
  check_keys(api, t, { "graph", "scale" })
  local G = t.graph
  if type(G) ~= "table" or type(G.zones) ~= "table" then fail(api, "'graph' must be a kit.layout graph.") end
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, z in ipairs(G.zones) do
    local a, b, c, d = rect_of(z)
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
    local a, b, c, d = rect_of(z)
    local ch = level_digit(z.pos[2])
    local xx = a
    while xx <= c do
      local zz = b
      while zz <= d do put(xx, zz, ch) zz = zz + scale end
      xx = xx + scale
    end
    local L = letters:sub(((i - 1) % #letters) + 1, ((i - 1) % #letters) + 1)
    put(z.pos[1], z.pos[3], L)
    legend[#legend + 1] = string.format("%s %s %s%s %gx%g m, floor %g m (levels +%s)%s", L, z.id, z.archetype, z.role and (" [" .. z.role .. "]") or "",
      z.size[1], z.size[2], z.pos[2], table.concat(z.levels or { 0 }, ",+"), z.palette and (", " .. (z.palette.theme or "") .. (z.palette.team and ("/" .. z.palette.team) or "")) or "")
  end
  local plan = resolve(G, nil, false)
  for _, P in ipairs(plan.links) do
    if P.f and P.c then
      local ch = LINK_CHAR[P.link.type] or "?"
      local g = P.f
      local a, b = math.min(g.wa, g.wb), math.max(g.wa, g.wb)
      local s = a
      while s <= b + EPS do
        if g.axis == "x" then put(s, P.c, ch) else put(P.c, s, ch) end
        s = s + scale * 0.5
      end
      legend[#legend + 1] = string.format("  %s %s %s-%s %g m wide, y %g -> %g", P.link.id, P.link.type, P.link.a, P.link.b, P.W or 0, link_ys(P.link, P.za, P.zb))
    end
  end
  local byid = {}
  for _, z in ipairs(G.zones) do byid[z.id] = z end
  for _, o in ipairs(G.objectives or {}) do
    local z = byid[o.zone or ""]
    if z then
      local ch = (o.kind == "flag" and (o.team == "red" and "R" or "B")) or (o.kind == "hill" and "H") or (o.kind == "checkpoint" and "C") or (o.kind == "spawns" and "S") or nil
      if ch then put(z.pos[1] + (o.kind == "spawns" and z.size[1] * 0.25 or 0), z.pos[3] + (o.kind == "spawns" and z.size[2] * 0.25 or 0), ch) end
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
  local sv = { string.format('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d">',
    math.ceil((x1 - x0) * px), math.ceil((z1 - z0) * px), math.ceil((x1 - x0) * px), math.ceil((z1 - z0) * px)) }
  sv[#sv + 1] = '<rect width="100%" height="100%" fill="#222"/>'
  local shade = { "#6b8", "#8ab", "#ca8", "#c88", "#a8c", "#aaa" }
  for i, z in ipairs(G.zones) do
    local a, b, c, d = rect_of(z)
    local li = tonumber(level_digit(z.pos[2])) + 1
    sv[#sv + 1] = string.format('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" fill="%s" stroke="#111"/>',
      (a - x0) * px, (z1 - d) * px, (c - a) * px, (d - b) * px, shade[((li - 1) % #shade) + 1])
    sv[#sv + 1] = string.format('<text x="%.1f" y="%.1f" font-size="12" text-anchor="middle">%s %s</text>', (z.pos[1] - x0) * px, (z1 - z.pos[3]) * px, z.id, z.archetype)
  end
  for _, P in ipairs(plan.links) do
    if P.f and P.c then
      local g = P.f
      local a, b = math.min(g.wa, g.wb), math.max(g.wa, g.wb)
      local hw = (P.W or 4) / 2
      local rx, ry, rw, rh = nil, nil, nil, nil
      if g.axis == "x" then rx, ry, rw, rh = a, P.c + hw, b - a, 2 * hw else rx, ry, rw, rh = P.c - hw, b, 2 * hw, b - a end
      sv[#sv + 1] = string.format('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" fill="%s"/>', (rx - x0) * px, (z1 - ry) * px, rw * px, rh * px,
        P.link.type == "corridor" and "#ddd" or P.link.type == "road" and "#eee" or "#fd6")
    end
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
  R.cx, R.cz, R.w, R.d = z.pos[1], z.pos[3], z.size[1], z.size[2]
  R.y, R.h, R.c = z.pos[2], z.height or 6, z.chamfer or 0
  R.step = z.levels[2] or 5
  R.pal = palette_of(z)
  R.textures = R.pal.textures
  R.trim = { texture = R.pal.trims.post }
  R.ports = plan.ports[z.id] or {}
  R.solids, R.spots, R.parts, R.brushes, R.warnings = {}, {}, {}, {}, {}
  R.lane = nil
  return R
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

-- True when a box inside the zone stays clear of every port approach (and of the cart lane).
local function clear(R, x0, z0, x1, z1, y0, y1)
  for _, p in ipairs(R.ports) do
    local kx0, kz0, kx1, kz1 = keepout(R, p, p.drop_to and 6 or DATA.keep_depth)
    local py0 = p.drop_to or p.y0
    if x0 < kx1 and x1 > kx0 and z0 < kz1 and z1 > kz0 and y0 < p.y1 and y1 > py0 - 0.3 then return false end
  end
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
  return add_rec(R, kit.stairs{ name = R.name .. "_" .. part, pos = { x, y, z }, dir = dir, rise = rise, width = width, tread = DATA.tread,
    group = false, textures = R.textures })
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
  late = function(R) blank_pilasters(R, 4) end,
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
        add_rec(R, kit.bridge{ name = R.name .. "_catwalk", from = { R.x0 + bw - 0.5, R.y + R.step + 0.125, z }, to = { R.x1 - bw + 0.5, R.y + R.step + 0.125, z },
          width = 3, style = "truss", depth = 1.6, landmark = false, group = false, textures = R.textures, frame_texture = R.trim.texture })
      end
    else
      local x = R.cx + off
      if clear(R, x - 2, R.z0 + bw - 0.5, x + 2, R.z1 - bw + 0.5, R.y + R.step - 1, R.y + R.step + 3) then
        add_rec(R, kit.bridge{ name = R.name .. "_catwalk", from = { x, R.y + R.step + 0.125, R.z0 + bw - 0.5 }, to = { x, R.y + R.step + 0.125, R.z1 - bw + 0.5 },
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
    solid(R, "ring_w", R.x0, yb, cz0, cx0, R.y, cz1)
    solid(R, "ring_e", cx1, yb, cz0, R.x1, R.y, cz1)
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
    R.court = { cx0, cz0, cx1, cz1, yb }
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
    local depth = 4
    add_rec(R, kit.floor_section{ name = R.name .. "_pit", min = { px0, R.y, pz0 }, max = { px1, R.y, pz1 }, y = R.y, rise = -depth, textures = R.textures })
    local run = stair_run(depth)
    stairs(R, "stairs_s", R.cx - 3, R.y - depth, pz0 + run, "-z", depth, 3)
    stairs(R, "stairs_n", R.cx + 3, R.y - depth, pz1 - run, "+z", depth, 3)
    -- the plank bridge across the pit along x
    add_rec(R, kit.bridge{ name = R.name .. "_bridge", from = { px0 - 1, R.y + 0.125, R.cz }, to = { px1 + 1, R.y + 0.125, R.cz }, width = 3, style = "plank",
      landmark = false, group = false, texture = R.trim.texture })
    solid(R, "pit_crate1", px0 + 1, R.y - depth, R.cz + 2.5, px0 + 2.5, R.y - depth + 1.4, R.cz + 4, { detail = true })
    solid(R, "pit_crate2", px1 - 2.5, R.y - depth, R.cz - 4, px1 - 1, R.y - depth + 1.4, R.cz - 2.5, { detail = true })
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
      add_rec(R, kit.landmark{ name = R.name .. "_mast", pos = { R.cx, R.y, R.cz }, height = 25, base = 5, style = "pylon", group = false, texture = R.trim.texture })
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
    add_rec(R, kit.landmark{ name = R.name .. "_mast", pos = { x, R.y + R.step, z }, height = 26, base = 4, style = "pylon", group = false, texture = R.trim.texture })
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
    local hole = math.min(R.w, R.d) * 0.36
    local ix0, iz0, ix1, iz1 = R.cx - hole / 2, R.cz - hole / 2, R.cx + hole / 2, R.cz + hole / 2
    R.hole = { ix0, iz0, ix1, iz1 }
    local pieces = {
      { { R.x0, R.z0 }, { R.x1, R.z0 }, { ix1, iz0 }, { ix0, iz0 } },
      { { R.x1, R.z0 }, { R.x1, R.z1 }, { ix1, iz1 }, { ix1, iz0 } },
      { { R.x1, R.z1 }, { R.x0, R.z1 }, { ix0, iz1 }, { ix1, iz1 } },
      { { R.x0, R.z1 }, { R.x0, R.z0 }, { ix0, iz0 }, { ix0, iz1 } },
    }
    for i, p in ipairs(pieces) do floating(R, "piece" .. i, p, R.y, 1.5, 6) end
    R.outline = rect_poly(R.x0, R.z0, R.x1, R.z1)
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
      add_rec(R, kit.bridge{ name = R.name .. "_plank", from = { e0 - sign * 6, R.y + 0.125, b }, to = { e1 + sign * 1, R.y + s + 0.125, b }, width = 3, style = "plank", landmark = false, group = false, texture = R.trim.texture })
    else
      stairs(R, "fly_stairs", a, R.y, foot, up, s, 3)
      add_rec(R, kit.bridge{ name = R.name .. "_plank", from = { b, R.y + 0.125, e0 - sign * 6 }, to = { b, R.y + s + 0.125, e1 + sign * 1 }, width = 3, style = "plank", landmark = false, group = false, texture = R.trim.texture })
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
      made[#made + 1] = kit.bridge{ name = name, from = from, to = to, width = W - 1, style = l.style or "truss", group = false,
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
      made[#made + 1] = kit.stairs{ name = name .. "_flight", pos = at(start, yl), dir = dname[f.axis][dd], rise = rise, width = W - 1, tread = DATA.tread, group = false, textures = tex }
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
    made[#made + 1] = kit.bridge{ name = name, from = at(wa - d * 1, ya + 0.125), to = at(wb + d * 1, yb + 0.125), width = W - 1, style = l.style or "truss",
      group = false, landmark = false, textures = tex, frame_texture = trim }
    return made
  end
  -- Door jambs where a link meets a zone: a 3 m doorway into each room (exemplar doorways 2.5-3.7 m), so the
  -- rooms read (and measure) as separate spaces joined by doors, not one floor.
  if (l.type == "corridor" or l.type == "stair" or l.type == "ramp" or l.type == "drop") and W >= DATA.door_width + 1 then
    local dw = DATA.door_width
    local y0, y1 = math.min(ya, yb), math.max(ya, yb) + link_height(l)
    if l.type == "drop" then y0, y1 = ya, ya + link_height(l) end
    local d = (f.wb > f.wa) and 1 or -1
    for i, e in ipairs({ { f.wa, d }, { f.wb, -d } }) do
      local s0, s1 = e[1], e[1] + e[2] * 0.5
      for j, side in ipairs({ -1, 1 }) do
        local inner = side * dw / 2
        local outer = side * W / 2
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
      made[#made + 1] = kit.stairs{ name = name .. "_stairs", pos = at(foot, S.yl), dir = dname[f.axis][S.d], rise = S.rise, width = W, tread = DATA.tread, group = false, textures = tex }
    else
      made[#made + 1] = kit.ramp{ name = name .. "_ramp", pos = at(foot, S.yl), dir = dname[f.axis][S.d], rise = S.rise, run = S.run, width = W, group = false, textures = tex }
    end
    local top = foot + S.d * S.run
    if (S.wh - top) * S.d > 0.05 then
      made[#made + 1] = { brush = link_solid(P, gname, "landing", top, S.wh, S.yl, S.yh, W / 2, tex) }
    end
  elseif l.type == "lift" and P.lift then
    local L = P.lift
    local mid = (L.s0 + L.s1) / 2
    local half = W / 2 - 0.4
    local c = at(mid, L.yl)
    local plat = level.brush{ shape = "box", pos = { c[1], L.yl + 0.125, c[3] }, size = { 2 * half, 0.25, 2 * half }, textures = tex,
      tags = { kit = "layout", ["kit.name"] = name, ["kit.part"] = "lift" } }
    local mover = level.create_mover{ name = name .. "_lift", brushes = { plat }, props = { movement_type = "Ping Pong Once" },
      tags = { kit = "layout", ["kit.name"] = name } }
    local travel = math.max(2, (L.yh - L.yl) / 2.5)
    mover.start_keyframe:set_many{ name = name .. "_lift_bottom", travel_time_to_next = travel, return_travel = travel }
    mover:add_keyframe{ offset = { 0, L.yh - L.yl, 0 }, props = { name = name .. "_lift_top", pause_time = 2, travel_time_to_next = travel, return_travel = travel } }
    local resets = 2 * travel + 2
    level.place{ kind = "trigger", name = name .. "_lift_call_bottom", pos = { c[1], L.yl + 1.2, c[3] },
      props = { shape = "Sphere", sphere_radius = half, resets_after = resets, activated_by = "Players" }, links = { mover }, tags = { kit = "layout", ["kit.name"] = name } }
    local top_at = at(L.s1 + L.d * 1.5, L.yh)
    level.place{ kind = "trigger", name = name .. "_lift_call_top", pos = { top_at[1], L.yh + 1.2, top_at[3] },
      props = { shape = "Sphere", sphere_radius = 1.5, resets_after = resets, activated_by = "Players", use_key_required = true }, links = { mover }, tags = { kit = "layout", ["kit.name"] = name } }
    made[#made + 1] = { brush = plat, mover = mover }
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
    lights[#lights + 1] = level.place{ kind = "light", name = gname .. "_" .. l.id .. "_light" .. i, pos = pos,
      props = { color = color or kit.defaults.light_color, size_range = 9 }, tags = { kit = "layout", ["kit.name"] = gname .. "_" .. l.id } }
  end
  return lights
end

-- Spots: farthest-point picks from a zone's candidates, clear of solids.
local function pick_spots(R, count, y_pref)
  local cand = {}
  -- only spots well inside the zone's plan (chamfered corners cut the grid's outer points)
  local plan = (R.room and R.room.outline) or R.outline
  local inner = plan and (I.inset_convex(plan, 1.2) or plan) or nil
  for _, s in ipairs(R.spots) do
    local ok = clear(R, s[1] - 1.2, s[3] - 1.2, s[1] + 1.2, s[3] + 1.2, s[2], s[2] + 2)
    if ok and inner and not I.inside(inner, s[1], s[3]) then ok = false end
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

---@kit kit.build_layout{graph, name?, lights?, strict?, force?, populate?} → layout_report — Builds a layout graph: kit.check_layout first (errors refuse; design-rule violations warn, or refuse with strict = true), then in CSG time order the zone shells (and the open-air void), every link's air, the zones' interiors (stairs, galleries, courts, cover, ...), the links' steps, ramps, landings, lifts and bridges (outside open-air layouts a bridge's deck spans a chasm cut to the sky, link.depth metres deep, default 8), details, lights, and the objectives: spawn groups, CTF flags, the KOTH hill (a Capture_Point_Handler with its trigger) and the payload cart (One Way mover on the track on any heading, Payload_Handler, checkpoints on its keyframes, each linking the attacker and defender spawns that are live while it is the target). Every brush of a zone is tagged kit.zone = the zone's name (<name>_<zone id>), and that name works as a room name in kit.dress, kit.items and kit.spawn_points. Returns {name, zones[] (id, archetype, name, min, max, floor_y, spots, brushes, room), links[], spawns, objectives, warnings[], violations[], area}.
---@opt graph: table — Required: a kit.layout graph (possibly edited).
---@opt name: string = "L" — Prefix of every part's name (and its kit.name tags).
---@opt lights: boolean = true — Place lights in every zone and link.
---@opt strict: boolean = false — Refuse to build when any design rule is violated (errors always refuse).
---@opt force: boolean = false — Build even with check errors that do not stop the geometry (use with care).
---@opt populate: function = none — Called as populate(zone_report, zone, graph) after each zone is built: the hook for a population pass (dressing, items, spawns) per zone (theme and density in zone.dress).
function kit.build_layout(t)
  local api = "kit.build_layout"
  check_keys(api, t, { "graph", "name", "lights", "strict", "force", "populate" })
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
  local report = { kind = "layout_report", name = gname, zones = {}, links = {}, objectives = {}, warnings = {}, violations = V, spawns = 0 }
  for _, m in ipairs(rules) do report.warnings[#report.warnings + 1] = m end

  level.props:set("multiplayer", true)
  local Rs = {}
  for _, z in ipairs(G.zones) do Rs[#Rs + 1] = zone_ctx(gname, z, plan) end
  -- The cart lane through payload yards and roads (cover keeps clear of it).
  local cart = nil
  for _, o in ipairs(G.objectives or {}) do if o.kind == "cart" then cart = o end end
  local byid = {}
  for _, R in ipairs(Rs) do byid[R.id] = R end
  local cart_pts = nil
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
    local function mid(p) return (p.lo + p.hi) / 2 end
    for i, id in ipairs(spine) do
      local R = byid[id]
      if R then
        local entry, exit = cart_port(R, spine[i - 1]), cart_port(R, spine[i + 1])
        R.lane = {}
        if R.z.archetype == "road" then
          local F = road_frame(R.z)
          local ys, yn = R.y, R.y + F.rise
          for _, e in ipairs({ { 1, ys }, { F.ls, ys }, { F.len - F.ln, yn }, { F.len - 1, yn } }) do
            local x, z = F.xz(e[1], F.centre)
            cart_pts[#cart_pts + 1] = { x, e[2], z }
          end
          local ax, az = F.xz(0, F.centre)
          local bx, bz = F.xz(F.len, F.centre)
          R.lane[1] = { ax, az, bx, bz, 3.2 }
        elseif R.z.archetype == "finale" then
          local F = zone_frame(R, heading_of(R.z))
          local ex, ez, wx, wz
          if entry then
            ex, ez = wall_pt(R, entry.side, mid(entry), 2)
            wx, wz = wall_pt(R, entry.side, mid(entry), 0)
          else
            ex, ez = F.w(0, -F.hd + 2)
            wx, wz = F.w(0, -F.hd)
          end
          local lx = F.l(ex, ez)
          R.cart_c = lx
          local ey = R.y
          local cxp = clamp(lx, -7 + 4.5, 7 - 4.5)
          local pz0 = -7 + 2
          cart_pts[#cart_pts + 1] = { ex, ey, ez }
          local x, z = F.w(cxp, pz0)
          cart_pts[#cart_pts + 1] = { x, ey, z }
          R.lane[1] = { wx, wz, x, z, 5 }
          x, z = F.w(cxp, pz0 + 8)
          cart_pts[#cart_pts + 1] = { x, ey - 2.5, z }
          x, z = F.w(0, 2)
          cart_pts[#cart_pts + 1] = { x, ey - 2.5, z }
          R.checkpoint_at = #cart_pts
        else
          -- Through the zone's centre: in at the entry port, out at the exit port (a turn when they are not opposite).
          local ey = R.y
          local ex, ez, ewx, ewz, xx, xz, xwx, xwz
          if entry then
            ex, ez = wall_pt(R, entry.side, mid(entry), 2)
            ewx, ewz = wall_pt(R, entry.side, mid(entry), 0)
          elseif exit then
            local m = (exit.side == "+x" or exit.side == "-x") and R.cz or R.cx
            ex, ez = wall_pt(R, OPP[exit.side], m, 2)
            ewx, ewz = wall_pt(R, OPP[exit.side], m, 0)
          else
            ex, ez = wall_pt(R, "-z", R.cx, 2)
            ewx, ewz = wall_pt(R, "-z", R.cx, 0)
          end
          if exit then
            xx, xz = wall_pt(R, exit.side, mid(exit), 2)
            xwx, xwz = wall_pt(R, exit.side, mid(exit), 0)
          else
            local side = entry and OPP[entry.side] or "+z"
            local m = entry and mid(entry) or R.cx
            xx, xz = wall_pt(R, side, m, 2)
            xwx, xwz = wall_pt(R, side, m, 0)
          end
          cart_pts[#cart_pts + 1] = { ex, ey, ez }
          cart_pts[#cart_pts + 1] = { R.cx, ey, R.cz }
          R.checkpoint_at = #cart_pts
          cart_pts[#cart_pts + 1] = { xx, ey, xz }
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
      local a, b, c, d = rect_of(z)
      x0, z0, x1, z1 = math.min(x0, a), math.min(z0, b), math.max(x1, c), math.max(z1, d)
      y0, y1 = math.min(y0, z.pos[2]), math.max(y1, z.pos[2] + (z.levels[#z.levels] or 0))
    end
    void = hull("layout", gname .. "_void", "void", box_points(x0 - 30, y0 - DATA.open_air_void_depth, z0 - 30, x1 + 30, y1 + 40, z1 + 30),
      { air = true, texture = kit.defaults.sky_texture })
    void:box_map{ tile = 64 }
    for _, f in ipairs(void.faces) do f:set_flag("show_sky", true) end
  end
  -- 2. Zone shells.
  for _, R in ipairs(Rs) do
    local rec = RECIPES[R.z.archetype]
    rec.shell(R)
  end
  -- 3. Link air.
  local airs = {}
  for _, P in ipairs(plan.links) do if P.c then build_link_air(P, gname, airs, open_air) end end
  -- The sky box (after the shells and links, so it lands beyond them).
  local needs_sky = open_air
  for _, R in ipairs(Rs) do if R.sky then needs_sky = true end end
  for _, P in ipairs(plan.links) do if (P.link.type == "road" or P.link.type == "bridge") and not P.link.tunnel then needs_sky = true end end
  if needs_sky then I.ensure_sky(gname) end
  -- 4. Zone interiors.
  for _, R in ipairs(Rs) do
    local rec = RECIPES[R.z.archetype]
    if rec.inner then rec.inner(R) end
  end
  -- 5. Link solids.
  for _, P in ipairs(plan.links) do
    if P.c then
      for _, m in ipairs(build_link_solid(P, gname, open_air)) do
        local _ = m
      end
    end
  end
  -- 6. Details.
  for _, R in ipairs(Rs) do
    local rec = RECIPES[R.z.archetype]
    if rec.late then rec.late(R) end
  end
  -- 7. Lights.
  local nlights = 0
  if want_lights then
    for _, R in ipairs(Rs) do
      local rec = RECIPES[R.z.archetype]
      local h = rec.light_h and rec.light_h(R) or (R.h - 1)
      local poly = (R.room and R.room.outline) or R.outline or rect_poly(R.x0, R.z0, R.x1, R.z1)
      local inset = I.inset_convex(poly, 1.5) or poly
      local spacing = (R.A.open_air and 7) or ((R.w * R.d > 500) and 8 or 6.5)
      local floor_y = R.y
      local lights = SP.plan_lights(R.name, inset, floor_y, h, spacing, nil)
      nlights = nlights + #lights
    end
    for _, P in ipairs(plan.links) do if P.c and not open_air then nlights = nlights + #link_lights(P, gname, nil) end end
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
          local sp = level.place{ kind = "respawn", name = gname .. "_" .. (o.id or "s") .. "_" .. i, pos = { s[1], s[2] + kit.defaults.spawn_height, s[3] }, props = props,
            tags = { kit = "layout", ["kit.name"] = R.name } }
          -- face along a route out: toward the nearest port at the spawn's level, else the zone centre
          local tx, tz = R.cx, R.cz
          local bd = math.huge
          for _, p in ipairs(R.ports) do
            if math.abs(p.floor - s[2]) < 1 then
              local px, pz = nil, nil
              local m = (p.lo + p.hi) / 2
              if p.side == "+x" or p.side == "-x" then px, pz = p.wall, m else px, pz = m, p.wall end
              local d = (px - s[1]) ^ 2 + (pz - s[3]) ^ 2
              if d < bd and d > 9 then bd, tx, tz = d, px, pz end
            end
          end
          if math.abs(tx - s[1]) + math.abs(tz - s[3]) > 0.1 then sp:look_at(tx, s[2] + kit.defaults.spawn_height, tz) end
          list[#list + 1] = sp
          first_spawn = first_spawn or { s[1], s[2], s[3], tx, tz }
        end
        spawn_objs[o.id or ("s" .. #spawn_objs)] = list
        report.spawns = report.spawns + #list
        report.objectives[#report.objectives + 1] = { kind = "spawns", id = o.id, zone = o.zone, team = o.team, count = #list }
      end
    end
  end
  if first_spawn then
    local ps = level.player_start{ pos = { first_spawn[1], first_spawn[2] + kit.defaults.spawn_height, first_spawn[3] }, yaw = 0 }
    if math.abs(first_spawn[4] - first_spawn[1]) + math.abs(first_spawn[5] - first_spawn[3]) > 0.1 then
      ps:look_at(first_spawn[4], first_spawn[2] + kit.defaults.spawn_height, first_spawn[5])
    end
  end
  for _, o in ipairs(G.objectives or {}) do
    local R = byid[o.zone or ""]
    if o.kind == "flag" and R then
      local s = R.objective_spot or { R.cx, R.y, R.cz }
      local it = level.place{ kind = "item", class = o.team == "red" and "flag_red" or "flag_blue", name = gname .. "_flag_" .. o.team,
        pos = { s[1], s[2] + kit.defaults.item_height, s[3] }, tags = { kit = "layout", ["kit.name"] = R.name } }
      report.objectives[#report.objectives + 1] = { kind = "flag", team = o.team, zone = o.zone, uid = it.uid, pos = it.pos }
    elseif o.kind == "hill" and R then
      local s = R.objective_spot or { R.cx, R.y, R.cz }
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
    for _, b in ipairs(R.brushes) do b:tag("kit.zone", R.name) end
    local zr = { id = R.id, archetype = R.z.archetype, role = R.z.role, name = R.name, min = vec(R.x0, R.y, R.z0), max = vec(R.x1, R.y + R.h, R.z1),
      floor_y = R.y, levels = copy_list(R.z.levels), brushes = #R.brushes, spots = R.spots, dress = R.z.dress, sky = R.sky or false,
      ports = #R.ports, warnings = R.warnings, room = R.room }
    for _, w in ipairs(R.warnings) do report.warnings[#report.warnings + 1] = w end
    report.zones[#report.zones + 1] = zr
    if t.populate then t.populate(zr, R.z, G) end
  end
  for _, P in ipairs(plan.links) do
    report.links[#report.links + 1] = { id = P.link.id, type = P.link.type, a = P.link.a, b = P.link.b, width = P.W, offset = P.c }
  end
  report.lights = nlights
  report.area = layout_area(G, plan)
  return report
end

end

return install
