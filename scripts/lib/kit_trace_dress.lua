-- Glacier design kit: kit.dress_trace, theming a kit.trace build from its reference shots (installed into the kit by
-- kit.lua; use it as local kit = ged.import("kit")).
--
-- kit.trace builds the literal shell (footprint and heights); this module carries each area's look from its shot into
-- it with the kit's existing tools, never moving a wall or floor: textures by role (floor, wall, ceiling, trim, as
-- ref.match_textures picked them) with mapper-style UVs (level.uv_align), lights in the shot's colours
-- (ref.shot_lighting: kit.light_room in the key colour and intensity, a few accent lights in the accent colour, the
-- level ambient and fog), structural detail (kit.wall_detail pilasters, kit.ceiling_detail beams, kit.pipe_run along
-- the longest wall, kit.railing on deck edges, kit.frame_opening on openings, kit.machinery) and props (a kit.dress
-- theme, and/or stock clutter classes with counts). Detail brushes are detail and objects are objects, so the level
-- stays sealed. A run is one undo step; the call is labelled in the Script Log.

local function install(kit, I)
local fail, check_keys, number, positive, boolean, fmt = I.fail, I.check_keys, I.number, I.positive, I.boolean, I.fmt
local solid_near = I.solid_near

local function dist(a, b) return math.sqrt((a[1] - b[1]) ^ 2 + (a[2] - b[2]) ^ 2) end

local function inside(poly, x, z)
  local c = false
  local j = #poly
  for i = 1, #poly do
    local a, b = poly[i], poly[j]
    if (a[2] > z) ~= (b[2] > z) and x < (b[1] - a[1]) * (z - a[2]) / (b[2] - a[2]) + a[1] then c = not c end
    j = i
  end
  return c
end

local function nearest_on(poly, p)
  local best, bq = math.huge, nil
  for i = 1, #poly do
    local a, b = poly[i], poly[i % #poly + 1]
    local vx, vz = b[1] - a[1], b[2] - a[2]
    local l2 = vx * vx + vz * vz
    local t = l2 > 0 and math.max(0, math.min(1, ((p[1] - a[1]) * vx + (p[2] - a[2]) * vz) / l2)) or 0
    local q = { a[1] + vx * t, a[2] + vz * t }
    local d = dist(p, q)
    if d < best then best, bq = d, q end
  end
  return bq, best
end

-- ---------------------------------------------------------------------------------------------
-- The adapter: the ONE place that reads kit.trace's record. Fields used:
--   trace.name; trace.regions[name] = {kind, floor, floor_high, ceiling, sky, points = {{x, z}...}, brushes,
--   zone? (B: the kit zone name), edges? (B: deck edges {{x, z}, {x, z}}), material?};
--   trace.openings[i] = {a, b, at = {x, z}, width, gap, y0, y1, brush?, quad?} (B may add pos / dir).
-- ---------------------------------------------------------------------------------------------
local function trace_regions(api, trace)
  if type(trace) ~= "table" or trace.kind ~= "trace" or type(trace.regions) ~= "table" then
    fail(api, "'trace' must be the record kit.trace{...} returned.")
  end
  if trace.built == false then fail(api, "the trace was not built (build = false): build it first.") end
  local out = {}
  for name, r in pairs(trace.regions) do
    local airs, solids = {}, {}
    for _, b in ipairs(r.brushes or {}) do
      if b.air then airs[#airs + 1] = b else solids[#solids + 1] = b end
    end
    local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
    for _, q in ipairs(r.points) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
    out[name] = { name = name, kind = r.kind, floor = r.floor, floor_high = r.floor_high or r.floor, ceiling = r.ceiling, sky = r.sky,
      points = r.points, area = r.area, airs = airs, solids = solids, zone = r.zone, edges = r.edges,
      x0 = x0, z0 = z0, x1 = x1, z1 = z1 }
  end
  -- trace.solids[i] = {brushes, region}: pillars and blocks belong to the region they stand in (their sides take trim).
  for _, s in ipairs(trace.solids or {}) do
    local g = s.region and out[s.region]
    if g then for _, b in ipairs(s.brushes or {}) do g.solids[#g.solids + 1] = b end end
  end
  local openings = {}
  for i, o in ipairs(trace.openings or {}) do
    openings[i] = { a = o.a, b = o.b, at = o.at, width = o.width, gap = o.gap or 0, y0 = o.y0, y1 = o.y1, brush = o.brush, pos = o.pos, dir = o.dir }
  end
  return out, openings, trace.name or "trace"
end

-- A kit room record over one region (or one of its air pieces), for kit.light_room / kit.dress / the detail modules.
local function record_of(trace_name, g, piece)
  local x0, y0, z0, x1, y1, z1 = g.x0, g.floor, g.z0, g.x1, g.ceiling, g.z1
  local brushes = g.airs
  if piece then
    local bb = piece.bounds
    x0, y0, z0, x1, y1, z1 = bb.min.x, bb.min.y, bb.min.z, bb.max.x, bb.max.y, bb.max.z
    brushes = { piece }
  end
  return { name = trace_name .. "_" .. g.name, kind = g.kind == "corridor" and "corridor" or "room",
    min = ged.vec(x0, y0, z0), max = ged.vec(x1, y1, z1), floor_y = g.floor, brushes = brushes, brush = brushes[1] }
end

local function rectangular(g)
  local bw, bd = g.x1 - g.x0, g.z1 - g.z0
  return g.area ~= nil and bw * bd > 0 and g.area / (bw * bd) >= 0.85
end

local function color3(api, key, c)
  if c == nil then return nil end
  if type(c) ~= "table" or type(c[1]) ~= "number" or type(c[2]) ~= "number" or type(c[3]) ~= "number" then
    fail(api, "'" .. key .. "' must be a colour {r, g, b} (0-255).")
  end
  return { math.floor(math.max(0, math.min(255, c[1])) + 0.5), math.floor(math.max(0, math.min(255, c[2])) + 0.5), math.floor(math.max(0, math.min(255, c[3])) + 0.5) }
end

local function try(report, what, fn)
  local ok, res = ged.try(fn)
  if not ok then
    report.warnings[#report.warnings + 1] = what .. ": " .. tostring(res and res.message or res)
    log.warn("kit.dress_trace: " .. what .. ": " .. tostring(res and res.message or res))
    return nil
  end
  return res
end

-- A deterministic stream (xorshift) for prop spots.
local function stream(seed)
  local s = (seed * 2654435761) % 4294967296
  if s == 0 then s = 1 end
  return function()
    s = (s * 1103515245 + 12345) % 2147483648
    return s / 2147483648
  end
end

local REGION_KEYS = { "floor", "wall", "ceiling", "trim", "texture", "lighting", "light", "theme", "style", "details", "props", "density", "seed", "uv" }
local DETAIL_KEYS = { "railings", "frames", "pillars", "beams", "pipes", "machinery" }

---@kit kit.dress_trace{trace, regions, ambient?, fog?, uv?, name?} → dressing — Themes a kit.trace build from its reference shots without moving a wall or floor: per region its textures by role (ref.match_textures picks), lights in the shot's colours (ref.shot_lighting), structural detail (pilasters, beams, pipes, deck railings, opening frames, machinery) and props (a kit.dress theme or stock clutter with counts). Detail brushes and objects only, so the level stays sealed. Returns {name, regions = {name = {textured, lights, accents, details, props, dress}}, lights, objects, brushes, warnings}.
---@opt trace: record — Required: the record kit.trace{...} returned (built).
---@opt regions: table — Required: {region_name = {floor?, wall?, ceiling?, trim? (textures), texture? (all roles), lighting? (a ref.shot_lighting result), light? {color?, intensity?, fixture?, spacing?, accents?} (a region gets lights and fixtures only when it gives lighting or light), theme? (kit.dress theme for props and fixtures), style? (detail style family), details? {railings, frames, pillars, beams, pipes, machinery}, props? ("theme" or {{class, count}}), density?, seed?, uv? (ppm)}}; "*" applies to every region not listed.
---@opt ambient: {r, g, b}|table = unchanged — The level ambient colour, or a ref.shot_lighting result (its ambient_color).
---@opt fog: {r, g, b}|table = unchanged — The level fog colour, or a ref.shot_lighting result (its fog_color).
---@opt uv: number = 64 — Texel density (pixels per metre) for level.uv_align on the retextured brushes (RED's stock 256 px per 4 m); a region's uv wins.
---@opt name: string = dress_trace<n> — Names the dressing (tags, light names).
function kit.dress_trace(t)
  local api = "kit.dress_trace"
  check_keys(api, t, { "trace", "regions", "ambient", "fog", "uv", "name" })
  local G, openings, tname = trace_regions(api, t.trace)
  if type(t.regions) ~= "table" then fail(api, "'regions' must map region names to their look {floor =, wall =, lighting =, ...}.") end
  for rname, spec in pairs(t.regions) do
    if rname ~= "*" and G[rname] == nil then
      local names = {}
      for n in pairs(G) do names[#names + 1] = n end
      table.sort(names)
      fail(api, "no traced region is named '" .. tostring(rname) .. "'.", "The trace's regions: " .. table.concat(names, ", ") .. ' (or "*" for all).')
    end
    check_keys(api .. ".regions." .. rname, spec, REGION_KEYS)
    if spec.details ~= nil then check_keys(api .. ".regions." .. rname .. ".details", spec.details, DETAIL_KEYS) end
    if spec.light ~= nil then check_keys(api .. ".regions." .. rname .. ".light", spec.light, { "color", "intensity", "fixture", "spacing", "accents" }) end
  end
  local name = t.name or (tname .. "_dress")
  local base_ppm = t.uv ~= nil and positive(api, "uv", t.uv) or 64
  local report = { kind = "dress_trace", name = name, regions = {}, lights = {}, objects = {}, brushes = {}, warnings = {} }

  ged.group(api .. " " .. name, function()
    -- Level ambient and fog from the shots.
    local amb = t.ambient
    if type(amb) == "table" and amb.ambient_color ~= nil then amb = amb.ambient_color end
    amb = color3(api, "ambient", amb)
    if amb then try(report, "ambient", function() level.props:set("ambient_color", amb) end) end
    local fog = t.fog
    if type(fog) == "table" and fog.fog_color ~= nil then fog = fog.fog_color end
    fog = color3(api, "fog", fog)
    if fog then try(report, "fog", function() level.props:set("fog_color", fog) end) end

    local order = {}
    for n in pairs(G) do order[#order + 1] = n end
    table.sort(order)
    for _, rname in ipairs(order) do
      local spec = t.regions[rname] or t.regions["*"]
      if spec ~= nil then
        local g = G[rname]
        local rep = { textured = 0, lights = 0, accents = 0, details = {}, props = 0 }
        report.regions[rname] = rep
        local seed = spec.seed or 1
        local style = spec.style or spec.theme or "industrial"
        local L = spec.lighting
        local light = spec.light or {}

        -- 1. Textures by role, then mapper UVs.
        local tex = { floor = spec.floor or spec.texture, wall = spec.wall or spec.texture, ceiling = spec.ceiling or spec.texture, trim = spec.trim or spec.wall or spec.texture }
        local touched = {}
        local function paint(f, role)
          local tx = tex[role]
          if tx ~= nil and not f.show_sky and not f.is_portal and f.texture ~= tx then f:set_texture(tx) rep.textured = rep.textured + 1 end
        end
        for _, b in ipairs(g.airs) do
          for _, f in ipairs(b.faces) do
            local ny = f.normal.y
            paint(f, ny < -0.7 and "floor" or (ny > 0.7 and "ceiling" or "wall"))
          end
          touched[#touched + 1] = b
        end
        for _, b in ipairs(g.solids) do
          for _, f in ipairs(b.faces) do
            local ny = f.normal.y
            paint(f, ny > 0.5 and "floor" or (ny < -0.7 and "ceiling" or "trim"))
          end
          touched[#touched + 1] = b
        end
        for _, o in ipairs(openings) do
          if o.brush ~= nil and (o.a == rname or o.b == rname) and tex.trim ~= nil then
            for _, f in ipairs(o.brush.faces) do
              local ny = f.normal.y
              paint(f, ny < -0.7 and "floor" or (ny > 0.7 and "ceiling" or "trim"))
            end
            touched[#touched + 1] = o.brush
          end
        end
        if #touched > 0 and (tex.floor or tex.wall or tex.ceiling or tex.trim) then
          try(report, rname .. " uv", function() level.uv_align{ brushes = touched, ppm = spec.uv or base_ppm } end)
        end

        -- 2. Lights in the shot's colours: key lights by kit.light_room, accents near the walls.
        local color = color3(api, rname .. ".light.color", light.color) or (L and color3(api, rname .. ".lighting.light_color", L.light_color)) or nil
        local inten = light.intensity or (L and L.intensity) or nil
        local recs = {}
        -- Open to the sky: one outdoor plan along the region's traced outline. Lit piece by piece, every air piece
        -- would get a full outdoor plan of its own, sized for its own floor, and the plans would overlap many times.
        local outdoor = false
        for _, b in ipairs(g.airs) do
          for _, f in ipairs(b:faces_facing("up")) do if f.show_sky then outdoor = true end end
        end
        if outdoor and type(g.points) == "table" and #g.points >= 3 then
          recs[1] = record_of(tname, g, nil)
          recs[1].points = g.points
        elseif #g.airs <= 1 or rectangular(g) then
          recs[1] = record_of(tname, g, nil)
        else
          for _, b in ipairs(g.airs) do
            local bb = b.bounds
            if (bb.max.x - bb.min.x) * (bb.max.z - bb.min.z) >= 12 then recs[#recs + 1] = record_of(tname, g, b) end
          end
          if #recs == 0 then recs[1] = record_of(tname, g, nil) end
        end
        -- Lights follow the region's lighting plan: a region given only textures (or details, or props) keeps the lights it has.
        -- A region that already holds lights (the script's own, a plan's, an earlier kit.light_room) keeps them: the
        -- dresser's key lights would stack on top and wash the floor out. An explicit light = {...} still adds them.
        local held = 0
        if spec.light == nil then
          -- (the lights this dressing placed in the regions before this one do not count: region boxes overlap)
          local mine = {}
          for _, l in ipairs(report.lights) do mine[l.uid] = true end
          for _, rec in ipairs(recs) do
            local q = level.objects_in{ min = rec.min, max = rec.max }
            for _, o in ipairs(q.objects or q) do
              if tostring(o.kind):lower() == "light" and not mine[o.uid] then held = held + 1 end
            end
          end
          if held > 0 and L ~= nil then
            log.info(api .. ": '" .. rname .. "' already holds " .. held .. " light(s), so it gets no key lights (give light = {...} to add them anyway).")
          end
        end
        if (spec.light ~= nil or L ~= nil) and held == 0 then
          for k, rec in ipairs(recs) do
            local lights = try(report, rname .. " lights", function()
              return kit.light_room{ room = rec, color = color, theme = spec.theme, fixture = light.fixture, spacing = light.spacing,
                name = name .. "_" .. rname .. (#recs > 1 and ("_" .. k) or "") }
            end)
            -- Indoors key lights stay overhead (outdoor lamps and posts stand on the edges by design): one the room's own ceiling measure hung low (under a deck, a ledge or a beam, less
            -- than 3 m, or 60 % of the room's height, over the region's floor) is removed with the fixture right over it; the floor stays lit by the rest.
            local floor_y = rec.floor_y or rec.min.y
            local low = floor_y + math.min(3, 0.6 * (rec.max.y - floor_y))
            local dropped = {}
            for _, l in ipairs(lights or {}) do
              if not outdoor and l.pos.y < low then
                dropped[#dropped + 1] = { l.pos.x, l.pos.y, l.pos.z }
                l:delete()
              else
                if inten ~= nil then try(report, rname .. " intensity", function() l:set("on_intensity", inten) end) end
                report.lights[#report.lights + 1] = l
                rep.lights = rep.lights + 1
              end
            end
            for _, f in ipairs((lights and lights.fixtures) or {}) do
              local p = f.pos
              local gone = false
              for _, d in ipairs(dropped) do
                if p ~= nil and math.abs(p.x - d[1]) < 1.0 and math.abs(p.z - d[3]) < 1.0 and p.y >= d[2] - 0.5 and p.y <= d[2] + 1.5 then gone = true end
              end
              if gone then f:delete() else report.objects[#report.objects + 1] = f end
            end
            if #dropped > 0 then rep.low_lights_removed = #dropped end
          end
        end
        local accent = (light.accents ~= false) and held == 0 and L and L.accent_color and color3(api, rname .. ".lighting.accent_color", L.accent_color) or nil
        if accent ~= nil then
          -- Two to four accent lights low on the walls (emergency lamps, glowing panels), 1 m in from the corners.
          local cx, cz = 0, 0
          for _, q in ipairs(g.points) do cx, cz = cx + q[1], cz + q[2] end
          cx, cz = cx / #g.points, cz / #g.points
          local want = math.max(2, math.min(4, math.floor((g.area or 50) / 60 + 0.5)))
          local step = math.max(1, math.floor(#g.points / want))
          for i = 1, #g.points, step do
            if rep.accents >= want then break end
            local q = g.points[i]
            local dx, dz = cx - q[1], cz - q[2]
            local d = math.sqrt(dx * dx + dz * dz)
            if d > 1.5 then
              local x, z = q[1] + dx / d * 1.2, q[2] + dz / d * 1.2
              local y = g.floor + math.min(2.2, (g.ceiling - g.floor) * 0.6)
              if inside(g.points, x, z) and not solid_near(x, y, z, 0.3, -1) then
                local a = level.place{ kind = "light", name = name .. "_" .. rname .. "_accent" .. (rep.accents + 1), pos = { x, y, z },
                  props = { color = accent, size_range = 7 } }
                a:tag("kit.part", "accent")
                report.lights[#report.lights + 1] = a
                rep.accents = rep.accents + 1
              end
            end
          end
        end

        -- 3. Structural detail.
        local D = spec.details or {}
        local rec = recs[1]
        local headroom = g.ceiling - g.floor_high
        if D.pillars and rectangular(g) and not g.sky then
          local d = try(report, rname .. " pillars", function() return kit.wall_detail{ room = rec, style = style, seed = seed, parts = { pilasters = true, base = true, top = true, panels = false } } end)
          if d then rep.details[#rep.details + 1] = "pillars" end
        end
        if D.beams and rectangular(g) and not g.sky and headroom >= 4 then
          local d = try(report, rname .. " beams", function() return kit.ceiling_detail{ room = rec, style = style, seed = seed, pipes = D.pipes and true or false } end)
          if d then rep.details[#rep.details + 1] = "beams" end
        end
        if D.pipes and not (D.beams and rectangular(g) and not g.sky and headroom >= 4) then
          -- A pipe run under the ceiling (or at 3 m outdoors) along the longest axis-aligned wall, 0.5 m off it.
          local best, bi = 0, nil
          for i = 1, #g.points do
            local a, b = g.points[i], g.points[i % #g.points + 1]
            local axis = math.abs(a[1] - b[1]) < 1e-3 or math.abs(a[2] - b[2]) < 1e-3
            if axis and dist(a, b) > best then best, bi = dist(a, b), i end
          end
          if bi and best >= 4 then
            local a, b = g.points[bi], g.points[bi % #g.points + 1]
            local ux, uz = (b[1] - a[1]) / best, (b[2] - a[2]) / best
            local nx, nz = -uz, ux -- inward for a CCW outline
            local y = g.sky and (g.floor + 3) or (g.ceiling - 0.6)
            local p1 = { a[1] + ux * 0.8 + nx * 0.5, y, a[2] + uz * 0.8 + nz * 0.5 }
            local p2 = { b[1] - ux * 0.8 + nx * 0.5, y, b[2] - uz * 0.8 + nz * 0.5 }
            local d = try(report, rname .. " pipes", function() return kit.pipe_run{ points = { p1, p2 }, count = 2, diameter = 0.3, style = style, seed = seed } end)
            if d then rep.details[#rep.details + 1] = "pipes" end
          end
        end
        if D.railings and g.kind == "deck" then
          -- Deck edges that drop: B's edges when given, else every outline edge whose outside is not floor at the
          -- deck's height.
          local edges = g.edges
          if edges == nil then
            edges = {}
            for i = 1, #g.points do
              local a, b = g.points[i], g.points[i % #g.points + 1]
              local L2 = dist(a, b)
              if L2 >= 1 then
                local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
                local ox, oz = mx + (b[2] - a[2]) / L2 * 0.4, mz - (b[1] - a[1]) / L2 * 0.4
                local level_floor = false
                for _, h in pairs(G) do
                  if h ~= g and math.abs((h.floor_high or h.floor) - g.floor) < 0.3 and inside(h.points, ox, oz) then level_floor = true end
                end
                if not level_floor then edges[#edges + 1] = { a, b } end
              end
            end
          end
          local n = 0
          for _, e in ipairs(edges) do
            local d = try(report, rname .. " railing", function()
              return kit.railing{ from = { e[1][1], g.floor, e[1][2] }, to = { e[2][1], g.floor, e[2][2] }, style = style, seed = seed }
            end)
            if d then n = n + 1 end
          end
          if n > 0 then rep.details[#rep.details + 1] = "railings x" .. n end
        end
        if D.frames then
          local n = 0
          for _, o in ipairs(openings) do
            if o.a == rname and o.gap >= 0.05 and o.y0 ~= nil then
              local pa = nearest_on(g.points, o.at)
              local pb = nearest_on(G[o.b].points, o.at)
              local dx, dz = pb[1] - pa[1], pb[2] - pa[2]
              local dir = nil
              if math.abs(dx) > 0.9 * dist(pa, pb) then dir = dx > 0 and "+x" or "-x" elseif math.abs(dz) > 0.9 * dist(pa, pb) then dir = dz > 0 and "+z" or "-z" end
              if dir then
                local d = try(report, rname .. " frame", function()
                  return kit.frame_opening{ pos = { pa[1], o.y0, pa[2] }, dir = dir, width = o.width, height = o.y1 - o.y0, thickness = math.max(0.25, o.gap), style = style, seed = seed }
                end)
                if d then n = n + 1 end
              end
            end
          end
          if n > 0 then rep.details[#rep.details + 1] = "frames x" .. n end
        end
        if D.machinery and not g.sky then
          local d = try(report, rname .. " machinery", function() return kit.machinery{ room = rec, style = style, seed = seed } end)
          if d then rep.details[#rep.details + 1] = "machinery" end
        end

        -- 4. Props: a kit.dress theme, and/or stock clutter classes with counts.
        local props = spec.props
        if props ~= nil then
          if type(props) == "string" then props = { props } end
          for _, p in ipairs(props) do
            if type(p) == "string" then
              local dr = try(report, rname .. " dress", function()
                return kit.dress{ room = rec, theme = (p == "theme" and spec.theme) or p, density = spec.density or 1, seed = seed, cover = false, name = name .. "_" .. rname .. "_dress" }
              end)
              if dr then
                rep.dress = dr.count
                rep.props = rep.props + (dr.count or 0)
              end
            elseif type(p) == "table" then
              local cls, count = p.class or p[1], p.count or p[2] or 1
              if type(cls) ~= "string" then fail(api, rname .. ".props: each entry is a theme name or {class, count}.") end
              local rnd = stream(seed + rep.props * 7 + 1)
              local placed, guard = 0, 0
              while placed < count and guard < count * 40 do
                guard = guard + 1
                local x = g.x0 + 1 + rnd() * math.max(0, g.x1 - g.x0 - 2)
                local z = g.z0 + 1 + rnd() * math.max(0, g.z1 - g.z0 - 2)
                local _, edge = nearest_on(g.points, { x, z })
                if inside(g.points, x, z) and edge >= 1 and not solid_near(x, g.floor + 0.6, z, 0.6, -1) then
                  local ok = try(report, rname .. " prop " .. cls, function()
                    local o = level.place{ kind = "clutter", class = cls, pos = { x, g.floor, z }, yaw = math.floor(rnd() * 4) * 90,
                      name = name .. "_" .. rname .. "_prop" .. (rep.props + 1) }
                    o:tag("kit.part", "prop")
                    report.objects[#report.objects + 1] = o
                    return true
                  end)
                  if not ok then break end
                  placed = placed + 1
                  rep.props = rep.props + 1
                end
              end
            end
          end
        end
      end
    end
  end)
  return report
end

end

return install
