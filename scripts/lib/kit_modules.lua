-- Glacier design kit: architectural detailing from the module library (ged.import("kit_modules")).
--
-- Installed into the kit by kit.lua (ged.import("kit_modules")(kit, internals)). These functions take a plain
-- kit room and make it read built: layered walls (pilasters, base and top trims, bands, panels), framed openings,
-- ceiling structure (beams, trusses, coffers), machinery and consoles against the walls, railings, catwalks, pipe
-- runs, stair stringers and handrails, and rock formations. Parts come from the `modules` library (harvested from
-- approved levels: modules.list / modules.place); when the library is absent or has nothing that fits, each
-- function builds a hand-authored primitive equivalent instead and says so (fallback = true, note).
--
-- Rules every function here follows (the kit's own rules):
--   * one option table, unknown keys rejected with the nearest name; an explicit seed (default 1) picks among
--     the best-scoring choices: the same options, level and seed give the same result;
--   * every brush is detail (a placed part can never unseal the level) and is tagged kit = "<function>",
--     kit.name, kit.part (the module category), kit.role (cover, route, landmark, theme_detail or fixture) and
--     kit.module (the module id, or "primitive.<what>" for a hand-authored part);
--   * clearances: doorway openings, routes between doorways, spawns, items and existing dressing are kept clear
--     through the generative fill's engine space (kit.dress), and floor-standing parts count against its walk
--     budget; wall detail shallower than 0.3 m and anything mounted 2.2 m or more above the floor does not use
--     walk budget but still keeps out of openings and never intersects another placed part;
--   * coherent sets: the parts of one wall_detail call come from one donor — modules found in the same source
--     level (else sharing a texture family, else the same primary style) — chosen by score, then by seed;
--   * returns {name, placed = {{id, role, category, pos, yaw, size, brushes}...}, counts (by category), roles
--     (by role), skipped = {{what, reason}...}, fallback, note, brushes}.
--
-- This module only defines functions and constants at its top level.

local P = ged.import("kit_probe")

local floor, ceil, sqrt, abs, min, max = math.floor, math.ceil, math.sqrt, math.abs, math.min, math.max

local KIT, K
local kit = {}

-- ---------------------------------------------------------------------------------------------
-- Styles: the library's style families, the kit's theme names mapped onto them, and the families each one
-- may borrow from when it has nothing of its own.
-- ---------------------------------------------------------------------------------------------

local STYLES = { "industrial", "bunker", "admin", "mine", "cave", "canyon", "mars_base", "lab", "tech", "generic" }
local STYLE_ALIAS = {
  construction = "industrial", construction_site = "industrial", site = "industrial", storage = "industrial",
  warehouse = "industrial", yard = "industrial", industrial_yard = "industrial",
  control = "tech", control_room = "tech", office = "admin", rock = "canyon", outdoor = "canyon", outdoor_canyon = "canyon",
}
local RELATED = {
  industrial = { "industrial", "bunker", "tech", "mars_base", "generic" },
  bunker = { "bunker", "industrial", "generic" },
  admin = { "admin", "tech", "lab", "industrial" },
  tech = { "tech", "admin", "lab", "industrial" },
  lab = { "lab", "tech", "admin", "industrial" },
  mars_base = { "mars_base", "industrial", "tech" },
  mine = { "mine", "cave", "industrial" },
  cave = { "cave", "mine", "canyon", "industrial" },
  canyon = { "canyon", "cave", "mine" },
  generic = { "generic", "industrial", "bunker" },
}

-- Stock textures of the hand-authored parts, by style family.
local PALETTE = {
  industrial = { frame = "mtl_girder03.tga", trim = "mtl_supportbeam02.tga", beam = "mtl_girder01.tga", panel = "mtl_beigedrt07_ventpanel.tga",
    pipe = "mtl_greypipe02_rust.tga", flange = "mtl_drkpipe01_drt.tga", machine = "mtl_contrl_panel08.tga", deck = "mtl_blugraydiamond01-a.tga",
    rail = "mtl_girder03.tga", rock = "rck_canyon_rock01.tga", hazard = "mtl_hazardstripe01.tga" },
  bunker = { frame = "cem_cement_panel02.tga", trim = "cem_greypanel01.tga", beam = "mtl_girder01.tga", panel = "cem_cement_panel02.tga",
    pipe = "mtl_greypipe02_rust.tga", flange = "mtl_drkpipe01_drt.tga", machine = "mtl_panel09.tga", deck = "mtl_blugraydiamond01-a.tga",
    rail = "mtl_girder03.tga", rock = "rck_canyon_rock01.tga", hazard = "mtl_hazardstripe01.tga" },
  tech = { frame = "mtl_chromegirder01.tga", trim = "mtl_cleanpanel02.tga", beam = "mtl_ss_girder02.tga", panel = "mtl_comp_panel01.tga",
    pipe = "mtl_chromepipe01.tga", flange = "mtl_energypipe02.tga", machine = "mtl_contrl_panel01.tga", deck = "mtl_blugraydiamond01-a.tga",
    rail = "mtl_chromegirder01.tga", rock = "rck_canyon_rock01.tga", hazard = "mtl_hazardstripe01.tga" },
  mine = { frame = "mtl_girder03_dirty01.tga", trim = "sld_woodpost01.tga", beam = "sld_woodpost01.tga", panel = "mtl_filthypanels02.tga",
    pipe = "mtl_brwnpipe02_drt.tga", flange = "mtl_drkpipe01_drt.tga", machine = "mtl_beigedrt04_pipepanel.tga", deck = "mtl_blugraydiamond01-a.tga",
    rail = "mtl_girder03_dirty01.tga", rock = "rck_canyon_rock01.tga", hazard = "mtl_hazardstripe01.tga" },
  canyon = { frame = "mtl_girder03_dirty01.tga", trim = "sld_woodpost01.tga", beam = "mtl_girder03_dirty01.tga", panel = "mtl_filthypanels01.tga",
    pipe = "mtl_greypipe02_rust.tga", flange = "mtl_drkpipe01_drt.tga", machine = "mtl_beigedrt04_pipepanel.tga", deck = "mtl_blugraydiamond01-a.tga",
    rail = "mtl_girder03_dirty01.tga", rock = "rck_canyon_rock02.tga", hazard = "mtl_hazardstripe01.tga" },
}
PALETTE.lab, PALETTE.mars_base, PALETTE.generic = PALETTE.tech, PALETTE.industrial, PALETTE.industrial
PALETTE.admin = { frame = "mtl_cleanpanel01.tga", trim = "mtl_cleanpanel02.tga", beam = "mtl_ss_girder02.tga", panel = "mtl_comp_panel01.tga",
  pipe = "mtl_chromepipe01.tga", flange = "mtl_energypipe02.tga", machine = "mtl_contrl_panel01.tga", deck = "mtl_blugraydiamond01-a.tga",
  rail = "mtl_chromegirder01.tga", rock = "rck_canyon_rock01.tga", hazard = "mtl_hazardstripe01.tga" }
PALETTE.cave = PALETTE.mine

local ROLES = { cover = true, route = true, landmark = true, theme_detail = true, fixture = true }

-- ---------------------------------------------------------------------------------------------
-- Small helpers.
-- ---------------------------------------------------------------------------------------------

local function fmt(n) return string.format("%.4g", n) end

local function clamp(v, a, b) if v < a then return a elseif v > b then return b end return v end

local function seed_of(api, t)
  local s = t.seed
  if s == nil then return 1 end
  if type(s) ~= "number" or s ~= floor(s) then K.fail(api, "'seed' must be an integer (got " .. tostring(s) .. ").") end
  return s
end

local function style_of(api, v)
  if v == nil then return "industrial" end
  if type(v) ~= "string" then K.fail(api, "'style' must be a style name.") end
  local key = STYLE_ALIAS[v] or v
  for _, s in ipairs(STYLES) do if s == key then return key end end
  K.fail(api, "unknown style '" .. v .. "'.", "Styles: " .. table.concat(STYLES, ", ") ..
    " (kit theme names also work: construction, storage, control, mine, canyon, yard, site, warehouse, tech, outdoor).")
end

local function role_of(api, v)
  if v == nil then return nil end
  if not ROLES[v] then K.fail(api, "'role' must be one of cover, route, landmark, theme_detail, fixture (got " .. tostring(v) .. ").") end
  return v
end

-- A point/vec from a value, as three numbers.
local function v3(api, key, v) return K.vec3(api, key, v) end

-- The texture family of a texture name: lower case, extension and trailing digits / suffixes dropped
-- ("mtl_merc_wall03.tga" -> "mtl_merc_wall").
local function family(name)
  local s = name:lower():gsub("%.[%w]+$", "")
  s = s:gsub("_?%d+[%w_%-]*$", "")
  return s
end

local DIRS = { ["+x"] = { 1, 0 }, ["-x"] = { -1, 0 }, ["+z"] = { 0, 1 }, ["-z"] = { 0, -1 } }

-- The yaw (degrees) that turns local +Z onto the horizontal unit (nx, nz), and its local axes.
local function yaw_of(nx, nz)
  local y = math.deg(math.atan2(nx, nz))
  y = floor(y * 1000 + 0.5) / 1000
  if y < 0 then y = y + 360 end
  return y
end

local function frame_of(yaw)
  local r = math.rad(yaw)
  local s, c = math.sin(r), math.cos(r)
  if abs(s) < 1e-12 then s = 0 end
  if abs(c) < 1e-12 then c = 0 end
  return { yaw = yaw, ax = c, az = -s, nx = s, nz = c }
end

-- World position of the local origin that lands local point (lx, ly, lz) on world point (wx, wy, wz).
local function origin_for(f, lx, ly, lz, wx, wy, wz)
  return wx - f.ax * lx - f.nx * lz, wy - ly, wz - f.az * lx - f.nz * lz
end

-- World AABB of a local box under frame f with origin (ox, oy, oz).
local function world_box(f, ox, oy, oz, b)
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, lx in ipairs({ b.x0, b.x1 }) do
    for _, lz in ipairs({ b.z0, b.z1 }) do
      local wx, wz = ox + f.ax * lx + f.nx * lz, oz + f.az * lx + f.nz * lz
      x0, z0, x1, z1 = min(x0, wx), min(z0, wz), max(x1, wx), max(z1, wz)
    end
  end
  return { x0 = x0, y0 = oy + b.y0, z0 = z0, x1 = x1, y1 = oy + b.y1, z1 = z1 }
end

-- ---------------------------------------------------------------------------------------------
-- The library, read once per session into plain records.
-- ---------------------------------------------------------------------------------------------

local CACHE = { path = false, lists = {} }
-- (true while kit.build_layout's detail pass runs a public detail function: its stand-ins are reported, not warned)
local QUIET = false
local WARNED = {}

local function rec_of(m)
  local s, b = m.size, m.bounds
  local st = m.stretch
  local r = {
    id = m.id, category = m.category, role = m.role, attach = m.attach, style = m.style, styles = {},
    sx = s.x, sy = s.y, sz = s.z, x0 = b.min.x, y0 = b.min.y, z0 = b.min.z, x1 = b.max.x, y1 = b.max.y, z1 = b.max.z,
    score = m.score, occ = m.occurrences, mount = m.mount_height, cgap = m.ceiling_gap, pitch = m.suggested_pitch,
    has_air = m.has_air, recess = m.recess_depth, brushes = m.brushes, source = m.source, sources = {}, textures = {}, fams = {},
    tiles = #m.tiles, stretch = {}, brightness = m.brightness,
  }
  for i, sname in ipairs(m.styles) do r.styles[sname] = i end
  for _, src in ipairs(m.sources) do r.sources[src] = true end
  for _, t in ipairs(m.textures) do
    r.textures[#r.textures + 1] = t
    r.fams[family(t)] = true
  end
  for _, a in ipairs({ "x", "y", "z" }) do
    local ab = st[a]
    if ab then r.stretch[a] = { ab[1], ab[2] } end
  end
  local o = m.opening
  if o then r.opening = { width = o.width, height = o.height, thick = o.wall_thickness } end
  return r
end

-- The usable modules of a category (stock textures unless custom; textures resolving in the mounted assets).
local function library(cat, custom)
  if not modules.available then return {} end
  if CACHE.path ~= modules.path then CACHE.path, CACHE.lists = modules.path, {} end
  local key = cat .. (custom and "+c" or "")
  local l = CACHE.lists[key]
  if not l then
    l = {}
    for _, m in ipairs(modules.list{ category = cat, stock_only = not custom }) do l[#l + 1] = rec_of(m) end
    CACHE.lists[key] = l
  end
  return l
end

-- Style weight of a module for the call's style list (0 when no listed style fits).
local function style_weight(r, styles)
  if r.style == styles[1] then return 1.0 end
  if r.styles[styles[1]] then return 0.6 end
  for i = 2, #styles do
    local s = styles[i]
    if r.style == s then return max(0.2, 0.55 - (i - 2) * 0.1) end
    if r.styles[s] then return max(0.15, 0.4 - (i - 2) * 0.1) end
  end
  return 0
end

-- The texture words that read as each style (a module whose textures carry them belongs to the style's look):
-- bunker is poured concrete, admin clean panels, wood and marble, industrial girders, rust and pipes.
local STYLE_TEX = {
  industrial = { "girder", "rust", "pipe", "grate", "crossmember", "support", "baserust", "rivet" },
  bunker = { "cem_", "bunker", "concrete", "poured" },
  admin = { "admin", "sld_", "marb", "wood", "clean", "carpet", "executive", "office" },
  tech = { "tech", "comp", "contrl", "chrome", "_ss_", "ss_", "control" },
  lab = { "med", "hosp", "lab", "clean", "sld_" },
  mars_base = { "_ss_", "ss_", "mars", "blu" },
  mine = { "mine", "wood", "rck_", "dirt", "drill" },
  cave = { "rck_", "cave", "dirt" },
  canyon = { "rck_", "canyon", "rock" },
  generic = {},
}

-- True when a module uses an animated or bitmap-sequence texture (.vbm): the renders cannot show those
-- faithfully, and wall parts read best in plain textures.
local function has_vbm(r)
  for _, tname in ipairs(r.textures) do if tname:lower():find("%.vbm$") then return true end end
  return false
end

-- Share of a module's textures carrying the style's words (0..1).
local function style_tex(r, style)
  local words = STYLE_TEX[style] or {}
  if #words == 0 or #r.textures == 0 then return 0 end
  local hit = 0
  for _, tname in ipairs(r.textures) do
    local l = tname:lower()
    for _, w in ipairs(words) do if l:find(w, 1, true) then hit = hit + 1 break end end
  end
  return hit / #r.textures
end

-- True when the module's brushes are closed solids (read once from its .rfg): open shells render as stray faces.
local function closed(r)
  if r.closed == nil then
    local m = modules.get(r.id)
    r.closed = (m ~= nil and m.closed ~= false)
  end
  return r.closed
end

-- Texture words that clash with a style's look (rust and girders in a clean office, chrome in a bunker).
local STYLE_CLASH = {
  admin = { "rust", "girder", "rivet", "dirt", "filth", "drkpipe", "grate" },
  lab = { "rust", "girder", "rivet", "dirt", "filth" },
  tech = { "rust", "dirt", "filth", "wood" },
  bunker = { "chrome", "clean", "marb", "admin", "merc_" },
  -- (polished office and lab finishes read as strangers on worn steel and in a mine)
  industrial = { "marb", "admin", "carpet", "executive", "hosp", "medfloor" },
  mars_base = { "marb", "admin", "carpet", "executive", "wood", "hosp" },
  mine = { "chrome", "clean", "marb", "admin", "carpet", "executive", "merc_", "hosp", "medfloor" },
}

-- The modules of the categories passing ok(r), ranked by score x style weight (ties: id), as {r, w}.
local function ranked(ctx, cats, ok)
  local out = {}
  for _, cat in ipairs(cats) do
    for _, r in ipairs(library(cat, ctx.custom)) do
      local w = style_weight(r, ctx.styles)
      if w > 0 and ok(r) and closed(r) then
        local a = 0
        if ctx.material then
          -- Material affinity: a module sharing the room's wall material (its texture prefix, e.g. cem_; better its
          -- texture family) sits better on the room's walls.
          for _, tname in ipairs(r.textures) do
            if ctx.wall_family and family(tname) == ctx.wall_family then a = 0.5 break end
            if tname:lower():sub(1, #ctx.material) == ctx.material then a = 0.25 end
          end
        end
        -- Style first: the requested style (primary style, then the style's texture words), then the room's
        -- material, then the harvest score.
        local clash = 0
        for _, tname in ipairs(r.textures) do
          local l = tname:lower()
          for _, word in ipairs(STYLE_CLASH[ctx.style] or {}) do if l:find(word, 1, true) then clash = clash + 1 break end end
        end
        clash = (#r.textures > 0) and clash / #r.textures or 0
        local wgt = 0.35 * r.score + 0.9 * w + 0.45 * style_tex(r, ctx.style) + 0.6 * a - 0.8 * clash
        -- Harmony: a module much brighter than the room's walls (a glowing strip on dull concrete) reads as a
        -- sticker; it loses weight in proportion.
        if r.brightness and ctx.wall_brightness then
          wgt = wgt - 4 * max(0, r.brightness - max(0.33, ctx.wall_brightness + 0.08))
        end
        out[#out + 1] = { r = r, w = wgt, off_style = clash >= 0.5 or (w < 0.3 and style_tex(r, ctx.style) == 0) }
      end
    end
  end
  P.sort(out, function(a, b) if a.w ~= b.w then return a.w > b.w end return a.r.id < b.r.id end)
  return out
end

-- The local box of a module after stretching to (len, hgt, dep) (nil keeps an axis), or nil + reason.
local CENTRED = { wall = { x = true }, edge = { x = true }, opening = { x = true }, corner = {}, span = { x = true, z = true } }
local function stretched(r, len, hgt, dep)
  local b = { x0 = r.x0, y0 = r.y0, z0 = r.z0, x1 = r.x1, y1 = r.y1, z1 = r.z1 }
  local want = { x = len, y = hgt, z = dep }
  local nat = { x = r.sx, y = r.sy, z = r.sz }
  local cen = CENTRED[r.attach] or { x = true, z = true }
  for _, a in ipairs({ "x", "y", "z" }) do
    local t = want[a]
    if t ~= nil and abs(t - nat[a]) >= 1e-3 then
      local sl = r.stretch[a]
      if not sl then return nil, r.id .. " cannot stretch on " .. a end
      local d = t - nat[a]
      if sl[2] - sl[1] + d < 0.05 then return nil, r.id .. " cannot shrink to " .. fmt(t) .. " m on " .. a end
      local lo, hi = b[a .. "0"], b[a .. "1"] + d
      if cen[a] then lo, hi = lo - d / 2, hi - d / 2 end
      b[a .. "0"], b[a .. "1"] = lo, hi
    end
  end
  return b
end

-- ---------------------------------------------------------------------------------------------
-- A call's context: naming, tagging, the probe, collision checks, the record.
-- ---------------------------------------------------------------------------------------------

local function new_ctx(api, fn, t, name)
  local style = style_of(api, t.style)
  local ctx = {
    api = api, fn = fn, name = name, st = P.stream(seed_of(api, t)), style = style, styles = RELATED[style] or { style },
    custom = t.custom == true, pal = PALETTE[style] or PALETTE.industrial,
    placed = {}, counts = {}, roles = {}, skipped = {}, fallback = false, notes = {}, brushes = {}, used_library = false,
  }
  if t.custom ~= nil then K.boolean(api, "custom", t.custom, false) end
  if t.textures ~= nil then
    if type(t.textures) ~= "table" then K.fail(api, "'textures' must be a table {old = new} of texture names.") end
    ctx.texmap = t.textures
  end
  ctx.retexture = K.boolean(api, "retexture", t.retexture, false)
  return ctx
end

local function skip(ctx, what, reason)
  ctx.skipped[#ctx.skipped + 1] = { what = what, reason = reason }
end

local function note(ctx, s)
  for _, n in ipairs(ctx.notes) do if n == s then return end end
  ctx.notes[#ctx.notes + 1] = s
end

local function count(ctx, category, role, brushes)
  ctx.counts[category] = (ctx.counts[category] or 0) + 1
  ctx.roles[role] = (ctx.roles[role] or 0) + 1
  for _, b in ipairs(brushes) do ctx.brushes[#ctx.brushes + 1] = b end
end

-- A turned frame (kit.build_layout's room detail pass): while XF is set, the wall, frame, ceiling, railing and stair
-- code below works in the frame's own coordinates (a turned zone's walls run along its axes) and what it builds lands
-- turned into place: probe queries map the frame's points to the world, modules go in at their world origin and yaw,
-- hand-authored parts are built from their world corners. nil = the world frame. World = (px, pz) + R(rot) . local,
-- R(rot) turning local +z toward +x by rot degrees (the yaw convention of the kit).
local XF = nil

local function set_frame(rot, px, pz)
  if rot == nil then XF = nil return end
  local r = math.rad(rot)
  local c, s = math.cos(r), math.sin(r)
  if abs(c) < 1e-12 then c = 0 end
  if abs(s) < 1e-12 then s = 0 end
  -- (the probe is read once per frame: a frame's parts never overlap by construction, and refreshing the shared probe
  -- re-reads every brush of the level)
  XF = { rot = rot % 360, c = c, s = s, px = px or 0, pz = pz or 0, square = (abs(c) < 1e-9 or abs(s) < 1e-9), pr = P.shared() }
end

-- Frame point -> world point, and back.
local function xf_w(x, z)
  if not XF then return x, z end
  return XF.px + x * XF.c + z * XF.s, XF.pz - x * XF.s + z * XF.c
end
local function xf_l(x, z)
  if not XF then return x, z end
  local dx, dz = x - XF.px, z - XF.pz
  return dx * XF.c - dz * XF.s, dx * XF.s + dz * XF.c
end

-- The probe, refreshed for the brushes placed so far (the engine extends it when brushes were only appended); in a
-- turned frame, a view of it that takes the frame's points.
local function probe()
  if not XF then return P.shared() end
  local pr = XF.pr
  return {
    air_exact = function(_, x, y, z) local wx, wz = xf_w(x, z) return pr:air_exact(wx, y, wz) end,
    air = function(_, x, y, z) local wx, wz = xf_w(x, z) return pr:air(wx, y, wz) end,
  }
end

-- True when every sample point of the box (shrunk by 0.04 m) is open air: nothing solid there yet (walls,
-- earlier parts, other dressing).
local function box_clear(b)
  local pr = probe()
  local x0, y0, z0, x1, y1, z1 = b.x0 + 0.04, b.y0 + 0.04, b.z0 + 0.04, b.x1 - 0.04, b.y1 - 0.04, b.z1 - 0.04
  if x1 < x0 then x0, x1 = (x0 + x1) / 2, (x0 + x1) / 2 end
  if y1 < y0 then y0, y1 = (y0 + y1) / 2, (y0 + y1) / 2 end
  if z1 < z0 then z0, z1 = (z0 + z1) / 2, (z0 + z1) / 2 end
  local nx, ny, nz = max(1, ceil((x1 - x0) / 0.5)), max(1, ceil((y1 - y0) / 0.75)), max(1, ceil((z1 - z0) / 0.5))
  for i = 0, nx do
    local x = x0 + (x1 - x0) * i / nx
    for j = 0, ny do
      local y = y0 + (y1 - y0) * j / ny
      for k = 0, nz do
        if not pr:air_exact(x, y, z0 + (z1 - z0) * k / nz) then return false end
      end
    end
  end
  return true
end

-- The texture map of a placement: the caller's {old = new}, or with retexture every module texture onto the
-- room's wall texture (the room's own material).
local function texmap(ctx, r)
  if ctx.texmap then return ctx.texmap end
  if ctx.retexture and ctx.wall_texture then
    local m = {}
    for _, tname in ipairs(r.textures) do m[tname] = ctx.wall_texture end
    return m
  end
  return nil
end

-- Places a library module with its origin at (ox, oy, oz) and frame f; records it.
local function place(ctx, r, f, ox, oy, oz, len, hgt, dep, role, mirror)
  local tags = { kit = ctx.fn, ["kit.name"] = ctx.name, ["kit.role"] = role or r.role }
  local wx, wz = xf_w(ox, oz)
  local yaw = XF and ((f.yaw + XF.rot) % 360) or f.yaw
  local res = modules.place{ id = r.id, pos = { wx, oy, wz }, yaw = yaw, length = len, height = hgt, depth = dep,
    mirror = mirror or nil, textures = texmap(ctx, r), tags = tags }
  local list = {}
  for i, b in ipairs(res.brushes) do list[i] = b end
  local rec = { id = r.id, role = tags["kit.role"], category = r.category, pos = ged.vec(wx, oy, wz), yaw = yaw, size = res.size, brushes = list, source = r.source }
  ctx.placed[#ctx.placed + 1] = rec
  ctx.used_library = true
  count(ctx, r.category, rec.role, list)
  return rec
end

-- A hand-authored detail brush (level.brush spec without tags), box-mapped at the kit's density; a thin member (post,
-- rail, beam, pilaster shaft) in a grained texture then gets the grain laid along it (level.uv_align members = "only").
local function prim_brush(ctx, part, role, what, spec, tex)
  spec.detail = true
  spec.tags = { kit = ctx.fn, ["kit.name"] = ctx.name, ["kit.part"] = part, ["kit.role"] = role, ["kit.module"] = "primitive." .. what }
  spec.texture = tex
  local b
  if XF and spec.shape == "box" and not spec.points then
    -- In a turned frame a box is built from its eight world corners (no brush is moved after it is made).
    local cx, cy, cz = spec.pos[1], spec.pos[2], spec.pos[3]
    local hx, hy, hz = spec.size[1] / 2, spec.size[2] / 2, spec.size[3] / 2
    local pts = {}
    for _, sx in ipairs({ -1, 1 }) do
      for _, sz in ipairs({ -1, 1 }) do
        local wx, wz = xf_w(cx + sx * hx, cz + sz * hz)
        pts[#pts + 1] = { wx, cy - hy, wz }
        pts[#pts + 1] = { wx, cy + hy, wz }
      end
    end
    spec = { points = pts, detail = true, tags = spec.tags, texture = tex }
  elseif XF and spec.points then
    local pts = {}
    for i, p in ipairs(spec.points) do local wx, wz = xf_w(p[1], p[3]) pts[i] = { wx, p[2], wz } end
    spec.points = pts
  end
  if spec.points then
    b = level.brush_from_points(spec)
  else
    b = level.brush(spec)
    if XF then
      b:rotate_about("y", XF.rot, 0, 0, 0)
      b:move(XF.px, 0, XF.pz)
    end
  end
  if KIT.defaults.ppm then b:box_map{ ppm = KIT.defaults.ppm } else b:box_map{ tile = KIT.defaults.tile_metres } end
  if KIT.defaults.uv_align then
    -- (in a frame turned off the world axes the box map runs askew: align every face, as the layout aligns its walls)
    level.uv_align{ brushes = b, ppm = KIT.defaults.ppm or 64, keep_fitted = false, members = (XF and not XF.square) and "auto" or "only" }
  end
  return b
end

-- Records a group of hand-authored brushes as one placed element (pos in the current frame; recorded in the world).
local function prim_record(ctx, category, role, what, brushes, pos, yaw, size)
  local wx, wz = xf_w(pos[1], pos[3])
  if XF then yaw = ((yaw or 0) + XF.rot) % 360 end
  local rec = { id = "primitive." .. what, role = role, category = category, pos = ged.vec(wx, pos[2], wz), yaw = yaw or 0,
    size = ged.vec(size[1], size[2], size[3]), brushes = brushes, primitive = true }
  ctx.placed[#ctx.placed + 1] = rec
  count(ctx, category, role, brushes)
  return rec
end

-- An axis-aligned primitive box by world corners.
local function pbox(ctx, part, role, what, x0, y0, z0, x1, y1, z1, tex)
  return prim_brush(ctx, part, role, what,
    { shape = "box", pos = { (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2 }, size = { x1 - x0, y1 - y0, z1 - z0 } }, tex)
end

-- A box in a wall frame: u along the wall (from u0 to u1), v out of the wall (v0 to v1), y absolute.
local function wbox(ctx, wf, part, role, what, u0, u1, v0, v1, y0, y1, tex)
  local ax, az = wf.px + wf.tx * u0 + wf.nx * v0, wf.pz + wf.tz * u0 + wf.nz * v0
  local bx, bz = wf.px + wf.tx * u1 + wf.nx * v1, wf.pz + wf.tz * u1 + wf.nz * v1
  return pbox(ctx, part, role, what, min(ax, bx), y0, min(az, bz), max(ax, bx), y1, max(az, bz), tex)
end

-- A horizontal cylinder along x or z (axis "x"/"z") or vertical ("y") between two coordinates.
local function pcyl(ctx, part, role, what, axis, a0, a1, c1, c2, d, tex, sides)
  local len = a1 - a0
  local spec
  if axis == "x" then
    spec = { shape = "cylinder", pos = { (a0 + a1) / 2, c1, c2 }, size = { d, len, d }, rot = { roll = 90 }, sides = sides or 10 }
  elseif axis == "z" then
    spec = { shape = "cylinder", pos = { c1, c2, (a0 + a1) / 2 }, size = { d, len, d }, rot = { pitch = 90 }, sides = sides or 10 }
  else
    spec = { shape = "cylinder", pos = { c1, (a0 + a1) / 2, c2 }, size = { d, len, d }, sides = sides or 10 }
  end
  return prim_brush(ctx, part, role, what, spec, tex)
end

local function finish(ctx, extra)
  local note_text
  if ctx.fallback then
    note_text = "built hand-authored primitives: " .. table.concat(ctx.notes, "; ")
    if not WARNED[ctx.api] and not ctx.quiet and not QUIET then
      WARNED[ctx.api] = true
      log.warn(ctx.api .. ": " .. ctx.name .. " " .. note_text .. ".")
    end
  elseif #ctx.notes > 0 then
    note_text = table.concat(ctx.notes, "; ")
  else
    note_text = "library modules"
  end
  local r = { name = ctx.name, placed = ctx.placed, counts = ctx.counts, roles = ctx.roles, skipped = ctx.skipped,
    fallback = ctx.fallback, note = note_text, brushes = ctx.brushes, style = ctx.style }
  if extra then for k, v in pairs(extra) do r[k] = v end end
  return r
end

-- Marks the call as falling back to primitives (once per reason).
local function fallback(ctx, why)
  ctx.fallback = true
  note(ctx, why)
end

-- ---------------------------------------------------------------------------------------------
-- Spaces and walls.
-- ---------------------------------------------------------------------------------------------

-- The one space of a call (room = record or name, or space = record / {min=, max=}).
local function one_space(api, t)
  if t.room ~= nil and t.space ~= nil then K.fail(api, "give 'room' or 'space', not both.") end
  local src = t.room or t.space
  if src == nil then K.fail(api, "'room' (a kit room record or kit.name) or 'space' ({min=, max=}) is required.") end
  local arg = {}
  if type(src) == "table" and src.min ~= nil and src.max ~= nil and src.kind == nil and src.brush == nil and src.brushes == nil then
    arg.area = src
  else
    arg.room = src
  end
  return P.spaces(api, arg, K.fail)[1]
end

-- The boxes a call details: the space itself, or — for a record made of several air boxes (a kit.corridor path, a
-- shaped room) — each air box on its own (its walls are the record's walls; where a neighbouring box continues the
-- space a wall samples as open).
local function boxes_of(api, t)
  local s = one_space(api, t)
  local airs = {}
  for _, b in ipairs(s.brushes or {}) do
    if b.air and not b.portal then airs[#airs + 1] = b end
  end
  if #airs < 2 then return { s } end
  local out = {}
  for i, b in ipairs(airs) do
    local bb = b.bounds
    out[i] = { name = s.name .. "#" .. i, kind = s.kind, x0 = bb.min.x, y0 = bb.min.y, z0 = bb.min.z, x1 = bb.max.x, y1 = bb.max.y, z1 = bb.max.z,
      floor_y = bb.min.y, sky = s.sky, brushes = { b } }
  end
  return out
end

-- The room's own wall texture (the first air brush's side face), for retexture.
local function wall_texture_of(s)
  local b = s.brushes and s.brushes[1]
  if not b then return nil end
  for _, f in ipairs(b.faces) do
    local n = f.normal
    if abs(n.y) < 0.5 and f.texture and f.texture ~= "" then return f.texture end
  end
  return nil
end

-- A wall of the space's box: side "+x" is the wall at x1 (the room is on its -x side). wf = {side, px, pz (a point
-- on the wall plane at t = 0), tx, tz (tangent), nx, nz (inward normal, into the room), t0, t1 (tangent range)}.
local function wall_frame(s, side)
  if side == "+x" then return { side = side, px = s.x1, pz = 0, tx = 0, tz = 1, nx = -1, nz = 0, t0 = s.z0, t1 = s.z1 } end
  if side == "-x" then return { side = side, px = s.x0, pz = 0, tx = 0, tz = 1, nx = 1, nz = 0, t0 = s.z0, t1 = s.z1 } end
  if side == "+z" then return { side = side, px = 0, pz = s.z1, tx = 1, tz = 0, nx = 0, nz = -1, t0 = s.x0, t1 = s.x1 } end
  return { side = side, px = 0, pz = s.z0, tx = 1, tz = 0, nx = 0, nz = 1, t0 = s.x0, t1 = s.x1 }
end

-- World point at tangent t, offset v into the room.
local function wpt(wf, t, v) return wf.px + wf.tx * t + wf.nx * v, wf.pz + wf.tz * t + wf.nz * v end

-- Samples a wall: each 0.125 m along it is "wall" (solid just behind the plane at every height, air in front),
-- "open" (air behind the plane: a doorway, window or another space) or "blocked" (solid in front of the plane:
-- something already stands there). Returns runs {kind, a, b} plus openings with their sill / top / thickness.
local function wall_profile(s, wf)
  local pr = probe()
  local y0, y1 = s.floor_y or s.y0, s.y1
  local H = y1 - y0
  local hs = { y0 + 0.35, y0 + 1.2, y0 + 2.1, y0 + H * 0.55, y1 - 0.35 }
  local step = 0.125
  local kinds, ts = {}, {}
  local n = floor((wf.t1 - wf.t0) / step)
  for i = 0, n - 1 do
    local t = wf.t0 + (i + 0.5) * step
    local ox, oz = wpt(wf, t, -0.06)
    local ix, iz = wpt(wf, t, 0.06)
    local kind = "wall"
    for _, y in ipairs(hs) do
      if y > y0 and y < y1 and pr:air_exact(ox, y, oz) then kind = "open" break end
    end
    if kind == "wall" then
      for _, y in ipairs({ y0 + 0.35, y0 + 1.2, y1 - 0.35 }) do
        if not pr:air_exact(ix, y, iz) then kind = "blocked" break end
      end
    end
    kinds[#kinds + 1] = kind
    ts[#ts + 1] = t
  end
  local runs = {}
  local i = 1
  while i <= #kinds do
    local j = i
    while j < #kinds and kinds[j + 1] == kinds[i] do j = j + 1 end
    runs[#runs + 1] = { kind = kinds[i], a = ts[i] - step / 2, b = ts[j] + step / 2, i = i, j = j }
    i = j + 1
  end
  -- Openings: their vertical extent (behind the plane at the middle) and the wall thickness beside them.
  for _, r in ipairs(runs) do
    if r.kind == "open" then
      local tm = (r.a + r.b) / 2
      local ox, oz = wpt(wf, tm, -0.06)
      local lo, hi = nil, nil
      local y = y0 + 0.03
      while y < y1 - 0.02 do
        if pr:air_exact(ox, y, oz) then
          if not lo then lo = y end
          hi = y
        elseif lo then
          break
        end
        y = y + 0.0625
      end
      r.sill = (lo or y0) - 0.03
      if r.sill < y0 + 0.05 then r.sill = y0 end
      r.top = min(y1, (hi or y1) + 0.0625)
      -- Wall thickness: from the plane, how far the solid beside the jamb goes before air.
      local thick = nil
      for _, tt in ipairs({ r.a - 0.15, r.b + 0.15 }) do
        if tt > wf.t0 and tt < wf.t1 then
          local d = 0.06
          local found = false
          while d < 4 do
            local qx, qz = wpt(wf, tt, -d)
            if pr:air_exact(qx, y0 + 1.0, qz) then found = true break end
            d = d + 0.0625
          end
          if found then thick = thick and min(thick, d - 0.03) or (d - 0.03) end
        end
      end
      r.thick = thick
      r.full = (r.top >= y1 - 0.1)
    end
  end
  return runs
end

-- Openings of a space's walls as frame records {pos (floor point on the wall plane), dir (outward), width, height,
-- sill, thickness, side}: door- and window-shaped openings only (not a whole open side).
local function openings_of(s, sides, profile)
  local out = {}
  for _, side in ipairs(sides) do
    local wf = wall_frame(s, side)
    for _, r in ipairs(profile or wall_profile(s, wf)) do
      if r.kind == "open" and r.thick and not r.full and (r.b - r.a) >= 0.9 and (r.b - r.a) <= 0.8 * (wf.t1 - wf.t0) + 0.01 then
        local tm = (r.a + r.b) / 2
        local x, z = wpt(wf, tm, 0)
        out[#out + 1] = { side = side, pos = { x, r.sill, z }, nx = -wf.nx, nz = -wf.nz, width = r.b - r.a, height = r.top - r.sill,
          sill = r.sill - (s.floor_y or s.y0), thickness = r.thick, tx = wf.tx, tz = wf.tz }
      end
    end
  end
  return out
end

-- A wall profile (wall_profile's runs) cut to the tangent range a..b: the layout reads a wall once along its whole
-- length and details each stretch of one floor from it.
local function clip_profile(profile, a, b)
  local out = {}
  for _, r in ipairs(profile) do
    local ca, cb = max(r.a, a), min(r.b, b)
    if cb - ca > 1e-6 then
      local c = {}
      for k, v in pairs(r) do c[k] = v end
      c.a, c.b = ca, cb
      out[#out + 1] = c
    end
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- kit.wall_detail
-- ---------------------------------------------------------------------------------------------

local function has_door_texture(r)
  for _, tname in ipairs(r.textures) do
    local l = tname:lower()
    if l:find("door", 1, true) or l:find("elev", 1, true) or l:find("lift", 1, true) or l:find("light", 1, true) or l:find("lite", 1, true)
      or l:find("_a%.") or l:find("sky", 1, true) or l:find("invisible", 1, true) or l:find("window", 1, true) or l:find("gls_", 1, true) then
      return true
    end
  end
  return false
end

local function pilaster_ok(H, maxw, maxd)
  return function(r)
    local sl = r.stretch.y
    if r.attach ~= "wall" or not sl or r.has_air or r.brushes > 6 then return false end
    if abs(r.y0) > 0.06 or r.sx < 0.3 or r.sx > maxw or r.sz < 0.1 or r.sz > maxd or r.sy < 2 then return false end
    local fixed = r.sy - (sl[2] - sl[1])
    if fixed > 0.45 * H or H - fixed < 0.25 then return false end
    -- Natural height within -50 % / +60 % of the wall: never an extreme stretch or squash.
    if r.sy < 0.5 * H or r.sy > 1.6 * H then return false end
    if has_door_texture(r) or has_vbm(r) then return false end
    return r.sx <= 0.6 * r.sy
  end
end

local function trim_ok(r)
  if r.attach ~= "wall" or not r.stretch.x or r.has_air or r.brushes > 3 then return false end
  -- A profile run: flat against the wall, never deeper than it is tall (that is a shelf).
  if r.sy < 0.12 or r.sy > 1.0 or r.sz < 0.04 or r.sz > 0.3 or r.sz > 1.2 * r.sy then return false end
  if has_door_texture(r) or has_vbm(r) then return false end
  -- The stretch zone must be most of the trim (a plain profile run, not a patterned panel).
  return (r.stretch.x[2] - r.stretch.x[1]) >= 0.6 * r.sx
end

local SIGN_WORDS = { "sign", "rules", "exit", "logo", "danger", "warn", "highvolt", "high_volt", "res_", "guard", "heavy_equip", "safety",
  "watchstep", "airlock", "lockerdoor", "pipe" }

local function panel_ok(r)
  if r.attach ~= "wall" or r.has_air or r.brushes > 8 then return false end
  if r.sz > 0.4 or r.sz < 0.02 or r.sx < 1.0 or r.sx > 6 or r.sy < 0.75 or r.sy > 4.5 then return false end
  -- A plate fitted to a bay (it stretches along the wall over most of its width), not a one-off sign, fitting
  -- or a brace; flat-backed and shallow against its size.
  if not r.stretch.x or (r.stretch.x[2] - r.stretch.x[1]) < 0.5 * r.sx then return false end
  if r.sz > 0.2 * min(r.sx, r.sy) or r.brushes > 4 or has_vbm(r) then return false end
  for _, tname in ipairs(r.textures) do
    local l = tname:lower()
    for _, w in ipairs(SIGN_WORDS) do if l:find(w, 1, true) then return false end end
  end
  return not has_door_texture(r)
end

local function shares(a, b, key)
  for k in pairs(a[key]) do if b[key][k] then return true end end
  return false
end

local function prefixes(r)
  local out = {}
  for _, tname in ipairs(r.textures) do
    local p = tname:lower():match("^(%a+_)")
    if p then out[p] = true end
  end
  return out
end

-- The best partner of pilaster p (or the reference module) in list: same source level first, then a shared
-- texture family, a shared material prefix and the same primary style, then score. Returns entry, how.
local function partner(list, p, src, exclude, prefer)
  local best, bw, bhow = nil, -1, nil
  local pp = p and prefixes(p) or {}
  for _, e in ipairs(list) do
    local r = e.r
    if r.id ~= exclude then
      local w, how = e.w, "style"
      if src and r.sources[src] then w, how = w + 1.0, "source"
      elseif p and shares(r, p, "fams") then w, how = w + 0.6, "family" end
      if p then
        for k in pairs(prefixes(r)) do if pp[k] then w = w + 0.15 break end end
        if r.style == p.style then w = w + 0.1 end
      end
      if prefer and prefer[r.category] then w = w + prefer[r.category] end
      if w > bw then best, bw, bhow = e, w, how end
    end
  end
  return best, bhow
end

-- The coherent wall sets {pilaster, base, top, band, panel, how, w}: one per pilaster look (its texture family),
-- each with its best source level, best first.
local function wall_sets(ctx, H, want)
  local pil = want.pilasters and ranked(ctx, { "pilaster" }, pilaster_ok(H, want.maxw or 1.2, want.maxd or 0.6)) or {}
  local trims = ranked(ctx, { "base_trim", "top_trim", "band" }, trim_ok)
  local panels = want.panels and ranked(ctx, { "wall_panel" }, panel_ok) or {}
  local sets = {}
  local function add(p, src)
    local ref = p and p.r or nil
    local set = { pilaster = ref, w = p and p.w or 0, how = {}, off_style = p and p.off_style }
    local base, h1 = partner(trims, ref, src, nil, { base_trim = 0.3 })
    if base then
      set.base, set.how.base = base.r, h1
      set.w = set.w + 0.5 * base.w + (h1 == "source" and 0.3 or (h1 == "family" and 0.15 or 0))
      local top, h2 = partner(trims, ref, src, base.r.id, { top_trim = 0.3, band = 0.15 })
      if not top then top, h2 = base, h1 end
      set.top, set.how.top = top.r, h2
      set.w = set.w + 0.3 * top.w + (h2 == "source" and 0.2 or 0)
      set.band = top.r
    end
    if #panels > 0 then
      local pan, h3 = partner(panels, ref, src, nil, nil)
      -- A panel only when it belongs to the donor's design (same level or texture family): a stranger repeated in
      -- every bay reads mechanical.
      if pan and ref and h3 ~= "source" and h3 ~= "family" then pan = nil end
      if pan then
        set.panel, set.how.panel = pan.r, h3
        set.w = set.w + 0.2 * pan.w + (h3 == "source" and 0.1 or 0)
      end
    end
    set.source = src
    sets[#sets + 1] = set
  end
  local fams = {}
  local nfam = 0
  for i = 1, #pil do
    local p = pil[i]
    local fk = family(p.r.textures[1] or p.r.id)
    if not fams[fk] then
      fams[fk] = 0
      nfam = nfam + 1
    end
    -- At most two pilasters of one look are considered, and at most eight looks.
    if fams[fk] < 2 and (nfam <= 8 or fams[fk] > 0) then
      fams[fk] = fams[fk] + 1
      local srcs = {}
      for src in pairs(p.r.sources) do srcs[#srcs + 1] = src end
      P.sort(srcs, function(a, b) return a < b end)
      if #srcs == 0 then srcs[1] = false end
      for _, src in ipairs(srcs) do add(p, src or nil) end
    end
  end
  if #pil == 0 and #trims > 0 then add(nil, nil) end
  P.sort(sets, function(a, b)
    if a.w ~= b.w then return a.w > b.w end
    local ia, ib = a.pilaster and a.pilaster.id or "", b.pilaster and b.pilaster.id or ""
    if ia ~= ib then return ia < ib end
    return tostring(a.source) < tostring(b.source)
  end)
  -- One set per pilaster look (its best source), so the seed chooses between genuinely different designs.
  local out, seen = {}, {}
  for _, st in ipairs(sets) do
    local k = st.pilaster and family(st.pilaster.textures[1] or st.pilaster.id) or "none"
    if not seen[k] then seen[k] = true out[#out + 1] = st end
  end
  return out
end

-- Primitive wall parts (style palette): a pilaster with base and cap, trims as plain strips.
local PRIM_PIL = { w = 0.6, d = 0.25, base_w = 0.8, base_d = 0.35, base_h = 0.45, cap_h = 0.35 }

local function prim_pilaster(ctx, wf, t, y0, y1)
  local p = PRIM_PIL
  local list = {
    wbox(ctx, wf, "pilaster", "theme_detail", "pilaster", t - p.base_w / 2, t + p.base_w / 2, 0, p.base_d, y0, y0 + p.base_h, ctx.pal.frame),
    wbox(ctx, wf, "pilaster", "theme_detail", "pilaster", t - p.w / 2, t + p.w / 2, 0, p.d, y0 + p.base_h, y1 - p.cap_h, ctx.pal.trim),
    wbox(ctx, wf, "pilaster", "theme_detail", "pilaster", t - p.base_w / 2, t + p.base_w / 2, 0, p.base_d, y1 - p.cap_h, y1, ctx.pal.frame),
  }
  local x, z = wpt(wf, t, 0)
  return prim_record(ctx, "pilaster", "theme_detail", "pilaster", list, { x, y0, z }, yaw_of(wf.nx, wf.nz), { p.base_w, y1 - y0, p.base_d })
end

-- Lays one wall's detail. Returns the pilaster centres (tangent) it placed.
local function detail_wall(ctx, s, wf, set, opts, budget)
  local y0, y1 = s.floor_y or s.y0, s.y1
  local H = y1 - y0
  local f = frame_of(yaw_of(wf.nx, wf.nz))
  local runs = opts.profile and clip_profile(opts.profile, wf.t0, wf.t1) or wall_profile(s, wf)
  local pil = set and set.pilaster
  local pw = pil and pil.sx or PRIM_PIL.base_w
  local pd = pil and pil.sz or PRIM_PIL.base_d
  local centres = {}
  -- Pilaster positions for the whole wall first: run ends (corners, jambs, obstacles) take priority, then the bays
  -- at the pitch (no bay under 2.5 m; a wall under 6 m long only gets its end pilasters); positions closer than
  -- 1.5 m to an accepted one are merged away.
  local wall_len = wf.t1 - wf.t0
  local function inset(nb, at_a)
    -- A corner gets one pilaster, on the wall running along x (just clear of the other wall's trim); the wall
    -- running along z starts its first pilaster half a bay in, so two never stand side by side across a corner.
    -- (opts.far names the wall ends that take the half-bay inset instead: {a =, b =}.) A jamb keeps 0.4 m.
    if nb == nil then
      local far = (opts.far ~= nil) and ((at_a and opts.far.a) or (not at_a and opts.far.b)) or (opts.far == nil and wf.tx == 0)
      if not far then return pd + 0.05 + pw / 2 end
      return max(1.5 + pw / 2, opts.pitch / 2)
    end
    if nb.kind == "open" then return 0.4 + pw / 2 end
    return 0.15 + pw / 2
  end
  local ends, mids = {}, {}
  for ri, r in ipairs(runs) do
    if r.kind == "wall" and opts.pilasters then
      local a, b = r.a + inset(runs[ri - 1], true), r.b - inset(runs[ri + 1], false)
      if b - a >= -1e-6 then
        if b - a < 0.5 then
          ends[#ends + 1] = { t = (a + b) / 2, run = ri }
        else
          ends[#ends + 1] = { t = a, run = ri }
          ends[#ends + 1] = { t = b, run = ri }
          if wall_len >= 6 then
            local n = max(1, floor((b - a) / opts.pitch + 0.5))
            while n > 1 and (b - a) / n < 2.5 do n = n - 1 end
            for k = 1, n - 1 do mids[#mids + 1] = { t = a + (b - a) * k / n, run = ri } end
          end
        end
      end
    end
  end
  local accepted = {}
  for _, list in ipairs({ ends, mids }) do
    for _, e in ipairs(list) do
      local near = false
      for _, q in ipairs(accepted) do if abs(q.t - e.t) < 1.5 then near = true break end end
      if not near then accepted[#accepted + 1] = e end
    end
  end

  for ri, r in ipairs(runs) do
    if r.kind == "wall" then
      local prev, nxt = runs[ri - 1], runs[ri + 1]
      local ts = {}
      for _, e in ipairs(accepted) do if e.run == ri then ts[#ts + 1] = e.t end end
      P.sort(ts, function(p1, p2) return p1 < p2 end)
      -- Pilasters.
      local placed_t = {}
      for _, t in ipairs(ts) do
        local ok = true
        local x, z = wpt(wf, t, 0)
        if pil then
          local nb = stretched(pil, nil, H, nil)
          if not nb then ok = false skip(ctx, "pilaster", "cannot stretch to the wall height") end
          if ok then
            local ox, oy, oz = origin_for(f, (pil.x0 + pil.x1) / 2, nb.y0, 0, x, y0, z)
            local wb = world_box(f, ox, oy, oz, { x0 = nb.x0, x1 = nb.x1, y0 = nb.y0, y1 = nb.y1, z0 = max(nb.z0, 0.02), z1 = nb.z1 })
            if not box_clear(wb) then
              skip(ctx, "pilaster", "something already stands against the wall at " .. fmt(x) .. ", " .. fmt(z))
            elseif pd >= 0.3 and not budget:take(pw * pd) then
              skip(ctx, "pilaster", "walk budget")
            else
              place(ctx, pil, f, ox, oy, oz, nil, H, nil, "theme_detail")
              placed_t[#placed_t + 1] = t
            end
          end
        else
          local wb = { x0 = min(x, x + wf.nx * pd) - pw / 2 * abs(wf.tx), x1 = max(x, x + wf.nx * pd) + pw / 2 * abs(wf.tx),
            z0 = min(z, z + wf.nz * pd) - pw / 2 * abs(wf.tz), z1 = max(z, z + wf.nz * pd) + pw / 2 * abs(wf.tz), y0 = y0, y1 = y1 }
          if box_clear(wb) then
            prim_pilaster(ctx, wf, t, y0, y1)
            placed_t[#placed_t + 1] = t
          else
            skip(ctx, "pilaster", "something already stands against the wall at " .. fmt(x) .. ", " .. fmt(z))
          end
        end
      end
      for _, t in ipairs(placed_t) do centres[#centres + 1] = t end
      -- Segments between pilasters (and run ends): trims, bands, panels.
      local cuts = { r.a + ((prev == nil) and (pd + 0.02) or 0) }
      for _, t in ipairs(placed_t) do cuts[#cuts + 1] = t - pw / 2 cuts[#cuts + 1] = t + pw / 2 end
      cuts[#cuts + 1] = r.b - ((nxt == nil) and (pd + 0.02) or 0)
      for k = 1, #cuts - 1, 2 do
        local sa, sb = cuts[k], cuts[k + 1]
        local L = sb - sa
        if L >= 0.6 then
          local tm = (sa + sb) / 2
          local x, z = wpt(wf, tm, 0)
          local function strip(r2, part, ybottom, ytop)
            -- A trim run along the segment: bottom at ybottom (or top at ytop).
            if r2 then
              local nb = stretched(r2, L, nil, nil)
              if not nb then skip(ctx, part, "cannot stretch to " .. fmt(L) .. " m") return nil end
              local yl = ybottom and (ybottom) or (ytop - (nb.y1 - nb.y0))
              local ox, oy, oz = origin_for(f, (nb.x0 + nb.x1) / 2, nb.y0, 0, x, yl, z)
              local wb = world_box(f, ox, oy, oz, { x0 = nb.x0, x1 = nb.x1, y0 = nb.y0, y1 = nb.y1, z0 = max(nb.z0, 0.02), z1 = nb.z1 })
              if not box_clear(wb) then skip(ctx, part, "blocked at " .. fmt(x) .. ", " .. fmt(z)) return nil end
              local rec = place(ctx, r2, f, ox, oy, oz, L, nil, nil, "theme_detail")
              rec.category_as = part
              return yl, yl + (nb.y1 - nb.y0)
            end
            local h, d = (part == "base_trim") and 0.3 or 0.25, (part == "band") and 0.12 or 0.15
            local yl = ybottom or (ytop - h)
            if modules.available then fallback(ctx, "primitive " .. part .. " (no fitting module in the donor set)") end
            local b = wbox(ctx, wf, part, "theme_detail", part, sa, sb, 0, d, yl, yl + h, ctx.pal.frame)
            prim_record(ctx, part, "theme_detail", part, { b }, { x, yl, z }, f.yaw, { L, h, d })
            return yl, yl + h
          end
          local base_top, top_bottom = y0, y1
          if opts.base then
            local _, yt = strip(set and set.base, "base_trim", y0, nil)
            if yt then base_top = yt end
          end
          if opts.top then
            local yb = strip(set and set.top, "top_trim", nil, y1)
            if yb then top_bottom = yb end
          end
          if opts.band then
            local yband = y0 + clamp(H * 0.55, 2.6, H - 1.2)
            if opts.door_top and opts.door_top > y0 + 1 then yband = max(yband, opts.door_top + 0.3) end
            if yband + 0.5 < top_bottom - 0.3 then strip(set and set.band, "band", yband, nil) end
          end
          if opts.panels and (opts.panel_sides == nil or opts.panel_sides[wf.side]) and L >= 2.5 then
            local pan = set and set.panel
            local lo, hi = base_top + 0.35, min(top_bottom - 0.35, y0 + 4.5)
            if pan and hi - lo >= 0.5 then
              local want_len = nil
              if pan.sx > L - 0.6 then want_len = L - 0.6 end
              local want_h = nil
              if pan.sy > hi - lo then want_h = hi - lo end
              local nb, why = stretched(pan, want_len, want_h, nil)
              if nb then
                local ph = nb.y1 - nb.y0
                local yl = clamp(y0 + (pan.mount or 1.0), lo, hi - ph)
                local ox, oy, oz = origin_for(f, (nb.x0 + nb.x1) / 2, nb.y0, 0, x, yl, z)
                local wb = world_box(f, ox, oy, oz, { x0 = nb.x0, x1 = nb.x1, y0 = nb.y0, y1 = nb.y1, z0 = max(nb.z0, 0.02), z1 = nb.z1 })
                if box_clear(wb) then
                  place(ctx, pan, f, ox, oy, oz, want_len, want_h, nil, "theme_detail")
                else
                  skip(ctx, "wall_panel", "blocked at " .. fmt(x) .. ", " .. fmt(z))
                end
              else
                skip(ctx, "wall_panel", why)
              end
            elseif not pan and opts.prim_panels then
              -- A recessed-looking panel: a thin plate with a frame strip around it.
              local pw2, ph2 = min(L - 1.0, 3.0), min(hi - lo, 2.2)
              if pw2 >= 1.2 and ph2 >= 0.8 then
                local yl = lo + 0.2
                local b1 = wbox(ctx, wf, "wall_panel", "theme_detail", "panel", -pw2 / 2 + (tm - 0), pw2 / 2 + tm, 0, 0.06, yl, yl + ph2, ctx.pal.panel)
                prim_record(ctx, "wall_panel", "theme_detail", "panel", { b1 }, { x, yl, z }, f.yaw, { pw2, ph2, 0.06 })
              end
            end
          end
        end
      end
    end
  end
  return centres
end

-- A walk budget for wall parts deeper than 0.3 m standing on the floor (a share of the floor area).
local function new_budget(s, share)
  local area = (s.x1 - s.x0) * (s.z1 - s.z0)
  local b = { left = area * share }
  function b:take(a)
    if a > self.left then return false end
    self.left = self.left - a
    return true
  end
  return b
end

local function walls_of(api, t)
  local walls = { "+x", "-x", "+z", "-z" }
  if t.walls ~= nil then
    if type(t.walls) ~= "table" or #t.walls == 0 then K.fail(api, "'walls' must list sides like {\"+x\", \"-z\"}.") end
    for i, w in ipairs(t.walls) do
      if not DIRS[w] then K.fail(api, "'walls[" .. i .. "]' must be \"+x\", \"-x\", \"+z\" or \"-z\" (got " .. tostring(w) .. ").") end
    end
    walls = t.walls
  end
  return walls
end

local function parts_of(api, t, H)
  local p = { pilasters = true, base = true, top = true, band = H >= 6, panels = true, recesses = false }
  if t.parts ~= nil then
    if type(t.parts) ~= "table" then K.fail(api, "'parts' must be a table like {pilasters = true, band = false}.") end
    K.check_keys(api .. " parts", t.parts, { "pilasters", "base", "top", "band", "panels", "recesses" })
    for k, v in pairs(t.parts) do p[k] = K.boolean(api, "parts." .. k, v, p[k]) end
  end
  return p
end

local function wall_detail_impl(api, t, ctx, boxes)
  local s = boxes[1]
  local H = 0
  for _, b in ipairs(boxes) do H = max(H, b.y1 - (b.floor_y or b.y0)) end
  local parts = parts_of(api, t, H)
  local walls = walls_of(api, t)
  ctx.wall_texture = wall_texture_of(s)
  ctx.material = ctx.wall_texture and ctx.wall_texture:lower():match("^(%a+_)") or nil
  ctx.wall_family = ctx.wall_texture and family(ctx.wall_texture) or nil
  ctx.wall_brightness = ctx.wall_texture and modules.available and modules.texture_brightness(ctx.wall_texture) or nil
  local narrow = false
  for _, b in ipairs(boxes) do if min(b.x1 - b.x0, b.z1 - b.z0) < 6 then narrow = true end end
  local pitch0 = t.pitch and K.positive(api, "pitch", t.pitch) or clamp(max(4, H * 0.5), 3, 8)
  parts.maxw = max(0.5, pitch0 / 6)
  parts.maxd = narrow and 0.3 or 0.6
  local sets = wall_sets(ctx, H, parts)
  local set = nil
  if #sets > 0 then
    local k = min(#sets, 3)
    set = sets[ctx.st.int(1, k)]
  end
  if not modules.available then
    fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")")
  elseif not set or (parts.pilasters and not set.pilaster) then
    fallback(ctx, "no " .. ctx.style .. " pilaster fits a " .. fmt(H) .. " m wall")
  end
  if set and set.pilaster == nil and parts.pilasters then set = nil end
  -- No pilaster that belongs to the style (a light-strip tech pilaster in a concrete bunker): the style's own
  -- primitive pilaster reads better than a stranger.
  if set and set.pilaster and set.off_style then
    note(ctx, "no " .. ctx.style .. " pilaster in the library; " .. set.pilaster.id .. " is off-style, so primitive pilasters")
    ctx.fallback = true
    set.pilaster = nil
  end
  -- Pitch: the pilaster's own recurrence where it was found, at least half the wall height (tall halls get wider
  -- bays), 3 to 8 m.
  local pitch = t.pitch and K.positive(api, "pitch", t.pitch)
    or clamp(max((set and set.pilaster and set.pilaster.pitch) or 4, H * 0.5), 3, 8)
  -- Door tops of the room (a band sits above them).
  local door_top = nil
  for _, b in ipairs(boxes) do
    for _, o in ipairs(openings_of(b, walls)) do
      local top = o.pos[2] + o.height
      if not door_top or top > door_top then door_top = top end
    end
  end
  local opts = { pilasters = parts.pilasters, base = parts.base, top = parts.top, band = parts.band, panels = parts.panels,
    prim_panels = false, pitch = pitch, door_top = door_top }
  if narrow then
    -- A corridor or narrow room: panels on one long wall only (never both walls at head height).
    local b = boxes[1]
    opts.panel_sides = ((b.x1 - b.x0) < (b.z1 - b.z0)) and { ["+x"] = true } or { ["+z"] = true }
  end
  local centres = {}
  for bi, b in ipairs(boxes) do
    local budget = new_budget(b, 0.015)
    for _, side in ipairs(walls) do
      local wf = wall_frame(b, side)
      local key = (#boxes > 1) and (side .. "#" .. bi) or side
      centres[key] = detail_wall(ctx, b, wf, set, opts, budget)
    end
  end
  if parts.recesses then skip(ctx, "recess", "recesses need at least 1 m of solid known behind the wall; not placed by wall_detail") end
  local donor = nil
  if set then
    donor = { source = set.source, pilaster = set.pilaster and set.pilaster.id, base = set.base and set.base.id, top = set.top and set.top.id,
      panel = set.panel and set.panel.id, how = set.how }
    note(ctx, "donor " .. tostring(set.source or set.pilaster and set.pilaster.style))
  end
  return { pitch = pitch, pilaster_positions = centres, donor = donor, set = set }
end

---@kit kit.wall_detail{room|space, style?, seed?, walls?, pitch?, parts?, keep?, custom?, textures?, retexture?, name?} → detail — Layers a room's walls the way RF mappers do: pilasters at a regular pitch (one each side of every doorway, 0.4 m off the jamb, corners kept clear) stretched floor to ceiling, base and top trims running between them, a band on tall walls and a panel centred in every bay of 2.5 m or more — one coherent set of library modules from one donor level, sitting exactly on the wall plane; hand-authored primitives without a library. Returns {name, placed, counts, roles, skipped, fallback, note, brushes, pitch, pilaster_positions, donor}.
---@opt room: record|string — The room: a kit record (room, corridor, shaped room) or a kit.name (give room or space).
---@opt space: {min=, max=} — A box whose walls are detailed (instead of room).
---@opt style: string = "industrial" — Style family: industrial, bunker, admin, mine, cave, canyon, mars_base, lab, tech, generic (or a kit.dress theme name).
---@opt seed: integer = 1 — Picks among the best-scoring coherent sets; the same options, level and seed give the same result.
---@opt walls: string[] = all four — The sides to detail: "+x" (the wall at max x), "-x", "+z", "-z".
---@opt pitch: number = the pilaster's suggested pitch (3 to 8 m), else 4 — Pilaster spacing in metres.
---@opt parts: {pilasters=, base=, top=, band=, panels=, recesses=} = pilasters, base, top, panels on; band on walls 6 m or taller — Which layers to build.
---@opt keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---@opt custom: boolean = false — Allow modules that need custom (non-stock) textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the room's own wall texture.
---@opt name: string = wall_detail<n> — Names the detail (tags).
function kit.wall_detail(t)
  local api = "kit.wall_detail"
  K.check_keys(api, t, { "room", "space", "style", "seed", "walls", "pitch", "parts", "keep", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "wall_detail", t.name)
  local ctx = new_ctx(api, "wall_detail", t, name)
  local extra = wall_detail_impl(api, t, ctx, boxes_of(api, t))
  extra.set = nil
  return finish(ctx, extra)
end

-- ---------------------------------------------------------------------------------------------
-- kit.frame_opening
-- ---------------------------------------------------------------------------------------------

local function frame_ok(W, Hd)
  return function(r)
    local o = r.opening
    -- (a frame is two jambs and a lintel at least: fewer brushes is a sign or a single trim over a door, which,
    -- stretched to a doorway, can fill it)
    if not o or r.attach ~= "opening" or r.has_air or r.brushes > 8 or r.brushes < 3 then return false end
    for _, tname in ipairs(r.textures) do
      local l = tname:lower()
      for _, w in ipairs(SIGN_WORDS) do if w ~= "pipe" and l:find(w, 1, true) then return false end end
    end
    if o.width < 1.0 or o.height < 1.8 or r.sz < 0.15 then return false end
    if has_door_texture(r) then
      -- frames named after door trims are fine; door leaves are not.
      for _, tname in ipairs(r.textures) do
        local l = tname:lower()
        if not l:find("trim", 1, true) and (l:find("door", 1, true) or l:find("_a%.")) then return false end
      end
    end
    -- Width: the X slice must take the difference; never narrower than the opening.
    local dw = W - o.width + 0.02
    if abs(dw) > 1e-3 then
      local sl = r.stretch.x
      if not sl or sl[2] - sl[1] + dw < 0.05 then return false end
    end
    local dh = Hd - o.height + 0.02
    if dh > 0 then
      local sl = r.stretch.y
      if not sl or sl[2] - sl[1] + dh < 0.05 then return false end
    elseif dh < -0.6 then
      return false
    end
    return r.sx - o.width <= 2.4 and r.sy - o.height <= 2.0
  end
end

-- Frames one opening record {pos, nx, nz (outward), width, height, thickness}.
local function frame_one(ctx, o, key)
  local W, Hd, T = o.width, o.height, o.thickness or 1.0
  local px, py, pz = o.pos[1], o.pos[2], o.pos[3]
  -- Mid-plane of the wall: the opening's face plane moved outward by half the thickness.
  local mx, mz = px + o.nx * T / 2, pz + o.nz * T / 2
  local fin = frame_of(yaw_of(-o.nx, -o.nz)) -- local +Z into the room the call works from
  -- (o.kind = "door" or "window" keeps to that kind's frames: a window frame's sill never lands across a doorway)
  local cats = (o.kind == "door" and { "door_frame" }) or (o.kind == "window" and { "window_frame" }) or { "door_frame", "window_frame" }
  local cand = ranked(ctx, cats, frame_ok(W, Hd))
  if #cand > 0 then
    local e = cand[ctx.st.int(1, min(3, #cand))]
    local r = e.r
    -- 2 cm of slack on each: the harvested opening size is quantised, and a frame must never narrow the opening.
    local len = r.sx + (W - r.opening.width) + 0.02
    local hgt = nil
    if Hd - r.opening.height > -0.02 then hgt = r.sy + (Hd - r.opening.height) + 0.02 end
    if abs(len - r.sx) < 1e-3 then len = nil end
    local nb = stretched(r, len, hgt, nil)
    if nb then
      local recs = {}
      local cx = (nb.x0 + nb.x1) / 2
      if r.sz >= T + 0.06 then
        local cz = (nb.z0 + nb.z1) / 2
        local ox, oy, oz = origin_for(fin, cx, 0, cz, mx, py, mz)
        recs[1] = place(ctx, r, fin, ox, oy, oz, len, hgt, nil, "theme_detail")
      else
        -- Thinner than the wall: one frame on each face, protruding 0.08 m.
        for _, sgn in ipairs({ 1, -1 }) do
          local f2 = sgn > 0 and fin or frame_of((fin.yaw + 180) % 360)
          local face = T / 2 + 0.08
          local ox, oy, oz = origin_for(f2, cx, 0, nb.z1, mx + (-o.nx) * face * sgn, py, mz + (-o.nz) * face * sgn)
          recs[#recs + 1] = place(ctx, r, f2, ox, oy, oz, len, hgt, nil, "theme_detail")
        end
      end
      for _, rc in ipairs(recs) do for _, b in ipairs(rc.brushes) do b:tag("kit.opening", key) end end
      return true
    end
  end
  -- Primitive frame: jambs and a lintel on both faces, outside the clear opening.
  if modules.available then fallback(ctx, "no frame module fits a " .. fmt(W) .. " x " .. fmt(Hd) .. " m opening")
  else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  local jw, pr, lh = 0.3, 0.12, 0.3
  local list = {}
  for _, sgn in ipairs({ 1, -1 }) do
    -- Face plane on this side: sgn = 1 the room side (the opening's own plane), -1 the far side.
    local fx, fz = px + o.nx * (sgn > 0 and 0 or T), pz + o.nz * (sgn > 0 and 0 or T)
    local vx, vz = -o.nx * sgn, -o.nz * sgn -- out of the wall face
    local wf = { px = fx, pz = fz, tx = o.tx, tz = o.tz, nx = vx, nz = vz }
    list[#list + 1] = wbox(ctx, wf, "door_frame", "theme_detail", "frame", -W / 2 - jw, -W / 2, 0, pr, py, py + Hd + lh, ctx.pal.frame)
    list[#list + 1] = wbox(ctx, wf, "door_frame", "theme_detail", "frame", W / 2, W / 2 + jw, 0, pr, py, py + Hd + lh, ctx.pal.frame)
    list[#list + 1] = wbox(ctx, wf, "door_frame", "theme_detail", "frame", -W / 2, W / 2, 0, pr, py + Hd, py + Hd + lh, ctx.pal.frame)
  end
  for _, b in ipairs(list) do b:tag("kit.opening", key) end
  prim_record(ctx, "door_frame", "theme_detail", "frame", list, { px, py, pz }, fin.yaw, { W + 2 * jw, Hd + lh, T + 2 * pr })
  return true
end

local function opening_key(o)
  local function q(v) return floor(v * 4 + 0.5) / 4 end
  local x, z = o.pos[1] + o.nx * (o.thickness or 1) / 2, o.pos[3] + o.nz * (o.thickness or 1) / 2
  return string.format("%g,%g,%g", q(x), q(o.pos[2]), q(z))
end

local function framed_already(key)
  return level.brushes_tagged("kit.opening", key).count > 0
end

-- An opening record from a kit doorway / opening record (axis, dir, min, max, floor_y, width, height, length).
local function opening_from_record(api, d, from_room)
  if type(d) ~= "table" or d.min == nil or d.max == nil or (d.axis ~= "x" and d.axis ~= "z") then
    K.fail(api, "'doorway' must be a kit.doorway / kit.opening record (with axis, min, max).")
  end
  local x0, y0, z0 = v3(api, "doorway.min", d.min)
  local x1, y1, z1 = v3(api, "doorway.max", d.max)
  local o = { sill = 0 }
  local dx, dz = 0, 0
  if d.dir and DIRS[d.dir] then dx, dz = DIRS[d.dir][1], DIRS[d.dir][2] elseif d.axis == "x" then dx = 1 else dz = 1 end
  -- Frame from the a side: its face plane is where the opening starts (walking toward b).
  if d.axis == "x" then
    local face = dx > 0 and x0 or x1
    o.pos = { face, y0, (z0 + z1) / 2 }
    o.width, o.thickness, o.tx, o.tz = z1 - z0, x1 - x0, 0, 1
  else
    local face = dz > 0 and z0 or z1
    o.pos = { (x0 + x1) / 2, y0, face }
    o.width, o.thickness, o.tx, o.tz = x1 - x0, z1 - z0, 1, 0
  end
  o.nx, o.nz = dx, dz
  o.height = y1 - y0
  return o
end

---@kit kit.frame_opening{doorway | pos, dir, width, height, thickness?, style?, seed?, custom?, textures?, retexture?, name?} → detail — Frames a door or window opening: a library door/window frame stretched (X and Y slices) to the opening's exact clear size (never narrower), centred in the wall or one on each face when the frame is thinner than the wall; else a primitive frame of jambs and lintel on both faces. Frames each opening once (kit.opening tag). Returns the detail record.
---@opt doorway: record — A kit.doorway / kit.opening record (give doorway, or pos + dir + width + height).
---@opt pos: vec — The floor point at the middle of the opening on the wall face it is framed from.
---@opt dir: string — Through the wall, away from that face: "+x", "-x", "+z" or "-z".
---@opt width: number — The clear width in metres.
---@opt height: number — The clear height in metres (from pos.y).
---@opt thickness: number = 1 — The wall thickness in metres.
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Picks among the best-scoring frames.
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on the placed module.
---@opt retexture: boolean = false — Map every module texture onto the wall's texture.
---@opt name: string = frame_opening<n> — Names the frame (tags).
function kit.frame_opening(t)
  local api = "kit.frame_opening"
  K.check_keys(api, t, { "doorway", "pos", "dir", "width", "height", "thickness", "style", "seed", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "frame_opening", t.name)
  local ctx = new_ctx(api, "frame_opening", t, name)
  local o
  if t.doorway ~= nil then
    if t.pos ~= nil or t.dir ~= nil then K.fail(api, "give 'doorway' or pos/dir/width/height, not both.") end
    o = opening_from_record(api, t.doorway)
  else
    local x, y, z = v3(api, "pos", t.pos)
    local d = DIRS[t.dir]
    if not d then K.fail(api, "'dir' must be \"+x\", \"-x\", \"+z\" or \"-z\" (got " .. tostring(t.dir) .. ").") end
    o = { pos = { x, y, z }, nx = d[1], nz = d[2], width = K.positive(api, "width", t.width), height = K.positive(api, "height", t.height),
      thickness = K.positive(api, "thickness", t.thickness, 1), tx = abs(d[2]), tz = abs(d[1]), sill = 0 }
  end
  local key = opening_key(o)
  if framed_already(key) then
    skip(ctx, "frame", "the opening is already framed")
  else
    frame_one(ctx, o, key)
  end
  return finish(ctx, { opening = { pos = ged.vec(o.pos[1], o.pos[2], o.pos[3]), width = o.width, height = o.height, thickness = o.thickness } })
end

-- ---------------------------------------------------------------------------------------------
-- kit.ceiling_detail
-- ---------------------------------------------------------------------------------------------

local function beam_ok(kind, H, span)
  return function(r)
    if not r.stretch.x or r.has_air or r.brushes > 24 then return false end
    if r.attach ~= "span" and r.attach ~= "ceiling" then return false end
    if (r.stretch.x[2] - r.stretch.x[1]) < 0.5 * r.sx then return false end
    if has_door_texture(r) then return false end
    if kind == "trusses" then
      return r.sy >= 0.6 and r.sy <= min(3.5, H - 3.2) and r.sz >= 0.2 and r.sz <= 2.5
    end
    return r.sy >= 0.25 and r.sy <= min(1.4, H - 2.6) and r.sz >= 0.2 and r.sz <= 1.6
  end
end

-- Pilaster positions on a room's walls (tags of earlier wall_detail calls), along `axis` ("x" or "z").
local function recorded_positions(s, axis)
  local out = {}
  local q = level.brushes_tagged("kit.part", "pilaster")
  for _, b in ipairs(q.brushes) do
    local bb = b.bounds
    local cx, cz = (bb.min.x + bb.max.x) / 2, (bb.min.z + bb.max.z) / 2
    if cx >= s.x0 - 0.1 and cx <= s.x1 + 0.1 and cz >= s.z0 - 0.1 and cz <= s.z1 + 0.1 and bb.min.y <= (s.floor_y or s.y0) + 0.5 then
      local v = axis == "x" and cx or cz
      local dup = false
      for _, w in ipairs(out) do if abs(w - v) < 0.3 then dup = true break end end
      if not dup then out[#out + 1] = v end
    end
  end
  P.sort(out, function(a, b) return a < b end)
  return out
end

local function ceiling_impl(api, t, ctx, s, pitch_hint)
  local y0, y1 = s.floor_y or s.y0, s.y1
  local H = y1 - y0
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  local axis = t.axis -- the axis the beams SPAN
  if axis ~= nil and axis ~= "x" and axis ~= "z" then K.fail(api, "'axis' must be \"x\" or \"z\" (the direction the beams span).") end
  axis = axis or (w <= d and "x" or "z")
  local span = axis == "x" and w or d
  local kind = t.kind or "auto"
  if kind ~= "beams" and kind ~= "trusses" and kind ~= "coffers" and kind ~= "auto" then
    K.fail(api, "'kind' must be \"beams\", \"trusses\", \"coffers\" or \"auto\" (got " .. tostring(kind) .. ").")
  end
  if kind == "auto" then kind = (H >= 8 and span >= 10) and "trusses" or "beams" end
  -- Beam positions along the other axis: the recorded pilaster positions, else a regular pitch.
  local along0, along1 = (axis == "x") and s.z0 or s.x0, (axis == "x") and s.z1 or s.x1
  local pos = recorded_positions(s, axis == "x" and "z" or "x")
  local inner = {}
  for _, v in ipairs(pos) do if v > along0 + 0.8 and v < along1 - 0.8 then inner[#inner + 1] = v end end
  local pitch = t.pitch and K.positive(api, "pitch", t.pitch) or pitch_hint or 4
  if #inner == 0 or t.pitch then
    inner = {}
    local L = along1 - along0
    local n = max(1, floor(L / pitch + 0.5))
    for k = 1, n - 1 do inner[#inner + 1] = along0 + L * k / n end
    if n == 1 then inner[1] = (along0 + along1) / 2 end
  end
  local lowest = y0 + 2.2
  local cand = ranked(ctx, { kind == "trusses" and "truss" or "beam" }, beam_ok(kind, H, span))
  if kind == "trusses" and #cand == 0 then cand = ranked(ctx, { "beam" }, beam_ok("beams", H, span)) end
  local r = nil
  if #cand > 0 then r = cand[ctx.st.int(1, min(3, #cand))].r end
  if not r then
    if modules.available then fallback(ctx, "no " .. ctx.style .. " beam spans " .. fmt(span) .. " m under a " .. fmt(H) .. " m ceiling")
    else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  end
  local f = frame_of(axis == "x" and 0 or 270)
  local function beam_at(v, rr, depth_scale)
    local cx, cz
    if axis == "x" then cx, cz = (s.x0 + s.x1) / 2, v else cx, cz = v, (s.z0 + s.z1) / 2 end
    if rr then
      local nb = stretched(rr, span, nil, nil)
      if not nb then skip(ctx, "beam", "cannot stretch to " .. fmt(span) .. " m") return end
      if y1 - (nb.y1 - nb.y0) < lowest then skip(ctx, "beam", "would hang below 2.2 m headroom") return end
      local ox, oy, oz = origin_for(f, (nb.x0 + nb.x1) / 2, nb.y1, (nb.z0 + nb.z1) / 2, cx, y1, cz)
      -- Its middle must be clear (its ends may rest on pilaster tops).
      local mid = { x0 = nb.x0 + 0.2 * (nb.x1 - nb.x0), x1 = nb.x1 - 0.2 * (nb.x1 - nb.x0), y0 = nb.y0, y1 = nb.y1, z0 = nb.z0, z1 = nb.z1 }
      if not box_clear(world_box(f, ox, oy, oz, mid)) then skip(ctx, "beam", "something already hangs there") return end
      place(ctx, rr, f, ox, oy, oz, span, nil, nil, "theme_detail")
    else
      -- An I-beam of three boxes: flanges and web.
      local bh, bw, fl = 0.6 * depth_scale, 0.4 * depth_scale, 0.08
      local tex = ctx.pal.beam
      local list = {}
      local function box3(u0, u1, yy0, yy1, w0, w1)
        if axis == "x" then return pbox(ctx, "beam", "theme_detail", "ibeam", s.x0 + u0, yy0, cz + w0, s.x0 + u1, yy1, cz + w1, tex) end
        return pbox(ctx, "beam", "theme_detail", "ibeam", cx + w0, yy0, s.z0 + u0, cx + w1, yy1, s.z0 + u1, tex)
      end
      list[1] = box3(0, span, y1 - fl, y1, -bw / 2, bw / 2)
      list[2] = box3(0, span, y1 - bh + fl, y1 - fl, -0.06, 0.06)
      list[3] = box3(0, span, y1 - bh, y1 - bh + fl, -bw / 2, bw / 2)
      prim_record(ctx, "beam", "theme_detail", "ibeam", list, { cx, y1, cz }, f.yaw, { span, bh, bw })
    end
  end
  for _, v in ipairs(inner) do beam_at(v, r, 1) end
  if kind == "coffers" then
    -- Secondary beams across the primaries: the grid reads as a coffered ceiling.
    local saved = axis
    axis = axis == "x" and "z" or "x"
    span = axis == "x" and w or d
    f = frame_of(axis == "x" and 0 or 270)
    local a0, a1 = (axis == "x") and s.z0 or s.x0, (axis == "x") and s.z1 or s.x1
    local n = max(2, floor((a1 - a0) / pitch + 0.5))
    local r2 = nil
    for _, e in ipairs(cand) do
      local nb = stretched(e.r, span, nil, nil)
      if nb then r2 = e.r break end
    end
    for k = 1, n - 1 do beam_at(a0 + (a1 - a0) * k / n, r2, 0.7) end
    axis = saved
  end
  return { axis = axis, kind = kind, positions = inner }
end

-- Ceiling beams along the current frame's x axis (the layout's ceiling pass): each line {x0, x1, z} a wall-to-wall
-- chord under the ceiling at y1 (or at its own ceiling height, a fourth number); floor_y the highest floor under them (2.2 m headroom kept); kind "beams" or
-- "trusses". One library beam (stretched to each chord) for every line, else three-box I-beams. Returns the count.
local function beams_impl(ctx, lines, y1, floor_y, kind, H)
  local lowest = floor_y + 2.2
  local maxspan = 0
  for _, ln in ipairs(lines) do maxspan = max(maxspan, ln[2] - ln[1]) end
  local cand = ranked(ctx, { kind == "trusses" and "truss" or "beam" }, beam_ok(kind, H, maxspan))
  if kind == "trusses" and #cand == 0 then cand = ranked(ctx, { "beam" }, beam_ok("beams", H, maxspan)) end
  local r = nil
  if #cand > 0 then r = cand[ctx.st.int(1, min(3, #cand))].r end
  if not r then
    if modules.available then fallback(ctx, "no " .. ctx.style .. " beam spans " .. fmt(maxspan) .. " m under a " .. fmt(H) .. " m ceiling")
    else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  end
  local f = frame_of(0)
  local n = 0
  local y1_base = y1
  for _, ln in ipairs(lines) do
    local x0, x1, cz = ln[1], ln[2], ln[3]
    y1 = ln[4] or y1_base
    local span, cx = x1 - x0, (x0 + x1) / 2
    if r then
      local nb = stretched(r, span, nil, nil)
      if not nb then
        skip(ctx, "beam", "cannot stretch to " .. fmt(span) .. " m")
      elseif y1 - (nb.y1 - nb.y0) < lowest then
        skip(ctx, "beam", "would hang below 2.2 m headroom")
      else
        local ox, oy, oz = origin_for(f, (nb.x0 + nb.x1) / 2, nb.y1, (nb.z0 + nb.z1) / 2, cx, y1, cz)
        local mid = { x0 = nb.x0 + 0.2 * (nb.x1 - nb.x0), x1 = nb.x1 - 0.2 * (nb.x1 - nb.x0), y0 = nb.y0, y1 = nb.y1 - 0.05, z0 = nb.z0, z1 = nb.z1 }
        if box_clear(world_box(f, ox, oy, oz, mid)) then
          place(ctx, r, f, ox, oy, oz, span, nil, nil, "theme_detail")
          n = n + 1
        else
          skip(ctx, "beam", "something already stands there")
        end
      end
    else
      local bh, bw, fl = 0.6, 0.4, 0.08
      if y1 - bh < lowest then
        skip(ctx, "beam", "would hang below 2.2 m headroom")
      elseif not box_clear({ x0 = x0 + 0.2 * span, x1 = x1 - 0.2 * span, y0 = y1 - bh, y1 = y1 - 0.05, z0 = cz - bw / 2, z1 = cz + bw / 2 }) then
        skip(ctx, "beam", "something already stands there")
      else
        local tex = ctx.pal.beam
        local list = {
          pbox(ctx, "beam", "theme_detail", "ibeam", x0, y1 - fl, cz - bw / 2, x1, y1, cz + bw / 2, tex),
          pbox(ctx, "beam", "theme_detail", "ibeam", x0, y1 - bh + fl, cz - 0.06, x1, y1 - fl, cz + 0.06, tex),
          pbox(ctx, "beam", "theme_detail", "ibeam", x0, y1 - bh, cz - bw / 2, x1, y1 - bh + fl, cz + bw / 2, tex),
        }
        prim_record(ctx, "beam", "theme_detail", "ibeam", list, { cx, y1, cz }, 0, { span, bh, bw })
        n = n + 1
      end
    end
  end
  return n
end

-- The layout's wall pass: walls = {{xf = {rot, px, pz}, s (a box in that frame whose "-z" side is the wall), far =
-- {a =, b =}, pilasters = false?}...}. One coherent set per storey height: the zone's main height picks it by seed,
-- the other heights keep its donor where they can. Returns {pilasters (world {x, z} of each), donor}.
local SETS_CACHE = { path = false, sets = {} }

local function walls_impl(ctx, walls, parts, main_h, door_top)
  local cache = {}
  local main = nil
  -- (the ranked sets depend only on the style, the wall material and the storey height class: a level's zones of one
  -- palette share them, read once per session)
  if SETS_CACHE.path ~= modules.path then SETS_CACHE.path, SETS_CACHE.sets = modules.path, {} end
  local function set_for(H)
    local key = floor(H * 2 + 0.5)
    if cache[key] ~= nil then return cache[key] or nil end
    local pitch0 = clamp(max(4, H * 0.5), 3, 8)
    local skey = table.concat({ ctx.style, tostring(ctx.wall_texture), tostring(ctx.custom), tostring(parts.pilasters), tostring(parts.panels), key }, "|")
    local sets = SETS_CACHE.sets[skey]
    if not sets then
      sets = wall_sets(ctx, H, { pilasters = parts.pilasters, panels = parts.panels, maxw = max(0.5, pitch0 / 6), maxd = 0.6 })
      SETS_CACHE.sets[skey] = sets
    end
    -- (a set is changed below when its pilaster is off-style: each zone works on its own copy)
    local copies = {}
    for i, st in ipairs(sets) do local c2 = {} for k, v in pairs(st) do c2[k] = v end copies[i] = c2 end
    sets = copies
    local set = nil
    if #sets > 0 then
      if main then
        for _, st in ipairs(sets) do if main.source and st.source == main.source then set = st break end end
        if not set and main.pilaster then
          local fk = family(main.pilaster.textures[1] or main.pilaster.id)
          for _, st in ipairs(sets) do if st.pilaster and family(st.pilaster.textures[1] or st.pilaster.id) == fk then set = st break end end
        end
        set = set or sets[1]
      else
        set = sets[ctx.st.int(1, min(#sets, 3))]
      end
    end
    if set and set.pilaster == nil and parts.pilasters then set = nil end
    if set and set.pilaster and set.off_style then
      note(ctx, "no " .. ctx.style .. " pilaster in the library; " .. set.pilaster.id .. " is off-style, so primitive pilasters")
      ctx.fallback = true
      set.pilaster = nil
    end
    cache[key] = set or false
    return set
  end
  main = set_for(main_h)
  if not modules.available then
    fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")")
  elseif not main then
    fallback(ctx, "no " .. ctx.style .. " pilaster fits a " .. fmt(main_h) .. " m wall")
  end
  local out = {}
  local profiles = {}
  for _, w in ipairs(walls) do
    set_frame(w.xf.rot, w.xf.px, w.xf.pz)
    local s = w.s
    -- (one reading of the whole wall per storey (w.whole), cut to each stretch: w.profile_key names it)
    if w.whole and w.profile_key and not profiles[w.profile_key] then
      profiles[w.profile_key] = wall_profile(w.whole, wall_frame(w.whole, "-z"))
    end
    w.profile = w.profile_key and profiles[w.profile_key] or nil
    local H = s.y1 - s.floor_y
    local set = set_for(H)
    local pitch = clamp(max((set and set.pilaster and set.pilaster.pitch) or 4, H * 0.5, parts.min_pitch or 0), 3, 9)
    local opts = { pilasters = parts.pilasters and w.pilasters ~= false, base = parts.base, top = parts.top, band = parts.band and H >= 6,
      panels = parts.panels, prim_panels = false, pitch = pitch, door_top = door_top, far = w.far, profile = w.profile }
    local cs = detail_wall(ctx, s, wall_frame(s, "-z"), set, opts, new_budget(s, 0.015))
    for _, t in ipairs(cs) do local x, z = xf_w(t, s.z0) out[#out + 1] = { x, z } end
  end
  set_frame(nil)
  local donor = nil
  if main then
    donor = { source = main.source, pilaster = main.pilaster and main.pilaster.id, base = main.base and main.base.id, top = main.top and main.top.id,
      panel = main.panel and main.panel.id }
    note(ctx, "donor " .. tostring(main.source or (main.pilaster and main.pilaster.style)))
  end
  return { pilasters = out, donor = donor }
end

---@kit kit.ceiling_detail{room|space, style?, seed?, kind?, pitch?, axis?, pipes?, custom?, textures?, retexture?, name?} → detail — Real ceiling structure: library beams or trusses stretched wall to wall across the room's shorter axis at a regular pitch, aligned to the pilasters a kit.wall_detail laid (read back from their tags), never lower than 2.2 m above the floor; coffers add a lighter cross grid; pipes = true adds a pipe run along the long axis. I-beams of three boxes without a library. Returns the detail record plus axis, kind, positions.
---@opt room: record|string — The room (a kit record or kit.name; give room or space).
---@opt space: {min=, max=} — A box instead of a room.
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Picks among the best-scoring beams.
---@opt kind: string = "auto" — "beams", "trusses" (rooms 8 m or taller), "coffers" (a beam grid) or "auto".
---@opt pitch: number = the wall detail's pilaster positions, else 4 — Beam spacing in metres.
---@opt axis: string = the shorter side — The axis the beams span: "x" or "z".
---@opt pipes: boolean = false — Also run a pair of pipes under the ceiling along the long axis.
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the room's wall texture.
---@opt name: string = ceiling_detail<n> — Names the detail (tags).
function kit.ceiling_detail(t)
  local api = "kit.ceiling_detail"
  K.check_keys(api, t, { "room", "space", "style", "seed", "kind", "pitch", "axis", "pipes", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "ceiling_detail", t.name)
  local ctx = new_ctx(api, "ceiling_detail", t, name)
  local boxes = boxes_of(api, t)
  local s = boxes[1]
  ctx.wall_texture = wall_texture_of(s)
  ctx.material = ctx.wall_texture and ctx.wall_texture:lower():match("^(%a+_)") or nil
  ctx.wall_family = ctx.wall_texture and family(ctx.wall_texture) or nil
  ctx.wall_brightness = ctx.wall_texture and modules.available and modules.texture_brightness(ctx.wall_texture) or nil
  local extra = ceiling_impl(api, t, ctx, s, nil)
  for i = 2, #boxes do
    local e2 = ceiling_impl(api, t, ctx, boxes[i], nil)
    for _, v in ipairs(e2.positions) do extra.positions[#extra.positions + 1] = v end
  end
  if K.boolean(api, "pipes", t.pipes, false) then
    local y1 = s.y1
    local long_x = (extra.axis == "z")
    local a, b
    if long_x then
      a = { s.x0, y1 - 0.45, s.z0 + 0.6 }
      b = { s.x1, y1 - 0.45, s.z0 + 0.6 }
    else
      a = { s.x0 + 0.6, y1 - 0.45, s.z0 }
      b = { s.x0 + 0.6, y1 - 0.45, s.z1 }
    end
    local pr = kit.pipe_run{ points = { a, b }, style = ctx.style, seed = seed_of(api, t), count = 2, diameter = 0.3, name = name .. "_pipes" }
    for _, e in ipairs(pr.placed) do ctx.placed[#ctx.placed + 1] = e count(ctx, e.category, e.role, e.brushes) end
  end
  return finish(ctx, extra)
end

-- ---------------------------------------------------------------------------------------------
-- The engine space (kit.dress's): keep-outs, walk budget, routes.
-- ---------------------------------------------------------------------------------------------

local function keep_of(api, t)
  local keep = {}
  if t.keep ~= nil then
    if type(t.keep) ~= "table" then K.fail(api, "'keep' must list points {x, y, z, r =} or boxes {min =, max =}.") end
    for i, k in ipairs(t.keep) do
      if type(k) == "table" and k.min ~= nil then
        local ax, ay, az = v3(api, "keep[" .. i .. "].min", k.min)
        local bx, by, bz = v3(api, "keep[" .. i .. "].max", k.max)
        keep[i] = { x0 = min(ax, bx), y0 = min(ay, by), z0 = min(az, bz), x1 = max(ax, bx), y1 = max(ay, by), z1 = max(az, bz) }
      else
        local x, y, z = v3(api, "keep[" .. i .. "]", k)
        keep[i] = { x = x, y = y, z = z, r = (type(k) == "table" and k.r) or 1.5 }
      end
    end
  end
  return keep
end

local HEAD = { { 1, 0 }, { 0, 1 }, { -1, 0 }, { 0, -1 } }

-- ---------------------------------------------------------------------------------------------
-- kit.machinery
-- ---------------------------------------------------------------------------------------------

local function machine_ok(H, w, d)
  return function(r)
    if r.has_air or r.brushes > 40 then return false end
    if r.attach ~= "floor" and r.attach ~= "wall" and r.attach ~= "corner" then return false end
    if abs(r.y0) > 0.1 or r.mount then return false end
    if r.sy < 0.6 or r.sy > min(H - 0.8, 5) then return false end
    if r.sx < 0.5 or r.sz < 0.4 or r.sx > min(6, 0.45 * max(w, d)) or r.sz > min(3.5, 0.3 * min(w, d)) then return false end
    return not has_door_texture(r)
  end
end

-- A primitive machine: a base, a body and a control top (style palette), along the wall frame.
local function prim_machine(ctx, sp, node, h, dist, st)
  local dv = HEAD[h]
  local ax, az = node.x - dv[1] * dist + dv[1] * 0.05, node.z - dv[2] * dist + dv[2] * 0.05
  local w, dpt, ht = st.range(1.4, 2.6), st.range(0.7, 1.1), st.range(1.1, 1.8)
  local parts = { { u0 = -w / 2, u1 = w / 2, v0 = 0, v1 = dpt, y0 = 0, y1 = ht } }
  local world, tok = sp:try(parts, ax, node.y, az, dv[1], dv[2], true, 0.3)
  if not world then return nil end
  sp:commit(world, tok)
  local wf = { px = ax, pz = az, tx = -dv[2], tz = dv[1], nx = dv[1], nz = dv[2] }
  local y0 = node.y
  local list = {
    wbox(ctx, wf, "machinery", "cover", "machine", -w / 2, w / 2, 0, dpt, y0, y0 + 0.2, ctx.pal.frame),
    wbox(ctx, wf, "machinery", "cover", "machine", -w / 2 + 0.05, w / 2 - 0.05, 0, dpt - 0.08, y0 + 0.2, y0 + ht - 0.25, ctx.pal.machine),
    wbox(ctx, wf, "machinery", "cover", "machine", -w / 2, w / 2, 0, dpt, y0 + ht - 0.25, y0 + ht, ctx.pal.frame),
  }
  return prim_record(ctx, "machinery", ht >= 1.0 and "cover" or "fixture", "machine", list, { ax, y0, az }, yaw_of(dv[1], dv[2]), { w, ht, dpt })
end

local function machinery_impl(api, t, ctx, s, n_hint)
  local y0 = s.floor_y or s.y0
  local H = s.y1 - y0
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  local office = ctx.style == "admin" or ctx.style == "tech" or ctx.style == "lab"
  local kinds = t.kinds or (office and { "console", "machinery" } or { "machinery", "console", "crate_stack" })
  if type(kinds) ~= "table" or #kinds == 0 then K.fail(api, "'kinds' must list categories like {\"machinery\", \"console\"}.") end
  local role = role_of(api, t.role)
  local n = n_hint or (t.count and K.number(api, "count", t.count)) or clamp(floor(w * d / 60 + 0.5), 2, 10)
  if n < 0 or n ~= floor(n) then K.fail(api, "'count' must be a whole number of 0 or more.") end
  local budget = t.budget and K.number(api, "budget", t.budget) or 0.02
  if budget < 0 or budget > 0.02 then K.fail(api, "'budget' must be between 0 and 0.02 (the walkable floor may change by at most 2 %).") end
  local pr = probe()
  local sp = K.dress_space(pr, s, keep_of(api, t), budget)
  local cand = ranked(ctx, kinds, machine_ok(H, w, d))
  if #cand == 0 then
    if modules.available then fallback(ctx, "no " .. ctx.style .. " " .. table.concat(kinds, "/") .. " module fits the room")
    else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  end
  local st = ctx.st
  -- Wall spots, shuffled once by the seed.
  local ws = sp.h:wall_spots()
  local spots = {}
  for q = 1, #ws.id do spots[q] = { id = ws.id[q], h = ws.h[q], dist = ws.dist[q] } end
  for i = #spots, 2, -1 do
    local j = st.int(1, i)
    spots[i], spots[j] = spots[j], spots[i]
  end
  local placed = 0
  local used = {}
  local top = min(#cand, 10)
  local tries = 0
  local si = 1
  while placed < n and tries < n * 6 and #spots > 0 do
    tries = tries + 1
    local ok = false
    if #cand > 0 then
      -- Rotate through the best candidates so a room gets a mix, never a random one.
      local e = cand[((placed + tries - 1) % top) + 1]
      local r = e.r
      for q = 0, min(#spots, 40) - 1 do
        local spot = spots[((si + q - 1) % #spots) + 1]
        local node = sp.L.nodes[spot.id]
        local dv = HEAD[spot.h]
        local ax, az = node.x - dv[1] * spot.dist + dv[1] * 0.05, node.z - dv[2] * spot.dist + dv[2] * 0.05
        local f = frame_of(yaw_of(dv[1], dv[2]))
        -- The Space's u axis is local -x (u = v turned +90 degrees, local x = v turned -90).
        local parts = { { u0 = -r.x1, u1 = -r.x0, v0 = 0, v1 = r.z1 - r.z0, y0 = 0, y1 = r.sy } }
        local world, tok = sp:try(parts, ax, node.y, az, dv[1], dv[2], true, 0.3)
        if world then
          local ox, oy, oz = origin_for(f, 0, r.y0, r.z0, ax, node.y, az)
          local wb = world_box(f, ox, oy, oz, r)
          if box_clear(wb) then
            sp:commit(world, tok)
            local rr = role or r.role
            if rr == "theme_detail" then rr = (r.category == "console") and "fixture" or ((r.sy >= 1.0 and r.sx >= 1.0) and "cover" or "fixture") end
            place(ctx, r, f, ox, oy, oz, nil, nil, nil, rr)
            placed = placed + 1
            si = si + q + 7
            ok = true
            break
          end
        end
      end
    end
    if not ok and (#cand == 0 or tries > n * 3) then
      -- Primitive machine at the next spot.
      for q = 0, min(#spots, 40) - 1 do
        local spot = spots[((si + q - 1) % #spots) + 1]
        local node = sp.L.nodes[spot.id]
        local rec = prim_machine(ctx, sp, node, spot.h, spot.dist, st)
        if rec then
          if role then
            rec.role = role
            for _, b in ipairs(rec.brushes) do b:tag("kit.role", role) end
          end
          if #cand > 0 then fallback(ctx, "no library machine fitted some wall spots") end
          placed = placed + 1
          si = si + q + 7
          ok = true
          break
        end
      end
    end
    if not ok then si = si + 11 end
  end
  if placed < n then skip(ctx, "machinery", (n - placed) .. " of " .. n .. " found no clear wall spot within the walk budget") end
  return { count = placed }
end

---@kit kit.machinery{room|space, style?, seed?, count?, kinds?, role?, keep?, budget?, custom?, textures?, retexture?, name?} → detail — Floor-standing machinery, consoles and crate stacks placed with purpose: backs against the walls facing into the room, kept off doorways, spawns, items and the route between doorways, and within the walk budget (the generative fill's engine space); a mix of the best-scoring modules of the style. Primitive machines without a library. Roles: cover for chunky pieces, fixture for consoles (or role = …).
---@opt room: record|string — The room (a kit record or kit.name; give room or space).
---@opt space: {min=, max=} — A box instead of a room.
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Picks the spots and the mix.
---@opt count: integer = 1 per 60 m² (2 to 10) — How many pieces.
---@opt kinds: string[] = {"machinery", "console", "crate_stack"} ({"console", "machinery"} for admin, tech and lab) — Module categories to draw from.
---@opt role: string = from the module — Record every piece as this role (cover, route, landmark, theme_detail, fixture).
---@opt keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---@opt budget: number = 0.02 — Largest share of the walkable floor the pieces may take (at most 0.02).
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the room's wall texture.
---@opt name: string = machinery<n> — Names the pieces (tags).
function kit.machinery(t)
  local api = "kit.machinery"
  K.check_keys(api, t, { "room", "space", "style", "seed", "count", "kinds", "role", "keep", "budget", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "machinery", t.name)
  local ctx = new_ctx(api, "machinery", t, name)
  local boxes = boxes_of(api, t)
  ctx.wall_texture = wall_texture_of(boxes[1])
  ctx.material = ctx.wall_texture and ctx.wall_texture:lower():match("^(%a+_)") or nil
  ctx.wall_family = ctx.wall_texture and family(ctx.wall_texture) or nil
  ctx.wall_brightness = ctx.wall_texture and modules.available and modules.texture_brightness(ctx.wall_texture) or nil
  local total = 0
  for _, b in ipairs(boxes) do
    local share = nil
    if t.count ~= nil and #boxes > 1 then
      local area, all = (b.x1 - b.x0) * (b.z1 - b.z0), 0
      for _, c in ipairs(boxes) do all = all + (c.x1 - c.x0) * (c.z1 - c.z0) end
      share = floor(K.number(api, "count", t.count) * area / all + 0.5)
    end
    total = total + machinery_impl(api, t, ctx, b, share).count
  end
  return finish(ctx, { count = total })
end

-- ---------------------------------------------------------------------------------------------
-- kit.railing and kit.catwalk
-- ---------------------------------------------------------------------------------------------

local function railing_ok(r)
  if r.attach ~= "edge" or not r.stretch.x or r.has_air or r.brushes > 16 then return false end
  if r.sy < 0.7 or r.sy > 1.4 or r.sz > 0.5 or abs(r.y0) > 0.1 then return false end
  -- (an animated grate panel cannot be shown faithfully and reads as a screen, not a rail)
  if has_vbm(r) then return false end
  return (r.stretch.x[2] - r.stretch.x[1]) >= 0.5 * r.sx
end

-- One straight railing leg from a to b (floor points at the edge), walkable side unit (wx, wz).
local function rail_leg(ctx, r, a, b, wx, wz, height)
  local dx, dz = b[1] - a[1], b[3] - a[3]
  local L = sqrt(dx * dx + dz * dz)
  if L < 0.3 then return end
  local ux, uz = dx / L, dz / L
  local y = a[2]
  if r then
    local f = frame_of(yaw_of(wx, wz))
    -- Local +X must run along a -> b: mirror when it runs the other way.
    local mirror = (f.ax * ux + f.az * uz) < 0
    local nb = stretched(r, L, height and r.stretch.y and height or nil, nil)
    if nb then
      local mx, mz = (a[1] + b[1]) / 2, (a[3] + b[3]) / 2
      local ox, oy, oz = origin_for(f, (nb.x0 + nb.x1) / 2 * (mirror and -1 or 1), nb.y0, 0, mx + wx * 0.02, y, mz + wz * 0.02)
      place(ctx, r, f, ox, oy, oz, L, height and r.stretch.y and height or nil, nil, "route", mirror)
      return
    end
  end
  -- Post-and-rail: posts every <= 1.6 m, a top rail and a mid rail, 0.1 m in from the edge.
  local h = height or 1.05
  local n = max(1, ceil(L / 1.6))
  local list = {}
  local inx, inz = wx * 0.1, wz * 0.1
  for k = 0, n do
    local px, pz = a[1] + ux * L * k / n + inx, a[3] + uz * L * k / n + inz
    list[#list + 1] = pbox(ctx, "railing", "route", "railing", px - 0.05, y, pz - 0.05, px + 0.05, y + h, pz + 0.05, ctx.pal.rail)
  end
  for _, yy in ipairs({ h - 0.08, h * 0.5 }) do
    local x0, x1 = min(a[1], b[1]) + inx, max(a[1], b[1]) + inx
    local z0, z1 = min(a[3], b[3]) + inz, max(a[3], b[3]) + inz
    if abs(ux) > abs(uz) then z0, z1 = z0 - 0.04, z1 + 0.04 else x0, x1 = x0 - 0.04, x1 + 0.04 end
    list[#list + 1] = pbox(ctx, "railing", "route", "railing", x0, y + yy, z0, x1, y + yy + 0.08, z1, ctx.pal.rail)
  end
  prim_record(ctx, "railing", "route", "railing", list, { a[1], y, a[3] }, yaw_of(wx, wz), { L, h, 0.12 })
end

-- The walkable side of an edge a -> b: the side with floor at the edge's height (nil when both or neither).
local function walk_side(a, b)
  local pr = probe()
  local dx, dz = b[1] - a[1], b[3] - a[3]
  local L = sqrt(dx * dx + dz * dz)
  local lx, lz = -dz / L, dx / L
  local mx, mz, y = (a[1] + b[1]) / 2, (a[3] + b[3]) / 2, a[2]
  local function floor_at(sx, sz)
    return pr:air_exact(mx + sx * 0.6, y + 0.5, mz + sz * 0.6) and not pr:air_exact(mx + sx * 0.6, y - 0.1, mz + sz * 0.6)
  end
  local l, r = floor_at(lx, lz), floor_at(-lx, -lz)
  if l and not r then return lx, lz end
  if r and not l then return -lx, -lz end
  return lx, lz
end

local function railing_impl(api, ctx, pts, side, height)
  -- (one railing look per call context: every edge a room's pass rails matches)
  local r = nil
  if ctx.rail_pick ~= nil then
    r = ctx.rail_pick or nil
  else
    local cand = ranked(ctx, { "railing" }, railing_ok)
    if #cand > 0 then r = cand[ctx.st.int(1, min(3, #cand))].r end
    ctx.rail_pick = r or false
  end
  if not r then
    if modules.available then fallback(ctx, "no stretchable " .. ctx.style .. " edge railing in the library")
    else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  end
  for i = 1, #pts - 1 do
    local a, b = pts[i], pts[i + 1]
    local wx, wz
    if side == "left" or side == "right" then
      local dx, dz = b[1] - a[1], b[3] - a[3]
      local L = sqrt(dx * dx + dz * dz)
      wx, wz = -dz / L, dx / L
      if side == "right" then wx, wz = -wx, -wz end
    elseif side and DIRS[side] then
      wx, wz = DIRS[side][1], DIRS[side][2]
    else
      wx, wz = walk_side(a, b)
    end
    rail_leg(ctx, r, a, b, wx, wz, height)
  end
  -- Corner posts at the joints of a multi-segment path.
  for i = 2, #pts - 1 do
    local p = pts[i]
    local h = height or 1.1
    local b = pbox(ctx, "railing", "route", "post", p[1] - 0.08, p[2], p[3] - 0.08, p[1] + 0.08, p[2] + h + 0.05, p[3] + 0.08, ctx.pal.frame)
    prim_record(ctx, "railing", "route", "post", { b }, p, 0, { 0.16, h + 0.05, 0.16 })
  end
end

local function points_of(api, key, list, min_n)
  if type(list) ~= "table" or #list < (min_n or 2) then K.fail(api, "'" .. key .. "' must list at least " .. (min_n or 2) .. " points.") end
  local pts = {}
  for i, p in ipairs(list) do
    local x, y, z = v3(api, key .. "[" .. i .. "]", p)
    pts[i] = { x, y, z }
  end
  return pts
end

---@kit kit.railing{from, to | path, side?, height?, style?, seed?, custom?, textures?, retexture?, name?} → detail — A railing along a floor edge: a library edge railing stretched to each leg (its posts re-spaced by the module's repeat), corner posts at the joints of a path; post-and-rail primitives without a library. Role route.
---@opt from: vec — The edge's first floor point (give from + to, or path).
---@opt to: vec — The edge's last floor point.
---@opt path: vec[] — Floor points along the edge (at least 2).
---@opt side: string = the side with floor — Where the walkable floor is: "left" / "right" of the direction of travel, or "+x", "-x", "+z", "-z".
---@opt height: number = the module's (about 1.05 m) — Railing height in metres.
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Picks among the best-scoring railings.
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the wall texture (no effect without a room).
---@opt name: string = railing<n> — Names the railing (tags).
function kit.railing(t)
  local api = "kit.railing"
  K.check_keys(api, t, { "from", "to", "path", "side", "height", "style", "seed", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "railing", t.name)
  local ctx = new_ctx(api, "railing", t, name)
  local pts
  if t.path ~= nil then
    if t.from ~= nil or t.to ~= nil then K.fail(api, "give from/to or path, not both.") end
    pts = points_of(api, "path", t.path)
  else
    if t.from == nil or t.to == nil then K.fail(api, "give from and to (floor points), or path.") end
    pts = points_of(api, "from/to", { t.from, t.to })
  end
  if t.side ~= nil and t.side ~= "left" and t.side ~= "right" and not DIRS[t.side] then
    K.fail(api, "'side' must be \"left\", \"right\", \"+x\", \"-x\", \"+z\" or \"-z\".")
  end
  local h = t.height and K.positive(api, "height", t.height) or nil
  railing_impl(api, ctx, pts, t.side, h)
  return finish(ctx)
end

---@kit kit.catwalk{from, to, width?, style?, seed?, rails?, supports?, pitch?, custom?, textures?, retexture?, name?} → detail — A raised walkway: a deck whose top runs from → to (a stretchable library catwalk or grate when one fits, else a primitive deck with a grating top and edge beams), railings on its sides (inside the deck), and supports to the floor below every pitch metres (columns), or wall brackets where no floor is in reach. The clear width between the rails stays at least the player width. Role route.
---@opt from: vec — Required: the deck top's start point (centre line).
---@opt to: vec — Required: the deck top's end point (level with from; along x or z).
---@opt width: number = 2.5 — Deck width in metres (rails inside it; at least kit.limits.min_clear_width + 0.25).
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Picks among the best-scoring parts.
---@opt rails: string = "both" — "both", "left", "right" (of the direction from → to) or "none".
---@opt supports: boolean = true — Columns down to the floor (or brackets to a wall) under the deck.
---@opt pitch: number = 4 — Support spacing in metres.
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the wall texture (no effect without a room).
---@opt name: string = catwalk<n> — Names the catwalk (tags).
function kit.catwalk(t)
  local api = "kit.catwalk"
  K.check_keys(api, t, { "from", "to", "width", "style", "seed", "rails", "supports", "pitch", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "catwalk", t.name)
  local ctx = new_ctx(api, "catwalk", t, name)
  local ax, ay, az = v3(api, "from", t.from)
  local bx, by, bz = v3(api, "to", t.to)
  if abs(ay - by) > 1e-3 then K.fail(api, "from and to must be level (use kit.stairs or kit.ramp for a climb).") end
  if abs(ax - bx) > 1e-3 and abs(az - bz) > 1e-3 then K.fail(api, "from and to must differ on x or z only.") end
  local width = K.positive(api, "width", t.width, 2.5)
  local rails = t.rails or "both"
  if rails ~= "both" and rails ~= "left" and rails ~= "right" and rails ~= "none" then K.fail(api, "'rails' must be \"both\", \"left\", \"right\" or \"none\".") end
  local inner = width - ((rails == "both") and 0.3 or (rails == "none" and 0 or 0.15))
  if inner < KIT.limits.min_clear_width - 1e-4 then
    K.fail(api, "the clear width between the rails (" .. fmt(inner) .. " m) is below the " .. KIT.limits.min_clear_width .. " m player width.", "Widen the deck.")
  end
  local L = sqrt((bx - ax) ^ 2 + (bz - az) ^ 2)
  local ux, uz = (bx - ax) / L, (bz - az) / L
  local lx, lz = -uz, ux
  local along_x = abs(ux) > 0.5
  local y = ay
  -- Deck.
  local deck = nil
  for _, e in ipairs(ranked(ctx, { "catwalk", "grate" }, function(r)
    return r.stretch.x and not r.has_air and r.sy <= 0.5 and r.brushes <= 10 and r.attach ~= "wall"
      and (abs(r.sz - width) < 0.3 or (r.stretch.z and r.stretch.z[2] - r.stretch.z[1] + width - r.sz >= 0.05)) and not has_door_texture(r)
  end)) do deck = e.r break end
  local deck_bottom = y - 0.3
  if deck then
    local f = frame_of(along_x and 0 or 270)
    local dep = abs(deck.sz - width) >= 0.01 and width or nil
    local nb = stretched(deck, L, nil, dep)
    if nb then
      local ox, oy, oz = origin_for(f, (nb.x0 + nb.x1) / 2, nb.y1, (nb.z0 + nb.z1) / 2, (ax + bx) / 2, y, (az + bz) / 2)
      place(ctx, deck, f, ox, oy, oz, L, nil, dep, "route")
      deck_bottom = y - (nb.y1 - nb.y0)
    else
      deck = nil
    end
  end
  if not deck then
    if modules.available then fallback(ctx, "no stretchable deck module " .. fmt(width) .. " m wide") else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
    local x0, x1 = min(ax, bx) - (along_x and 0 or width / 2), max(ax, bx) + (along_x and 0 or width / 2)
    local z0, z1 = min(az, bz) - (along_x and width / 2 or 0), max(az, bz) + (along_x and width / 2 or 0)
    local list = { pbox(ctx, "catwalk", "route", "deck", x0, y - 0.12, z0, x1, y, z1, ctx.pal.deck) }
    -- Edge beams under the deck's long sides.
    for _, sgn in ipairs({ -1, 1 }) do
      if along_x then
        local zc = (az + bz) / 2 + sgn * (width / 2 - 0.12)
        list[#list + 1] = pbox(ctx, "catwalk", "route", "deck", x0, y - 0.42, zc - 0.12, x1, y - 0.12, zc + 0.12, ctx.pal.beam)
      else
        local xc = (ax + bx) / 2 + sgn * (width / 2 - 0.12)
        list[#list + 1] = pbox(ctx, "catwalk", "route", "deck", xc - 0.12, y - 0.42, z0, xc + 0.12, y - 0.12, z1, ctx.pal.beam)
      end
    end
    prim_record(ctx, "catwalk", "route", "deck", list, { (ax + bx) / 2, y, (az + bz) / 2 }, along_x and 0 or 270, { L, 0.42, width })
    deck_bottom = y - 0.42
  end
  -- Rails (inside the deck edges).
  local half = width / 2 - 0.02
  local function rail_side(sgn)
    local a = { ax + lx * half * sgn, y, az + lz * half * sgn }
    local b = { bx + lx * half * sgn, y, bz + lz * half * sgn }
    railing_impl(api, ctx, { a, b }, sgn > 0 and "right" or "left", nil)
  end
  if rails == "both" or rails == "left" then rail_side(1) end
  if rails == "both" or rails == "right" then rail_side(-1) end
  -- Supports.
  if K.boolean(api, "supports", t.supports, true) then
    local pitch = K.positive(api, "pitch", t.pitch, 4)
    local n = max(1, floor(L / pitch + 0.5))
    local pr = probe()
    for k = 0, n do
      local f = k / n
      local cx, cz = ax + (bx - ax) * f + ux * (k == 0 and 0.4 or (k == n and -0.4 or 0)), az + (bz - az) * f + uz * (k == 0 and 0.4 or (k == n and -0.4 or 0))
      for _, sgn in ipairs({ -1, 1 }) do
        local px, pz = cx + lx * (width / 2 - 0.25) * sgn, cz + lz * (width / 2 - 0.25) * sgn
        -- The floor below.
        local gy = nil
        local yy = deck_bottom - 0.1
        while yy > deck_bottom - 16 do
          if not pr:air_exact(px, yy, pz) then gy = yy + 0.05 break end
          yy = yy - 0.1
        end
        if gy and deck_bottom - gy >= 0.5 then
          local b = pbox(ctx, "support", "route", "column", px - 0.15, gy, pz - 0.15, px + 0.15, deck_bottom, pz + 0.15, ctx.pal.beam)
          prim_record(ctx, "support", "route", "column", { b }, { px, gy, pz }, 0, { 0.3, deck_bottom - gy, 0.3 })
        end
      end
    end
  end
  return finish(ctx, { width = width, length = L, top_y = y })
end

-- ---------------------------------------------------------------------------------------------
-- kit.pipe_run
-- ---------------------------------------------------------------------------------------------

local function pipe_ok(dia)
  return function(r)
    if not r.stretch.x or r.has_air or r.brushes > 6 then return false end
    if r.sy > dia * 1.6 or r.sz > dia * 1.6 or r.sy < dia * 0.6 then return false end
    return (r.stretch.x[2] - r.stretch.x[1]) >= 0.6 * r.sx
  end
end

-- The nearest solid surface around a point (for brackets): the axis direction and distance, or nil.
local function nearest_surface(x, y, z, reach, skip_axis)
  local pr = probe()
  local best = nil
  for _, d in ipairs({ { 0, 1, 0 }, { 1, 0, 0 }, { -1, 0, 0 }, { 0, 0, 1 }, { 0, 0, -1 }, { 0, -1, 0 } }) do
    local axis = d[1] ~= 0 and "x" or (d[2] ~= 0 and "y" or "z")
    if axis ~= skip_axis then
      local s = 0.1
      while s <= reach do
        if not pr:air_exact(x + d[1] * s, y + d[2] * s, z + d[3] * s) then
          if not best or s < best.dist then best = { d = d, dist = s } end
          break
        end
        s = s + 0.05
      end
    end
  end
  return best
end

---@kit kit.pipe_run{points | from, to, along?, diameter?, count?, brackets?, style?, seed?, custom?, textures?, retexture?, name?} → detail — Axis-aligned pipe legs through the points (or routed from → to along a wall, ceiling or floor as an L), up to count parallel pipes, flanges at the ends and every 4 m, elbow collars at the turns, and brackets or hangers to the nearest wall or ceiling at a pitch. Library pipe modules stretched along a leg when one of the diameter exists; primitive cylinders otherwise. Role theme_detail.
---@opt points: vec[] — The pipe's centre line (at least 2 points; each leg along x, y or z). Give points, or from + to.
---@opt from: vec — Start of a routed run.
---@opt to: vec — End of a routed run (an L: first along x, then z, then y).
---@opt along: string = "ceiling" — With from/to: "ceiling", "wall" or "floor" (only labels the brackets' side; the route is the same L).
---@opt diameter: number = 0.3 — Pipe diameter in metres.
---@opt count: integer = 1 — Parallel pipes (1 to 4), spaced 1.6 diameters apart.
---@opt brackets: number = 2.5 — Bracket / hanger spacing in metres (0 for none).
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Picks among the best-scoring pipe modules.
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the wall texture (no effect without a room).
---@opt name: string = pipe_run<n> — Names the pipes (tags).
function kit.pipe_run(t)
  local api = "kit.pipe_run"
  K.check_keys(api, t, { "points", "from", "to", "along", "diameter", "count", "brackets", "style", "seed", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "pipe_run", t.name)
  local ctx = new_ctx(api, "pipe_run", t, name)
  local pts
  if t.points ~= nil then
    if t.from ~= nil or t.to ~= nil then K.fail(api, "give points, or from and to, not both.") end
    pts = points_of(api, "points", t.points)
  else
    if t.from == nil or t.to == nil then K.fail(api, "give points = {...}, or from and to.") end
    local a, b = points_of(api, "from/to", { t.from, t.to })[1], points_of(api, "from/to", { t.from, t.to })[2]
    pts = { a }
    if abs(b[1] - a[1]) > 1e-3 then pts[#pts + 1] = { b[1], a[2], a[3] } end
    if abs(b[3] - a[3]) > 1e-3 then pts[#pts + 1] = { b[1], a[2], b[3] } end
    if abs(b[2] - a[2]) > 1e-3 then pts[#pts + 1] = { b[1], b[2], b[3] } end
    if #pts == 1 then K.fail(api, "from and to are the same point.") end
  end
  if t.along ~= nil and t.along ~= "ceiling" and t.along ~= "wall" and t.along ~= "floor" then K.fail(api, "'along' must be \"ceiling\", \"wall\" or \"floor\".") end
  local dia = K.positive(api, "diameter", t.diameter, 0.3)
  local n = K.number(api, "count", t.count, 1)
  if n < 1 or n > 4 or n ~= floor(n) then K.fail(api, "'count' must be a whole number from 1 to 4.") end
  local bp = K.number(api, "brackets", t.brackets, 2.5)
  local cand = ranked(ctx, { "pipe" }, pipe_ok(dia))
  local pm = cand[1] and cand[ctx.st.int(1, min(3, #cand))].r or nil
  if not pm then
    if modules.available then fallback(ctx, "no stretchable " .. fmt(dia) .. " m pipe module") else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  end
  -- Offsets of the parallel pipes, across the first leg in the horizontal plane (or along x for a vertical one).
  for i = 1, #pts - 1 do
    local a, b = pts[i], pts[i + 1]
    local diff = { b[1] - a[1], b[2] - a[2], b[3] - a[3] }
    local nz = 0
    for _, v in ipairs(diff) do if abs(v) > 1e-3 then nz = nz + 1 end end
    if nz ~= 1 then K.fail(api, "points " .. i .. " and " .. (i + 1) .. " must differ on exactly one of x, y or z.", "Add corner points (or give from/to for a routed L).") end
  end
  local first = { pts[2][1] - pts[1][1], pts[2][2] - pts[1][2], pts[2][3] - pts[1][3] }
  local ox, oy, oz = 0, 0, 0
  if abs(first[1]) > 1e-3 then oz = 1 elseif abs(first[3]) > 1e-3 then ox = 1 else ox = 1 end
  local gap = dia * 1.6
  for k = 0, n - 1 do
    local off = (k - (n - 1) / 2) * gap
    local list = {}
    for i = 1, #pts - 1 do
      local a = { pts[i][1] + ox * off, pts[i][2] + oy * off, pts[i][3] + oz * off }
      local b = { pts[i + 1][1] + ox * off, pts[i + 1][2] + oy * off, pts[i + 1][3] + oz * off }
      local axis, ai = "x", 1
      if abs(b[2] - a[2]) > 1e-3 then axis, ai = "y", 2 elseif abs(b[3] - a[3]) > 1e-3 then axis, ai = "z", 3 end
      local lo, hi = min(a[ai], b[ai]), max(a[ai], b[ai])
      local c1, c2
      if axis == "x" then c1, c2 = a[2], a[3] elseif axis == "z" then c1, c2 = a[1], a[2] else c1, c2 = a[1], a[3] end
      local placed_mod = false
      if pm and axis ~= "y" then
        local f = frame_of(axis == "x" and 0 or 270)
        local nb = stretched(pm, hi - lo, nil, nil)
        if nb then
          local wx = axis == "x" and (lo + hi) / 2 or a[1]
          local wz = axis == "z" and (lo + hi) / 2 or a[3]
          local px, py, pz = origin_for(f, (nb.x0 + nb.x1) / 2, (nb.y0 + nb.y1) / 2, (nb.z0 + nb.z1) / 2, wx, a[2], wz)
          place(ctx, pm, f, px, py, pz, hi - lo, nil, nil, "theme_detail")
          placed_mod = true
        end
      end
      if not placed_mod then
        list[#list + 1] = pcyl(ctx, "pipe", "theme_detail", "pipe", axis, lo, hi, c1, c2, dia, ctx.pal.pipe)
      end
      -- Flanges at the leg ends and every 4 m.
      local m = max(1, floor((hi - lo) / 4 + 0.5))
      for q = 0, m do
        local at = lo + (hi - lo) * q / m
        local f0, f1 = at - 0.06, at + 0.06
        if q == 0 then f0, f1 = lo, lo + 0.12 elseif q == m then f0, f1 = hi - 0.12, hi end
        list[#list + 1] = pcyl(ctx, "pipe", "theme_detail", "flange", axis, f0, f1, c1, c2, dia * 1.35, ctx.pal.flange)
      end
      -- Brackets / hangers to the nearest surface at the pitch.
      if bp > 0 then
        local nbk = max(1, floor((hi - lo) / bp))
        for q = 1, nbk do
          local at = lo + (hi - lo) * (q - 0.5) / nbk
          local px, py, pz
          if axis == "x" then px, py, pz = at, c1, c2 elseif axis == "z" then px, py, pz = c1, c2, at else px, py, pz = c1, at, c2 end
          local surf = nearest_surface(px, py, pz, 2.5, axis)
          if surf and surf.dist > dia / 2 + 0.05 then
            local d = surf.d
            local e0, e1 = dia / 2, surf.dist + 0.05
            local hw = 0.05
            local x0 = px + min(d[1] * e0, d[1] * e1) - (d[1] == 0 and hw or 0)
            local x1 = px + max(d[1] * e0, d[1] * e1) + (d[1] == 0 and hw or 0)
            local y0 = py + min(d[2] * e0, d[2] * e1) - (d[2] == 0 and hw or 0)
            local y1 = py + max(d[2] * e0, d[2] * e1) + (d[2] == 0 and hw or 0)
            local z0 = pz + min(d[3] * e0, d[3] * e1) - (d[3] == 0 and hw or 0)
            local z1 = pz + max(d[3] * e0, d[3] * e1) + (d[3] == 0 and hw or 0)
            list[#list + 1] = pbox(ctx, "support", "theme_detail", "bracket", x0, y0, z0, x1, y1, z1, ctx.pal.frame)
          end
        end
      end
    end
    -- Elbow collars at the turns.
    for i = 2, #pts - 1 do
      local p = { pts[i][1] + ox * off, pts[i][2] + oy * off, pts[i][3] + oz * off }
      local s2 = dia * 0.8
      list[#list + 1] = pbox(ctx, "pipe", "theme_detail", "elbow", p[1] - s2, p[2] - s2, p[3] - s2, p[1] + s2, p[2] + s2, p[3] + s2, ctx.pal.flange)
    end
    if #list > 0 then prim_record(ctx, "pipe", "theme_detail", "pipe", list, pts[1], 0, { dia, dia, dia }) end
  end
  return finish(ctx, { points = pts })
end

-- ---------------------------------------------------------------------------------------------
-- kit.stair_detail
-- ---------------------------------------------------------------------------------------------

local stair_impl

---@kit kit.stair_detail{stairs, style?, seed?, stringers?, rails?, posts_every?, name?} → detail — Finishes a kit.stairs flight: sloped stringers along both sides and handrails (a post every few treads and a sloped rail through their tops), hand-authored in the style's stock textures; set inside the stair width only while the clear width stays at least the player width, else just outside it where there is open air. Role route.
---@opt stairs: record — Required: a kit.stairs record.
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — Kept for the kit's rules (the result does not depend on it).
---@opt stringers: boolean = true — Build the two side stringers.
---@opt rails: string = "both" — Handrails: "both", "left", "right" (looking up the stairs) or "none".
---@opt posts_every: integer = 3 — A rail post every this many treads.
---@opt name: string = stair_detail<n> — Names the detail (tags).
function kit.stair_detail(t)
  local api = "kit.stair_detail"
  K.check_keys(api, t, { "stairs", "style", "seed", "stringers", "rails", "posts_every", "name" })
  local name = K.name_for(api, "stair_detail", t.name)
  local ctx = new_ctx(api, "stair_detail", t, name)
  return finish(ctx, stair_impl(api, t, ctx))
end

-- The work of kit.stair_detail (also run by the layout's detail pass in a zone's turned frame: the flight's bounds are
-- read in the frame, where its steps run along an axis).
stair_impl = function(api, t, ctx)
  local rec = t.stairs
  if type(rec) ~= "table" or rec.kind ~= "stairs" or type(rec.brushes) ~= "table" or rec.top == nil then
    K.fail(api, "'stairs' must be a kit.stairs record.")
  end
  local x0, y0, z0, x1, y1, z1 = math.huge, math.huge, math.huge, -math.huge, -math.huge, -math.huge
  local nsteps = 0
  for _, b in ipairs(rec.brushes) do
    local part = b:tags()["kit.part"] or ""
    if part:sub(1, 4) == "step" then
      nsteps = nsteps + 1
      if XF then
        for _, f in ipairs(b.faces) do
          for _, v in ipairs(f.vertices) do
            local lx, lz = xf_l(v.x, v.z)
            x0, y0, z0 = min(x0, lx), min(y0, v.y), min(z0, lz)
            x1, y1, z1 = max(x1, lx), max(y1, v.y), max(z1, lz)
          end
        end
      else
        local bb = b.bounds
        x0, y0, z0 = min(x0, bb.min.x), min(y0, bb.min.y), min(z0, bb.min.z)
        x1, y1, z1 = max(x1, bb.max.x), max(y1, bb.max.y), max(z1, bb.max.z)
      end
    end
  end
  if nsteps == 0 then K.fail(api, "the stairs record has no steps.") end
  local top = rec.top
  if XF then
    local lx, lz = xf_l(top.x, top.z)
    top = { x = lx, y = top.y, z = lz }
  end
  local along_x = (x1 - x0) > (z1 - z0) + 1e-3 or (abs(top.x - (x0 + x1) / 2) > abs(top.z - (z0 + z1) / 2))
  -- dir: the climbing direction along the long axis.
  local sgn
  if along_x then sgn = (top.x > (x0 + x1) / 2) and 1 or -1 else sgn = (top.z > (z0 + z1) / 2) and 1 or -1 end
  local a0 = along_x and (sgn > 0 and x0 or x1) or (sgn > 0 and z0 or z1)
  local run = along_x and (x1 - x0) or (z1 - z0)
  local w0, w1 = along_x and z0 or x0, along_x and z1 or x1
  local width = w1 - w0
  local rise = y1 - y0
  local tread = run / nsteps
  local riser = rise / nsteps
  local slope = rise / run
  local pr = probe()
  -- Inside the width when the clear width allows, else just outside (where air is).
  local th = 0.16
  local inside = width - 2 * th >= KIT.limits.min_clear_width - 1e-4
  local function pt(a, wv, y) -- a along the climb from the foot, wv across, y absolute
    local la = a0 + sgn * a
    if along_x then return { la, y, wv } end
    return { wv, y, la }
  end
  local sides = {}
  for _, s in ipairs({ -1, 1 }) do
    local edge = s < 0 and w0 or w1
    local wa, wb
    if inside then
      wa, wb = (s < 0) and edge or edge - th, (s < 0) and edge + th or edge
    else
      wa, wb = (s < 0) and edge - th or edge, (s < 0) and edge or edge + th
      local mx = (s < 0) and edge - th / 2 or edge + th / 2
      local q = pt(run / 2, mx, y0 + rise / 2 + 0.3)
      if not pr:air_exact(q[1], q[2], q[3]) then wa = nil end
    end
    sides[#sides + 1] = { s = s, wa = wa, wb = wb, rail = (s < 0) and wb or wa }
  end
  -- "left" looking up the stairs: for a climb along +x, left is +z.
  local function is_left(s)
    if along_x then return (sgn > 0) == (s > 0) end
    return (sgn > 0) == (s < 0)
  end
  local list = {}
  local tex = ctx.pal.frame
  if K.boolean(api, "stringers", t.stringers, true) then
    for _, sd in ipairs(sides) do
      if sd.wa then
        local d = 0.45
        local pts = {}
        for _, wv in ipairs({ sd.wa, sd.wb }) do
          pts[#pts + 1] = pt(0, wv, y0)
          pts[#pts + 1] = pt(min(run, d / max(slope, 0.1)), wv, y0)
          pts[#pts + 1] = pt(run, wv, y1 - d)
          pts[#pts + 1] = pt(run, wv, y1 + 0.18)
          pts[#pts + 1] = pt(0, wv, y0 + riser + 0.18)
        end
        list[#list + 1] = prim_brush(ctx, "stairs", "route", "stringer", { points = pts }, tex)
      else
        skip(ctx, "stringer", "no open air beside the stairs and no room inside them")
      end
    end
  end
  local rails = t.rails or "both"
  if rails ~= "both" and rails ~= "left" and rails ~= "right" and rails ~= "none" then K.fail(api, "'rails' must be \"both\", \"left\", \"right\" or \"none\".") end
  local every = K.number(api, "posts_every", t.posts_every, 3)
  if every < 1 or every ~= floor(every) then K.fail(api, "'posts_every' must be a whole number of 1 or more.") end
  for _, sd in ipairs(sides) do
    local want = rails == "both" or (rails == "left" and is_left(sd.s)) or (rails == "right" and not is_left(sd.s))
    if want and sd.wa then
      local wv = (sd.wa + sd.wb) / 2
      local hgt = 1.0
      local first, last = nil, nil
      for i = 1, nsteps, every do
        local a = (i - 0.5) * tread
        local ty = y0 + i * riser
        local c = pt(a, wv, ty)
        list[#list + 1] = pbox(ctx, "railing", "route", "post", c[1] - 0.05, ty, c[3] - 0.05, c[1] + 0.05, ty + hgt, c[3] + 0.05, ctx.pal.rail)
        first = first or { a, ty }
        last = { a, ty }
      end
      local lastc = { (nsteps - 0.5) * tread, y0 + nsteps * riser }
      if last[1] < lastc[1] - 1e-3 then
        local c = pt(lastc[1], wv, lastc[2])
        list[#list + 1] = pbox(ctx, "railing", "route", "post", c[1] - 0.05, lastc[2], c[3] - 0.05, c[1] + 0.05, lastc[2] + hgt, c[3] + 0.05, ctx.pal.rail)
        last = lastc
      end
      if last[1] > first[1] + 0.2 then
        local pts = {}
        for _, e in ipairs({ first, last }) do
          for _, dw in ipairs({ -0.04, 0.04 }) do
            for _, dy in ipairs({ hgt - 0.02, hgt + 0.06 }) do
              pts[#pts + 1] = pt(e[1], wv + dw, e[2] + dy)
            end
          end
        end
        list[#list + 1] = prim_brush(ctx, "railing", "route", "handrail", { points = pts }, ctx.pal.rail)
      end
    end
  end
  if modules.available then note(ctx, "stair stringers and rails are hand-authored in the style's stock textures") end
  if #list > 0 then prim_record(ctx, "stairs", "route", "stair_detail", list, { x0, y0, z0 }, 0, { x1 - x0, rise, z1 - z0 }) end
  return { inside = inside, width = width, clear_width = inside and width - 2 * th or width }
end

-- ---------------------------------------------------------------------------------------------
-- kit.rock_detail
-- ---------------------------------------------------------------------------------------------

local function rock_ok(H)
  return function(r)
    if r.has_air or r.brushes > 30 then return false end
    if r.sy < 0.6 or r.sy > min(H - 0.5, 10) or r.sx > 9 or r.sz > 6 or r.sx < 0.6 then return false end
    if r.attach ~= "floor" and r.attach ~= "wall" and r.attach ~= "corner" and r.attach ~= "free" and r.attach ~= "column" then return false end
    -- Real rock: every texture a rock texture (no placeholder faces, no machinery that happens to touch rock).
    for _, tname in ipairs(r.textures) do
      local l = tname:lower()
      if l:sub(1, 4) ~= "rck_" or l:find("default", 1, true) or l:find("invisible", 1, true) then return false end
    end
    return true
  end
end

-- A primitive boulder standing on (x, gy, z): the convex hull of points on a squashed, irregular dome (wider at
-- its foot), sunk 0.2 m into the floor so it never floats, rock-textured.
local function boulder(ctx, role, x, gy, z, sx, sy, sz, tex)
  local st = ctx.st
  local pts = {}
  local rot = st.range(-0.35, 0.35)
  local cr, sr = math.cos(rot), math.sin(rot)
  local rings = { { -0.2, 1.0 }, { sy * 0.3, 0.95 }, { sy * 0.65, 0.75 }, { sy * 0.9, 0.42 } }
  for ri, ring in ipairs(rings) do
    local n = (ri == #rings) and 4 or 6
    local off = st.range(0, 2 * math.pi / n)
    for k = 1, n do
      local a = off + (k - 1) * 2 * math.pi / n
      local rad = ring[2] * st.range(0.78, 1.0)
      local lx, lz = math.cos(a) * sx / 2 * rad, math.sin(a) * sz / 2 * rad
      pts[#pts + 1] = { x + lx * cr - lz * sr, gy + ring[1] + (ri > 1 and st.range(-0.08, 0.08) * sy or 0), z + lx * sr + lz * cr }
    end
  end
  pts[#pts + 1] = { x + st.range(-0.1, 0.1) * sx, gy + sy, z + st.range(-0.1, 0.1) * sz }
  local b = prim_brush(ctx, "rock", role, "boulder", { points = pts, snap = 1 / 64 }, tex)
  b:box_map{ tile = KIT.defaults.tile_metres }
  return b
end

---@kit kit.rock_detail{space | path, style?, seed?, count?, role?, keep?, budget?, custom?, retexture?, name?} → detail — Rock formations at the foot of cliff, canyon and cave walls: a library rock (or a large roughened boulder) with smaller boulders clustered beside it, partly sunk into the floor so nothing floats, plus a few free-standing clusters as cover where the floor is wide — through the generative fill's engine space so routes, doorways and spawns stay clear and the walk budget holds. Rocks take the space's own wall texture (retexture, default on) so they match the cliff. Roles: cover for clusters 1-2.6 m tall, landmark for big formations, theme_detail otherwise (or role = …).
---@opt space: record|string|{min=, max=} — The space: a kit record (canyon, cave, room …), a kit.name, or a box (give space or path).
---@opt path: record — A kit.canyon / kit.cave / kit.passage record whose floor the rocks line.
---@opt style: string = "canyon" — "canyon", "cave" or "mine" (or any style family).
---@opt seed: integer = 1 — Picks the spots and the formations.
---@opt count: integer = 1 per 40 m² (2 to 16) — How many wall clusters.
---@opt role: string = by size — Record every formation as this role.
---@opt keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---@opt budget: number = 0.02 — Largest share of the walkable floor the formations may take (at most 0.02).
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt retexture: boolean = true — Give library rocks the space's own wall texture (false keeps theirs).
---@opt name: string = rock_detail<n> — Names the formations (tags).
function kit.rock_detail(t)
  local api = "kit.rock_detail"
  K.check_keys(api, t, { "space", "path", "style", "seed", "count", "role", "keep", "budget", "custom", "retexture", "name" })
  local name = K.name_for(api, "rock_detail", t.name)
  local opts = {}
  for k, v in pairs(t) do opts[k] = v end
  if opts.style == nil then opts.style = "canyon" end
  if opts.retexture == nil then opts.retexture = true end
  local ctx = new_ctx(api, "rock_detail", opts, name)
  if t.space ~= nil and t.path ~= nil then K.fail(api, "give 'space' or 'path', not both.") end
  local src = t.space or t.path
  if src == nil then K.fail(api, "'space' or 'path' is required.") end
  local arg = {}
  if type(src) == "table" and src.min ~= nil and src.kind == nil then arg.area = src else arg.room = src end
  local s = P.spaces(api, arg, K.fail)[1]
  ctx.wall_texture = wall_texture_of(s)
  local rock_tex = ctx.wall_texture or ctx.pal.rock
  local y0 = s.floor_y or s.y0
  local H = s.y1 - y0
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  local role = role_of(api, t.role)
  local n = t.count and K.number(api, "count", t.count) or clamp(floor(w * d / 40 + 0.5), 2, 16)
  if n < 0 or n ~= floor(n) then K.fail(api, "'count' must be a whole number of 0 or more.") end
  local budget = t.budget and K.number(api, "budget", t.budget) or 0.02
  if budget < 0 or budget > 0.02 then K.fail(api, "'budget' must be between 0 and 0.02.") end
  local sp = K.dress_space(probe(), s, keep_of(api, t), budget)
  local cand = ranked(ctx, { "rock" }, rock_ok(H))
  if #cand == 0 then
    if modules.available then fallback(ctx, "no " .. ctx.style .. " rock module fits") else fallback(ctx, "no module library (" .. tostring(modules.reason) .. ")") end
  end
  local low, tall = {}, {}
  for _, e in ipairs(cand) do
    if e.r.sy <= 1.45 then low[#low + 1] = e else tall[#tall + 1] = e end
  end
  local st = ctx.st
  local ws = sp.h:wall_spots()
  local spots = {}
  for q = 1, #ws.id do spots[q] = { id = ws.id[q], h = ws.h[q], dist = ws.dist[q] } end
  -- Rough or leaning cliffs leave few flat wall spots: also cast from the space's middle toward each side every
  -- 2.5 m and take the floor node 0.35 m short of the rock face.
  local pr = probe()
  local cx, cz = (s.x0 + s.x1) / 2, (s.z0 + s.z1) / 2
  for h, dv in ipairs(HEAD) do
    local along_x = dv[1] == 0
    local a0, a1 = along_x and s.x0 or s.z0, along_x and s.x1 or s.z1
    local reach = along_x and (s.z1 - s.z0) / 2 + 1 or (s.x1 - s.x0) / 2 + 1
    local tt = a0 + 1.5
    while tt <= a1 - 1.5 do
      local ox, oz = along_x and tt or cx, along_x and cz or tt
      local dd = pr:wall_distance(ox, y0 + 0.6, oz, -dv[1], -dv[2], reach)
      if dd < reach - 0.01 then
        -- The first flat floor node in front of the rock face (a cliff foot often slopes): the rock fills the gap.
        local back = 0.35
        while back <= 3.4 do
          local px, pz = ox - dv[1] * (dd - back), oz - dv[2] * (dd - back)
          local id = P.nearest(sp.L, px, y0 + 0.1, pz, 0.3, 1.0)
          if id and sp.flat[id] and not sp.keepout[id] then
            spots[#spots + 1] = { id = id, h = h, dist = 0, gap = back - 0.35 }
            break
          end
          back = back + 0.5
        end
      end
      tt = tt + 2.5
    end
  end
  for i = #spots, 2, -1 do
    local j = st.int(1, i)
    spots[i], spots[j] = spots[j], spots[i]
  end
  local function role_for(ht, big)
    if role then return role end
    if big then return "landmark" end
    if ht >= 1.0 and ht <= 2.6 then return "cover" end
    return "theme_detail"
  end
  -- One cluster at a wall spot: a main rock (library, else a big boulder) and 1-2 smaller boulders beside it.
  local function cluster(node, dv, dist, k, gap)
    gap = gap or 0
    local ax, az = node.x - dv[1] * dist + dv[1] * 0.02, node.z - dv[2] * dist + dv[2] * 0.02
    local ux, uz = -dv[2], dv[1]
    local main_w, main_h = 0, 0
    local placed = false
    -- Mostly knee-to-chest high rocks (under the 1.45 m walk step they cost no walk budget), one in three taller.
    local pool = (k % 3 == 0) and tall or low
    if #pool == 0 then pool = (#low > 0) and low or tall end
    if #pool > 0 then
      local r = pool[((k - 1) % min(#pool, 6)) + 1].r
      local f = frame_of(yaw_of(dv[1], dv[2]))
      local parts = { { u0 = -r.x1, u1 = -r.x0, v0 = 0, v1 = r.z1 - r.z0, y0 = 0, y1 = min(r.sy, 2.0) } }
      local world, tok = sp:try(parts, ax, node.y, az, dv[1], dv[2], true, 0.2)
      if world then
        sp:commit(world, tok)
        local sink = min(0.3, 0.1 + 0.05 * r.sy)
        local ox, oy, oz = origin_for(f, 0, r.y0, r.z0, ax - dv[1] * (0.2 + gap), node.y - sink, az - dv[2] * (0.2 + gap))
        place(ctx, r, f, ox, oy, oz, nil, nil, nil, role_for(r.sy, r.sx * r.sz * r.sy >= 60))
        main_w, main_h, placed = r.sx, r.sy, true
      end
    end
    if not placed then
      local bw = st.range(1.6, 3.0)
      local bh = (k % 3 == 0) and st.range(1.8, 2.6) or st.range(0.9, 1.4)
      local parts = { { u0 = -bw / 2, u1 = bw / 2, v0 = 0, v1 = bw * 0.75, y0 = 0, y1 = min(bh, 2.0) } }
      local world, tok = sp:try(parts, ax, node.y, az, dv[1], dv[2], true, 0.2)
      if not world then return false end
      sp:commit(world, tok)
      -- Reaching back over any sloped cliff foot to the face, so the rock reads grown out of it.
      local depth = bw * 0.75 + gap
      local cx, cz = ax + dv[1] * (bw * 0.75 / 2 - gap / 2), az + dv[2] * (bw * 0.75 / 2 - gap / 2)
      local fx = abs(dv[1]) > 0.5
      local b = boulder(ctx, role_for(bh, false), cx, node.y, cz, fx and depth or bw, bh + gap * 0.3, fx and bw or depth, rock_tex)
      prim_record(ctx, "rock", role_for(bh, false), "boulder", { b }, { cx, node.y, cz }, yaw_of(dv[1], dv[2]), { bw, bh, bw * 0.75 })
      main_w, main_h = bw, bh
      if #cand > 0 then note(ctx, "some clusters are hand-authored boulders") end
    end
    -- Satellites: smaller boulders beside the main rock, along the wall, alternating sides.
    local nsat = st.int(1, 2)
    local side = (st.next() < 0.5) and -1 or 1
    for _ = 1, nsat do
      local sw, sh = st.range(0.7, 1.3), st.range(0.5, min(1.2, main_h * 0.6 + 0.3))
      local off = side * (main_w / 2 + sw / 2 + 0.05)
      local px, pz = ax + ux * off, az + uz * off
      local parts = { { u0 = -sw / 2, u1 = sw / 2, v0 = 0, v1 = sw * 0.9, y0 = 0, y1 = sh } }
      local world, tok = sp:try(parts, px, node.y, pz, dv[1], dv[2], true, 0.1)
      if world then
        sp:commit(world, tok)
        local cx, cz = px + dv[1] * sw * 0.45, pz + dv[2] * sw * 0.45
        local b = boulder(ctx, role_for(sh, false), cx, node.y, cz, sw, sh, sw * 0.9, rock_tex)
        prim_record(ctx, "rock", role_for(sh, false), "boulder", { b }, { cx, node.y, cz }, 0, { sw, sh, sw * 0.9 })
      end
      side = -side
    end
    return true
  end
  local placed, si, tries = 0, 1, 0
  while placed < n and tries < n * 5 and #spots > 0 do
    tries = tries + 1
    local ok = false
    for q = 0, min(#spots, 30) - 1 do
      local spot = spots[((si + q - 1) % #spots) + 1]
      if cluster(sp.L.nodes[spot.id], HEAD[spot.h], spot.dist, placed + 1, spot.gap) then ok = true si = si + q + 9 break end
    end
    if ok then placed = placed + 1 else si = si + 13 end
  end
  -- Free-standing cover clusters where the floor is wide (one per 300 m², at most 3).
  if min(w, d) >= 10 then
    local L = sp.L
    local want = min(3, floor(w * d / 300))
    local got = 0
    for _ = 1, want * 12 do
      if got >= want or #L.nodes == 0 then break end
      local id = st.int(1, #L.nodes)
      local node = L.nodes[id]
      if sp.flat[id] and not sp.keepout[id] and not sp.occupied[id] then
        local bw, bh = st.range(1.4, 2.4), st.range(1.1, 1.8)
        local hv = HEAD[st.int(1, 4)]
        local parts = { { u0 = -bw / 2, u1 = bw / 2, v0 = -bw / 2, v1 = bw / 2, y0 = 0, y1 = bh } }
        local world, tok = sp:try(parts, node.x, node.y, node.z, hv[1], hv[2], false, 0.8)
        if world then
          sp:commit(world, tok)
          local b = boulder(ctx, role or "cover", node.x, node.y, node.z, bw, bh, bw * 0.8, rock_tex)
          prim_record(ctx, "rock", role or "cover", "boulder", { b }, { node.x, node.y, node.z }, 0, { bw, bh, bw * 0.8 })
          got = got + 1
        end
      end
    end
  end
  if placed < n then skip(ctx, "rock", (n - placed) .. " of " .. n .. " found no clear spot within the walk budget") end
  return finish(ctx, { count = placed })
end

-- ---------------------------------------------------------------------------------------------
-- kit.detail
-- ---------------------------------------------------------------------------------------------

---@kit kit.detail{room|space, style?, seed?, level?, doorways?, stairs?, edges?, keep?, custom?, textures?, retexture?, name?} → detail — The detailing pass an agent reaches for once a room's shell is built: kit.wall_detail (pilasters, trims, band, panels: theme_detail), kit.frame_opening on every door and window opening of the room first (found by probing its walls, plus the doorways given; the pilasters then keep clear of the frames), and with level = "full" also kit.ceiling_detail (beams on the pilaster pitch: theme_detail) and kit.machinery (cover / fixture), kit.stair_detail for the stairs given and kit.railing along the edges given (route) — one style, one seed, one donor set. Use "light" for corridors and small rooms, "full" for halls and set-piece rooms. Returns the merged record plus parts = {wall, frames, ceiling, machinery, stairs, railings}.
---@opt room: record|string — The room (a kit record or kit.name; give room or space).
---@opt space: {min=, max=} — A box instead of a room.
---@opt style: string = "industrial" — Style family (see kit.wall_detail).
---@opt seed: integer = 1 — One seed for every part.
---@opt level: string = "full" — "light" (walls and frames) or "full" (plus ceiling, machinery, stairs and railings).
---@opt doorways: record[] = none — Extra kit.doorway / kit.opening records to frame (openings in the room's walls are found anyway).
---@opt stairs: record[] = none — kit.stairs records in the room to finish (full only).
---@opt edges: vec[][] = none — Floor-edge paths that get railings (full only).
---@opt keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---@opt custom: boolean = false — Allow modules that need custom textures.
---@opt textures: {old = new} = none — Texture replacements on every placed module.
---@opt retexture: boolean = false — Map every module texture onto the room's wall texture.
---@opt name: string = detail<n> — Names the detail; the parts are named <name>_walls, <name>_frame<i>, <name>_ceiling, <name>_machinery.
function kit.detail(t)
  local api = "kit.detail"
  K.check_keys(api, t, { "room", "space", "style", "seed", "level", "doorways", "stairs", "edges", "keep", "custom", "textures", "retexture", "name" })
  local name = K.name_for(api, "detail", t.name)
  local lvl = t.level or "full"
  if lvl ~= "light" and lvl ~= "full" then K.fail(api, "'level' must be \"light\" or \"full\" (got " .. tostring(lvl) .. ").") end
  local style = style_of(api, t.style)
  local seed = seed_of(api, t)
  local boxes = boxes_of(api, t)
  local base = { style = style, seed = seed, custom = t.custom, textures = t.textures, retexture = t.retexture }
  local function with(extra)
    local c = {}
    for k, v in pairs(base) do c[k] = v end
    for k, v in pairs(extra) do c[k] = v end
    return c
  end
  local where = (t.room ~= nil) and { room = t.room } or { space = t.space }
  local out = { name = name, placed = {}, counts = {}, roles = {}, skipped = {}, fallback = false, notes = {}, brushes = {}, parts = {} }
  local function merge(key, r)
    out.parts[key] = r
    for _, e in ipairs(r.placed) do out.placed[#out.placed + 1] = e end
    for k, v in pairs(r.counts) do out.counts[k] = (out.counts[k] or 0) + v end
    for k, v in pairs(r.roles) do out.roles[k] = (out.roles[k] or 0) + v end
    for _, e in ipairs(r.skipped) do out.skipped[#out.skipped + 1] = e end
    for _, b in ipairs(r.brushes) do out.brushes[#out.brushes + 1] = b end
    if r.fallback then out.fallback = true end
    if r.note and r.note ~= "library modules" then out.notes[#out.notes + 1] = key .. ": " .. r.note end
  end
  -- 1. Openings (probed before anything stands at the walls), 2. frames and (full) machinery against the walls (the
  -- walls' pilasters and trims then keep clear of them), 3. walls, 4. ceiling, 5. stairs and railings.
  local found = {}
  for _, b in ipairs(boxes) do
    for _, o in ipairs(openings_of(b, { "+x", "-x", "+z", "-z" })) do found[#found + 1] = o end
  end
  local frames = {}
  local i = 0
  for _, o in ipairs(found) do
    i = i + 1
    local r = kit.frame_opening(with{ pos = o.pos, dir = (o.nx > 0.5 and "+x") or (o.nx < -0.5 and "-x") or (o.nz > 0.5 and "+z") or "-z",
      width = o.width, height = o.height, thickness = o.thickness, name = name .. "_frame" .. i })
    frames[#frames + 1] = r
    merge("frame" .. i, r)
  end
  if t.doorways ~= nil then
    if type(t.doorways) ~= "table" then K.fail(api, "'doorways' must list kit.doorway / kit.opening records.") end
    for _, d in ipairs(t.doorways) do
      i = i + 1
      local r = kit.frame_opening(with{ doorway = d, name = name .. "_frame" .. i })
      frames[#frames + 1] = r
      merge("frame" .. i, r)
    end
  end
  out.parts.frames = frames
  if lvl == "full" then
    merge("machinery", kit.machinery(with{ room = where.room, space = where.space, keep = t.keep, name = name .. "_machinery" }))
  end
  local wd = kit.wall_detail(with{ room = where.room, space = where.space, keep = t.keep, name = name .. "_walls" })
  merge("wall", wd)
  if lvl == "full" then
    local H = 0
    for _, b in ipairs(boxes) do H = max(H, b.y1 - (b.floor_y or b.y0)) end
    if H >= 4.4 then
      merge("ceiling", kit.ceiling_detail(with{ room = where.room, space = where.space, name = name .. "_ceiling" }))
    end
    if t.stairs ~= nil then
      if type(t.stairs) ~= "table" then K.fail(api, "'stairs' must list kit.stairs records.") end
      for k, sr in ipairs(t.stairs) do
        merge("stairs" .. k, kit.stair_detail{ stairs = sr, style = style, seed = seed, name = name .. "_stairs" .. k })
      end
    end
    if t.edges ~= nil then
      if type(t.edges) ~= "table" then K.fail(api, "'edges' must list floor-edge paths {{x, y, z}, ...}.") end
      for k, e in ipairs(t.edges) do
        merge("railing" .. k, kit.railing{ path = e, style = style, seed = seed, name = name .. "_railing" .. k })
      end
    end
  end
  out.note = #out.notes > 0 and table.concat(out.notes, "; ") or "library modules"
  out.notes = nil
  out.style = style
  out.level = lvl
  out.donor = wd.donor
  out.pitch = wd.pitch
  return out
end

-- The installer: copies the functions above into the kit.
return function(target, internals)
  KIT, K = target, internals
  for _, fn in ipairs({ "wall_detail", "frame_opening", "ceiling_detail", "machinery", "railing", "catwalk", "pipe_run", "stair_detail", "rock_detail", "detail" }) do
    target[fn] = kit[fn]
  end
  target.detail_styles = STYLES
  -- The pieces kit.build_layout's room detail pass composes (kit_layout.lua): the same parts, run in a zone's frame.
  internals.detail_kit = {
    set_frame = set_frame, xf_w = xf_w, xf_l = xf_l, new_ctx = new_ctx, finish = finish, fallback = fallback, note = note, skip = skip,
    family = family, openings_of = openings_of, profile = function(s) return wall_profile(s, wall_frame(s, "-z")) end, frame_one = frame_one, framed_already = framed_already, walls_impl = walls_impl,
    beams_impl = beams_impl, railing_impl = railing_impl, stair_impl = stair_impl,
    quietly = function(fn, arg) QUIET = true local r = fn(arg) QUIET = false return r end,
    styles = function() return STYLES end, palette = function(style) return PALETTE[style] or PALETTE.industrial end,
  }
end
