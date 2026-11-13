-- Glacier design kit: proportions, non-box rooms, roads and the space archetype library (installed into
-- the kit by kit.lua; use it as local kit = ged.import("kit")).
--
-- The rules come from the exemplar study (docs/internal/DESIGN-QUALITY-SPEC.md §(a), §c.1-2 to c.1-9):
--   * proportions by footprint: corridors 4-5 m high, rooms under 100 m² 3.5-6 m, halls of 100-400 m²
--     6-9 m, atria and set pieces 12 m or more; a hub is at least twice the height of the corridors that
--     feed it (kit.proportion, and kit.room when its size has no height);
--   * ceilings vary inside a space (exemplar headroom IQR/median 0.68-1.27): stepped, gabled, shed,
--     coffered and beamed ceilings, raised and sunken floor sections;
--   * walls are not all on the grid axes (exemplar off-axis wall share 0.08-0.35): chamfers, octagons,
--     L/T/U/cross plans and any polygon footprint;
--   * kit.space builds a whole space from a named archetype (indoor rooms, open-air platforms and decks,
--     payload road, overlook, gate and finale pieces) at exemplar scale, sealed under both build methods.
--
-- Everything is ordinary brushes in CSG time order: a space's air first, then the solids standing in it,
-- then any air carved through those solids. Detail brushes carry the dressing structure (slabs, beams,
-- trusses), so they never seal or split rooms.
--
-- This module returns one function, install(kit, internals), which kit.lua calls with its private
-- helpers (kit_struct installs first); it defines nothing global and never reads rng.

local function install(kit, I)
local fail, check_keys, number, positive, boolean = I.fail, I.check_keys, I.number, I.positive, I.boolean
local vec3, vec, name_for, group_if_many, fmt = I.vec3, I.vec, I.name_for, I.group_if_many, I.fmt
local textures_of, EPS, hull, box_points, warn = I.textures_of, I.EPS, I.hull, I.box_points, I.warn

-- ---------------------------------------------------------------------------------------------
-- Proportions (spec c.1-2)
-- ---------------------------------------------------------------------------------------------

local function half(x) return math.floor(x * 2 + 0.5) / 2 end

local PROPORTIONS = {
  corridor = { 4, 5 },
  room = { 3.5, 6 },
  hall = { 6, 9 },
  atrium = { 12, 24 },
}

local function proportion(kind, area, feeds)
  if kind == nil then
    if area < 100 then kind = "room" elseif area <= 400 then kind = "hall" else kind = "atrium" end
  end
  local lo, hi, h = nil, nil, nil
  if kind == "corridor" then
    lo, hi = 4, 5
    h = math.max(lo, math.min(hi, kit.defaults.corridor_height))
  elseif kind == "room" then
    lo, hi = 3.5, 6
    h = half(math.max(lo, math.min(hi, 3.5 + 2.5 * math.sqrt(math.max(area, 0) / 100))))
  elseif kind == "hall" then
    lo, hi = 6, 9
    h = half(math.max(lo, math.min(hi, 6 + 3 * (area - 100) / 300)))
  elseif kind == "atrium" then
    lo, hi = 12, 24
    h = 12
  elseif kind == "hub" then
    local c = feeds or kit.defaults.corridor_height
    lo, hi = 2 * c, math.max(2 * c, 16)
    h = half(math.max(lo, math.min(hi, 6 + 3 * (area - 100) / 300)))
  else
    return nil
  end
  return { kind = kind, height = h, min = lo, max = hi, area = area }
end
I.proportion = proportion

---@kit kit.proportion{area?|size?, kind?, height?, feeds?} → proportion — The exemplar proportion rule for a space: {kind, height, min, max, area}, and with height given also ok and message. Kinds: corridor 4-5 m, room (< 100 m²) 3.5-6 m, hall (100-400 m²) 6-9 m, atrium/set piece (> 400 m²) at least 12 m, hub at least 2 x the corridors that feed it.
---@opt area: number = from size — Floor area in m² (give area or size).
---@opt size: {w, d} — Footprint in metres.
---@opt kind: string = from the area — "corridor", "room", "hall", "atrium" or "hub".
---@opt height: number = none — A height to check against the rule (adds ok and message).
---@opt feeds: number = kit.defaults.corridor_height — Height of the corridors feeding a hub, metres.
function kit.proportion(t)
  local api = "kit.proportion"
  check_keys(api, t, { "area", "size", "kind", "height", "feeds" })
  local area = nil
  if t.area ~= nil then
    if t.size ~= nil then fail(api, "give either 'area' or 'size', not both.") end
    area = positive(api, "area", t.area)
  elseif t.size ~= nil then
    local s = t.size
    if type(s) ~= "table" or type(s[1]) ~= "number" or type(s[#s]) ~= "number" then fail(api, "'size' must be {w, d} in metres.") end
    area = s[1] * s[#s]
  elseif t.kind ~= "corridor" then
    fail(api, "give 'area' or 'size' (or kind = \"corridor\").")
  else
    area = 0
  end
  if t.kind ~= nil and PROPORTIONS[t.kind] == nil and t.kind ~= "hub" then
    fail(api, "'kind' must be \"corridor\", \"room\", \"hall\", \"atrium\" or \"hub\" (got " .. tostring(t.kind) .. ").")
  end
  local feeds = t.feeds ~= nil and positive(api, "feeds", t.feeds) or nil
  local r = proportion(t.kind, area, feeds)
  if t.height ~= nil then
    local h = positive(api, "height", t.height)
    r.ok = h >= r.min - EPS and h <= r.max + EPS
    if not r.ok then
      r.message = fmt(h) .. " m is outside the " .. r.kind .. " range " .. fmt(r.min) .. "-" .. fmt(r.max) ..
        " m (exemplar proportions; " .. fmt(r.height) .. " m suits " .. fmt(area) .. " m²)."
    end
  end
  return r
end

-- ---------------------------------------------------------------------------------------------
-- 2D polygon helpers (points {x, z}; outlines counter-clockwise, i.e. positive shoelace area)
-- ---------------------------------------------------------------------------------------------

local function area2(p)
  local s = 0
  for i = 1, #p do
    local a, b = p[i], p[i % #p + 1]
    s = s + a[1] * b[2] - b[1] * a[2]
  end
  return s / 2
end

local function cross2(o, a, b) return (a[1] - o[1]) * (b[2] - o[2]) - (a[2] - o[2]) * (b[1] - o[1]) end

local function is_convex(p)
  for i = 1, #p do
    if cross2(p[i], p[i % #p + 1], p[(i + 1) % #p + 1]) < -1e-9 then return false end
  end
  return true
end

local function inside(p, x, z)
  local c = false
  local j = #p
  for i = 1, #p do
    local a, b = p[i], p[j]
    if ((a[2] > z) ~= (b[2] > z)) and (x < (b[1] - a[1]) * (z - a[2]) / (b[2] - a[2]) + a[1]) then c = not c end
    j = i
  end
  return c
end
I.inside = inside

-- Ear-clipping triangulation of a simple CCW polygon, then Hertel-Mehlhorn merging into convex pieces
-- (each a CCW point list). Pieces share their diagonals exactly, so their walls meet on identical faces.
local function convex_pieces(p)
  if is_convex(p) then return { p } end
  local idx = {}
  for i = 1, #p do idx[i] = i end
  local tris = {}
  local guard = 0
  while #idx > 3 do
    guard = guard + 1
    if guard > 10000 then return nil end
    local found = false
    for k = 1, #idx do
      local i0, i1, i2 = idx[(k - 2) % #idx + 1], idx[k], idx[k % #idx + 1]
      local a, b, c = p[i0], p[i1], p[i2]
      if cross2(a, b, c) > 1e-9 then
        local ear = true
        for _, j in ipairs(idx) do
          if j ~= i0 and j ~= i1 and j ~= i2 then
            local q = p[j]
            if cross2(a, b, q) >= -1e-9 and cross2(b, c, q) >= -1e-9 and cross2(c, a, q) >= -1e-9 then ear = false break end
          end
        end
        if ear then
          tris[#tris + 1] = { i0, i1, i2 }
          table.remove(idx, k)
          found = true
          break
        end
      end
    end
    if not found then return nil end
  end
  tris[#tris + 1] = { idx[1], idx[2], idx[3] }
  -- Merge pieces across shared diagonals while the union stays convex.
  local pieces = tris
  local merged = true
  while merged do
    merged = false
    for a = 1, #pieces do
      for b = a + 1, #pieces do
        local pa, pb = pieces[a], pieces[b]
        -- find a shared edge (u, v) in pa and (v, u) in pb
        for i = 1, #pa do
          local u, v = pa[i], pa[i % #pa + 1]
          for j = 1, #pb do
            if pb[j] == v and pb[j % #pb + 1] == u then
              local m = {}
              for k = 0, #pa - 2 do m[#m + 1] = pa[(i + k) % #pa + 1] end           -- v ... u (pa without edge)
              for k = 1, #pb - 2 do m[#m + 1] = pb[(j + k) % #pb + 1] end           -- pb's others after u
              local pts = {}
              for k, id in ipairs(m) do pts[k] = p[id] end
              if is_convex(pts) then
                pieces[a] = m
                table.remove(pieces, b)
                merged = true
              end
              break
            end
          end
          if merged then break end
        end
        if merged then break end
      end
      if merged then break end
    end
  end
  local out = {}
  for _, piece in ipairs(pieces) do
    local pts = {}
    for k, id in ipairs(piece) do pts[k] = p[id] end
    if area2(pts) > 1e-4 then out[#out + 1] = pts end -- a sliver left by collinear corners carries no floor
  end
  return out
end

-- A convex polygon moved inward by d on every edge (nil when it collapses): the original clipped by
-- each edge's offset half-plane, so short edges that vanish drop out cleanly.
local function inset_convex(p, d)
  local n = #p
  local out = p
  for i = 1, n do
    local a, b = p[i], p[i % n + 1]
    local ex, ez = b[1] - a[1], b[2] - a[2]
    local l = math.sqrt(ex * ex + ez * ez)
    local nx, nz = -ez / l, ex / l -- inward (left of a CCW edge)
    local ox, oz = a[1] + nx * d, a[2] + nz * d
    local function side(q) return (q[1] - ox) * nx + (q[2] - oz) * nz end
    local clipped = {}
    for k = 1, #out do
      local u, v = out[k], out[k % #out + 1]
      local su, sv = side(u), side(v)
      if su >= 0 then clipped[#clipped + 1] = u end
      if (su >= 0) ~= (sv >= 0) then
        local f = su / (su - sv)
        clipped[#clipped + 1] = { u[1] + (v[1] - u[1]) * f, u[2] + (v[2] - u[2]) * f }
      end
    end
    out = clipped
    if #out < 3 then return nil end
  end
  -- Drop near-duplicate corners left where an edge vanished.
  local clean = {}
  for k = 1, #out do
    local q, r = out[k], out[k % #out + 1]
    if math.abs(q[1] - r[1]) + math.abs(q[2] - r[2]) > 1e-6 then clean[#clean + 1] = q end
  end
  if #clean < 3 or area2(clean) < 1 then return nil end
  return clean
end
I.inset_convex = inset_convex

-- Cuts every convex corner by c along both edges (clamped to 40% of the shorter edge).
local function chamfer(p, c)
  if c <= EPS then return p end
  local out = {}
  local n = #p
  for i = 1, n do
    local a, v, b = p[(i - 2) % n + 1], p[i], p[i % n + 1]
    if cross2(a, v, b) > 1e-9 then
      local la = math.sqrt((a[1] - v[1]) ^ 2 + (a[2] - v[2]) ^ 2)
      local lb = math.sqrt((b[1] - v[1]) ^ 2 + (b[2] - v[2]) ^ 2)
      local k = math.min(c, 0.4 * la, 0.4 * lb)
      out[#out + 1] = { v[1] + (a[1] - v[1]) / la * k, v[2] + (a[2] - v[2]) / la * k }
      out[#out + 1] = { v[1] + (b[1] - v[1]) / lb * k, v[2] + (b[2] - v[2]) / lb * k }
    else
      out[#out + 1] = v
    end
  end
  return out
end

-- Rounds outline points to the 1/16 m floor grid (chamfers on the grid stay exact 45° walls).
local function snap16(p)
  local out = {}
  for i, q in ipairs(p) do out[i] = { math.floor(q[1] * 16 + 0.5) / 16, math.floor(q[2] * 16 + 0.5) / 16 } end
  return out
end

local SHAPES = { rect = true, octagon = true, L = true, T = true, U = true, cross = true }

-- The local (unrotated, centred) outline of a named plan shape.
local function shape_outline(api, shape, w, d, arm)
  local x0, x1, z0, z1 = -w / 2, w / 2, -d / 2, d / 2
  if shape == "rect" or shape == "octagon" then
    return { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }
  end
  local a = arm or math.floor(math.min(w, d) * 0.45 * 4 + 0.5) / 4
  if a < 2 or a > math.min(w, d) - 1 then fail(api, "'arm' must be between 2 m and the footprint's short side minus 1 m (got " .. fmt(a) .. ").") end
  if shape == "L" then
    return { { x0, z0 }, { x1, z0 }, { x1, z0 + a }, { x0 + a, z0 + a }, { x0 + a, z1 }, { x0, z1 } }
  elseif shape == "T" then
    return { { -a / 2, z0 }, { a / 2, z0 }, { a / 2, z1 - a }, { x1, z1 - a }, { x1, z1 }, { x0, z1 }, { x0, z1 - a }, { -a / 2, z1 - a } }
  elseif shape == "U" then
    return { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x1 - a, z1 }, { x1 - a, z0 + a }, { x0 + a, z0 + a }, { x0 + a, z1 }, { x0, z1 } }
  else -- cross
    local ha = a / 2
    return {
      { -ha, z0 }, { ha, z0 }, { ha, -ha }, { x1, -ha }, { x1, ha }, { ha, ha },
      { ha, z1 }, { -ha, z1 }, { -ha, ha }, { x0, ha }, { x0, -ha }, { -ha, -ha },
    }
  end
end

-- ---------------------------------------------------------------------------------------------
-- Frames: local plan coordinates (facing +z) to world, for the four headings
-- ---------------------------------------------------------------------------------------------

local ROT = {
  ["+z"] = function(x, z) return x, z end,
  ["+x"] = function(x, z) return z, -x end,
  ["-z"] = function(x, z) return -x, -z end,
  ["-x"] = function(x, z) return -z, x end,
}
local DIRV = { ["+x"] = { 1, 0 }, ["-x"] = { -1, 0 }, ["+z"] = { 0, 1 }, ["-z"] = { 0, -1 } }

local function frame(api, pos, facing)
  facing = facing or "+z"
  local rot = ROT[facing]
  if rot == nil then fail(api, "'facing' must be one of \"+x\", \"-x\", \"+z\", \"-z\" (got " .. tostring(facing) .. ").") end
  local ox, oy, oz = pos[1], pos[2], pos[3]
  local F = { facing = facing, x = ox, y = oy, z = oz }
  function F.p(x, y, z)
    local wx, wz = rot(x, z)
    return { ox + wx, oy + y, oz + wz }
  end
  function F.xz(x, z)
    local wx, wz = rot(x, z)
    return { ox + wx, oz + wz }
  end
  function F.d(dir)
    local v = DIRV[dir]
    local wx, wz = rot(v[1], v[2])
    for k, u in pairs(DIRV) do if math.abs(u[1] - wx) < 0.5 and math.abs(u[2] - wz) < 0.5 then return k end end
  end
  function F.poly(p)
    local out = {}
    for i, q in ipairs(p) do out[i] = F.xz(q[1], q[2]) end
    return out
  end
  -- A local rectangle (x0, z0, x1, z1) as world min/max x, z.
  function F.rect(x0, z0, x1, z1)
    local a, b = F.xz(x0, z0), F.xz(x1, z1)
    return math.min(a[1], b[1]), math.min(a[2], b[2]), math.max(a[1], b[1]), math.max(a[2], b[2])
  end
  return F
end
I.frame = frame

-- A world prism over a CCW plan polygon from y0 to y1 (y1 may be a function of the plan point for
-- sloped tops; the top must stay planar).
local function prism(fn, name, part, poly, y0, y1, o)
  local pts = {}
  for _, q in ipairs(poly) do
    pts[#pts + 1] = { q[1], y0, q[2] }
    local top = type(y1) == "function" and y1(q[1], q[2]) or y1
    pts[#pts + 1] = { q[1], top, q[2] }
  end
  return hull(fn, name, part, pts, o)
end
I.prism = prism

local function bbox(poly)
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, q in ipairs(poly) do
    x0, z0 = math.min(x0, q[1]), math.min(z0, q[2])
    x1, z1 = math.max(x1, q[1]), math.max(z1, q[2])
  end
  return x0, z0, x1, z1
end

local function centroid(poly)
  local cx, cz = 0, 0
  for _, q in ipairs(poly) do cx, cz = cx + q[1], cz + q[2] end
  return cx / #poly, cz / #poly
end

local function tex_opts(t)
  if t.texture ~= nil then return { texture = t.texture } end
  if t.textures ~= nil then return { textures = t.textures } end
  return { texture = kit.defaults.texture, textures = kit.defaults.textures }
end

local function size2(api, key, v)
  if type(v) ~= "table" then fail(api, "'" .. key .. "' must be {w, d} in metres.") end
  local w, d = nil, nil
  if v[1] ~= nil then w, d = v[1], v[#v] else w, d = v.w or v.x, v.d or v.z end
  if type(w) ~= "number" or type(d) ~= "number" or w <= 0 or d <= 0 then fail(api, "'" .. key .. "' must be {w, d} in metres, both > 0.") end
  return w, d
end

-- ---------------------------------------------------------------------------------------------
-- kit.shaped_room
-- ---------------------------------------------------------------------------------------------

local CEILINGS = { flat = true, stepped = true, gable = true, shed = true, coffered = true, beamed = true }

---@kit kit.shaped_room{name?, pos, size?|footprint?, shape?, arm?, chamfer?, height?, ceiling?, rise?, inset?, region?, facing?, detail?, texture?|textures?} → room — A room that is not a box: a rect, octagon, L, T, U or cross plan (or any simple polygon footprint), corners chamfered, with a flat, stepped, gabled, shed, coffered or beamed ceiling. Air pieces are convex prisms meeting on identical faces; the record works with kit.corridor/doorway/opening like a kit.room (min/max is the plan's bounds, max.y the base ceiling).
---@opt name: string = shaped_room<n> — Names the room (tags).
---@opt pos: vec — Required: the floor point at the centre of the plan.
---@opt size: {w, d} — The plan's bounds along x and z before facing (give size, or footprint).
---@opt footprint: {{x, z}...} = none — A simple polygon (3 to 64 points, either winding) relative to pos, instead of a named shape.
---@opt shape: string = "rect" — "rect", "octagon", "L", "T", "U" or "cross".
---@opt arm: number = 45% of the short side — Arm width of an L, T, U or cross plan, metres.
---@opt chamfer: number = octagon: 29% of the short side, others 0 — Cut every outer corner by this many metres (45° walls; 1-3 m reads as a built corner).
---@opt height: number = kit.proportion for the plan's area — Base clear height, metres.
---@opt ceiling: string = "flat" — "flat", "stepped" (a raised bay), "gable" (a ridged bay), "shed" (a bay sloping up along its long axis), "coffered" (a grid of recesses) or "beamed" (detail beams under a flat ceiling).
---@opt rise: number = stepped/gable/shed: max(2, 0.6 x height); coffered: 0.75 — How far the ceiling feature rises above the base height, metres.
---@opt inset: number = 25% of the short side — How far the raised bay stays in from the walls (stepped, gable, shed, coffered), metres.
---@opt region: {x0, z0, x1, z1} = from inset — The raised bay as a rectangle relative to pos (before facing), instead of inset.
---@opt facing: string = "+z" — Rotates the plan: the local +z side faces this way ("+x", "-x", "+z", "-z").
---@opt detail: boolean = false — Make the air pieces detail.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.shaped_room(t)
  local api = "kit.shaped_room"
  check_keys(api, t, { "name", "pos", "size", "footprint", "shape", "arm", "chamfer", "height", "ceiling", "rise", "inset", "region",
    "facing", "detail", "texture", "textures" })
  local name = name_for(api, "shaped_room", t.name)
  local px, py, pz = vec3(api, "pos", t.pos)
  local F = frame(api, { px, py, pz }, t.facing)
  local shape = t.shape or "rect"
  local outline = nil
  local w, d = nil, nil
  if t.footprint ~= nil then
    if t.size ~= nil or t.shape ~= nil or t.arm ~= nil then fail(api, "give either 'footprint' (a polygon) or 'size' with 'shape', not both.") end
    if type(t.footprint) ~= "table" or #t.footprint < 3 or #t.footprint > 64 then fail(api, "'footprint' must list 3 to 64 points {x, z}.") end
    outline = {}
    for i, q in ipairs(t.footprint) do
      local x, z = nil, nil
      if type(q) == "table" then x, z = q[1] or q.x, q[2] or q.z end
      if type(x) ~= "number" or type(z) ~= "number" then fail(api, "'footprint[" .. i .. "]' must be a point {x, z}.") end
      outline[i] = { x, z }
    end
    if area2(outline) < 0 then
      local r = {}
      for i = #outline, 1, -1 do r[#r + 1] = outline[i] end
      outline = r
    end
    if area2(outline) < 4 then fail(api, "'footprint' encloses less than 4 m².") end
    local x0, z0, x1, z1 = bbox(outline)
    w, d = x1 - x0, z1 - z0
    shape = "polygon"
  else
    if t.size == nil then fail(api, "'size' ({w, d}) or 'footprint' is required.") end
    w, d = size2(api, "size", t.size)
    if not SHAPES[shape] then fail(api, "'shape' must be \"rect\", \"octagon\", \"L\", \"T\", \"U\" or \"cross\" (got " .. tostring(shape) .. ").") end
    if t.arm ~= nil and (shape == "rect" or shape == "octagon") then fail(api, "'arm' only applies to L, T, U and cross plans.") end
    outline = shape_outline(api, shape, w, d, t.arm ~= nil and positive(api, "arm", t.arm) or nil)
  end
  local cut = t.chamfer ~= nil and number(api, "chamfer", t.chamfer) or (shape == "octagon" and math.floor(math.min(w, d) * 0.2929 * 4 + 0.5) / 4 or 0)
  if cut < 0 then fail(api, "'chamfer' must be 0 or more metres.") end
  outline = snap16(chamfer(outline, cut))
  local plan_area = area2(outline)
  local height = t.height ~= nil and positive(api, "height", t.height) or proportion(nil, plan_area).height
  I.check_walkable(api, "room", math.min(w, d), height, false)
  local world = F.poly(outline)
  -- A simple polygon: no two non-adjacent edges touch.
  local nw = #world
  for i = 1, nw do
    for j = i + 2, nw do
      if not (i == 1 and j == nw) then
        local a, b, c2, d2 = world[i], world[i % nw + 1], world[j], world[j % nw + 1]
        local d1, d2_, d3, d4 = cross2(a, b, c2), cross2(a, b, d2), cross2(c2, d2, a), cross2(c2, d2, b)
        if ((d1 > 0) ~= (d2_ > 0)) and ((d3 > 0) ~= (d4 > 0)) then
          fail(api, "the footprint is not a simple polygon (its edges cross).", "List the points in order around the outline.")
        end
      end
    end
  end
  local pieces = convex_pieces(world)
  if pieces == nil then fail(api, "the footprint is not a simple polygon (its edges cross).", "List the points in order around the outline.") end

  local tex = tex_opts(t)
  local detail = boolean(api, "detail", t.detail, false)
  local brushes = {}
  for i, piece in ipairs(pieces) do
    brushes[#brushes + 1] = prism("shaped_room", name, "air" .. i, piece, py, py + height,
      { air = true, detail = detail, texture = tex.texture, textures = tex.textures })
  end

  -- The ceiling feature.
  local style = t.ceiling or "flat"
  if not CEILINGS[style] then fail(api, "'ceiling' must be \"flat\", \"stepped\", \"gable\", \"shed\", \"coffered\" or \"beamed\" (got " .. tostring(style) .. ").") end
  if style == "flat" and (t.rise ~= nil or t.inset ~= nil or t.region ~= nil) then fail(api, "'rise', 'inset' and 'region' need a ceiling other than \"flat\".") end
  local rise = t.rise ~= nil and positive(api, "rise", t.rise) or (style == "coffered" and 0.75 or math.max(2, half(0.6 * height)))
  local top = py + height
  local raised = nil
  if style == "stepped" or style == "gable" or style == "shed" or style == "coffered" then
    if t.region ~= nil then
      if t.inset ~= nil then fail(api, "give either 'inset' or 'region', not both.") end
      local r = t.region
      if type(r) ~= "table" or #r ~= 4 then fail(api, "'region' must be {x0, z0, x1, z1} relative to pos.") end
      local x0, z0, x1, z1 = math.min(r[1], r[3]), math.min(r[2], r[4]), math.max(r[1], r[3]), math.max(r[2], r[4])
      raised = F.poly({ { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } })
      for _, q in ipairs(raised) do
        if not inside(world, q[1] + (px - q[1]) * 1e-6, q[2] + (pz - q[2]) * 1e-6) then
          fail(api, "'region' corner (" .. fmt(q[1] - px) .. ", " .. fmt(q[2] - pz) .. ") lies outside the room's plan.")
        end
      end
    else
      local inset = t.inset ~= nil and positive(api, "inset", t.inset) or math.max(1, math.min(w, d) * 0.25)
      local biggest, best = pieces[1], -1
      for _, piece in ipairs(pieces) do local a = area2(piece) if a > best then biggest, best = piece, a end end
      raised = inset_convex(biggest, inset)
      if raised == nil then fail(api, "inset " .. fmt(inset) .. " m leaves no raised bay inside the plan.", "Use a smaller inset, or give region.") end
    end
    raised = snap16(raised)
    local rx0, rz0, rx1, rz1 = bbox(raised)
    if style == "stepped" then
      brushes[#brushes + 1] = prism("shaped_room", name, "raised", raised, py, top + rise, { air = true, detail = detail, texture = tex.texture, textures = tex.textures })
    elseif style == "shed" then
      local along_x = (rx1 - rx0) >= (rz1 - rz0)
      local f = along_x and function(x, _) return top + rise * (x - rx0) / (rx1 - rx0) end or function(_, z) return top + rise * (z - rz0) / (rz1 - rz0) end
      brushes[#brushes + 1] = prism("shaped_room", name, "raised", raised, py, f, { air = true, detail = detail, texture = tex.texture, textures = tex.textures })
    elseif style == "gable" then
      local pts = {}
      for _, q in ipairs(raised) do
        pts[#pts + 1] = { q[1], py, q[2] }
        pts[#pts + 1] = { q[1], top, q[2] }
      end
      if (rx1 - rx0) >= (rz1 - rz0) then
        local mz = (rz0 + rz1) / 2
        pts[#pts + 1] = { rx0, top + rise, mz }
        pts[#pts + 1] = { rx1, top + rise, mz }
      else
        local mx = (rx0 + rx1) / 2
        pts[#pts + 1] = { mx, top + rise, rz0 }
        pts[#pts + 1] = { mx, top + rise, rz1 }
      end
      brushes[#brushes + 1] = hull("shaped_room", name, "raised", pts, { air = true, detail = detail, texture = tex.texture, textures = tex.textures })
    else -- coffered: recesses on a ~4 m grid inside the bay, ribs 0.6 m wide between them
      local rib = 0.6
      local nx = math.max(1, math.floor((rx1 - rx0) / 4 + 0.5))
      local nz = math.max(1, math.floor((rz1 - rz0) / 4 + 0.5))
      local cw, cd = (rx1 - rx0) / nx, (rz1 - rz0) / nz
      local k = 0
      for i = 0, nx - 1 do
        for j = 0, nz - 1 do
          local x0, z0 = rx0 + i * cw + rib / 2, rz0 + j * cd + rib / 2
          local x1, z1 = rx0 + (i + 1) * cw - rib / 2, rz0 + (j + 1) * cd - rib / 2
          local ok = inside(raised, x0 + 0.01, z0 + 0.01) and inside(raised, x1 - 0.01, z0 + 0.01) and inside(raised, x0 + 0.01, z1 - 0.01) and inside(raised, x1 - 0.01, z1 - 0.01)
          if ok then
            k = k + 1
            local q = snap16({ { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } })
            brushes[#brushes + 1] = prism("shaped_room", name, "coffer" .. k, q, py, top + rise, { air = true, detail = detail, texture = tex.texture, textures = tex.textures })
          end
        end
      end
    end
  elseif style == "beamed" then
    if t.inset ~= nil or t.region ~= nil then fail(api, "'inset' and 'region' do not apply to a beamed ceiling.") end
    local x0, z0, x1, z1 = bbox(world)
    local along_x = (x1 - x0) < (z1 - z0) -- beams span the short way
    local n = math.max(1, math.floor(((along_x and (z1 - z0)) or (x1 - x0)) / 4))
    local by = top - 0.25
    for i = 1, n do
      local f = i / (n + 1)
      local a, b = nil, nil
      if along_x then
        local z = z0 + (z1 - z0) * f
        a, b = { x0 - 0.25, by, z }, { x1 + 0.25, by, z }
      else
        local x = x0 + (x1 - x0) * f
        a, b = { x, by, z0 - 0.25 }, { x, by, z1 + 0.25 }
      end
      brushes[#brushes + 1] = I.member("shaped_room", name, "beam" .. i, a, b, 0.4, 0.5, tex)
    end
    rise = 0
  else
    rise = 0
  end

  local bx0, bz0, bx1, bz1 = bbox(world)
  return {
    kind = "room", name = name, shape = shape, brush = brushes[1], brushes = brushes, outline = world, pieces = pieces,
    pos = vec(px, py, pz), floor_y = py, height = height, ceiling = style, rise = rise, raised = raised,
    top_y = top + ((style == "beamed" or style == "flat") and 0 or rise), area = plan_area, chamfer = cut, facing = F.facing,
    min = vec(bx0, py, bz0), max = vec(bx1, py + height, bz1), center = vec((bx0 + bx1) / 2, py + height / 2, (bz0 + bz1) / 2),
    size = vec(bx1 - bx0, height, bz1 - bz0),
  }
end

-- ---------------------------------------------------------------------------------------------
-- Alcoves, pilasters and floor sections
-- ---------------------------------------------------------------------------------------------

-- The plan polygon of a record: its outline, or its min/max box.
local function plan_of(r)
  if type(r.outline) == "table" then return r.outline end
  local x0, z0, x1, z1 = r.min.x, r.min.z, r.max.x, r.max.z
  return { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }
end

-- Positions along one side of a record's bounds where the bounds face is really a wall: the probe just
-- inside is in the plan and the probe just outside is not.
local function wall_spots(r, side, spacing, margin, width, at)
  local plan = plan_of(r)
  local x0, z0, x1, z1 = r.min.x, r.min.z, r.max.x, r.max.z
  local along_x = side == "+z" or side == "-z"
  local lo, hi = along_x and x0 or z0, along_x and x1 or z1
  local wall = (side == "+x" and x1) or (side == "-x" and x0) or (side == "+z" and z1) or z0
  local out_sign = (side == "+x" or side == "+z") and 1 or -1
  local spots = {}
  local candidates = {}
  if at ~= nil then
    local mid = (lo + hi) / 2
    for _, a in ipairs(at) do candidates[#candidates + 1] = mid + a end
  else
    local n = math.floor((hi - lo - 2 * margin) / spacing + EPS)
    local used = n * spacing
    for k = 0, n do candidates[#candidates + 1] = (lo + hi) / 2 - used / 2 + k * spacing end
  end
  for _, s in ipairs(candidates) do
    local ok = true
    for _, e in ipairs({ -width / 2, 0, width / 2 }) do
      local a = s + e
      local ix, iz, ox, oz = nil, nil, nil, nil
      if along_x then ix, iz, ox, oz = a, wall - out_sign * 0.05, a, wall + out_sign * 0.05
      else ix, iz, ox, oz = wall - out_sign * 0.05, a, wall + out_sign * 0.05, a end
      if not inside(plan, ix, iz) or inside(plan, ox, oz) then ok = false end
    end
    if ok then spots[#spots + 1] = s end
  end
  return spots, wall, out_sign, along_x
end

local function side_of(api, key, v)
  if DIRV[v] == nil then fail(api, "'" .. key .. "' must be one of \"+x\", \"-x\", \"+z\", \"-z\" (got " .. tostring(v) .. ").") end
  return v
end

local function room_record(api, key, r)
  if type(r) ~= "table" or r.min == nil or r.max == nil then fail(api, "'" .. key .. "' must be a kit room record (kit.room or kit.shaped_room).") end
  return r
end

---@kit kit.alcoves{name?, room, side, count?|at?, width?, depth?, height?, spacing?, texture?|textures?} → alcoves — Air insets cut outward from one wall of a room (only where that side really is a wall): count evenly spaced alcoves, or one at each offset in at. Fails when an alcove would break into another space.
---@opt name: string = alcoves<n> — Names the alcoves (tags).
---@opt room: record — Required: a kit.room or kit.shaped_room record.
---@opt side: string — Required: the wall, "+x", "-x", "+z" or "-z" (world).
---@opt count: integer = as many as fit at spacing — How many alcoves (give count or at).
---@opt at: number[] = none — Offsets of the alcove centres along the wall from its middle, metres.
---@opt width: number = 3 — Alcove width, metres.
---@opt depth: number = 1.5 — How far the alcove goes into the wall, metres (1-2 reads as an inset).
---@opt height: number = 3.5 (at most the room height) — Alcove clear height, metres.
---@opt spacing: number = width + 2 — Centre spacing when placing by count, metres.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.alcoves(t)
  local api = "kit.alcoves"
  check_keys(api, t, { "name", "room", "side", "count", "at", "width", "depth", "height", "spacing", "texture", "textures" })
  local name = name_for(api, "alcoves", t.name)
  local r = room_record(api, "room", t.room)
  local side = side_of(api, "side", t.side)
  local width = positive(api, "width", t.width, 3)
  local depth = positive(api, "depth", t.depth, 1.5)
  local room_h = r.max.y - r.floor_y
  local height = positive(api, "height", t.height, math.min(3.5, room_h))
  I.check_walkable(api, "alcove", width, height, false)
  local spacing = positive(api, "spacing", t.spacing, width + 2)
  if t.count ~= nil and t.at ~= nil then fail(api, "give either 'count' or 'at', not both.") end
  local spots, wall, out_sign, along_x = wall_spots(r, side, spacing, (r.chamfer or 0) + 0.5 + width / 2, width, t.at)
  if t.count ~= nil then
    local c = number(api, "count", t.count)
    if c < 1 or c ~= math.floor(c) then fail(api, "'count' must be a whole number of at least 1.") end
    if c > #spots then fail(api, "only " .. #spots .. " alcove(s) fit on the " .. side .. " wall of " .. (r.name or "the room") .. " at " .. fmt(spacing) .. " m spacing.") end
    -- keep the middle c spots
    local first = math.floor((#spots - c) / 2) + 1
    local keep = {}
    for i = first, first + c - 1 do keep[#keep + 1] = spots[i] end
    spots = keep
  end
  if #spots == 0 then fail(api, "no alcove fits on the " .. side .. " wall of " .. (r.name or "the room") .. ".", "Use a narrower width, or another side.") end
  local tex = tex_opts(t)
  local brushes = {}
  local y0 = r.floor_y
  for i, s in ipairs(spots) do
    local a0, a1 = s - width / 2, s + width / 2
    local w0, w1 = wall - out_sign * 0.25, wall + out_sign * depth
    local x0, z0, x1, z1 = nil, nil, nil, nil
    if along_x then x0, x1, z0, z1 = a0, a1, math.min(w0, w1), math.max(w0, w1)
    else x0, x1, z0, z1 = math.min(w0, w1), math.max(w0, w1), a0, a1 end
    -- Never break into another space (an air portal brush is an opening like any doorway).
    local q = level.brushes_in{ min = { x0 - 0.5, y0, z0 - 0.5 }, max = { x1 + 0.5, y0 + height, z1 + 0.5 } }
    for _, b in ipairs(q.brushes) do
      if b.air then
        local tags = b:tags()
        local own = false
        for _, rb in ipairs(r.brushes or { r.brush }) do if rb.uid == b.uid then own = true end end
        if not own and tags["kit.name"] ~= r.name then
          local bb = b.bounds
          if bb.min.x < x1 + 0.5 and bb.max.x > x0 - 0.5 and bb.min.z < z1 + 0.5 and bb.max.z > z0 - 0.5 then
            fail(api, "alcove " .. i .. " would come within 0.5 m of another space (" .. (tags["kit.name"] or ("brush#" .. b.uid)) .. ").", "Use a shallower depth or another wall.")
          end
        end
      end
    end
    brushes[#brushes + 1] = hull("alcoves", name, "alcove" .. i, box_points(x0, y0, z0, x1, y0 + height, z1), { air = true, texture = tex.texture, textures = tex.textures })
  end
  return { kind = "alcoves", name = name, brushes = brushes, count = #brushes, depth = depth, width = width, height = height }
end

---@kit kit.pilasters{name?, room, sides?, spacing?, width?, depth?, height?, detail?, texture?|textures?} → pilasters — Pilasters (shallow piers) against a room's walls every spacing metres, only where the side really is a wall; detail by default.
---@opt name: string = pilasters<n> — Names the pilasters (tags).
---@opt room: record — Required: a kit.room or kit.shaped_room record.
---@opt sides: string[] = all four — The walls to line ({"+x", "-z"}, ...).
---@opt spacing: number = 5 — Centre spacing, metres (4-6 m reads as a panelled wall).
---@opt width: number = 1 — Pilaster width along the wall, metres.
---@opt depth: number = 0.5 — How far a pilaster stands out of the wall, metres.
---@opt height: number = the room height — Pilaster height, metres.
---@opt detail: boolean = true — Make the pilasters detail.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.pilasters(t)
  local api = "kit.pilasters"
  check_keys(api, t, { "name", "room", "sides", "spacing", "width", "depth", "height", "detail", "texture", "textures" })
  local name = name_for(api, "pilasters", t.name)
  local r = room_record(api, "room", t.room)
  local spacing = positive(api, "spacing", t.spacing, 5)
  local width = positive(api, "width", t.width, 1)
  local depth = positive(api, "depth", t.depth, 0.5)
  local height = positive(api, "height", t.height, r.max.y - r.floor_y)
  local detail = boolean(api, "detail", t.detail, true)
  local sides = t.sides or { "+x", "-x", "+z", "-z" }
  if type(sides) ~= "table" then fail(api, "'sides' must list walls, e.g. {\"+x\", \"-z\"}.") end
  local tex = tex_opts(t)
  local brushes = {}
  for _, side in ipairs(sides) do
    side_of(api, "sides[]", side)
    local spots, wall, out_sign, along_x = wall_spots(r, side, spacing, (r.chamfer or 0) + width, width, nil)
    for _, s in ipairs(spots) do
      local a0, a1 = s - width / 2, s + width / 2
      local w0, w1 = wall, wall - out_sign * depth
      local x0, z0, x1, z1 = nil, nil, nil, nil
      if along_x then x0, x1, z0, z1 = a0, a1, math.min(w0, w1), math.max(w0, w1)
      else x0, x1, z0, z1 = math.min(w0, w1), math.max(w0, w1), a0, a1 end
      brushes[#brushes + 1] = hull("pilasters", name, "pilaster" .. (#brushes + 1),
        box_points(x0, r.floor_y, z0, x1, r.floor_y + height, z1), { detail = detail, texture = tex.texture, textures = tex.textures })
    end
  end
  return { kind = "pilasters", name = name, brushes = brushes, count = #brushes }
end

---@kit kit.floor_section{name?, room?, min, max, rise, y?, detail?, texture?|textures?} → floor_section — A raised (rise > 0, a solid block) or sunken (rise < 0, an air pit) floor section over a floor rectangle; 0.5-1.5 m reads as a level change. A rise above the 1 m step limit needs stairs or a ramp (needs_steps in the record). With room, the rectangle must lie in the room's plan; in a convex plan with chamfered corners (room.chamfer > 0, room.outline) a rectangle that only crosses a chamfer is cut to the plan (the record's outline and clipped say so).
---@opt name: string = floor_section<n> — Names the section (tags).
---@opt room: record = none — The room it stands in (its floor height, and the rectangle must lie inside its plan).
---@opt min: vec — Required: one floor corner {x, y, z} of the rectangle (y ignored with room).
---@opt max: vec — Required: the opposite corner.
---@opt rise: number — Required: height change in metres (non-zero; positive raises, negative sinks).
---@opt y: number = the room's floor, else min.y — The floor height the section starts from.
---@opt detail: boolean = false — Make a raised block detail.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.floor_section(t)
  local api = "kit.floor_section"
  check_keys(api, t, { "name", "room", "min", "max", "rise", "y", "detail", "texture", "textures" })
  local name = name_for(api, "floor_section", t.name)
  local ax, ay, az = vec3(api, "min", t.min)
  local bx, _, bz = vec3(api, "max", t.max)
  local x0, x1, z0, z1 = math.min(ax, bx), math.max(ax, bx), math.min(az, bz), math.max(az, bz)
  if x1 - x0 < 0.5 or z1 - z0 < 0.5 then fail(api, "the rectangle must be at least 0.5 m on each side.") end
  local rise = number(api, "rise", t.rise)
  if math.abs(rise) < 0.0625 then fail(api, "'rise' must be at least 1/16 m up or down.") end
  local y = ay
  local poly = nil
  if t.room ~= nil then
    local r = room_record(api, "room", t.room)
    y = r.floor_y
    local plan = plan_of(r)
    local out = nil
    for _, q in ipairs({ { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }) do
      if not inside(plan, q[1] + (q[1] < (x0 + x1) / 2 and 0.01 or -0.01), q[2] + (q[2] < (z0 + z1) / 2 and 0.01 or -0.01)) then out = out or q end
    end
    if out then
      -- A convex plan (a chamfered rect or octagon): cut the rectangle to the plan when its centre is inside.
      local cp = plan
      if area2(cp) < 0 then local rv = {} for i = #cp, 1, -1 do rv[#rv + 1] = cp[i] end cp = rv end
      if is_convex(cp) and inside(cp, (x0 + x1) / 2, (z0 + z1) / 2) then
        local cut = { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }
        for i = 1, #cp do
          local a, b = cp[i], cp[i % #cp + 1]
          local res = {}
          for j = 1, #cut do
            local p1, p2 = cut[j], cut[j % #cut + 1]
            local s1, s2 = cross2(a, b, p1), cross2(a, b, p2)
            if s1 >= -1e-9 then res[#res + 1] = p1 end
            if (s1 >= -1e-9) ~= (s2 >= -1e-9) then
              local f = s1 / (s1 - s2)
              res[#res + 1] = { p1[1] + (p2[1] - p1[1]) * f, p1[2] + (p2[2] - p1[2]) * f }
            end
          end
          cut = res
          if #cut < 3 then break end
        end
        if #cut >= 3 and math.abs(area2(cut)) >= 0.25 then poly = cut end
      end
      if not poly then
        local hint = (r.chamfer or 0) > 0 and ("The room's corners are chamfered by " .. fmt(r.chamfer) .. " m: keep the rectangle that far in from the corners (room.outline is the plan).") or nil
        fail(api, "corner (" .. fmt(out[1]) .. ", " .. fmt(out[2]) .. ") lies outside " .. (r.name or "the room") .. "'s plan.", hint)
      end
    end
  end
  if t.y ~= nil then y = number(api, "y", t.y) end
  local tex = tex_opts(t)
  local brush = nil
  local function points(ylo, yhi)
    if not poly then return box_points(x0, ylo, z0, x1, yhi, z1) end
    local pts = {}
    for _, q in ipairs(poly) do pts[#pts + 1] = { q[1], ylo, q[2] } pts[#pts + 1] = { q[1], yhi, q[2] } end
    return pts
  end
  if rise > 0 then
    brush = hull("floor_section", name, "raised", points(y, y + rise),
      { detail = boolean(api, "detail", t.detail, false), texture = tex.texture, textures = tex.textures })
  else
    if t.detail ~= nil then fail(api, "'detail' applies only to a raised section.") end
    brush = hull("floor_section", name, "sunken", points(y + rise, y + 0.5), { air = true, texture = tex.texture, textures = tex.textures })
  end
  return {
    kind = "floor_section", name = name, brush = brush, brushes = { brush }, rise = rise, needs_steps = math.abs(rise) > kit.limits.max_riser + EPS,
    outline = poly or { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } }, clipped = poly ~= nil,
    min = vec(x0, math.min(y, y + rise), z0), max = vec(x1, math.max(y, y + rise), z1), floor_y = y + rise,
  }
end

-- ---------------------------------------------------------------------------------------------
-- kit.road: a wide graded road with level turn pads (the payload cart route)
-- ---------------------------------------------------------------------------------------------

local function sky_up(brushes)
  local n = 0
  for _, b in ipairs(brushes) do
    for _, f in ipairs(b.faces) do
      if f.normal.y > 0.5 then
        f:set_flag("show_sky", true)
        f:set_texture(kit.defaults.sky_texture) -- unseen in game (the sky room shows), but reads as sky in the editor
        n = n + 1
      end
    end
  end
  return n
end
I.sky_up = sky_up

local function ensure_sky(name)
  if level.objects_tagged("kit", "sky_room").count == 0 then return kit.sky_room{ name = name .. "_sky" } end
  return nil
end
I.ensure_sky = ensure_sky

---@kit kit.road{name?, path, width?, height?, sky?, tunnels?, tunnel_height?, max_grade?, texture?|textures?} → road — A wide road cut along a floor path on any heading: one sloped or level air prism per leg and a level pad at every turn (at most 90°, half the road width deep on each side of the turn point), so grades and turns meet on identical faces. Legs listed in tunnels get a lower roof and no sky. A leg's grade is its rise over its sloped stretch: the leg's length less the level pads at its ends (grades[i]); warns when a grade is steeper than max_grade. The payload cart route.
---@opt name: string = road<n> — Names the road (tags).
---@opt path: vec[] — Required: 2 to 64 floor points; each leg at least width + 2 m long; a leg that climbs must climb at least 2% (a level leg is exactly level).
---@opt width: number = 8 — Road width, metres (the cart plus 2-3 players abreast).
---@opt height: number = 8 — Height of the cut's walls above the road, metres (the design grader's median corridor height limit is 8.5 m).
---@opt sky: boolean = true — Open the road to the sky (show_sky tops plus a kit.sky_room when the level has none).
---@opt tunnels: integer[] = none — Leg numbers (leg i runs from path point i to i + 1) roofed as tunnels.
---@opt tunnel_height: number = 6 — Clear height inside a tunnel leg, metres.
---@opt max_grade: number = 0.15 — The steepest grade before a warning: rise over the sloped stretch, which excludes the level turn pads (a 4.5 m climb on a 35 m leg between two turns slopes over 27 m: 17%). Uphill pushes are the tense moments; 45° is the hard limit.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.road(t)
  local api = "kit.road"
  check_keys(api, t, { "name", "path", "width", "height", "sky", "tunnels", "tunnel_height", "max_grade", "texture", "textures" })
  local name = name_for(api, "road", t.name)
  if type(t.path) ~= "table" or #t.path < 2 or #t.path > 64 then fail(api, "'path' must list 2 to 64 floor points.") end
  local pts = {}
  for i, p in ipairs(t.path) do
    local x, y, z = vec3(api, "path[" .. i .. "]", p)
    pts[i] = { x, y, z }
  end
  local width = positive(api, "width", t.width, 8)
  I.check_walkable(api, "road", width, nil, false)
  local height = positive(api, "height", t.height, 8)
  local tunnel_h = positive(api, "tunnel_height", t.tunnel_height, 6)
  local max_grade = positive(api, "max_grade", t.max_grade, 0.15)
  local tunnels = {}
  if t.tunnels ~= nil then
    if type(t.tunnels) ~= "table" then fail(api, "'tunnels' must list leg numbers.") end
    for _, i in ipairs(t.tunnels) do
      if type(i) ~= "number" or i < 1 or i >= #pts or i ~= math.floor(i) then fail(api, "'tunnels' entries must be leg numbers 1 to " .. (#pts - 1) .. ".") end
      tunnels[i] = true
    end
  end
  local hw = width / 2
  local n = #pts
  local dirs = {}
  for i = 1, n - 1 do
    local dx, dz = pts[i + 1][1] - pts[i][1], pts[i + 1][3] - pts[i][3]
    local l = math.sqrt(dx * dx + dz * dz)
    if l < width + 2 - EPS then fail(api, "leg " .. i .. " is " .. fmt(l) .. " m long; legs need at least width + 2 = " .. fmt(width + 2) .. " m.") end
    dirs[i] = { dx / l, dz / l, l }
  end
  for i = 2, n - 1 do
    local a, b = dirs[i - 1], dirs[i]
    local dot = a[1] * b[1] + a[2] * b[2]
    if dot < -EPS then fail(api, "the road turns more than 90° at path point " .. i .. ".", "Add a point to split the turn.") end
  end
  local tex = tex_opts(t)
  local o = { air = true, texture = tex.texture, textures = tex.textures }
  local brushes, open = {}, {}
  local warnings = {}
  local grades = {}
  local function leftn(d) return { -d[2], d[1] } end
  -- Leg end segments: start/end trimmed by hw at interior points (where the pads sit).
  for i = 1, n - 1 do
    local p, q, d = pts[i], pts[i + 1], dirs[i]
    local s0 = i > 1 and hw or 0
    local s1 = i < n - 1 and hw or 0
    local nl = leftn(d)
    local a = { p[1] + d[1] * s0, p[3] + d[2] * s0 }
    local b = { q[1] - d[1] * s1, q[3] - d[2] * s1 }
    local ya, yb = p[2], q[2]
    local run = d[3] - s0 - s1
    local grade = (yb - ya) / run
    if math.abs(yb - ya) > EPS and math.abs(grade) < 0.02 then
      fail(api, "leg " .. i .. " climbs only " .. fmt(math.abs(grade) * 100) .. "%; a climbing leg needs at least 2% (or make it exactly level).")
    end
    if math.abs(grade) > kit.limits.max_slope + EPS then fail(api, "leg " .. i .. " is steeper than 45°.") end
    if math.abs(grade) > max_grade + EPS then
      local pads = s0 + s1
      warn(api, warnings, "leg " .. i .. " climbs " .. fmt(math.abs(yb - ya)) .. " m over " .. fmt(run) .. " m of slope (" ..
        (pads > 0 and ("its " .. fmt(d[3]) .. " m less " .. fmt(pads) .. " m of level turn pads") or "the whole leg") .. "): a " ..
        fmt(math.abs(grade) * 100) .. "% grade, steeper than " .. fmt(max_grade * 100) .. "%. Lengthen the leg to at least " ..
        fmt(math.ceil((math.abs(yb - ya) / max_grade + pads) * 2) / 2) .. " m or climb less.")
    end
    grades[i] = grade
    local h = tunnels[i] and tunnel_h or height
    local quad = {
      { a[1] - nl[1] * hw, ya, a[2] - nl[2] * hw }, { a[1] + nl[1] * hw, ya, a[2] + nl[2] * hw },
      { b[1] - nl[1] * hw, yb, b[2] - nl[2] * hw }, { b[1] + nl[1] * hw, yb, b[2] + nl[2] * hw },
    }
    local hp = {}
    for _, c in ipairs(quad) do
      hp[#hp + 1] = c
      hp[#hp + 1] = { c[1], c[2] + h, c[3] }
    end
    local br = hull("road", name, "leg" .. i, hp, o)
    brushes[#brushes + 1] = br
    if not tunnels[i] then open[#open + 1] = br end
  end
  for i = 2, n - 1 do
    local p, d1, d2 = pts[i], dirs[i - 1], dirs[i]
    local n1, n2 = leftn(d1), leftn(d2)
    local plan = {
      { p[1] - d1[1] * hw - n1[1] * hw, p[3] - d1[2] * hw - n1[2] * hw }, { p[1] - d1[1] * hw + n1[1] * hw, p[3] - d1[2] * hw + n1[2] * hw },
      { p[1] + d2[1] * hw - n2[1] * hw, p[3] + d2[2] * hw - n2[2] * hw }, { p[1] + d2[1] * hw + n2[1] * hw, p[3] + d2[2] * hw + n2[2] * hw },
    }
    local turn = d1[1] * d2[2] - d1[2] * d2[1]
    if math.abs(turn) > 1e-6 then
      local s = turn > 0 and -1 or 1 -- the outer side
      local mx, mz = s * (n1[1] + n2[1]), s * (n1[2] + n2[2])
      local ml = math.sqrt(mx * mx + mz * mz)
      local c = (d1[1] * d2[1] + d1[2] * d2[2])
      local k = hw / math.sqrt((1 + c) / 2)
      plan[#plan + 1] = { p[1] + mx / ml * k, p[3] + mz / ml * k }
    end
    local hp = {}
    for _, c in ipairs(plan) do
      hp[#hp + 1] = { c[1], p[2], c[2] }
      hp[#hp + 1] = { c[1], p[2] + height, c[2] }
    end
    local br = hull("road", name, "pad" .. i, hp, o)
    brushes[#brushes + 1] = br
    open[#open + 1] = br
  end
  local sky = nil
  if boolean(api, "sky", t.sky, true) then
    sky_up(open)
    sky = ensure_sky(name)
  end
  local stations, length = {}, 0
  for i, p in ipairs(pts) do
    if i > 1 then length = length + dirs[i - 1][3] end
    local d = dirs[math.min(i, n - 1)]
    stations[i] = { pos = vec(p[1], p[2], p[3]), floor_y = p[2], width = width, height = (tunnels[math.min(i, n - 1)] and tunnel_h or height), heading = math.deg(math.atan2(d[1], d[2])) }
  end
  local lo = { math.huge, math.huge, math.huge }
  local hi = { -math.huge, -math.huge, -math.huge }
  for _, b in ipairs(brushes) do
    local bb = b.bounds
    lo = { math.min(lo[1], bb.min.x), math.min(lo[2], bb.min.y), math.min(lo[3], bb.min.z) }
    hi = { math.max(hi[1], bb.max.x), math.max(hi[2], bb.max.y), math.max(hi[3], bb.max.z) }
  end
  return {
    kind = "road", name = name, brushes = brushes, stations = stations, length = length, grades = grades, width = width,
    height = height, sky = sky, floor_y = pts[1][2], start = stations[1].pos, ["end"] = stations[n].pos,
    min = vec(lo[1], lo[2], lo[3]), max = vec(hi[1], hi[2], hi[3]), warnings = warnings,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Organic moderation and the cave-mouth check (spec c.1-7, c.1-8)
-- ---------------------------------------------------------------------------------------------

local BUILT = { beam = true, truss = true, bridge = true, platform = true, landmark = true, stairs = true, floor_section = true, pilasters = true }

-- The level-floor share of a tube, from its stations (no level queries).
local function floor_shares(r)
  local stations = r.stations or {}
  local area, flat = 0, 0
  for i = 2, #stations do
    local a, b = stations[i - 1], stations[i]
    local l = math.sqrt((b.pos.x - a.pos.x) ^ 2 + (b.pos.z - a.pos.z) ^ 2)
    local w = (a.width + b.width) / 2
    area = area + l * w
    if math.abs(b.floor_y - a.floor_y) < 0.01 then flat = flat + l * w end
  end
  return area, area > 0 and flat / area or 1
end

local function organic_report(r)
  local area, flat_share = floor_shares(r)
  local built = 0
  if r.min ~= nil and r.max ~= nil then
    local q = level.brushes_in{ min = { r.min.x, r.min.y, r.min.z }, max = { r.max.x, r.max.y, r.max.z } }
    local seen = {}
    for _, b in ipairs(q.brushes) do
      if b.detail or b.solid then
        local tags = b:tags()
        if BUILT[tags.kit or ""] then
          local key = (tags.kit or "") .. ":" .. (tags["kit.name"] or "") .. ":" .. (tags["kit.part"] or b.uid)
          if not seen[key] then seen[key] = true built = built + 1 end
        end
      end
    end
  end
  return {
    area = area, flat_share = flat_share, sloped_share = 1 - flat_share, built = built,
    built_per_100m2 = area > 0 and built / area * 100 or 0,
  }
end

---@kit kit.organic_report{along} → organic_report — Organic moderation of a kit.cave or kit.canyon record (spec c.1-8): its floor area, flat_share (level floor, target at least 0.3), sloped_share, and the built elements standing in it (kit beams, trusses, bridges, platforms, stairs...) per 100 m² (target at least 1), with ok and warnings.
---@opt along: record — Required: a kit.cave, kit.canyon or kit.passage record.
function kit.organic_report(t)
  local api = "kit.organic_report"
  check_keys(api, t, { "along" })
  local r = t.along
  if type(r) ~= "table" or type(r.stations) ~= "table" then fail(api, "'along' must be a kit.cave, kit.canyon or kit.passage record.") end
  local rep = organic_report(r)
  rep.warnings = {}
  if rep.flat_share < 0.3 then
    rep.warnings[#rep.warnings + 1] = string.format("only %d%% of %s's floor is level; organic zones keep at least 30%% flat fighting floor.", math.floor(rep.flat_share * 100 + 0.5), r.name or "the tube")
  end
  if rep.built_per_100m2 < 1 then
    rep.warnings[#rep.warnings + 1] = string.format("%s has %.2f built elements per 100 m²; give organic zones at least 1 (kit.beam, kit.truss, kit.bridge, kit.platform).", r.name or "the tube", rep.built_per_100m2)
  end
  rep.ok = #rep.warnings == 0
  return rep
end

-- The cave-mouth warning, wrapped around kit.cave and kit.canyon: a path end inside a kit room whose
-- mouth is wider than 0.8x the room's clear width (or, for a cave, a crown taller than 0.8x its headroom)
-- merges the two spaces; and a tube with less than 30% level floor breaks the organic-moderation rule.
local function wrap_organic(fn_name, mouth, crown)
  local base = kit[fn_name]
  kit[fn_name] = function(t)
    local r = base(t)
    local api = "kit." .. fn_name
    local warnings = {}
    local st = r.stations
    if type(st) == "table" and #st > 0 then
      -- Two queries per tube (kit rooms and shaped rooms); a room's tags are read only when it warns.
      local rooms = {}
      for _, kind in ipairs({ "room", "shaped_room" }) do
        for _, b in ipairs(level.brushes_tagged("kit", kind).brushes) do
          if b.air then rooms[#rooms + 1] = b end
        end
      end
      for _, s in ipairs({ st[1], st[#st] }) do
        local p, y = s.pos, s.floor_y + 0.5
        for _, b in ipairs(rooms) do
          local bb = b.bounds
          if p.x > bb.min.x and p.x < bb.max.x and p.z > bb.min.z and p.z < bb.max.z and y > bb.min.y and y < bb.max.y then
            local room = { name = "", x0 = bb.min.x, x1 = bb.max.x, z0 = bb.min.z, z1 = bb.max.z, y1 = bb.max.y, floor = bb.min.y }
            local width, top = mouth(t, s), crown and (s.floor_y + s.height) or -math.huge
            local cw = math.min(room.x1 - room.x0, room.z1 - room.z0)
            if width > 0.8 * cw + EPS or top - room.floor > 0.8 * (room.y1 - room.floor) + EPS then
              room.name = b:tags()["kit.name"] or ("brush#" .. b.uid)
              I.mouth_check(api, (r.name or fn_name) .. "'s mouth", width, top, { room }, warnings)
            end
          end
        end
      end
    end
    local _, flat_share = floor_shares(r)
    if flat_share < 0.3 - EPS then
      warn(api, warnings, string.format("only %d%% of %s's floor is level; organic zones keep at least 30%% flat fighting floor.", math.floor(flat_share * 100 + 0.5), r.name or fn_name))
    end
    r.warnings = warnings
    return r
  end
end
wrap_organic("cave", function(t, s) return s.width / ((t.floor_flatness or 0.6)) * (1 + (t.roughness or kit.defaults.roughness)) end, true)
-- A canyon is open to the sky: its rim must stay under the room's ceiling (kit.canyon refuses otherwise), so only its
-- width can merge the two spaces.
wrap_organic("canyon", function(t, s) return s.width * (1 + (t.roughness or kit.defaults.roughness)) end, false)

-- ---------------------------------------------------------------------------------------------
-- kit.space: the archetype library
-- ---------------------------------------------------------------------------------------------

-- Lights on a grid inside a plan polygon, kept clear of solids standing in the space.
local function plan_lights(name, poly, floor_y, height, spacing, color)
  local x0, z0, x1, z1 = bbox(poly)
  local nx = math.max(1, math.floor((x1 - x0) / spacing + 0.5))
  local nz = math.max(1, math.floor((z1 - z0) / spacing + 0.5))
  local lights = {}
  local y = floor_y + height
  for i = 0, nx - 1 do
    for k = 0, nz - 1 do
      local x, z = x0 + (i + 0.5) * (x1 - x0) / nx, z0 + (k + 0.5) * (z1 - z0) / nz
      if inside(poly, x, z) then
        local clear = true
        local q = level.brushes_in{ min = { x - 0.5, y - 0.5, z - 0.5 }, max = { x + 0.5, y + 0.5, z + 0.5 } }
        for _, b in ipairs(q.brushes) do if b.solid and b:contains(x, y, z) then clear = false end end
        if clear then
          lights[#lights + 1] = level.place{
            kind = "light", name = name .. "_light" .. (#lights + 1), pos = { x, y, z },
            props = { color = color or kit.defaults.light_color, size_range = math.max(kit.defaults.light_range_min, spacing * 1.3) },
            tags = { kit = "space", ["kit.name"] = name },
          }
        end
      end
    end
  end
  return lights
end

local function unpack3(p) return p[1], p[2], p[3] end

local function rect_poly(x0, z0, x1, z1) return { { x0, z0 }, { x1, z0 }, { x1, z1 }, { x0, z1 } } end

-- The context every recipe gets: frame F, size w x d, floor y, textures, the name prefix and collectors.
local function new_ctx(api, name, F, w, d, t)
  local ctx = { api = api, name = name, F = F, w = w, d = d, y = F.y, t = t, brushes = {}, parts = {}, lights = {}, warnings = {}, anchors = {} }
  ctx.tex = tex_opts(t)
  ctx.trim = t.trim_texture ~= nil and { texture = t.trim_texture } or ctx.tex
  function ctx.n(part) return name .. "_" .. part end
  function ctx.add(part, rec)
    ctx.parts[part] = rec
    if type(rec) == "table" then
      if type(rec.brushes) == "table" then for _, b in ipairs(rec.brushes) do ctx.brushes[#ctx.brushes + 1] = b end
      elseif rec.brush ~= nil then ctx.brushes[#ctx.brushes + 1] = rec.brush end
      if type(rec.envelope) == "userdata" then
        local dup = false
        for _, b in ipairs(ctx.brushes) do if b.uid == rec.envelope.uid then dup = true end end
        if not dup then ctx.brushes[#ctx.brushes + 1] = rec.envelope end
      end
      if type(rec.warnings) == "table" then for _, m in ipairs(rec.warnings) do ctx.warnings[#ctx.warnings + 1] = m end end
    elseif type(rec) == "userdata" then
      ctx.brushes[#ctx.brushes + 1] = rec
    end
    return rec
  end
  function ctx.b(part, rec) return ctx.add(part, rec) end
  -- textures for kit calls
  function ctx.tx(o)
    o = o or {}
    if ctx.tex.texture ~= nil then o.texture = ctx.tex.texture elseif ctx.tex.textures ~= nil then o.textures = ctx.tex.textures end
    return o
  end
  function ctx.trimx(o)
    o = o or {}
    if ctx.trim.texture ~= nil then o.texture = ctx.trim.texture elseif ctx.trim.textures ~= nil then o.textures = ctx.trim.textures end
    return o
  end
  function ctx.anchor(kind, x, y, z, dir, width, height)
    ctx.anchors[#ctx.anchors + 1] = { kind = kind, pos = vec(unpack3(F.p(x, y, z))), dir = dir and F.d(dir) or nil, width = width, height = height }
  end
  return ctx
end

-- A detail or solid box in local coordinates (x0, y0, z0)-(x1, y1, z1).
local function lbox(ctx, part, x0, y0, z0, x1, y1, z1, o)
  local a, b = ctx.F.p(x0, y0, z0), ctx.F.p(x1, y1, z1)
  local br = hull("space", ctx.name, part, box_points(math.min(a[1], b[1]), math.min(a[2], b[2]), math.min(a[3], b[3]),
    math.max(a[1], b[1]), math.max(a[2], b[2]), math.max(a[3], b[3])), o)
  ctx.add(part, br)
  return br
end

-- A prism over a local polygon.
local function lprism(ctx, part, poly, y0, y1, o)
  local world = ctx.F.poly(poly)
  if area2(world) < 0 then local r = {} for i = #world, 1, -1 do r[#r + 1] = world[i] end world = r end
  local br = prism("space", ctx.name, part, snap16(world), ctx.y + y0, ctx.y + y1, o)
  ctx.add(part, br)
  return br
end

-- The quads of a band of width bw inside a convex local polygon (one per edge), optionally skipping edges.
local function band(poly, bw, skip)
  local inner = inset_convex(poly, bw)
  local quads = {}
  if inner == nil then return quads end
  for i = 1, #poly do
    if not (skip and skip(i, poly[i], poly[i % #poly + 1])) then
      quads[#quads + 1] = { poly[i], poly[i % #poly + 1], inner[i % #poly + 1], inner[i] }
    end
  end
  return quads, inner
end

-- An edge kerb and railing along a local polygon's edges (open-air edge treatment), skipping gaps.
local function edge_treatment(ctx, poly, y, gaps)
  local n = 0
  local inner = inset_convex(poly, 0.45)
  for i = 1, #poly do
    local a, b = poly[i], poly[i % #poly + 1]
    local ia, ib = inner[i], inner[i % #poly + 1]
    local gap = gaps and gaps(i, a, b)
    if not gap then
      n = n + 1
      -- Kerb: a low detail strip along the edge.
      local q = { a, b, ib, ia }
      lprism(ctx, "kerb" .. n, q, y, y + 0.25, { detail = true, texture = ctx.trim.texture, textures = ctx.trim.textures })
      -- Posts every ~2.5 m and a hand rail at 1.05 m.
      local ex, ez = ib[1] - ia[1], ib[2] - ia[2]
      local l = math.sqrt(ex * ex + ez * ez)
      local k = math.max(1, math.floor(l / 2.5 + 0.5))
      for j = 0, k do
        local f = j / k
        local px, pz = ia[1] + ex * f, ia[2] + ez * f
        local p0, p1 = ctx.F.p(px, y + 0.25, pz), ctx.F.p(px, y + 1.1, pz)
        ctx.add("post" .. n .. "_" .. j, I.member("space", ctx.name, "post" .. n .. "_" .. j, p0, p1, 0.12, 0.12, ctx.trim))
      end
      ctx.add("rail" .. n, I.member("space", ctx.name, "rail" .. n, ctx.F.p(ia[1], y + 1.05, ia[2]), ctx.F.p(ib[1], y + 1.05, ib[2]), 0.1, 0.1, ctx.trim))
    end
  end
end

-- A floating slab with a tapering rocky underside (open-air platforms): a structural prism and a
-- structural frustum below it.
local function floating(ctx, part, poly, top, thick, under)
  lprism(ctx, part .. "_slab", poly, top - thick, top, ctx.tx({}))
  local cx, cz = centroid(poly)
  local pts = {}
  for _, q in ipairs(poly) do
    local a = ctx.F.p(q[1], top - thick, q[2])
    pts[#pts + 1] = a
    local b = ctx.F.p(cx + (q[1] - cx) * 0.35, top - thick - under, cz + (q[2] - cz) * 0.35)
    pts[#pts + 1] = b
  end
  local br = hull("space", ctx.name, part .. "_under", pts, ctx.tx({}))
  ctx.add(part .. "_under", br)
end

-- A big open-air void: a structural air box whose every face shows the sky.
local function void(ctx, x0, y0, z0, x1, y1, z1)
  local br = lbox(ctx, "void", x0, y0, z0, x1, y1, z1, { air = true, texture = kit.defaults.sky_texture })
  br:box_map{ tile = 64 }
  for _, f in ipairs(br.faces) do f:set_flag("show_sky", true) end
  local sky = ensure_sky(ctx.name)
  if sky then ctx.parts.sky = sky end
  return br
end

local ARCH = {}
local ORDER = {}
local function archetype(name, class, w, d, minw, mind, summary, build)
  ARCH[name] = { name = name, class = class, w = w, d = d, minw = minw, mind = mind, summary = summary, build = build }
  ORDER[#ORDER + 1] = name
end

-- ---- indoor ---------------------------------------------------------------------------------

archetype("hall", "indoor", 24, 16, 16, 12,
  "A hall with chamfered corners, pilasters every 4 m, a raised clerestory bay (6 m aisles, 10 m bay) and a 1 m dais with steps.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("room"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 2,
      height = 6, ceiling = "stepped", rise = 4, region = { -w / 2 + 4, -d / 2 + 3, w / 2 - 4, d / 2 - 3 } }))
    c.add("pilasters", kit.pilasters(c.trimx{ name = c.n("pilasters"), room = room, sides = { F.d("+z"), F.d("-z") }, spacing = 4, depth = 0.5, width = 0.8 }))
    local x0, z0, x1, z1 = F.rect(w / 2 - 3.5, -d / 2 + 3, w / 2 - 0.5, d / 2 - 3)
    c.add("dais", kit.floor_section(c.tx{ name = c.n("dais"), room = room, min = { x0, 0, z0 }, max = { x1, 0, z1 }, rise = 1 }))
    c.add("dais_steps", kit.stairs(c.tx{ name = c.n("dais_steps"), pos = F.p(w / 2 - 5.5, 0, 0), dir = F.d("+x"), rise = 1, width = 4, group = false, collision_ramp = false }))
    c.anchor("door", -w / 2, 0, 0, "-x", 3, 4)
    c.anchor("door", 0, 0, -d / 2, "-z", 3, 4)
    c.anchor("door", 0, 0, d / 2, "+z", 3, 4)
    c.light_poly, c.light_h = room.outline, 5
  end)

archetype("atrium", "indoor", 28, 28, 24, 24,
  "A two-storey octagonal atrium 14 m tall: a 3 m gallery ring at 6 m on posts, a stair up to it and a 22 m truss catwalk crossing the void.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local cut = math.floor(math.min(w, d) * 0.25 * 4 + 0.5) / 4
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("room"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = cut, height = 14 }))
    local outer = chamfer(rect_poly(-w / 2, -d / 2, w / 2, d / 2), cut)
    local gw = 3
    local quads, inner = band(outer, gw)
    for i, q in ipairs(quads) do lprism(c, "gallery" .. i, q, 5.5, 6, { detail = true, texture = c.tex.texture, textures = c.tex.textures }) end
    for i, q in ipairs(inner) do
      local p0, p1 = F.p(q[1], 0, q[2]), F.p(q[1], 5.5, q[2])
      c.add("post" .. i, I.member("space", c.name, "post" .. i, p0, p1, 0.5, 0.5, c.trim))
    end
    local ix = w / 2 - gw
    c.add("stairs", kit.stairs(c.tx{ name = c.n("stairs"), pos = F.p(ix - 12, 0, 0), dir = F.d("+x"), rise = 6, width = 3, group = false }))
    local cz = d / 2 - gw + 0.5
    c.add("catwalk", kit.bridge{ name = c.n("catwalk"), from = F.p(-4, 6.125, -cz), to = F.p(-4, 6.125, cz), width = 3,
      style = "truss", depth = 1.6, landmark = false, group = false, texture = c.tex.texture, textures = c.tex.textures, frame_texture = c.trim.texture })
    c.anchor("door", -w / 2, 0, 0, "-x", 3, 4)
    c.anchor("door", w / 2, 6, 0, "+x", 3, 4)
    c.light_polys = { { poly = room.outline, h = 10, world = true }, { poly = inner, h = 4.5 } }
  end)

archetype("arena", "indoor", 32, 32, 26, 26,
  "An arena hall after dm12: 8 m walls, a 12 m raised centre bay, a 10 x 10 x 3 m central block with a tunnel through it and ramps onto it, and four columns.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("room"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 4,
      height = 8, ceiling = "stepped", rise = 4, inset = 7 }))
    lbox(c, "block", -5, 0, -5, 5, 3, 5, c.tx({}))
    lbox(c, "tunnel", -5.5, 0, -1.5, 5.5, 2.5, 1.5, c.tx({ air = true }))
    c.add("ramp_s", kit.ramp(c.tx{ name = c.n("ramp_s"), pos = F.p(0, 0, -11), dir = F.d("+z"), rise = 3, run = 6, width = 3, group = false }))
    c.add("ramp_n", kit.ramp(c.tx{ name = c.n("ramp_n"), pos = F.p(0, 0, 11), dir = F.d("-z"), rise = 3, run = 6, width = 3, group = false }))
    local q = w / 2 - 7
    for i, s in ipairs({ { -q, -q }, { q, -q }, { q, q }, { -q, q } }) do
      local p = F.p(s[1], 0, s[2])
      c.add("column" .. i, kit.pillar_row(c.trimx{ name = c.n("column" .. i), from = p, count = 1, height = 8, size = 1.2, shape = "cylinder", group = false }))
    end
    for _, s in ipairs({ { "-x", -w / 2, 0 }, { "+x", w / 2, 0 }, { "-z", 0, -d / 2 }, { "+z", 0, d / 2 } }) do c.anchor("door", s[2], 0, s[3], s[1], 4, 4) end
    c.light_poly, c.light_h = room.outline, 6.5
  end)

archetype("ring", "indoor", 36, 36, 30, 30,
  "Two stacked ring corridors (5 m wide, 4.5 m high, 6 m apart) around a shared solid core, joined at the corners by 12 x 12 m, 10.5 m double-height bays (hub ratio 2.3) crossed by 3 m catwalks, and by two carved stairs.",
  function(c)
    local w, F = c.w, c.F
    local s, cw, bay, walk = w / 2, 5, 12, 3
    local storeys = { 0, 6 }
    -- Legs (both storeys), then the double-height corner bays.
    for k, y0 in ipairs(storeys) do
      lbox(c, "leg_n" .. k, -(s - bay), y0, s - cw, s - bay, y0 + 4.5, s, c.tx({ air = true }))
      lbox(c, "leg_s" .. k, -(s - bay), y0, -s, s - bay, y0 + 4.5, -(s - cw), c.tx({ air = true }))
      lbox(c, "leg_e" .. k, s - cw, y0, -(s - bay), s, y0 + 4.5, s - bay, c.tx({ air = true }))
      lbox(c, "leg_w" .. k, -s, y0, -(s - bay), -(s - cw), y0 + 4.5, s - bay, c.tx({ air = true }))
    end
    local bays = {}
    for i, q in ipairs({ { 1, 1 }, { -1, 1 }, { -1, -1 }, { 1, -1 } }) do
      -- The bay square with only its outer corner cut.
      local sx, sz = q[1], q[2]
      local sq = { { (s - bay) * sx, (s - bay) * sz }, { s * sx, (s - bay) * sz }, { s * sx, (s - 3) * sz }, { (s - 3) * sx, s * sz }, { (s - bay) * sx, s * sz } }
      if area2(sq) < 0 then local r = {} for j = #sq, 1, -1 do r[#r + 1] = sq[j] end sq = r end
      bays[i] = sq
      lprism(c, "bay" .. i, sq, 0, 10.5, c.tx({ air = true }))
    end
    -- Catwalks carry the upper ring through each bay (an L along the outer band).
    for i, q in ipairs({ { 1, 1 }, { -1, 1 }, { -1, -1 }, { 1, -1 } }) do
      local sx, sz = q[1], q[2]
      local function rect(ax, az, bx, bz)
        local x0, x1 = math.min(ax * sx, bx * sx), math.max(ax * sx, bx * sx)
        local z0, z1 = math.min(az * sz, bz * sz), math.max(az * sz, bz * sz)
        return rect_poly(x0, z0, x1, z1)
      end
      lprism(c, "catwalk" .. i .. "a", rect(s - bay, s - walk, s, s), 5.5, 6, { detail = true, texture = c.tex.texture, textures = c.tex.textures })
      lprism(c, "catwalk" .. i .. "b", rect(s - walk, s - bay, s, s - walk), 5.5, 6, { detail = true, texture = c.tex.texture, textures = c.tex.textures })
    end
    c.add("stairs_n", kit.stairs(c.tx{ name = c.n("stairs_n"), pos = F.p(-4.8, 0, s - cw + 1.25), dir = F.d("+x"), rise = 6, tread = 0.4, width = 2.5, carve = true, headroom = 4.5, group = false }))
    c.add("stairs_s", kit.stairs(c.tx{ name = c.n("stairs_s"), pos = F.p(4.8, 0, -(s - cw + 1.25)), dir = F.d("-x"), rise = 6, tread = 0.4, width = 2.5, carve = true, headroom = 4.5, group = false }))
    c.anchor("door", 0, 0, s, "+z", 3, 4)
    c.anchor("door", 0, 6, -s, "-z", 3, 4)
    c.anchor("door", s, 0, 0, "+x", 3, 4)
    c.anchor("door", -s, 6, 0, "-x", 3, 4)
    c.light_polys = {}
    for _, b in ipairs(bays) do c.light_polys[#c.light_polys + 1] = { poly = b, h = 9 } end
    c.light_polys[#c.light_polys + 1] = { poly = rect_poly(-(s - bay), s - cw, s - bay, s), h = 3.5 }
    c.light_polys[#c.light_polys + 1] = { poly = rect_poly(-(s - bay), -s, s - bay, -(s - cw)), h = 3.5 }
    c.light_polys[#c.light_polys + 1] = { poly = rect_poly(-(s - bay), s - cw, s - bay, s), h = 9.5 }
    c.light_polys[#c.light_polys + 1] = { poly = rect_poly(-(s - bay), -s, s - bay, -(s - cw)), h = 9.5 }
    c.main = { min = vec(unpack3(F.p(-s, 0, -s))), max = vec(unpack3(F.p(s, 10.5, s))) }
  end)

archetype("sunken_court", "indoor", 40, 40, 34, 34,
  "An octagonal hall (8 m) around a court sunk 5 m (after E22): a 17 m lantern over the court, four stair flights down, low cover blocks and a raised centre plinth.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("hall"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, shape = "octagon", chamfer = 8, height = 8 }))
    local cw = w - 14
    c.add("court", kit.shaped_room(c.tx{ name = c.n("court"), pos = F.p(0, -5, 0), size = { cw, cw }, facing = F.facing, chamfer = 6, height = 17 }))
    local e = cw / 2
    for _, s in ipairs({ { "+x", e - 8, 0 }, { "-x", -(e - 8), 0 }, { "+z", 0, e - 8 }, { "-z", 0, -(e - 8) } }) do
      c.add("stairs" .. s[1], kit.stairs(c.tx{ name = c.n("stairs" .. (s[1] == "+x" and "e" or s[1] == "-x" and "w" or s[1] == "+z" and "n" or "s")),
        pos = F.p(s[2], -5, s[3]), dir = F.d(s[1]), rise = 5, tread = 0.4, width = 3, group = false }))
    end
    for i, q in ipairs({ { 6, 6 }, { -6, 6 }, { -6, -6 }, { 6, -6 } }) do
      lbox(c, "cover" .. i, q[1] - 1.5, -5, q[2] - 0.6, q[1] + 1.5, -3.8, q[2] + 0.6, c.tx({}))
    end
    lbox(c, "plinth", -2.5, -5, -2.5, 2.5, -4.25, 2.5, c.tx({}))
    for _, s in ipairs({ { "-x", -w / 2, 0 }, { "+x", w / 2, 0 }, { "-z", 0, -d / 2 }, { "+z", 0, d / 2 } }) do c.anchor("door", s[2], 0, s[3], s[1], 3, 4) end
    c.light_polys = { { poly = room.outline, h = 6, world = true }, { poly = chamfer(rect_poly(-e, -e, e, e), 6), h = 3.5, y = c.y - 5 } }
  end)

archetype("stair_hall", "indoor", 16, 18, 14, 16,
  "A 16 m stair tower with chamfered corners: switchback flights up to landings at 5 m and 10 m (doorway anchors on each level).",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("room"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 3, height = 16 }))
    lbox(c, "landing1", -w / 2, 4.5, d / 2 - 4, w / 2, 5, d / 2, { detail = true, texture = c.tex.texture, textures = c.tex.textures })
    lbox(c, "landing2", -w / 2, 9.5, -d / 2, w / 2, 10, -d / 2 + 6, { detail = true, texture = c.tex.texture, textures = c.tex.textures })
    c.add("flight1", kit.stairs(c.tx{ name = c.n("flight1"), pos = F.p(-w / 2 + 2.5, 0, d / 2 - 12), dir = F.d("+z"), rise = 5, tread = 0.4, width = 3, group = false }))
    c.add("flight2", kit.stairs(c.tx{ name = c.n("flight2"), pos = F.p(w / 2 - 2.5, 5, d / 2 - 4), dir = F.d("-z"), rise = 5, tread = 0.4, width = 3, group = false }))
    c.anchor("door", -w / 2, 0, -d / 2 + 4, "-x", 3, 4)
    c.anchor("door", 0, 5, d / 2, "+z", 3, 4)
    c.anchor("door", 0, 10, -d / 2, "-z", 3, 4)
    c.light_polys = {
      { poly = rect_poly(-w / 2 + 2, -d / 2 + 6.5, w / 2 - 2, d / 2 - 4.5), h = 3.5 },
      { poly = rect_poly(-w / 2 + 3, d / 2 - 4, w / 2 - 3, d / 2), h = 8.5 },
      { poly = rect_poly(-w / 2 + 3, -d / 2, w / 2 - 3, -d / 2 + 6), h = 13.5 },
    }
    c.light_spacing = 6
  end)

archetype("courtyard", "indoor", 30, 22, 24, 18,
  "A courtyard under the sky (12 m walls, chamfered corners) with a 4.5 m colonnaded arcade along both long sides and a raised planter.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("court"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 3, height = 12 }))
    I.sky_up(room.brushes)
    local sky = ensure_sky(c.name)
    if sky then c.parts.sky = sky end
    for _, sz in ipairs({ 1, -1 }) do
      local tag = sz > 0 and "n" or "s"
      local z0, z1 = sz > 0 and d / 2 - 0.5 or -d / 2 - 4, sz > 0 and d / 2 + 4 or -d / 2 + 0.5
      lbox(c, "arcade_" .. tag, -w / 2 + 3, 0, z0, w / 2 - 3, 4.5, z1, c.tx({ air = true }))
    end
    for _, sz in ipairs({ 1, -1 }) do
      local tag = sz > 0 and "n" or "s"
      local n = math.floor((w - 8) / 4 + 0.5)
      c.add("columns_" .. tag, kit.pillar_row(c.trimx{ name = c.n("columns_" .. tag), from = F.p(-(n * 4) / 2, 0, sz * d / 2), to = F.p((n * 4) / 2, 0, sz * d / 2),
        count = n + 1, height = 4.5, size = 0.8, shape = "cylinder", group = false }))
    end
    local x0, z0, x1, z1 = F.rect(-3, -2, 3, 2)
    c.add("planter", kit.floor_section(c.tx{ name = c.n("planter"), room = room, min = { x0, 0, z0 }, max = { x1, 0, z1 }, rise = 0.75 }))
    c.anchor("door", -w / 2, 0, 0, "-x", 4, 4)
    c.anchor("door", w / 2, 0, 0, "+x", 4, 4)
    c.light_polys = {
      { poly = rect_poly(-w / 2 + 3, d / 2, w / 2 - 3, d / 2 + 4), h = 3.5 },
      { poly = rect_poly(-w / 2 + 3, -d / 2 - 4, w / 2 - 3, -d / 2), h = 3.5 },
      { poly = rect_poly(-w / 2 + 4, -d / 2 + 3, w / 2 - 4, d / 2 - 3), h = 8 },
    }
    c.light_spacing = 8
    c.main = room
  end)

archetype("canyon_bridge", "indoor", 20, 72, 16, 60,
  "A 72 m sky canyon (20 m floor, 22 m rim) crossed 12 m up by a ~28 m through-truss bridge between two side passages and rooms, with a flat-topped ledge beside the floor route (under, beside, across).",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local canyon = c.add("canyon", kit.canyon{ name = c.n("canyon"), path = { F.p(0, 0, -d / 2), F.p(3, 0, -d / 6), F.p(-3, 0, d / 6), F.p(0, 0, d / 2) },
      width = w, height = 22, seed = c.seed, group = false, texture = c.tex.texture, textures = c.tex.textures })
    if canyon.sky then c.parts.sky = canyon.sky end
    I.sky_up(canyon.brushes) -- the sky texture on the rim tops (they already show the sky)
    for _, sx in ipairs({ -1, 1 }) do
      local tag = sx < 0 and "w" or "e"
      local room = c.add("room_" .. tag, kit.room(c.tx{ name = c.n("room_" .. tag), pos = F.p(sx * (w / 2 + 18), 12, 0), size = { 8, 5, 8 } }))
      c.add("passage_" .. tag, kit.passage(c.tx{ name = c.n("passage_" .. tag), path = { F.p(sx * (w / 2 - 3), 12, 0), F.p(sx * (w / 2 + 14), 12, 0) }, width = 4.5, height = 4.5, group = false }))
      c.anchor("door", sx * (w / 2 + 22), 12, 0, sx < 0 and "-x" or "+x", 3, 4)
      local _ = room
    end
    c.add("bridge", kit.bridge{ name = c.n("bridge"), from = F.p(-(w / 2 + 4), 12.125, 0), to = F.p(w / 2 + 4, 12.125, 0), width = 4, style = "truss",
      group = false, texture = c.tex.texture, textures = c.tex.textures, frame_texture = c.trim.texture })
    c.add("ledge", kit.rock_wall{ name = c.n("ledge"), from = F.p(w / 2 - 4, 0, -d / 2 + 10), to = F.p(w / 2 - 4, 0, -8), height = 1.5, depth = 3, flat_top = true,
      seed = c.seed + 1, group = false, texture = c.tex.texture, textures = c.tex.textures })
    c.anchor("door", 0, 0, -d / 2, "-z", 6, 5)
    c.anchor("door", 0, 0, d / 2, "+z", 6, 5)
    c.lights_along, c.path_spacing, c.path_height = canyon, 10, 5
    c.light_polys = { { poly = rect_poly(-(w / 2 - 2), -1.5, w / 2 - 2, 1.5), h = 15 } }
    c.light_spacing = 8
    c.main = canyon
    c.organic = canyon
  end)

archetype("cave_chamber", "indoor", 64, 24, 56, 20,
  "A cave chamber 18 m across and 11 m high between two lit entry rooms, with timber ceiling beams and posts, a raised timber deck and boulders (organic moderation: level floor, built elements).",
  function(c)
    local F = c.F
    local hx = c.w / 2
    local ra = c.add("room_w", kit.room(c.tx{ name = c.n("room_w"), pos = F.p(-hx + 7, 0, 0), size = (F.facing == "+z" or F.facing == "-z") and { 14, 7, 12 } or { 12, 7, 14 } }))
    local rb = c.add("room_e", kit.room(c.tx{ name = c.n("room_e"), pos = F.p(hx - 7, 0, 0), size = (F.facing == "+z" or F.facing == "-z") and { 14, 7, 12 } or { 12, 7, 14 } }))
    local _ = ra, rb
    local cave = c.add("cave", kit.cave{ name = c.n("cave"), path = { F.p(-hx + 13, 0, 0), F.p(-8, 0, 0), F.p(6, 0, 3), F.p(hx - 13, 0, 0) },
      radii = { 3.5, 9, 9, 3.5 }, heights = { 5, 11, 11, 5 }, seed = c.seed, group = false, texture = c.tex.texture, textures = c.tex.textures })
    for i, x in ipairs({ -6, 0, 6 }) do
      c.add("beam" .. i, kit.beam{ name = c.n("beam" .. i), from = F.p(x, 7.5, -12), to = F.p(x, 7.5, 12), size = { 0.4, 0.5 }, texture = c.trim.texture, textures = c.trim.textures })
      for _, sz in ipairs({ -1, 1 }) do
        c.add("post" .. i .. (sz < 0 and "s" or "n"), kit.beam{ name = c.n("post" .. i .. (sz < 0 and "s" or "n")), from = F.p(x, 0, sz * 4.5), to = F.p(x, 7.5, sz * 4.5),
          size = 0.4, texture = c.trim.texture, textures = c.trim.textures })
      end
    end
    c.add("deck", kit.platform(c.trimx{ name = c.n("deck"), pos = F.p(3, 0, -2), size = (F.facing == "+z" or F.facing == "-z") and { 5, 3 } or { 3, 5 }, height = 2.5,
      thickness = 0.3, supports = true, detail = true, group = false }))
    c.add("boulders", kit.boulders{ name = c.n("boulders"), along = cave, count = 10, seed = c.seed + 2, group = false, texture = c.tex.texture, textures = c.tex.textures })
    c.anchor("door", -hx, 0, 0, "-x", 3, 4)
    c.anchor("door", hx, 0, 0, "+x", 3, 4)
    c.lights_along = cave
    c.light_rooms = { ra, rb }
    c.main = { min = vec(unpack3(F.p(-hx, 0, -c.d / 2))), max = vec(unpack3(F.p(hx, 11, c.d / 2))) }
    c.organic = cave
  end)

archetype("pit_room", "indoor", 24, 24, 22, 22,
  "An octagonal 9 m room with a raised 13 m centre bay over a 10 m deep rough pit, a plank bridge across the pit, a 4 m wall ledge with stairs and two raised corner sections.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("room"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 5, height = 9,
      ceiling = "stepped", rise = 4, inset = 6 }))
    c.add("pit", kit.pit{ name = c.n("pit"), pos = F.p(-3, 0, -3), radius = 5, depth = 10, seed = c.seed, group = false, texture = c.tex.texture, textures = c.tex.textures })
    c.add("bridge", kit.bridge{ name = c.n("bridge"), from = F.p(-11, 0.125, -3), to = F.p(5, 0.125, -3), width = 3, style = "plank", group = false,
      texture = c.trim.texture, textures = c.trim.textures })
    lbox(c, "ledge", -6, 3.5, d / 2 - 3, 6, 4, d / 2, { detail = true, texture = c.tex.texture, textures = c.tex.textures })
    c.add("ledge_stairs", kit.stairs(c.tx{ name = c.n("ledge_stairs"), pos = F.p(6 - 1.5 - 0.25, 0, d / 2 - 11), dir = F.d("+z"), rise = 4, width = 3, group = false }))
    local x0, z0, x1, z1 = F.rect(w / 2 - 7, -d / 2 + 3, w / 2 - 3, -d / 2 + 7)
    c.add("raised_se", kit.floor_section(c.tx{ name = c.n("raised_se"), room = room, min = { x0, 0, z0 }, max = { x1, 0, z1 }, rise = 1 }))
    x0, z0, x1, z1 = F.rect(-w / 2 + 2, d / 2 - 9, -w / 2 + 6, d / 2 - 5)
    c.add("raised_nw", kit.floor_section(c.tx{ name = c.n("raised_nw"), room = room, min = { x0, 0, z0 }, max = { x1, 0, z1 }, rise = 0.75 }))
    for _, s in ipairs({ { "-x", -w / 2, 0 }, { "+x", w / 2, 0 }, { "-z", 0, -d / 2 } }) do c.anchor("door", s[2], 0, s[3], s[1], 3, 4) end
    c.light_poly, c.light_h = room.outline, 7
  end)

archetype("control_room", "indoor", 30, 14, 30, 14,
  "A 12 m machine bay overlooked by a raised control room (floor 4 m up, shed ceiling) through a wide window, reached by stairs and a doorway, with consoles and a machine block.",
  function(c)
    local F = c.F
    local bay = c.add("bay", kit.shaped_room(c.tx{ name = c.n("bay"), pos = F.p(-6, 0, 0), size = { 18, 14 }, facing = F.facing, chamfer = 3, height = 12 }))
    local ctrl = c.add("control", kit.shaped_room(c.tx{ name = c.n("control"), pos = F.p(9, 4, 0), size = { 10, 9 }, facing = F.facing, height = 4.5,
      ceiling = "shed", rise = 1.5, inset = 1 }))
    c.add("window", kit.opening(c.tx{ name = c.n("window"), a = bay, b = ctrl, shape = "window", floor_y = c.y + 5, width = 5, height = 2, offset = (F.facing == "+z" or F.facing == "+x") and 1.75 or -1.75 }))
    c.add("door", kit.doorway(c.tx{ name = c.n("door"), a = bay, b = ctrl, floor_y = c.y + 4, offset = (F.facing == "+z" or F.facing == "+x") and -3 or 3 }))
    c.add("stairs", kit.stairs(c.tx{ name = c.n("stairs"), pos = F.p(-5, 0, -3), dir = F.d("+x"), rise = 4, width = 3, group = false }))
    local x0, z0, x1, z1 = F.rect(11.5, -4, 13.5, 4)
    c.add("dais", kit.floor_section(c.tx{ name = c.n("dais"), room = ctrl, min = { x0, 0, z0 }, max = { x1, 0, z1 }, rise = 0.5 }))
    lbox(c, "console1", 4.6, 4, 0, 5.4, 5, 3.5, { detail = true, texture = c.trim.texture, textures = c.trim.textures })
    lbox(c, "machine", -12, 0, 1.5, -6, 3, 5.5, c.tx({}))
    c.add("pilasters", kit.pilasters(c.trimx{ name = c.n("pilasters"), room = bay, sides = { F.d("-z"), F.d("+z") }, spacing = 4 }))
    c.anchor("door", -15, 0, 0, "-x", 3, 4)
    c.anchor("door", -6, 0, 7, "+z", 3, 4)
    c.light_polys = { { poly = bay.outline, h = 9, world = true }, { poly = ctrl.outline, h = 3.5, world = true, y = c.y + 4 } }
    c.main = bay
  end)

archetype("storage", "indoor", 20, 14, 18, 12,
  "A storage hall: 4.5 m low bay with ceiling beams and three wall alcoves, an 8.5 m high bay with two-tier racks.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("room", kit.shaped_room(c.tx{ name = c.n("room"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 1.5, height = 4.5,
      ceiling = "stepped", rise = 4, region = { -w / 2 + 2, -d / 2 + 2, 1, d / 2 - 2 } }))
    local side = F.d("+z")
    local ap = F.xz(4.5, 0)
    local along = (side == "+z" or side == "-z") and (ap[1] - F.x) or (ap[2] - F.z)
    c.add("alcoves", kit.alcoves(c.tx{ name = c.n("alcoves"), room = room, side = side, at = { along }, width = 3, depth = 1.5, height = 3 }))
    c.add("alcoves2", kit.alcoves(c.tx{ name = c.n("alcoves2"), room = room, side = F.d("+x"), count = 2, width = 3, depth = 1.5, height = 3 }))
    -- Two-tier racks: detail shelves on posts.
    for i, z in ipairs({ -2.5, 2.5 }) do
      local x0, x1 = -w / 2 + 3, -w / 2 + 10
      for k, h in ipairs({ 2.4, 4.6 }) do
        lbox(c, "rack" .. i .. "_shelf" .. k, x0, h - 0.15, z - 0.6, x1, h, z + 0.6, { detail = true, texture = c.trim.texture, textures = c.trim.textures })
      end
      for j, x in ipairs({ x0 + 0.1, (x0 + x1) / 2, x1 - 0.1 }) do
        for _, dz in ipairs({ -0.5, 0.5 }) do
          c.add("rack" .. i .. "_post" .. j .. dz, I.member("space", c.name, "rack" .. i .. "_post" .. j .. (dz < 0 and "a" or "b"),
            F.p(x, 0, z + dz), F.p(x, 4.6, z + dz), 0.12, 0.12, c.trim))
        end
      end
    end
    for i, x in ipairs({ 3.5, 7 }) do
      c.add("beam" .. i, kit.beam{ name = c.n("beam" .. i), from = F.p(x, 4.2, -d / 2 + 0.75), to = F.p(x, 4.2, d / 2 - 0.75), size = 0.4, texture = c.trim.texture, textures = c.trim.textures })
    end
    c.anchor("door", -w / 2, 0, 0, "-x", 3, 4)
    c.anchor("door", 0, 0, -d / 2, "-z", 3, 4)
    c.light_poly, c.light_h = room.outline, 3.8
  end)

-- ---- open air -------------------------------------------------------------------------------

archetype("platform", "open_air", 30, 24, 24, 20,
  "Floating platforms in the sky: a 30 x 24 m octagonal island with a rocky underside, kerb-and-rail edges and a deckhouse, a sky bridge to a higher island and a plank bridge down to a lower one.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    void(c, -w / 2 - 22, -40, -d / 2 - 30, w / 2 + 52, 30, d / 2 + 22)
    local main = chamfer(rect_poly(-w / 2, -d / 2, w / 2, d / 2), math.min(w, d) * 0.25)
    floating(c, "main", main, 0, 2, 12)
    local high = chamfer(rect_poly(w / 2 + 18, -7, w / 2 + 32, 7), 3)
    floating(c, "high", high, 5, 2, 8)
    local low = chamfer(rect_poly(-8, -d / 2 - 22, 8, -d / 2 - 10), 3)
    floating(c, "low", low, -6, 2, 8)
    -- Deckhouse: a solid shell, its room carved inside, a doorway out.
    lbox(c, "house", -w / 2 + 3, 0, -4, -w / 2 + 13, 5.5, 4, c.tx({}))
    lbox(c, "house_air", -w / 2 + 3.5, 0, -3.5, -w / 2 + 12.5, 4.5, 3.5, c.tx({ air = true }))
    lbox(c, "house_door", -w / 2 + 12.25, 0, -1.5, -w / 2 + 13.5, 3.5, 1.5, c.tx({ air = true }))
    edge_treatment(c, main, 0, function(_, a, b)
      local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
      return (mx > w / 2 - 0.5 and math.abs(mz) < 3) or (mz < -d / 2 + 0.5 and math.abs(mx) < 4)
    end)
    edge_treatment(c, high, 5, function(_, a, b) return (a[1] + b[1]) / 2 < w / 2 + 18.5 end)
    c.add("sky_bridge", kit.bridge{ name = c.n("sky_bridge"), from = F.p(w / 2 - 2, 0.125, 0), to = F.p(w / 2 + 20, 5.125, 0), width = 4, style = "truss", group = false,
      texture = c.tex.texture, textures = c.tex.textures, frame_texture = c.trim.texture })
    c.add("plank_bridge", kit.bridge{ name = c.n("plank_bridge"), from = F.p(0, 0.125, -d / 2 + 2), to = F.p(0, -5.875, -d / 2 - 12), width = 3, style = "plank",
      landmark = false, group = false, texture = c.trim.texture, textures = c.trim.textures })
    c.light_polys = { { poly = main, h = 6 }, { poly = high, h = 11 }, { poly = rect_poly(-w / 2 + 4, -3, -w / 2 + 12, 3), h = 3.5 } }
    c.light_spacing = 12
    c.main = { min = vec(unpack3(F.p(-w / 2, -2, -d / 2))), max = vec(unpack3(F.p(w / 2, 6, d / 2))) }
  end)

archetype("sky_bridge", "open_air", 24, 18, 20, 16,
  "Two sky decks 24 m apart joined by a 28 m through-truss bridge rising 3 m, with a lower deck 10 m below beside and under the span and a stair block up from it.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local gap = 24
    void(c, -w / 2 - 20, -36, -d / 2 - 20, w / 2 + gap + w + 20, 30, d / 2 + 20)
    local a = chamfer(rect_poly(-w / 2, -d / 2, w / 2, d / 2), 3)
    local b = chamfer(rect_poly(w / 2 + gap, -d / 2, w / 2 + gap + w, d / 2), 3)
    local low = chamfer(rect_poly(w / 2 + 4, 1, w / 2 + 20, 13), 2)
    floating(c, "deck_a", a, 0, 2, 10)
    floating(c, "deck_b", b, 3, 2, 10)
    floating(c, "deck_low", low, -10, 1.5, 6)
    edge_treatment(c, a, 0, function(_, p, q) return (p[1] + q[1]) / 2 > w / 2 - 0.5 end)
    edge_treatment(c, b, 3, function(_, p, q) return (p[1] + q[1]) / 2 < w / 2 + gap + 0.5 end)
    c.add("bridge", kit.bridge{ name = c.n("bridge"), from = F.p(w / 2 - 2, 0.125, 0), to = F.p(w / 2 + gap + 2, 3.125, 0), width = 4, style = "truss", group = false,
      texture = c.tex.texture, textures = c.tex.textures, frame_texture = c.trim.texture })
    c.add("stairs", kit.stairs(c.tx{ name = c.n("stairs"), pos = F.p(w / 2 + 12, -10, 4.5), dir = F.d("-x"), rise = 10, tread = 0.3, width = 3, group = false }))
    c.light_polys = { { poly = a, h = 6 }, { poly = b, h = 9 }, { poly = low, h = -5 } }
    c.light_spacing = 12
    c.main = { min = vec(unpack3(F.p(-w / 2, -2, -d / 2))), max = vec(unpack3(F.p(w / 2, 6, d / 2))) }
  end)

archetype("open_deck", "open_air", 32, 20, 24, 16,
  "An open deck in the sky with full edge treatment (kerb, posts and rail, gaps for access), a canopy on posts, a deckhouse, a cantilevered lookout and a 30 m lattice mast landmark.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    void(c, -w / 2 - 24, -30, -d / 2 - 24, w / 2 + 24, 36, d / 2 + 30)
    local deck = chamfer(rect_poly(-w / 2, -d / 2, w / 2, d / 2), 4)
    floating(c, "deck", deck, 0, 1.5, 6)
    local look = rect_poly(-3, d / 2, 3, d / 2 + 6)
    lprism(c, "lookout", look, -0.5, 0, { detail = true, texture = c.tex.texture, textures = c.tex.textures })
    edge_treatment(c, deck, 0, function(_, a, b)
      local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
      return (mz > d / 2 - 0.5) or (mx < -w / 2 + 0.5)
    end)
    edge_treatment(c, look, 0, function(i) return i == 1 end)
    c.add("canopy", kit.platform(c.trimx{ name = c.n("canopy"), pos = F.p(4, 0, -2), size = (F.facing == "+z" or F.facing == "-z") and { 10, 6 } or { 6, 10 }, height = 4,
      thickness = 0.3, supports = true, detail = true, group = false }))
    lbox(c, "house", -w / 2 + 3, 0, -d / 2 + 3, -w / 2 + 11, 4.5, -d / 2 + 9, c.tx({}))
    lbox(c, "house_air", -w / 2 + 3.5, 0, -d / 2 + 3.5, -w / 2 + 10.5, 3.5, -d / 2 + 8.5, c.tx({ air = true }))
    lbox(c, "house_door", -w / 2 + 10.25, 0, -d / 2 + 4.5, -w / 2 + 11.5, 3, -d / 2 + 7.5, c.tx({ air = true }))
    c.add("mast", kit.landmark(c.trimx{ name = c.n("mast"), pos = F.p(w / 2 - 5, 0, d / 2 - 5), height = 30, base = 4, style = "pylon", group = false }))
    for i, q in ipairs({ { -w / 2 + 6, -d / 2 + 6 }, { w / 2 - 6, -d / 2 + 6 } }) do
      c.add("strut" .. i, kit.beam{ name = c.n("strut" .. i), from = F.p(q[1], -1.5, q[2]), to = F.p(q[1] * 0.6, -14, q[2] * 0.6), size = 0.6, texture = c.trim.texture, textures = c.trim.textures })
    end
    c.light_polys = { { poly = deck, h = 6 }, { poly = rect_poly(-w / 2 + 4, -d / 2 + 4, -w / 2 + 10, -d / 2 + 8), h = 2.8 } }
    c.light_spacing = 12
    c.main = { min = vec(unpack3(F.p(-w / 2, -1.5, -d / 2))), max = vec(unpack3(F.p(w / 2, 6, d / 2))) }
  end)

-- ---- payload --------------------------------------------------------------------------------

archetype("cart_road", "payload", 8, 96, 6, 80,
  "A payload cart road 8 m wide cut 9 m deep under the sky: a level start with a raised sidewalk, a 13% uphill grade, a 45° turn on a level pad and a roofed tunnel leg.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local k = d / 96
    local path = { F.p(0, 0, -48 * k), F.p(0, 0, -30 * k), F.p(0, 3, 0), F.p(16 * k, 3, 16 * k), F.p(16 * k, 3, 48 * k) }
    local road = c.add("road", kit.road(c.tx{ name = c.n("road"), path = path, width = w, height = 9, tunnels = { 4 }, tunnel_height = 6 }))
    if road.sky then c.parts.sky = road.sky end
    local x0, z0, x1, z1 = F.rect(w / 2 - 2, -47 * k, w / 2, -31 * k - w / 2)
    c.add("sidewalk", kit.floor_section(c.tx{ name = c.n("sidewalk"), min = { x0, c.y, z0 }, max = { x1, c.y, z1 }, rise = 0.5 }))
    -- High ground over the uphill push: the cut widens on the left into a 3 m terrace reached by stairs.
    local terrace = lbox(c, "terrace_air", -w / 2 - 5, 0, -46 * k, -w / 2, 12, -6 * k, c.tx({ air = true }))
    I.sky_up({ terrace })
    lbox(c, "terrace", -w / 2 - 5, 0, -38 * k, -w / 2, 3, -6 * k, c.tx({}))
    c.add("terrace_stairs", kit.stairs(c.tx{ name = c.n("terrace_stairs"), pos = F.p(-w / 2 - 2.75, 0, -38 * k - 6), dir = F.d("+z"), rise = 3, width = 3, group = false }))
    lbox(c, "terrace_parapet", -w / 2 - 0.6, 3, -30 * k, -w / 2, 4.1, -10 * k, { detail = true, texture = c.trim.texture, textures = c.trim.textures })
    c.anchor("road", 0, 0, -48 * k, "-z", w, 9)
    c.anchor("road", 16 * k, 3, 48 * k, "+z", w, 6)
    c.lights_along = road
    c.main = road
  end)

archetype("overlook", "payload", 40, 28, 34, 24,
  "A sky yard with defender high ground: a 4.5 m chamfered terrace with parapets, two stair flights and a covered position, and a 2.5 m attacker ledge with a ramp opposite.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local room = c.add("yard", kit.shaped_room(c.tx{ name = c.n("yard"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 3, height = 14 }))
    I.sky_up(room.brushes)
    local sky = ensure_sky(c.name)
    if sky then c.parts.sky = sky end
    local hx, hz = w / 2 - 3, d / 2
    local tz = d / 2 - 8
    lprism(c, "terrace", { { -hx, tz + 3 }, { -hx + 3, tz }, { hx - 3, tz }, { hx, tz + 3 }, { hx, hz }, { -hx, hz } }, 0, 4.5, c.tx({}))
    c.add("stairs_w", kit.stairs(c.tx{ name = c.n("stairs_w"), pos = F.p(-hx + 4.5, 0, tz - 9), dir = F.d("+z"), rise = 4.5, width = 3, group = false }))
    c.add("stairs_e", kit.stairs(c.tx{ name = c.n("stairs_e"), pos = F.p(hx - 4.5, 0, tz - 9), dir = F.d("+z"), rise = 4.5, width = 3, group = false }))
    lbox(c, "parapet", -hx + 6.25, 4.5, tz, hx - 6.25, 5.6, tz + 0.4, { detail = true, texture = c.trim.texture, textures = c.trim.textures })
    c.add("canopy", kit.platform(c.trimx{ name = c.n("canopy"), pos = F.p(0, 4.5, hz - 3), size = (F.facing == "+z" or F.facing == "-z") and { 12, 4 } or { 4, 12 }, height = 3.5,
      thickness = 0.3, supports = true, detail = true, group = false }))
    lbox(c, "ledge", -12, 0, -d / 2, 12, 2.5, -d / 2 + 4, c.tx({}))
    c.add("ledge_ramp", kit.ramp(c.tx{ name = c.n("ledge_ramp"), pos = F.p(-17, 0, -d / 2 + 2), dir = F.d("+x"), rise = 2.5, run = 5, width = 3, group = false }))
    for _, s in ipairs({ { "-x", -w / 2, 0 }, { "+x", w / 2, 0 } }) do c.anchor("road", s[2], 0, s[3], s[1], 8, 7) end
    c.light_polys = { { poly = rect_poly(-hx, -d / 2 + 2, hx, d / 2 - 2), h = 8 } }
    c.light_spacing = 10
    c.main = room
  end)

archetype("gate", "payload", 36, 64, 32, 48,
  "A gate between two sky yards: a 10 m wall with octagonal 15 m towers, a round-arched 8 m cart gate, two flank doorways, crenellated wall-walks and stairs up on the defenders' side.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local yard = c.add("yard", kit.shaped_room(c.tx{ name = c.n("yard"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = 3, height = 18 }))
    I.sky_up(yard.brushes)
    local sky = ensure_sky(c.name)
    if sky then c.parts.sky = sky end
    local hx = w / 2
    lbox(c, "wall", -hx, 0, -2, hx, 10, 2, c.tx({}))
    for i, x in ipairs({ -6.5, 6.5 }) do
      lprism(c, "tower" .. i, chamfer(rect_poly(x - 2.5, -3.5, x + 2.5, 3.5), 1.25), 0, 15, c.tx({}))
    end
    -- The cart gate: a round-arched air hull through the wall, overlapping the yard on both sides.
    local pts = {}
    local hw, spring = 4, 3.5
    local prof = { { -hw, 0 }, { hw, 0 } }
    for k = 0, 8 do local a = k / 8 * math.pi prof[#prof + 1] = { hw * math.cos(a), spring + hw * math.sin(a) } end
    for _, q in ipairs(prof) do
      for _, z in ipairs({ -4, 4 }) do pts[#pts + 1] = F.p(q[1], q[2], z) end
    end
    local gate = hull("space", c.name, "gate", pts, c.tx({ air = true }))
    c.add("gate", gate)
    for i, x in ipairs({ -14, 14 }) do lbox(c, "flank" .. i, x - 1.5, 0, -2.5, x + 1.5, 4, 2.5, c.tx({ air = true })) end
    for i = 0, math.floor((w - 2) / 2.5) do
      local x = -hx + 1 + i * 2.5
      if math.abs(x) > 9.5 then lbox(c, "merlon" .. i, x - 0.5, 10, -2, x + 0.5, 11.2, -1.5, { detail = true, texture = c.trim.texture, textures = c.trim.textures }) end
    end
    c.add("stairs_w", kit.stairs(c.tx{ name = c.n("stairs_w"), pos = F.p(-10.5, 0, 14), dir = F.d("-z"), rise = 10, tread = 0.3, width = 2.5, group = false }))
    c.add("stairs_e", kit.stairs(c.tx{ name = c.n("stairs_e"), pos = F.p(10.5, 0, 14), dir = F.d("-z"), rise = 10, tread = 0.3, width = 2.5, group = false }))
    c.anchor("road", 0, 0, -d / 2, "-z", 8, 7)
    c.anchor("road", 0, 0, d / 2, "+z", 8, 7)
    c.light_polys = { { poly = rect_poly(-hx + 3, -d / 2 + 3, hx - 3, -3), h = 9 }, { poly = rect_poly(-hx + 3, 3, hx - 3, d / 2 - 3), h = 9 } }
    c.light_spacing = 12
    c.main = yard
  end)

archetype("finale", "payload", 44, 44, 40, 40,
  "A fortified finale: an octagonal sky court with a 4.5 m defender terrace ring (open at the main gate), a final pit 2.5 m down with a cart ramp, parapets, canopies, two stairs, an arched gate from the approach and flank doorways onto the terrace.",
  function(c)
    local w, d, F = c.w, c.d, c.F
    local cut = 10
    local court = c.add("court", kit.shaped_room(c.tx{ name = c.n("court"), pos = F.p(0, 0, 0), size = { w, d }, facing = F.facing, chamfer = cut, height = 16 }))
    I.sky_up(court.brushes)
    local outer = chamfer(rect_poly(-w / 2, -d / 2, w / 2, d / 2), cut)
    local quads, inner = band(outer, 5, function(_, a, b) return (a[2] + b[2]) / 2 < -d / 2 + 0.5 end)
    for i, q in ipairs(quads) do lprism(c, "terrace" .. i, q, 0, 4.5, c.tx({})) end
    c.add("pit", kit.shaped_room(c.tx{ name = c.n("pit"), pos = F.p(0, -2.5, 0), size = { 16, 16 }, facing = F.facing, chamfer = 4, height = 3.5 }))
    c.add("cart_ramp", kit.ramp(c.tx{ name = c.n("cart_ramp"), pos = F.p(0, -2.5, 2), dir = F.d("-z"), rise = 2.5, run = 10, width = 8, group = false }))
    local approach = c.add("approach", kit.room(c.tx{ name = c.n("approach"), pos = F.p(0, 0, -d / 2 - 7), size = (F.facing == "+z" or F.facing == "-z") and { 16, 12, 12 } or { 12, 12, 16 } }))
    I.sky_up({ approach.brush })
    c.add("gate", kit.opening(c.tx{ name = c.n("gate"), a = approach, b = court, shape = "arch", width = 8, height = 7, sill = 0 }))
    for _, sx in ipairs({ -1, 1 }) do
      local tag = sx < 0 and "w" or "e"
      local fr = c.add("flank_" .. tag, kit.room(c.tx{ name = c.n("flank_" .. tag), pos = F.p(sx * (w / 2 + 4), 4.5, 0), size = { 6, 5, 6 } }))
      c.add("flank_door_" .. tag, kit.doorway(c.tx{ name = c.n("flank_door_" .. tag), a = court, b = fr, floor_y = c.y + 4.5 }))
      c.anchor("door", sx * (w / 2 + 7), 4.5, 0, sx < 0 and "-x" or "+x", 3, 4)
    end
    local iz = d / 2 - 5
    c.add("stairs_n", kit.stairs(c.tx{ name = c.n("stairs_n"), pos = F.p(-6, 0, iz - 9), dir = F.d("+z"), rise = 4.5, width = 3, group = false }))
    c.add("stairs_w", kit.stairs(c.tx{ name = c.n("stairs_w"), pos = F.p(-(w / 2 - 5) + 9, 0, 7), dir = F.d("-x"), rise = 4.5, width = 3, group = false }))
    -- Parapets on the terrace's inner edges, gaps where the stairs arrive.
    for i, q in ipairs(quads) do
      local a, b = q[4], q[3]
      local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
      local skip = (mz > iz - 0.5 and math.abs(mx) < 12) or (mx < -(w / 2 - 5) + 0.5 and math.abs(mz) < 12)
      if not skip then
        c.add("parapet" .. i, I.member("space", c.name, "parapet" .. i, F.p(a[1], 5.05, a[2]), F.p(b[1], 5.05, b[2]), 0.4, 1.1, c.trim))
      end
    end
    c.add("canopy_e", kit.platform(c.trimx{ name = c.n("canopy_e"), pos = F.p(w / 2 - 2.5, 4.5, -6), size = (F.facing == "+z" or F.facing == "-z") and { 4, 6 } or { 6, 4 },
      height = 3.5, thickness = 0.3, supports = true, detail = true, group = false }))
    c.anchor("road", 0, 0, -d / 2 - 13, "-z", 8, 7)
    c.light_polys = { { poly = inner, h = 8 } }
    c.light_spacing = 10
    c.main = court
  end)

-- The recipe parts kit_layout builds its own zones from (installed after this module).
I.space_parts = {
  new_ctx = new_ctx, floating = floating, chamfer = chamfer, rect_poly = rect_poly, plan_lights = plan_lights,
  area2 = area2, bbox = bbox, centroid = centroid, snap16 = snap16,
}

---@kit kit.archetypes{} → archetype[] — The kit.space archetype library: {name, class ("indoor", "open_air" or "payload"), size = {w, d}, min = {w, d}, summary} for every archetype, in library order.
function kit.archetypes(t)
  if t ~= nil then check_keys("kit.archetypes", t, {}) end
  local out = {}
  for i, n in ipairs(ORDER) do
    local a = ARCH[n]
    out[i] = { name = a.name, class = a.class, size = { a.w, a.d }, min = { a.minw, a.mind }, summary = a.summary }
  end
  return out
end

---@kit kit.space{archetype, name?, pos, size?, facing?, seed?, lights?, group?, texture?|textures?, trim_texture?} → space — Builds a whole space from the archetype library at exemplar scale (kit.archetypes lists them: hall, atrium, arena, ring, sunken_court, stair_hall, courtyard, canyon_bridge, cave_chamber, pit_room, control_room, storage; open-air platform, sky_bridge, open_deck; payload cart_road, overlook, gate, finale). Returns {kind = "space", name, archetype, class, summary, min, max (the main room's bounds), floor_y, pos, size, facing, parts, brushes, group, anchors, lights, warnings, organic}; every brush is tagged kit.space = name. parts maps each part name to a record with world bounds min/max: kit records as returned (rooms, corridors, stairs, bridges ...), single brushes as {kind = "brush", brush, brushes, min, max}, part groups as {kind = "group", items, brushes, min, max} - so for _, p in pairs(s.parts) do print(p.min, p.max) end always works. anchors lists the attach points: {kind ("door" or "road"), pos (vec: the floor point where the opening's centre meets the space's outer wall), dir ("+x", "-x", "+z" or "-z": outward), width, height}; join a corridor by ending a kit.passage{path = {..., a.pos}} (or a kit.corridor path) exactly at pos along dir, at most width wide and height high.
---@opt archetype: string — Required: an archetype name from kit.archetypes{}.
---@opt name: string = <archetype><n> — Names the space (prefix of every part's name, the group and the kit.space tag).
---@opt pos: vec — Required: the floor point at the centre of the space's plan (the canyon floor, the main deck top, the road start's centre line at its middle).
---@opt size: {w, d} = the archetype's size — The plan size before facing; at least the archetype's minimum.
---@opt facing: string = "+z" — Rotates the space: its local +z side faces this way.
---@opt seed: integer = 1 — The random stream of any organic part (canyon, cave, pit, rocks).
---@opt lights: boolean = true — Place Light objects through the space.
---@opt group: boolean = true — Group every brush under the space's name.
---@opt texture: string = kit.defaults.texture (nil: textures below) — One texture for every face of the space's structure.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---@opt trim_texture: string = the space's textures — The texture of trims, rails, posts, beams and parapets.
function kit.space(t)
  local api = "kit.space"
  check_keys(api, t, { "archetype", "name", "pos", "size", "facing", "seed", "lights", "group", "texture", "textures", "trim_texture" })
  local an = t.archetype
  local a = ARCH[an or ""]
  if a == nil then
    fail(api, "unknown archetype '" .. tostring(an) .. "'.", "Valid archetypes: " .. table.concat(ORDER, ", ") .. ".")
  end
  if t.texture ~= nil and t.textures ~= nil then fail(api, "give either 'texture' (every face) or 'textures' (by role), not both.") end
  local name = name_for(api, an, t.name)
  local px, py, pz = vec3(api, "pos", t.pos)
  local F = frame(api, { px, py, pz }, t.facing)
  local w, d = a.w, a.d
  if t.size ~= nil then
    w, d = size2(api, "size", t.size)
    if w < a.minw - EPS or d < a.mind - EPS then
      fail(api, "the " .. an .. " archetype needs a plan of at least " .. fmt(a.minw) .. " x " .. fmt(a.mind) .. " m (got " .. fmt(w) .. " x " .. fmt(d) .. ").")
    end
  end
  local seed = t.seed or 1
  if type(seed) ~= "number" or seed ~= math.floor(seed) then fail(api, "'seed' must be an integer.") end
  local ctx = new_ctx(api, name, F, w, d, t)
  ctx.seed = seed
  a.build(ctx)

  -- Lights.
  local lights = {}
  if boolean(api, "lights", t.lights, true) then
    local spacing = ctx.light_spacing or kit.defaults.light_spacing * 1.4
    local function add_lights(list)
      for _, l in ipairs(list) do lights[#lights + 1] = l end
    end
    if ctx.light_poly then
      add_lights(plan_lights(name, ctx.light_poly, py, ctx.light_h, spacing))
    end
    for _, lp in ipairs(ctx.light_polys or {}) do
      local poly = lp.world and lp.poly or F.poly(lp.poly)
      if area2(poly) < 0 then local r = {} for i = #poly, 1, -1 do r[#r + 1] = poly[i] end poly = r end
      add_lights(plan_lights(name .. "_" .. (#lights + 1), poly, lp.y or py, lp.h, spacing))
    end
    if ctx.lights_along then
      add_lights(kit.light_path{ along = ctx.lights_along, spacing = ctx.path_spacing or 12, height = ctx.path_height, name = name .. "_path" })
    end
    for _, r in ipairs(ctx.light_rooms or {}) do add_lights(kit.light_room{ room = r, spacing = 7, name = r.name }) end
  end

  for _, b in ipairs(ctx.brushes) do b:tag("kit.space", name) end
  local group = group_if_many(name, ctx.brushes, boolean(api, "group", t.group, true))
  -- Every part as a record with world bounds (brush handles and part groups wrapped; nothing nil).
  local function bounds_of(list)
    local x0, y0, z0, x1, y1, z1 = math.huge, math.huge, math.huge, -math.huge, -math.huge, -math.huge
    for _, b in ipairs(list) do
      if type(b) == "userdata" then
        local bb = b.bounds
        x0, y0, z0 = math.min(x0, bb.min.x), math.min(y0, bb.min.y), math.min(z0, bb.min.z)
        x1, y1, z1 = math.max(x1, bb.max.x), math.max(y1, bb.max.y), math.max(z1, bb.max.z)
      end
    end
    if x0 == math.huge then return nil, nil end
    return vec(x0, y0, z0), vec(x1, y1, z1)
  end
  local parts = {}
  for k, v in pairs(ctx.parts) do
    if type(v) == "userdata" then
      local mn, mx = bounds_of({ v })
      parts[k] = { kind = "brush", name = k, brush = v, brushes = { v }, min = mn, max = mx }
    elseif type(v) == "table" then
      if v.min == nil or v.max == nil then
        local list = {}
        if type(v.brushes) == "table" then for _, b in ipairs(v.brushes) do list[#list + 1] = b end end
        if v.brush ~= nil then list[#list + 1] = v.brush end
        for _, e in ipairs(v) do
          if type(e) == "userdata" then list[#list + 1] = e
          elseif type(e) == "table" then
            if type(e.brushes) == "table" then for _, b in ipairs(e.brushes) do list[#list + 1] = b end end
            if e.brush ~= nil then list[#list + 1] = e.brush end
          end
        end
        local mn, mx = bounds_of(list)
        if #v > 0 and v.kind == nil then
          parts[k] = { kind = "group", name = k, items = v, brushes = list, min = mn, max = mx }
        else
          v.min, v.max = v.min or mn, v.max or mx
          if v.brushes == nil then v.brushes = list end
          parts[k] = v
        end
      else
        parts[k] = v
      end
    end
  end
  local main = ctx.main or ctx.parts.room
  local organic = nil
  if ctx.organic then organic = kit.organic_report{ along = ctx.organic } end
  return {
    kind = "space", name = name, archetype = an, class = a.class, summary = a.summary, parts = parts, brushes = ctx.brushes, group = group,
    anchors = ctx.anchors, lights = lights, warnings = ctx.warnings, organic = organic, size = { w, d }, facing = F.facing,
    min = main and main.min, max = main and main.max, floor_y = py, pos = vec(px, py, pz),
  }
end

end

return install
