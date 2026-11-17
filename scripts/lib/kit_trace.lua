-- Glacier design kit: kit.trace, the literal plan builder for recreating an area from a reference picture
-- (installed into the kit by kit.lua; use it as local kit = ged.import("kit")).
--
-- The generative kit (kit.layout, zones, links) snaps, reshapes, links and dresses: right for blockouts, wrong
-- for recreation. kit.trace builds exactly the plan it is given: each region's polygon (any simple polygon,
-- concave allowed) as convex air prisms (kit_space's convex decomposition, the same pieces kit.shaped_room uses),
-- its floor and ceiling (or open sky), ramps and stairs as sloped or stepped solid fills of their own traced
-- regions, doorways as air cut through the wall gap between two regions, and solid blocks inside regions.
-- Everything outside the regions stays solid rock (the world is solid until air is carved). Corners are snapped
-- to the 1/16 m grid and corners within the weld tolerance of another region's corner or edge are moved onto it
-- and inserted into that edge (a T-vertex), so shared walls carry the same corners and meet exactly; nothing else
-- is moved, linked or added. Ramps, stairs and decks may lie inside a room or yard (they replace its floor there
-- and take its ceiling and sky); other regions may overlap only as stacked storeys with a slab between. A path
-- region (centre line + width) becomes mitred segment regions, sloped ones floored by two planes through the
-- segment's corner heights so neighbours meet exactly. It validates first and explains: regions sharing space,
-- a slab too thin, a wall wedge too thin to build (it would leak), an opening not between two regions, an
-- impossible ramp or stair, a bend too sharp, a polygon whose edges cross.
--
-- CSG time order: every region's air first, then the opening air, then the solids (ramp, stair and path fills,
-- deck slabs, blocks), so the solids stand in the air.
--
-- Traced builds are normal builds that follow a set design: by default (via = "layout") the validated plan becomes a
-- kit.layout graph of literal poly zones (kit_layout.lua's literal mode: no raised bays, cover or pilasters, nothing
-- moved) whose hooks build exactly these prisms, joined where they share walls and linked at the traced doorways, and
-- kit.build_layout builds it: kit.check_layout's flow checks, the kit's own ramps and stairs (with purpose), the room
-- detail pass (trims, door frames, beams, railings, stair finishes) and lights. via = "prism" keeps the plan alone.

local function install(kit, I)
local fail, check_keys, number, positive, boolean = I.fail, I.check_keys, I.number, I.positive, I.boolean
local name_for, group_if_many, fmt, textures_of = I.name_for, I.group_if_many, I.fmt, I.textures_of
local prism, convex_pieces, area2, cross2, inside = I.prism, I.convex_pieces, I.area2, I.cross2, I.inside
local sky_up, ensure_sky = I.sky_up, I.ensure_sky
local vec = I.vec

local KINDS = {
  room = { h = 4 }, corridor = { h = 3.5 }, yard = { h = 10, sky = true }, pit = { h = 6 },
  deck = { h = 4 }, ramp = { h = 4 }, stairs = { h = 4 },
  canyon = { h = 16, sky = true }, cave = { h = 6 },
}
local KIND_LIST = "room, corridor, yard, pit, deck, ramp, stairs, canyon, cave, path"
local ROCKY = { canyon = true, cave = true }

-- Materials: the kit.palette themes (and their aliases) give a region its floor / wall / ceiling family; metal and
-- grate add the stock industrial family (grate with the stock grating floor and ceiling).
local PALETTE_THEMES = { industrial = true, bunker = true, admin = true, cave = true, marsbase = true,
  tech = true, office = true, rock = true, mine = true, concrete = true, mars = true }
local MATERIAL_EXTRA = {
  metal = { theme = "industrial" }, metal_floor = { theme = "industrial" },
  grate = { theme = "industrial", floor = "mtl_dark_grating01.tga", ceiling = "mtl_merc_grating_lite01.tga" },
}
local MATERIAL_LIST = "concrete, metal, metal_floor, grate, rock, industrial, bunker, admin, cave, marsbase, mars, tech, office, mine"

-- A material name (or a kit.palette record) as {floor, wall, ceiling} textures, plus the name it is reported as.
local function material_textures(api, key, m)
  if type(m) == "table" then
    if m.kind ~= "palette" or type(m.textures) ~= "table" then fail(api, "'" .. key .. "' must be a material name or a kit.palette{...} record.") end
    return { floor = m.textures.floor, wall = m.textures.wall, ceiling = m.textures.ceiling }, m.theme
  end
  if type(m) ~= "string" then fail(api, "'" .. key .. "' must be a material name or a kit.palette{...} record.", "Materials: " .. MATERIAL_LIST .. ".") end
  local nm = m:lower()
  local extra = MATERIAL_EXTRA[nm]
  if extra == nil and not PALETTE_THEMES[nm] then fail(api, "'" .. key .. "': unknown material '" .. m .. "'.", "Materials: " .. MATERIAL_LIST .. ".") end
  local pal = kit.palette{ theme = extra and extra.theme or nm }
  local tx = { floor = pal.textures.floor, wall = pal.textures.wall, ceiling = pal.textures.ceiling }
  if extra then tx.floor, tx.ceiling = extra.floor or tx.floor, extra.ceiling or tx.ceiling end
  return tx, nm
end

local function q16(v) return math.floor(v * 16 + 0.5) / 16 end
local function dist(a, b) return math.sqrt((a[1] - b[1]) ^ 2 + (a[2] - b[2]) ^ 2) end
local function P(p) return "(" .. fmt(p[1]) .. ", " .. fmt(p[2]) .. ")" end

-- Distance from p to segment ab, the closest point and its parameter.
local function seg_near(p, a, b)
  local vx, vz = b[1] - a[1], b[2] - a[2]
  local l2 = vx * vx + vz * vz
  local t = l2 > 0 and math.max(0, math.min(1, ((p[1] - a[1]) * vx + (p[2] - a[2]) * vz) / l2)) or 0
  local q = { a[1] + vx * t, a[2] + vz * t }
  return dist(p, q), q, t
end

-- The nearest edge of a CCW polygon to p: distance, closest point, edge index, unit direction, inward normal.
local function nearest_edge(poly, p)
  local best, bq, bi = math.huge, nil, nil
  for i = 1, #poly do
    local d, q = seg_near(p, poly[i], poly[i % #poly + 1])
    if d < best then best, bq, bi = d, q, i end
  end
  local a, b = poly[bi], poly[bi % #poly + 1]
  local L = dist(a, b)
  local ux, uz = (b[1] - a[1]) / L, (b[2] - a[2]) / L
  return best, bq, bi, { ux, uz }, { -uz, ux }
end

-- Sutherland-Hodgman: convex subject clipped by convex CCW clip polygon.
local function clip(subject, clipper)
  local out = subject
  for i = 1, #clipper do
    local a, b = clipper[i], clipper[i % #clipper + 1]
    local input = out
    out = {}
    if #input == 0 then break end
    local function side(q) return cross2(a, b, q) end
    for k = 1, #input do
      local u, v = input[k], input[k % #input + 1]
      local su, sv = side(u), side(v)
      if su >= 0 then out[#out + 1] = u end
      if (su >= 0) ~= (sv >= 0) then
        local f = su / (su - sv)
        out[#out + 1] = { u[1] + (v[1] - u[1]) * f, u[2] + (v[2] - u[2]) * f }
      end
    end
  end
  return out
end

-- A convex polygon cut to the band lo <= dot(p, c) <= hi.
local function band(poly, c, lo, hi)
  local function cut(pts, sgn, lim)
    local out = {}
    for k = 1, #pts do
      local u, v = pts[k], pts[k % #pts + 1]
      local su = sgn * (u[1] * c[1] + u[2] * c[2] - lim)
      local sv = sgn * (v[1] * c[1] + v[2] * c[2] - lim)
      if su <= 0 then out[#out + 1] = u end
      if (su <= 0) ~= (sv <= 0) then
        local f = su / (su - sv)
        out[#out + 1] = { u[1] + (v[1] - u[1]) * f, u[2] + (v[2] - u[2]) * f }
      end
    end
    return out
  end
  local r = cut(poly, -1, lo)
  if #r < 3 then return nil end
  r = cut(r, 1, hi)
  if #r < 3 or area2(r) < 1e-4 then return nil end
  return r
end

-- Two segments cross properly (touching at an end does not count).
local function crosses(a, b, c, d)
  local d1, d2, d3, d4 = cross2(a, b, c), cross2(a, b, d), cross2(c, d, a), cross2(c, d, b)
  return ((d1 > 1e-9 and d2 < -1e-9) or (d1 < -1e-9 and d2 > 1e-9)) and ((d3 > 1e-9 and d4 < -1e-9) or (d3 < -1e-9 and d4 > 1e-9))
end

-- The part of a convex polygon left of the line a -> b (cross2 >= 0), nil when nothing is left.
local function left_of(poly, a, b)
  local out = {}
  for k = 1, #poly do
    local u, v = poly[k], poly[k % #poly + 1]
    local su, sv = cross2(a, b, u), cross2(a, b, v)
    if su >= 0 then out[#out + 1] = u end
    if (su >= 0) ~= (sv >= 0) then
      local f = su / (su - sv)
      out[#out + 1] = { u[1] + (v[1] - u[1]) * f, u[2] + (v[2] - u[2]) * f }
    end
  end
  if #out < 3 or area2(out) < 1e-4 then return nil end
  return out
end

-- The plane y = A x + B z + C through three plan points with heights (nil when they are collinear).
local function plane3(p, q, r, hp, hq, hr)
  local d = (q[1] - p[1]) * (r[2] - p[2]) - (r[1] - p[1]) * (q[2] - p[2])
  if math.abs(d) < 1e-9 then return nil end
  local A = ((hq - hp) * (r[2] - p[2]) - (hr - hp) * (q[2] - p[2])) / d
  local B = ((q[1] - p[1]) * (hr - hp) - (r[1] - p[1]) * (hq - hp)) / d
  return { A, B, hp - A * p[1] - B * p[2] }
end

-- Length along which polygons A and B share a wall line (collinear edges overlapping, within tol).
local function shared(A, B, tol)
  local total, mid = 0, nil
  for i = 1, #A do
    local a0, a1 = A[i], A[i % #A + 1]
    local L = dist(a0, a1)
    if L > 1e-6 then
      local ux, uz = (a1[1] - a0[1]) / L, (a1[2] - a0[2]) / L
      for j = 1, #B do
        local b0, b1 = B[j], B[j % #B + 1]
        local function off(p) return math.abs((p[1] - a0[1]) * -uz + (p[2] - a0[2]) * ux) end
        if off(b0) <= tol and off(b1) <= tol then
          local s0 = (b0[1] - a0[1]) * ux + (b0[2] - a0[2]) * uz
          local s1 = (b1[1] - a0[1]) * ux + (b1[2] - a0[2]) * uz
          local lo, hi = math.max(0, math.min(s0, s1)), math.min(L, math.max(s0, s1))
          if hi - lo > 1e-3 then
            total = total + (hi - lo)
            local m = (lo + hi) / 2
            mid = mid or { a0[1] + ux * m, a0[2] + uz * m }
          end
        end
      end
    end
  end
  return total, mid
end

-- The spans (metres along a0 -> a1) where an edge of polygon B lies on the segment a0-a1 (collinear within tol).
local function spans_on(a0, a1, B, tol)
  local out, L = {}, dist(a0, a1)
  if L < 1e-6 then return out, L end
  local ux, uz = (a1[1] - a0[1]) / L, (a1[2] - a0[2]) / L
  local function off(p) return math.abs((p[1] - a0[1]) * -uz + (p[2] - a0[2]) * ux) end
  for j = 1, #B do
    local b0, b1 = B[j], B[j % #B + 1]
    if off(b0) <= tol and off(b1) <= tol then
      local s0 = (b0[1] - a0[1]) * ux + (b0[2] - a0[2]) * uz
      local s1 = (b1[1] - a0[1]) * ux + (b1[2] - a0[2]) * uz
      local lo, hi = math.max(0, math.min(s0, s1)), math.min(L, math.max(s0, s1))
      if hi - lo > 1e-3 then out[#out + 1] = { lo, hi } end
    end
  end
  return out, L
end

-- The parts of [0, L] not covered by spans (pieces shorter than 5 cm dropped).
local function minus(L, spans)
  table.sort(spans, function(p, q) return p[1] < q[1] end)
  local out, at = {}, 0
  for _, s in ipairs(spans) do
    if s[1] > at + 0.05 then out[#out + 1] = { at, s[1] } end
    at = math.max(at, s[2])
  end
  if L > at + 0.05 then out[#out + 1] = { at, L } end
  return out
end

local function along(a0, a1, L, s) return { a0[1] + (a1[1] - a0[1]) * s / L, a0[2] + (a1[2] - a0[2]) * s / L } end

-- Points: units "m" = {x, z} world metres; "px" = {x, y} picture pixels through the ref calibration.
local function points(api, key, list, units)
  if type(list) ~= "table" or #list < 3 then fail(api, "'" .. key .. "' must list at least 3 points.") end
  if #list > 256 then fail(api, "'" .. key .. "' has " .. #list .. " points; at most 256.", "Simplify the outline (ref.mask simplify) or split the region.") end
  local raw = {}
  for i, q in ipairs(list) do
    local x, z = nil, nil
    if type(q) == "table" then x, z = q[1] or q.x, q[2] or q.z or q.y end
    if type(x) ~= "number" or type(z) ~= "number" then fail(api, "'" .. key .. "[" .. i .. "]' must be a point {x, " .. (units == "px" and "y" or "z") .. "}.") end
    raw[i] = { x, z }
  end
  if units == "px" then raw = ref.world_points(raw) end
  local out = {}
  for _, q in ipairs(raw) do
    local p = { q16(q[1]), q16(q[2]) }
    local last = out[#out]
    if last == nil or math.abs(last[1] - p[1]) + math.abs(last[2] - p[2]) > 1e-6 then out[#out + 1] = p end
  end
  if #out > 1 and math.abs(out[1][1] - out[#out][1]) + math.abs(out[1][2] - out[#out][2]) < 1e-6 then out[#out] = nil end
  return out
end

local function point(api, key, q, units)
  local x, z = nil, nil
  if type(q) == "table" then x, z = q[1] or q.x, q[2] or q.z or q.y end
  if type(x) ~= "number" or type(z) ~= "number" then fail(api, "'" .. key .. "' must be a point {x, " .. (units == "px" and "y" or "z") .. "}.") end
  if units == "px" then local w = ref.to_world(x, z) return { w[1], w[2] } end
  return { x, z }
end

local function ccw(p)
  if area2(p) >= 0 then return p end
  local r = {}
  for i = #p, 1, -1 do r[#r + 1] = p[i] end
  return r
end

-- Validates one region (corners, simple outline, area), splits it into convex pieces and gives it floor_at.
local function prepare(api, g)
  local p = {}
  for _, q in ipairs(g.poly) do
    local last = p[#p]
    if last == nil or dist(last, q) > 1e-6 then p[#p + 1] = q end
  end
  if #p > 1 and dist(p[1], p[#p]) < 1e-6 then p[#p] = nil end
  if #p < 3 then fail(api, "'" .. g.name .. "' has fewer than 3 distinct corners after welding.", "Lower tolerance, or check the points.") end
  p = ccw(p)
  for i = 1, #p do
    for j = i + 2, #p do
      if not (i == 1 and j == #p) and crosses(p[i], p[i % #p + 1], p[j], p[j % #p + 1]) then
        fail(api, "'" .. g.name .. "': its outline crosses itself (edge " .. i .. " and edge " .. j .. ", near " .. P(p[i]) .. ").", "List the corners in order around the outline.")
      end
    end
  end
  g.poly = p
  g.area = area2(p)
  if g.area < 1 then fail(api, "'" .. g.name .. "' encloses " .. fmt(g.area) .. " m² (at least 1 m²).") end
  g.pieces = convex_pieces(p)
  if g.pieces == nil then fail(api, "'" .. g.name .. "' could not be split into convex pieces (its outline touches itself).", "Separate the touching corners.") end
  -- floor height at a plan point (ramps and stairs rise along climb)
  if g.surface then
    -- a sloped path segment: two planes split by its diagonal, through the segment's corner heights, so the floor
    -- meets the next segment's exactly along the shared joint line
    local S = g.surface
    for k, pl in ipairs({ S.left, S.right }) do
      if math.sqrt(pl[1] * pl[1] + pl[2] * pl[2]) > 1 + 1e-6 then
        fail(api, "'" .. g.name .. "': this path segment rises steeper than 45 degrees" .. (k == 1 and "" or " (across its bend)") .. ".",
          "Space the path's points further apart, or lower the change in floors along it.")
      end
    end
    local function plane_at(q)
      local pl = cross2(S.a, S.b, q) >= 0 and S.left or S.right
      return pl[1] * q[1] + pl[2] * q[2] + pl[3]
    end
    function g.floor_at(q) return math.max(g.lo, math.min(g.hi, plane_at(q))) end
    g.plane_at = plane_at
  elseif g.kind == "ramp" or g.kind == "stairs" then
    local s0, s1 = math.huge, -math.huge
    for _, q in ipairs(p) do local s = q[1] * g.climb[1] + q[2] * g.climb[2] s0, s1 = math.min(s0, s), math.max(s1, s) end
    g.s0, g.s1 = s0, s1
    local run, rise = s1 - s0, g.hi - g.lo
    if rise < 1e-6 then fail(api, "'" .. g.name .. "' is a " .. g.kind .. " with no rise: floor = {low, high} must differ (or make it a room).") end
    if rise > run + 1e-6 then
      fail(api, "'" .. g.name .. "': an impossible " .. g.kind .. ": it rises " .. fmt(rise) .. " m over a run of " .. fmt(run) .. " m along climb (steeper than 45 degrees).",
        "Lengthen it along climb to at least " .. fmt(rise) .. " m, lower the rise, or check climb points uphill along its long side.")
    end
    if g.kind == "stairs" then
      g.steps = math.max(1, math.ceil(rise / g.riser - 1e-6))
      if rise / g.steps > kit.limits.max_riser + 1e-6 then g.steps = math.ceil(rise / kit.limits.max_riser - 1e-6) end
      g.tread = run / g.steps
    end
    function g.floor_at(q)
      local f = ((q[1] * g.climb[1] + q[2] * g.climb[2]) - g.s0) / (g.s1 - g.s0)
      return g.lo + (g.hi - g.lo) * math.max(0, math.min(1, f))
    end
  else
    function g.floor_at() return g.lo end
  end
end

---@kit kit.trace{name?, regions, openings?, solids?, storeys?, units?, tolerance?, check?, build?, via?, lights?, detail?, material?|texture?|textures?} → trace — Builds exactly the plan given (from a reference) through the layout builder: literal zones, doorways, kit ramps/stairs, decks, paths, storeys, blocks; trims, frames, lights; rock elsewhere.
---@opt name: string = trace<n> — Names the plan (tags, group).
---@opt regions: table[] — Required: {name, poly = {{x, z}...}, kind = room|corridor|yard|pit|deck|ramp|stairs|canyon|cave|path, floor (ramp/stairs: {low, high}), ceiling? or height?, sky?, storey?, climb = {dx, dz} (ramp/stairs uphill), riser?, thickness? (deck), material?|texture?|textures?}. kind = path: path = {{x, z}...} centre line, width|widths, floor|floors per point (a sloped or curved ramp), split into name.1, name.2... A ramp or stairs may have any outline (one sloped floor from floor[1] at its foot to floor[2] along climb); standing on its own its ceiling runs parallel to its floor at its height (an absolute ceiling keeps it flat). Ramps, stairs and decks may lie inside another region; others overlap only as stacked storeys. Organic (detail rock over the traced air; the floor keeps the outline within 0.35 m): kind = "canyon" (open sky, 16 m, rock walls in strata that step back above 0.3 x height by lean? = 1.5 m where no other space is near, overhang?) or "cave" (rock walls with a vault and hanging rock, pillars? = count or points); organic = true on a path or room (a rough tunnel); walls = "rock" or rock_edges = {i...} (edge i runs from point i to i + 1); ground = "organic" (gentle mounds, relief? = 0.5) or "terrain" (kit.ground); boulders = count or {count?, density? (per 100 m²), size?}; rock_detail = true (kit.rock_detail); seed?. Doorways, keep_clear routes and hosted ramps stay clear; rocky regions default to material "rock".
---@opt keep_clear: {x, z}[][] = none — Routes (polylines in the plan's units, e.g. a cart route) that boulders, mounds and rock formations keep at least 1.75 m clear of.
---@opt seed: integer = 1 — The random stream of the organic rock (each region adds its own seed).
---@opt storeys: table[] = none — {name, floor, ceiling? | height?}: a storey a region joins with storey = name (its floor and ceiling defaults); stacked regions need lower ceiling <= upper floor - 0.25.
---@opt openings: table[] = none — {a, b, at = {x, z}, width = 3, height = 3}: a doorway of width cut through the gap between regions a and b at the point at (on or between their walls, at most 4 m from each); floors must agree within 0.5 m.
---@opt solids: table[] = none — {poly | circle = {x, z, r}, region?, base?, top? (a height, or "ceiling") | height? (1.5), detail? (true)}: a block standing in a region (pillar, machine footprint); base defaults to the floor of the region it stands in.
---@opt units: string = "m" — "m": points are world {x, z} metres; "px": points (and at, circle, climb, path widths) are picture pixels {x, y} mapped through ref.calibrate.
---@opt tolerance: number = 0.15 — Weld distance (m): corners this close to another region's corner or wall move onto it (and are inserted into that wall), so shared walls match; a thinner wedge left between two regions is refused.
---@opt check: boolean = false — After building, run the leak check (a sealed build, a few seconds): trace.holes and trace.hole_positions near the plan; holes also log a warning.
---@opt build: boolean = true — false validates and reports (areas, connections with their drop and ledge flag, hosts, stacked storeys, warnings, and with via = "layout" the graph and layout_check) without building.
---@opt via: string = "layout" — "layout": the plan becomes a kit.layout graph of literal zones (shared walls as joins, doorways as links at their traced places) built by kit.build_layout: kit.check_layout first (trace.layout_check {ok, errors, flow, rules}), ramps and stairs that are rectangles along climb through kit.ramp / kit.stairs (purpose = "require": one that serves no route is warned and still built), the room detail pass and lights (trace.layout, trace.detail, trace.literal_notes: where literal and generated building differ). "prism": the plan's own prisms only.
---@opt lights: boolean = true — via = "layout": the layout builder's lights in every zone (false when kit.dress_trace or a script lights the plan).
---@opt detail: boolean|table = true — via = "layout": kit.build_layout's room detail pass (wall trims and pilasters, door frames, ceiling beams, railings at drops, stair stringers and handrails, rock formations); a table turns parts off as there.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face (a region's own texture/textures wins).
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt material: string|palette = none — Textures for every region by material (a region's own material/texture wins): concrete, metal, metal_floor, grate, rock, or a kit.palette theme or record. Mapped at 64 px/m, aligned across the plan.
function kit.trace(t)
  local api = "kit.trace"
  check_keys(api, t, { "name", "regions", "openings", "solids", "storeys", "units", "tolerance", "check", "build", "texture", "textures", "material",
    "keep_clear", "seed", "via", "lights", "detail" })
  local name = name_for(api, "trace", t.name)
  local via = t.via or "layout"
  if via ~= "layout" and via ~= "prism" then fail(api, "'via' must be \"layout\" or \"prism\" (got " .. tostring(via) .. ").") end
  local want_lights = boolean(api, "lights", t.lights, true)
  if t.detail ~= nil and type(t.detail) ~= "boolean" and type(t.detail) ~= "table" then fail(api, "'detail' must be true, false or a table (kit.build_layout's detail option).") end
  if via == "prism" and (t.lights ~= nil or t.detail ~= nil) then fail(api, "'lights' and 'detail' apply to via = \"layout\" (the prism path builds the plan only).") end
  local units = t.units or "m"
  if units ~= "m" and units ~= "px" then fail(api, "'units' must be \"m\" or \"px\" (got " .. tostring(units) .. ").") end
  if units == "px" and not ref.info().calibrated then
    fail(api, "units = \"px\" needs a calibrated reference.", "Run ref.open(picture) and ref.calibrate{...} first, or give world metres.")
  end
  local tol = t.tolerance ~= nil and number(api, "tolerance", t.tolerance) or 0.15
  if tol < 0 or tol > 2 then fail(api, "'tolerance' must be 0 to 2 metres.") end
  local building = boolean(api, "build", t.build, true)
  local checking = boolean(api, "check", t.check, false)
  local base_tex, base_texs = textures_of(api, t)
  local base_mat = nil
  if t.material ~= nil then
    if t.texture ~= nil or t.textures ~= nil then fail(api, "give material or texture/textures, not both.") end
    base_texs, base_mat = material_textures(api, "material", t.material)
    base_tex = nil
  end
  if type(t.regions) ~= "table" or #t.regions == 0 then fail(api, "'regions' must list at least one region.") end
  local MIN_SLAB = 0.25

  -- climb vector: px units turn a picture direction into a world direction
  local function direction(key, v)
    if type(v) ~= "table" or type(v[1]) ~= "number" or type(v[2]) ~= "number" then fail(api, "'" .. key .. "' must be a direction {dx, dz}.") end
    local dx, dz = v[1], v[2]
    if units == "px" then
      local a, b = ref.to_world(0, 0), ref.to_world(v[1], v[2])
      dx, dz = b[1] - a[1], b[2] - a[2]
    end
    local L = math.sqrt(dx * dx + dz * dz)
    if L < 1e-9 then fail(api, "'" .. key .. "' must not be zero.") end
    return { dx / L, dz / L }
  end
  local function metres(v) -- a length in the plan's units, in metres
    if units ~= "px" then return v end
    local a, b = ref.to_world(0, 0), ref.to_world(v, 0)
    return dist(a, b)
  end

  -- 0. Storeys: named floor/ceiling defaults.
  local storeys = {}
  for i, s in ipairs(t.storeys or {}) do
    local key = "storeys[" .. i .. "]"
    check_keys(api .. "." .. key, s, { "name", "floor", "ceiling", "height" })
    if type(s.name) ~= "string" or s.name == "" then fail(api, key .. " needs a name.") end
    if storeys[s.name] then fail(api, "two storeys are named '" .. s.name .. "'.") end
    local f = number(api, key .. ".floor", s.floor)
    if s.ceiling ~= nil and s.height ~= nil then fail(api, key .. ": give ceiling or height, not both.") end
    storeys[s.name] = { floor = f, ceiling = s.ceiling ~= nil and number(api, key .. ".ceiling", s.ceiling) or nil,
      height = s.height ~= nil and positive(api, key .. ".height", s.height) or nil }
  end

  -- 1. Read the regions (a path region becomes one region per segment).
  local R, by, paths = {}, {}, {}
  local function add(g)
    if by[g.name] or paths[g.name] then fail(api, "two regions are named '" .. g.name .. "'.") end
    g.index = #R + 1
    R[#R + 1] = g
    by[g.name] = g
  end
  local function heights(g, r, key, K, st, floor_hi)
    if r.ceiling ~= nil and r.height ~= nil then fail(api, "'" .. r.name .. "': give ceiling or height, not both.") end
    local ceiling, height = r.ceiling, r.height
    if ceiling == nil and height == nil and st ~= nil then ceiling, height = st.ceiling, st.height end
    g.top_given = ceiling ~= nil or height ~= nil
    g.ceiling_given = ceiling ~= nil -- an absolute ceiling (a ramp's then stays flat; a height runs parallel to its floor)
    g.top = ceiling ~= nil and number(api, key .. ".ceiling", ceiling) or (floor_hi + (height ~= nil and positive(api, key .. ".height", height) or K.h))
    if g.top - floor_hi < 1 then fail(api, "'" .. g.name .. "': ceiling " .. fmt(g.top) .. " leaves " .. fmt(g.top - floor_hi) .. " m over the floor (at least 1 m; a player needs 2).") end
    g.sky_given = r.sky ~= nil
    g.sky = boolean(api, key .. ".sky", r.sky, K.sky or false)
    g.storey = r.storey
    if r.texture ~= nil or r.textures ~= nil then
      if r.material ~= nil then fail(api, "'" .. r.name .. "': give material or texture/textures, not both.") end
      g.texture, g.textures = textures_of(api, r)
    elseif r.material ~= nil then
      g.textures, g.material = material_textures(api, key .. ".material", r.material)
    elseif ROCKY[r.kind or ""] or r.organic == true or r.walls == "rock" then
      -- canyons, caves and rock-walled regions default to the rock palette (not the plan's built material)
      g.textures, g.material = material_textures(api, key .. ".material", "rock")
      -- a cave's or tunnel's roof is the same rock as its walls
      if r.kind == "cave" or r.organic == true then g.textures = { floor = g.textures.floor, wall = g.textures.wall, ceiling = g.textures.wall } end
    else
      g.texture, g.textures, g.material = base_tex, base_texs, base_mat
    end
  end

  -- Organic options of a region (canyon / cave kinds, rock walls, ground, rock dressing), validated once.
  local function organic_opts(r, key, kind)
    local o = {}
    local any = ROCKY[kind] or false
    if r.walls ~= nil then
      if r.walls ~= "rock" then fail(api, "'" .. r.name .. "': walls must be \"rock\" (got " .. tostring(r.walls) .. ").") end
      o.walls = "rock" any = true
    end
    if r.rock_edges ~= nil then
      if kind == "path" then fail(api, "'" .. r.name .. "': rock_edges apply to polygon regions; give a path organic = true.") end
      if type(r.rock_edges) ~= "table" or #r.rock_edges == 0 then fail(api, "'" .. r.name .. "': rock_edges must list edge numbers (edge i runs from point i to point i + 1).") end
      o.rock_edges = {}
      for i, e in ipairs(r.rock_edges) do
        if type(e) ~= "number" or e ~= math.floor(e) or e < 1 then fail(api, "'" .. r.name .. "': rock_edges[" .. i .. "] must be an edge number (1 or more).") end
        o.rock_edges[#o.rock_edges + 1] = e
      end
      any = true
    end
    if r.organic ~= nil then
      o.organic = boolean(api, key .. ".organic", r.organic, false)
      if o.organic then
        if kind == "ramp" or kind == "stairs" or kind == "deck" then fail(api, "'" .. r.name .. "': organic applies to rooms, corridors and paths (a " .. kind .. " takes its host's walls).") end
        any = true
      end
    end
    if r.ground ~= nil then
      if r.ground ~= "flat" and r.ground ~= "organic" and r.ground ~= "terrain" then fail(api, "'" .. r.name .. "': ground must be \"flat\", \"organic\" or \"terrain\" (got " .. tostring(r.ground) .. ").") end
      if r.ground ~= "flat" and (kind == "ramp" or kind == "stairs" or kind == "deck" or kind == "path") then fail(api, "'" .. r.name .. "': ground = \"" .. r.ground .. "\" applies to level regions (rooms, yards, pits, canyons, caves).") end
      o.ground = r.ground any = any or r.ground ~= "flat"
    end
    if r.relief ~= nil then
      o.relief = number(api, key .. ".relief", r.relief)
      if o.relief < 0 or o.relief > 3 then fail(api, "'" .. r.name .. "': relief must be 0 to 3 metres.") end
    end
    if r.boulders ~= nil then
      local b = r.boulders
      if type(b) == "number" then b = { count = b } end
      if type(b) ~= "table" then fail(api, "'" .. r.name .. "': boulders must be a count or {count?, density?, size?}.") end
      check_keys(api .. "." .. key .. ".boulders", b, { "count", "density", "size" })
      local sz = b.size or { 0.7, 1.8 }
      if type(sz) == "number" then sz = { sz, sz } end
      if type(sz) ~= "table" or type(sz[1]) ~= "number" or type(sz[2]) ~= "number" or sz[1] <= 0 or sz[2] < sz[1] then fail(api, "'" .. r.name .. "': boulders.size must be a diameter or {min, max}.") end
      o.boulders = { count = b.count ~= nil and number(api, key .. ".boulders.count", b.count) or nil,
        density = b.density ~= nil and number(api, key .. ".boulders.density", b.density) or nil, size = sz }
      if o.boulders.count == nil and o.boulders.density == nil then o.boulders.density = 1.5 end
      any = true
    end
    if r.rock_detail ~= nil then o.rock_detail = boolean(api, key .. ".rock_detail", r.rock_detail, false) any = any or o.rock_detail end
    if r.overhang ~= nil then
      o.overhang = number(api, key .. ".overhang", r.overhang)
      if o.overhang < 0 or o.overhang > 4 then fail(api, "'" .. r.name .. "': overhang must be 0 to 4 metres.") end
    end
    if r.lean ~= nil then
      o.lean = number(api, key .. ".lean", r.lean)
      if o.lean < 0 or o.lean > 6 then fail(api, "'" .. r.name .. "': lean must be 0 to 6 metres (how far the upper walls step back).") end
    end
    if r.pillars ~= nil then
      if type(r.pillars) == "number" then o.pillars = { count = math.floor(r.pillars) }
      elseif type(r.pillars) == "table" then
        o.pillars = { at = {} }
        for i, q in ipairs(r.pillars) do o.pillars.at[i] = point(api, key .. ".pillars[" .. i .. "]", q, units) end
      else fail(api, "'" .. r.name .. "': pillars must be a count or a list of points {x, z}.") end
    end
    if r.seed ~= nil then o.seed = number(api, key .. ".seed", r.seed) end
    if not any then return nil end
    return o
  end

  -- A path: centre line + width; segments joined by mitres, so neighbours share their joint line exactly.
  local function expand_path(r, key, st)
    if r.poly ~= nil then fail(api, "'" .. r.name .. "' is a path: give path = {{x, z}...} (its centre line), not poly.") end
    if type(r.path) ~= "table" or #r.path < 2 then fail(api, "'" .. r.name .. "' is a path: path must list at least 2 centre-line points.") end
    if #r.path > 128 then fail(api, "'" .. r.name .. "': path has " .. #r.path .. " points; at most 128.") end
    local raw = {}
    for i, q in ipairs(r.path) do raw[i] = point(api, key .. ".path[" .. i .. "]", q, units) end
    local n = #raw
    local function per_point(list, scalar, what, conv)
      if list ~= nil then
        if type(list) ~= "table" or #list ~= n then fail(api, "'" .. r.name .. "': " .. what .. "s must list one number per path point (" .. n .. ").") end
        local out = {}
        for i, v in ipairs(list) do out[i] = conv(number(api, key .. "." .. what .. "s[" .. i .. "]", v)) end
        return out
      end
      local out = {}
      for i = 1, n do out[i] = scalar end
      return out
    end
    if r.width ~= nil and r.widths ~= nil then fail(api, "'" .. r.name .. "': give width or widths, not both.") end
    if r.floor ~= nil and r.floors ~= nil then fail(api, "'" .. r.name .. "': give floor or floors, not both.") end
    local W = per_point(r.widths, r.width ~= nil and metres(positive(api, key .. ".width", r.width)) or 3, "width", metres)
    local fl = r.floor
    if fl == nil and r.floors == nil and st ~= nil then fl = st.floor end
    local F = per_point(r.floors, fl ~= nil and number(api, key .. ".floor", fl) or nil, "floor", function(v) return v end)
    if F[1] == nil then fail(api, "'" .. r.name .. "' needs floor (or floors, one per path point).") end
    for i = 1, n do if W[i] < 1 then fail(api, "'" .. r.name .. "': width " .. fmt(W[i]) .. " m at path point " .. i .. " (at least 1 m).") end end
    -- unit directions and left normals per segment
    local D = {}
    for i = 1, n - 1 do
      local L = dist(raw[i], raw[i + 1])
      if L < 0.25 then fail(api, "'" .. r.name .. "': path points " .. i .. " and " .. (i + 1) .. " are " .. fmt(L) .. " m apart (at least 0.25 m).") end
      D[i] = { (raw[i + 1][1] - raw[i][1]) / L, (raw[i + 1][2] - raw[i][2]) / L }
    end
    local Lp, Rp = {}, {}
    for i = 1, n do
      local nx, nz
      local scale = 1
      if i == 1 or i == n then
        local d = D[i == 1 and 1 or n - 1]
        nx, nz = -d[2], d[1]
      else
        local a, b = D[i - 1], D[i]
        local mx, mz = -a[2] - b[2], a[1] + b[1]
        local ml = math.sqrt(mx * mx + mz * mz)
        local c = ml > 1e-9 and (mx / ml) * -a[2] + (mz / ml) * a[1] or 0
        if c < 0.5 then
          fail(api, "'" .. r.name .. "': the turn at path point " .. i .. " " .. P(raw[i]) .. " is too sharp to mitre (" .. fmt(math.deg(2 * math.acos(math.max(-1, math.min(1, c))))) .. " degrees; at most 120).",
            "Add a point to round the bend, or trace that corner as its own room.")
        end
        nx, nz, scale = mx / ml, mz / ml, 1 / c
      end
      local h = W[i] / 2 * scale
      Lp[i] = { q16(raw[i][1] + nx * h), q16(raw[i][2] + nz * h) }
      Rp[i] = { q16(raw[i][1] - nx * h), q16(raw[i][2] - nz * h) }
    end
    local names = {}
    local K = KINDS.corridor
    for i = 1, n - 1 do
      local quad = { Rp[i], Rp[i + 1], Lp[i + 1], Lp[i] }
      for k = 1, 4 do
        if cross2(quad[k], quad[k % 4 + 1], quad[(k + 1) % 4 + 1]) <= 1e-9 then
          fail(api, "'" .. r.name .. "': segment " .. i .. " (path points " .. i .. " to " .. (i + 1) .. ") is too short for the turns at its ends (its mitres cross).",
            "Space the points further apart than the path's width, or drop a point.")
        end
      end
      local g = { name = r.name .. "." .. i, kind = "corridor", path = r.name, poly = quad }
      local f0, f1 = F[i], F[i + 1]
      g.lo, g.hi = math.min(f0, f1), math.max(f0, f1)
      if math.abs(f1 - f0) > 1e-6 then
        g.kind = "ramp"
        g.surface = { a = Rp[i], b = Lp[i + 1],
          left = plane3(Rp[i], Lp[i + 1], Lp[i], f0, f1, f0), right = plane3(Rp[i], Rp[i + 1], Lp[i + 1], f0, f1, f1) }
      end
      heights(g, r, key, K, st, g.hi)
      add(g)
      names[#names + 1] = g.name
    end
    paths[r.name] = names
  end

  for i, r in ipairs(t.regions) do
    local key = "regions[" .. i .. "]"
    check_keys(api .. "." .. key, r, { "name", "poly", "path", "width", "widths", "floors", "kind", "floor", "ceiling", "height", "sky", "storey",
      "climb", "riser", "thickness", "texture", "textures", "material",
      "walls", "rock_edges", "organic", "ground", "relief", "boulders", "rock_detail", "overhang", "lean", "pillars", "seed" })
    if type(r.name) ~= "string" or r.name == "" then fail(api, key .. " needs a name.") end
    local kind = r.kind or "room"
    local org = organic_opts(r, key, kind)
    local st = nil
    if r.storey ~= nil then
      st = storeys[r.storey]
      if st == nil then fail(api, "'" .. r.name .. "': unknown storey '" .. tostring(r.storey) .. "' (declare it in storeys = {{name, floor, ceiling?|height?}}).") end
    end
    if kind == "path" then
      expand_path(r, key, st)
      if org then for _, sn in ipairs(paths[r.name]) do by[sn].org = org end end
    else
      if r.path ~= nil or r.width ~= nil or r.widths ~= nil or r.floors ~= nil then fail(api, "'" .. r.name .. "': path, width, widths and floors apply to kind = \"path\".") end
      local K = KINDS[kind]
      if K == nil then fail(api, key .. " ('" .. r.name .. "'): kind must be one of " .. KIND_LIST .. " (got " .. tostring(kind) .. ").") end
      local g = { name = r.name, kind = kind, poly = points(api, key .. ".poly", r.poly, units), org = org }
      if org and org.rock_edges then
        -- the traced edges (before welding inserts corners): a wall span on one of them is rock
        org.segs = {}
        local n = #g.poly
        for _, e in ipairs(org.rock_edges) do
          if e > n then fail(api, "'" .. r.name .. "': rock_edges names edge " .. e .. " but the outline has " .. n .. " edges.") end
          org.segs[#org.segs + 1] = { g.poly[e], g.poly[e % n + 1] }
        end
      end
      local sloped = kind == "ramp" or kind == "stairs"
      if sloped then
        if type(r.floor) ~= "table" or type(r.floor[1]) ~= "number" or type(r.floor[2]) ~= "number" then
          fail(api, "'" .. r.name .. "' is a " .. kind .. ": floor must be {low, high}.")
        end
        g.lo, g.hi = r.floor[1], r.floor[2]
        if g.hi < g.lo then g.lo, g.hi = g.hi, g.lo end
        if r.climb == nil then fail(api, "'" .. r.name .. "' is a " .. kind .. ": give climb = {dx, dz}, the uphill direction.") end
        g.climb = direction(key .. ".climb", r.climb)
      else
        if r.climb ~= nil or r.riser ~= nil then fail(api, "'" .. r.name .. "': climb and riser apply to ramp and stairs regions.") end
        local f = r.floor
        if f == nil and st ~= nil then f = st.floor end
        g.lo = number(api, key .. ".floor", f)
        g.hi = g.lo
      end
      heights(g, r, key, K, st, g.hi)
      g.riser = r.riser ~= nil and positive(api, key .. ".riser", r.riser) or kit.defaults.riser
      g.thickness = r.thickness ~= nil and positive(api, key .. ".thickness", r.thickness) or 0.5
      if r.thickness ~= nil and kind ~= "deck" then fail(api, "'" .. r.name .. "': thickness applies to deck regions.") end
      add(g)
    end
  end

  -- 2. Weld. Corners near an earlier region's corner take it. Then corners near another region's wall move onto it
  --    AND are inserted into that wall (a T-vertex), in both directions until nothing changes, so two regions that
  --    share a wall carry the same corners along it (a nearly collinear seam closes instead of leaking).
  local welded, snapped, inserted = 0, 0, 0
  local function move_all(old, new)
    for _, h in ipairs(R) do
      for k, q in ipairs(h.poly) do
        if math.abs(q[1] - old[1]) < 1e-9 and math.abs(q[2] - old[2]) < 1e-9 then h.poly[k] = { new[1], new[2] } end
      end
    end
  end
  for i, g in ipairs(R) do
    for k, p in ipairs(g.poly) do
      local done = false
      for j = 1, i - 1 do
        for _, q in ipairs(R[j].poly) do
          local d = dist(p, q)
          if d > 1e-9 and d <= tol then g.poly[k] = { q[1], q[2] } welded = welded + 1 done = true break end
          if d <= 1e-9 then done = true break end
        end
        if done then break end
      end
    end
  end
  if tol > 0 then
    local budget = 0
    for _, g in ipairs(R) do budget = budget + #g.poly end
    budget = budget * 4
    for _ = 1, 8 do
      local changed = false
      for i, g in ipairs(R) do
        for j, h in ipairs(R) do
          if i ~= j then
            local e = 1
            while e <= #h.poly do
              local a, b = h.poly[e], h.poly[e % #h.poly + 1]
              local L = dist(a, b)
              local best = nil
              if L > 1e-6 then
                for k, p in ipairs(g.poly) do
                  local d, q, s = seg_near(p, a, b)
                  if d <= tol and s * L > 1e-4 and (1 - s) * L > 1e-4 and dist(p, a) > 1e-6 and dist(p, b) > 1e-6 and (best == nil or s < best.s) then
                    best = { k = k, q = q, s = s, d = d }
                  end
                end
              end
              if best and budget > 0 then
                budget = budget - 1
                local p = g.poly[best.k]
                if best.d > 1e-9 then move_all(p, best.q) snapped = snapped + 1 end
                local v = g.poly[best.k]
                table.insert(h.poly, e + 1, { v[1], v[2] })
                inserted = inserted + 1
                changed = true
              end
              e = e + 1
            end
          end
        end
      end
      if not changed then break end
    end
  end

  -- 3. Validate each region and decompose it.
  local warnings, total = {}, 0
  local function warn(msg) warnings[#warnings + 1] = msg end
  for i = 1, #R do
    prepare(api, R[i])
    total = total + R[i].area
  end
  local function sloped(g) return g.kind == "ramp" or g.kind == "stairs" end

  -- 4. Overlaps, hosts, stacked storeys, slivers and connections.
  --    Ramps, stairs and decks may stand inside another region (hosted); other regions may overlap only when one
  --    lies wholly above the other with a slab between (stacked storeys).
  local connections, stacked = {}, {}
  local function bounds(g)
    local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
    for _, q in ipairs(g.poly) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
    return x0, z0, x1, z1
  end
  for _, g in ipairs(R) do g.hosts = {} g.bx0, g.bz0, g.bx1, g.bz1 = bounds(g) end
  for i = 1, #R do
    for j = i + 1, #R do
      local a, b = R[i], R[j]
      if a.bx0 <= b.bx1 + tol and b.bx0 <= a.bx1 + tol and a.bz0 <= b.bz1 + tol and b.bz0 <= a.bz1 + tol then
        local over, at = 0, nil
        for _, pa in ipairs(a.pieces) do
          for _, pb in ipairs(b.pieces) do
            local c = clip(pa, pb)
            if #c >= 3 then
              local ar = area2(c)
              if ar > 1e-3 then
                over = over + ar
                if at == nil then local cx, cz = 0, 0 for _, q in ipairs(c) do cx, cz = cx + q[1], cz + q[2] end at = { cx / #c, cz / #c } end
              end
            end
          end
        end
        local overlapping = over > math.max(0.25, 0.01 * math.min(a.area, b.area))
        local relation = nil -- "hosted" | "stacked" | "deck"
        if overlapping then
          local inner, host = nil, nil
          if (a.kind == "deck" or sloped(a)) and not (b.kind == "deck" or sloped(b)) then inner, host = a, b
          elseif (b.kind == "deck" or sloped(b)) and not (a.kind == "deck" or sloped(a)) then inner, host = b, a end
          local lower, upper = a, b
          if b.top <= a.lo + 1e-6 then lower, upper = b, a end
          if inner and inner.lo < host.top - 1e-6 and inner.hi >= host.lo - 1e-6 then
            relation = "hosted"
            inner.hosts[#inner.hosts + 1] = host
          elseif lower.top + MIN_SLAB <= upper.lo + 1e-6 then
            relation = "stacked"
            stacked[#stacked + 1] = { lower = lower.name, upper = upper.name, slab = upper.lo - lower.top, area = over, at = at }
          elseif a.kind == "deck" or b.kind == "deck" then
            relation = "deck" -- a deck over another region's air, or above its ceiling: allowed as before
          elseif lower.top <= upper.lo + 1e-6 then
            fail(api, "regions '" .. lower.name .. "' and '" .. upper.name .. "' are stacked with a " .. fmt(upper.lo - lower.top) .. " m slab between them around " .. P(at) .. " (at least " .. fmt(MIN_SLAB) .. " m).",
              "Lower '" .. lower.name .. "''s ceiling to " .. fmt(upper.lo - MIN_SLAB) .. " or less, or make '" .. upper.name .. "' a deck.")
          else
            fail(api, "regions '" .. a.name .. "' and '" .. b.name .. "' overlap by " .. fmt(over) .. " m² (around " .. P(at) .. ") and share the space from " .. fmt(math.max(a.lo, b.lo)) .. " to " .. fmt(math.min(a.top, b.top)) .. ".",
              "Trace each floor once: move the shared wall so they only touch; make the upper one a deck; or stack them as storeys (lower ceiling at most upper floor - " .. fmt(MIN_SLAB) .. ").")
          end
        end
        if relation == nil and not (a.kind == "deck" or b.kind == "deck") then
          -- Slivers: facing walls of the two regions running nearly parallel and closer than a wall, but not on one line.
          for ea = 1, #a.poly do
            local a0, a1 = a.poly[ea], a.poly[ea % #a.poly + 1]
            local La = dist(a0, a1)
            if La > 1e-3 then
              local ux, uz = (a1[1] - a0[1]) / La, (a1[2] - a0[2]) / La
              for eb = 1, #b.poly do
                local b0, b1 = b.poly[eb], b.poly[eb % #b.poly + 1]
                local Lb = dist(b0, b1)
                if Lb > 1e-3 then
                  local vx, vz = (b1[1] - b0[1]) / Lb, (b1[2] - b0[2]) / Lb
                  if ux * vx + uz * vz < -0.98 then -- facing (opposite directions), within about 11 degrees
                    local function s_of(p) return (p[1] - a0[1]) * ux + (p[2] - a0[2]) * uz end
                    local function d_of(p) return (p[1] - a0[1]) * -uz + (p[2] - a0[2]) * ux end
                    local sb0, sb1 = s_of(b0), s_of(b1)
                    local lo, hi = math.max(0, math.min(sb0, sb1)), math.min(La, math.max(sb0, sb1))
                    if hi - lo > 0.05 then
                      -- b's offset from a's line, linear along the overlap
                      local function d_at(s) local f = (s - sb0) / (sb1 - sb0) return d_of(b0) + (d_of(b1) - d_of(b0)) * f end
                      local d0, d1 = d_at(lo), d_at(hi)
                      if math.max(math.abs(d0), math.abs(d1)) > 1e-3 then
                        -- length of the overlap where 1 mm < |offset| < 5 cm: a solid wedge too thin to build
                        local thin, steps = 0, 32
                        for k = 0, steps - 1 do
                          local d = math.abs(d0 + (d1 - d0) * (k + 0.5) / steps)
                          if d > 1e-3 and d < 0.05 then thin = thin + (hi - lo) / steps end
                        end
                        if thin > 0.1 then
                          local m = (lo + hi) / 2
                          fail(api, "regions '" .. a.name .. "' and '" .. b.name .. "' leave a wedge of wall " .. fmt(math.min(math.abs(d0), math.abs(d1))) .. " to " .. fmt(math.max(math.abs(d0), math.abs(d1))) .. " m thin between their walls near " .. P({ a0[1] + ux * m, a0[2] + uz * m }) .. " (it would leak).",
                            "Make the walls meet on one line (give both the same corners, or raise tolerance above the gap), or leave a wall at least 0.05 m thick.")
                        end
                      end
                    end
                  end
                end
              end
            end
          end
        end
        if relation == nil or relation == "deck" then
          local len, mid = shared(a.poly, b.poly, tol)
          if len > 0.25 then
            local fa, fb = a.floor_at(mid), b.floor_at(mid)
            local open = math.min(a.top, b.top) - math.max(fa, fb)
            if open > 0 then
              local drop = math.abs(fa - fb)
              -- a drop above a step along a shared line is a ledge: reported (it may be meant), never refused
              connections[#connections + 1] = { kind = "shared", a = a.name, b = b.name, length = len, at = mid, drop = drop, open_height = open,
                ledge = drop > kit.limits.max_riser + 1e-6 and a.kind ~= "deck" and b.kind ~= "deck" }
            end
          end
        end
      end
    end
  end
  -- Hosted ramps, stairs and decks take the host's ceiling (when it has head room over them) and sky by default.
  for _, g in ipairs(R) do
    if #g.hosts > 0 then
      local top, sky = nil, false
      for _, h in ipairs(g.hosts) do
        if h.top >= g.hi + 2 - 1e-6 then top = math.max(top or -math.huge, h.top) end
        sky = sky or h.sky
      end
      if not g.top_given and top ~= nil then g.top = top end
      if not g.sky_given and sky then g.sky = true end
    end
  end
  -- A ramp or stairs of any outline standing on its own (no host) under a roof: its ceiling runs parallel to its floor
  -- at its height over it (default 4 m; height = h sets it, an absolute ceiling keeps it flat), so a flared corridor
  -- that climbs is a sloped tube with its traced walls, not a tall room with a wedge of fill in it.
  for _, g in ipairs(R) do
    if sloped(g) and not g.surface and #g.hosts == 0 and not g.sky and not g.ceiling_given then
      local head = g.top - g.hi
      g.ceil_at = function(q) return g.floor_at(q) + head end
    end
  end

  -- 5. Openings (a path's name picks its segment nearest to at).
  local openings = {}
  local function region_for(nm, at)
    if by[nm] then return by[nm] end
    if paths[nm] then
      local best, bd = nil, math.huge
      for _, sn in ipairs(paths[nm]) do local d = nearest_edge(by[sn].poly, at) if d < bd then best, bd = by[sn], d end end
      return best
    end
    return nil
  end
  for i, o in ipairs(t.openings or {}) do
    local key = "openings[" .. i .. "]"
    check_keys(api .. "." .. key, o, { "a", "b", "at", "width", "height" })
    local at = point(api, key .. ".at", o.at, units)
    local a, b = region_for(o.a, at), region_for(o.b, at)
    if a == nil or b == nil then fail(api, key .. ": unknown region '" .. tostring(a == nil and o.a or o.b) .. "'.") end
    local width = o.width ~= nil and positive(api, key .. ".width", o.width) or 3
    local da, pa, _, _, na = nearest_edge(a.poly, at)
    local db, pb = nearest_edge(b.poly, at)
    if da > 4 or db > 4 then
      fail(api, key .. " (" .. a.name .. " to " .. b.name .. ") at " .. P(at) .. " is not on a wall between them: '" .. a.name .. "' is " .. fmt(da) .. " m away and '" .. b.name .. "' " .. fmt(db) .. " m (at most 4 m each).",
        "Put at on the wall gap between the two regions (read it off ref.grid), or check the region names.")
    end
    local gap = dist(pa, pb)
    local fa, fb = a.floor_at(pa), b.floor_at(pb)
    if math.abs(fa - fb) > kit.limits.max_riser + 1e-6 then
      fail(api, key .. " (" .. a.name .. " to " .. b.name .. "): the floors differ by " .. fmt(math.abs(fa - fb)) .. " m there (an opening joins floors within " .. fmt(kit.limits.max_riser) .. " m).",
        "Trace a ramp or stairs region between them and open onto it.")
    end
    local rec = { a = a.name, b = b.name, at = at, width = width, gap = gap, pa = pa, pb = pb }
    if gap < 0.05 then
      rec.note = "the regions already meet here (open along their shared wall line)"
      -- for a door frame: the point on the shared line, its floor and the direction from a into b
      local y0 = math.max(fa, fb)
      rec.pos, rec.dir, rec.floor = { pa[1], y0, pa[2] }, { -na[1], -na[2] }, y0
      rec.height = o.height ~= nil and positive(api, key .. ".height", o.height) or math.min(3, math.min(a.top, b.top) - y0)
    else
      local n = { (pb[1] - pa[1]) / gap, (pb[2] - pa[2]) / gap }
      if n[1] * na[1] + n[2] * na[2] > 0.2 then
        fail(api, key .. ": '" .. b.name .. "' is not across '" .. a.name .. "''s wall at " .. P(at) .. " (they overlap there).", "Check the region outlines with ref.compare or render_plan.")
      end
      local u = { -n[2], n[1] }
      local y0 = math.max(fa, fb)
      local top = math.min(a.top, b.top)
      local h = o.height ~= nil and positive(api, key .. ".height", o.height) or math.min(3, top - y0)
      if y0 + h > top + 1e-6 then warn(key .. ": height " .. fmt(h) .. " m reaches above the lower ceiling (" .. fmt(top) .. ").") end
      local e = 0.25
      local hw = width / 2
      rec.quad = ccw({
        { pa[1] - n[1] * e - u[1] * hw, pa[2] - n[2] * e - u[2] * hw },
        { pa[1] - n[1] * e + u[1] * hw, pa[2] - n[2] * e + u[2] * hw },
        { pb[1] + n[1] * e + u[1] * hw, pb[2] + n[2] * e + u[2] * hw },
        { pb[1] + n[1] * e - u[1] * hw, pb[2] + n[2] * e - u[2] * hw },
      })
      rec.y0, rec.y1 = y0, y0 + h
      rec.pos, rec.dir, rec.floor, rec.height = { (pa[1] + pb[1]) / 2, y0, (pa[2] + pb[2]) / 2 }, n, y0, h
      if gap > 2.5 then warn(key .. ": a " .. fmt(gap) .. " m deep passage between '" .. a.name .. "' and '" .. b.name .. "' (wider than a wall); trace it as a corridor region if it is one.") end
    end
    openings[#openings + 1] = rec
    -- every doorway is also a connection (kind = "opening"), so later steps (door frames, hand-off) find it there
    if gap >= 0.05 then
      connections[#connections + 1] = { kind = "opening", opening = #openings, a = a.name, b = b.name, at = { rec.pos[1], rec.pos[3] },
        pos = rec.pos, dir = rec.dir, width = width, height = rec.height, length = width, drop = math.abs(fa - fb), open_height = rec.height, ledge = false }
    else
      rec.connection = true
    end
  end

  -- 6. Solids.
  local solids = {}
  for i, s in ipairs(t.solids or {}) do
    local key = "solids[" .. i .. "]"
    check_keys(api .. "." .. key, s, { "poly", "circle", "region", "base", "top", "height", "detail", "texture", "textures" })
    local poly
    if s.circle ~= nil then
      if s.poly ~= nil then fail(api, key .. ": give poly or circle, not both.") end
      local c = s.circle
      if type(c) ~= "table" or type(c[1]) ~= "number" or type(c[2]) ~= "number" or type(c[3]) ~= "number" or c[3] <= 0 then fail(api, key .. ".circle must be {x, z, r}.") end
      local ctr, r = point(api, key .. ".circle", { c[1], c[2] }, units), metres(c[3])
      local n = r >= 1.5 and 12 or 8
      poly = {}
      for k = 0, n - 1 do local a = 2 * math.pi * (k + 0.5) / n poly[#poly + 1] = { q16(ctr[1] + r * math.cos(a)), q16(ctr[2] + r * math.sin(a)) } end
    else
      poly = points(api, key .. ".poly", s.poly, units)
    end
    poly = ccw(poly)
    local cx, cz = 0, 0
    for _, q in ipairs(poly) do cx, cz = cx + q[1], cz + q[2] end
    local ctr = { cx / #poly, cz / #poly }
    local home = s.region ~= nil and by[s.region] or nil
    if s.region ~= nil and home == nil then fail(api, key .. ": unknown region '" .. tostring(s.region) .. "'.") end
    if home == nil then
      -- a ramp or stairs inside a room carries the floor there; otherwise the first region holding the point
      for _, g in ipairs(R) do if sloped(g) and inside(g.poly, ctr[1], ctr[2]) then home = g break end end
    end
    if home == nil then
      for _, g in ipairs(R) do if g.kind ~= "deck" and inside(g.poly, ctr[1], ctr[2]) then home = g break end end
    end
    if home == nil and s.base == nil then warn(key .. " at " .. P(ctr) .. " stands in no region (inside rock); give region or base.") end
    local base = s.base ~= nil and number(api, key .. ".base", s.base) or (home and home.floor_at(ctr) or 0)
    local top
    if s.top == "ceiling" then
      if home == nil then fail(api, key .. ": top = \"ceiling\" needs the region it stands in.") end
      top = home.top
    elseif s.top ~= nil then
      if s.height ~= nil then fail(api, key .. ": give top or height, not both.") end
      top = number(api, key .. ".top", s.top)
    else
      top = base + (s.height ~= nil and positive(api, key .. ".height", s.height) or 1.5)
    end
    if top <= base then fail(api, key .. ": top " .. fmt(top) .. " is not above base " .. fmt(base) .. ".") end
    local pieces = convex_pieces(poly)
    if pieces == nil then fail(api, key .. ": the outline crosses itself.") end
    local tex, texs = base_tex, base_texs
    if s.texture ~= nil or s.textures ~= nil then tex, texs = textures_of(api, s) end
    solids[#solids + 1] = { pieces = pieces, base = base, top = top, detail = boolean(api, key .. ".detail", s.detail, true), texture = tex, textures = texs, region = home and home.name, at = ctr }
  end

  -- The record (also the report of a validate-only call).
  local rec = { kind = "trace", name = name, regions = {}, openings = openings, connections = connections, stacked = stacked, warnings = warnings,
    area = total, welded = welded, snapped = snapped, inserted = inserted, paths = paths, brushes = {}, built = building }
  local function hosted_pair(g, h)
    for _, x in ipairs(g.hosts) do if x == h then return true end end
    for _, x in ipairs(h.hosts) do if x == g then return true end end
    return false
  end
  -- Dressing data: each region's walls (solid spans of its outline) and open edges (spans open to a neighbour), and
  -- the plan's railing edges (deck edges over a drop, ledges between connected regions).
  local edges = {}
  for _, g in ipairs(R) do
    local hosts = {}
    for _, h in ipairs(g.hosts) do hosts[#hosts + 1] = h.name end
    local walls, open_edges = {}, {}
    if #g.hosts == 0 then
      for e = 1, #g.poly do
        local a0, a1 = g.poly[e], g.poly[e % #g.poly + 1]
        local open, L = {}, dist(a0, a1)
        for _, h in ipairs(R) do
          if h ~= g and not hosted_pair(g, h) then
            for _, s in ipairs((spans_on(a0, a1, h.poly, tol))) do
              local m = along(a0, a1, L, (s[1] + s[2]) / 2)
              local fg, fh = g.floor_at(m), h.floor_at(m)
              if math.min(g.top, h.top) - math.max(fg, fh) > 0 then
                open[#open + 1] = s
                open_edges[#open_edges + 1] = { a = along(a0, a1, L, s[1]), b = along(a0, a1, L, s[2]), to = h.name, drop = fg - fh }
              end
            end
          end
        end
        for _, s in ipairs(minus(L, open)) do
          local m = along(a0, a1, L, (s[1] + s[2]) / 2)
          local w = { a = along(a0, a1, L, s[1]), b = along(a0, a1, L, s[2]), length = s[2] - s[1], floor = g.floor_at(m), ceiling = g.top }
          local last = walls[#walls]
          -- a wall split only by an inserted T-vertex (same line, same floor) stays one wall
          if last and dist(last.b, w.a) < 1e-6 and math.abs(last.floor - w.floor) < 1e-6
            and math.abs(cross2(last.a, last.b, w.b)) < 1e-4 * math.max(1, last.length * (last.length + w.length)) then
            last.b, last.length = w.b, last.length + w.length
          else
            walls[#walls + 1] = w
          end
        end
      end
      local first, last = walls[1], walls[#walls]
      if #walls > 1 and dist(last.b, first.a) < 1e-6 and math.abs(last.floor - first.floor) < 1e-6
        and math.abs(cross2(last.a, last.b, first.b)) < 1e-4 * math.max(1, last.length * (last.length + first.length)) then
        first.a, first.length = last.a, first.length + last.length
        walls[#walls] = nil
      end
    end
    if g.kind == "deck" then
      -- a deck's edge needs a railing unless it runs on into a floor at its height or stands against its host's wall
      for e = 1, #g.poly do
        local a0, a1 = g.poly[e], g.poly[e % #g.poly + 1]
        local cut, L = {}, dist(a0, a1)
        for _, h in ipairs(R) do
          if h ~= g then
            local host = false
            for _, x in ipairs(g.hosts) do if x == h then host = true end end
            for _, s in ipairs((spans_on(a0, a1, h.poly, tol))) do
              local m = along(a0, a1, L, (s[1] + s[2]) / 2)
              if host or math.abs(h.floor_at(m) - g.lo) <= kit.limits.max_riser + 1e-6 then cut[#cut + 1] = s end
            end
          end
        end
        for _, s in ipairs(minus(L, cut)) do
          local m = along(a0, a1, L, (s[1] + s[2]) / 2)
          local below = nil
          for _, h in ipairs(g.hosts) do if inside(h.poly, m[1], m[2]) or nearest_edge(h.poly, m) < tol + 0.05 then below = h break end end
          edges[#edges + 1] = { kind = "deck", region = g.name, a = along(a0, a1, L, s[1]), b = along(a0, a1, L, s[2]), length = s[2] - s[1], y = g.lo,
            below = below and below.name or nil, drop = below and (g.lo - below.floor_at(m)) or nil }
        end
      end
    end
    rec.regions[g.name] = { kind = g.kind, area = g.area, floor = g.lo, floor_high = g.hi, ceiling = g.top, sky = g.sky, points = g.poly, outline = g.poly,
      pieces = #g.pieces, steps = g.steps, hosts = hosts, path = g.path, storey = g.storey, material = g.material, texture = g.texture, textures = g.textures,
      zone = g.path or g.name, walls = walls, open_edges = open_edges, brushes = {}, air = {}, fills = {} }
  end
  for _, c in ipairs(connections) do
    if c.ledge then
      local a, b = by[c.a], by[c.b]
      local hi_r, lo_r = a, b
      if b.floor_at(c.at) > a.floor_at(c.at) then hi_r, lo_r = b, a end
      for e = 1, #hi_r.poly do
        local a0, a1 = hi_r.poly[e], hi_r.poly[e % #hi_r.poly + 1]
        local spans, L = spans_on(a0, a1, lo_r.poly, tol)
        for _, s in ipairs(spans) do
          local m = along(a0, a1, L, (s[1] + s[2]) / 2)
          edges[#edges + 1] = { kind = "ledge", region = hi_r.name, below = lo_r.name, a = along(a0, a1, L, s[1]), b = along(a0, a1, L, s[2]),
            length = s[2] - s[1], y = hi_r.floor_at(m), drop = hi_r.floor_at(m) - lo_r.floor_at(m) }
        end
      end
    end
  end
  rec.edges = edges

  -- Kit zones: every region (a path as one zone) is a kit room record (name, kind, min/max box, floor_y, its air
  -- brushes) that kit.light_room and kit.trim take, and its brushes carry kit.zone = its name, so kit.dress,
  -- kit.items and kit.detail take that name like a kit.build_layout zone's.
  local zones, zone_by = {}, {}
  for _, g in ipairs(R) do
    local zn = g.path or g.name
    local z = zone_by[zn]
    if z == nil then
      z = { name = zn, kind = g.path and "corridor" or g.kind, region_kind = g.path and "path" or g.kind, trace = name, storey = g.storey,
        regions = {}, brushes = {}, fills = {}, area = 0, sky = false, material = g.material, texture = g.texture, textures = g.textures,
        x0 = math.huge, z0 = math.huge, x1 = -math.huge, z1 = -math.huge, floor = math.huge, floor_high = -math.huge, ceiling = -math.huge }
      if not g.path then z.outline = g.poly else z.outlines = {} end
      zone_by[zn] = z
      zones[#zones + 1] = z
    end
    z.regions[#z.regions + 1] = g.name
    if z.outlines then z.outlines[#z.outlines + 1] = g.poly end
    z.area = z.area + g.area
    z.sky = z.sky or g.sky
    z.x0, z.z0, z.x1, z.z1 = math.min(z.x0, g.bx0), math.min(z.z0, g.bz0), math.max(z.x1, g.bx1), math.max(z.z1, g.bz1)
    z.floor, z.floor_high, z.ceiling = math.min(z.floor, g.lo), math.max(z.floor_high, g.hi), math.max(z.ceiling, g.top)
  end
  for _, z in ipairs(zones) do
    z.min, z.max, z.floor_y = vec(z.x0, z.floor, z.z0), vec(z.x1, z.ceiling, z.z1), z.floor
    z.x0, z.z0, z.x1, z.z1 = nil, nil, nil, nil
  end
  rec.zones, rec.zone = zones, zone_by
  -- Routes to keep clear of rock dressing and mounds (e.g. a cart route): polylines in the plan's units.
  local keep_clear = {}
  if t.keep_clear ~= nil then
    if type(t.keep_clear) ~= "table" then fail(api, "'keep_clear' must list polylines {{x, z}, ...}.") end
    for i, line in ipairs(t.keep_clear) do
      if type(line) ~= "table" or #line < 1 then fail(api, "keep_clear[" .. i .. "] must be a polyline {{x, z}, ...}.") end
      local pl = {}
      for k, q in ipairs(line) do pl[k] = point(api, "keep_clear[" .. i .. "][" .. k .. "]", q, units) end
      keep_clear[#keep_clear + 1] = pl
    end
  end
  local trace_seed = t.seed ~= nil and number(api, "seed", t.seed) or 1
  local RK = I.rock
  local organic_regions = {}
  for _, g in ipairs(R) do if g.org then organic_regions[#organic_regions + 1] = g end end
  local function route_dist(p)
    local best = math.huge
    for _, pl in ipairs(keep_clear) do
      if #pl == 1 then best = math.min(best, dist(p, pl[1])) end
      for k = 1, #pl - 1 do best = math.min(best, (seg_near(p, pl[k], pl[k + 1]))) end
    end
    return best
  end

  -- The rock plan of one region: which wall spans become rock, with what profile (no brushes yet).
  local function rock_plan(g)
    local o, rr = g.org, rec.regions[g.name]
    local treat
    if g.kind == "canyon" then treat = "canyon"
    elseif g.kind == "cave" or o.organic then treat = "cave"
    elseif o.walls == "rock" or o.rock_edges then treat = "wall" end
    if treat == nil then return nil end
    local H = g.top - g.lo
    local plan = { treat = treat, spans = {}, st = RK.stream(trace_seed * 7919 + g.index * 104729 + (o.seed or 0) * 31), H = H }
    local my_openings = {}
    for _, op in ipairs(openings) do
      if (op.a == g.name or op.b == g.name) and op.pos then my_openings[#my_openings + 1] = op end
    end
    plan.openings = my_openings
    for _, w in ipairs(rr.walls) do
      local a, b = w.a, w.b
      local L = dist(a, b)
      local use = L > 0.5
      if use and treat == "wall" and o.rock_edges then
        use = false
        local m = along(a, b, L, L / 2)
        for _, sg in ipairs(o.segs) do if (seg_near(m, sg[1], sg[2])) <= tol + 0.1 then use = true break end end
      end
      if use then
        local u = { (b[1] - a[1]) / L, (b[2] - a[2]) / L }
        local n = { -u[2], u[1] } -- the outline runs counter-clockwise: the space is on the left
        local cut = {}
        for _, op in ipairs(my_openings) do
          local px, pz = op.pos[1] - a[1], op.pos[3] - a[2]
          local s, d = px * u[1] + pz * u[2], math.abs(px * n[1] + pz * n[2])
          local hw = (op.width or 3) / 2 + 0.6
          if d < 1.6 and s > -hw and s < L + hw then cut[#cut + 1] = { math.max(0, s - hw), math.min(L, s + hw) } end
        end
        for _, sp in ipairs(minus(L, cut)) do
          if sp[2] - sp[1] > 0.6 then
            local pa, pb = along(a, b, L, sp[1]), along(a, b, L, sp[2])
            plan.spans[#plan.spans + 1] = { a = pa, b = pb, n = n, u = u, L = sp[2] - sp[1],
              y0 = math.min(g.floor_at(pa), g.floor_at(pb)) }
          end
        end
      end
    end
    -- Canyon: the upper walls step back (air carved behind the traced line) where no other space is near.
    if treat == "canyon" then
      local d = o.lean or (H >= 8 and 1.5 or 0)
      local ys = g.lo + math.max(3.5, 0.3 * H)
      for _, op in ipairs(my_openings) do ys = math.max(ys, op.floor + (op.height or 3) + 1) end
      if d > 0 and g.top - ys >= 3 then
        for _, sp in ipairs(plan.spans) do
          if sp.L >= 2 then
            local m = 0.75
            local function at(s, dd) return { sp.a[1] + sp.u[1] * s - sp.n[1] * dd, sp.a[2] + sp.u[2] * s - sp.n[2] * dd } end
            local probe = ccw({ at(-m, -0.05), at(sp.L + m, -0.05), at(sp.L + m, d + m), at(-m, d + m) })
            local clear = true
            for _, h in ipairs(R) do
              if h ~= g and h.lo - 0.5 < g.top and h.top + 0.5 > ys and h.bx0 <= math.max(probe[1][1], probe[2][1], probe[3][1], probe[4][1]) + 1 then
                for _, pc in ipairs(h.pieces) do
                  local c = clip(pc, probe)
                  if #c >= 3 and area2(c) > 0.01 then clear = false break end
                end
              end
              if not clear then break end
            end
            if clear then sp.setback = { y = ys, d = d, quad = ccw({ at(0, -0.02), at(sp.L, -0.02), at(sp.L, d), at(0, d) }) } end
          end
        end
      end
    end
    return plan
  end

  for _, w in ipairs(warnings) do log.warn(api .. ": " .. w) end
  local plans = {}
  local all = rec.brushes
  local function keep(list, b) list[#list + 1] = b all[#all + 1] = b return b end
  local any_sky = false

  -- The air of one region (its convex pieces, floor to ceiling, open to the sky when it is).
  local function region_air(g, sink)
    local out = rec.regions[g.name].brushes
    local air = {}
    local top = g.top
    if g.ceil_at then top = function(x, z) return g.ceil_at({ x, z }) end end
    for k, piece in ipairs(g.pieces) do
      air[#air + 1] = keep(out, prism("trace", name, g.name .. ".air" .. k, piece, g.lo, top, { air = true, texture = g.texture, textures = g.textures }))
      if sink then sink[#sink + 1] = air[#air] end
    end
    if g.sky then sky_up(air) any_sky = true end
  end
  -- Canyon walls that lean back: air behind the traced wall line above the set-back height (before any solid).
  local function lean_air(g, sink)
    local pl = plans[g.name]
    if not pl then return end
    local out = rec.regions[g.name].brushes
    local lean = {}
    for k, sp in ipairs(pl.spans) do
      if sp.setback then
        lean[#lean + 1] = keep(out, prism("trace", name, g.name .. ".lean" .. k, sp.setback.quad, sp.setback.y, g.top, { air = true, texture = g.texture, textures = g.textures }))
        if sink then sink[#sink + 1] = lean[#lean] end
      end
    end
    if g.sky and #lean > 0 then sky_up(lean) end
    pl.lean_count = #lean
  end
  local function opening_air(i, o)
    local b = prism("trace", name, "opening" .. i, o.quad, o.y0, o.y1, { air = true, texture = base_tex, textures = base_texs })
    all[#all + 1] = b
    o.brush = b
    return b
  end
  -- The literal fill of a sloped or raised region: a sloped path segment's two planes, a ramp's plane, a stair's
  -- steps (bands across climb), a deck's slab.
  local function region_fill(g, sink)
    local out = rec.regions[g.name].brushes
    local o = { texture = g.texture, textures = g.textures }
    local function put(b) keep(out, b) if sink then sink[#sink + 1] = b end end
    if g.surface then
      local S = g.surface
      for k, piece in ipairs(g.pieces) do
        for side, half in ipairs({ left_of(piece, S.a, S.b), left_of(piece, S.b, S.a) }) do
          if half then
            local pl = side == 1 and S.left or S.right
            put(prism("trace", name, g.name .. ".ramp" .. k .. "_" .. side, half, g.lo - 1, function(x, z) return pl[1] * x + pl[2] * z + pl[3] end, o))
          end
        end
      end
    elseif g.kind == "ramp" then
      for k, piece in ipairs(g.pieces) do
        put(prism("trace", name, g.name .. ".ramp" .. k, piece, g.lo - 1, function(x, z) return g.floor_at({ x, z }) end, o))
      end
    elseif g.kind == "stairs" then
      local r = (g.hi - g.lo) / g.steps
      for j = 0, g.steps - 1 do
        for k, piece in ipairs(g.pieces) do
          local strip = band(piece, g.climb, g.s0 + j * g.tread, g.s0 + (j + 1) * g.tread)
          if strip then put(prism("trace", name, g.name .. ".step" .. (j + 1) .. "_" .. k, strip, g.lo - 1, g.lo + (j + 1) * r, o)) end
        end
      end
    elseif g.kind == "deck" then
      for k, piece in ipairs(g.pieces) do
        put(prism("trace", name, g.name .. ".deck" .. k, piece, g.lo - g.thickness, g.lo, { detail = true, texture = g.texture, textures = g.textures }))
      end
    end
  end
  local function solid_fill(i, s, sink)
    s.brushes = {}
    for k, piece in ipairs(s.pieces) do
      local b = prism("trace", name, "solid" .. i .. "_" .. k, piece, s.base, s.top, { detail = s.detail, texture = s.texture, textures = s.textures })
      s.brushes[#s.brushes + 1] = b
      all[#all + 1] = b
      if sink then sink[#sink + 1] = b end
    end
  end

  -- The layout graph of the plan (via = "layout"): every region that stands on its own is a literal poly zone (its
  -- welded outline, floor, ceiling or sky, textures); ramps, stairs and decks inside another region belong to their
  -- host's zone; shared walls become literal joins and doorways literal links at their traced places. The zones'
  -- hooks build the trace's own geometry (shell: air; inner: decks, sloped fills, blocks; late: ramps and stairs
  -- through kit.ramp / kit.stairs), so the layout builder adds its intent checks, detail pass and lights to it.
  local notes = {}
  local function note(s) for _, e in ipairs(notes) do if e == s then return end end notes[#notes + 1] = s end
  local function root(g) return (#g.hosts > 0) and g.hosts[1] or g end
  local function theme_of(g)
    -- Fully rocky regions are caves to the layout builder, as organic zones are in generated layouts: the detail pass
    -- then dresses them with rock formations, not wall trims and pilasters.
    if ROCKY[g.kind or ""] or (g.org and (g.org.organic or g.org.walls == "rock")) then return "cave" end
    local m = g.material
    if m == nil then return "industrial" end
    if MATERIAL_EXTRA[m] then return MATERIAL_EXTRA[m].theme end
    return m
  end
  -- A ramp's or stair's region as a rectangle along its climb: {s0, s1, a0, a1} (along climb, across), else nil.
  local function climb_rect(g)
    local c = g.climb
    local u = { -c[2], c[1] }
    local s0, s1, a0, a1 = math.huge, -math.huge, math.huge, -math.huge
    for _, q in ipairs(g.poly) do
      local s, a = q[1] * c[1] + q[2] * c[2], q[1] * u[1] + q[2] * u[2]
      s0, s1, a0, a1 = math.min(s0, s), math.max(s1, s), math.min(a0, a), math.max(a1, a)
    end
    for _, q in ipairs(g.poly) do
      local s, a = q[1] * c[1] + q[2] * c[2], q[1] * u[1] + q[2] * u[2]
      if math.min(math.abs(s - s0), math.abs(s - s1), math.abs(a - a0), math.abs(a - a1)) > 0.02 then return nil end
    end
    if math.abs(g.area - (s1 - s0) * (a1 - a0)) > 0.02 * g.area then return nil end
    return { s0 = s0, s1 = s1, a0 = a0, a1 = a1, u = u }
  end
  -- A traced ramp or stairs through the kit's own builder (kit.ramp; kit.stairs as the layout's flights, with their
  -- stringers and handrails), purpose = "require": a flight that serves no route is reported and still built (the
  -- plan is the design). nil when the region is no rectangle along its climb or the builder refuses its size.
  local function kit_flight(R, L, g, sink)
    local f = climb_rect(g)
    if not f then return nil end
    local c = g.climb
    local am = (f.a0 + f.a1) / 2
    local foot = { c[1] * f.s0 + f.u[1] * am, c[2] * f.s0 + f.u[2] * am }
    local width, run, rise = f.a1 - f.a0, f.s1 - f.s0, g.hi - g.lo
    local dir = nil
    if math.abs(c[1]) > 1 - 1e-9 then dir = c[1] > 0 and "+x" or "-x" elseif math.abs(c[2]) > 1 - 1e-9 then dir = c[2] > 0 and "+z" or "-z" end
    local turned = dir == nil
    local tex = g.texture
    local texs = (tex == nil) and (g.textures or kit.defaults.textures) or nil
    local kname = name .. "_" .. g.name:gsub("%.", "_")
    local function make(purpose)
      local opts = { name = kname, pos = { foot[1], g.lo, foot[2] }, dir = dir or "+z", rise = rise, width = width, group = false,
        texture = tex, textures = texs, purpose = purpose, detail = turned or nil }
      if g.kind == "stairs" then
        opts.riser, opts.tread = rise / g.steps, run / g.steps
        return L.flight(opts)
      end
      opts.run = run
      return kit.ramp(opts)
    end
    local ok, res = ged.try(make, "require")
    if not ok and tostring(res):find("serves no route", 1, true) then
      local why = tostring(res):match("serves no route: ([^\n]*)") or tostring(res)
      warnings[#warnings + 1] = "'" .. g.name .. "' (traced " .. g.kind .. ") serves no route: " .. why
      log.warn(api .. ": '" .. g.name .. "' (traced " .. g.kind .. ") serves no route: " .. why)
      ok, res = ged.try(make, "quiet")
    end
    if not ok then
      note("'" .. g.name .. "': the kit's " .. (g.kind == "stairs" and "stairs" or "ramp") .. " refused its traced size (" .. tostring(res):sub(1, 120) .. "); built from the trace's own fill")
      return nil
    end
    if turned then
      -- built along +z from the foot, then turned about the foot to the traced climb
      local yaw = math.deg(math.atan2(c[1], c[2]))
      for _, b in ipairs(res.brushes) do b:rotate_about("y", L.ROT_SIGN * yaw, foot[1], g.lo, foot[2]) end
      note("'" .. g.name .. "': its " .. g.kind .. " climbs off the world axes, so the kit's steps are detail brushes turned onto the climb (no stringers)")
    elseif g.kind == "stairs" then
      R.flights[#R.flights + 1] = res
    end
    local out = rec.regions[g.name].brushes
    for _, b in ipairs(res.brushes) do keep(out, b) if sink then sink[#sink + 1] = b end end
    return res
  end

  local G = nil
  local function make_graph()
    local zones, links, payload = {}, {}, {}
    for _, g in ipairs(R) do
      if #g.hosts == 0 then
        local L = { tag = g.path or g.name, region = g, hosted = {}, solids = {}, openings = {}, rails = {}, texture = g.texture,
          rocks = (g.org and g.org.rock_detail) or nil,
          textures = (g.texture == nil) and (g.textures or kit.defaults.textures) or nil }
        payload[g.name] = L
        local outline = {}
        for i, q in ipairs(g.poly) do outline[i] = { q[1], q[2] } end
        zones[#zones + 1] = { id = g.name, archetype = "poly", outline = outline, pos = { (g.bx0 + g.bx1) / 2, g.lo, (g.bz0 + g.bz1) / 2 },
          size = { g.bx1 - g.bx0, g.bz1 - g.bz0 }, height = g.top - g.lo, sky = g.sky or nil,
          levels = { 0 }, chamfer = 0, palette = { theme = theme_of(g) }, literal = L }
      end
    end
    for _, g in ipairs(R) do
      if #g.hosts > 0 then local L = payload[root(g).name] if L then L.hosted[#L.hosted + 1] = g end end
    end
    for i, s in ipairs(solids) do
      local L = s.region and payload[root(by[s.region]).name] or payload[zones[1].id]
      L.solids[#L.solids + 1] = i
    end
    for _, e in ipairs(edges) do
      local L = payload[root(by[e.region]).name]
      if L then L.rails[#L.rails + 1] = { e.a, e.b, e.y } end
    end
    -- shared walls: one literal join per pair of zones, its band the widest the trace found between them
    local seen = {}
    for _, c in ipairs(connections) do
      if c.kind == "shared" then
        local A, B = root(by[c.a]), root(by[c.b])
        if A ~= B then
          local key = (A.name < B.name) and (A.name .. "\0" .. B.name) or (B.name .. "\0" .. A.name)
          local y0 = math.max(by[c.a].floor_at(c.at), by[c.b].floor_at(c.at))
          local y1 = math.min(by[c.a].top, by[c.b].top)
          local l = seen[key]
          if l then l.literal.y0, l.literal.y1 = math.min(l.literal.y0, y0), math.max(l.literal.y1, y1)
          else
            l = { id = "join" .. (#links + 1), type = "join", a = A.name, b = B.name, literal = { y0 = y0, y1 = y1 } }
            seen[key] = l
            links[#links + 1] = l
          end
        end
      end
    end
    -- doorways through a wall gap: literal links where they were traced
    for i, o in ipairs(openings) do
      if o.quad then
        local A, B = root(by[o.a]), root(by[o.b])
        if A ~= B then
          links[#links + 1] = { id = "door" .. i, type = "doorway", a = A.name, b = B.name, width = o.width, height = o.y1 - o.y0,
            literal = { quad = o.quad, y0 = o.y0, y1 = o.y1, pa = o.pa, pb = o.pb, air = function() opening_air(i, o) end } }
        else
          local L = payload[A.name]
          L.openings[#L.openings + 1] = i
        end
      end
    end
    -- the zones' hooks
    for _, z in ipairs(zones) do
      local L = z.literal
      local g = L.region
      local members = { g }
      for _, h in ipairs(L.hosted) do members[#members + 1] = h end
      -- (assigned, not 'function L.shell': MoonSharp mis-closes method definitions over loop locals)
      L.shell = function(Rz)
        for _, m in ipairs(members) do region_air(m, Rz.brushes) lean_air(m, Rz.brushes) end
        for _, i in ipairs(L.openings) do Rz.brushes[#Rz.brushes + 1] = opening_air(i, openings[i]) end
        Rz.room = { pieces = g.pieces, outline = g.poly, min = vec(g.bx0, g.lo, g.bz0), max = vec(g.bx1, g.top, g.bz1), floor_y = g.lo }
        for _, e in ipairs(L.rails) do Rz.rails[#Rz.rails + 1] = e end
      end
      L.inner = function(Rz)
        for _, m in ipairs(members) do
          if m.surface or m.kind == "deck" then region_fill(m, Rz.brushes) end
        end
        for _, i in ipairs(L.solids) do solid_fill(i, solids[i], Rz.brushes) end
      end
      L.late = function(Rz, LY)
        for _, m in ipairs(members) do
          if not m.surface and (m.kind == "ramp" or m.kind == "stairs") then
            if not kit_flight(Rz, LY, m, Rz.brushes) then
              region_fill(m, Rz.brushes)
              if climb_rect(m) == nil then note("'" .. m.name .. "': a " .. m.kind .. " that is no rectangle along its climb keeps the trace's own fill (tagged as a route)") end
            end
          end
        end
      end
    end
    for _, g in ipairs(R) do
      if g.surface then note("sloped path segments ('" .. (g.path or g.name) .. "') keep the trace's two-plane fill so neighbouring segments meet exactly") break end
    end
    return { literal = true, gametype = "dm", class = "indoor", seed = trace_seed, zones = zones, links = links, objectives = {} }
  end

  if via == "layout" then
    G = make_graph()
    -- the record's graph: plain data (no hooks, no region internals), so it can be read, saved or checked again
    local pz, pl = {}, {}
    for i, z in ipairs(G.zones) do
      pz[i] = { id = z.id, archetype = z.archetype, outline = z.outline, pos = z.pos, size = z.size, height = z.height, sky = z.sky, levels = z.levels,
        chamfer = z.chamfer, palette = z.palette, literal = { tag = z.literal.tag } }
    end
    for i, l in ipairs(G.links) do
      local L = l.literal
      pl[i] = { id = l.id, type = l.type, a = l.a, b = l.b, width = l.width, height = l.height,
        literal = { y0 = L.y0, y1 = L.y1, quad = L.quad, pa = L.pa, pb = L.pb } }
    end
    rec.graph = { literal = true, gametype = G.gametype, class = G.class, seed = G.seed, zones = pz, links = pl, objectives = {} }
    local V = kit.check_layout{ graph = G }
    -- errors refuse the build; the flow readings (dead ends, separate parts, a zone that is the only way on) are the
    -- plan's walk graph and worth a look; the other design rules (floor levels, loops, variety, symmetry) measure a
    -- generated layout and are listed only
    local errs, rules, flow = {}, {}, {}
    local FLOW = { degree = true, connected = true, articulation = true }
    for _, v in ipairs(V) do
      if v.severity == "error" then errs[#errs + 1] = v.message
      else
        rules[#rules + 1] = v.message
        if FLOW[v.rule] then flow[#flow + 1] = v.message end
      end
    end
    rec.layout_check = { errors = errs, rules = rules, flow = flow, violations = V, ok = #errs == 0 }
    for _, m in ipairs(flow) do log.info(api .. ": flow: " .. m) end
  end
  if not building then return rec end
  for _, g in ipairs(organic_regions) do plans[g.name] = rock_plan(g) end

  if via == "prism" then
    -- 7. Build: region air, then opening air, then solids (ramp, stair and path fills, deck slabs, blocks), so a ramp
    --    or deck inside a room replaces the room's floor under it.
    for _, g in ipairs(R) do region_air(g) end
    for i, o in ipairs(openings) do if o.quad then opening_air(i, o) end end
    for _, g in ipairs(organic_regions) do lean_air(g) end
    for _, g in ipairs(R) do region_fill(g) end
    for i, s in ipairs(solids) do solid_fill(i, s) end
  else
    -- 7. Build through kit.build_layout: the zone shells (the regions' air), the doorways' air, the interiors (deck
    --    slabs, sloped fills, blocks), the ramps and stairs (kit.ramp / kit.stairs), then the room detail pass (trims,
    --    door frames, beams, railings at the drops, stair finishes) and the lights.
    if #rec.layout_check.errors > 0 then
      fail(api, "the traced plan's layout check found " .. #rec.layout_check.errors .. " error(s): " .. table.concat(rec.layout_check.errors, " "),
        "Pass via = \"prism\" to build the plan without the layout builder, or fix the regions it names.")
    end
    local report = kit.build_layout{ graph = G, name = name, lights = want_lights, detail = t.detail }
    rec.layout = report
    rec.lights = report.lights
    rec.detail = report.detail
    for _, w in ipairs(report.warnings or {}) do warnings[#warnings + 1] = w end
    note("regions touching along a shared wall join as literal joins: no air strip across the line (the welded outlines meet exactly) and floors may differ (a ramp's foot, a deck's storey)")
    note("the layout's design rules (dead ends, loops, floor levels) are reported in layout_check.rules, never applied: the traced plan is the design")
    rec.literal_notes = notes
  end
  rec.solids = solids

  -- Organic rock (detail, after every air and solid of the plan): wall skins, cave vaults and pillars, ground
  -- mounds and resting boulders. The traced air still seals; the rock only covers its flat walls and floor.
  local rock_name = name .. "_rock"
  rec.rock_name = rock_name
  -- The organic budget (kit_rock.lua): the skins keep full facets in the view band and coarser cells above it; when
  -- the whole trace's rock would not fit its share of the level's budget, its far rock (and the vaults and pillars)
  -- coarsen together. Seen facets are estimated from the cells (about 7 seen per chunk).
  local rock_view = kit.defaults.organic_view or 5
  local rock_far = kit.defaults.organic_far or 3
  local rock_scale, band_scale = 1, 1
  if type(kit.defaults.organic_budget) == "number" and kit.defaults.organic_budget > 0 then
    local est_band, est_far = 0, 0
    for _, g in ipairs(organic_regions) do
      local pl = plans[g.name]
      if pl then
        for _, sp in ipairs(pl.spans) do
          local L, H = dist(sp.a, sp.b), g.top - sp.y0
          if L > 0.5 and H > 0.5 then
            local size = math.max(1.8, math.min(4.2, H / 3.5))
            local rowh = math.max(0.9, math.min(2.2, H / 6))
            local band = math.min(H, math.min(2.4, math.max(0.9, 0.45 * H)) + rock_view)
            local f1, f2 = math.max(0, math.min(8, H - band)), math.max(0, H - band - 8)
            est_band = est_band + 7 * L / size * band / rowh
            est_far = est_far + 7 * L / size * (f1 / (rowh * rock_far ^ 2) + f2 / (rowh * (1.5 * rock_far) ^ 2))
          end
        end
        if pl.treat == "cave" and not g.sky then est_far = est_far + 2 * g.area / (4 * 0.62) ^ 2 end
      end
    end
    band_scale, rock_scale = RK.budget_scales(est_band, est_far)
    rec.rock_budget = { seen_estimate = math.floor(est_band / band_scale ^ 2 + est_far / rock_scale ^ 2 + 0.5), scale = rock_scale, band_scale = band_scale }
  end
  local rock_lod = (type(kit.defaults.organic_budget) == "number" and kit.defaults.organic_budget > 0)
    and { view = rock_view, far = rock_far * rock_scale, band = band_scale } or nil
  for _, g in ipairs(organic_regions) do
    local o, pl, rr = g.org, plans[g.name], rec.regions[g.name]
    local zn = g.path or g.name
    local st = pl and pl.st or RK.stream(trace_seed * 7919 + g.index * 104729 + (o.seed or 0) * 31)
    -- the region's own textures when it is rock (its material, or textures it chose), else the kit's rock
    local rocky_mat = g.material == "rock" or g.material == "cave" or g.material == "mine"
    local rtex, rtexs
    -- (a region's textures = {...} counts when any of its roles is a rock texture: the skin takes the region's rock)
    local function rck(s) return type(s) == "string" and s:lower():sub(1, 4) == "rck_" end
    local rocky_set = type(g.textures) == "table" and (rck(g.textures.wall) or rck(g.textures.floor) or rck(g.textures.ceiling))
    if rocky_mat or (g.material == nil and (rck(g.texture) or rocky_set)) then rtex, rtexs = RK.textures(g.texture, g.textures)
    else rtex, rtexs = RK.textures(nil, nil) end
    local info = { skin = 0, foot = 0, lean = pl and pl.lean_count or 0, vault = 0, pillars = 0, mounds = 0, boulders = 0 }
    local function emit(hulls, part, role, purpose)
      local bs = RK.emit(hulls, { name = rock_name, fn = "trace_rock", part = g.name .. "." .. part, texture = rtex, textures = rtexs })
      for _, b in ipairs(bs) do
        b:tag("kit.zone", zn)
        b:tag("kit.role", role)
        if purpose then b:tag("kit.purpose", purpose) end
        all[#all + 1] = b
      end
      return bs
    end
    local my_openings = pl and pl.openings or {}
    if pl == nil then
      for _, op in ipairs(openings) do if (op.a == g.name or op.b == g.name) and op.pos then my_openings[#my_openings + 1] = op end end
    end
    -- A floor spot is free: inside the outline, clear of its walls, doorways, routes, hosted ramps/stairs/decks and
    -- what was already placed (taken: {x, z, r}).
    local taken = {}
    local function free(x, z, r, wall_gap)
      local p = { x, z }
      if not inside(g.poly, x, z) then return false end
      if nearest_edge(g.poly, p) < r + wall_gap then return false end
      for _, op in ipairs(my_openings) do
        if dist(p, { op.pos[1], op.pos[3] }) < r + (op.width or 3) / 2 + 1.5 then return false end
      end
      if route_dist(p) < r + 1.75 then return false end
      for _, h in ipairs(R) do
        if h ~= g then
          local hosted = false
          for _, x in ipairs(h.hosts) do if x == g then hosted = true end end
          if hosted and (inside(h.poly, x, z) or nearest_edge(h.poly, p) < r + 0.5) then return false end
        end
      end
      for _, q in ipairs(taken) do if dist(p, q) < r + q[3] + 0.3 then return false end end
      return true
    end
    if pl then
      local cave = pl.treat == "cave" or (pl.treat == "wall" and not g.sky)
      local H = pl.H
      local hulls = {}
      for _, sp in ipairs(pl.spans) do
        local h, foot = RK.wall_skin(sp.a, sp.b, sp.n, {
          y0 = sp.y0, y1 = g.top, st = st,
          base = { 0.05, 0.35 },
          lean = pl.treat == "canyon" and -0.02 or 0,
          overhang = o.overhang or (cave and math.min(1.2, 0.25 * H) or 0),
          cap = cave,
          setback = sp.setback,
          band = pl.treat == "cave" and math.max(1.0, math.min(2.2, H / 3)) or nil,
          lod = rock_lod,
        })
        for _, x in ipairs(h) do hulls[#hulls + 1] = x end
        info.foot = math.max(info.foot, foot)
      end
      info.skin = #emit(hulls, "skin", "theme_detail", "rock wall skin along a traced wall")
      -- the vault: chunks hanging from a cave's ceiling, over the head room
      if pl.treat == "cave" and not g.sky then
        local vh = {}
        for _, piece in ipairs(g.pieces) do
          for _, x in ipairs(RK.ceiling_chunks(piece, g.top, st, function(cx, cz, bottom, rad)
            local p = { cx, cz }
            return inside(g.poly, cx, cz) and nearest_edge(g.poly, p) >= rad + 0.6 and bottom >= g.floor_at(p) + 2.6
          end, 4 * rock_scale)) do vh[#vh + 1] = x end
        end
        info.vault = #emit(vh, "vault", "theme_detail", "cave vault")
      end
    end
    -- pillars: rough columns floor to ceiling
    if o.pillars and not g.sky then
      local spots = {}
      if o.pillars.at then
        for _, p in ipairs(o.pillars.at) do spots[#spots + 1] = p end
      else
        local tries = 0
        while #spots < (o.pillars.count or 0) and tries < 400 do
          tries = tries + 1
          local x, z = g.bx0 + (g.bx1 - g.bx0) * st.next(), g.bz0 + (g.bz1 - g.bz0) * st.next()
          if free(x, z, 1.2, 2.5) then spots[#spots + 1] = { x, z } taken[#taken + 1] = { x, z, 3 } end
        end
      end
      local ph = {}
      for _, p in ipairs(spots) do
        local r0 = st.range(0.8, 1.3)
        local circ = {}
        for k = 0, 6 do local a = k * 2 * math.pi / 7 circ[#circ + 1] = { p[1] + math.cos(a) * r0, p[2] + math.sin(a) * r0 } end
        local hs = RK.mass_hulls(circ, g.floor_at(p), g.top + 0.3, { st = st, band = 1.6, step = 0, rough = 0.25, top_vary = 0, convex_pieces = convex_pieces, lod = rock_lod })
        for _, x in ipairs(hs) do ph[#ph + 1] = x end
      end
      info.pillars = #spots
      emit(ph, "pillar", "landmark", "cave pillar")
    end
    -- organic ground: gentle mounds (detail), level at the walls' feet, doorways and routes
    if o.ground == "organic" and g.lo == g.hi then
      local n = math.max(1, math.min(12, math.floor(g.area / 80 + 0.5)))
      local relief = o.relief or 0.5
      local mh, tries = {}, 0
      while #mh < n and tries < 300 and relief > 0 do
        tries = tries + 1
        local rad = st.range(2, 4.5)
        local x, z = g.bx0 + (g.bx1 - g.bx0) * st.next(), g.bz0 + (g.bz1 - g.bz0) * st.next()
        if free(x, z, rad, 0.6) then
          local h = math.min(relief * st.range(0.6, 1.0), 0.25 * rad)
          mh[#mh + 1] = RK.mound(x, g.lo, z, rad, h, st)
          taken[#taken + 1] = { x, z, rad }
        end
      end
      info.mounds = #emit(mh, "mound", "theme_detail", "ground mound")
    end
    -- boulders resting on the floor, mostly at the walls' feet, clear of doorways, routes and mounds
    if o.boulders and g.lo == g.hi then
      local B = o.boulders
      local want = B.count or math.floor(g.area * B.density / 100 + 0.5)
      local cands = {}
      for _, w in ipairs(rr.walls) do
        local L = dist(w.a, w.b)
        local u = { (w.b[1] - w.a[1]) / math.max(L, 1e-9), (w.b[2] - w.a[2]) / math.max(L, 1e-9) }
        local s = 0.8
        while s < L - 0.8 do
          cands[#cands + 1] = { w.a[1] + u[1] * s, w.a[2] + u[2] * s, -u[2], u[1] }
          s = s + 1.6
        end
      end
      local order = {}
      for i = 1, #cands do order[i] = i end
      for i = #order, 2, -1 do local j = 1 + math.floor(st.next() * i) order[i], order[j] = order[j], order[i] end
      local bh, placed = {}, 0
      local k, tries = 1, 0
      while placed < want and tries < want * 30 + 60 do
        tries = tries + 1
        local size = st.range(B.size[1], B.size[2])
        local x, z
        if k <= #order and st.next() < 0.8 then
          local c = cands[order[k]]
          k = k + 1
          local off = size / 2 + 0.45 + st.range(0, 1.0)
          x, z = c[1] + c[3] * off, c[2] + c[4] * off
        else
          x, z = g.bx0 + (g.bx1 - g.bx0) * st.next(), g.bz0 + (g.bz1 - g.bz0) * st.next()
        end
        if free(x, z, size / 2, 0.4) then
          local pts, rad = RK.boulder(x, g.lo, z, size, st)
          bh[#bh + 1] = { pts = pts, tall = size * 0.7 >= 1.0 }
          taken[#taken + 1] = { x, z, rad }
          placed = placed + 1
        end
      end
      local cover, small = {}, {}
      for _, e in ipairs(bh) do if e.tall then cover[#cover + 1] = e.pts else small[#small + 1] = e.pts end end
      info.boulders = #emit(cover, "boulder", "cover", "boulder at a rock wall") + #emit(small, "boulder", "theme_detail", "boulder at a rock wall")
      if placed < want then warnings[#warnings + 1] = "'" .. g.name .. "': " .. placed .. " of " .. want .. " boulders fit clear of walls, doorways and routes." end
    end
    rr.rock = info
  end

  -- Intent tags, as the kit's own builders carry them: traced ramps, stairs, path fills and decks are routes.
  for _, g in ipairs(R) do
    if g.kind == "ramp" or g.kind == "stairs" or g.kind == "deck" then
      for _, b in ipairs(rec.regions[g.name].brushes) do
        -- (kit.ramp / kit.stairs tag their own route and what it climbs)
        if not b.air and b:tags()["kit.role"] == nil then
          b:tag("kit.role", "route")
          b:tag("kit.purpose", (g.kind == "deck" and "deck" or g.kind) .. " " .. g.name)
        end
      end
    end
  end
  if any_sky then rec.sky_room = ensure_sky(name) end
  rec.group = group_if_many(name, all, true)

  -- Zones: air and fill brushes per region, the kit.zone tag on them, and the zone records' brushes.
  for _, g in ipairs(R) do
    local rr, z = rec.regions[g.name], zone_by[g.path or g.name]
    for _, b in ipairs(rr.brushes) do
      b:tag("kit.zone", z.name)
      if b.air then rr.air[#rr.air + 1] = b z.brushes[#z.brushes + 1] = b else rr.fills[#rr.fills + 1] = b z.fills[#z.fills + 1] = b end
    end
  end
  for _, z in ipairs(zones) do z.brush = z.brushes[1] end
  -- Through the kit's own organic builders, as kit.build_layout's rock spaces and ground use them: an Alpine terrain
  -- (kit.ground) for ground = "terrain", and the detail pass's rock formations (kit.rock_detail) for rock_detail.
  for _, g in ipairs(organic_regions) do
    local o, rr = g.org, rec.regions[g.name]
    local z = zone_by[g.path or g.name]
    local keep = {}
    for _, op in ipairs(openings) do
      if (op.a == g.name or op.b == g.name) and op.pos then keep[#keep + 1] = { op.pos[1], op.pos[2], op.pos[3], r = (op.width or 3) / 2 + 1.5 } end
    end
    for _, pl in ipairs(keep_clear) do
      for k = 1, #pl do
        keep[#keep + 1] = { pl[k][1], g.lo, pl[k][2], r = 2 }
        if k < #pl then
          local L = dist(pl[k], pl[k + 1])
          local nk = math.max(1, math.ceil(L / 2))
          for j = 1, nk - 1 do local q = along(pl[k], pl[k + 1], L, L * j / nk) keep[#keep + 1] = { q[1], g.lo, q[2], r = 2 } end
        end
      end
    end
    if o.ground == "terrain" then
      local flat = {}
      for _, k in ipairs(keep) do flat[#flat + 1] = { k[1], k[3], k.r } end
      local ok, res = ged.try(kit.ground, { min = { g.bx0, g.lo, g.bz0 }, max = { g.bx1, g.top, g.bz1 }, outline = g.poly, y = g.lo, mode = "terrain",
        relief = o.relief, seed = trace_seed + g.index, name = g.name .. "_ground", flat = (#flat > 0) and flat or nil })
      if ok then rr.ground = res else warnings[#warnings + 1] = "'" .. g.name .. "': no terrain (" .. tostring(res) .. ")." log.warn(api .. ": '" .. g.name .. "': no terrain: " .. tostring(res)) end
    end
    -- (via the layout builder, a rock-palette zone's detail pass places its rock formations already)
    local zone_rocks = false
    for _, zr in ipairs(rec.layout and rec.layout.zones or {}) do
      if zr.id == root(g).name and zr.detail and zr.detail.by_part and (zr.detail.by_part.rocks or 0) > 0 then zone_rocks = true end
    end
    if zone_rocks and o.rock_detail then note("'" .. g.name .. "': rock_detail is the layout detail pass's rock formations (the zone's rock palette)") end
    if o.rock_detail and not rr.rock_detail_done and not zone_rocks then
      rr.rock_detail_done = true
      local style = (g.kind == "cave" or o.organic) and "cave" or "canyon"
      local ok, res = ged.try(kit.rock_detail, { space = z, style = style, seed = trace_seed + g.index, keep = (#keep > 0) and keep or nil, name = g.name .. "_rocks" })
      if ok then rr.rock_detail = res else warnings[#warnings + 1] = "'" .. g.name .. "': no rock formations (" .. tostring(res) .. ")." log.warn(api .. ": '" .. g.name .. "': no rock formations: " .. tostring(res)) end
    end
  end
  -- UVs: the kit's mapper-style alignment over the whole plan (as kit.build_layout does over a layout), so textures
  -- run on across a region's convex pieces and into its neighbours; stair steps keep their own mapping.
  if kit.defaults.box_map and kit.defaults.uv_align and kit.defaults.ppm then
    level.brushes_tagged("kit.name", name):where(function(b)
      return not (b:tags()["kit.part"] or ""):find(".step", 1, true)
    end):uv_align{ ppm = kit.defaults.ppm }
  end

  -- 8. Optional leak check: a sealed build of the whole level; the holes inside the plan's box are this trace's.
  if checking then
    local h = ops.holes()
    local x0, z0, x1, z1, y0, y1 = math.huge, math.huge, -math.huge, -math.huge, math.huge, -math.huge
    for _, g in ipairs(R) do
      x0, z0, x1, z1 = math.min(x0, g.bx0), math.min(z0, g.bz0), math.max(x1, g.bx1), math.max(z1, g.bz1)
      y0, y1 = math.min(y0, g.lo - 1), math.max(y1, g.top)
    end
    local near = {}
    for _, p in ipairs(h.positions or {}) do
      local x, y, z = p.x or p[1], p.y or p[2], p.z or p[3]
      if x >= x0 - 2 and x <= x1 + 2 and z >= z0 - 2 and z <= z1 + 2 and y >= y0 - 2 and y <= y1 + 2 then near[#near + 1] = { x, y, z } end
    end
    rec.holes, rec.hole_positions, rec.level_holes = #near, near, h.count
    if #near > 0 then
      log.warn(api .. ": '" .. name .. "' leaks: " .. #near .. " open edge(s), the first near " .. P({ near[1][1], near[1][3] }) .. " at y " .. fmt(near[1][2]) .. ".")
    end
  end
  return rec
end

end

return install
