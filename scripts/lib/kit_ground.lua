-- Glacier design kit: the ground choice (installed into the kit by kit.lua; use it as local kit = ged.import("kit")).
--
-- kit.ground{...} decides whether open ground is built from brushes or as an Alpine terrain, and builds the terrain
-- (ALPINE-COMPAT-SPEC §4.4). Terrain is an option the kit weighs, not the outdoor default:
--   * a terrain cannot seal, cannot form rooms or portals, cannot overhang (a heightfield), cannot rotate, needs an
--     enclosing air volume, and only blends and lights properly on Direct3D 11 (Alpine editor_patch/terrain_build.cpp
--     493-512, 314-359; game_patch/misc/alpine_terrain.cpp 63-87);
--   * brush-built canyons and caves seal, portal and overhang, and look the same on every renderer;
--   * terrain wins for large open ground: smooth, cheap to shape, painted with blended layers.
-- "auto" picks terrain only when the area is large, open to the sky, needs no overhang, is natural ground (not a cave,
-- a canyon or a built deck) and the level already targets Direct3D 11-era features. Canyon walls, caves and
-- architecture always stay brush-built. Every result records the choice, the reason and the inputs; explain = true
-- reports them without building anything.
--
-- A terrain the kit builds goes through the level.terrain API (C8/C9): heights from the kit's ground shape (seeded
-- mounds that fall to the base height at the edges, at doorways and landings and under anything standing on the
-- ground), holes outside the area's outline and where the kit cuts openings, and layers painted from the material
-- intent (ground, worn paths between the openings, rock on the crests). It rests 1/16 m above the brush floor it covers
-- (so the two never share a plane) and is always enclosed in air: an area that is not already inside an air volume
-- gets a sky volume around it, so the level still seals.
--
-- This module returns one function, install(kit, internals), which kit.lua calls after kit_struct and kit_space; it
-- defines nothing global and never reads rng (its mounds come from the seed).

local function install(kit, I)
local fail, check_keys, number, positive, boolean = I.fail, I.check_keys, I.number, I.positive, I.boolean
local vec3, vec, name_for, fmt = I.vec3, I.vec, I.name_for, I.fmt
local inside = I.inside

-- Tunables (kit.defaults): the ground choice and the terrain the kit shapes.
-- A large area: at least 400 m² (kit.proportion's atrium / set-piece class starts above 400 m²) and 16 m across.
kit.defaults.terrain_min_area = 400
kit.defaults.terrain_min_side = 16
-- Mound height (m) of the kit's ground shape, the grid spacing (m; grown when a footprint needs more than 257 vertices,
-- Alpine's limit, alpine_terrain.h:40-41) and the lift above the brush floor the terrain covers.
kit.defaults.terrain_relief = 1.5
kit.defaults.terrain_cell = 0.5
kit.defaults.terrain_lift = 1 / 16
-- Stock textures by material intent: the ground, the worn paths between openings, rock on the crests.
kit.defaults.terrain_textures = { ground = "rck_tanmudfloor01.tga", path = "rck_comflrtrax03.tga", rock = "rck_tanrock01.tga" }

local MODES = { brush = true, terrain = true, auto = true }
-- Record kinds whose ground is never a terrain: caves overhang, canyon floors follow their tube between brush walls.
local NOT_TERRAIN_KIND = { cave = "a cave overhangs: caves stay brush-built", canyon = "a canyon's floor follows its brush walls: canyons stay brush-built",
  passage = "a passage is roofed: passages stay brush-built", tunnel = "a tunnel is roofed" }

local function smooth(x, a, b)
  if x <= a then return 0 elseif x >= b then return 1 end
  local t = (x - a) / (b - a)
  return t * t * (3 - 2 * t)
end

-- Distance from (x, z) to the nearest edge of polygon p.
local function edge_dist(p, x, z)
  local best = math.huge
  for i = 1, #p do
    local a, b = p[i], p[i % #p + 1]
    local ex, ez = b[1] - a[1], b[2] - a[2]
    local L2 = ex * ex + ez * ez
    local t = 0
    if L2 > 0 then t = math.max(0, math.min(1, ((x - a[1]) * ex + (z - a[2]) * ez) / L2)) end
    local px, pz = a[1] + ex * t, a[2] + ez * t
    best = math.min(best, math.sqrt((x - px) ^ 2 + (z - pz) ^ 2))
  end
  return best
end

local function poly_area(p)
  local s = 0
  for i = 1, #p do local a, b = p[i], p[i % #p + 1] s = s + a[1] * b[2] - b[1] * a[2] end
  return math.abs(s) / 2
end

local function rect_poly(x0, z0, x1, z1) return { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } } end

-- A polygon option: {{x, z}...} (or {x, y, z} points; y ignored). min_points: 3 for a polygon, 2 for a polyline.
local function polygon(api, key, v, min_points)
  min_points = min_points or 3
  if type(v) ~= "table" or #v < min_points then
    fail(api, "'" .. key .. "' must be a " .. (min_points == 3 and "polygon of 3" or "polyline of 2") .. " or more {x, z} points.")
  end
  local out = {}
  for i, q in ipairs(v) do
    if type(q) ~= "table" then fail(api, "'" .. key .. "[" .. i .. "]' must be a point {x, z}.") end
    local x, z = q[1], q[3] ~= nil and q[3] or q[2]
    if q.x ~= nil then x, z = q.x, q.z end
    if type(x) ~= "number" or type(z) ~= "number" then fail(api, "'" .. key .. "[" .. i .. "]' must be a point {x, z} of numbers.") end
    out[#out + 1] = { x, z }
  end
  return out
end

local function polyline(api, key, v) return polygon(api, key, v, 2) end

-- An opening or flat place: a kit record (its min / max box or its outline), a {min, max} box, a polygon, or (flat)
-- {x, z, r}. Returns a polygon, or a circle {x, z, r}.
local function region(api, key, v)
  if type(v) ~= "table" then fail(api, "'" .. key .. "' must be a record, a {min, max} box, a polygon or {x, z, r}.") end
  if type(v.outline) == "table" and #v.outline >= 3 then return polygon(api, key .. ".outline", v.outline) end
  if v.min ~= nil and v.max ~= nil then
    local x0, _, z0 = vec3(api, key .. ".min", v.min)
    local x1, _, z1 = vec3(api, key .. ".max", v.max)
    return rect_poly(math.min(x0, x1), math.min(z0, z1), math.max(x0, x1), math.max(z0, z1))
  end
  if type(v[1]) == "number" and type(v[2]) == "number" and type(v[3]) == "number" and v[4] == nil then
    return { circle = true, x = v[1], z = v[2], r = v[3] }
  end
  if v.x ~= nil and v.z ~= nil and v.r ~= nil then return { circle = true, x = v.x, z = v.z, r = v.r } end
  return polygon(api, key, v)
end

local function region_list(api, key, v)
  if v == nil then return {} end
  if type(v) ~= "table" then fail(api, "'" .. key .. "' must be a list.") end
  local out = {}
  for i, r in ipairs(v) do out[#out + 1] = region(api, key .. "[" .. i .. "]", r) end
  return out
end

local function region_dist(r, x, z)
  if r.circle then return math.sqrt((x - r.x) ^ 2 + (z - r.z) ^ 2) - r.r end
  if inside(r, x, z) then return 0 end
  return edge_dist(r, x, z)
end

-- ---------------------------------------------------------------------------------------------
-- The decision (§4.4)
-- ---------------------------------------------------------------------------------------------

-- What marks the level as targeting Direct3D 11-era features: the Require Direct3D 11 or D3D11-only lightmaps setting,
-- or a terrain already in it.
local function d3d11_signals()
  local s = {}
  local A = level.alpine
  if A:get("require_d3d11_setting") == true then s[#s + 1] = "require_d3d11_setting" end
  if A:get("d3d11_only_lightmaps_setting") == true then s[#s + 1] = "d3d11_only_lightmaps_setting" end
  if level.terrain.count > 0 then s[#s + 1] = "terrain" end
  return s
end

-- True when the ceiling straight above (x, y, z) is the sky: the air brush whose top makes it shows the sky.
local function sky_above(x, y, z)
  local cy = I.ceiling_point(x, z, y)
  if cy == nil then return false end
  local q = level.brushes_in{ min = { x - 0.01, cy - 0.02, z - 0.01 }, max = { x + 0.01, cy + 0.02, z + 0.01 } }
  for _, b in ipairs(q.brushes) do
    if b.air and not b.portal and math.abs(b.bounds.max.y - cy) < 0.02 then
      for _, f in ipairs(b:faces_facing("up")) do if f.show_sky then return true end end
    end
  end
  return false
end

-- Up to 25 probe points spread over a footprint, inside its outline.
local function probe_points(x0, z0, x1, z1, outline)
  local pts = {}
  for i = 0, 4 do
    for k = 0, 4 do
      local x = x0 + (x1 - x0) * (0.1 + 0.2 * i)
      local z = z0 + (z1 - z0) * (0.1 + 0.2 * k)
      if outline == nil or inside(outline, x, z) then pts[#pts + 1] = { x, z } end
    end
  end
  return pts
end

-- The §4.4 decision. a = {area, min_side, kind, natural, open (true / false / nil = probe), open_share, overhang,
-- d3d11 (true = declared)}; mode = "brush" | "terrain" | "auto". Returns choice, reason, inputs, possible.
local function decide(a, mode)
  local signals = d3d11_signals()
  if a.d3d11 == true then table.insert(signals, 1, "declared") end
  local inputs = {
    area = math.floor(a.area * 10 + 0.5) / 10, min_side = math.floor(a.min_side * 100 + 0.5) / 100, kind = a.kind,
    large = a.area >= kit.defaults.terrain_min_area - 1e-6 and a.min_side >= kit.defaults.terrain_min_side - 1e-6,
    open_topped = a.open == true, open_share = a.open_share, overhang = a.overhang == true, natural = a.natural ~= false,
    d3d11 = #signals > 0, d3d11_signals = signals, rotated = a.rotated or false,
  }
  -- Terrain at all: natural, open ground with no overhang (a heightfield under the sky).
  local why_not = nil
  if a.refuse then why_not = a.refuse
  elseif not inputs.natural then why_not = "it is architecture (" .. tostring(a.kind) .. "): built floors stay brush-built"
  elseif inputs.overhang then why_not = "it needs an overhang or more than one floor level, which a heightfield cannot make"
  elseif not inputs.open_topped then
    why_not = "it is not open to the sky" .. (a.open_share and (" (" .. math.floor(a.open_share * 100 + 0.5) .. "% of it)") or "") ..
      ": terrain is for open outdoor ground"
  end
  inputs.possible = why_not == nil
  local size = fmt(inputs.area) .. " m², " .. fmt(inputs.min_side) .. " m across"
  if mode == "brush" then return "brush", "asked for brush ground", inputs end
  if mode == "terrain" then
    if why_not then return "brush", "terrain refused: " .. why_not, inputs end
    return "terrain", "asked for terrain ground (" .. size .. ")", inputs
  end
  if why_not then return "brush", "auto: brush ground, because " .. why_not, inputs end
  if not inputs.large then
    return "brush", "auto: brush ground, because the area is small (" .. size .. "; terrain pays off from " .. kit.defaults.terrain_min_area ..
      " m² and " .. kit.defaults.terrain_min_side .. " m across)", inputs
  end
  if not inputs.d3d11 then
    return "brush", "auto: brush ground, because the level does not target Direct3D 11 (terrain blends and lights properly only there; set " ..
      "level.alpine require_d3d11_setting or pass d3d11 = true)", inputs
  end
  return "terrain", "auto: terrain, because the area is large (" .. size .. "), open to the sky, needs no overhang and the level targets Direct3D 11 (" ..
    table.concat(signals, ", ") .. ")", inputs
end
I.ground_decide = decide

-- ---------------------------------------------------------------------------------------------
-- Building the terrain
-- ---------------------------------------------------------------------------------------------

-- Builds a terrain over s = {name, x0, z0, x1, z1, y, outline, holes, flats, paths, relief, seed, cell, layers, lift}
-- with the level.terrain API. Returns the handle and {nx, nz, cell, holes, top, mounds}.
local function build_terrain(api, s)
  local c = s.cell
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  local nx, nz
  while true do
    nx, nz = math.ceil(w / c - 1e-6) + 1, math.ceil(d / c - 1e-6) + 1
    if nx <= 257 and nz <= 257 then break end
    c = c * 2
  end
  nx, nz = math.max(nx, 2), math.max(nz, 2)
  local sx, sz = (nx - 1) * c, (nz - 1) * c
  local outline = s.outline or rect_poly(s.x0, s.z0, s.x1, s.z1)
  local area = poly_area(outline)

  -- The ground shape: seeded mounds inside the outline, falling to the base at the edge band, at the flat places
  -- (doorways, landings, what stands on the ground) and never below it.
  local st = I.stream(s.seed * 7919 + 101)
  local mounds = {}
  local want = math.max(2, math.min(14, math.floor(area / 110)))
  for _ = 1, want do
    for _ = 1, 24 do
      local mx, mz = s.x0 + st() * w, s.z0 + st() * d
      local r = 3 + st() * 4.5
      local h = 0.55 + st() * 0.45
      if inside(outline, mx, mz) and edge_dist(outline, mx, mz) > 2 then mounds[#mounds + 1] = { x = mx, z = mz, r = r, h = h } break end
    end
  end
  local edge0, edge1 = 1.25, 5
  local fall = 3.5 -- (a flat place's level ground reaches 0.5 m beyond it and blends back in over 3 m)
  local flats = {}
  for i, f in ipairs(s.flats) do
    local bx0, bz0, bx1, bz1
    if f.circle then bx0, bz0, bx1, bz1 = f.x - f.r, f.z - f.r, f.x + f.r, f.z + f.r
    else
      bx0, bz0, bx1, bz1 = math.huge, math.huge, -math.huge, -math.huge
      for _, q in ipairs(f) do bx0, bz0, bx1, bz1 = math.min(bx0, q[1]), math.min(bz0, q[2]), math.max(bx1, q[1]), math.max(bz1, q[2]) end
    end
    flats[i] = { f = f, x0 = bx0 - fall, z0 = bz0 - fall, x1 = bx1 + fall, z1 = bz1 + fall }
  end
  -- e = distance to the outline's edge (the caller knows the point is inside it)
  local function shape(wx, wz, e)
    local v = 0
    for i = 1, #mounds do
      local m = mounds[i]
      local dx, dz = (wx - m.x) / m.r, (wz - m.z) / m.r
      local q = dx * dx + dz * dz
      if q < 6 then v = v + m.h * math.exp(-q) end
    end
    if v <= 0 then return 0 end
    v = v / (1 + 0.35 * v) -- (mounds that overlap swell, never spike)
    v = v * smooth(e, edge0, edge1)
    for i = 1, #flats do
      local F = flats[i]
      if v > 0 and wx >= F.x0 and wx <= F.x1 and wz >= F.z0 and wz <= F.z1 then v = v * smooth(region_dist(F.f, wx, wz), 0.5, fall) end
    end
    return v
  end
  local relief, lift = s.relief, s.lift
  local terrain = level.terrain.create{ center = { s.x0 + sx / 2, s.y, s.z0 + sz / 2 }, nx = nx, nz = nz, cell_size = c, name = s.name,
    layers = { s.layers.ground, s.layers.path, s.layers.rock }, height_range = math.max(0.5, relief + lift + 0.25) }
  -- The base ground repeats every 8 m (a 4 m repeat of one rock texture over a whole platform reads as a grid from
  -- above), the paths at the kit's 4 m, the rock at 6 m.
  terrain:set_layers{ { texture = s.layers.ground, uv_scale = 8 }, { texture = s.layers.path, uv_scale = 4 }, { texture = s.layers.rock, uv_scale = 6 } }
  -- One pass over the vertices: inside the outline (or on it) and the height. Lists, not per-vertex callbacks: a list
  -- crosses into the editor once.
  local heights, vin = {}, {}
  for iz = 0, nz - 1 do
    local wz = s.z0 + iz * c
    for ix = 0, nx - 1 do
      local wx = s.x0 + ix * c
      local e = edge_dist(outline, wx, wz)
      local ok = e < 1e-4 or inside(outline, wx, wz)
      vin[iz * nx + ix] = ok
      heights[#heights + 1] = lift + ((ok and relief > 0) and relief * shape(wx, wz, e) or 0)
    end
  end
  terrain:set_heights(heights, { fit = true })

  -- Holes: every cell with a corner outside the outline (the cell is not wholly inside it), and the openings.
  local cells = {}
  local nh = 0
  for iz = 0, nz - 2 do
    local wz = s.z0 + (iz + 0.5) * c
    for ix = 0, nx - 2 do
      local wx = s.x0 + (ix + 0.5) * c
      local hole = not (vin[iz * nx + ix] and vin[iz * nx + ix + 1] and vin[(iz + 1) * nx + ix] and vin[(iz + 1) * nx + ix + 1])
      if not hole then
        for i = 1, #s.holes do
          local r = s.holes[i]
          if r.circle then
            if (wx - r.x) ^ 2 + (wz - r.z) ^ 2 < r.r * r.r then hole = true break end
          elseif inside(r, wx, wz) then hole = true break end
        end
      end
      if hole then nh = nh + 1 end
      cells[#cells + 1] = hole
    end
  end
  local holes = nh > 0 and terrain:set_holes(cells) or 0

  -- Layers from the material intent: worn paths, rock on the crests (layer 1, the ground, is the base).
  for _, p in ipairs(s.paths) do
    if #p >= 2 then terrain:paint{ layer = 2, path = p, radius = s.path_radius or 1.75, strength = 1, falloff = "smooth" } end
  end
  local top = s.y + lift
  for _, m in ipairs(mounds) do
    local peak = relief * shape(m.x, m.z, edge_dist(outline, m.x, m.z))
    top = math.max(top, s.y + lift + peak)
    if peak >= 0.6 then terrain:paint{ layer = 3, at = { m.x, m.z }, radius = m.r * 0.6, strength = 0.75 } end
  end
  return terrain, { nx = nx, nz = nz, cell = c, holes = holes, top = top, mounds = #mounds }
end
I.ground_build = build_terrain

-- Whether the terrain over s is enclosed in air: probes over the footprint just above the base and above the highest
-- mound. Returns in_air, probes.
local function enclosure(s, top)
  local n, ok = 0, 0
  for _, p in ipairs(probe_points(s.x0, s.z0, s.x1, s.z1, s.outline)) do
    for _, y in ipairs({ s.y + 0.02, top + 1 }) do
      n = n + 1
      if I.air_at(p[1], y, p[2]) then ok = ok + 1 end
    end
  end
  return ok, n
end
I.ground_enclosure = enclosure

-- A sky volume around a terrain over open space: an air box from the base height to sky metres above the highest point,
-- 2 m beyond the footprint, its floor in the ground texture and every other face showing the sky (plus the level's sky
-- room when it has none), so the level seals around the terrain.
local function sky_volume(name, s, top, height)
  local m = 2
  local b = I.hull("ground", name, "sky", I.box_points(s.x0 - m, s.y, s.z0 - m, s.x1 + m, top + height, s.z1 + m),
    { air = true, texture = kit.defaults.sky_texture })
  b:box_map{ tile = 64 }
  for _, f in ipairs(b.faces) do
    if f.normal.y > 0.7 or f.normal.y < -0.7 then
      -- (an air brush's faces point out of the space: the floor's normal points down)
      if f.normal.y < -0.7 then f:set_texture(s.layers.ground) else f:set_flag("show_sky", true) end
    else
      f:set_flag("show_sky", true)
    end
  end
  local sky = I.ensure_sky(name)
  return b, sky
end

-- ---------------------------------------------------------------------------------------------
-- kit.ground
-- ---------------------------------------------------------------------------------------------

---@kit kit.ground{area?|min?, max?, outline?, y?, mode?, explain?, name?, relief?, seed?, cell?, holes?, flat?, paths?, layers?, d3d11?, sky?} → ground — Brush or terrain ground for an open area (ALPINE-COMPAT-SPEC §4.4): "auto" builds an Alpine terrain only when the area is large (kit.defaults.terrain_min_area / terrain_min_side), open to the sky, needs no overhang, is natural ground (caves, canyons and built decks stay brushes) and the level targets Direct3D 11 (require_d3d11_setting, d3d11_only_lightmaps_setting, a terrain already in the level, or d3d11 = true); "terrain" builds one unless the area cannot take it (an error); "brush" keeps the brush floor. A terrain gets heights from the kit's ground shape (seeded mounds, level at the edges, the flat places and the openings), holes outside the outline and at the openings, layers painted from the material intent (ground, worn paths, rock on the crests), rests 1/16 m above the brush floor and is enclosed in air (a sky volume when the area is not already in air). Returns {kind = "ground", name, mode, choice ("brush" or "terrain"), reason, inputs = {area, min_side, kind, large, open_topped, open_share, overhang, natural, d3d11, d3d11_signals, possible}, built, terrain, uid, min, max, y, top, cell, cells = {x, z}, holes, layers, sky, warnings}.
---@opt area: record = none — The area: a kit room, a kit.build_layout zone report, a kit.sky_room or any record with min / max (its outline, floor_y, sky, kind or archetype are read when present). Give area, or min and max.
---@opt min: vec — One corner of the ground rectangle (its y is the floor height unless y is given).
---@opt max: vec — The opposite corner.
---@opt outline: {x, z}[] = the area's outline, else the rectangle — The ground's plan in world metres; terrain cells not wholly inside it are holes.
---@opt y: number = the area's floor — The brush floor height the ground rests on (the terrain sits terrain_lift above it).
---@opt mode: string = "auto" — "auto", "terrain" or "brush".
---@opt explain: boolean = false — Only decide: return the choice, reason and inputs (and the grid a terrain would get) without changing the level.
---@opt name: string = ground<n> — The terrain's script name and the tag of the sky volume.
---@opt relief: number = kit.defaults.terrain_relief (1.5) — Mound height in metres (0 = level ground).
---@opt seed: integer = 1 — The mounds' stream; the same options and seed give the same ground.
---@opt cell: number = kit.defaults.terrain_cell (0.5) — Grid spacing in metres (doubled until the footprint fits 257 vertices a side).
---@opt holes: table[] = none — Openings cut through the ground: records, {min, max} boxes, polygons or {x, z, r} circles.
---@opt flat: table[] = none — Places kept level at the base height (doorways, landings, what stands on the ground): records, boxes, polygons or {x, z, r}.
---@opt paths: {x, z}[][] = none — Walkways painted with the path layer (polylines in world metres).
---@opt layers: {ground=, path=, rock=} = kit.defaults.terrain_textures — The three layer textures by material intent.
---@opt d3d11: boolean = false — Declare that the level targets Direct3D 11 (an "auto" input).
---@opt sky: boolean = true — Build a sky volume around the terrain when the area is not already in air (false: refuse instead).
function kit.ground(t)
  local api = "kit.ground"
  t = t or {}
  check_keys(api, t, { "area", "min", "max", "outline", "y", "mode", "explain", "name", "relief", "seed", "cell", "holes", "flat", "paths",
    "layers", "d3d11", "sky" })
  local mode = t.mode or "auto"
  if not MODES[mode] then fail(api, "'mode' must be \"brush\", \"terrain\" or \"auto\" (got " .. tostring(mode) .. ").") end
  local explain = boolean(api, "explain", t.explain, false)
  if t.area ~= nil and (t.min ~= nil or t.max ~= nil) then fail(api, "give either 'area' (a record) or 'min' and 'max', not both.") end

  -- The area: footprint, outline, floor, and what the record says about it.
  local x0, y0, z0, x1, z1, outline, kind, rec_open, overhang, natural, rotated, refuse = nil, nil, nil, nil, nil, nil, "box", nil, false, true, false, nil
  if t.area ~= nil then
    local r = t.area
    if type(r) ~= "table" or r.min == nil or r.max == nil then fail(api, "'area' must be a kit record with min / max (a room, a zone report, a sky).") end
    local ax0, ay0, az0 = vec3(api, "area.min", r.min)
    local ax1, _, az1 = vec3(api, "area.max", r.max)
    x0, z0, x1, z1 = math.min(ax0, ax1), math.min(az0, az1), math.max(ax0, ax1), math.max(az0, az1)
    y0 = r.floor_y or ay0
    if type(r.outline) == "table" and #r.outline >= 3 then outline = polygon(api, "area.outline", r.outline) end
    kind = r.archetype or r.kind or "room"
    if NOT_TERRAIN_KIND[kind] then refuse = NOT_TERRAIN_KIND[kind] end
    if r.sky == true then rec_open = true end
    if type(r.levels) == "table" and #r.levels > 1 then overhang = true end
    if r.archetype ~= nil and kit.layout_archetypes ~= nil then
      -- a kit.build_layout zone report: its archetype says whether its ground is natural (isles, gardens) or built
      for _, a in ipairs(kit.layout_archetypes{}) do if a.name == r.archetype then natural = a.natural_ground == true end end
    end
    if (r.rot or 0) % 90 ~= 0 then rotated = true end
  else
    if t.min == nil or t.max == nil then fail(api, "give 'area' (a record) or both 'min' and 'max'.", "For example kit.ground{min = {-30, 0, -30}, max = {30, 0, 30}}.") end
    local ax0, ay0, az0 = vec3(api, "min", t.min)
    local ax1, _, az1 = vec3(api, "max", t.max)
    x0, z0, x1, z1 = math.min(ax0, ax1), math.min(az0, az1), math.max(ax0, ax1), math.max(az0, az1)
    y0 = ay0
  end
  if t.outline ~= nil then outline = polygon(api, "outline", t.outline) end
  if t.y ~= nil then y0 = number(api, "y", t.y) end
  if x1 - x0 < 1 or z1 - z0 < 1 then fail(api, "the area is " .. fmt(x1 - x0) .. " x " .. fmt(z1 - z0) .. " m; ground needs at least 1 m each way.") end
  local area = outline and poly_area(outline) or (x1 - x0) * (z1 - z0)
  local min_side = math.min(x1 - x0, z1 - z0)

  -- Open to the sky: what the record says, else probes over the footprint (the ceiling above each is a show-sky face).
  local open, open_share = rec_open, nil
  if open == nil then
    local pts = probe_points(x0, z0, x1, z1, outline)
    local n = 0
    for _, p in ipairs(pts) do if sky_above(p[1], y0 + 0.5, p[2]) then n = n + 1 end end
    open_share = #pts > 0 and n / #pts or 0
    -- Over open space (no brush anywhere above or below), a terrain gets its own sky volume: open.
    local any_air = false
    for _, p in ipairs(pts) do if I.air_at(p[1], y0 + 0.5, p[2]) then any_air = true break end end
    if not any_air and t.sky ~= false then open, open_share = true, 1 else open = open_share >= 0.9 end
  end
  local d3d11 = boolean(api, "d3d11", t.d3d11, false)
  local choice, reason, inputs = decide({ area = area, min_side = min_side, kind = kind, natural = natural, open = open, open_share = open_share,
    overhang = overhang, d3d11 = d3d11, rotated = rotated, refuse = refuse }, mode)
  if mode == "terrain" and choice ~= "terrain" then fail(api, reason .. ".", "Use mode = \"brush\" (or \"auto\") for this area.") end

  local relief = number(api, "relief", t.relief, kit.defaults.terrain_relief)
  if relief < 0 then fail(api, "'relief' must be 0 or more.") end
  local seed = number(api, "seed", t.seed, 1)
  if seed ~= math.floor(seed) then fail(api, "'seed' must be an integer.") end
  local cell = positive(api, "cell", t.cell, kit.defaults.terrain_cell)
  if cell < 0.125 or cell > 64 then fail(api, "'cell' must be 0.125 to 64 m (Alpine's limits).") end
  local layers = t.layers or kit.defaults.terrain_textures
  if type(layers) ~= "table" then fail(api, "'layers' must be {ground =, path =, rock =}.") end
  local L = { ground = layers.ground or kit.defaults.terrain_textures.ground, path = layers.path or kit.defaults.terrain_textures.path,
    rock = layers.rock or kit.defaults.terrain_textures.rock }
  local holes, flats = region_list(api, "holes", t.holes), region_list(api, "flat", t.flat)
  local paths = {}
  if t.paths ~= nil then
    if type(t.paths) ~= "table" then fail(api, "'paths' must list polylines of {x, z} points.") end
    for i, p in ipairs(t.paths) do paths[i] = polyline(api, "paths[" .. i .. "]", p) end
  end

  local result = {
    kind = "ground", mode = mode, choice = choice, reason = reason, inputs = inputs, built = false, explain = explain,
    min = vec(x0, y0, z0), max = vec(x1, y0, z1), y = y0, layers = L, warnings = {},
  }
  if explain then
    if choice == "terrain" then
      local c = cell
      local nx, nz
      repeat
        nx, nz = math.ceil((x1 - x0) / c - 1e-6) + 1, math.ceil((z1 - z0) / c - 1e-6) + 1
        if nx > 257 or nz > 257 then c = c * 2 end
      until nx <= 257 and nz <= 257
      result.cell, result.cells = c, { nx - 1, nz - 1 }
    end
    return result
  end
  if choice ~= "terrain" then return result end

  local name = name_for(api, "ground", t.name)
  local s = { name = name, x0 = x0, z0 = z0, x1 = x1, z1 = z1, y = y0, outline = outline, holes = holes, flats = flats, paths = paths,
    relief = relief, seed = seed, cell = cell, layers = L, lift = kit.defaults.terrain_lift }
  -- Enclosed in air before building: a terrain in no room has no collision (terrain_build.cpp:493-512).
  local estimate_top = y0 + kit.defaults.terrain_lift + relief
  local ok, n = enclosure(s, estimate_top)
  local sky_brush, sky_room = nil, nil
  if ok < n then
    if ok == 0 and t.sky ~= false then
      sky_brush, sky_room = sky_volume(name, s, estimate_top, 12)
    elseif ok == 0 then
      fail(api, "the area is not inside any air volume, and sky = false: a terrain in no room has no collision and the level would not seal.",
        "Build it inside an outdoor room (kit.room + kit.sky_room{open = ...}) or leave sky on.")
    else
      I.warn(api, result.warnings, (n - ok) .. " of " .. n .. " probe points over the ground are inside solid: the terrain has no collision there " ..
        "(Alpine RED's build lint). Extend the air around it.")
    end
  end
  local terrain, info = build_terrain(api, s)
  result.built, result.terrain, result.uid = true, terrain, terrain.uid
  result.name, result.cell, result.cells, result.holes, result.top = name, info.cell, { info.nx - 1, info.nz - 1 }, info.holes, info.top
  result.sky, result.sky_room = sky_brush, sky_room
  return result
end

end

return install
