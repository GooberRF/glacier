-- Glacier design kit: structures and the ground seam (installed into the kit by kit.lua; use it as
-- local kit = ged.import("kit")).
--
-- kit.ground_at / kit.ground_region / kit.ground_provider: every placement rule reads the ground through
-- this one provider. The built-in provider ("brush") finds floors from the level's brushes (CSG time
-- order, detail solids on top); another provider (Alpine terrain, say) plugs in with
-- kit.ground_provider{name =, at =, region =} without changing any generator.
--
-- kit.beam, kit.truss, kit.bridge, kit.landmark: built structure as DETAIL convex brushes (chords, posts,
-- diagonals, decks), so they never seal or split rooms and cannot open a leak. kit.opening: windows,
-- arches and rough mouths between two spaces, with the mouth-size check.
--
-- Scale follows the exemplar study (docs/internal/DESIGN-QUALITY-SPEC.md §1 finding 10, §c.1-5): landmark
-- bridges span 12-27 m, 2-4 m wide, over 5-12 m drops; outdoor landmarks stand 25-40 m tall.
--
-- This module returns one function, install(kit, internals), which kit.lua calls with its private
-- helpers; it defines nothing global and never reads rng.

local function install(kit, I)
local fail, check_keys, number, positive, boolean = I.fail, I.check_keys, I.number, I.positive, I.boolean
local vec3, vec, name_for, group_if_many, fmt = I.vec3, I.vec, I.name_for, I.group_if_many, I.fmt
local textures_of, record_box, EPS = I.textures_of, I.record_box, I.EPS

-- ---------------------------------------------------------------------------------------------
-- Shared helpers (also handed to kit_space through I)
-- ---------------------------------------------------------------------------------------------

-- One convex brush (the hull of points) at the end of time, tagged like every kit brush and box-mapped
-- at the kit's tile.
local function hull(fn, name, part, points, o)
  o = o or {}
  local spec = {
    points = points, tile = kit.defaults.tile_metres,
    tags = { kit = fn, ["kit.name"] = name, ["kit.part"] = part },
  }
  if o.air then spec.air = true end
  if o.detail then spec.detail = true end
  if o.texture ~= nil then spec.texture = o.texture elseif o.textures ~= nil then spec.textures = o.textures end
  local b = level.brush_from_points(spec)
  if kit.defaults.ppm then b:box_map{ ppm = kit.defaults.ppm } end
  return b
end
I.hull = hull

-- An axis-aligned box as hull points (min/max corners).
local function box_points(x0, y0, z0, x1, y1, z1)
  return {
    { x0, y0, z0 }, { x1, y0, z0 }, { x0, y1, z0 }, { x1, y1, z0 },
    { x0, y0, z1 }, { x1, y0, z1 }, { x0, y1, z1 }, { x1, y1, z1 },
  }
end
I.box_points = box_points

local function sub(a, b) return { a[1] - b[1], a[2] - b[2], a[3] - b[3] } end
local function add(a, b) return { a[1] + b[1], a[2] + b[2], a[3] + b[3] } end
local function mul(a, s) return { a[1] * s, a[2] * s, a[3] * s } end
local function len(a) return math.sqrt(a[1] * a[1] + a[2] * a[2] + a[3] * a[3]) end
local function cross(a, b) return { a[2] * b[3] - a[3] * b[2], a[3] * b[1] - a[1] * b[3], a[1] * b[2] - a[2] * b[1] } end
local function unit(a) local l = len(a) return { a[1] / l, a[2] / l, a[3] / l } end
I.v3 = { sub = sub, add = add, mul = mul, len = len, cross = cross, unit = unit }

-- A deterministic stream in [0, 1) (Park-Miller), for seeded shapes; the kit never reads rng.
local function stream(seed)
  local s = (math.floor(seed) % 2147483646) + 1
  return function()
    s = (s * 16807) % 2147483647
    return (s - 1) / 2147483646
  end
end
I.stream = stream

local function point(api, key, v)
  local x, y, z = vec3(api, key, v)
  return { x, y, z }
end

-- A size given as a number (square section) or {w, h}.
local function section(api, key, v, default)
  if v == nil then return default, default end
  if type(v) == "number" then
    if not (v > 0) then fail(api, "'" .. key .. "' must be greater than 0.") end
    return v, v
  end
  if type(v) == "table" and type(v[1]) == "number" and type(v[2]) == "number" and v[1] > 0 and v[2] > 0 then
    return v[1], v[2]
  end
  fail(api, "'" .. key .. "' must be a number or {w, h} (metres, both > 0).")
end

local function struct_textures(api, t)
  local texture, textures = textures_of(api, t)
  return { texture = texture, textures = textures }
end

local function warn(api, list, message)
  list[#list + 1] = message
  log.warn(api .. ": " .. message)
end
I.warn = warn

-- ---------------------------------------------------------------------------------------------
-- The ground provider
-- ---------------------------------------------------------------------------------------------

local SPAN = 8192

-- The brush provider: the solid/air state of a point is decided by the structural brushes containing it
-- in CSG time order (the world starts solid; air carves, solid fills), then any detail solid fills. An air
-- portal brush carves too, after every other brush (RED applies its portal list after its brush list; a
-- kit.doorway{portal = true} opening is carved by its portal brush alone); a solid portal brush carves nothing.
local function column(x, z)
  local q = level.brushes_in{ min = { x - 0.001, -SPAN, z - 0.001 }, max = { x + 0.001, SPAN, z + 0.001 } }
  local structural, details, portals = {}, {}, {}
  local lo, hi = math.huge, -math.huge
  for _, b in ipairs(q.brushes) do
    if not b.portal or b.air then
      if b.portal then
        portals[#portals + 1] = b
      elseif b.detail then
        if b.solid then details[#details + 1] = b end
      else
        structural[#structural + 1] = b
      end
      local bb = b.bounds
      lo, hi = math.min(lo, bb.min.y), math.max(hi, bb.max.y)
    end
  end
  table.sort(structural, function(a, b) return a.time_index < b.time_index end)
  for i = 1, #portals do structural[#structural + 1] = portals[i] end
  return structural, details, lo, hi
end

local function solid_at(structural, details, x, y, z)
  local s = true
  for i = 1, #structural do
    local b = structural[i]
    if b:contains(x, y, z) then s = b.solid end
  end
  if not s then
    for i = 1, #details do
      if details[i]:contains(x, y, z) then return true, details[i] end
    end
  end
  return s
end

-- The highest floor at or below y (default: the top of whatever stands in the column): march down to the
-- first air, then to the first solid under it, then bisect the boundary to 1/512 m.
local function brush_at(x, z, y)
  local structural, details, lo, hi = column(x, z)
  if lo == math.huge then return nil end
  local step = 0.25
  local cur = y or (hi + 1)
  local bottom = lo - 1
  -- 1. Into air (a start point inside solid walks down until it leaves the solid).
  while solid_at(structural, details, x, cur, z) do
    cur = cur - step
    if cur < bottom then return nil end
  end
  -- 2. Down to solid.
  local above = cur
  local hit_detail = nil
  while true do
    cur = cur - step
    if cur < bottom then return nil end
    local s, d = solid_at(structural, details, x, cur, z)
    if s then hit_detail = d break end
    above = cur
  end
  -- 3. Bisect [cur (solid), above (air)].
  local a, b = cur, above
  for _ = 1, 8 do
    local m = (a + b) / 2
    if solid_at(structural, details, x, m, z) then a = m else b = m end
  end
  local gy = (a + b) / 2
  -- Snap to the 1/64 m grid when the boundary sits on it (kit floors do), else keep the bisected value.
  local snapped = math.floor(gy * 64 + 0.5) / 64
  if math.abs(snapped - gy) < 0.004 then gy = snapped end
  return gy, hit_detail ~= nil
end

local function brush_provider_at(x, z, y)
  local gy, detail = brush_at(x, z, y)
  if gy == nil then return { kind = "void" } end
  -- The normal from the floors half a metre either side (only those within 1 m of this one).
  local function near(px, pz)
    local h = brush_at(px, pz, gy + 1)
    if h ~= nil and math.abs(h - gy) <= 1 then return h end
    return gy
  end
  local d = 0.5
  local gx = (near(x + d, z) - near(x - d, z)) / (2 * d)
  local gz = (near(x, z + d) - near(x, z - d)) / (2 * d)
  local n = unit({ -gx, 1, -gz })
  return { y = gy, normal = n, kind = detail and "detail" or "brush" }
end

local provider = { name = "brush", at = brush_provider_at }

local function ground_point(x, z, y)
  local r = provider.at(x, z, y)
  if type(r) ~= "table" then return { kind = "void", provider = provider.name } end
  local out = { kind = r.kind or (r.y and "ground" or "void"), provider = provider.name, x = x, z = z }
  if r.y ~= nil then
    out.y = r.y
    local n = r.normal or { 0, 1, 0 }
    if type(n) == "userdata" then n = { n.x, n.y, n.z } end
    out.normal = vec(n[1], n[2], n[3])
    out.slope = math.deg(math.acos(math.max(-1, math.min(1, n[2]))))
    out.pos = vec(x, r.y, z)
  end
  return out
end
I.ground_point = ground_point

-- The lowest solid at or above (x, y, z) in the brush column (a ceiling, a beam, a detail top hanging over
-- the point), bisected to 1/512 m; nil when the point is inside solid or nothing solid lies above it.
local function ceiling_point(x, z, y)
  local structural, details, lo, hi = column(x, z)
  if lo == math.huge or solid_at(structural, details, x, y, z) then return nil end
  local step = 0.25
  local cur, below = y, y
  while true do
    cur = cur + step
    if cur > hi + 1 then return nil end
    if solid_at(structural, details, x, cur, z) then break end
    below = cur
  end
  local a, b = below, cur
  for _ = 1, 9 do
    local m = (a + b) / 2
    if solid_at(structural, details, x, m, z) then b = m else a = m end
  end
  local cy = (a + b) / 2
  local snapped = math.floor(cy * 64 + 0.5) / 64
  if math.abs(snapped - cy) < 0.004 then cy = snapped end
  return cy
end
I.ceiling_point = ceiling_point

---@kit kit.ground_at{x, z, y?} → ground — The ground under a point through the kit's ground provider: {y, normal, slope, kind, pos, provider}; kind "brush" (a brush floor), "detail" (a detail top) or "void" (nothing below, no y). Every kit placement rule reads the ground this way.
---@opt x: number — Required: world x, metres.
---@opt z: number — Required: world z, metres.
---@opt y: number = the top of the column — Look for the highest floor at or below this height (pick a storey).
function kit.ground_at(t)
  local api = "kit.ground_at"
  check_keys(api, t, { "x", "z", "y" })
  local x = number(api, "x", t.x)
  local z = number(api, "z", t.z)
  local y = t.y ~= nil and number(api, "y", t.y) or nil
  return ground_point(x, z, y)
end

---@kit kit.ground_region{min, max, step?, y?} → ground_region — Samples the ground provider on a grid over a floor rectangle: {count, found, min_y, max_y, mean_y, flat_share (slope under 5°), samples = {{x, z, y, kind, slope}...}, provider}.
---@opt min: vec — Required: one corner {x, y, z} of the rectangle (y is ignored).
---@opt max: vec — Required: the opposite corner.
---@opt step: number = 2 — Grid spacing in metres (at most 4,096 samples).
---@opt y: number = the top of each column — Look for floors at or below this height.
function kit.ground_region(t)
  local api = "kit.ground_region"
  check_keys(api, t, { "min", "max", "step", "y" })
  local x0, _, z0 = vec3(api, "min", t.min)
  local x1, _, z1 = vec3(api, "max", t.max)
  if x1 < x0 then x0, x1 = x1, x0 end
  if z1 < z0 then z0, z1 = z1, z0 end
  local step = positive(api, "step", t.step, 2)
  local nx, nz = math.floor((x1 - x0) / step) + 1, math.floor((z1 - z0) / step) + 1
  if nx * nz > 4096 then fail(api, "the grid would take " .. (nx * nz) .. " samples (at most 4096).", "Use a larger step.") end
  local y = t.y ~= nil and number(api, "y", t.y) or nil
  if provider.region ~= nil then
    local r = provider.region({ min = { x0, 0, z0 }, max = { x1, 0, z1 }, step = step, y = y })
    if type(r) == "table" then r.provider = provider.name return r end
  end
  local samples, found, sum, flat = {}, 0, 0, 0
  local lo, hi = math.huge, -math.huge
  for i = 0, nx - 1 do
    for k = 0, nz - 1 do
      local px, pz = x0 + i * step, z0 + k * step
      local g = ground_point(px, pz, y)
      samples[#samples + 1] = { x = px, z = pz, y = g.y, kind = g.kind, slope = g.slope }
      if g.y ~= nil then
        found = found + 1
        sum = sum + g.y
        lo, hi = math.min(lo, g.y), math.max(hi, g.y)
        if g.slope < 5 then flat = flat + 1 end
      end
    end
  end
  return {
    count = #samples, found = found, samples = samples, provider = provider.name,
    min_y = found > 0 and lo or nil, max_y = found > 0 and hi or nil, mean_y = found > 0 and sum / found or nil,
    flat_share = found > 0 and flat / found or 0,
  }
end

---@kit kit.ground_provider{name?, at?, region?} → string — Swaps the ground provider every kit placement rule reads (the terrain seam): at(x, z, y?) returns {y, normal = {x, y, z}, kind} or nil; region(box) is optional. No options restores the built-in "brush" provider. Returns the previous provider's name.
---@opt name: string = "custom" — The provider's name (reported by kit.ground_at).
---@opt at: function — The point query; required unless resetting.
---@opt region: function = sampled from at — The rectangle query (optional fast path).
function kit.ground_provider(t)
  local api = "kit.ground_provider"
  t = t or {}
  check_keys(api, t, { "name", "at", "region" })
  local previous = provider.name
  if t.at == nil and t.region == nil and t.name == nil then
    provider = { name = "brush", at = brush_provider_at }
    return previous
  end
  if type(t.at) ~= "function" then fail(api, "'at' must be a function(x, z, y) returning {y, normal, kind} or nil.") end
  if t.region ~= nil and type(t.region) ~= "function" then fail(api, "'region' must be a function(box).") end
  if t.name ~= nil and (type(t.name) ~= "string" or t.name == "") then fail(api, "'name' must be a non-empty string.") end
  provider = { name = t.name or "custom", at = t.at, region = t.region }
  return previous
end

-- The spec's spelling: kit.ground.at(x, z [, y]) and kit.ground.region{min =, max =, step =}.
kit.ground = {
  at = function(x, z, y) return kit.ground_at{ x = x, z = z, y = y } end,
  region = function(box) return kit.ground_region(box) end,
}

-- ---------------------------------------------------------------------------------------------
-- Beams and trusses
-- ---------------------------------------------------------------------------------------------

-- The 8 corners of a beam with a w x h section from a to b (h along `up`, made perpendicular to the beam).
local function beam_points(a, b, w, h, up)
  local d = sub(b, a)
  local u = unit(d)
  local ref = up or { 0, 1, 0 }
  if math.abs(u[1] * ref[1] + u[2] * ref[2] + u[3] * ref[3]) > 0.95 then ref = { 1, 0, 0 } end
  if math.abs(u[1] * ref[1] + u[2] * ref[2] + u[3] * ref[3]) > 0.95 then ref = { 0, 0, 1 } end
  local side = unit(cross(ref, u))
  local upv = unit(cross(u, side))
  local pts = {}
  for _, e in ipairs({ a, b }) do
    for _, s in ipairs({ { -1, -1 }, { 1, -1 }, { -1, 1 }, { 1, 1 } }) do
      pts[#pts + 1] = add(add(e, mul(side, s[1] * w / 2)), mul(upv, s[2] * h / 2))
    end
  end
  return pts
end

local function member(fn, name, part, a, b, w, h, tex, up, detail)
  return hull(fn, name, part, beam_points(a, b, w, h, up), { detail = detail ~= false, texture = tex.texture, textures = tex.textures })
end
I.member = member

---@kit kit.beam{name?, from, to, size?, up?, detail?, texture?|textures?} → beam — One straight beam (any direction) from from to to: a convex brush with a size section, detail by default. Use it for posts, rafters, braces, rails and supports.
---@opt name: string = beam<n> — Names the beam (tags).
---@opt from: vec — Required: one end of the beam's centre line.
---@opt to: vec — Required: the other end (at least 0.05 m away).
---@opt size: number|{w, h} = 0.3 — Section: a square side, or width x height (height along up), metres.
---@opt up: vec = {0, 1, 0} — Which way the section's height faces (made perpendicular to the beam).
---@opt detail: boolean = true — Make the beam detail (it never seals or splits rooms).
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.beam(t)
  local api = "kit.beam"
  check_keys(api, t, { "name", "from", "to", "size", "up", "detail", "texture", "textures" })
  local name = name_for(api, "beam", t.name)
  local a, b = point(api, "from", t.from), point(api, "to", t.to)
  local l = len(sub(b, a))
  if l < 0.05 then fail(api, "'from' and 'to' are only " .. fmt(l) .. " m apart (at least 0.05).") end
  local w, h = section(api, "size", t.size, 0.3)
  local up = t.up ~= nil and point(api, "up", t.up) or nil
  local brush = member("beam", name, "beam", a, b, w, h, struct_textures(api, t), up, boolean(api, "detail", t.detail, true))
  local bb = brush.bounds
  return { kind = "beam", name = name, brush = brush, brushes = { brush }, length = l, from = vec(a[1], a[2], a[3]), to = vec(b[1], b[2], b[3]), min = bb.min, max = bb.max }
end

-- The members of one truss plane from a to b (bottom chord), top chord `depth` along up.
local function truss_plane(fn, name, prefix, a, b, upv, depth, panels, chord, web, style, tex, out)
  local ta, tb = add(a, mul(upv, depth)), add(b, mul(upv, depth))
  local function node(p, top) local f = p / panels return add(top and ta or a, mul(sub(top and tb or b, top and ta or a), f)) end
  out[#out + 1] = member(fn, name, prefix .. "chord_bottom", a, b, chord, chord, tex, upv)
  out[#out + 1] = member(fn, name, prefix .. "chord_top", ta, tb, chord, chord, tex, upv)
  for p = 0, panels do
    if style ~= "warren" or p == 0 or p == panels then
      out[#out + 1] = member(fn, name, prefix .. "post" .. p, node(p, false), node(p, true), web, web, tex)
    end
  end
  for p = 0, panels - 1 do
    local d0, d1 = nil, nil
    if style == "warren" then
      if p % 2 == 0 then d0, d1 = node(p, false), node(p + 1, true) else d0, d1 = node(p, true), node(p + 1, false) end
    else
      -- Pratt: diagonals lean down toward mid-span; Howe: up toward mid-span.
      local left = (p + 0.5) < panels / 2
      local down = (style == "pratt") == left
      if down then d0, d1 = node(p, true), node(p + 1, false) else d0, d1 = node(p, false), node(p + 1, true) end
    end
    out[#out + 1] = member(fn, name, prefix .. "diag" .. p, d0, d1, web, web, tex)
  end
end

local function truss_frame(a, b, up)
  local u = unit(sub(b, a))
  local ref = up or { 0, 1, 0 }
  if math.abs(u[1] * ref[1] + u[2] * ref[2] + u[3] * ref[3]) > 0.95 then ref = { 1, 0, 0 } end
  local side = unit(cross(ref, u))
  local upv = unit(cross(u, side))
  return u, side, upv
end

---@kit kit.truss{name?, from, to, depth?, width?, panels?, style?, chord?, web?, up?, group?, texture?|textures?} → truss — A truss of detail beams: bottom chord from → to, top chord depth above it, posts and diagonals per panel; width > 0 makes two parallel trusses tied by floor beams (a bridge or gantry frame).
---@opt name: string = truss<n> — Names the truss (tags, group).
---@opt from: vec — Required: one end of the bottom chord's centre line.
---@opt to: vec — Required: the other end (at least 2 m away).
---@opt depth: number = 2 — Distance from the bottom to the top chord, metres.
---@opt width: number = 0 — Distance between two truss planes (0 = one plane), metres.
---@opt panels: integer = length / depth, rounded (at least 2) — Number of panels.
---@opt style: string = "warren" — "warren" (zig-zag diagonals), "pratt" (posts, diagonals falling toward mid-span) or "howe" (posts, diagonals rising toward mid-span).
---@opt chord: number = 0.3 — Chord section side, metres.
---@opt web: number = 0.2 — Post and diagonal section side, metres.
---@opt up: vec = {0, 1, 0} — The direction from the bottom chord to the top chord (made perpendicular to the span).
---@opt group: boolean = true — Group the members under the truss's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.truss(t)
  local api = "kit.truss"
  check_keys(api, t, { "name", "from", "to", "depth", "width", "panels", "style", "chord", "web", "up", "group", "texture", "textures" })
  local name = name_for(api, "truss", t.name)
  local a, b = point(api, "from", t.from), point(api, "to", t.to)
  local l = len(sub(b, a))
  if l < 2 then fail(api, "the truss is only " .. fmt(l) .. " m long (at least 2).") end
  local depth = positive(api, "depth", t.depth, 2)
  local width = number(api, "width", t.width, 0)
  if width < 0 then fail(api, "'width' must be 0 or more metres.") end
  local panels = t.panels ~= nil and number(api, "panels", t.panels) or math.max(2, math.floor(l / depth + 0.5))
  if panels < 1 or panels ~= math.floor(panels) or panels > 64 then fail(api, "'panels' must be a whole number from 1 to 64.") end
  local style = t.style or "warren"
  if style ~= "warren" and style ~= "pratt" and style ~= "howe" then
    fail(api, "'style' must be \"warren\", \"pratt\" or \"howe\" (got " .. tostring(style) .. ").")
  end
  local chord = positive(api, "chord", t.chord, 0.3)
  local web = positive(api, "web", t.web, 0.2)
  local _, side, upv = truss_frame(a, b, t.up ~= nil and point(api, "up", t.up) or nil)
  local tex = struct_textures(api, t)
  local brushes = {}
  if width <= EPS then
    truss_plane("truss", name, "", a, b, upv, depth, panels, chord, web, style, tex, brushes)
  else
    local off = mul(side, width / 2)
    truss_plane("truss", name, "l_", sub(a, off), sub(b, off), upv, depth, panels, chord, web, style, tex, brushes)
    truss_plane("truss", name, "r_", add(a, off), add(b, off), upv, depth, panels, chord, web, style, tex, brushes)
    for p = 0, panels do
      local m = add(a, mul(sub(b, a), p / panels))
      brushes[#brushes + 1] = member("truss", name, "tie" .. p, sub(m, off), add(m, off), web, web, tex, upv)
    end
  end
  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  return { kind = "truss", name = name, brushes = brushes, group = group, length = l, panels = panels, depth = depth }
end

-- ---------------------------------------------------------------------------------------------
-- Bridges
-- ---------------------------------------------------------------------------------------------

-- A bridge end: {x, y, z}, or {x, z} / {x =, z =} whose y comes from the ground provider.
local function anchor(api, key, v)
  if type(v) == "table" then
    local x, y, z = nil, nil, nil
    if v[1] ~= nil then
      if v[3] == nil then x, z = v[1], v[2] else x, y, z = v[1], v[2], v[3] end
    else
      x, y, z = v.x, v.y, v.z
    end
    if type(x) == "number" and type(z) == "number" and y == nil then
      local g = ground_point(x, z, nil)
      if g.y == nil then fail(api, "'" .. key .. "' has no y and there is no ground under (" .. fmt(x) .. ", " .. fmt(z) .. ").", "Give the deck height as {x, y, z}.") end
      return { x, g.y, z }
    end
  end
  return point(api, key, v)
end

---@kit kit.bridge{name?, from, to, width?, style?, deck?, depth?, railing?, landmark?, group?, texture?|textures?, frame_texture?} → bridge — A landmark-scale bridge of detail brushes: a deck whose top runs from → to (ends without y sit on the ground), carried by a through truss, an arch, girders or as a plank deck with rails. Reports the span, the unsupported gap and the drop under mid-span (all read through the ground provider) and warns below landmark scale (span < 12 m, drop < 6 m).
---@opt name: string = bridge<n> — Names the bridge (tags, group).
---@opt from: vec — Required: one end of the deck's walking surface (centre of its width); {x, z} takes y from the ground.
---@opt to: vec — Required: the other end (the deck may climb, at most 45°).
---@opt width: number = 3.5 — Walkable deck width, metres (exemplar landmark bridges are 2-4 m).
---@opt style: string = "truss" — "truss" (a through truss whose chords are the rails), "arch" (an arch rib under each deck edge with spandrel posts), "girder" (two deep girders under the deck) or "plank" (a plank deck on cross beams with post-and-rail sides).
---@opt deck: number = 0.4 — Deck thickness, metres.
---@opt depth: number = by style (truss: span / 10 within 1.6-3; arch: rise span / 5; girder: span / 15 at least 0.8) — Structure depth, metres.
---@opt railing: boolean = true — Posts and a hand rail along both edges (truss style: the chords are the rails).
---@opt landmark: boolean = true — Warn when the bridge is below landmark scale (span under 12 m or drop under 6 m).
---@opt group: boolean = true — Group every brush under the bridge's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — The deck's texture (every face).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — The deck's textures by role.
---@opt frame_texture: string = the deck's — The truss/arch/girder/rail texture.
function kit.bridge(t)
  local api = "kit.bridge"
  check_keys(api, t, { "name", "from", "to", "width", "style", "deck", "depth", "railing", "landmark", "group", "texture", "textures", "frame_texture" })
  local name = name_for(api, "bridge", t.name)
  local a, b = anchor(api, "from", t.from), anchor(api, "to", t.to)
  local width = positive(api, "width", t.width, 3.5)
  I.check_walkable(api, "bridge", width, nil, false)
  local style = t.style or "truss"
  if style ~= "truss" and style ~= "arch" and style ~= "girder" and style ~= "plank" then
    fail(api, "'style' must be \"truss\", \"arch\", \"girder\" or \"plank\" (got " .. tostring(style) .. ").")
  end
  local deck = positive(api, "deck", t.deck, 0.4)
  local dx, dz = b[1] - a[1], b[3] - a[3]
  local span = math.sqrt(dx * dx + dz * dz)
  if span < 2 then fail(api, "'from' and 'to' are only " .. fmt(span) .. " m apart across the ground (at least 2).") end
  local rise = b[2] - a[2]
  if math.abs(rise) > span + EPS then fail(api, "the deck climbs " .. fmt(rise) .. " m over " .. fmt(span) .. " m (steeper than 45°).") end
  local tex = struct_textures(api, t)
  local ftex = t.frame_texture ~= nil and { texture = t.frame_texture } or tex
  local fwd = { dx / span, 0, dz / span }
  local side = { -fwd[3], 0, fwd[1] }
  local hw = width / 2
  local warnings = {}
  local brushes = {}

  local function at(f, s, dy) -- the point at fraction f along, s across, dy above the deck top
    local p = add(a, mul(sub(b, a), f))
    return add(add(p, mul(side, s)), { 0, dy, 0 })
  end

  -- 0. Measurements through the ground provider, before anything is built: the drop under mid-span (from the
  -- deck's underside) and the unsupported gap (where the ground is more than the deck depth + 1 m below the top).
  local function ground_below(f)
    local p = at(f, 0, 0.25)
    local g = ground_point(p[1], p[3], p[2])
    return g.y, p[2] - 0.25
  end
  local gy, top_mid = ground_below(0.5)
  local drop = gy and (top_mid - deck - gy) or math.huge
  local gap0, gap1 = 0, 1
  local samples = math.max(8, math.floor(span / 0.5))
  for k = math.floor(samples / 2), 0, -1 do
    local y0, t0 = ground_below(k / samples)
    if y0 ~= nil and t0 - y0 <= deck + 1 then gap0 = k / samples break end
  end
  for k = math.ceil(samples / 2), samples do
    local y1, t1 = ground_below(k / samples)
    if y1 ~= nil and t1 - y1 <= deck + 1 then gap1 = k / samples break end
  end
  local gap = (gap1 - gap0) * span

  -- 1. The deck: one slab whose top face runs exactly from a to b.
  local deck_brush = hull("bridge", name, "deck", {
    at(0, -hw, 0), at(0, hw, 0), at(1, -hw, 0), at(1, hw, 0),
    at(0, -hw, -deck), at(0, hw, -deck), at(1, -hw, -deck), at(1, hw, -deck),
  }, { detail = true, texture = tex.texture, textures = tex.textures })
  brushes[1] = deck_brush

  -- 2. The structure.
  local depth = nil
  if style == "truss" then
    depth = t.depth ~= nil and positive(api, "depth", t.depth) or math.max(1.6, math.min(3, span / 10))
    local panels = math.max(2, math.floor(span / depth + 0.5))
    local rail_off = hw + 0.2
    for _, s in ipairs({ -rail_off, rail_off }) do
      truss_plane("bridge", name, s < 0 and "l_" or "r_", at(0, s, 0.15), at(1, s, 0.15), { 0, 1, 0 }, depth, panels, 0.3, 0.2, "pratt", ftex, brushes)
    end
    for p = 0, panels do
      local f = p / panels
      brushes[#brushes + 1] = member("bridge", name, "floor_beam" .. p, at(f, -rail_off, -deck - 0.15), at(f, rail_off, -deck - 0.15), 0.3, 0.3, ftex)
    end
  elseif style == "girder" then
    depth = t.depth ~= nil and positive(api, "depth", t.depth) or math.max(0.8, span / 15)
    for _, s in ipairs({ -(hw - 0.3), hw - 0.3 }) do
      local yc = -deck - depth / 2
      brushes[#brushes + 1] = member("bridge", name, s < 0 and "girder_l" or "girder_r", at(0, s, yc), at(1, s, yc), 0.4, depth, ftex)
    end
    local n = math.max(2, math.floor(span / 4 + 0.5))
    for p = 0, n do
      brushes[#brushes + 1] = member("bridge", name, "cross" .. p, at(p / n, -(hw - 0.3), -deck - 0.2), at(p / n, hw - 0.3, -deck - 0.2), 0.25, 0.25, ftex)
    end
  elseif style == "arch" then
    depth = t.depth ~= nil and positive(api, "depth", t.depth) or span / 5
    local n = math.max(6, math.floor(span / 2.5 + 0.5))
    if n % 2 == 1 then n = n + 1 end
    for _, s in ipairs({ -(hw - 0.3), hw - 0.3 }) do
      local tag = s < 0 and "l" or "r"
      local prev = nil
      for k = 0, n do
        local f = k / n
        local y = -deck - 0.3 - depth * (2 * f - 1) ^ 2 -- springs depth below the deck, crown just under it
        local p = at(f, s, y)
        if prev then brushes[#brushes + 1] = member("bridge", name, "rib_" .. tag .. k, prev, p, 0.5, 0.6, ftex) end
        if k > 0 and k < n then
          brushes[#brushes + 1] = member("bridge", name, "spandrel_" .. tag .. k, p, at(f, s, -deck), 0.25, 0.25, ftex)
        end
        prev = p
      end
    end
    for k = 1, n - 1 do
      brushes[#brushes + 1] = member("bridge", name, "cross" .. k, at(k / n, -(hw - 0.3), -deck - 0.2), at(k / n, hw - 0.3, -deck - 0.2), 0.25, 0.25, ftex)
    end
  else -- plank
    depth = 0.3
    local n = math.max(2, math.floor(span / 2 + 0.5))
    for p = 0, n do
      brushes[#brushes + 1] = member("bridge", name, "cross" .. p, at(p / n, -(hw + 0.2), -deck - 0.15), at(p / n, hw + 0.2, -deck - 0.15), 0.3, 0.3, ftex)
    end
  end

  -- 3. Railings (the truss's chords already are).
  if style ~= "truss" and boolean(api, "railing", t.railing, true) then
    local n = math.max(2, math.floor(span / 2.5 + 0.5))
    for _, s in ipairs({ -(hw - 0.1), hw - 0.1 }) do
      local tag = s < 0 and "l" or "r"
      for p = 0, n do
        brushes[#brushes + 1] = member("bridge", name, "post_" .. tag .. p, at(p / n, s, 0), at(p / n, s, 1.1), 0.15, 0.15, ftex)
      end
      brushes[#brushes + 1] = member("bridge", name, "rail_" .. tag, at(0, s, 1.05), at(1, s, 1.05), 0.12, 0.12, ftex)
    end
  elseif style == "truss" and t.railing ~= nil then
    boolean(api, "railing", t.railing, true)
  end

  if boolean(api, "landmark", t.landmark, true) then
    if span < 12 - EPS then
      warn(api, warnings, name .. " spans " .. fmt(span) .. " m; landmark bridges span 12-27 m (exemplar study). Pass landmark = false for a small crossing.")
    end
    if drop < 6 - EPS then
      warn(api, warnings, name .. " is only " .. fmt(drop) .. " m above the ground under mid-span; landmark bridges cross 5-12 m drops (at least 6).")
    end
  end

  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  local lo = { math.huge, math.huge, math.huge }
  local hi = { -math.huge, -math.huge, -math.huge }
  for _, br in ipairs(brushes) do
    local bb = br.bounds
    lo = { math.min(lo[1], bb.min.x), math.min(lo[2], bb.min.y), math.min(lo[3], bb.min.z) }
    hi = { math.max(hi[1], bb.max.x), math.max(hi[2], bb.max.y), math.max(hi[3], bb.max.z) }
  end
  return {
    kind = "bridge", name = name, style = style, brushes = brushes, deck = deck_brush, group = group,
    span = span, gap = gap, drop = drop, width = width, depth = depth, climb = rise,
    from = vec(a[1], a[2], a[3]), to = vec(b[1], b[2], b[3]), floor_y = math.min(a[2], b[2]),
    min = vec(lo[1], lo[2], lo[3]), max = vec(hi[1], hi[2], hi[3]), warnings = warnings,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Landmarks
-- ---------------------------------------------------------------------------------------------

---@kit kit.landmark{name?, pos, height?, base?, style?, sides?, group?, texture?|textures?} → landmark — An outdoor landmark standing on the floor point pos: a tapering tower, a lattice pylon of trusses, or a spire on a plinth. Exemplar outdoor landmarks stand 25-40 m tall; it warns outside that range.
---@opt name: string = landmark<n> — Names the landmark (tags, group).
---@opt pos: vec — Required: the floor point at the centre of its base.
---@opt height: number = 30 — Total height, metres.
---@opt base: number = height / 4 — Base width, metres.
---@opt style: string = "tower" — "tower" (a tapering solid shaft with a wider cap), "pylon" (four truss faces tapering to a top platform) or "spire" (a plinth and a tall tapering needle).
---@opt sides: integer = 8 — Sides of the tower or spire section (4 to 16).
---@opt group: boolean = true — Group the brushes under the landmark's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.landmark(t)
  local api = "kit.landmark"
  check_keys(api, t, { "name", "pos", "height", "base", "style", "sides", "group", "texture", "textures" })
  local name = name_for(api, "landmark", t.name)
  local p = point(api, "pos", t.pos)
  local height = positive(api, "height", t.height, 30)
  local base = positive(api, "base", t.base, height / 4)
  local style = t.style or "tower"
  if style ~= "tower" and style ~= "pylon" and style ~= "spire" then
    fail(api, "'style' must be \"tower\", \"pylon\" or \"spire\" (got " .. tostring(style) .. ").")
  end
  local sides = number(api, "sides", t.sides, 8)
  if sides < 4 or sides > 16 or sides ~= math.floor(sides) then fail(api, "'sides' must be a whole number from 4 to 16.") end
  local tex = struct_textures(api, t)
  local warnings = {}
  if height < 25 - EPS or height > 40 + EPS then
    warn(api, warnings, name .. " is " .. fmt(height) .. " m tall; exemplar outdoor landmarks stand 25-40 m.")
  end
  local function ring(r, y, rot)
    local pts = {}
    for k = 0, sides - 1 do
      local a = (k + (rot or 0.5)) / sides * 2 * math.pi
      pts[#pts + 1] = { p[1] + r * math.cos(a), y, p[3] + r * math.sin(a) }
    end
    return pts
  end
  local function frustum(part, r0, y0, r1, y1, detail)
    local pts = ring(r0, y0)
    for _, q in ipairs(ring(r1, y1)) do pts[#pts + 1] = q end
    return hull("landmark", name, part, pts, { detail = detail, texture = tex.texture, textures = tex.textures })
  end
  local brushes = {}
  local r = base / 2
  local y = p[2]
  if style == "tower" then
    brushes[#brushes + 1] = frustum("plinth", r * 1.25, y, r * 1.1, y + 2, false)
    brushes[#brushes + 1] = frustum("shaft", r, y + 2, r * 0.6, y + height * 0.82, false)
    brushes[#brushes + 1] = frustum("gallery", r * 0.95, y + height * 0.82, r * 0.95, y + height * 0.86, true)
    brushes[#brushes + 1] = frustum("lantern", r * 0.55, y + height * 0.86, r * 0.35, y + height * 0.95, true)
    brushes[#brushes + 1] = frustum("cap", r * 0.4, y + height * 0.95, 0.15, y + height, true)
  elseif style == "spire" then
    brushes[#brushes + 1] = frustum("plinth", r, y, r * 0.85, y + height * 0.12, false)
    brushes[#brushes + 1] = frustum("needle", r * 0.45, y + height * 0.12, 0.1, y + height, true)
  else -- pylon: four tapering legs, braced on each face, under a top platform
    local top = height - 1
    local rb, rt = r, r * 0.35
    local legs = {}
    for k, c in ipairs({ { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } }) do
      legs[k] = { { p[1] + c[1] * rb, y, p[3] + c[2] * rb }, { p[1] + c[1] * rt, y + top, p[3] + c[2] * rt } }
      brushes[#brushes + 1] = member("landmark", name, "leg" .. k, legs[k][1], legs[k][2], 0.6, 0.6, tex)
    end
    local levels = math.max(3, math.floor(top / 5 + 0.5))
    for k = 1, 4 do
      local l0, l1 = legs[k], legs[k % 4 + 1]
      local function on(leg, f) return add(leg[1], mul(sub(leg[2], leg[1]), f)) end
      for j = 0, levels - 1 do
        local f0, f1 = j / levels, (j + 1) / levels
        brushes[#brushes + 1] = member("landmark", name, "brace" .. k .. "_" .. j .. "a", on(l0, f0), on(l1, f1), 0.25, 0.25, tex)
        brushes[#brushes + 1] = member("landmark", name, "brace" .. k .. "_" .. j .. "b", on(l1, f0), on(l0, f1), 0.25, 0.25, tex)
        brushes[#brushes + 1] = member("landmark", name, "ring" .. k .. "_" .. j, on(l0, f1), on(l1, f1), 0.3, 0.3, tex)
      end
    end
    brushes[#brushes + 1] = hull("landmark", name, "platform",
      box_points(p[1] - rt - 1, y + top, p[3] - rt - 1, p[1] + rt + 1, y + height, p[3] + rt + 1),
      { detail = true, texture = tex.texture, textures = tex.textures })
  end
  local group = group_if_many(name, brushes, boolean(api, "group", t.group, true))
  return {
    kind = "landmark", name = name, style = style, brushes = brushes, group = group, height = height, base = base,
    min = vec(p[1] - r * 1.25, y, p[3] - r * 1.25), max = vec(p[1] + r * 1.25, y + height, p[3] + r * 1.25), floor_y = y,
    warnings = warnings,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Openings between spaces
-- ---------------------------------------------------------------------------------------------

-- The clear width (narrow horizontal extent) and headroom of a kit record.
local function clear_of(r)
  local w = math.min(r.x1 - r.x0, r.z1 - r.z0)
  return w, r.y1 - r.floor
end

-- The cave-mouth check (spec c.1-7): a mouth wider than 0.8x the clear width, or taller than 0.8x the
-- headroom, of a space it joins reads as two spaces merged into one. Returns the warnings.
local function mouth_check(api, what, width, top, spaces, warnings)
  for _, s in ipairs(spaces) do
    local cw, head = clear_of(s)
    if width > 0.8 * cw + EPS then
      warn(api, warnings, what .. " is " .. fmt(width) .. " m wide, more than 0.8 x the " .. fmt(cw) .. " m clear width of " .. s.name ..
        ": the two spaces would read as one. Narrow it to at most " .. fmt(0.8 * cw) .. " m or widen " .. s.name .. ".")
    end
    if top - s.floor > 0.8 * head + EPS then
      warn(api, warnings, what .. " reaches " .. fmt(top - s.floor) .. " m above " .. s.name .. "'s floor, more than 0.8 x its " .. fmt(head) ..
        " m headroom: the two spaces would read as one.")
    end
  end
end
I.mouth_check = mouth_check

---@kit kit.opening{name?, a, b, shape?, width?, height?, sill?, floor_y?, offset?, seed?, portal?, allow_tight?, texture?|textures?} → opening — An air opening through the wall between two records (abutting both, like kit.doorway): a window above a sill (a sight link), a round-topped arch, a rough seeded mouth, or a plain door. Warns when the opening is wider than 0.8x the clear width or taller than 0.8x the headroom of either space (the two would merge into one).
---@opt name: string = opening<n> — Names the opening (tags).
---@opt a: record — Required: the space on one side of the wall.
---@opt b: record — Required: the space on the other side.
---@opt shape: string = "window" — "window" (rectangle above a sill), "arch" (round top), "rough" (seeded broken outline) or "door" (plain rectangle on the floor).
---@opt width: number = window 4, arch 4, rough 4, door kit.defaults.door_width — Clear width, metres.
---@opt height: number = window 2, arch 5, rough 4.5, door kit.defaults.door_height — Clear height above the sill, metres.
---@opt sill: number = window 1.2, others 0 — Height of the opening's bottom above a's floor, metres.
---@opt floor_y: number = a's floor + sill — The opening's bottom height (overrides sill).
---@opt offset: number = 0 — Shift across the shared span, metres.
---@opt seed: integer = 1 — The random stream of a rough outline.
---@opt portal: boolean = false — Make the opening an air portal brush: it cuts the wall itself, so the walls it opens become portals and the opening a small room between the two spaces (RED's portal construct).
---@opt allow_tight: boolean = false — Allow a walkable shape (arch, rough, door) below the player's size.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.opening(t)
  local api = "kit.opening"
  check_keys(api, t, { "name", "a", "b", "shape", "width", "height", "sill", "floor_y", "offset", "seed", "portal", "allow_tight", "texture", "textures" })
  local name = name_for(api, "opening", t.name)
  local shape = t.shape or "window"
  local defaults = {
    window = { 4, 2, 1.2 }, arch = { 4, 5, 0 }, rough = { 4, 4.5, 0 },
    door = { kit.defaults.door_width, kit.defaults.door_height, 0 },
  }
  local d = defaults[shape]
  if d == nil then fail(api, "'shape' must be \"window\", \"arch\", \"rough\" or \"door\" (got " .. tostring(shape) .. ").") end
  local width = positive(api, "width", t.width, d[1])
  local height = positive(api, "height", t.height, d[2])
  local sill = number(api, "sill", t.sill, d[3])
  if sill < 0 then fail(api, "'sill' must be 0 or more metres.") end
  if shape ~= "window" then I.check_walkable(api, shape .. " opening", width, height, t.allow_tight) end
  if t.a == nil or t.b == nil then fail(api, "'a' and 'b' are required (the two spaces the opening joins).") end
  local ra, rb = record_box(api, "a", t.a), record_box(api, "b", t.b)
  local floor = t.floor_y ~= nil and number(api, "floor_y", t.floor_y) or (ra.floor + sill)
  local c = I.connect(api, "opening", { a = t.a, b = t.b, offset = t.offset, floor_y = floor }, width, height)

  -- The cross-section in (s across, v up), extruded across the wall gap.
  local profile = {}
  local hw = width / 2
  if shape == "arch" or shape == "rough" then
    local spring = math.max(0, height - hw)
    profile[#profile + 1] = { -hw, 0 }
    profile[#profile + 1] = { hw, 0 }
    local n = 8
    for k = 0, n do
      local ang = k / n * math.pi
      profile[#profile + 1] = { hw * math.cos(ang), spring + math.min(hw, height) * math.sin(ang) }
    end
    if shape == "rough" then
      local r = stream(t.seed ~= nil and number(api, "seed", t.seed) or 1)
      for i = 3, #profile do
        local q = profile[i]
        local j = 0.12 * width
        profile[i] = { q[1] + (r() - 0.5) * j, math.max(2.2, q[2] + (r() - 0.5) * j) }
      end
    end
  else
    profile = { { -hw, 0 }, { hw, 0 }, { hw, height }, { -hw, height } }
  end
  local mid_s = c.axis == "x" and (c.z0 + c.z1) / 2 or (c.x0 + c.x1) / 2
  local pts = {}
  for _, q in ipairs(profile) do
    for _, along in ipairs(c.axis == "x" and { c.x0, c.x1 } or { c.z0, c.z1 }) do
      if c.axis == "x" then pts[#pts + 1] = { along, floor + q[2], mid_s + q[1] }
      else pts[#pts + 1] = { mid_s + q[1], floor + q[2], along } end
    end
  end
  local texture, textures = textures_of(api, t)
  local brush = hull("opening", name, "opening", pts, { air = true, texture = texture, textures = textures })
  local top = floor
  for _, q in ipairs(profile) do top = math.max(top, floor + q[2]) end

  -- portal = true: the opening itself becomes the air portal brush. RED builds a portal from an air portal brush
  -- only where it cuts through solid — the two wall faces it opens become the portals and the opening between them
  -- a room of its own — and none from one placed in space that is already open (compiler-parity-notes.md "Air
  -- portal brushes and inside-out rooms", rule 1).
  local portal_brush = nil
  if boolean(api, "portal", t.portal, false) then
    brush:set_portal(true)
    portal_brush = brush
  end

  local warnings = {}
  mouth_check(api, name, width, top, { ra, rb }, warnings)
  return {
    kind = "opening", name = name, shape = shape, brush = brush, brushes = { brush }, portal_brush = portal_brush,
    axis = c.axis, dir = c.dir, width = width, height = top - floor, sill = floor - ra.floor, length = c.length,
    min = vec(c.x0, floor, c.z0), max = vec(c.x1, top, c.z1),
    center = vec((c.x0 + c.x1) / 2, (floor + top) / 2, (c.z0 + c.z1) / 2), floor_y = floor,
    sight_only = shape == "window" and (floor - ra.floor) > kit.limits.max_riser, a = t.a, b = t.b, warnings = warnings,
  }
end

end

return install
