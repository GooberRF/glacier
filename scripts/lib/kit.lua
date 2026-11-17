-- Glacier design kit (ged.import("kit")).
--
-- Small, composable level-building functions over the scripting API. Everything the kit emits is
-- ordinary, hand-editable editor content: primitive brushes tagged kit / kit.name / kit.part, named
-- objects, and named groups for multi-brush parts. The kit has no special engine support.
--
-- Ground rules (also in ged.conventions):
--   * The world starts solid. A room is an AIR brush; a solid placed inside air adds geometry.
--   * CSG is strictly time-ordered: later air carves earlier solid, later solid fills earlier air.
--     Every kit brush goes at the end of time, in the order each function lists, so build the air a
--     solid stands in BEFORE the solid.
--   * Air brushes that touch merge: two rooms sharing a face are open to each other. Keep at least
--     `kit.defaults.wall` metres of solid between rooms that should stay separate. Kit openings abut
--     the rooms exactly (no overlap), so they stay grid-clean and easy to edit by hand.
--   * Floor-anchored: every kit `pos` is a point ON THE FLOOR (x, z = footprint centre unless stated,
--     y = floor height). size = {w, h, d} is the interior clear size along X, Y, Z (metres).
--   * Directions are the strings "+x", "-x", "+z", "-z".
--   * Each function takes one table, rejects unknown keys, returns a plain record table, and never
--     uses rng (same script + same level = same result).
--
-- To customise the kit, copy this file to <scripts>/lib/mykit.lua and ged.import("mykit"). The name
-- "kit" itself always loads the copy built into Glacier.
--
-- This module only defines functions and constants at its top level (a module's top level runs once,
-- the first time it is imported).

local kit = {}

kit.version = 3

-- Tunable defaults. Edit fields on this table to change every later call, e.g.
--   kit.defaults.corridor_width = 3
kit.defaults = {
  -- Solid left between rooms that should stay separate (tunable).
  wall = 1.0,
  -- Sizes marked [norm] follow the corpus norms' reference bucket (catalog.norms(metric).p50, rounded;
  -- `glacier-cli norms --metric <id>`), pinned by the KitDefaultsFollowNorms test.
  -- Corridor clear width / height [norm corridor.width p50 of the multiplayer exemplars (gametype dm), to 0.5 m;
  -- corridor.height p50, to 0.5 m].
  corridor_width = 4.5,
  corridor_height = 4.5,
  -- Door clear width / height. Width [norm opening.width p50, to 0.25 m] is clamped up to the player
  -- collision diameter 1.8 m (entity.tbl miner1 "$Collision Radius: 0.90") plus 2 x 0.5 m wall
  -- clearance (Alpine Faction game_patch/misc/waypoints.h:46, kWaypointGenerateWallClearance), so it
  -- stays 3. Height [norm portal.height p50, to 0.25 m; inside opening.height's p25-p75] is >= the
  -- standing headroom 2.0 m (waypoints.h:49, kWaypointGenerateStandingHeadroom). Tunable.
  door_width = 3,
  door_height = 4,
  -- Stair step (26.6 degree pitch). Tunable within the hard limits in kit.limits.
  riser = 0.25,
  tread = 0.5,
  -- Ramp rise/run (tunable; hard cap kit.limits.max_slope).
  ramp_slope = 0.5,
  -- A new level's Player Start sits 1 m above the floor (LevelTemplates).
  spawn_height = 1.0,
  -- Spawn inset from each wall: > collision radius 0.9 + wall clearance 0.5 (tunable).
  spawn_margin = 1.5,
  -- Height above the floor for items (tunable).
  item_height = 0.5,
  -- Light grid spacing in metres [norm: sqrt(100 / lights.per_100m2 p50), to 1 m] (tunable).
  light_spacing = 5,
  -- The editor's new-Light blueprint (ObjectFactory, Light): colour and range.
  light_color = { 255, 240, 200 },
  light_range_min = 10,
  -- Square pillar / platform-leg side (tunable).
  pillar_size = 1.0,
  -- Door slab thickness (m), travel time (s) and open pause (s) (tunable).
  door_thickness = 0.25,
  door_travel = 1.0,
  door_pause = 3.0,
  -- Every kit part without its own texture / textures gets these (one texture, or textures by role:
  -- floor / wall / ceiling): stock concrete, so nothing falls back to the editor's placeholder grid.
  -- Set texture (one for every face) or replace textures to restyle a whole layout.
  texture = nil,
  textures = { floor = "cem_dark_floor01.tga", wall = "cem_bunker04.tga", ceiling = "cem_cementflr01.tga" },
  -- Box-map every kit brush in world space after creation so textures tile continuously across
  -- neighbouring brushes (RED's world-aligned projection), at ppm pixels per metre of each texture's
  -- own size (the mounted library's; 256 px when unknown): 64 is RED's density on stock levels (32, 64
  -- and 128 px textures all map at 64 px/m) and gives a 256 px texture the exemplars' 4 m tile
  -- [norm texel.tile_metres p50 = 4 = 256 px / 64 px/m]. Set ppm = nil to map every texture at
  -- tile_metres per repeat instead (4 = a newly drawn brush's tile, GeometryUtil.DefaultTileMetres).
  box_map = true,
  tile_metres = 4,
  ppm = 64,
  -- Then line every kit part's textures up like a mapper would (level.uv_align, at ppm): walls stand on
  -- their floor line, wrap around corners and fit their height to whole repeats, slopes and rock map at
  -- true length, nothing is mirrored. Each part is aligned as it is built, and kit.build_layout aligns
  -- the whole layout at the end so textures run on across neighbouring parts. false = box map only.
  uv_align = true,
  -- The texture of invisible collision brushes (the stairs' ramp): any *_invisible* texture is
  -- compiled invisible (sld_invisible01, rck_invisible04, mtl_invisible02, cem_invisible03 are stock).
  invisible_texture = "sld_invisible01.tga",
  -- kit.sky_room: the sky box texture (a stock sky, as in dm04) and how far beyond the level's +X edge
  -- the box goes when no pos is given.
  sky_texture = "Sky_Blu01_01.tga",
  sky_offset = 64,
  -- Clearance (m) a kit light keeps from any solid brush standing in its room.
  light_clearance = 0.25,
  -- Organic spaces (kit.cave, kit.canyon) and rock dressing (tunable): cave half-width and crown
  -- height, canyon floor width and rim height above its highest floor, and wall roughness (a fraction
  -- of the size).
  cave_radius = 3,
  cave_height = 4.5,
  canyon_width = 12,
  canyon_height = 16,
  roughness = 0.2,
  -- Textures and tile of the organic functions (caves, canyons, rock walls, boulders, pits): stock
  -- rock by role; rock_texture (one for every face) wins when set; nil both = kit.defaults.texture(s).
  -- The kit's own density (ppm, else tile_metres) applies. Rock tiles like every other surface.
  rock_texture = nil,
  rock_textures = { floor = "rck_cavernflr01.tga", wall = "rck_canyon_rock03.tga", ceiling = "rck_coal01.tga" },
  rock_tile_metres = nil,
  -- The organic rock budget (kit_rock.lua): a level holds at most 32,767 lightmap surfaces (Alpine RED's limit) and
  -- every rock facet a player sees takes one, so the organic builders (kit.trace's rock, kit.rock_mass) share one
  -- level-wide budget of seen facets. Rock keeps full facets in the view band (the foot zone plus organic_view metres
  -- over its floor) and is cut organic_far times coarser above it (1.5 x that from 8 m higher); a call whose rock
  -- would not fit its share of what is left coarsens its far rock further. nil or false = no budget (full facets).
  organic_budget = 14000,
  organic_view = 5,
  organic_far = 3,
}

-- Hard limits the kit enforces (not tunables).
kit.limits = {
  -- Player collision diameter: entity.tbl miner1 "$Collision Radius: 0.90" (x2).
  min_clear_width = 1.8,
  -- Standing headroom: Alpine Faction game_patch/misc/waypoints.h:49.
  min_headroom = 2.0,
  -- Highest single step a walk sample takes: Alpine Faction waypoints.h:45 (kWalkDirectMaxStep).
  max_riser = 1.0,
  -- 45 degree maximum incline (rise/run 1): Alpine Faction waypoints.h:37.
  max_slope = 1.0,
}

-- ---------------------------------------------------------------------------------------------
-- Internal helpers
-- ---------------------------------------------------------------------------------------------

local EPS = 1e-4

local counters = {}

local DIRS = {
  ["+x"] = { 1, 0 },
  ["-x"] = { -1, 0 },
  ["+z"] = { 0, 1 },
  ["-z"] = { 0, -1 },
}

-- Misuse is an API error: reported at the line of the calling script, with the hint in its own field.
local function fail(api, message, hint)
  ged.fail(api .. ": " .. message, hint)
end

local function edit_distance(a, b)
  local la, lb = #a, #b
  local prev = {}
  for j = 0, lb do prev[j] = j end
  for i = 1, la do
    local cur = { [0] = i }
    for j = 1, lb do
      local cost = (a:sub(i, i) == b:sub(j, j)) and 0 or 1
      cur[j] = math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
    end
    prev = cur
  end
  return prev[lb]
end

local function nearest(input, candidates)
  local best, best_d = nil, math.huge
  for _, c in ipairs(candidates) do
    local d = edit_distance(input:lower(), c)
    if c:sub(1, #input) == input:lower() then d = 0 end
    if d < best_d or (d == best_d and c < best) then best, best_d = c, d end
  end
  if best and best_d <= math.max(2, math.floor(#input / 3)) then return best end
  return nil
end

-- Strict table args (mirrors the API's own rule): an unknown key is an error naming the nearest one.
local function check_keys(api, t, allowed)
  if type(t) ~= "table" then
    fail(api, "expects one table argument, e.g. " .. api .. "{ ... }.")
  end
  local set = {}
  for _, k in ipairs(allowed) do set[k] = true end
  local unknown = {}
  for k in pairs(t) do
    if type(k) ~= "string" then
      fail(api, "takes named options only (found a positional value).", "Valid options: " .. table.concat(allowed, ", ") .. ".")
    end
    if not set[k] then unknown[#unknown + 1] = k end
  end
  if #unknown > 0 then
    table.sort(unknown)
    local sorted = {}
    for i, k in ipairs(allowed) do sorted[i] = k end
    table.sort(sorted)
    local near = nearest(unknown[1], sorted)
    fail(api, "unknown option '" .. unknown[1] .. "'.",
      (near and ("Did you mean '" .. near .. "'? ") or "") .. "Valid options: " .. table.concat(sorted, ", ") .. ".")
  end
end

local function is_finite(n)
  return type(n) == "number" and n == n and n ~= math.huge and n ~= -math.huge
end

local function number(api, key, v, default)
  if v == nil then
    if default == nil then fail(api, "'" .. key .. "' is required.") end
    return default
  end
  if not is_finite(v) then
    fail(api, "'" .. key .. "' must be a finite number (got " .. tostring(v) .. ").")
  end
  return v
end

local function positive(api, key, v, default)
  local n = number(api, key, v, default)
  if n <= 0 then fail(api, "'" .. key .. "' must be greater than 0 (got " .. tostring(n) .. ").") end
  return n
end

local function boolean(api, key, v, default)
  if v == nil then return default end
  if type(v) ~= "boolean" then fail(api, "'" .. key .. "' must be true or false (got " .. type(v) .. ").") end
  return v
end

-- A point/size: a vec, {x, y, z} or {x=, y=, z=}. Returns three numbers.
local function vec3(api, key, v)
  if v == nil then fail(api, "'" .. key .. "' is required (a point {x, y, z}).") end
  local x, y, z
  if type(v) == "userdata" then
    x, y, z = v.x, v.y, v.z
  elseif type(v) == "table" then
    if v[1] ~= nil then x, y, z = v[1], v[2], v[3] else x, y, z = v.x, v.y, v.z end
  end
  if not (is_finite(x) and is_finite(y) and is_finite(z)) then
    fail(api, "'" .. key .. "' must be a vec, {x, y, z} or {x=, y=, z=} of finite numbers.")
  end
  return x, y, z
end

local function vec(x, y, z) return ged.vec(x, y, z) end

local function direction(api, key, v)
  local d = DIRS[v]
  if not d then
    fail(api, "'" .. key .. "' must be one of \"+x\", \"-x\", \"+z\", \"-z\" (got " .. tostring(v) .. ").")
  end
  return d[1], d[2]
end

-- True when a name is already used by kit content, a group or a mover in the level (an earlier
-- script's room1, say), so a defaulted name never collides with one from another run.
local function name_taken(n)
  return level.brushes_tagged("kit.name", n).count > 0 or level.objects_tagged("kit.name", n).count > 0
    or level.find_group(n) ~= nil or level.find_mover(n) ~= nil
end

local function name_for(api, fn, name)
  if name == nil then
    local candidate
    repeat
      counters[fn] = (counters[fn] or 0) + 1
      candidate = fn .. counters[fn]
    until not name_taken(candidate)
    return candidate
  end
  if type(name) ~= "string" or name == "" then fail(api, "'name' must be a non-empty string.") end
  return name
end

local function check_walkable(api, what, width, height, allow_tight)
  if allow_tight then return end
  if width and width < kit.limits.min_clear_width - EPS then
    fail(api, what .. " width " .. width .. " m is below the " .. kit.limits.min_clear_width ..
      " m player collision diameter (entity.tbl miner1 $Collision Radius 0.90).", "Widen it, or pass allow_tight = true.")
  end
  if height and height < kit.limits.min_headroom - EPS then
    fail(api, what .. " height " .. height .. " m is below the " .. kit.limits.min_headroom ..
      " m standing headroom (Alpine waypoints.h:49).", "Raise it, or pass allow_tight = true.")
  end
end

local function textures_of(api, t)
  if t.texture ~= nil and t.textures ~= nil then
    fail(api, "give either 'texture' (every face) or 'textures' (by role), not both.")
  end
  if t.texture ~= nil then return t.texture, nil end
  if t.textures ~= nil then return nil, t.textures end
  return kit.defaults.texture, kit.defaults.textures
end

-- The intent layer: light fixtures and their lights carry their role (kit.role = "fixture") and what they light.
local FIXTURE_FNS = { light_room = true, light_path = true }

-- The purpose helpers kit_struct.lua installs (purpose_mode, tag_role, stair_route), read at call time.
local internals_purpose = nil

-- Depth of the composite builders running (kit.space, kit.build_layout): the flights they build as a unit are
-- "quiet" by default (tagged and built; the grader reads them once the whole piece exists).
local composite_depth = 0

-- Emits one tagged brush at the end of time. spec = level.brush options (without tags/textures).
local function emit(fn, name, part, spec, texture, textures)
  local extra = spec.extra_tags
  spec.extra_tags = nil
  spec.tags = { kit = fn, ["kit.name"] = name, ["kit.part"] = part }
  for k, v in pairs(extra or {}) do spec.tags[k] = v end
  if FIXTURE_FNS[fn] then
    spec.tags["kit.role"] = "fixture"
    spec.tags["kit.purpose"] = "lights " .. tostring(name)
  end
  if not spec.portal then
    if texture ~= nil then spec.texture = texture elseif textures ~= nil then spec.textures = textures end
  end
  local b = level.brush(spec)
  if kit.defaults.box_map and not spec.portal then
    if kit.defaults.ppm then b:box_map{ ppm = kit.defaults.ppm } else b:box_map{ tile = kit.defaults.tile_metres } end
    if kit.defaults.uv_align and kit.defaults.ppm then level.uv_align{ brushes = b, ppm = kit.defaults.ppm, keep_fitted = false } end
  end
  return b
end

local function tags_for(fn, name)
  if FIXTURE_FNS[fn] then
    return { kit = fn, ["kit.name"] = name, ["kit.role"] = "fixture", ["kit.purpose"] = "lights " .. tostring(name) }
  end
  return { kit = fn, ["kit.name"] = name }
end

-- An axis-aligned box (min/max corners) as a level.brush pos/size pair.
local function box_spec(x0, y0, z0, x1, y1, z1)
  return { pos = { (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2 }, size = { x1 - x0, y1 - y0, z1 - z0 } }
end

local function group_if_many(name, brushes, wanted)
  if wanted == false or #brushes < 2 then return nil end
  return level.create_group{ name = name, brushes = brushes }
end

-- Reads the box of any kit record (room, corridor, opening): min/max vecs and floor_y.
local function record_box(api, key, r)
  if type(r) ~= "table" or r.min == nil or r.max == nil then
    fail(api, "'" .. key .. "' must be a kit record with min/max (a room, corridor or opening).")
  end
  local x0, y0, z0 = vec3(api, key .. ".min", r.min)
  local x1, y1, z1 = vec3(api, key .. ".max", r.max)
  local floor = r.floor_y or y0
  return { x0 = x0, y0 = y0, z0 = z0, x1 = x1, y1 = y1, z1 = z1, floor = floor, name = r.name or key }
end

local function fmt(n)
  return string.format("%.4g", n)
end

-- The straight a/b connection shared by corridor and doorway: an air box spanning the gap exactly.
local function connect(api, fn, t, width, height)
  local a = record_box(api, "a", t.a)
  local b = record_box(api, "b", t.b)
  local offset = number(api, "offset", t.offset, 0)

  local floor = t.floor_y
  if floor == nil then
    if math.abs(a.floor - b.floor) > EPS then
      fail(api, a.name .. " (floor " .. fmt(a.floor) .. ") and " .. b.name .. " (floor " .. fmt(b.floor) .. ") have different floors.",
        "Use kit.stairs or kit.ramp, or pass floor_y.")
    end
    floor = a.floor
  else
    floor = number(api, "floor_y", floor)
  end

  local sep_x = math.max(b.x0 - a.x1, a.x0 - b.x1)
  local sep_z = math.max(b.z0 - a.z1, a.z0 - b.z1)
  local extents = a.name .. " x[" .. fmt(a.x0) .. ", " .. fmt(a.x1) .. "] z[" .. fmt(a.z0) .. ", " .. fmt(a.z1) .. "], " ..
    b.name .. " x[" .. fmt(b.x0) .. ", " .. fmt(b.x1) .. "] z[" .. fmt(b.z0) .. ", " .. fmt(b.z1) .. "]"

  if (math.abs(sep_x) <= EPS and sep_z <= EPS) or (math.abs(sep_z) <= EPS and sep_x <= EPS) or (sep_x < -EPS and sep_z < -EPS) then
    fail(api, a.name .. " and " .. b.name .. " already touch — touching air brushes are already open to each other.",
      "Move them at least kit.defaults.wall apart, or skip the connection.")
  end
  if sep_x > EPS and sep_z > EPS then
    fail(api, a.name .. " and " .. b.name .. " are not separated along a single axis (" .. extents .. ").",
      "Line them up on x or z, or use kit.corridor{path = {...}} for a bend.")
  end

  local axis = sep_x > EPS and "x" or "z"
  local gap, lo, hi, c0, c1
  if axis == "x" then
    gap = sep_x
    if b.x0 >= a.x1 then lo, hi = a.x1, b.x0 else lo, hi = b.x1, a.x0 end
    c0, c1 = math.max(a.z0, b.z0), math.min(a.z1, b.z1)
  else
    gap = sep_z
    if b.z0 >= a.z1 then lo, hi = a.z1, b.z0 else lo, hi = b.z1, a.z0 end
    c0, c1 = math.max(a.x0, b.x0), math.min(a.x1, b.x1)
  end
  local overlap = c1 - c0
  local across = axis == "x" and "z" or "x"
  if overlap < width - EPS then
    fail(api, a.name .. " and " .. b.name .. " overlap only " .. fmt(math.max(overlap, 0)) .. " m along " .. across ..
      ", less than the " .. fmt(width) .. " m width (" .. extents .. ").",
      "Narrow the opening, line the rooms up along " .. across .. ", or use kit.corridor{path = {...}}.")
  end
  if math.abs(offset) > (overlap - width) / 2 + EPS then
    fail(api, "offset " .. fmt(offset) .. " moves the " .. fmt(width) .. " m opening outside the " .. fmt(overlap) ..
      " m the two sides share along " .. across .. ".", "Keep |offset| <= " .. fmt((overlap - width) / 2) .. ".")
  end
  for _, r in ipairs{ a, b } do
    if floor + height <= r.y0 + EPS or floor >= r.y1 - EPS then
      fail(api, "the opening (y " .. fmt(floor) .. " to " .. fmt(floor + height) .. ") does not reach " .. r.name ..
        " (y " .. fmt(r.y0) .. " to " .. fmt(r.y1) .. ").", "Pass a floor_y inside both rooms' heights.")
    end
  end

  local mid = (c0 + c1) / 2 + offset
  local x0, x1, z0, z1
  if axis == "x" then
    x0, x1, z0, z1 = lo, hi, mid - width / 2, mid + width / 2
  else
    x0, x1, z0, z1 = mid - width / 2, mid + width / 2, lo, hi
  end
  local towards_b
  if axis == "x" then towards_b = (b.x0 >= a.x1) and "+x" or "-x" else towards_b = (b.z0 >= a.z1) and "+z" or "-z" end
  return {
    axis = axis, dir = towards_b, length = gap, floor = floor,
    x0 = x0, x1 = x1, z0 = z0, z1 = z1, y0 = floor, y1 = floor + height,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Public functions
-- ---------------------------------------------------------------------------------------------

---@kit kit.room{name?, pos, size, texture?|textures?, detail?} → room — One air box (the room's clear interior), floor at pos.y; returns {brush, min, max, center, floor_y}.
---@opt name: string = room<n> — Names the room (tags, and the default name of its lights and spawns); a default skips names already in the level.
---@opt pos: vec — Required: the floor point at the centre of the footprint (metres).
---@opt size: vec — Required: interior clear size {w, h, d} in metres, each > 0; leave h out ({w, nil, d}) for the kit.proportion height of the footprint.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt detail: boolean = false — Make the air brush detail.
function kit.room(t)
  local api = "kit.room"
  check_keys(api, t, { "name", "pos", "size", "texture", "textures", "detail" })
  local name = name_for(api, "room", t.name)
  local x, y, z = vec3(api, "pos", t.pos)
  local w, h, d
  local s = t.size
  if type(s) == "table" and type(s[1]) == "number" and s[2] == nil and type(s[3]) == "number" then
    w, d = s[1], s[3]
    h = kit.proportion{ size = { w, d } }.height -- exemplar proportions by footprint (kit_space)
  else
    w, h, d = vec3(api, "size", t.size)
  end
  if w <= 0 or h <= 0 or d <= 0 then fail(api, "'size' components must be greater than 0.") end
  local texture, textures = textures_of(api, t)
  local brush = emit("room", name, "air", {
    shape = "box", pos = { x, y + h / 2, z }, size = { w, h, d }, air = true,
    detail = boolean(api, "detail", t.detail, false),
  }, texture, textures)
  return {
    kind = "room", name = name, brush = brush,
    pos = vec(x, y, z), size = vec(w, h, d), floor_y = y,
    min = vec(x - w / 2, y, z - d / 2), max = vec(x + w / 2, y + h, z + d / 2),
    center = vec(x, y + h / 2, z),
  }
end

---@kit kit.corridor{name?, a, b, width?, height?, offset?, floor_y?} | {name?, path, width?, height?} (+ texture?|textures?, detail?, allow_tight?) → corridor — Air box(es) joining two records across their gap, or along an axis-aligned floor path.
---@opt name: string = corridor<n> — Names the corridor (tags).
---@opt a: record — One end: a room, corridor or opening record (give a and b, or path).
---@opt b: record — The other end; separated from a along x or z by solid.
---@opt path: vec[] — Floor points (at least 2, one height, each step along x or z only) for a corridor with bends.
---@opt width: number = kit.defaults.corridor_width (4.5) — Clear width in metres (at least 1.8 unless allow_tight).
---@opt height: number = kit.defaults.corridor_height (4.5) — Clear height in metres (at least 2 unless allow_tight).
---@opt offset: number = 0 — Shift across the shared span, metres (a/b form only).
---@opt floor_y: number = the rooms' common floor — Floor height of the corridor (a/b form only).
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt allow_tight: boolean = false — Allow a width or height below the player's size.
---@opt detail: boolean = false — Make the air brushes detail.
function kit.corridor(t)
  local api = "kit.corridor"
  check_keys(api, t, { "name", "a", "b", "path", "width", "height", "offset", "floor_y", "texture", "textures", "allow_tight", "detail" })
  local name = name_for(api, "corridor", t.name)
  local width = positive(api, "width", t.width, kit.defaults.corridor_width)
  local height = positive(api, "height", t.height, kit.defaults.corridor_height)
  check_walkable(api, "corridor", width, height, t.allow_tight)
  local texture, textures = textures_of(api, t)
  local detail = boolean(api, "detail", t.detail, false)

  if t.path ~= nil then
    if t.a ~= nil or t.b ~= nil or t.offset ~= nil or t.floor_y ~= nil then
      fail(api, "give either a/b (straight connection) or path (floor points), not both.")
    end
    if type(t.path) ~= "table" or #t.path < 2 then fail(api, "'path' must list at least 2 floor points.") end
    local pts = {}
    for i, p in ipairs(t.path) do
      local x, y, z = vec3(api, "path[" .. i .. "]", p)
      pts[i] = { x = x, y = y, z = z }
    end
    local brushes = {}
    local n = #pts - 1
    local gx0, gy0, gz0, gx1, gy1, gz1 = math.huge, math.huge, math.huge, -math.huge, -math.huge, -math.huge
    for i = 1, n do
      local p, q = pts[i], pts[i + 1]
      if math.abs(p.y - q.y) > EPS then
        fail(api, "path points " .. i .. " and " .. (i + 1) .. " are at different heights.", "Use kit.stairs or kit.ramp for a climb.")
      end
      local dx, dz = q.x - p.x, q.z - p.z
      if (math.abs(dx) > EPS) == (math.abs(dz) > EPS) then
        fail(api, "path points " .. i .. " and " .. (i + 1) .. " must differ on exactly one of x or z.",
          "Add a corner point to make the bend explicit, or use kit.passage{path = …} for a corridor on any heading.")
      end
      -- Interior joints extend by width/2 so the two segment boxes fill the corner; path ends do not.
      local ext0 = (i > 1) and width / 2 or 0
      local ext1 = (i < n) and width / 2 or 0
      local x0, x1, z0, z1
      if math.abs(dx) > EPS then
        local s = dx > 0 and 1 or -1
        local a0, a1 = p.x - s * ext0, q.x + s * ext1
        x0, x1 = math.min(a0, a1), math.max(a0, a1)
        z0, z1 = p.z - width / 2, p.z + width / 2
      else
        local s = dz > 0 and 1 or -1
        local a0, a1 = p.z - s * ext0, q.z + s * ext1
        z0, z1 = math.min(a0, a1), math.max(a0, a1)
        x0, x1 = p.x - width / 2, p.x + width / 2
      end
      local bs = box_spec(x0, p.y, z0, x1, p.y + height, z1)
      brushes[i] = emit("corridor", name, "seg" .. i, {
        shape = "box", pos = bs.pos, size = bs.size, air = true, detail = detail,
      }, texture, textures)
      gx0, gy0, gz0 = math.min(gx0, x0), math.min(gy0, p.y), math.min(gz0, z0)
      gx1, gy1, gz1 = math.max(gx1, x1), math.max(gy1, p.y + height), math.max(gz1, z1)
    end
    return {
      kind = "corridor", name = name, brushes = brushes, axis = nil,
      min = vec(gx0, gy0, gz0), max = vec(gx1, gy1, gz1), floor_y = pts[1].y, width = width, height = height,
    }
  end

  if t.a == nil or t.b == nil then fail(api, "give a and b (two rooms or openings), or path = {p1, p2, ...}.") end
  local c = connect(api, "corridor", t, width, height)
  local bs = box_spec(c.x0, c.y0, c.z0, c.x1, c.y1, c.z1)
  local brush = emit("corridor", name, "seg1", {
    shape = "box", pos = bs.pos, size = bs.size, air = true, detail = detail,
  }, texture, textures)
  return {
    kind = "corridor", name = name, brushes = { brush }, brush = brush, axis = c.axis, dir = c.dir,
    min = vec(c.x0, c.y0, c.z0), max = vec(c.x1, c.y1, c.z1),
    center = vec((c.x0 + c.x1) / 2, c.floor + height / 2, (c.z0 + c.z1) / 2),
    floor_y = c.floor, width = width, height = height, length = c.length, a = t.a, b = t.b,
  }
end

---@kit kit.doorway{name?, a, b, width?, height?, offset?, floor_y?, portal?, texture?|textures?, allow_tight?} → opening — A door-sized air opening through the wall between two records (abutting both); portal = true makes the opening an air portal brush (two portals, the doorway a small room between them).
---@opt name: string = doorway<n> — Names the opening (tags).
---@opt a: record — Required: the room (or corridor/opening record) on one side of the wall.
---@opt b: record — Required: the record on the other side.
---@opt width: number = kit.defaults.door_width (3) — Clear width in metres.
---@opt height: number = kit.defaults.door_height (4) — Clear height in metres.
---@opt offset: number = 0 — Shift across the shared span, metres.
---@opt floor_y: number = the rooms' common floor — Floor height of the opening.
---@opt portal: boolean = false — Make the opening an air portal brush that cuts the wall itself: the two walls it opens become portals and the doorway a small room between the two sides (RED's portal construct; splits the level for visibility — use it where a kit.door goes).
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt allow_tight: boolean = false — Allow a width or height below the player's size.
function kit.doorway(t)
  local api = "kit.doorway"
  check_keys(api, t, { "name", "a", "b", "width", "height", "offset", "floor_y", "portal", "texture", "textures", "allow_tight" })
  local name = name_for(api, "doorway", t.name)
  local width = positive(api, "width", t.width, kit.defaults.door_width)
  local height = positive(api, "height", t.height, kit.defaults.door_height)
  check_walkable(api, "doorway", width, height, t.allow_tight)
  if t.a == nil or t.b == nil then fail(api, "'a' and 'b' are required (the two rooms the doorway joins).") end
  local texture, textures = textures_of(api, t)
  local c = connect(api, "doorway", t, width, height)
  local bs = box_spec(c.x0, c.y0, c.z0, c.x1, c.y1, c.z1)
  local brush = emit("doorway", name, "opening", {
    shape = "box", pos = bs.pos, size = bs.size, air = true,
  }, texture, textures)

  local cx, cy, cz = (c.x0 + c.x1) / 2, c.floor + height / 2, (c.z0 + c.z1) / 2
  local portal_brush
  if boolean(api, "portal", t.portal, false) then
    -- The opening itself becomes the air portal brush: RED builds a portal from an air portal brush only where it
    -- cuts through solid (the two wall faces it opens become the portals, the opening between them a room of its
    -- own), and none from a slab placed in an opening that is already carved (compiler-parity-notes.md "Air portal
    -- brushes and inside-out rooms", rule 1). Its side faces are the doorway's jambs, so it keeps the textures.
    brush:set_portal(true)
    portal_brush = brush
  end
  return {
    kind = "opening", name = name, brush = brush, portal_brush = portal_brush, axis = c.axis, dir = c.dir,
    min = vec(c.x0, c.y0, c.z0), max = vec(c.x1, c.y1, c.z1), center = vec(cx, cy, cz),
    floor_y = c.floor, width = width, height = height, length = c.length, a = t.a, b = t.b,
  }
end

-- Palettes, trims and slope-aligned UVs (scripts/lib/kit_uv.lua), bound at the end of this file.
local uv

local function envelope(api, fn, name, x, y, z, dx, dz, width, run, rise, headroom, texture, textures)
  local hw = width / 2
  local x0, x1, z0, z1
  if dx ~= 0 then
    x0, x1 = math.min(x, x + dx * run), math.max(x, x + dx * run)
    z0, z1 = z - hw, z + hw
  else
    z0, z1 = math.min(z, z + dz * run), math.max(z, z + dz * run)
    x0, x1 = x - hw, x + hw
  end
  local bs = box_spec(x0, y, z0, x1, y + rise + headroom, z1)
  return emit(fn, name, "envelope", { shape = "box", pos = bs.pos, size = bs.size, air = true }, texture, textures)
end

---@kit kit.stairs{name?, pos, dir, rise, width?, riser?, tread?, carve?, headroom?, detail?, group?, texture?|textures?, allow_tight?, collision_ramp?, purpose?} → stairs — Full-height solid steps from the floor point pos climbing toward dir, with an invisible collision ramp over them; carve = true first cuts an air envelope through solid. A flight must serve a route: before building, it is refused (with the reason) when its top climbs into a wall or solid, when an end that has floor lacks 1 m clear to step on or off, when the floors are already joined on foot nearby (a walk under 1.5x its length + 4 m), or when an end leads on to under 16 m2 of floor (purpose); an end whose floor is not built yet is noted, not judged. Every brush is tagged kit.role = "route"; the record carries purpose {ok, fault, detour, top_area, foot_area, foot_clear, top_clear}.
---@opt name: string = stairs<n> — Names the stairs (tags, group).
---@opt pos: vec — Required: the floor point at the foot of the stairs, centre of the width.
---@opt dir: string — Required: the climbing direction, "+x", "-x", "+z" or "-z".
---@opt rise: number — Required: total height climbed, a whole number of risers (metres).
---@opt width: number = kit.defaults.corridor_width (4.5) — Width in metres.
---@opt riser: number = kit.defaults.riser (0.25) — Step height in metres (at most 1).
---@opt tread: number = kit.defaults.tread (0.5) — Step depth in metres (riser/tread at most 1).
---@opt carve: boolean = false — First cut an air envelope (rise + headroom high) through solid, for stairs between floors.
---@opt headroom: number = kit.defaults.corridor_height (4.5) — Envelope clearance above the steps (carve only).
---@opt detail: boolean = false — Make the steps detail.
---@opt group: boolean = true — Group the brushes under the stairs' name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt allow_tight: boolean = false — Allow a width or headroom below the player's size.
---@opt collision_ramp: boolean = true — Add an invisible detail ramp over the step edges (kit.defaults.invisible_texture) so players walk up smoothly.
---@opt purpose: string = "require" (inside kit.space and kit.build_layout: "quiet") — "require" refuses a flight that serves no route or whose landing is blocked; "report" warns and builds; "quiet" or "off" builds without measuring (the grader's G30 reads it once built).
function kit.stairs(t)
  local api = "kit.stairs"
  check_keys(api, t, { "name", "pos", "dir", "rise", "width", "riser", "tread", "carve", "headroom", "detail", "group",
    "texture", "textures", "allow_tight", "collision_ramp", "purpose" })
  local name = name_for(api, "stairs", t.name)
  local mode = internals_purpose.purpose_mode(api, t.purpose, composite_depth > 0 and "quiet" or "require")
  local x, y, z = vec3(api, "pos", t.pos)
  local dx, dz = direction(api, "dir", t.dir)
  local rise = positive(api, "rise", t.rise)
  local width = positive(api, "width", t.width, kit.defaults.corridor_width)
  local riser = positive(api, "riser", t.riser, kit.defaults.riser)
  local tread = positive(api, "tread", t.tread, kit.defaults.tread)
  check_walkable(api, "stairs", width, nil, t.allow_tight)
  if riser > kit.limits.max_riser + EPS then
    fail(api, "riser " .. fmt(riser) .. " m is above the " .. kit.limits.max_riser .. " m step limit (Alpine waypoints.h:45).")
  end
  if riser / tread > kit.limits.max_slope + EPS then
    fail(api, "riser/tread " .. fmt(riser / tread) .. " is steeper than 45 degrees (Alpine waypoints.h:37).", "Use a longer tread.")
  end
  local n = math.floor(rise / riser + 0.5)
  if n < 1 or math.abs(n * riser - rise) >= EPS then
    local lo = math.max(1, math.floor(rise / riser)) * riser
    fail(api, "rise " .. fmt(rise) .. " is not a whole number of " .. fmt(riser) .. " m risers.",
      "Use rise = " .. fmt(lo) .. " or " .. fmt(lo + riser) .. ", or change riser.")
  end
  local run = n * tread
  local texture, textures = textures_of(api, t)
  local detail = boolean(api, "detail", t.detail, false)

  -- The route it serves (the intent layer): refused, or warned about, before anything is built.
  local route = nil
  if mode ~= "off" and mode ~= "quiet" then
    route = internals_purpose.stair_route(x, y, z, dx, dz, run, rise, width)
    if not route.ok then
      if mode == "require" then
        fail(api, name .. " serves no route: " .. route.fault .. ".",
          "A flight joins two floors that are otherwise apart, with room to step on and off (spatial.purpose{} lists the grader's reading of every flight); pass purpose = \"report\" to build it anyway.")
      end
      log.warn(api .. ": " .. name .. " serves no route: " .. route.fault .. ".")
    elseif route.pending ~= nil then
      log.info(api .. ": " .. name .. ": " .. route.pending .. ".")
    end
  end

  local brushes = {}
  local env
  if boolean(api, "carve", t.carve, false) then
    local headroom = positive(api, "headroom", t.headroom, kit.defaults.corridor_height)
    check_walkable(api, "stairs headroom", nil, headroom, t.allow_tight)
    env = envelope(api, "stairs", name, x, y, z, dx, dz, width, run, rise, headroom, texture, textures)
    brushes[#brushes + 1] = env
  elseif t.headroom ~= nil then
    fail(api, "'headroom' only applies with carve = true.")
  end

  for i = 1, n do
    local along = (i - 0.5) * tread
    local h = i * riser
    local size = dx ~= 0 and { tread, h, width } or { width, h, tread }
    brushes[#brushes + 1] = emit("stairs", name, "step" .. i, {
      shape = "box", pos = { x + dx * along, y + h / 2, z + dz * along }, size = size, detail = detail,
    }, texture, textures)
  end

  -- The collision ramp: an invisible DETAIL wedge whose slope passes through every step's front top
  -- edge (nosing), from the floor one tread in front of the first riser up to the top step's front
  -- edge, so the player glides up instead of bumping over each step. Detail brushes never seal, so it
  -- cannot open a leak; the invisible texture (the *_invisible* naming rule) keeps it unseen.
  local ramp
  if boolean(api, "collision_ramp", t.collision_ramp, true) then
    local a0, a1 = -tread, (n - 1) * tread
    local mid = (a0 + a1) / 2
    ramp = emit("stairs", name, "ramp", {
      shape = "wedge", pos = { x + dx * mid, y + rise / 2, z + dz * mid }, size = { a1 - a0, rise, width },
      rot = { yaw = math.deg(math.atan2(dz, -dx)) }, detail = true,
    }, kit.defaults.invisible_texture, nil)
    brushes[#brushes + 1] = ramp
  end

  uv.stairs(brushes, t.dir)
  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  internals_purpose.tag_named(name, "route", "a flight " .. fmt(rise) .. " m up from (" .. fmt(x) .. ", " .. fmt(y) .. ", " .. fmt(z) .. ") toward " .. t.dir, mode)
  if route ~= nil then
    route.text = route.ok and (route.pending or "serves a route") or ("serves no route: " .. route.fault)
  end
  return {
    kind = "stairs", name = name, brushes = brushes, envelope = env, group = group, ramp = ramp,
    top = vec(x + dx * run, y + rise, z + dz * run), run = run, steps = n, role = "route", purpose = route,
  }
end

---@kit kit.ramp{name?, pos, dir, rise, run?, width?, carve?, headroom?, detail?, group?, texture?|textures?, allow_tight?, allow_steep?, purpose?} → ramp — One solid wedge from the floor point pos rising toward dir (default slope kit.defaults.ramp_slope, max 45 degrees). Like kit.stairs, a ramp must serve a route (purpose): it is refused when its top climbs into a wall or solid, when an end lacks 1 m clear, when the floors are already joined on foot nearby, or when an end leads nowhere; an end whose floor is not built yet is noted, not judged.
---@opt name: string = ramp<n> — Names the ramp (tags, group).
---@opt pos: vec — Required: the floor point at the foot of the ramp, centre of the width.
---@opt dir: string — Required: the rising direction, "+x", "-x", "+z" or "-z".
---@opt rise: number — Required: height gained in metres.
---@opt run: number = rise / kit.defaults.ramp_slope — Horizontal length in metres.
---@opt width: number = kit.defaults.corridor_width (4.5) — Width in metres.
---@opt carve: boolean = false — First cut an air envelope through solid.
---@opt headroom: number = kit.defaults.corridor_height (4.5) — Envelope clearance above the ramp (carve only).
---@opt detail: boolean = false — Make the wedge detail.
---@opt group: boolean = true — Group the brushes (with carve) under the ramp's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt allow_tight: boolean = false — Allow a width or headroom below the player's size.
---@opt allow_steep: boolean = false — Allow steeper than 45 degrees.
---@opt purpose: string = "require" (inside kit.space and kit.build_layout: "quiet") — As kit.stairs: "require" refuses a ramp that serves no route or whose landing is blocked; "report" warns and builds; "quiet" or "off" builds without measuring.
function kit.ramp(t)
  local api = "kit.ramp"
  check_keys(api, t, { "name", "pos", "dir", "rise", "run", "width", "carve", "headroom", "detail", "group",
    "texture", "textures", "allow_tight", "allow_steep", "purpose" })
  local name = name_for(api, "ramp", t.name)
  local mode = internals_purpose.purpose_mode(api, t.purpose, composite_depth > 0 and "quiet" or "require")
  local x, y, z = vec3(api, "pos", t.pos)
  local dx, dz = direction(api, "dir", t.dir)
  local rise = positive(api, "rise", t.rise)
  local run = positive(api, "run", t.run, rise / kit.defaults.ramp_slope)
  local width = positive(api, "width", t.width, kit.defaults.corridor_width)
  check_walkable(api, "ramp", width, nil, t.allow_tight)
  if rise / run > kit.limits.max_slope + EPS and not t.allow_steep then
    fail(api, "rise/run " .. fmt(rise / run) .. " is steeper than 45 degrees (Alpine waypoints.h:37).",
      "Lengthen run, or pass allow_steep = true.")
  end
  local texture, textures = textures_of(api, t)

  -- The route it serves (the intent layer), as for stairs.
  local route = nil
  if mode ~= "off" and mode ~= "quiet" then
    route = internals_purpose.stair_route(x, y, z, dx, dz, run, rise, width)
    if not route.ok then
      if mode == "require" then
        fail(api, name .. " serves no route: " .. route.fault .. ".",
          "A ramp joins two floors that are otherwise apart, with room to step on and off (spatial.purpose{} lists the grader's reading of every flight); pass purpose = \"report\" to build it anyway.")
      end
      log.warn(api .. ": " .. name .. " serves no route: " .. route.fault .. ".")
    elseif route.pending ~= nil then
      log.info(api .. ": " .. name .. ": " .. route.pending .. ".")
    end
    route.text = route.ok and (route.pending or "serves a route") or ("serves no route: " .. route.fault)
  end

  local brushes = {}
  local env
  if boolean(api, "carve", t.carve, false) then
    local headroom = positive(api, "headroom", t.headroom, kit.defaults.corridor_height)
    check_walkable(api, "ramp headroom", nil, headroom, t.allow_tight)
    env = envelope(api, "ramp", name, x, y, z, dx, dz, width, run, rise, headroom, texture, textures)
    brushes[#brushes + 1] = env
  elseif t.headroom ~= nil then
    fail(api, "'headroom' only applies with carve = true.")
  end

  -- The factory wedge has its right angle at local (-X, -Y) and its high edge at local -X, so the
  -- yaw that raises it toward dir = (dx, 0, dz) is atan2(dz, -dx): +x 180, -x 0, +z 90, -z -90.
  local yaw = math.deg(math.atan2(dz, -dx))
  local ramp = emit("ramp", name, "ramp", {
    shape = "wedge", pos = { x + dx * run / 2, y + rise / 2, z + dz * run / 2 }, size = { run, rise, width },
    rot = { yaw = yaw }, detail = boolean(api, "detail", t.detail, false),
  }, texture, textures)
  uv.ramp(ramp)
  brushes[#brushes + 1] = ramp
  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  internals_purpose.tag_named(name, "route", "a ramp " .. fmt(rise) .. " m up from (" .. fmt(x) .. ", " .. fmt(y) .. ", " .. fmt(z) .. ") toward " .. t.dir, mode)
  return {
    kind = "ramp", name = name, brush = ramp, brushes = brushes, envelope = env, group = group,
    top = vec(x + dx * run, y + rise, z + dz * run), run = run, role = "route", purpose = route,
  }
end

---@kit kit.door{name?, opening, slide?, thickness?, travel?, pause?, trigger?, buttons?, button_class?, texture?|textures?} → door — A sliding mover door filling a doorway/corridor opening, opened by use-key buttons or an automatic trigger.
---@opt name: string = door<n> — Names the door, its mover, keyframes and triggers.
---@opt opening: record — Required: a kit.doorway record or a single-segment kit.corridor record.
---@opt slide: string = "up" — Where the door goes when open: "up", "down", "left" or "right" (as seen walking from a to b).
---@opt thickness: number = kit.defaults.door_thickness (0.25) — Slab thickness in metres.
---@opt travel: number = kit.defaults.door_travel (1) — Seconds to open (and to close).
---@opt pause: number = kit.defaults.door_pause (3) — Seconds the door stays open.
---@opt trigger: string|boolean = "auto" — "auto" adds a walk-in trigger at the door; false adds none (ignored with buttons).
---@opt buttons: vec[] — Points of use-key triggers that open the door, instead of the automatic trigger.
---@opt button_class: string = none — A clutter class placed at each button point so players can see where to press Use.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.door(t)
  local api = "kit.door"
  check_keys(api, t, { "name", "opening", "slide", "thickness", "travel", "pause", "trigger", "buttons", "button_class", "texture", "textures" })
  local name = name_for(api, "door", t.name)
  local o = t.opening
  if type(o) ~= "table" or (o.axis ~= "x" and o.axis ~= "z") or o.center == nil or o.width == nil or o.height == nil then
    fail(api, "'opening' must be a kit.doorway record or a single-segment kit.corridor record.")
  end
  local thickness = positive(api, "thickness", t.thickness, kit.defaults.door_thickness)
  local travel = positive(api, "travel", t.travel, kit.defaults.door_travel)
  local pause = number(api, "pause", t.pause, kit.defaults.door_pause)
  if pause < 0 then fail(api, "'pause' must be 0 or more seconds.") end
  local length = o.length or thickness
  if thickness > length + EPS then
    fail(api, "thickness " .. fmt(thickness) .. " m is deeper than the " .. fmt(length) .. " m opening.")
  end
  local cx, cy, cz = vec3(api, "opening.center", o.center)
  local w, h = o.width, o.height
  local texture, textures = textures_of(api, t)

  -- 1. The door slab. 2. The mover — created AFTER the brush is final, because the mover keeps its
  -- own copy of each member brush taken at creation.
  local size = o.axis == "x" and { thickness, h, w } or { w, h, thickness }
  local brush = emit("door", name, "door", { shape = "box", pos = { cx, cy, cz }, size = size }, texture, textures)
  local mover = level.create_mover{
    name = name, brushes = { brush },
    props = { movement_type = "Ping Pong Once", is_door = true },
    tags = tags_for("door", name),
  }

  -- 3. The open keyframe: slide up (default), down, or left/right across the opening (left/right as
  -- seen walking through it from a to b).
  local slide = t.slide or "up"
  local ox, oy, oz = 0, 0, 0
  local fx, fz = 0, 0
  if o.dir and DIRS[o.dir] then fx, fz = DIRS[o.dir][1], DIRS[o.dir][2]
  elseif o.axis == "x" then fx = 1 else fz = 1 end
  if slide == "up" then oy = h
  elseif slide == "down" then oy = -h
  elseif slide == "right" then ox, oz = fz * w, -fx * w
  elseif slide == "left" then ox, oz = -fz * w, fx * w
  else
    fail(api, "'slide' must be \"up\", \"down\", \"left\" or \"right\" (got " .. tostring(slide) .. ").")
  end

  -- 4. Mover timing (Ping Pong Once + is_door): travel there, pause, travel back. Both keyframes carry
  -- the travel time in both directions: RED's stock doors put the closing time on the closed (first)
  -- keyframe's Return Travel (dm03/dm06/dm15/dm17/ctf01: keyframe 1 travel = return), which the stock
  -- game reads for the leg back to it, while Alpine's mover code (game_patch/object/mover.cpp:661-670)
  -- reads the reverse time of the keyframe the leg departs from — the open one.
  local start = mover.start_keyframe
  start:set_many{ name = name .. "_keyframe1", travel_time_to_next = travel, return_travel = travel }
  start:tag("kit.part", "keyframe")
  local open = mover:add_keyframe{
    offset = { ox, oy, oz },
    props = { name = name .. "_keyframe2", pause_time = pause, travel_time_to_next = travel, return_travel = travel },
  }
  open:tag("kit", "door"):tag("kit.name", name):tag("kit.part", "keyframe")

  -- 5. Triggers, each linked to the mover (the link is stored as its start keyframe).
  local resets = 2 * travel + pause
  local triggers = {}
  local buttons = {}
  if t.buttons ~= nil then
    if t.trigger ~= nil and t.trigger ~= "buttons" then
      fail(api, "give either 'buttons' or 'trigger', not both.")
    end
    if type(t.buttons) ~= "table" or #t.buttons == 0 then fail(api, "'buttons' must list at least one point.") end
    for i, p in ipairs(t.buttons) do
      local bx, by, bz = vec3(api, "buttons[" .. i .. "]", p)
      triggers[i] = level.place{
        kind = "trigger", pos = { bx, by, bz }, name = name .. "_button" .. i,
        props = { shape = "Sphere", sphere_radius = 1.5, resets_after = resets, use_key_required = true },
        links = { mover }, tags = tags_for("door", name),
      }
      triggers[i]:tag("kit.part", "button")
    end
    if t.button_class ~= nil then
      if type(t.button_class) ~= "string" or t.button_class == "" then fail(api, "'button_class' must be a clutter class name.") end
      for i, p in ipairs(t.buttons) do
        local bx, by, bz = vec3(api, "buttons[" .. i .. "]", p)
        buttons[i] = level.place{
          kind = "clutter", class = t.button_class, pos = { bx, by, bz }, name = name .. "_panel" .. i,
          tags = tags_for("door", name),
        }
        buttons[i]:tag("kit.part", "panel")
      end
    end
  elseif t.button_class ~= nil then
    fail(api, "'button_class' needs 'buttons' (the points the panels mark).")
  else
    local mode = t.trigger
    if mode == nil then mode = "auto" end
    if mode == "auto" then
      triggers[1] = level.place{
        kind = "trigger", pos = { cx, cy, cz }, name = name .. "_trigger",
        props = { shape = "Sphere", sphere_radius = math.max(w, 2), resets_after = resets, activated_by = "Players" },
        links = { mover }, tags = tags_for("door", name),
      }
      triggers[1]:tag("kit.part", "trigger")
    elseif mode ~= false then
      fail(api, "'trigger' must be \"auto\" or false (or pass buttons = {...}).")
    end
  end

  return { kind = "door", name = name, brush = brush, mover = mover, keyframes = { start, open }, triggers = triggers, panels = buttons }
end

---@kit kit.platform{name?, pos, size = {w, d}, height, thickness?, supports?, detail?, group?, texture?|textures?} → platform — A raised walkable surface at pos.y + height: one solid block, or a slab of thickness (plus 4 legs with supports = true).
---@opt name: string = platform<n> — Names the platform (tags, group).
---@opt pos: vec — Required: the floor point under the centre of the platform.
---@opt size: {w, d} — Required: footprint in metres along x and z.
---@opt height: number — Required: height of the walkable top above pos.y, metres.
---@opt thickness: number = none (a solid block) — Make a slab this thick instead of a block reaching the floor.
---@opt supports: boolean = false — Add four kit.defaults.pillar_size legs under a slab (needs thickness).
---@opt detail: boolean = false — Make the brushes detail.
---@opt group: boolean = true — Group the brushes under the platform's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.platform(t)
  local api = "kit.platform"
  check_keys(api, t, { "name", "pos", "size", "height", "thickness", "supports", "detail", "group", "texture", "textures" })
  local name = name_for(api, "platform", t.name)
  local x, y, z = vec3(api, "pos", t.pos)
  local s = t.size
  local w, d
  if type(s) == "table" then
    if s[1] ~= nil then w, d = s[1], s[2] else w, d = s.w or s.x, s.d or s.z end
  end
  if not (is_finite(w) and is_finite(d)) or w <= 0 or d <= 0 then fail(api, "'size' must be {w, d} (metres, both > 0).") end
  local height = positive(api, "height", t.height)
  local detail = boolean(api, "detail", t.detail, false)
  local supports = boolean(api, "supports", t.supports, false)
  local texture, textures = textures_of(api, t)
  local top = y + height
  local brushes = {}

  if t.thickness == nil then
    if supports then fail(api, "'supports' needs a 'thickness' (the slab the legs hold up).") end
    brushes[1] = emit("platform", name, "block", { shape = "box", pos = { x, y + height / 2, z }, size = { w, height, d }, detail = detail },
      texture, textures)
  else
    local thickness = positive(api, "thickness", t.thickness)
    if thickness >= height - EPS then fail(api, "'thickness' must be less than 'height' (use no thickness for a solid block).") end
    brushes[1] = emit("platform", name, "slab", { shape = "box", pos = { x, top - thickness / 2, z }, size = { w, thickness, d }, detail = detail },
      texture, textures)
    if supports then
      local ps = kit.defaults.pillar_size
      if 2 * ps > w + EPS or 2 * ps > d + EPS then
        fail(api, "the slab is too small for " .. fmt(ps) .. " m legs at each corner.", "Lower kit.defaults.pillar_size or enlarge the slab.")
      end
      local leg_h = height - thickness
      local ix, iz = w / 2 - ps / 2, d / 2 - ps / 2
      local corners = { { -1, -1 }, { 1, -1 }, { -1, 1 }, { 1, 1 } }
      for i, c in ipairs(corners) do
        brushes[#brushes + 1] = emit("platform", name, "leg" .. i, {
          shape = "box", pos = { x + c[1] * ix, y + leg_h / 2, z + c[2] * iz }, size = { ps, leg_h, ps }, detail = detail,
        }, texture, textures)
      end
    end
  end

  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  internals_purpose.tag_named(name, "route", "raised walkable floor " .. fmt(height) .. " m up at (" .. fmt(x) .. ", " .. fmt(z) .. ")")
  return {
    kind = "platform", name = name, brushes = brushes, group = group, top_y = top,
    min = vec(x - w / 2, y, z - d / 2), max = vec(x + w / 2, top, z + d / 2),
  }
end

---@kit kit.pillar_row{name?, from, to?, count, height, size?, shape?, sides?, detail?, group?, texture?|textures?} → pillars — count solid pillars standing on the floor, evenly spaced from from to to (inclusive), box or cylinder.
---@opt name: string = pillar_row<n> — Names the row (tags, group).
---@opt from: vec — Required: the floor point of the first pillar.
---@opt to: vec = from — The floor point of the last pillar.
---@opt count: integer — Required: number of pillars (at least 1).
---@opt height: number — Required: pillar height in metres.
---@opt size: number = kit.defaults.pillar_size (1) — Side (box) or diameter (cylinder) in metres.
---@opt shape: string = "box" — "box" or "cylinder".
---@opt sides: integer = 12 — Cylinder segments (cylinder only).
---@opt detail: boolean = false — Make the pillars detail.
---@opt group: boolean = true — Group the pillars under the row's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.pillar_row(t)
  local api = "kit.pillar_row"
  check_keys(api, t, { "name", "from", "to", "count", "height", "size", "shape", "sides", "detail", "group", "texture", "textures" })
  local name = name_for(api, "pillar_row", t.name)
  local fx, fy, fz = vec3(api, "from", t.from)
  local tx, ty, tz = fx, fy, fz
  if t.to ~= nil then tx, ty, tz = vec3(api, "to", t.to) end
  local count = number(api, "count", t.count)
  if count < 1 or count ~= math.floor(count) then fail(api, "'count' must be a whole number of at least 1.") end
  local height = positive(api, "height", t.height)
  local size = positive(api, "size", t.size, kit.defaults.pillar_size)
  local shape = t.shape or "box"
  if shape ~= "box" and shape ~= "cylinder" then fail(api, "'shape' must be \"box\" or \"cylinder\" (got " .. tostring(shape) .. ").") end
  if t.sides ~= nil and shape ~= "cylinder" then fail(api, "'sides' only applies to shape = \"cylinder\".") end
  local texture, textures = textures_of(api, t)
  local detail = boolean(api, "detail", t.detail, false)

  local brushes = {}
  for i = 1, count do
    local f = count == 1 and 0 or (i - 1) / (count - 1)
    local px, py, pz = fx + (tx - fx) * f, fy + (ty - fy) * f, fz + (tz - fz) * f
    local spec = { shape = shape, pos = { px, py + height / 2, pz }, size = { size, height, size }, detail = detail }
    if shape == "cylinder" then spec.sides = t.sides or 12 end
    brushes[i] = emit("pillar_row", name, "pillar" .. i, spec, texture, textures)
  end
  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  internals_purpose.tag_named(name, "cover", "a pillar row breaking the sightlines along it")
  return { kind = "pillars", name = name, brushes = brushes, group = group }
end

-- The earliest CSG time index among a record's air brushes (-1 when the record has none): solids
-- later in time than the air fill it, so those are the solids standing in the room.
local function record_time_index(rec)
  local idx
  local function take(b)
    if type(b) == "userdata" and b.time_index ~= nil and b.time_index >= 0 then
      idx = idx and math.min(idx, b.time_index) or b.time_index
    end
  end
  if type(rec) == "table" then
    take(rec.brush)
    if type(rec.brushes) == "table" then for _, b in ipairs(rec.brushes) do take(b) end end
  end
  return idx or -1
end

local CUBE_CORNERS = {
  { -1, -1, -1 }, { 1, -1, -1 }, { -1, 1, -1 }, { 1, 1, -1 },
  { -1, -1, 1 }, { 1, -1, 1 }, { -1, 1, 1 }, { 1, 1, 1 },
}

-- True when a solid brush later in time than `after` covers the point or comes within c metres of it.
local function solid_near(x, y, z, c, after)
  local q = level.brushes_in{ min = { x - c, y - c, z - c }, max = { x + c, y + c, z + c } }
  for _, b in ipairs(q.brushes) do
    if b.solid and b.time_index > after then
      if b:contains(x, y, z) then return true end
      for _, s in ipairs(CUBE_CORNERS) do
        if b:contains(x + s[1] * c, y + s[2] * c, z + s[3] * c) then return true end
      end
    end
  end
  return false
end

-- The free spot nearest (cx, cz) inside the cell [x0, x1] x [z0, z1] at height y, searched on a
-- 0.5 m grid (ties go to the spot nearer the room centre); nil when the whole cell is blocked.
local function free_spot(cx, y, cz, x0, x1, z0, z1, rcx, rcz, c, after)
  if not solid_near(cx, y, cz, c, after) then return cx, cz end
  local step = 0.5
  local k = math.floor(math.max(cx - x0, x1 - cx, cz - z0, z1 - cz) / step)
  -- Rings of equal grid distance, nearest first; inside a ring the spot nearer the room centre wins
  -- (then lower i, then lower j). Plain loops rather than table.sort: a sort comparator is a callback,
  -- and callbacks are far costlier than inline Lua.
  local rings = {}
  for i = -k, k do
    for j = -k, k do
      local x, z = cx + i * step, cz + j * step
      if (i ~= 0 or j ~= 0) and x >= x0 - EPS and x <= x1 + EPS and z >= z0 - EPS and z <= z1 + EPS then
        local d = i * i + j * j
        rings[d] = rings[d] or {}
        local ring = rings[d]
        ring[#ring + 1] = { x = x, z = z, c = (x - rcx) ^ 2 + (z - rcz) ^ 2 }
      end
    end
  end
  for d = 1, 2 * k * k do
    local ring = rings[d]
    if ring then
      -- Selection order within the ring (rings hold at most a few spots).
      local used = {}
      for _ = 1, #ring do
        local best
        for n, s in ipairs(ring) do
          if not used[n] and (best == nil or s.c < ring[best].c - EPS) then best = n end
        end
        used[best] = true
        local s = ring[best]
        if not solid_near(s.x, y, s.z, c, after) then return s.x, s.z end
      end
    end
  end
  return nil
end

---@kit kit.sky_room{name?, pos?, size?, texture?, open?} → sky — An outdoor sky: a small sealed air box away from the play space holding a Sky Room effect (the view every show-sky face displays, built like the stock dm01/dm04 skies), plus show_sky on the ceilings of the open rooms.
---@opt name: string = sky_room<n> — Names the sky box (tags) and its effect (<name>_effect).
---@opt pos: vec = 64 m beyond the level's +X edge — The floor point of the sky box; keep it out of the play space.
---@opt size: vec = {6, 6, 6} — Interior size of the sky box in metres.
---@opt texture: string = kit.defaults.sky_texture — The sky texture on every face of the box.
---@opt open: record[] — Rooms or corridors whose ceilings show the sky: their up-facing air faces get show_sky and the sky texture (as kit.road, kit.canyon and kit.space do), so they read as sky in the editor too.
function kit.sky_room(t)
  local api = "kit.sky_room"
  t = t or {}
  check_keys(api, t, { "name", "pos", "size", "texture", "open" })
  local name = name_for(api, "sky_room", t.name)
  local w, h, d = 6, 6, 6
  if t.size ~= nil then w, h, d = vec3(api, "size", t.size) end
  if w <= 0 or h <= 0 or d <= 0 then fail(api, "'size' components must be greater than 0.") end
  local x, y, z
  if t.pos ~= nil then
    x, y, z = vec3(api, "pos", t.pos)
  else
    local edge = 0
    for _, b in ipairs(level.brushes) do edge = math.max(edge, b.bounds.max.x) end
    x, y, z = edge + kit.defaults.sky_offset + w / 2, 0, 0
  end
  local texture = t.texture or kit.defaults.sky_texture
  if type(texture) ~= "string" or texture == "" then fail(api, "'texture' must be a texture name.") end

  local brush = emit("sky_room", name, "air", { shape = "box", pos = { x, y + h / 2, z }, size = { w, h, d }, air = true }, texture, nil)
  local effect = level.place{
    kind = "room_effect", pos = { x, y + h / 2, z }, name = name .. "_effect", props = { type = "Sky Room" },
    tags = tags_for("sky_room", name),
  }
  effect:tag("kit.part", "effect")

  local sky_faces = 0
  if t.open ~= nil then
    if type(t.open) ~= "table" then fail(api, "'open' must list room or corridor records.") end
    for i, r in ipairs(t.open) do
      local brushes = type(r) == "table" and (r.brushes or (r.brush and { r.brush })) or nil
      if brushes == nil then fail(api, "'open[" .. i .. "]' must be a kit room or corridor record.") end
      for _, b in ipairs(brushes) do
        if b.air then
          for _, f in ipairs(b:faces_facing("up")) do
            f:set_flag("show_sky", true)
            f:set_texture(texture) -- unseen in game (the sky room shows), but reads as sky in the editor
            sky_faces = sky_faces + 1
          end
        end
      end
    end
  end

  return {
    kind = "sky", name = name, brush = brush, effect = effect, sky_faces = sky_faces,
    min = vec(x - w / 2, y, z - d / 2), max = vec(x + w / 2, y + h, z + d / 2), center = vec(x, y + h / 2, z), floor_y = y,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Organic spaces and rock dressing. Each function is one geo builder call (geo.tube, geo.passage,
-- geo.rock_row, geo.scatter, geo.pit) plus naming, tags, a group, walkway limits and the sky; the
-- heavy work runs in the editor, never per vertex in Lua. Organic air first, then the rocks that
-- stand in it. Seeds are explicit options (default 1), so the kit still never reads rng.
-- ---------------------------------------------------------------------------------------------

-- Textures and tile of an organic function: its own texture/textures, else the rock defaults, else
-- the kit defaults (then the editor's). Given both, textures sets the roles it names and texture fills
-- the roles it leaves out.
local function rock_textures_of(api, t)
  if t.texture ~= nil and t.textures ~= nil then
    if type(t.texture) ~= "string" then fail(api, "'texture' must be a texture name (got " .. type(t.texture) .. ").") end
    if type(t.textures) ~= "table" then
      fail(api, "'textures' must be a table {floor =, wall =, ceiling =} (got " .. type(t.textures) .. ").")
    end
    local merged = {}
    for k, v in pairs(t.textures) do merged[k] = v end
    for _, role in ipairs({ "floor", "wall", "ceiling" }) do
      if merged[role] == nil then merged[role] = t.texture end
    end
    return nil, merged
  end
  if t.texture ~= nil then return t.texture, nil end
  if t.textures ~= nil then return nil, t.textures end
  if kit.defaults.rock_texture ~= nil then return kit.defaults.rock_texture, nil end
  if kit.defaults.rock_textures ~= nil then return nil, kit.defaults.rock_textures end
  return kit.defaults.texture, kit.defaults.textures
end

local function rock_tile(api, t)
  if t.tile ~= nil then return positive(api, "tile", t.tile) end
  return kit.defaults.rock_tile_metres or kit.defaults.tile_metres
end

local function seed_of(api, t)
  local s = t.seed
  if s == nil then return 1 end
  if type(s) ~= "number" or s ~= math.floor(s) then fail(api, "'seed' must be an integer (got " .. tostring(s) .. ").") end
  return s
end

-- The option table every geo builder shares: tags, the kit.part prefix, textures and tile.
local function builder_args(api, fn, name, part, t)
  local texture, textures = rock_textures_of(api, t)
  local a = { tags = tags_for(fn, name), part = part, tile = rock_tile(api, t) }
  if texture ~= nil then a.texture = texture elseif textures ~= nil then a.textures = textures end
  return a
end

local function walk_limits(a, allow_tight)
  a.min_clear_width = allow_tight and 0 or kit.limits.min_clear_width
  a.min_headroom = allow_tight and 0 or kit.limits.min_headroom
  a.max_slope = kit.limits.max_slope
end

local function one_of(api, t, a, b)
  if t[a] ~= nil and t[b] ~= nil then fail(api, "give either '" .. a .. "' or '" .. b .. "', not both.") end
end

-- A cave or canyon that runs into a kit room must stay under the room's ceiling where it enters: a crown
-- (or canyon rim) that pokes above the ceiling crosses the room's ceiling-wall edge, which leaks. Checked
-- before anything is built, at every path point inside a room's footprint and where each leg from such a
-- point crosses the room's walls. crown(i, f) is the highest the tube can reach above the floor at
-- fraction f along leg i (path point i to i + 1). Margin: the crown stays at least 0.1 m under the ceiling.
local function check_room_mouths(api, kind, pts, crown, change)
  local rooms = level.brushes_tagged("kit", "room").brushes
  if #rooms == 0 then return end
  local margin = 0.1
  local function inside(b, x, y, z)
    local lo, hi = b.bounds.min, b.bounds.max
    return x > lo.x and x < hi.x and z > lo.z and z < hi.z and y >= lo.y - 1e-6 and y < hi.y
  end
  local function check(b, x, y, z, top)
    local hi = b.bounds.max.y
    if top > hi - margin then
      local name = b:tags()["kit.name"] or ("brush#" .. b.uid)
      local need = math.ceil((top + margin - b.bounds.min.y) * 4) / 4
      fail(api, "room '" .. name .. "' is " .. fmt(hi - b.bounds.min.y) .. " m high, but the " .. kind .. " can reach " .. fmt(top - b.bounds.min.y)
        .. " m above its floor where it enters at (" .. fmt(x) .. ", " .. fmt(y) .. ", " .. fmt(z) .. "): it would cross the room's ceiling edge, which leaks.",
        "Raise the room to at least " .. fmt(need) .. " m, or " .. change .. ".")
    end
  end
  for _, b in ipairs(rooms) do
    for i = 1, #pts do
      local p = pts[i]
      if inside(b, p[1], p[2], p[3]) then
        check(b, p[1], p[2], p[3], p[2] + crown(math.min(i, #pts - 1), i == #pts and 1 or 0))
        -- Where the legs on either side leave the room: the first wall the centre line crosses.
        for _, j in ipairs({ i - 1, i + 1 }) do
          local q = pts[j]
          if q ~= nil and not inside(b, q[1], q[2], q[3]) then
            local lo, hi = b.bounds.min, b.bounds.max
            local t = 1
            for _, axis in ipairs({ { 1, lo.x, hi.x }, { 3, lo.z, hi.z } }) do
              local a, l, h = axis[1], axis[2], axis[3]
              local d = q[a] - p[a]
              if d > 0 then t = math.min(t, (h - p[a]) / d) elseif d < 0 then t = math.min(t, (l - p[a]) / d) end
            end
            local leg, f = math.min(i, j), (j > i) and t or (1 - t)
            local a0, a1 = pts[leg], pts[leg + 1]
            local y = a0[2] + (a1[2] - a0[2]) * f
            check(b, a0[1] + (a1[1] - a0[1]) * f, y, a0[3] + (a1[3] - a0[3]) * f, y + crown(leg, f))
          end
        end
      end
    end
  end
end

-- The path as {x, y, z} triples (validated again, with every message, by geo.tube).
local function path_points(api, path)
  if type(path) ~= "table" then return {} end
  local pts = {}
  for i, v in ipairs(path) do
    local x, y, z = vec3(api, "path[" .. i .. "]", v)
    pts[#pts + 1] = { x, y, z }
  end
  return pts
end

local function tube_record(kind, name, tube, extra)
  local r = {
    kind = kind, name = name, brushes = tube.brushes.brushes, stations = tube.stations, climbs = tube.climbs,
    min = tube.min, max = tube.max, floor_y = tube.floor_y, top_y = tube.top_y, start = tube.start, ["end"] = tube["end"],
    length = tube.length, seed = tube.seed, tube = tube,
  }
  for k, v in pairs(extra) do r[k] = v end
  return r
end

---@kit kit.cave{name?, path, radius?|radii?, height?|heights?, roughness?, floor_flatness?, segments?|spacing?, sides?, seed?, allow_tight?, group?, texture?, textures?, tile?} → cave — A rough cave along a floor path on any heading (geo.tube): structural air segments meeting on identical faces, a floor that is level or a smooth slope (at most 45°) on each leg, and a 1.8 m wide, 2 m high walkway along the path.
---@opt name: string = cave<n> — Names the cave (tags, group).
---@opt path: vec[] — Required: 2 to 256 floor points; legs at least 2 m apart, at most 90° of turn per point and 45° of climb per leg (a climbing leg that turns keeps a level landing at the turn). Start or end it at least 1 m inside a room it joins, at the room's floor height, with the room at least height × (1 + roughness) + 0.1 m high; end it on its start point for a closed loop.
---@opt radius: number = kit.defaults.cave_radius (3) — Half-width in metres.
---@opt radii: number[] = none — Half-width per path point, interpolated along the path.
---@opt height: number = kit.defaults.cave_height (4.5) — Clear height at the crown, metres.
---@opt heights: number[] = none — Clear height per path point.
---@opt roughness: number = kit.defaults.roughness (0.2) — Wall jitter as a fraction of the size (0.05 to 0.5).
---@opt floor_flatness: number = 0.6 — Floor half-width as a fraction of the radius (0.3 to 1).
---@opt segments: integer = from spacing and the climb — Segments per leg (give segments or spacing).
---@opt spacing: number = max(3, radius) — Target distance between stations, metres.
---@opt sides: integer = 8 — Points per ring (6 to 14).
---@opt seed: integer = 1 — The random stream; the same options and seed give the same cave.
---@opt allow_tight: boolean = false — Skip the walkway width and headroom guarantee.
---@opt group: boolean = true — Group every brush the call made under the cave's name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---@opt tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.cave(t)
  local api = "kit.cave"
  check_keys(api, t, { "name", "path", "radius", "radii", "height", "heights", "roughness", "floor_flatness", "segments", "spacing",
    "sides", "seed", "allow_tight", "group", "texture", "textures", "tile" })
  local name = name_for(api, "cave", t.name)
  if t.path == nil then fail(api, "'path' is required (2 or more floor points).", "For example path = {{0, 0, 0}, {0, 0, 30}, {20, 0, 50}}.") end
  one_of(api, t, "radius", "radii")
  one_of(api, t, "height", "heights")
  one_of(api, t, "segments", "spacing")
  local a = builder_args(api, "cave", name, "seg", t)
  a.path, a.profile = t.path, "cave"
  if t.radii ~= nil then a.radii = t.radii else a.radius = t.radius or kit.defaults.cave_radius end
  if t.heights ~= nil then a.heights = t.heights else a.height = t.height or kit.defaults.cave_height end
  a.roughness = t.roughness or kit.defaults.roughness
  a.floor_flatness, a.segments, a.spacing, a.sides = t.floor_flatness, t.segments, t.spacing, t.sides
  a.seed = seed_of(api, t)
  walk_limits(a, boolean(api, "allow_tight", t.allow_tight, false))
  local pts = path_points(api, t.path)
  if type(a.roughness) == "number" then
    -- The arch reaches at most height x (1 + roughness) above the floor.
    check_room_mouths(api, "cave's crown", pts, function(i, f)
      local h0 = type(a.heights) == "table" and a.heights[i] or a.height
      local h1 = type(a.heights) == "table" and a.heights[i + 1] or a.height
      if type(h0) ~= "number" or type(h1) ~= "number" then return 0 end
      return (h0 + (h1 - h0) * f) * (1 + a.roughness)
    end, "lower the cave's height or roughness")
  end
  local rec = tube_record("cave", name, geo.tube(a), {})
  uv.rock(t, name)
  rec.group = group_if_many(name, rec.brushes, boolean(api, "group", t.group, true))
  return rec
end

---@kit kit.canyon{name?, path, width?|widths?, height?, roughness?, lean?, sky?, segments?|spacing?, seed?, group?, texture?, textures?, tile?} → canyon — An open canyon along a floor path on any heading (geo.tube): structural air segments meeting on identical faces, a floor that is level or a smooth slope on each leg, leaning rough walls and one common rim height, open to the sky (show_sky tops plus a kit.sky_room when the level has none).
---@opt name: string = canyon<n> — Names the canyon (tags, group; its sky room is <name>_sky).
---@opt path: vec[] — Required: 2 to 256 floor points; legs at least 2 m apart, at most 90° of turn per point and 45° of climb per leg. A canyon that runs into a room needs the room's ceiling at least 0.1 m above the canyon's rim.
---@opt width: number = kit.defaults.canyon_width (12) — Floor width in metres.
---@opt widths: number[] = none — Floor width per path point (basins and narrows), interpolated along the path.
---@opt height: number = kit.defaults.canyon_height (16) — Rim height above the highest floor, metres.
---@opt roughness: number = kit.defaults.roughness (0.2) — Wall jitter as a fraction of the size (0.05 to 0.5).
---@opt lean: number = 0.15 — How far the walls lean out per metre of height (0 to 0.5).
---@opt sky: boolean = true — Show the sky through the canyon's top faces (adds kit.sky_room{name = <name>_sky} when the level has no sky room).
---@opt segments: integer = from spacing and the climb — Segments per leg (give segments or spacing).
---@opt spacing: number = width / 2 — Target distance between stations, metres.
---@opt seed: integer = 1 — The random stream; the same options and seed give the same canyon.
---@opt group: boolean = true — Group the canyon's segments under its name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---@opt tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.canyon(t)
  local api = "kit.canyon"
  check_keys(api, t, { "name", "path", "width", "widths", "height", "roughness", "lean", "sky", "segments", "spacing", "seed",
    "group", "texture", "textures", "tile" })
  local name = name_for(api, "canyon", t.name)
  if t.path == nil then fail(api, "'path' is required (2 or more floor points).", "For example path = {{0, 0, 0}, {10, 0, 40}, {-6, 0, 80}}.") end
  one_of(api, t, "width", "widths")
  one_of(api, t, "segments", "spacing")
  local a = builder_args(api, "canyon", name, "seg", t)
  a.path, a.profile = t.path, "canyon"
  if t.widths ~= nil then a.widths = t.widths else a.width = t.width or kit.defaults.canyon_width end
  a.height = t.height or kit.defaults.canyon_height
  a.roughness = t.roughness or kit.defaults.roughness
  a.lean, a.segments, a.spacing = t.lean, t.segments, t.spacing
  a.seed = seed_of(api, t)
  walk_limits(a, false)
  local pts = path_points(api, t.path)
  if type(a.height) == "number" and #pts > 0 then
    -- The rim is one common height: the highest floor plus height.
    local top = -math.huge
    for _, p in ipairs(pts) do top = math.max(top, p[2]) end
    top = top + a.height
    check_room_mouths(api, "canyon's rim", pts, function(i, f)
      local y = pts[i][2] + (pts[i + 1][2] - pts[i][2]) * f
      return top - y
    end, "lower the canyon's height")
  end
  local tube = geo.tube(a)
  local sky
  if boolean(api, "sky", t.sky, true) then
    tube.brushes:set_face_flag("up", "show_sky", true)
    if level.objects_tagged("kit", "sky_room").count == 0 then
      sky = kit.sky_room{ name = name .. "_sky" }
    end
  end
  local rec = tube_record("canyon", name, tube, { sky = sky })
  uv.rock(t, name)
  rec.group = group_if_many(name, rec.brushes, boolean(api, "group", t.group, true))
  return rec
end

---@kit kit.passage{name?, path, width?, height?, allow_tight?, detail?, group?, texture?, textures?, tile?} → passage — A clean level corridor along a floor path on any heading (geo.passage): one exact air prism per leg, mitred so neighbours meet on identical faces.
---@opt name: string = passage<n> — Names the passage (tags, group).
---@opt path: vec[] — Required: 2 to 256 floor points at one height, turning at most 120° per point. An end may stop exactly on an axis-aligned room wall when its last leg is axis-aligned.
---@opt width: number = kit.defaults.corridor_width (4.5) — Clear width in metres (at least 1.8 unless allow_tight).
---@opt height: number = kit.defaults.corridor_height (4.5) — Clear height in metres (at least 2 unless allow_tight).
---@opt allow_tight: boolean = false — Allow a width or height below the player's size.
---@opt detail: boolean = false — Make the air brushes detail.
---@opt group: boolean = true — Group the prisms under the passage's name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---@opt tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.passage(t)
  local api = "kit.passage"
  check_keys(api, t, { "name", "path", "width", "height", "allow_tight", "detail", "group", "texture", "textures", "tile" })
  local name = name_for(api, "passage", t.name)
  if t.path == nil then fail(api, "'path' is required (2 or more floor points).", "For example path = {{0, 0, 0}, {10, 0, 10}, {10, 0, 30}}.") end
  local width = positive(api, "width", t.width, kit.defaults.corridor_width)
  local height = positive(api, "height", t.height, kit.defaults.corridor_height)
  check_walkable(api, "passage", width, height, t.allow_tight)
  local a = builder_args(api, "passage", name, "seg", t)
  a.path, a.width, a.height = t.path, width, height
  a.detail = boolean(api, "detail", t.detail, false)
  a.min_clear_width, a.min_headroom = 0, 0
  local tube = geo.passage(a)
  local rec = tube_record("passage", name, tube, { width = width, height = height })
  uv.rock(t, name)
  rec.group = group_if_many(name, rec.brushes, boolean(api, "group", t.group, true))
  return rec
end

local function rocks_record(name, rocks, group_wanted)
  local brushes = rocks.brushes.brushes
  return {
    kind = "rocks", name = name, brushes = brushes, group = group_if_many(name, brushes, group_wanted),
    count = rocks.count, min = rocks.min, max = rocks.max, seed = rocks.seed,
  }
end

---@kit kit.rock_wall{name?, from, to, height, depth?, roughness?, flat_top?, structural?, spacing?, seed?, group?, texture?, textures?, tile?} → rocks — A row of overlapping rough detail rocks along the floor line from → to (geo.rock_row), or with structural = true one rough structural ridge standing exactly on the floor. Build it after the air it stands in.
---@opt name: string = rock_wall<n> — Names the rocks (tags, group).
---@opt from: vec — Required: the floor point the wall starts at.
---@opt to: vec — Required: the floor point the wall ends at.
---@opt height: number — Required: visible height above the floor, metres.
---@opt depth: number = 2 — Thickness across the line, metres.
---@opt roughness: number = kit.defaults.roughness (0.2) — Jitter as a fraction of the rock size (0.05 to 0.5).
---@opt flat_top: boolean = false — Clip every rock at one height: a flat, walkable ledge.
---@opt structural: boolean = false — Build one structural ridge (seals and splits rooms) instead of detail rocks; keep it clear of walls it would graze.
---@opt spacing: number = 0.8 × depth — Distance between rock centres, metres (neighbours overlap).
---@opt seed: integer = 1 — The random stream.
---@opt group: boolean = true — Group the rocks under the wall's name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---@opt tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.rock_wall(t)
  local api = "kit.rock_wall"
  check_keys(api, t, { "name", "from", "to", "height", "depth", "roughness", "flat_top", "structural", "spacing", "seed", "group",
    "texture", "textures", "tile" })
  local name = name_for(api, "rock_wall", t.name)
  local a = builder_args(api, "rock_wall", name, "rock", t)
  a.from, a.to = t.from, t.to
  a.height = positive(api, "height", t.height)
  a.depth = t.depth
  a.roughness = t.roughness or kit.defaults.roughness
  a.flat_top = boolean(api, "flat_top", t.flat_top, false)
  a.structural = boolean(api, "structural", t.structural, false)
  a.spacing = t.spacing
  a.seed = seed_of(api, t)
  local rec = rocks_record(name, geo.rock_row(a), boolean(api, "group", t.group, true))
  internals_purpose.tag_named(name, "theme_detail", "a rock wall foot along a floor line")
  uv.rock(t, name)
  return rec
end

---@kit kit.boulders{name?, area?|room?|along?, count, size?, margin?, seed?, group?, texture?, textures?, tile?, purpose?} → rocks — count detail boulders on a rectangle, a kit room's floor, or a cave/canyon/passage floor clear of its walkway (geo.scatter). Rocks belong in organic spaces and against walls and edges: on a rectangle or room the space must be open to the sky or rock-walled (a built interior or walled yard is refused), and the boulders are scattered only in the bands along its sides (a cave, canyon or passage scatter keeps its walkway clear), never over the open middle; the record's count says how many fit. Every boulder is tagged kit.role = "theme_detail" and kit.purpose. Build them after the air they stand in.
---@opt name: string = boulders<n> — Names the boulders (tags, group).
---@opt area: {min=, max=} — A floor rectangle (its floor is min.y); give one of area, room or along.
---@opt room: record — A kit room (or other min/max record): its floor rectangle.
---@opt along: record — A kit.cave, kit.canyon or kit.passage record: its floor, clear of the walkway.
---@opt count: integer — Required: how many boulders (1 to 500).
---@opt size: number|{min, max} = {0.6, 2} — Boulder diameter in metres, or the range it is drawn from.
---@opt margin: number = kit.defaults.spawn_margin (room), 0.5 (area), 0.25 (along) — Inset from the edges, metres; along a tube, how far a boulder's centre stays inside the floor's edge (boulders may lean into the wall foot).
---@opt seed: integer = 1 — The random stream.
---@opt group: boolean = true — Group the boulders under their name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---@opt tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
---@opt purpose: string = "require" — Theme fit: "require" refuses a built rectangle or room (an interior or a walled yard: walls not natural rock) and scatters only in the bands along the rectangle's sides; "report" warns and scatters over the whole rectangle; "off" skips both.
function kit.boulders(t)
  local api = "kit.boulders"
  check_keys(api, t, { "name", "area", "room", "along", "count", "size", "margin", "seed", "group", "texture", "textures", "tile", "purpose" })
  local mode = internals_purpose.purpose_mode(api, t.purpose)
  local name = name_for(api, "boulders", t.name)
  local given = (t.area ~= nil and 1 or 0) + (t.room ~= nil and 1 or 0) + (t.along ~= nil and 1 or 0)
  if given ~= 1 then
    fail(api, "give exactly one of 'area' (a rectangle {min=, max=}), 'room' (a kit room) or 'along' (a cave, canyon or passage).")
  end
  local a = builder_args(api, "boulders", name, "rock", t)
  a.count = number(api, "count", t.count)
  local wanted = a.count
  a.size, a.margin = t.size, t.margin
  a.seed = seed_of(api, t)
  a.roughness = kit.defaults.roughness
  if t.area ~= nil then
    a.area = t.area
  elseif t.room ~= nil then
    local r = record_box(api, "room", t.room)
    a.area = { min = { r.x0, r.floor, r.z0 }, max = { r.x1, r.floor, r.z1 } }
    if t.margin == nil then a.margin = kit.defaults.spawn_margin end
  else
    if type(t.along) ~= "table" or t.along.tube == nil then
      fail(api, "'along' must be a kit.cave, kit.canyon or kit.passage record.")
    end
    a.along = t.along.tube
  end
  -- Theme fit on a rectangle or room: the space must be organic, and the boulders go against its walls and edges.
  local context = nil
  if t.along == nil and mode ~= "off" then
    local P = ged.import("kit_probe")
    local bx = a.area
    local function c3(v) if type(v) == "table" and v[1] ~= nil then return v[1], v[2], v[3] end return v.x, v.y, v.z end
    local x0, y0, z0 = c3(bx.min)
    local x1, _, z1 = c3(bx.max)
    local sp = { x0 = math.min(x0, x1), z0 = math.min(z0, z1), x1 = math.max(x0, x1), z1 = math.max(z0, z1), y0 = y0, y1 = y0 + 6, kind = "area" }
    if t.room ~= nil and type(t.room) == "table" then
      sp.kind = t.room.kind or "room"
      sp.brushes = t.room.brushes or (t.room.brush and { t.room.brush }) or nil
      for _, b in ipairs(sp.brushes or {}) do
        for _, f in ipairs(b.faces) do if f.show_sky then sp.sky = true end end
      end
    end
    context = P.context(sp)
    if context == "built_interior" or context == "built_yard" then
      local msg = "rocks belong in caves, canyons and outdoor ground, not in a built " .. (context == "built_yard" and "yard" or "interior") .. " (" .. fmt(sp.x0) .. ".." .. fmt(sp.x1) .. ", " .. fmt(sp.z0) .. ".." .. fmt(sp.z1) .. ": walls not natural rock)"
      if mode == "require" then
        fail(api, msg .. ".", "Dress built rooms with kit.dress (the theme's built props), or give the space rock walls or sky; pass purpose = \"report\" to place them anyway.")
      end
      log.warn(api .. ": " .. msg .. ".")
    end
  end
  -- Against walls and edges (a rectangle or room): the boulders are scattered in the bands along its four sides (each
  -- band as deep as the largest boulder plus 1 m, at most a third of the side), never over its open middle. Along a
  -- cave, canyon or passage the scatter already keeps the walkway clear, so the boulders sit by the walls.
  local rec
  if mode == "require" and t.along == nil then
    local function c3(v) if type(v) == "table" and v[1] ~= nil then return v[1], v[2], v[3] end return v.x, v.y, v.z end
    local ax, ay, az = c3(a.area.min)
    local bx, _, bz = c3(a.area.max)
    local x0, x1, z0, z1 = math.min(ax, bx), math.max(ax, bx), math.min(az, bz), math.max(az, bz)
    local smax = type(t.size) == "number" and t.size or (type(t.size) == "table" and (t.size[2] or t.size.max) or 2)
    local d = math.min(smax + 1.0, (x1 - x0) / 3, (z1 - z0) / 3)
    local bands = {
      { x0, z0, x1, z0 + d }, { x0, z1 - d, x1, z1 },
      { x0, z0 + d, x0 + d, z1 - d }, { x1 - d, z0 + d, x1, z1 - d },
    }
    local total = 0
    for _, bd in ipairs(bands) do bd.area = math.max(0, (bd[3] - bd[1]) * (bd[4] - bd[2])) total = total + bd.area end
    local brushes, left = {}, wanted
    for i, bd in ipairs(bands) do
      local n = (i == #bands) and left or math.floor(wanted * bd.area / math.max(total, 1e-6) + 0.5)
      n = math.min(n, left)
      while n > 0 do
        local args = {}
        for k, v in pairs(a) do args[k] = v end
        args.area = { min = { bd[1], ay, bd[2] }, max = { bd[3], ay, bd[4] } }
        args.count = n
        args.seed = (a.seed or 1) + i * 7919
        args.margin = math.min(a.margin or 0.25, 0.25)
        local ok, r = ged.try(geo.scatter, args)
        if ok then
          for _, b in ipairs(r.brushes.brushes) do brushes[#brushes + 1] = b end
          left = left - n
          break
        end
        n = n - 1
      end
    end
    if #brushes == 0 then fail(api, "no boulder fits in the bands along the walls of the area.", "Enlarge the area or lower size.") end
    if #brushes < wanted then
      log.info(api .. ": " .. name .. " placed " .. #brushes .. " of " .. wanted .. " boulders (only along the walls and edges; the middle stays clear).")
    end
    rec = { kind = "rocks", name = name, brushes = brushes, count = #brushes, seed = a.seed }
  else
    if mode == "require" and t.along ~= nil then
      -- At the wall foot: each boulder's centre within half the largest size plus 0.75 m of the floor's edge.
      local smax = type(t.size) == "number" and t.size or (type(t.size) == "table" and (t.size[2] or t.size.max) or 2)
      a.band = smax / 2 + 0.75
    end
    rec = rocks_record(name, geo.scatter(a), false)
  end
  rec.group = group_if_many(name, rec.brushes, boolean(api, "group", t.group, true))
  local where = t.along ~= nil and "rubble along the walls of " .. tostring(t.along.name or "the passage") or ("rubble against the walls and edges" .. (context and (" (" .. context .. ")") or ""))
  internals_purpose.tag_named(name, "theme_detail", where, mode)
  rec.role = "theme_detail"
  rec.context = context
  uv.rock(t, name)
  return rec
end

---@kit kit.pit{name?, pos, radius, depth, roughness?, taper?, seed?, group?, texture?, textures?, tile?} → pit — A rough pit dug down from a point on a level floor (geo.pit): a stack of structural air segments meeting on identical faces, narrowing toward a flat floor depth metres down; its top ring stands a little above the floor, so the mouth opens through the floor at any height.
---@opt name: string = pit<n> — Names the pit (tags, group).
---@opt pos: vec — Required: the centre of the pit's mouth, on a level floor (a room, or a level stretch of cave or canyon).
---@opt radius: number — Required: radius of the mouth, metres.
---@opt depth: number — Required: depth of the flat floor below pos, metres (rounded to 1/16, at least 1).
---@opt roughness: number = kit.defaults.roughness (0.2) — Radial jitter as a fraction of the radius (0.05 to 0.5).
---@opt taper: number = 0.7 — Floor radius as a fraction of the mouth radius (0.2 to 2).
---@opt seed: integer = 1 — The random stream.
---@opt group: boolean = true — Group the segments under the pit's name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---@opt tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.pit(t)
  local api = "kit.pit"
  check_keys(api, t, { "name", "pos", "radius", "depth", "roughness", "taper", "seed", "group", "texture", "textures", "tile" })
  local name = name_for(api, "pit", t.name)
  local a = builder_args(api, "pit", name, "ring", t)
  a.pos = t.pos
  a.radius = positive(api, "radius", t.radius)
  a.depth = positive(api, "depth", t.depth)
  a.roughness = t.roughness or kit.defaults.roughness
  a.taper = t.taper
  a.seed = seed_of(api, t)
  local pit = geo.pit(a)
  uv.rock(t, name)
  local brushes = pit.brushes.brushes
  return {
    kind = "pit", name = name, brushes = brushes, group = group_if_many(name, brushes, boolean(api, "group", t.group, true)),
    floor_y = pit.floor_y, min = pit.min, max = pit.max, center = pit.center, radius = pit.radius, depth = pit.depth, seed = pit.seed,
  }
end

-- The fixture placer for kit.light_path (kit_dress.lua installs it: a wall lamp or a lamp post for one light).
local internals_light_fixture = nil

---@kit kit.light_path{along, spacing?, color?, range?, height?, name?, fixture?} → object[] — Light objects along a path record — the station line of a kit.cave, kit.canyon, kit.passage or kit.road, or the legs of a kit.corridor (straight or path) — every spacing metres, each with a visible fixture. Lights hang low enough to light the floor (60 % of the clear height, at most 4.5 m), alternating sides: in front of a lamp on the wall (rock or brush) at that height where one stands within half the path's width plus 4 m (a stock wall lamp when the game tables are loaded, else a small full-bright detail lamp), else under the head of a detail lamp post standing at the walkway's edge, never in its middle. The fixtures are in the returned list's fixtures field.
---@opt along: record — Required: a kit.cave, kit.canyon, kit.passage, kit.road or kit.corridor record.
---@opt spacing: number = kit.defaults.light_spacing (5) — Distance between lights along the path, metres.
---@opt color: {r, g, b} = kit.defaults.light_color — Light colour, 0-255 per channel.
---@opt range: number = reaches across the path and to the next light (at least kit.defaults.light_range_min) — Light range in metres.
---@opt height: number = 60 % of the clear height, at most 4.5 m — Height above the floor, metres.
---@opt name: string = the record's name — Prefix of the lights' names (<name>_light<i>) and their kit.name tag.
---@opt fixture: boolean = true — false: bare lights (on paths wider than 8 m still mounted 0.6 m off the walls, alternating sides).
function kit.light_path(t)
  local api = "kit.light_path"
  check_keys(api, t, { "along", "spacing", "color", "range", "height", "name", "fixture" })
  local r = t.along
  local stations = type(r) == "table" and r.stations or nil
  if type(r) == "table" and stations == nil and r.kind == "corridor" and type(r.brushes) == "table" then
    -- A corridor: its legs' centre lines, in order.
    stations = {}
    for _, b in ipairs(r.brushes) do
      if type(b) == "userdata" and b.air then
        local bb = b.bounds
        local x0, y0, z0, x1, y1, z1 = bb.min.x, bb.min.y, bb.min.z, bb.max.x, bb.max.y, bb.max.z
        local cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
        local w = math.min(x1 - x0, z1 - z0)
        local ends = (x1 - x0 >= z1 - z0) and { { x0, cz }, { x1, cz } } or { { cx, z0 }, { cx, z1 } }
        local first = ends[1]
        if #stations > 0 then
          local last = stations[#stations].pos
          if (ends[2][1] - last.x) ^ 2 + (ends[2][2] - last.z) ^ 2 < (ends[1][1] - last.x) ^ 2 + (ends[1][2] - last.z) ^ 2 then
            ends = { ends[2], ends[1] }
          end
        end
        for _, e in ipairs(ends) do
          stations[#stations + 1] = { pos = vec(e[1], y0, e[2]), floor_y = y0, height = y1 - y0, width = w }
        end
      end
    end
  end
  if type(stations) ~= "table" or #stations == 0 then
    fail(api, "'along' must be a kit.cave, kit.canyon, kit.passage, kit.road or kit.corridor record.")
  end
  local name = t.name ~= nil and name_for(api, "light_path", t.name) or (r.name or name_for(api, "light_path", nil))
  local spacing = positive(api, "spacing", t.spacing, kit.defaults.light_spacing)
  local height = t.height ~= nil and positive(api, "height", t.height) or nil
  local color = t.color or kit.defaults.light_color
  local fixture = boolean(api, "fixture", t.fixture, true)
  local after = record_time_index(r)
  local fixtures = {}

  -- The station line, read once.
  local pts, total = {}, 0
  for i, st in ipairs(stations) do
    local p = st.pos
    if i > 1 then
      local q = pts[i - 1]
      total = total + math.sqrt((p.x - q.x) ^ 2 + (p.z - q.z) ^ 2)
    end
    pts[i] = { x = p.x, z = p.z, y = st.floor_y or p.y, h = st.height or (r.height or 4), w = st.width or r.width or 4, at = total }
  end

  local probe = nil
  local lights = {}
  local n = math.max(1, math.floor(total / spacing + 0.5))
  local seg = 1
  for k = 1, n do
    local s = (k - 0.5) * total / n
    while seg < #pts - 1 and pts[seg + 1].at < s do seg = seg + 1 end
    local a, b = pts[seg], pts[math.min(seg + 1, #pts)]
    local f = (b.at > a.at) and (s - a.at) / (b.at - a.at) or 0
    local h = height or math.min(0.6 * math.min(a.h, b.h), 4.5)
    local x, z = a.x + (b.x - a.x) * f, a.z + (b.z - a.z) * f
    local gy = math.max(a.y, b.y)
    local y = gy + h
    local width = math.min(a.w, b.w)
    local dx, dz = b.x - a.x, b.z - a.z
    local len = math.sqrt(dx * dx + dz * dz)
    local placed = false
    if len > 1e-6 then
      -- The side this light leans to (alternating along the path).
      local sx, sz = -dz / len, dx / len
      if k % 2 == 0 then sx, sz = -sx, -sz end
      if fixture and internals_light_fixture then
        -- A lamp on the wall on this side, else a post at the walkway's edge (the light moves with its fixture).
        local lx, ly, lz, fx = internals_light_fixture(name, k, x, y, z, gy, sx, sz, width, after)
        if lx then
          x, y, z = lx, ly, lz
          for _, f in ipairs(fx) do fixtures[#fixtures + 1] = f end
          placed = true
        end
      end
      if not placed and width > 8 then
        -- Wide paths: mount the light on a wall, 0.6 m off whatever the wall is at that height.
        probe = probe or ged.import("kit_probe").shared()
        local d = probe:wall_distance(x, y, z, sx, sz, width / 2 + 4)
        if d < width / 2 + 4 and d > 1.2 then x, z = x + sx * (d - 0.6), z + sz * (d - 0.6) end
      end
    end
    local range = t.range ~= nil and positive(api, "range", t.range)
      or math.max(kit.defaults.light_range_min, 1.25 * spacing, 1.1 * math.sqrt(width * width + h * h))
    lights[k] = level.place{
      kind = "light", name = name .. "_light" .. k,
      pos = { x, y, z },
      props = { color = color, size_range = range },
      tags = tags_for("light_path", name),
    }
  end
  lights.fixtures = fixtures
  return lights
end

-- Extension modules, each returning install(kit, internals) and adding its functions to this table:
-- kit_struct.lua (ground provider, beams, trusses, bridges, landmarks, openings); kit_space.lua (proportions,
-- shaped rooms, alcoves, pilasters, floor sections, roads and the kit.space archetypes); kit_trace.lua (kit.trace: the literal
-- plan builder for recreating from a reference picture); kit_ground.lua (kit.ground:
-- brush or Alpine terrain ground for open areas, ALPINE-COMPAT-SPEC §4.4); kit_dress.lua and
-- kit_items.lua (population: generative fill kit.dress, light fixtures kit.light_room, protected spawns
-- kit.spawn_points and the item economy kit.items, with the kit_probe.lua space probe); kit_layout.lua
-- (layout graphs, their constraint checker, plan preview and builder); kit_compose.lua (the layout composer:
-- free placement of the zones kit.layout generates); kit_modules.lua (architectural detailing
-- from the module library: wall detail, frames, ceilings, machinery, railings, catwalks, pipes, stairs, rocks).
do
  local internals = {
    fail = fail, check_keys = check_keys, number = number, positive = positive, boolean = boolean, vec3 = vec3, vec = vec,
    name_for = name_for, tags_for = tags_for, emit = emit, group_if_many = group_if_many, fmt = fmt, textures_of = textures_of,
    record_box = record_box, check_walkable = check_walkable, connect = connect, EPS = EPS,
    record_time_index = record_time_index, free_spot = free_spot, solid_near = solid_near,
  }
  ged.import("kit_struct")(kit, internals)
  internals_purpose = { purpose_mode = internals.purpose_mode, tag_role = internals.tag_role, tag_named = internals.tag_named, stair_route = internals.stair_route }
  ged.import("kit_space")(kit, internals)
  ged.import("kit_rock")(kit, internals) -- organic rock that follows a plan (kit.rock_mass; kit.trace's canyon/cave/rock options)
  ged.import("kit_trace")(kit, internals)
  ged.import("kit_ground")(kit, internals)
  ged.import("kit_layout")(kit, internals)
  ged.import("kit_compose")(kit, internals)
  ged.import("kit_compose_payload")(kit, internals)
  ged.import("kit_compose_open")(kit, internals)
  ged.import("kit_dress")(kit, internals)
  internals_light_fixture = internals.light_fixture
  ged.import("kit_items")(kit, internals)
  ged.import("kit_modules")(kit, internals)
  ged.import("kit_trace_dress")(kit, internals) -- kit.dress_trace: theme a kit.trace build from its reference shots
  -- The composite builders: the flights inside them default to purpose = "quiet" (see kit.stairs).
  for _, fname in ipairs({ "space", "build_layout" }) do
    local inner = kit[fname]
    if inner ~= nil then
      kit[fname] = function(t)
        -- (A failed build ends the script, so the depth needs no unwinding.)
        local saved = composite_depth
        composite_depth = saved + 1
        local record = inner(t)
        composite_depth = saved
        return record
      end
    end
  end
end

---@kit kit.help{} → string — The kit's reference as text: every function with its options (types, defaults, meanings); the same as ged.describe("kit").text.
function kit.help(t)
  if t ~= nil then check_keys("kit.help", t, {}) end
  return ged.describe("kit").text
end

uv = ged.import("kit_uv").install(kit, {
  fail = fail, check_keys = check_keys, number = number, positive = positive, boolean = boolean, record_box = record_box,
  name_for = name_for, emit = emit, group_if_many = group_if_many, EPS = EPS, fmt = fmt,
})

return kit
