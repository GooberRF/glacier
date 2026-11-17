-- Glacier design kit: population — generative fill (kit.dress) and light fixtures (kit.light_room).
--
-- Installed into the kit by kit.lua (ged.import("kit_dress")(kit, internals)); every function below
-- is a kit function (kit.dress, kit.light_room) with the kit's rules: one option table, unknown keys
-- rejected, tagged editable output (kit = "dress" / "light_room", kit.name, kit.part), and an explicit
-- seed instead of the run's rng (same options + same level + same seed = same result).
--
-- What kit.dress places comes from the exemplar study (docs/internal/DESIGN-QUALITY-SPEC.md §a.3,
-- §c.2): 5-10 dressing elements per 100 m^2 (detail props, beams, clutter, decals) with every 8 m
-- cell dressed, cover 1.2-2.5 m tall every 5-6 m in spaces wider than 8 m, one tall sightline breaker
-- per 24 m, decals 0.05-0.5 per 100 m^2 — while the walkable floor changes by at most 2 % and no
-- route, doorway, spawn or item is blocked. Clutter classes, decal and fixture textures are stock
-- names from the game tables and the stock levels; clutter is only placed when the tables are
-- loaded (catalog.available) and the class exists there.
--
-- This module only defines functions and constants at its top level.

local P = ged.import("kit_probe")
local CORE = ged.import("kit_core")

local floor, ceil, sqrt, abs, min, max = math.floor, math.ceil, math.sqrt, math.abs, math.min, math.max
local PITCH = P.PITCH

-- The kit this module is installed in, and the kit's internal helpers (set by the installer); the
-- functions are defined on a staging table and copied into the kit.
local KIT, K
local kit = {}

-- ---------------------------------------------------------------------------------------------
-- Theme packs. Textures are stock names (each appears in the stock texture packfiles). Decal
-- textures are the ones the stock levels project (dm01/dm02/dm06/dm15/dm16/dm19/ctf07 decals).
-- Clutter classes are stock clutter.tbl classes; ext = their mesh bounds {x0, x1, y0, y1, z0, z1}
-- in metres, measured from the stock .v3m files (V3dReader, LOD 0 vertices), used as footprints.
-- ---------------------------------------------------------------------------------------------

local CLUTTER = {
  ["Oil Drum"] = { -0.33, 0.33, -0.52, 0.52, -0.33, 0.33 },
  ["Ultor Barrel Yellow"] = { -0.42, 0.40, -0.67, 0.67, -0.41, 0.39 },
  ["Ultor Crate Yellow"] = { -0.68, 0.68, -0.45, 0.45, -0.57, 0.57 },
  ["Square Plastic Crate"] = { -0.38, 0.38, 0.00, 0.65, -0.37, 0.37 },
  ["Long Plastic Crate"] = { -0.90, 0.90, 0.00, 0.65, -0.37, 0.37 },
  ["Cable Spool"] = { -2.00, 2.00, 0.00, 2.25, -1.90, 1.90 },
  ["Generator_Small01"] = { -0.70, 1.51, -1.46, 0.59, -1.00, 1.00 },
  ["Pump02"] = { -0.50, 0.50, -0.69, 0.81, -0.75, 0.75 },
  ["AirCompressor01"] = { -0.72, 0.72, -0.87, 0.83, -1.24, 1.24 },
  ["bigtoolbox"] = { -0.43, 0.43, -0.71, 0.71, -0.34, 0.34 },
  ["Console_large01"] = { -2.08, 2.08, -0.48, 0.48, -0.59, 0.59 },
  ["ControlPanel01"] = { -1.41, 1.47, -1.22, 1.24, -0.42, 0.49 },
  ["Com_CtrlPanel01"] = { -1.01, 1.01, -1.15, 1.15, -1.05, 1.05 },
  ["Standalone Console1"] = { -0.28, 0.28, -1.13, 1.13, -0.30, 0.30 },
  ["Satellite Computer1"] = { -0.35, 0.35, -0.81, 0.81, -0.17, 0.17 },
  ["Lockers"] = { -1.11, 1.11, -1.25, 1.25, -0.33, 0.33 },
  ["Trash Can1"] = { -0.34, 0.34, -0.68, 0.68, -0.29, 0.29 },
  ["Metal Debris 1"] = { -1.55, 1.55, -0.53, 0.53, -1.38, 1.38 },
}

-- Ceiling fixtures: mesh top above the origin (the origin hangs that far under the ceiling) and how
-- far under the ceiling the light goes (just below the lamp). Stock MP levels hang a small light
-- right under each fixture (dm01 Tube Light 0.19 m, dm12 Double Mine Light 4 0.29 m, ctf07
-- ShopLight01 1.9 m).
local FIXTURES = {
  ["Mine Light 8"] = { top = 0.01, drop = 0.35 },
  ["Double Mine Light 4"] = { top = 0.07, drop = 0.4 },
  ["Tube Light"] = { top = 0.07, drop = 0.3 },
  ["SlatLight01"] = { top = 0.03, drop = 0.3 },
  ["Mine Light 3"] = { top = 0.24, drop = 0.85 },
  ["ShopLight01"] = { top = 0.58, drop = 1.35, min_height = 6 },
}
-- The outdoor fixture: a floor-standing lamp post. Like every bound above it is measured in the engine's
-- model space (LOD 0 vertices plus the submesh offset, which the game adds — stock levels stand their
-- plastic crates and cable spools with the origin on the floor): x -0.57..0.17, y -3.00..5.44,
-- z -2.97..0.37. The post stands at model (-0.2, 0); its arm reaches 2.1 m along -Z to the lamp head,
-- whose underside is about 7.6 m above the base.
local POLE = { class = "Pole_Light_1_Dirty", base = 3.00, top = 8.44, light = 7.6, post_x = -0.2, arm = 2.1 }

local THEMES = {
  industrial = {
    label = "industrial yard",
    tex = {
      crate = { "mtl_crate01.tga", "mtl_con_crate01.tga", "mtl_crategreen01.tga", "mtl_con_crate02.tga" },
      metal = { "mtl_girder01.tga", "mtl_supportbeam02.tga", "mtl_girder03.tga" },
      pipe = { "mtl_greypipe02_rust.tga", "mtl_brwnpipe01.tga", "mtl_yellpipe02_drt.tga" },
      machine = { "mtl_contrl_panel08.tga", "mtl_beigedrt04_pipepanel.tga", "mtl_panel09.tga" },
      barrel = { "mtl_redpipe02_drt.tga", "mtl_yellpipe02_drt.tga", "mtl_blupipe02_drt.tga" },
      concrete = { "cem_greypanel01.tga", "cem_cement_panel02.tga" },
      hazard = { "mtl_hazardstripe01.tga" },
      panel = { "mtl_beigedrt07_ventpanel.tga", "mtl_contrl_panel03.tga" },
    },
    props = { { "crates", 3 }, { "crate_block", 3 }, { "barrels", 3 }, { "pipe_bundle", 2 }, { "pipe_run", 2 }, { "machine", 2 }, { "generator", 2 }, { "pallet", 2 } },
    cover = { { "barrier", 3 }, { "crate_row", 3 }, { "low_wall", 1 }, { "pipe_bundle", 1 } },
    tall = { "crate_tower", "machine_tall" },
    overhead = { "beam", "pipe_overhead" },
    wall = { "wall_pipe", "wall_panel", "wall_vent" },
    clutter = { "Oil Drum", "Ultor Barrel Yellow", "Ultor Crate Yellow", "Generator_Small01", "Pump02", "AirCompressor01", "bigtoolbox" },
    signs = { { "mtl_hi_pressure01.tga", 4, 0.5 }, { "mtl_highvoltage.tga", 1.25, 1.25 }, { "mtl_heavy_equipment01.tga", 2, 2 },
      { "mtl_safety1st01.tga", 2, 2 }, { "mtl_watchstep01.tga", 2, 1 }, { "mtl_loadingdock01.tga", 4, 2 }, { "mtl_danger01.tga", 2, 1 } },
    grime = { { "blastmark02.tga", 4, 3, 160 }, { "missileblastmk01_A.tga", 4, 3, 192 } },
    streaks = { { "vertdirt02_A.tga", 3, 5, 128 }, { "cables06_A.tga", 3.5, 1.25, 255 } },
    fixtures = { "ShopLight01", "Double Mine Light 4", "Mine Light 8" },
    light_color = { 255, 222, 180 },
  },
  construction = {
    label = "construction site",
    tex = {
      crate = { "mtl_con_crate01.tga", "mtl_con_crate02.tga", "pls_cratedrt01.tga" },
      metal = { "mtl_girder05.tga", "mtl_girder01.tga", "mtl_airgirder01.tga" },
      pipe = { "mtl_greypipe02_rust.tga", "mtl_drkpipe01_drt.tga" },
      machine = { "mtl_beigedrt04_pipepanel.tga", "mtl_panel07.tga" },
      barrel = { "mtl_yellpipe02_drt.tga", "mtl_redpipe02_drt.tga" },
      concrete = { "cem_cement_panel02.tga", "cem_cement_panel05.tga", "cem_greypanel02.tga" },
      hazard = { "mtl_hazardstripe01.tga" },
      panel = { "mtl_panel07.tga" },
      wood = { "sld_woodpost01.tga" },
    },
    props = { { "girders", 3 }, { "pallet", 3 }, { "crates", 2 }, { "crate_block", 2 }, { "barrels", 2 }, { "scaffold", 2 }, { "pipe_bundle", 2 }, { "generator", 1 } },
    cover = { { "barrier", 4 }, { "low_wall", 2 }, { "girders_low", 1 } },
    tall = { "crate_tower", "scaffold_wall" },
    overhead = { "beam" },
    wall = { "wall_pipe", "wall_panel" },
    clutter = { "Cable Spool", "Oil Drum", "Generator_Small01", "bigtoolbox", "Metal Debris 1" },
    signs = { { "mtl_heavy_equipment01.tga", 2, 2 }, { "mtl_safety1st01.tga", 2, 2 }, { "mtl_watchstep01.tga", 2, 1 },
      { "mtl_danger01.tga", 2, 1 }, { "mtl_no_horsing02.tga", 2, 2 } },
    grime = { { "blastmark02.tga", 4, 3, 160 } },
    streaks = { { "vertdirt02_A.tga", 3, 5, 128 } },
    fixtures = { "Double Mine Light 4", "Mine Light 3", "Mine Light 8" },
    light_color = { 255, 230, 190 },
  },
  storage = {
    label = "storage",
    tex = {
      crate = { "mtl_crate01.tga", "mtl_crategreen01.tga", "mtl_crategreen02.tga", "mtl_con_crate01.tga" },
      metal = { "mtl_girder03.tga", "mtl_supportbeam02.tga" },
      pipe = { "mtl_blackpipe01.tga", "mtl_greypipe02_rust.tga" },
      machine = { "mtl_panel09.tga" },
      barrel = { "mtl_blupipe02_drt.tga", "mtl_redpipe02_drt.tga" },
      concrete = { "cem_greypanel01.tga" },
      hazard = { "mtl_hazardstripe01.tga" },
      panel = { "mtl_beigedrt07_ventpanel.tga" },
    },
    props = { { "crates", 4 }, { "crate_block", 4 }, { "pallet", 3 }, { "rack", 2 }, { "barrels", 2 } },
    cover = { { "crate_row", 4 }, { "crate_pair", 3 } },
    tall = { "crate_tower", "rack" },
    overhead = { "beam" },
    wall = { "wall_panel", "wall_pipe" },
    clutter = { "Ultor Crate Yellow", "Square Plastic Crate", "Long Plastic Crate", "Oil Drum", "Lockers" },
    signs = { { "mtl_cargo_logo01.tga", 3, 3 }, { "mtl_loadingdock01.tga", 4, 2 }, { "ultorlogo01_A.tga", 3, 3 }, { "ultorlogo02_A.tga", 3, 1.5 } },
    grime = { { "blastmark02.tga", 3, 3, 140 } },
    streaks = { { "vertdirt02_A.tga", 3, 5, 110 } },
    fixtures = { "SlatLight01", "Tube Light", "Double Mine Light 4" },
    light_color = { 245, 240, 225 },
  },
  control = {
    label = "control room",
    tex = {
      crate = { "mtl_crate01.tga" },
      metal = { "mtl_chromegirder01.tga", "mtl_ss_girder02.tga" },
      pipe = { "mtl_chromepipe01.tga", "mtl_energypipe02.tga" },
      machine = { "mtl_contrl_panel01.tga", "mtl_contrl_panel05.tga", "mtl_comp_panel01.tga", "mtl_ss_computers01.tga", "comp_tech_1a.tga" },
      console = { "mtl_contrl_panel02.tga", "mtl_contrl_panel06.tga", "mtl_contrl_panel09.tga", "mtl_conpanel01.tga" },
      barrel = { "mtl_blupipe01.tga" },
      concrete = { "mtl_cleanpanel01.tga", "mtl_cleanpanel02.tga" },
      hazard = { "mtl_hazardstripe01.tga" },
      panel = { "mtl_comp_panel01.tga", "comp_tech_1b.tga", "mtl_contrl_panel04.tga" },
    },
    props = { { "console", 5 }, { "machine", 2 }, { "cabinet", 2 } },
    cover = { { "console_island", 3 }, { "partition", 2 } },
    tall = { "server_rack", "partition_tall" },
    overhead = { "cable_tray" },
    wall = { "wall_panel", "wall_screen", "wall_panel" },
    clutter = { "Console_large01", "ControlPanel01", "Com_CtrlPanel01", "Standalone Console1", "Satellite Computer1" },
    signs = { { "mtl_sensitive_electronix02.tga", 3.25, 0.75 }, { "mtl_res_acc02.tga", 3, 0.75 }, { "mtl_enter_live_area01.tga", 1.5, 1.5 },
      { "mtl_eos_rules01.tga", 2, 2 }, { "mtl_obey_rules01.tga", 2, 2 }, { "ultorlogo02_A.tga", 3, 1.5 } },
    grime = {},
    streaks = { { "cables06_A.tga", 3.5, 1.25, 255 } },
    fixtures = { "SlatLight01", "Tube Light" },
    light_color = { 210, 225, 255 },
  },
  mine = {
    label = "mine",
    tex = {
      crate = { "pls_cratedrt01.tga", "mtl_con_crate01.tga" },
      metal = { "mtl_girder03_dirty01.tga", "mtl_girder01.tga" },
      pipe = { "mtl_brwnpipe02_drt.tga", "mtl_drkpipe01_drt.tga" },
      machine = { "mtl_beigedrt04_pipepanel.tga", "mtl_filthypanels01.tga" },
      barrel = { "mtl_brwnpipe04_drt.tga", "mtl_redpipe02_drt.tga" },
      concrete = { "rck_rock02.tga" },
      hazard = { "mtl_hazardstripe01.tga" },
      panel = { "mtl_filthypanels02.tga" },
      wood = { "sld_woodpost01.tga" },
      rock = { "rck_canyon_rock01.tga", "rck_rock02.tga" },
    },
    props = { { "timber", 3 }, { "rocks", 2 }, { "crates", 2 }, { "barrels", 2 }, { "machine", 1 }, { "generator", 1 }, { "pipe_bundle", 1 } },
    cover = { { "rock_low", 3 }, { "crate_row", 2 }, { "barrier", 1 } },
    tall = { "rock_tall", "crate_tower" },
    overhead = { "timber_beam", "pipe_overhead" },
    wall = { "wall_pipe", "timber_post" },
    clutter = { "Oil Drum", "Generator_Small01", "Pump02", "Metal Debris 1", "bigtoolbox" },
    signs = { { "mtl_danger01.tga", 2, 1 }, { "mtl_hi_pressure01.tga", 4, 0.5 }, { "mtl_biohazard02.tga", 1.5, 1.5 }, { "mtl_watchstep01.tga", 2, 1 } },
    grime = { { "blastmark02.tga", 4, 3, 170 } },
    streaks = { { "vertdirt02_A.tga", 3, 5, 140 } },
    fixtures = { "Mine Light 3", "Mine Light 8", "Double Mine Light 4" },
    light_color = { 255, 205, 150 },
  },
  canyon = {
    label = "outdoor canyon",
    tex = {
      crate = { "pls_cratedrt01.tga", "mtl_con_crate02.tga" },
      metal = { "mtl_girder03_dirty01.tga" },
      pipe = { "mtl_greypipe02_rust.tga" },
      machine = { "mtl_beigedrt04_pipepanel.tga" },
      barrel = { "mtl_redpipe02_drt.tga" },
      concrete = { "cem_cement_panel02.tga" },
      hazard = { "mtl_hazardstripe01.tga" },
      panel = { "mtl_filthypanels01.tga" },
      wood = { "sld_woodpost01.tga" },
      rock = { "rck_canyon_rock01.tga", "rck_canyon_rock02.tga", "rck_tanrock01.tga" },
    },
    props = { { "rocks", 6 }, { "crates", 1 }, { "barrels", 1 }, { "timber", 1 } },
    cover = { { "rock_low", 5 }, { "barrier", 1 } },
    tall = { "rock_tall" },
    overhead = {},
    wall = { "rock_wall_foot" },
    clutter = { "Oil Drum", "Metal Debris 1" },
    signs = { { "mtl_danger01.tga", 2, 1 } },
    grime = { { "rck_grassy01_A.tga", 4, 4, 255 }, { "moss02_A.tga", 3, 3, 255 } },
    streaks = { { "vertdirt02_A.tga", 4, 6, 120 } },
    fixtures = {},
    light_color = { 255, 236, 205 },
    outdoor = true,
  },
}

local ALIASES = {
  yard = "industrial", industrial_yard = "industrial", site = "construction", construction_site = "construction",
  warehouse = "storage", control_room = "control", tech = "control", mars_base = "mine", cave = "mine",
  rock = "canyon", outdoor = "canyon", outdoor_canyon = "canyon",
}

local THEME_NAMES = { "industrial", "construction", "storage", "control", "mine", "canyon" }

local function theme_of(api, name)
  if name == nil then return THEMES.industrial, "industrial" end
  if type(name) ~= "string" then K.fail(api, "'theme' must be a theme name.") end
  local key = ALIASES[name] or name
  local th = THEMES[key]
  if not th then
    K.fail(api, "unknown theme '" .. name .. "'.", "Themes: " .. table.concat(THEME_NAMES, ", ") ..
      " (aliases: yard, site, warehouse, control_room, tech, mars_base, cave, rock, outdoor).")
  end
  return th, key
end

-- The stock clutter classes the loaded game tables have (nil when no tables are loaded).
local clutter_cache
local function clutter_classes()
  if not catalog.available then return nil end
  if clutter_cache == nil then
    clutter_cache = {}
    for _, c in ipairs(catalog.classes("Clutter")) do clutter_cache[c] = true end
  end
  return clutter_cache
end

-- ---------------------------------------------------------------------------------------------
-- Prop recipes. A recipe returns parts in a local frame: u along the wall (centred), v away from the
-- wall surface (>= 0; free-standing props are centred on v too), y up from the floor. Part shapes:
-- "box", "cyl" (vertical cylinder), "pipe_u" (cylinder along u). tex = a theme texture key;
-- fit = fit each face to one tile (crates, panels).
-- ---------------------------------------------------------------------------------------------

local function box(u0, u1, v0, v1, y0, y1, tex, part, fit)
  return { shape = "box", u0 = u0, u1 = u1, v0 = v0, v1 = v1, y0 = y0, y1 = y1, tex = tex, part = part, fit = fit }
end
local function cyl(u, v, d, y0, y1, tex, part)
  return { shape = "cyl", u0 = u - d / 2, u1 = u + d / 2, v0 = v - d / 2, v1 = v + d / 2, y0 = y0, y1 = y1, tex = tex, part = part }
end
local function pipe_u(u0, u1, v, d, y, tex, part)
  return { shape = "pipe_u", u0 = u0, u1 = u1, v0 = v - d / 2, v1 = v + d / 2, y0 = y - d / 2, y1 = y + d / 2, tex = tex, part = part }
end

-- Each recipe: function(st, ctx) -> parts, or nil when it does not fit ctx (ctx.head = clear height
-- over the floor, ctx.tall = a tall piece is allowed). Wall recipes start at v = 0.
local R = {}

function R.crates(st, ctx)
  local s = st.pick({ 1.0, 1.2, 1.2 })
  local s2 = st.pick({ 0.8, 1.0 })
  local p = { box(-s, 0, 0, s, 0, s, "crate", "crate", true), box(0.05, 0.05 + s2, 0, s2, 0, s2, "crate", "crate", true) }
  if st.next() < 0.4 then p[#p + 1] = box(s + 0.15, s + 0.15 + s2, 0.1, 0.1 + s2, 0, s2, "crate", "crate", true) end
  return p
end
function R.crate_block(st, ctx)
  local h = st.pick({ 1.2, 1.3, 1.4 })
  return { box(-1.3, 0, 0, 1.3, 0, h, "crate", "crate", true), box(0.05, 1.35, 0, 1.3, 0, h, "crate", "crate", true),
    box(1.4, 2.3, 0.1, 1.0, 0, 0.9, "crate", "crate", true) }
end
function R.pipe_bundle(st, ctx)
  local len = st.pick({ 4, 5, 6 })
  return { pipe_u(-len / 2, len / 2, 0.35, 0.6, 0.3, "pipe", "pipe"), pipe_u(-len / 2, len / 2, 1.0, 0.6, 0.3, "pipe", "pipe"),
    pipe_u(-len / 2 + 0.4, len / 2 - 0.4, 0.68, 0.55, 0.85, "pipe", "pipe") }
end
function R.generator(st, ctx)
  return { box(-1.3, 1.3, 0, 1.4, 0, 1.3, "machine", "machine", true), box(-1.1, -0.3, 0.2, 1.2, 1.3, 1.45, "hazard", "trim"),
    cyl(0.8, 0.7, 0.5, 1.3, 1.45, "pipe", "vent") }
end
function R.crate_pair(st, ctx)
  return { box(-1.25, -0.05, -0.6, 0.6, 0, 1.2, "crate", "crate", true), box(0.05, 1.05, -0.45, 0.55, 0, 1.0, "crate", "crate", true) }
end
function R.crate_tower(st, ctx)
  if ctx.head < 3.2 then return nil end
  return { box(-1.2, 0, 0, 1.2, 0, 1.2, "crate", "crate", true), box(0, 1.2, 0, 1.2, 0, 1.2, "crate", "crate", true),
    box(-0.6, 0.6, 0, 1.2, 1.2, 2.4, "crate", "crate", true) }
end
function R.barrels(st, ctx)
  local n = st.int(2, 4)
  local spots = { { -0.4, 0.4 }, { 0.4, 0.4 }, { 0.0, 1.1 }, { 0.85, 1.15 } }
  local p = {}
  for i = 1, n do p[i] = cyl(spots[i][1], spots[i][2], 0.7, 0, 1.0, "barrel", "barrel") end
  return p
end
function R.pallet(st, ctx)
  local s = st.pick({ 0.8, 0.9 })
  return { box(-0.65, 0.65, 0, 1.3, 0, 0.15, "metal", "pallet"), box(-s / 2, s / 2, 0.2, 0.2 + s, 0.15, 0.15 + s, "crate", "crate", true) }
end
function R.pipe_run(st, ctx)
  local len = st.pick({ 3, 4, 5 })
  local p = { pipe_u(-len / 2, len / 2, 0.3, 0.45, 0.45, "pipe", "pipe") }
  if ctx.head >= 3 then p[#p + 1] = cyl(-len / 2 - 0.25, 0.3, 0.35, 0, ctx.head, "pipe", "riser") end
  return p
end
function R.machine(st, ctx)
  local p = { box(-1.0, 1.0, 0, 1.2, 0, 1.0, "machine", "machine", true), box(-0.9, 0.9, 0.05, 0.25, 1.0, 1.4, "hazard", "trim") }
  return p
end
function R.machine_tall(st, ctx)
  if ctx.head < 3 then return nil end
  return { box(-1.0, 1.0, 0, 1.2, 0, 1.0, "machine", "machine", true), box(-0.5, 0.5, 0, 0.7, 1.0, 2.2, "machine", "machine", true) }
end
function R.girders(st, ctx)
  return { box(-2, 2, 0.0, 0.35, 0, 0.35, "metal", "girder"), box(-2, 2, 0.45, 0.8, 0, 0.35, "metal", "girder"),
    box(-1.8, 2.2, 0.2, 0.55, 0.35, 0.7, "metal", "girder") }
end
function R.girders_low(st, ctx)
  return { box(-2, 2, -0.4, -0.05, 0, 0.35, "metal", "girder"), box(-2, 2, 0.05, 0.4, 0, 0.35, "metal", "girder"),
    box(-1.8, 2.2, -0.2, 0.15, 0.35, 0.7, "metal", "girder") }
end
function R.scaffold(st, ctx)
  if ctx.head < 4 then return nil end
  local h = min(3.2, ctx.head - 0.6)
  local deck = h - 0.55
  local p = {}
  for _, c in ipairs({ { -1.3, 0.1 }, { 1.3, 0.1 }, { -1.3, 1.3 }, { 1.3, 1.3 } }) do
    p[#p + 1] = box(c[1] - 0.06, c[1] + 0.06, c[2] - 0.06, c[2] + 0.06, 0, h, "metal", "post")
  end
  p[#p + 1] = box(-1.4, 1.4, 0.0, 1.4, deck, deck + 0.12, "metal", "deck")
  p[#p + 1] = box(-1.4, 1.4, 1.34, 1.4, deck + 0.12, h, "metal", "rail")
  return p
end
function R.scaffold_wall(st, ctx) return R.scaffold(st, ctx) end
function R.rack(st, ctx)
  if ctx.head < 3.6 then return nil end
  local p = { box(-1.3, -1.2, 0, 0.8, 0, 3.0, "metal", "post"), box(1.2, 1.3, 0, 0.8, 0, 3.0, "metal", "post") }
  for _, y in ipairs({ 0.0, 1.2, 2.4 }) do p[#p + 1] = box(-1.2, 1.2, 0, 0.8, y, y + 0.08, "metal", "shelf") end
  p[#p + 1] = box(-1.0, -0.1, 0.05, 0.75, 0.08, 0.9, "crate", "crate", true)
  p[#p + 1] = box(0.1, 1.0, 0.05, 0.75, 1.28, 2.0, "crate", "crate", true)
  return p
end
function R.console(st, ctx)
  return { box(-1.0, 1.0, 0, 0.8, 0, 0.85, "console", "console", true), box(-1.0, 1.0, 0, 0.25, 0.85, 1.45, "machine", "screen", true) }
end
function R.cabinet(st, ctx)
  return { box(-0.5, 0.5, 0, 0.6, 0, 1.4, "machine", "cabinet", true) }
end
function R.server_rack(st, ctx)
  if ctx.head < 3 then return nil end
  return { box(-0.6, 0.6, 0, 0.8, 0, 2.2, "machine", "rack", true) }
end
function R.console_island(st, ctx)
  return { box(-1.2, 1.2, -0.45, 0.45, 0, 0.85, "console", "console", true), box(-1.2, 1.2, -0.12, 0.12, 0.85, 1.3, "machine", "screen", true) }
end
function R.partition(st, ctx)
  return { box(-1.25, 1.25, -0.12, 0.12, 0, 1.35, "concrete", "partition") }
end
function R.partition_tall(st, ctx)
  if ctx.head < 3 then return nil end
  return { box(-1.0, 1.0, -0.12, 0.12, 0, 2.2, "concrete", "partition") }
end
function R.barrier(st, ctx)
  -- A jersey barrier: a concrete foot and cap with a hazard-striped band between them.
  return { box(-1.2, 1.2, -0.35, 0.35, 0, 0.7, "concrete", "barrier"), box(-1.2, 1.2, -0.3, 0.3, 0.7, 0.9, "hazard", "trim"),
    box(-1.2, 1.2, -0.15, 0.15, 0.9, 1.25, "concrete", "barrier") }
end
function R.crate_row(st, ctx)
  return { box(-1.2, 0, -0.6, 0.6, 0, 1.2, "crate", "crate", true), box(0, 1.2, -0.6, 0.6, 0, 1.2, "crate", "crate", true) }
end
function R.low_wall(st, ctx)
  return { box(-1.5, 1.5, -0.25, 0.25, 0, 1.35, "concrete", "wall"), box(-1.5, 1.5, -0.28, 0.28, 1.35, 1.45, "hazard", "trim") }
end
function R.timber(st, ctx)
  local h = min(ctx.head, 4.5)
  return { box(-0.18, 0.18, 0.0, 0.36, 0, h, "wood", "post"), box(-1.2, 1.2, 0.0, 0.36, h - 0.36, h, "wood", "header"),
    cyl(0.9, 0.55, 0.7, 0, 1.0, "barrel", "barrel") }
end
function R.timber_post(st, ctx)
  local h = min(ctx.head, 4.5)
  return { box(-0.18, 0.18, 0.0, 0.3, 0, h, "wood", "post") }
end
-- Rocks: cylinders roughened by geo.jitter after they are made (jitter = fraction of the size).
function R.rocks(st, ctx)
  local d1, d2 = st.range(1.2, 1.8), st.range(0.8, 1.2)
  local p = { cyl(-0.4, d1 / 2, d1, 0, st.range(0.9, 1.3), "rock", "rock") }
  p[1].rough = 0.18
  p[2] = cyl(0.55 + d2 / 2, d2 / 2 + 0.2, d2, 0, st.range(0.6, 0.9), "rock", "rock")
  p[2].rough = 0.18
  return p
end
function R.rock_low(st, ctx)
  local p = { cyl(-0.7, 0, 1.5, 0, 1.3, "rock", "rock"), cyl(0.7, 0.1, 1.3, 0, 1.2, "rock", "rock") }
  p[1].rough, p[2].rough = 0.15, 0.15
  return p
end
function R.rock_tall(st, ctx)
  if ctx.head < 3.2 then return nil end
  local p = { cyl(0, 0, 1.6, 0, 2.4, "rock", "rock") }
  p[1].rough = 0.15
  return p
end
function R.rock_wall_foot(st, ctx)
  local p = { cyl(0, 0.5, 1.0, 0, 0.8, "rock", "rock") }
  p[1].rough = 0.2
  return p
end
-- Small pieces that join a prop to make a group (stock clutter meshes take their place when the tables have them).
function R.crate_single(st, ctx)
  local s = st.pick({ 0.8, 0.9, 1.0, 1.1 })
  local p = { box(-s / 2, s / 2, 0, s, 0, s, "crate", "crate", true) }
  if st.next() < 0.45 then
    local t = st.pick({ 0.6, 0.7 })
    local du = st.range(-0.1, 0.1)
    p[2] = box(du - t / 2, du + t / 2, 0.1, 0.1 + t, s, s + t, "crate", "crate", true)
  end
  return p
end
function R.barrel_pair(st, ctx)
  local p = { cyl(-0.38, 0.38, 0.7, 0, 1.0, "barrel", "barrel") }
  if st.next() < 0.6 then p[2] = cyl(0.38, 0.42, 0.7, 0, st.pick({ 1.0, 0.9 }), "barrel", "barrel") end
  return p
end
function R.rock_small(st, ctx)
  local d = st.range(0.6, 0.95)
  local p = { cyl(0, d / 2, d, 0, st.range(0.4, 0.75), "rock", "rock") }
  p[1].rough = 0.2
  return p
end
local SATELLITES = { "crate_single", "barrel_pair", "crate_single" }

-- Theme fit (the intent layer): rocks, boulders and rubble belong in organic spaces (caves, canyons, outdoor ground,
-- rock-walled yards) and against walls and edges; in a built interior or walled yard a theme's rock pieces become its built
-- counterparts, and nowhere does a rock stand alone on open floor unless it is cover breaking a lane.
local ROCK_SWAP = { rocks = "crates", rock_low = "crate_row", rock_tall = "crate_tower", rock_wall_foot = "crate_single", rock_small = "crate_single" }
local function fit_recipe(sp, recipe)
  if sp and sp.built and ROCK_SWAP[recipe] then return ROCK_SWAP[recipe] end
  return recipe
end

-- Wall-mounted elements (nothing on the floor).
function R.wall_pipe(st, ctx)
  if ctx.head < 3 then return nil end
  return { cyl(0, 0.2, 0.3, 0, ctx.head, "pipe", "riser") }
end
function R.wall_panel(st, ctx)
  if ctx.head < 3 then return nil end
  return { box(-0.8, 0.8, 0, 0.12, 2.05, 3.0, "panel", "panel", true) }
end
function R.wall_screen(st, ctx)
  if ctx.head < 3 then return nil end
  return { box(-1.0, 1.0, 0, 0.1, 2.05, 2.9, "machine", "screen", true) }
end
function R.wall_vent(st, ctx)
  if ctx.head < 3.4 then return nil end
  return { box(-0.6, 0.6, 0, 0.15, 2.4, 3.0, "panel", "vent", true) }
end

local OVERHEAD_TEX = { beam = "metal", pipe_overhead = "pipe", cable_tray = "metal", timber_beam = "wood" }

-- ---------------------------------------------------------------------------------------------
-- Placement helpers.
-- ---------------------------------------------------------------------------------------------

-- Heading h (1..4): the v axis (away from the wall) and the u axis (v turned +90 degrees).
local HEAD = { { 1, 0 }, { 0, 1 }, { -1, 0 }, { 0, -1 } }

-- World AABB of a local part at anchor (ax, ay, az) with v axis (vx, vz).
local function world_box(p, ax, ay, az, vx, vz)
  local ux, uz = -vz, vx
  local xa = ax + p.u0 * ux + p.v0 * vx
  local xb = ax + p.u1 * ux + p.v1 * vx
  local za = az + p.u0 * uz + p.v0 * vz
  local zb = az + p.u1 * uz + p.v1 * vz
  return min(xa, xb), ay + p.y0, min(za, zb), max(xa, xb), ay + p.y1, max(za, zb)
end

local function col_range(a, b)
  return ceil(a / PITCH - 0.5), floor(b / PITCH - 0.5)
end

-- The textures a part gets from the theme.
local function tex_for(th, st, key)
  local list = th.tex[key] or th.tex.metal
  return list[st.int(1, #list)]
end

-- ---------------------------------------------------------------------------------------------
-- A space being dressed: its lattice, keep-outs, occupancy, anchors and budget.
-- ---------------------------------------------------------------------------------------------

local Space = {}

-- A space being dressed: the engine holds its lattice, keep-outs, occupancy, anchors and walk budget
-- (and checks every prop against them); the tables here mirror flat / keep-out / occupancy for the
-- placement policies below.
local function new_space(probe, s, keep, budget)
  local self = P.instance(Space)
  self.s, self.probe = s, probe
  local L = probe:space_lattice(s)
  self.L = L
  -- Objects that keep floor clear: spawns and the player start 1.6 m, items 1.2 m, triggers, events,
  -- nav points 1 m, clutter 0.8 m.
  local objs = level.objects_in{ min = { s.x0 - 2, s.y0 - 1, s.z0 - 2 }, max = { s.x1 + 2, s.y1 + 1, s.z1 + 2 } }
  local list = {}
  for _, o in ipairs(objs.objects) do
    local kind = o.kind
    if kind == "MpRespawnPoint" or kind == "PlayerStart" or kind == "Item" or kind == "Trigger" or kind == "Event"
      or kind == "NavPoint" or kind == "Entity" or kind == "Clutter" then
      local p = o.pos
      list[#list + 1] = { kind, p.x, p.y, p.z }
    end
  end
  local kl = {}
  for _, kb in ipairs(keep or {}) do
    if kb.r then kl[#kl + 1] = { x = kb.x, y = kb.y, z = kb.z, r = kb.r }
    else kl[#kl + 1] = { x0 = kb.x0, y0 = kb.y0, z0 = kb.z0, x1 = kb.x1, y1 = kb.y1, z1 = kb.z1 } end
  end
  local h = L.h:dress_space{ space = { x0 = s.x0, y0 = s.y0, z0 = s.z0, x1 = s.x1, y1 = s.y1, z1 = s.z1 },
    keep = kl, objects = list, budget = budget }
  self.h = h
  -- Flat nodes: no neighbouring floor within a foot step at another height (a wall or a drop beside a
  -- node is fine; a step, ramp or slope is not).
  self.flat = h:flat()
  self.keepout = h:keepout()
  self.doors = h:doors()
  self.occupied = {}
  self.area_before = h.area_before
  return self
end

-- Tries a prop: parts (local) at anchor (ax, az) on floor y, v axis (vx, vz), wall = true when the
-- v = 0 side is against a wall. Returns the world parts and the engine's commit token, or nil.
function Space:try(parts, ax, ay, az, vx, vz, wall, pad)
  local r = self.h:try(parts, ax, ay, az, vx, vz, wall and true or false, pad or 0.3)
  if r.token == 0 then return nil end
  local world = {}
  for n, b in ipairs(r.world) do
    world[n] = { p = parts[n], x0 = b[1], y0 = b[2], z0 = b[3], x1 = b[4], y1 = b[5], z1 = b[6], base = b[7], vx = vx, vz = vz, wall = wall }
  end
  return world, r.token
end

-- Commits a prop accepted by try: occupancy (+ 0.45 m), wide set, budget.
function Space:commit(world, token)
  local occ = self.occupied
  for _, id in ipairs(self.h:commit(token)) do occ[id] = true end
end

-- ---------------------------------------------------------------------------------------------
-- Emitting.
-- ---------------------------------------------------------------------------------------------

-- The role tags of every element kit.dress places (kit.role, kit.purpose, kit.theme, kit.space), given at creation.
local function role_tags(ctx, role, purpose, sp, base)
  local t = base or {}
  purpose = purpose or (ctx.theme.label .. " detail")
  if #purpose > 250 then purpose = purpose:sub(1, 247) .. "..." end
  t["kit.role"] = role or "theme_detail"
  t["kit.purpose"] = purpose
  t["kit.theme"] = ctx.theme_key
  if sp and sp.s and sp.s.name then t["kit.space"] = sp.s.name end
  return t
end

local function emit_part(ctx, w, sp, role, purpose)
  local p = w.p
  local th, st = ctx.theme, ctx.st
  if p.shape == "clutter" then
    -- A stock clutter mesh standing in its footprint: the mesh's -Z side toward the v = 0 side (the wall), turned by
    -- the part's jitter, its own x/z bounds centred on the footprint and its base on the floor.
    local e = CLUTTER[p.cls]
    local yaw = math.deg(math.atan2(w.vx or 0, w.vz or 1)) + (p.jitter or 0)
    local r = math.rad(yaw)
    local mx, mz = (e[1] + e[2]) / 2, (e[5] + e[6]) / 2
    local ox = mx * math.cos(r) + mz * math.sin(r)
    local oz = -mx * math.sin(r) + mz * math.cos(r)
    local cx, cz = (w.x0 + w.x1) / 2, (w.z0 + w.z1) / 2
    local o = level.place{ kind = "clutter", class = p.cls, pos = { cx - ox, w.y0 - e[3], cz - oz }, yaw = yaw,
      name = ctx.name .. "_clutter" .. (ctx.count.clutter + 1), tags = role_tags(ctx, role, purpose, sp, K.tags_for("dress", ctx.name)) }
    o:tag("kit.part", "clutter")
    ctx.objects[#ctx.objects + 1] = o
    if sp then sp.probe:add_block(w.x0, w.y0, w.z0, w.x1, w.y1, w.z1) end
    ctx.count.clutter = ctx.count.clutter + 1
    return o
  end
  local tex = tex_for(th, st, p.tex)
  local cx, cy, cz = (w.x0 + w.x1) / 2, (w.y0 + w.y1) / 2, (w.z0 + w.z1) / 2
  local sx, sy, sz = w.x1 - w.x0, w.y1 - w.y0, w.z1 - w.z0
  local spec = nil
  if p.shape == "box" then
    spec = { shape = "box", pos = { cx, cy, cz }, size = { sx, sy, sz }, detail = true }
  elseif p.shape == "cyl" then
    spec = { shape = "cylinder", pos = { cx, cy, cz }, size = { sx, sy, sz }, sides = p.rough and 8 or 10, detail = true }
  else -- pipe_u: a cylinder lying along the longer horizontal extent
    if sx >= sz then
      spec = { shape = "cylinder", pos = { cx, cy, cz }, size = { sy, sx, sz }, sides = 10, detail = true, rot = { roll = 90 } }
    else
      spec = { shape = "cylinder", pos = { cx, cy, cz }, size = { sx, sz, sy }, sides = 10, detail = true, rot = { pitch = 90 } }
    end
  end
  spec.extra_tags = role_tags(ctx, role, purpose, sp)
  local b = K.emit("dress", ctx.name, p.part, spec, tex, nil)
  if p.rough then
    local q = geo.jitter{ brushes = { b }, amount = p.rough * min(sx, sz), seed = ctx.st.int(1, 1000000) }
    local nb = q.brushes and q.brushes[1]
    if nb then b = nb end
    b:box_map{ tile = KIT.defaults.tile_metres }
  elseif p.fit then
    for _, f in ipairs(b.faces) do f:fit() end
  end
  ctx.brushes[#ctx.brushes + 1] = b
  ctx.count.brushes = ctx.count.brushes + 1
  return b
end

-- Role of each placement kind: cover breaks a measured lane; everything else is the zone's theme detail.
local ROLE_OF = { cover = "cover", props = "theme_detail", flanks = "theme_detail" }

local function emit_prop(ctx, sp, world, cover_list, gone, lost, role, purpose)
  sp:commit(world, cover_list, gone, lost)
  local r = ROLE_OF[role or ""] or "theme_detail"
  if not purpose then
    local where = sp.s and sp.s.name or "the space"
    purpose = role == "flanks" and (ctx.theme.label .. " props framing a doorway of " .. where)
      or (ctx.theme.label .. " dressing of " .. where .. " (" .. (sp.context or "space") .. ")" .. ((world[1] and world[1].wall) and ", against the wall" or ""))
  end
  for _, w in ipairs(world) do
    emit_part(ctx, w, sp, r, purpose)
    ctx.count.elements = ctx.count.elements + 1
    local c = sp:cell_of((w.x0 + w.x1) / 2, (w.z0 + w.z1) / 2)
    sp.cell_count[c] = (sp.cell_count[c] or 0) + 1
  end
  if role then ctx.count[role] = (ctx.count[role] or 0) + 1 end
  if #world > 1 then ctx.count.groups = ctx.count.groups + 1 end
end

-- ---------------------------------------------------------------------------------------------
-- Groups. A prop reads as placed on purpose when it is part of a group: a crate stack with a barrel pair
-- beside it, drums by a generator, a barrier with crates at its end. compose() grows a prop's parts with
-- one or two satellites (stock clutter meshes when the tables have them, else small crate / barrel
-- pieces) abutting it along u, 0.1-0.2 m apart; the whole group is fitted (Space:try) as one prop.
-- ---------------------------------------------------------------------------------------------

-- The theme's stock clutter classes small enough to stand beside a prop (tables loaded, clutter on).
local function small_clutter(ctx)
  if ctx.small_clutter == nil then
    local out = {}
    local classes = clutter_classes()
    if classes and ctx.clutter_on then
      for _, c in ipairs(ctx.theme.clutter) do
        local e = CLUTTER[c]
        if classes[c] and e and e[2] - e[1] <= 1.6 and e[6] - e[5] <= 1.6 and e[4] - e[3] <= 1.6 then out[#out + 1] = c end
      end
    end
    ctx.small_clutter = out
  end
  return ctx.small_clutter
end

-- A clutter part: the class's footprint turned by `jitter` degrees, its back at v0.
local function clutter_part(cls, jitter, v0)
  local e = CLUTTER[cls]
  local wd, dp, ht = e[2] - e[1], e[6] - e[5], e[4] - e[3]
  local r = math.rad(jitter)
  local fw = wd * abs(math.cos(r)) + dp * abs(math.sin(r))
  local fd = wd * abs(math.sin(r)) + dp * abs(math.cos(r))
  return { shape = "clutter", cls = cls, jitter = jitter, u0 = -fw / 2, u1 = fw / 2, v0 = v0, v1 = v0 + fd, y0 = 0, y1 = ht, part = "clutter" }
end

local function u_range(parts)
  local lo, hi = math.huge, -math.huge
  for _, p in ipairs(parts) do lo, hi = min(lo, p.u0), max(hi, p.u1) end
  return lo, hi
end

local function shift_u(parts, du)
  for _, p in ipairs(parts) do p.u0, p.u1 = p.u0 + du, p.u1 + du end
end

-- One satellite: a small clutter pair (turned a little each, as if set down by hand) or a crate / barrel piece.
local function satellite(ctx, st, v0)
  local sc = small_clutter(ctx)
  if #sc > 0 and not (ctx.theme.outdoor and ctx.organic) and st.next() < 0.6 then
    local cls = sc[st.int(1, #sc)]
    local e = CLUTTER[cls]
    local n = (e[2] - e[1] <= 0.9) and st.int(1, 2) or 1
    local parts, hi = {}, nil
    for _ = 1, n do
      local q = clutter_part(cls, st.range(-25, 25), v0 + 0.05)
      if hi then shift_u({ q }, hi + 0.08 - q.u0) end
      hi = q.u1
      parts[#parts + 1] = q
    end
    return parts
  end
  -- Outdoors (rocks in a canyon) a group grows smaller rocks, not crates.
  local parts = R[(ctx.theme.outdoor and ctx.organic) and "rock_small" or SATELLITES[st.int(1, #SATELLITES)]](st, { head = 9, tall = false })
  if v0 ~= 0 then for _, p in ipairs(parts) do p.v0, p.v1 = p.v0 + v0, p.v1 + v0 end end
  return parts
end

-- The prop's parts plus n satellites, alternating sides.
local function compose(ctx, st, parts, n)
  local out = {}
  for i, p in ipairs(parts) do out[i] = p end
  local lo, hi = u_range(parts)
  local vmin = math.huge
  for _, p in ipairs(parts) do vmin = min(vmin, p.v0) end
  local side = st.next() < 0.5 and -1 or 1
  for _ = 1, n do
    local sat = satellite(ctx, st, vmin)
    local a, b = u_range(sat)
    local gap = st.range(0.1, 0.2)
    if side > 0 then
      shift_u(sat, hi + gap - a)
      hi = hi + gap + (b - a)
    else
      shift_u(sat, lo - gap - b)
      lo = lo - gap - (b - a)
    end
    for _, p in ipairs(sat) do out[#out + 1] = p end
    side = -side
  end
  return out
end

-- Fits a prop as a group (nsat satellites) when it can, else on its own (unless alone = false).
local function try_group(ctx, sp, parts, ax, ay, az, vx, vz, wall, pad, nsat, alone)
  local st = ctx.st
  if nsat > 0 then
    local world, tok = sp:try(compose(ctx, st, parts, nsat), ax, ay, az, vx, vz, wall, pad)
    if world then return world, tok end
  end
  if alone == false then return nil end
  return sp:try(parts, ax, ay, az, vx, vz, wall, pad)
end

-- ---------------------------------------------------------------------------------------------
-- kit.dress
-- ---------------------------------------------------------------------------------------------

function Space:cell_of(x, z)
  local s = self.s
  local a = min(self.ncx - 1, max(0, floor((x - s.x0) / self.cw)))
  local b = min(self.ncz - 1, max(0, floor((z - s.z0) / self.cd)))
  return a * 1000 + b
end

-- Existing dressing in the space (the study's classes: beams, slabs, small solids, detail brushes,
-- clutter, decals) so a second call is not over-filled. Light fixtures (kit.light_room) are left
-- out: the target below is the dressing on top of a lit space's fixtures.
local function existing_dressing(probe, s)
  local n = probe:dressing_in(s.x0, s.y0, s.z0, s.x1, s.y1, s.z1)
  local objs = level.objects_in{ min = { s.x0, s.y0 - 0.5, s.z0 }, max = { s.x1, s.y1 + 0.5, s.z1 } }
  for _, o in ipairs(objs.objects) do
    if (o.kind == "Clutter" or o.kind == "Decal") and o:tags().kit ~= "light_room" then n = n + 1 end
  end
  return n
end

-- Wall-adjacent floor spots of a space: {id, vx, vz, wall distance} for nodes with a wall within
-- 0.75 m along one axis (the v axis points away from that wall).
local function wall_spots(sp)
  local w = sp.h:wall_spots()
  local ids, hs, ds = w.id, w.h, w.dist
  local out = {}
  for q = 1, #ids do out[q] = { id = ids[q], h = hs[q], dist = ds[q] } end
  return out
end

-- True when a wall spot stands in a corner: another wall within 1.5 m along the wall's own direction.
local function corner_spot(sp, s)
  local n = sp.L.nodes[s.id]
  local d = HEAD[s.h]
  local ux, uz = -d[2], d[1]
  local y = n.y + 1.0
  return sp.probe:wall_distance(n.x, y, n.z, ux, uz, 1.6) < 1.5 or sp.probe:wall_distance(n.x, y, n.z, -ux, -uz, 1.6) < 1.5
end

-- The wall spots of a cell (or every spot) in the space's shuffled order, corners first (props gather in
-- corners and along walls before they spread over a wall's middle).
local function spots_for(ctx, sp, spots, cell)
  if not sp.spot_cells then
    -- Shuffle once (Fisher-Yates on the stream), corners to the front (stable), then bucket by cell.
    local list = {}
    for i = 1, #spots do list[i] = spots[i] end
    for i = #list, 2, -1 do
      local j = ctx.st.int(1, i)
      list[i], list[j] = list[j], list[i]
    end
    local corners, rest = {}, {}
    for _, s in ipairs(list) do
      if corner_spot(sp, s) then corners[#corners + 1] = s else rest[#rest + 1] = s end
    end
    list = corners
    for _, s in ipairs(rest) do list[#list + 1] = s end
    sp.spot_all = list
    sp.spot_cells = {}
    local L = sp.L
    for i = 1, #list do
      local n = L.nodes[list[i].id]
      local c = sp:cell_of(n.x, n.z)
      local b = sp.spot_cells[c]
      if not b then b = {} sp.spot_cells[c] = b end
      b[#b + 1] = list[i]
    end
  end
  return cell and (sp.spot_cells[cell] or {}) or sp.spot_all
end

-- Tries recipe at one wall spot, as a group of nsat satellites when it fits; true when placed.
local function try_wall_spot(ctx, sp, s, recipe, nsat, role, purpose)
  local node = sp.L.nodes[s.id]
  local d = HEAD[s.h]
  local ax = node.x - d[1] * s.dist + d[1] * 0.05
  local az = node.z - d[2] * s.dist + d[2] * 0.05
  local parts = R[fit_recipe(sp, recipe)](ctx.st, { head = node.top - node.y, tall = true })
  if not parts then return false end
  local world, tok = try_group(ctx, sp, parts, ax, node.y, az, d[1], d[2], true, 0.3, nsat)
  if not world then return false end
  emit_prop(ctx, sp, world, tok, nil, nil, role or "props", purpose)
  return true
end

-- Places one wall prop from recipe list near cell `cell` (or anywhere when nil); most become groups.
local function place_wall_prop(ctx, sp, spots, recipe, cell)
  local st = ctx.st
  local list = spots_for(ctx, sp, spots, cell)
  local n = #list
  if n == 0 then return false end
  -- Corner spots first: start inside the corner run when the cell has one.
  local start = st.int(1, n)
  if corner_spot(sp, list[1]) then start = 0 end
  local nsat = (st.next() < 0.7) and st.int(1, 2) or 0
  if recipe:sub(1, 5) == "wall_" or recipe == "timber_post" or recipe == "rock_wall_foot" then nsat = 0 end
  for t = 0, min(n, 24) - 1 do
    if try_wall_spot(ctx, sp, list[((start + t) % n) + 1], recipe, nsat) then return true end
  end
  return false
end

-- A free-standing group on open floor in a cell (or anywhere): at least 2 m from walls, never a lone piece,
-- at most one per 120 m² of an indoor space (props live against walls; open floor gets cover, kept by
-- place_cover). Outdoor themes (rocks in a canyon) are not capped.
local function place_free_prop(ctx, sp, recipe, cell)
  local L, st = sp.L, ctx.st
  -- Rocks never stand alone on open floor (they go against walls and edges, or serve as cover on a lane).
  if ROCK_SWAP[recipe] then return false end
  if not (ctx.theme.outdoor and sp.organic) and (sp.free_count or 0) >= max(1, floor(sp.area_before / 120)) then return false end
  if not sp.free_cells then
    sp.free_cells, sp.free_all = {}, {}
    local list = {}
    local edge = P.near_edge(L, 2.0)
    for id = 1, #L.nodes do
      if sp.flat[id] and not edge[id] and not sp.keepout[id] then list[#list + 1] = id end
    end
    for i = #list, 2, -1 do
      local j = st.int(1, i)
      list[i], list[j] = list[j], list[i]
    end
    sp.free_all = list
    for i = 1, #list do
      local n = L.nodes[list[i]]
      local c = sp:cell_of(n.x, n.z)
      local b = sp.free_cells[c]
      if not b then b = {} sp.free_cells[c] = b end
      b[#b + 1] = list[i]
    end
  end
  local list = cell and (sp.free_cells[cell] or {}) or sp.free_all
  local n = #list
  if n == 0 then return false end
  local start = st.int(1, n)
  for t = 0, min(n, 12) - 1 do
    local node = L.nodes[list[((start + t) % n) + 1]]
    if not sp.occupied[node.id] then
      local parts = R[recipe](st, { head = node.top - node.y, tall = true })
      if parts then
        local h = HEAD[st.int(1, 4)]
        local world, tok = try_group(ctx, sp, parts, node.x, node.y, node.z, h[1], h[2], false, 0.6, st.int(1, 2), false)
        if world then
          emit_prop(ctx, sp, world, tok, nil, nil, "props", ctx.theme.label .. " group on the open floor of " .. (sp.s.name or "the space"))
          sp.free_count = (sp.free_count or 0) + 1
          return true
        end
      end
    end
  end
  return false
end

-- A stock clutter group (the tables have the class) against a wall near the spot list: one piece, often with
-- smaller pieces beside it (drums by a generator, a toolbox by the crates), each turned a little.
local function place_clutter(ctx, sp, spots, cell)
  local classes = clutter_classes()
  if not classes or not ctx.clutter_on then return false end
  local avail = {}
  for _, c in ipairs(ctx.theme.clutter) do if classes[c] and CLUTTER[c] then avail[#avail + 1] = c end end
  if #avail == 0 or #spots == 0 then return false end
  local st, L = ctx.st, sp.L
  local cls = avail[st.int(1, #avail)]
  local list = spots_for(ctx, sp, spots, cell)
  local n = #list
  if n == 0 then return false end
  local start = st.int(1, n)
  if corner_spot(sp, list[1]) then start = 0 end
  local nsat = (st.next() < 0.65) and st.int(1, 2) or 0
  for t = 0, min(n, 16) - 1 do
    local s = list[((start + t) % n) + 1]
    local node = L.nodes[s.id]
    local d = HEAD[s.h]
    local ax = node.x - d[1] * s.dist + d[1] * 0.08
    local az = node.z - d[2] * s.dist + d[2] * 0.08
    local world, tok = try_group(ctx, sp, { clutter_part(cls, st.range(-6, 6), 0) }, ax, node.y, az, d[1], d[2], true, 0.3, nsat)
    if world then
      emit_prop(ctx, sp, world, tok, nil, nil, nil, ctx.theme.label .. " clutter against a wall of " .. (sp.s.name or "the space"))
      return true
    end
  end
  return false
end

-- Two props flanking each doorway of the space (one each side, 2.8-4.8 m from the opening, against the wall):
-- the classic framing of a way through.
local function place_door_flanks(ctx, sp, spots)
  local doors = sp.doors
  if not doors or #doors == 0 or #spots == 0 then return end
  local L, st = sp.L, ctx.st
  local clusters = {}
  for _, id in ipairs(doors) do
    local n = L.nodes[id]
    if n then
      local found = nil
      for _, c in ipairs(clusters) do
        if (c.x - n.x) ^ 2 + (c.z - n.z) ^ 2 < 9 and abs(c.y - n.y) < 1 then found = c break end
      end
      if found then
        found.n = found.n + 1
        found.x, found.z = found.x + (n.x - found.x) / found.n, found.z + (n.z - found.z) / found.n
      else
        clusters[#clusters + 1] = { x = n.x, y = n.y, z = n.z, n = 1 }
      end
    end
  end
  local small = { "crate_single", "barrel_pair", "crates" }
  for _, c in ipairs(clusters) do
    local cand = {}
    for _, s in ipairs(spots) do
      local n = L.nodes[s.id]
      local d2 = (n.x - c.x) ^ 2 + (n.z - c.z) ^ 2
      if d2 >= 2.8 * 2.8 and d2 <= 4.8 * 4.8 and abs(n.y - c.y) < 0.5 then cand[#cand + 1] = { s = s, d2 = d2 } end
    end
    P.sort(cand, function(a, b) return a.d2 < b.d2 end)
    local first = nil
    for _, e in ipairs(cand) do
      local n = L.nodes[e.s.id]
      if not first or (n.x - first.x) ^ 2 + (n.z - first.z) ^ 2 >= 16 then
        if try_wall_spot(ctx, sp, e.s, small[st.int(1, #small)], st.int(0, 1), "flanks") then
          if first then break end
          first = { x = n.x, z = n.z }
        end
      end
    end
  end
end

-- Cover against measured threats (the intent layer). Each space's lanes are measured on the probe: from floor nodes on a
-- 2 m grid, the longest open chest-height line through the node (8 axes, both ways). Lanes of at least LANE_MIN m (the
-- grader's significant sightline, G29) are threats; so are the long lines into each spawn, flag or hill in the space
-- (their approaches). Longest first, each threat not already broken gets one cover cluster ON the lane, its long side
-- across it: lanes of BREAK_LANE m or more get a tall sightline breaker at their middle (it blocks the eye line; the
-- probe sees it at once), shorter lanes low cover a player can duck behind. Clusters stand at least 5 m apart (exemplar
-- cover every 5-6 m) and at most one per 45 m2. A space with no lane (a small room) gets no free-standing cover at all:
-- cover there would break nothing. Each piece is tagged with the lane it breaks.
local LANE_MIN = 14
local BREAK_LANE = 24
local AXES = {}
for k = 0, 7 do AXES[k + 1] = { math.cos(k * math.pi / 8), math.sin(k * math.pi / 8) } end

-- The open line through (x, y, z) along axis a (both ways): its length and the two ends.
local function lane_through(probe, x, y, z, a)
  local f = probe:ray(x, y + 1.2, z, a[1], a[2], 60)
  local b = probe:ray(x, y + 1.2, z, -a[1], -a[2], 60)
  return f + b, x - a[1] * b, z - a[2] * b, x + a[1] * f, z + a[2] * f
end

-- Distance from (px, pz) to the segment (ax, az)-(bx, bz).
local function seg_distance(px, pz, ax, az, bx, bz)
  local dx, dz = bx - ax, bz - az
  local l2 = dx * dx + dz * dz
  local t = l2 > 0 and math.max(0, math.min(1, ((px - ax) * dx + (pz - az) * dz) / l2)) or 0
  local qx, qz = ax + t * dx - px, az + t * dz - pz
  return math.sqrt(qx * qx + qz * qz)
end

local function place_cover(ctx, sp)
  local s, L, st, th, probe = sp.s, sp.L, ctx.st, ctx.theme, sp.probe
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  if min(w, d) < 6 then return 0 end
  local edge = P.near_edge(L, 1.5)
  local threats = {}
  for id, n in ipairs(L.nodes) do
    if n.i % 4 == 0 and n.k % 4 == 0 and sp.flat[id] and not sp.keepout[id] and not edge[id] then
      local best = nil
      for _, a in ipairs(AXES) do
        local len, ax, az, bx, bz = lane_through(probe, n.x, n.y, n.z, a)
        if len >= LANE_MIN and (best == nil or len > best.len + 1e-6) then
          best = { id = id, y = n.y, len = len, ax = ax, az = az, bx = bx, bz = bz, dx = a[1], dz = a[2], kind = "lane" }
        end
      end
      if best then threats[#threats + 1] = best end
    end
  end
  -- Approaches: the longest open line into each spawn, flag or hill marker in the space (its cover stands 3.5-5 m out).
  for _, o in ipairs(level.objects_in{ min = { s.x0, s.y0 - 0.5, s.z0 }, max = { s.x1, s.y1, s.z1 } }.objects) do
    local cls = string.lower(o.class_name or "")
    if o.kind == "MpRespawnPoint" or o.kind == "PlayerStart" or cls:find("flag", 1, true) or cls:find("capture", 1, true) then
      local p = o.pos
      local dx, dz, len = probe:longest_ray(p.x, p.y, p.z, 60)
      if len >= LANE_MIN then
        threats[#threats + 1] = { y = p.y, len = len + 100, ax = p.x, az = p.z, bx = p.x + dx * len, bz = p.z + dz * len, dx = dx, dz = dz,
          kind = "approach", at = 4.25, who = (o.kind == "MpRespawnPoint" and "spawn" or (o.kind == "PlayerStart" and "player start" or "objective")) }
      end
    end
  end
  P.sort(threats, function(a, b)
    if abs(a.len - b.len) > 1e-6 then return a.len > b.len end
    if abs(a.ax - b.ax) > 1e-6 then return a.ax < b.ax end
    return a.az < b.az
  end)
  local cap = max(1, floor(sp.area_before / 45))
  local placed = {}
  local inset = 2.0
  for _, t in ipairs(threats) do
    if #placed >= cap then break end
    local len = t.kind == "approach" and (t.len - 100) or t.len
    -- Already broken: a cluster stands on (within 2.5 m of) this line.
    local served = false
    for _, c in ipairs(placed) do
      if seg_distance(c.x, c.z, t.ax, t.az, t.bx, t.bz) <= 2.5 then served = true break end
    end
    if not served and t.kind == "lane" and t.id then
      -- A tall breaker placed since may have cut it: measure it again.
      local n = L.nodes[t.id]
      local l2 = lane_through(probe, n.x, n.y, n.z, { t.dx, t.dz })
      if l2 < LANE_MIN then served = true end
    end
    if not served then
      -- The spot: the lane's middle (an approach: 3.5-5 m out from the spawn), held inside the space.
      local f = t.kind == "approach" and (t.at / len) or 0.5
      local x, z = t.ax + (t.bx - t.ax) * f, t.az + (t.bz - t.az) * f
      x = max(s.x0 + inset, min(s.x1 - inset, x))
      z = max(s.z0 + inset, min(s.z1 - inset, z))
      local id = P.nearest(L, x, t.y, z, 1.5, 0.3)
      local ok = id ~= nil and sp.flat[id] and not sp.keepout[id] and not sp.occupied[id]
      if ok then
        local n = L.nodes[id]
        for _, c in ipairs(placed) do
          if (c.x - n.x) ^ 2 + (c.z - n.z) ^ 2 < 25 then ok = false break end
        end
        if ok then
          local tall = len >= BREAK_LANE and t.kind == "lane" and #th.tall > 0
          local recipe = fit_recipe(sp, tall and th.tall[st.int(1, #th.tall)] or st.weighted(th.cover))
          -- Rock cover only where rocks belong (organic spaces); a built space takes the theme's built cover.
          local purpose = string.format("%s the %.0f m %s (%.1f, %.1f) -> (%.1f, %.1f)", tall and "breaks" or "shelters along",
            len, t.kind == "approach" and ("approach to a " .. t.who) or "lane", t.ax, t.az, t.bx, t.bz)
          local parts = R[recipe](st, { head = n.top - n.y, tall = true })
          if not parts and tall then
            recipe = fit_recipe(sp, st.weighted(th.cover))
            parts = R[recipe](st, { head = n.top - n.y, tall = false })
          end
          if parts then
            -- v along the lane (snapped to the nearer axis: props stand on the lattice): the cluster's long side (u)
            -- stands across it, facing the threat.
            local vx, vz = 0, 0
            if abs(t.dx) >= abs(t.dz) then vx = t.dx >= 0 and 1 or -1 else vz = t.dz >= 0 and 1 or -1 end
            -- A tall breaker stands as an outcrop or stack with two smaller pieces at its foot (never a lone pillar).
            local nsat = tall and 2 or ((st.next() < 0.65) and 1 or 0)
            local world, tok = try_group(ctx, sp, parts, n.x, n.y, n.z, vx, vz, false, 0.6, nsat, not tall)
            if not world and tall then world, tok = try_group(ctx, sp, parts, n.x, n.y, n.z, vx, vz, false, 0.6, 1) end
            if world then
              emit_prop(ctx, sp, world, tok, nil, nil, "cover", purpose)
              placed[#placed + 1] = { x = n.x, z = n.z }
              if tall then
                ctx.count.breakers = ctx.count.breakers + 1
                for _, wb in ipairs(world) do probe:add_block(wb.x0, wb.y0, wb.z0, wb.x1, wb.y1, wb.z1) end
              end
              ctx.count.lanes = (ctx.count.lanes or 0) + 1
            end
          end
        end
      end
    end
  end
  return #placed
end

-- Wall-side cover for a room with no long lane (a small room): one low cover piece against the wall per 120 m2 (at
-- least one in a room of 60 m2 or more), at the wall spots nearest the line between its doorways — the way a player
-- crosses the room — so it is cover beside that route, not a lone prop in the middle. Stands against the wall, so it
-- never reads as free-standing cover (G29).
local function place_wall_cover(ctx, sp, spots)
  local L, st, th = sp.L, ctx.st, ctx.theme
  if sp.area_before < 60 or #spots == 0 or #th.cover == 0 then return 0 end
  -- The doorway clusters; the route is the line between the two farthest apart (one doorway: to the room's centre).
  local clusters = {}
  for _, id in ipairs(sp.doors or {}) do
    local n = L.nodes[id]
    if n then
      local found = nil
      for _, c in ipairs(clusters) do
        if (c.x - n.x) ^ 2 + (c.z - n.z) ^ 2 < 9 then found = c break end
      end
      if not found then clusters[#clusters + 1] = { x = n.x, z = n.z } end
    end
  end
  local s0 = sp.s
  local ax, az = (s0.x0 + s0.x1) / 2, (s0.z0 + s0.z1) / 2
  local bx, bz = ax, az
  if #clusters >= 2 then
    local bd = -1
    for i = 1, #clusters do
      for j = i + 1, #clusters do
        local d2 = (clusters[i].x - clusters[j].x) ^ 2 + (clusters[i].z - clusters[j].z) ^ 2
        if d2 > bd then bd, ax, az, bx, bz = d2, clusters[i].x, clusters[i].z, clusters[j].x, clusters[j].z end
      end
    end
  elseif #clusters == 1 then
    ax, az = clusters[1].x, clusters[1].z
  else
    return 0
  end
  local cand = {}
  for _, sspot in ipairs(spots) do
    local n = L.nodes[sspot.id]
    local d = seg_distance(n.x, n.z, ax, az, bx, bz)
    -- Beside the route (within 6 m of it) but clear of the doorways themselves (3 m).
    local da = math.min((n.x - ax) ^ 2 + (n.z - az) ^ 2, (n.x - bx) ^ 2 + (n.z - bz) ^ 2)
    if d <= 6 and da >= 9 and not sp.occupied[sspot.id] then cand[#cand + 1] = { s = sspot, d = d } end
  end
  P.sort(cand, function(a, b) return a.d < b.d end)
  local want = max(1, floor(sp.area_before / 120))
  local purpose = string.format("cover beside the doorway-to-doorway line (%.1f, %.1f) -> (%.1f, %.1f), against the wall", ax, az, bx, bz)
  local placed, used = 0, {}
  for _, c in ipairs(cand) do
    if placed >= want then break end
    local n = L.nodes[c.s.id]
    local near = false
    for _, u in ipairs(used) do if (u.x - n.x) ^ 2 + (u.z - n.z) ^ 2 < 25 then near = true break end end
    if not near and try_wall_spot(ctx, sp, c.s, st.weighted(th.cover), (st.next() < 0.5) and 1 or 0, "cover", purpose) then
      placed = placed + 1
      used[#used + 1] = { x = n.x, z = n.z }
    end
  end
  return placed
end

-- Overhead beams across the short axis, flush under the ceiling, clear of lights.
local function place_overhead(ctx, sp)
  local s, L, st, th, probe = sp.s, sp.L, ctx.st, ctx.theme, sp.probe
  if #th.overhead == 0 or s.sky then return end
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  local short_x = w < d
  local span = short_x and w or d
  local long = short_x and d or w
  if span > 24 or long < 4 then return end
  -- Ceiling: the lowest air top over the main floor nodes.
  local ceiling = math.huge
  local base = math.huge
  for id, n in ipairs(L.nodes) do
    if sp.flat[id] then ceiling = min(ceiling, n.top) base = min(base, n.y) end
  end
  if ceiling == math.huge or ceiling - base < 4.5 then return end
  local lights = level.objects_in{ min = { s.x0, s.y0, s.z0 }, max = { s.x1, s.y1 + 0.5, s.z1 } }
  local lpos = {}
  for _, o in ipairs(lights.objects) do
    if o.kind == "Light" or o.kind == "Clutter" then lpos[#lpos + 1] = o.pos end
  end
  local kind = th.overhead[st.int(1, #th.overhead)]
  local tex = OVERHEAD_TEX[kind] or "metal"
  local count = max(1, floor(long / 5))
  local step = long / (count + 1)
  local placed = 0
  for c = 1, count do
    local along = (short_x and s.z0 or s.x0) + c * step
    local y1 = ceiling - 0.01
    local depth = (kind == "pipe_overhead") and 0.4 or 0.45
    local thick = (kind == "pipe_overhead") and 0.4 or 0.35
    local y0 = y1 - depth
    local x0, x1, z0, z1 = nil, nil, nil, nil
    if short_x then x0, x1, z0, z1 = s.x0, s.x1, along - thick / 2, along + thick / 2
    else x0, x1, z0, z1 = along - thick / 2, along + thick / 2, s.z0, s.z1 end
    -- Clear of every light and fixture by 0.9 m, and in open air along its whole length.
    local ok = true
    for _, p in ipairs(lpos) do
      if p.x > x0 - 0.9 and p.x < x1 + 0.9 and p.z > z0 - 0.9 and p.z < z1 + 0.9 and p.y > y0 - 1.5 then ok = false end
    end
    if ok then
      local n = short_x and floor(w / 0.5) or floor(d / 0.5)
      for q = 1, n - 1 do
        local px = short_x and (s.x0 + q * 0.5) or along
        local pz = short_x and along or (s.z0 + q * 0.5)
        if not probe:air_exact(px, (y0 + y1) / 2, pz) then ok = false break end
      end
    end
    if ok then
      local part = { shape = (kind == "pipe_overhead") and "pipe_u" or "box", tex = tex, part = kind,
        u0 = 0, u1 = 0, v0 = 0, v1 = 0, y0 = 0, y1 = 0 }
      local wbox = { p = part, x0 = x0, y0 = y0, z0 = z0, x1 = x1, y1 = y1, z1 = z1 }
      emit_part(ctx, wbox, sp, "theme_detail", ctx.theme.label .. " overhead structure under the ceiling of " .. (s.name or "the space"))
      ctx.count.elements = ctx.count.elements + 1
      ctx.count.overhead = ctx.count.overhead + 1
      local cell = sp:cell_of((x0 + x1) / 2, (z0 + z1) / 2)
      sp.cell_count[cell] = (sp.cell_count[cell] or 0) + 1
      placed = placed + 1
    end
  end
end

-- Wall runs: one long pipe (or cable tray) along each long wall of a box space, 3-3.6 m up, where
-- the line stays in open air from end to end. One element each, a lot of industrial character.
local WALL_RUN = { industrial = "pipe", construction = "pipe", storage = "pipe", mine = "pipe", control = "tray" }
local function place_wall_runs(ctx, sp, theme_key)
  local s, probe, th = sp.s, sp.probe, ctx.theme
  local kind = WALL_RUN[theme_key]
  if not kind or s.sky or s.kind == "area" then return end
  local floor_y = s.floor_y or s.y0
  local head = s.y1 - floor_y
  if head < 4.5 then return end
  local y = floor_y + min(3.6, head - 1.2)
  local walls = {}
  local w, d = s.x1 - s.x0, s.z1 - s.z0
  if w >= d then
    walls = { { "z", s.z0, 1, s.x0, s.x1 }, { "z", s.z1, -1, s.x0, s.x1 } }
  else
    walls = { { "x", s.x0, 1, s.z0, s.z1 }, { "x", s.x1, -1, s.z0, s.z1 } }
  end
  for _, wl in ipairs(walls) do
    local axis, at, sign = wl[1], wl[2], wl[3]
    local len = wl[5] - wl[4] - 0.6
    local nseg = max(1, ceil(len / 16))
    local seg = len / nseg
    for k = 1, nseg do
    local a0 = wl[4] + 0.3 + (k - 1) * seg + (k > 1 and 0.15 or 0)
    local a1 = wl[4] + 0.3 + k * seg - (k < nseg and 0.15 or 0)
    if a1 - a0 >= 6 then
      local dia = (kind == "tray") and 0.3 or 0.4
      local v0, v1 = 0.15, 0.15 + dia
      local ok = true
      local t = a0
      while t <= a1 + 1e-6 do
        local px, pz
        if axis == "z" then px, pz = t, at + sign * (v0 + v1) / 2 else px, pz = at + sign * (v0 + v1) / 2, t end
        if not probe:air_exact(px, y, pz) then ok = false break end
        t = t + 1.0
      end
      if ok then
        local x0, x1, z0, z1
        if axis == "z" then
          x0, x1 = a0, a1
          z0, z1 = min(at + sign * v0, at + sign * v1), max(at + sign * v0, at + sign * v1)
        else
          z0, z1 = a0, a1
          x0, x1 = min(at + sign * v0, at + sign * v1), max(at + sign * v0, at + sign * v1)
        end
        local hy = (kind == "tray") and 0.12 or dia / 2
        local part = { shape = (kind == "tray") and "box" or "pipe_u", tex = (kind == "tray") and "metal" or "pipe", part = "wall_run",
          u0 = 0, u1 = 0, v0 = 0, v1 = 0, y0 = 0, y1 = 0 }
        emit_part(ctx, { p = part, x0 = x0, y0 = y - hy, z0 = z0, x1 = x1, y1 = y + hy, z1 = z1 }, sp, "theme_detail", ctx.theme.label .. " service run along a wall of " .. (s.name or "the space"))
        ctx.count.elements = ctx.count.elements + 1
        ctx.count.overhead = ctx.count.overhead + 1
        local cell = sp:cell_of((x0 + x1) / 2, (z0 + z1) / 2)
        sp.cell_count[cell] = (sp.cell_count[cell] or 0) + 1
      end
    end
    end
  end
end

-- A decal candidate: decals are placed after every space is dressed, once their boxes are checked against the
-- compiled faces they touch (commit_decals).
local function decal_candidate(ctx, sp, cover, tex, w, h, alpha, x, y, z, fx, fy, fz, ux, uy, uz, depth)
  ctx.pending[#ctx.pending + 1] = { sp = sp, cover = cover, cell = sp:cell_of(x, z), tex = tex, w = w, h = h, d = depth or 1.0,
    alpha = alpha or 255, x = x, y = y, z = z, fx = fx, fy = fy, fz = fz, ux = ux, uy = uy, uz = uz }
end

-- Places the decal candidates whose box touches no compiled face with more than 19 vertex records: the game and the
-- editor clip a decal to each face it touches in fixed 25-vertex buffers and crash loading the level when a face is
-- longer (t-junction points along its edges; research/alpine-decal-clip-overflow.md). The faces are measured on the
-- stored build when it is current, else on a private sealed build. Each space keeps at most its decal target of the
-- safe candidates (it drew twice as many); a cell's coverage decal is kept when safe.
local function commit_decals(ctx)
  local list = ctx.pending
  ctx.pending = {}
  if #list == 0 then return end
  local specs = {}
  for i, d in ipairs(list) do
    specs[i] = { x = d.x, y = d.y, z = d.z, fx = d.fx, fy = d.fy, fz = d.fz, ux = d.ux, uy = d.uy, uz = d.uz, w = d.w, h = d.h, d = d.d }
  end
  local chk = CORE:decal_check{ decals = specs }
  ctx.decal_measure = chk.measure
  if not chk.checked then
    log.warn("kit.dress: " .. ctx.name .. " placed no decals (" .. tostring(chk.measure) .. ").")
    ctx.count.decals_unsafe = ctx.count.decals_unsafe + #list
    return
  end
  local kept = {}
  for i, d in ipairs(list) do
    local sp = d.sp
    if (chk.records[i] or 0) > chk.limit then
      ctx.count.decals_unsafe = ctx.count.decals_unsafe + 1
    elseif d.cover or (kept[sp] or 0) < sp.decal_target then
      if not d.cover then kept[sp] = (kept[sp] or 0) + 1 end
      local o = level.place{ kind = "decal", pos = { d.x, d.y, d.z }, rot = { forward = { d.fx, d.fy, d.fz }, up = { d.ux, d.uy, d.uz } },
        name = ctx.name .. "_decal" .. (ctx.count.decals + 1),
        tags = role_tags(ctx, "theme_detail", (d.fy == -1 and "floor wear" or "wall sign") .. " of " .. ((sp.s and sp.s.name) or "the space"), sp, K.tags_for("dress", ctx.name)),
        props = { texture = d.tex, extents = { d.w, d.h, d.d }, alpha = d.alpha } }
      o:tag("kit.part", "decal")
      ctx.objects[#ctx.objects + 1] = o
      ctx.count.decals = ctx.count.decals + 1
      ctx.count.elements = ctx.count.elements + 1
      sp.cell_count[d.cell] = (sp.cell_count[d.cell] or 0) + 1
      sp.placed = sp.placed + 1
    end
  end
end

-- Decals: signs on walls beside doorways and walls, grime on open floor (twice the target as candidates; see
-- commit_decals).
local function place_decals(ctx, sp, n_target, spots)
  local th, st, L, probe = ctx.theme, ctx.st, sp.L, sp.probe
  local placed = 0
  sp.decal_target = n_target
  n_target = 2 * n_target
  local function decal(tex, w, h, alpha, x, y, z, fx, fy, fz, ux, uy, uz, depth)
    decal_candidate(ctx, sp, false, tex, w, h, alpha, x, y, z, fx, fy, fz, ux, uy, uz, depth)
    sp.decal_cells[sp:cell_of(x, z)] = true
    placed = placed + 1
  end
  -- Wall signs: a wall spot whose wall runs on at least the sign's width at sign height.
  local used = {}
  local signs = th.signs
  local streaks = th.streaks
  local wanted_signs = max(1, floor(n_target * 0.6 + 0.5))
  local n = #spots
  if n > 0 and (#signs > 0 or #streaks > 0) then
    local start = st.int(1, n)
    for t = 0, n - 1 do
      if placed >= wanted_signs then break end
      local s = spots[((start + t * 13) % n) + 1]
      local node = L.nodes[s.id]
      local key = sp:cell_of(node.x, node.z)
      if not used[key] and node.top - node.y >= 3.2 then
        local d = HEAD[s.h]
        local ux, uz = -d[2], d[1]
        local use_sign = (#signs > 0) and ((#streaks == 0) or st.next() < 0.7)
        local def = use_sign and signs[st.int(1, #signs)] or streaks[st.int(1, #streaks)]
        local w, h = def[2], def[3]
        local cy = node.y + (use_sign and 2.2 or min(node.top - node.y - h / 2 - 0.2, 2.6))
        if cy - h / 2 >= node.y + 0.3 and cy + h / 2 <= node.top - 0.1 then
          local wx = node.x - d[1] * s.dist
          local wz = node.z - d[2] * s.dist
          -- The wall must be there across the decal's width (both ends hit within 0.2 m).
          local ok = true
          for _, f in ipairs({ -w / 2 + 0.1, w / 2 - 0.1 }) do
            local px, pz = wx + ux * f + d[1] * 0.3, wz + uz * f + d[2] * 0.3
            local dist = probe:wall_distance(px, cy, pz, -d[1], -d[2], 0.6)
            if dist > 0.45 or not probe:air_exact(px, cy, pz) then ok = false end
          end
          if ok then
            used[key] = true
            decal(def[1], w, h, def[4], wx + d[1] * 0.05, cy, wz + d[2] * 0.05, -d[1], 0, -d[2], 0, 1, 0, 1.0)
          end
        end
      end
    end
  end
  -- Floor grime on open, flat floor away from the walls.
  if #th.grime > 0 then
    local tries = 0
    while placed < n_target and tries < 40 do
      tries = tries + 1
      local id = st.int(1, #L.nodes)
      local node = L.nodes[id]
      if sp.flat[id] and not sp.occupied[id] then
        local def = th.grime[st.int(1, #th.grime)]
        decal(def[1], def[2], def[3], def[4], node.x, node.y + 0.02, node.z, 0, -1, 0, 0, 0, 1, 1.0)
      end
    end
  end
  return placed
end

---@kit kit.dress{room?|rooms?|area?, theme?, density?, seed?, name?, cover?, overhead?, decals?, clutter?, keep_clear?, budget?, group?} → dressing — Generative fill: dresses spaces by 8 m cells with the theme's detail props, cover, overhead beams, stock clutter and decals (5–10 elements per 100 m², every cell dressed), every element placed for a reason and tagged with it (kit.role = cover or theme_detail, kit.purpose, kit.theme, kit.space): cover only against measured threats (lanes of 14 m or more and the approaches to spawns and objectives, a tall breaker on lanes of 24 m or more, each cluster on the lane it breaks with its long side across it), rocks only in organic spaces (caves, canyons, rock-walled spaces, open ground under the sky — a built interior or walled yard gets the theme's built pieces instead) and only against walls or as cover, keeping doorways, stairs, spawns and items clear, never cutting a walk route, and changing the walkable floor by at most budget (default 1.5 %). No decal touches a compiled face with more than 19 vertex records (the game and the editor crash loading such a level): decal spots are checked on the stored build when it is current, else on a private sealed build, and unsafe ones are left out. Returns {name, theme, seed, brushes, objects, group, count (elements placed, a number), by_kind {props, cover, breakers, lanes (threats given cover), overhead, clutter, decals, decals_unsafe (spots left out), brushes}, decal_check (what the decals were measured on), area, existing, density (elements per 100 m², existing included), walk {before, after, change}, cells {total, dressed, undressed}, spaces[]}.
---@opt room: record|string — One space: a kit record (room, corridor, cave, canyon, passage), a kit.name, or a kit.build_layout zone's name (report.zones[i].name).
---@opt rooms: (record|string)[] — Several spaces.
---@opt area: {min=, max=} — A box: every floor inside it (give one of room, rooms, area; none = every air space of the level).
---@opt theme: string = "industrial" — industrial (yard), construction (site), storage, control (control room), mine or canyon (outdoor); aliases yard, site, warehouse, tech, mars_base, cave, rock, outdoor.
---@opt density: number = 1 — Scales the element target (6 per 100 m² at 1, on top of light fixtures; 0.25 to 2).
---@opt seed: integer = 1 — The random stream: the same options, level and seed give the same dressing.
---@opt name: string = dress<n> — Names the dressing (tags, group, object names).
---@opt cover: boolean = true — Free-standing cover against the space's measured threats: one cluster on every open lane of 14 m or more not already broken (a tall sightline breaker at the middle of lanes of 24 m or more) and on the long approach into each spawn, flag or hill, at least 5 m apart and at most one per 45 m²; a space with no such lane (a small room) gets low cover against the wall beside its doorway-to-doorway route instead (one per 120 m², from 60 m²).
---@opt overhead: boolean = true — Beams, pipes or cable trays under ceilings at least 4.5 m high.
---@opt decals: boolean = true — Wall signs, streaks and floor grime (stock decal textures, about 0.3 per 100 m²).
---@opt clutter: boolean = true — Stock clutter props from the theme when the game tables are loaded (catalog.available).
---@opt keep_clear: table[] = none — Extra places to keep clear: points {x, y, z, r = metres} or boxes {min=, max=}.
---@opt budget: number = 0.015 — Largest share of a space's walkable floor that tall props may take (at most 0.02).
---@opt group: boolean = true — Group the dressing brushes under the dressing's name.
function kit.dress(t)
  local api = "kit.dress"
  t = t or {}
  K.check_keys(api, t, { "room", "rooms", "area", "theme", "density", "seed", "name", "cover", "overhead", "decals", "clutter",
    "keep_clear", "budget", "group" })
  local th, theme_key = theme_of(api, t.theme)
  local name = K.name_for(api, "dress", t.name)
  local density = K.positive(api, "density", t.density, 1)
  if density < 0.25 or density > 2 then K.fail(api, "'density' must be between 0.25 and 2 (got " .. K.fmt(density) .. ").") end
  local seed = t.seed
  if seed == nil then seed = 1 end
  if type(seed) ~= "number" or seed ~= floor(seed) then K.fail(api, "'seed' must be an integer (got " .. tostring(seed) .. ").") end
  local budget = K.number(api, "budget", t.budget, 0.015)
  if budget < 0 or budget > 0.02 then K.fail(api, "'budget' must be between 0 and 0.02 (the walkable floor may change by at most 2 %).") end
  local keep = {}
  if t.keep_clear ~= nil then
    if type(t.keep_clear) ~= "table" then K.fail(api, "'keep_clear' must list points {x, y, z, r =} or boxes {min =, max =}.") end
    for i, k in ipairs(t.keep_clear) do
      if type(k) == "table" and k.min ~= nil then
        local ax, ay, az = K.vec3(api, "keep_clear[" .. i .. "].min", k.min)
        local bx, by, bz = K.vec3(api, "keep_clear[" .. i .. "].max", k.max)
        keep[i] = { x0 = min(ax, bx), y0 = min(ay, by), z0 = min(az, bz), x1 = max(ax, bx), y1 = max(ay, by), z1 = max(az, bz) }
      else
        local x, y, z = K.vec3(api, "keep_clear[" .. i .. "]", k)
        keep[i] = { x = x, y = y, z = z, r = (type(k) == "table" and k.r) or 1.5 }
      end
    end
  end
  local spaces = P.spaces(api, t, K.fail)
  local probe = P.shared()
  P.mark("dress probe")
  local st = P.stream(seed)
  local ctx = {
    name = name, theme = th, theme_key = theme_key, st = st, brushes = {}, objects = {}, clutter_on = K.boolean(api, "clutter", t.clutter, true),
    count = { elements = 0, brushes = 0, clutter = 0, decals = 0, cover = 0, props = 0, overhead = 0, breakers = 0, lanes = 0, decals_unsafe = 0, groups = 0, flanks = 0 },
    pending = {},
  }
  local do_cover = K.boolean(api, "cover", t.cover, true)
  local do_overhead = K.boolean(api, "overhead", t.overhead, true)
  local do_decals = K.boolean(api, "decals", t.decals, true)
  local per100 = 6 * density

  local reports = {}
  local total_area, total_before, total_after, cells_total, cells_dressed, existing_total = 0, 0, 0, 0, 0, 0
  local built = {}
  for _, s in ipairs(spaces) do
    P.mark("dress space start")
    local sp = new_space(probe, s, keep, budget)
    built[#built + 1] = sp
    -- Theme fit: the space's own context decides where rocks may go (and swaps them out of built interiors and walled yards).
    sp.context = P.context(s)
    sp.organic = sp.context == "rock_walls" or sp.context == "open"
    sp.built = not sp.organic
    ctx.organic = sp.organic
    local L = sp.L
    local w, d = s.x1 - s.x0, s.z1 - s.z0
    sp.ncx, sp.ncz = max(1, floor(w / 8 + 0.5)), max(1, floor(d / 8 + 0.5))
    sp.cw, sp.cd = w / sp.ncx, d / sp.ncz
    sp.cell_count = {}
    sp.decal_cells = {}
    sp.decal_target = 0
    local area = sp.area_before
    local existing = existing_dressing(probe, s)
    existing_total = existing_total + existing
    local target = max(0, floor(area / 100 * per100 + 0.5) - existing)
    local start_elements = ctx.count.elements
    -- Cells and their floor (nodes on flat floor).
    local cell_nodes = {}
    for id, n in ipairs(L.nodes) do
      local c = sp:cell_of(n.x, n.z)
      cell_nodes[c] = (cell_nodes[c] or 0) + 1
    end
    -- Existing dressing counts for its cell.
    for _, o in ipairs(level.objects_in{ min = { s.x0, s.y0 - 0.5, s.z0 }, max = { s.x1, s.y1 + 0.5, s.z1 } }.objects) do
      if o.kind == "Clutter" or o.kind == "Decal" then
        local c = sp:cell_of(o.pos.x, o.pos.z)
        sp.cell_count[c] = (sp.cell_count[c] or 0) + 1
      end
    end
    local dc = probe:detail_centres(s.x0, s.y0, s.z0, s.x1, s.y1, s.z1)
    for q = 1, #dc, 2 do
      local c = sp:cell_of(dc[q], dc[q + 1])
      sp.cell_count[c] = (sp.cell_count[c] or 0) + 1
    end
    P.mark("dress space ready")
    local spots = wall_spots(sp)
    -- 1. Overhead structure, 2. cover, 3. props per cell until the target, 4. decals, 5. coverage.
    P.mark("dress spots")
    if do_overhead then
      place_overhead(ctx, sp)
      place_wall_runs(ctx, sp, theme_key)
    end
    P.mark("dress overhead")
    if do_cover and place_cover(ctx, sp) == 0 then place_wall_cover(ctx, sp, spots) end
    place_door_flanks(ctx, sp, spots)
    P.mark("dress cover")
    local cells = {}
    for a = 0, sp.ncx - 1 do for b = 0, sp.ncz - 1 do cells[#cells + 1] = a * 1000 + b end end
    local decal_target = do_decals and max(area >= 60 and 1 or 0, floor(area / 100 * 0.3 * density + 0.5)) or 0
    local prop_target = max(0, target - decal_target)
    local total_target = max(1, floor(area / 100 * per100 + 0.5))
    local function fill_cell(c)
      local ok = nil
      local r = st.next()
      -- Stock clutter meshes first when the tables have the theme's classes (they read as real objects).
      if ctx.clutter_on and r < ((#small_clutter(ctx) > 0) and 0.45 or 0.2) then ok = place_clutter(ctx, sp, spots, c) end
      if not ok and r < 0.65 then ok = place_wall_prop(ctx, sp, spots, st.weighted(th.props), c) end
      if not ok then ok = place_free_prop(ctx, sp, st.weighted(th.props), c) end
      if not ok then ok = place_wall_prop(ctx, sp, spots, st.weighted(th.props), c) end
      if not ok and #th.wall > 0 then ok = place_wall_prop(ctx, sp, spots, th.wall[st.int(1, #th.wall)], c) end
      return ok
    end
    -- Pass 1: each cell up to its share of the target; pass 2: anywhere, until the target.
    local guard = 0
    local progress = true
    while ctx.count.elements - start_elements < prop_target and progress and guard < 400 do
      progress = false
      for _, c in ipairs(cells) do
        if ctx.count.elements - start_elements >= prop_target then break end
        local want = max(1, total_target * (cell_nodes[c] or 0) * PITCH * PITCH / max(area, 1))
        if (sp.cell_count[c] or 0) < want then
          guard = guard + 1
          if fill_cell(c) then progress = true end
        end
      end
    end
    progress = true
    while ctx.count.elements - start_elements < prop_target and progress and guard < 600 do
      guard = guard + 1
      progress = fill_cell(nil) and true or false
    end
    P.mark("dress props")
    if do_decals then place_decals(ctx, sp, decal_target, spots) end
    -- Coverage: a cell with walkable floor but no element gets a wall element, a prop or a decal.
    for _, c in ipairs(cells) do
      if (cell_nodes[c] or 0) * PITCH * PITCH >= 8 and (sp.cell_count[c] or 0) == 0 and not sp.decal_cells[c] then
        local ok = false
        if #th.wall > 0 then ok = place_wall_prop(ctx, sp, spots, th.wall[st.int(1, #th.wall)], c) end
        if not ok then ok = place_wall_prop(ctx, sp, spots, st.weighted(th.props), c) end
        if not ok and do_decals and #th.grime > 0 then
          -- A floor decal on the cell's flat floor.
          for id, n in ipairs(L.nodes) do
            if sp.flat[id] and not sp.occupied[id] and sp:cell_of(n.x, n.z) == c then
              local def = th.grime[st.int(1, #th.grime)]
              decal_candidate(ctx, sp, true, def[1], def[2], def[3], def[4], n.x, n.y + 0.02, n.z, 0, -1, 0, 0, 0, 1, 1.0)
              sp.decal_cells[c] = true
              break
            end
          end
        end
      end
    end
    sp.cells, sp.cell_nodes, sp.placed = cells, cell_nodes, ctx.count.elements - start_elements
    total_area = total_area + area
    reports[#reports + 1] = { name = s.name, area = area, existing = existing, context = sp.context }
  end

  -- The decals, checked against the compiled faces they touch; then each space's dressed cells.
  if #ctx.pending > 0 then commit_decals(ctx) end
  if ctx.count.decals_unsafe > 0 then
    log.info(api .. ": " .. name .. " left out " .. ctx.count.decals_unsafe .. " decal spot(s) touching a compiled face with more than 19 vertices (they crash the game).")
  end
  for i, sp in ipairs(built) do
    local dressed, ncell = 0, 0
    for _, c in ipairs(sp.cells) do
      if (sp.cell_nodes[c] or 0) * PITCH * PITCH >= 8 then
        ncell = ncell + 1
        if (sp.cell_count[c] or 0) > 0 then dressed = dressed + 1 end
      end
    end
    cells_total, cells_dressed = cells_total + ncell, cells_dressed + dressed
    reports[i].placed, reports[i].cells, reports[i].dressed = sp.placed, ncell, dressed
  end

  -- Walkable floor after dressing, measured on a fresh probe of the same spaces (every brush the
  -- call made included).
    P.mark("dress decals+coverage")
  for _, b in ipairs(ctx.brushes) do probe:add_brush(b) end
  P.touch(probe)
  local after_probe = probe
  for i, sp in ipairs(built) do
    local a2 = after_probe:walk_area_of(sp.s)
    reports[i].walk_before, reports[i].walk_after = sp.area_before, a2
    total_before, total_after = total_before + sp.area_before, total_after + a2
  end
  local change = total_before > 0 and (total_after - total_before) / total_before or 0
  if abs(change) > 0.02 then
    log.warn(api .. ": " .. name .. " changed the walkable floor by " .. K.fmt(change * 100) .. " % (more than 2 %).")
  end
  local group = (K.boolean(api, "group", t.group, true) and #ctx.brushes >= 2) and level.create_group{ name = name, brushes = ctx.brushes } or nil
  local elements = ctx.count.elements + existing_total
  local by_kind = {}
  for k, v in pairs(ctx.count) do if k ~= "elements" then by_kind[k] = v end end
  return {
    kind = "dressing", name = name, theme = theme_key, seed = seed, brushes = ctx.brushes, objects = ctx.objects, group = group,
    count = ctx.count.elements, by_kind = by_kind, area = total_area, existing = existing_total, decal_check = ctx.decal_measure,
    density = total_area > 0 and elements / total_area * 100 or 0,
    walk = { before = total_before, after = total_after, change = change },
    cells = { total = cells_total, dressed = cells_dressed, undressed = cells_total > 0 and (cells_total - cells_dressed) / cells_total or 0 },
    spaces = reports,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Light fixtures: ceiling lamps indoors, lamps on the walls and posts along the edges outdoors.
-- ---------------------------------------------------------------------------------------------

-- Outdoor lighting, calibrated on the stock levels' standable floor open to the sky (research/example_rfls: the MP
-- maps and the outdoor single-player maps with at least 500 m² of it). The lights that carry the floor (range 10 m
-- or more) stand 0.32-1.07 per 100 m² of it where a map lights its outdoors with lights at all (median 0.53; ctf01
-- 0.60, ctf02 0.58, ctf04 1.07, dm16 0.53, dm12 0.37), reach 14-60 m (median about 20 m) from 8-34 m up (median about
-- 11 m), and 62-80 % of them are within 3 m of a wall although only 7-53 % of the floor is (ctf01, ctf02, ctf04, dm16).
-- The rest are short accents (3-6 m range) at fixtures and walls, so that outdoor floors carry 1.19-2.22 lights per
-- 100 m² in all (ctf01, ctf04, ctf06, dm16, dm19). Light fixtures are sparse: 0-1.07 per 100 m², median under 0.1.
-- ctf04, the stock map lit by lamp posts, stands its posts 0.7-1.2 m off a wall with the arm pointing away from it,
-- about 15 m apart (range 15 m, intensity 1.5), and mounts wall lamps on the walls (range 8-12 m, intensity 1.0-2.0).
local OUTDOOR = {
  per100 = 0.5,   -- fixtures per 100 m² of a room's floor
  reach = 18,     -- the farthest floor point from its nearest light before an inner post is added (m)
  wall_h = 8.0,   -- a wall lamp's light above the floor (m), headroom allowing
  post_off = 1.0, -- a post's distance from the edge it stands along (m)
  corner = 1.5,   -- no fixture closer than this to a corner (m)
  door_max = 8,   -- an opening up to this wide is a doorway (wider: an open edge) (m)
  door_clear = 1.5, -- posts keep this far from a doorway's sides (m)
  route = 3,      -- inner posts keep this far from the straight lines between doorways (m)
  max_range = 40, -- range cap (m)
  max_i = 2.0,    -- intensity cap (stock outdoor lamps run 1.0-2.0)
  glow_range = 5, -- the short light at each fixture (stock outdoor accent lights: 3-6 m)
}
-- The stock wall lamp (model space, LOD 0 vertices plus the submesh offset: x -0.35..0.35, y -0.09..0.23,
-- z -0.08..0.17; ctf04 mounts it with +Z out of the wall and its light 0.7 m above it). The kit puts the light
-- 0.3 m in front of it and 0.7 m above.
local WALL_LAMP = { class = "Wedge Light 3", back = 0.08, up = 0.7, out = 0.3 }
local DETAIL_ARM = 0.6 -- a detail lamp post's head reaches this far from the post (m)

-- A lamp post standing on the floor at (px, gy, pz), its head toward the unit (dx, dz), the light lh above the floor
-- (the stock post: its own lamp height and arm). Returns the light position and the fixture handles.
local function emit_post(fn, name, i, px, gy, pz, dx, dz, lh, stock)
  if stock then
    -- The stock lamp post: its post on the floor at the spot, its arm over the light. Model -Z is the arm; yaw
    -- turns +Z to (sin yaw, cos yaw), so -Z points along the heading.
    local yaw = math.deg(math.atan2(-dx, -dz))
    local rr = math.rad(yaw)
    local ox = px - POLE.post_x * math.cos(rr)
    local oz = pz + POLE.post_x * math.sin(rr)
    local f = level.place{ kind = "clutter", class = POLE.class, pos = { ox, gy + POLE.base, oz }, yaw = yaw,
      name = name .. "_fixture" .. i, tags = K.tags_for(fn, name) }
    f:tag("kit.part", "fixture")
    return px + dx * POLE.arm, gy + POLE.light, pz + dz * POLE.arm, { f }
  end
  -- A slim detail post, a short arm and a lamp head whose full-bright underside the light hangs under.
  local yaw = math.deg(math.atan2(dx, dz))
  local tex = "mtl_girder03_dirty01.tga"
  local post = K.emit(fn, name, "post", { shape = "box", pos = { px, gy + (lh + 0.5) / 2, pz }, size = { 0.2, lh + 0.5, 0.2 }, detail = true }, tex, nil)
  local arm = K.emit(fn, name, "arm", { shape = "box", pos = { px + dx * DETAIL_ARM / 2, gy + lh + 0.42, pz + dz * DETAIL_ARM / 2 },
    size = { 0.1, 0.1, DETAIL_ARM }, rot = { yaw = yaw }, detail = true }, tex, nil)
  local hx, hz = px + dx * DETAIL_ARM, pz + dz * DETAIL_ARM
  local hd = K.emit(fn, name, "fixture", { shape = "box", pos = { hx, gy + lh + 0.3, hz }, size = { 0.5, 0.15, 0.5 }, rot = { yaw = yaw }, detail = true },
    KIT.defaults.fixture_texture, nil)
  for _, fc in ipairs(hd:faces_facing("down")) do fc:set_flag("full_bright", true) end
  return hx, gy + lh, hz, { post, arm, hd }
end

-- A lamp on a wall: (wx, wz) on the wall's surface, (nx, nz) the unit normal out of it, the light at height ly.
-- Returns the light position and the fixture handles.
local function emit_wall_lamp(fn, name, i, wx, wz, nx, nz, ly, stock)
  local yaw = math.deg(math.atan2(nx, nz))
  if stock then
    local f = level.place{ kind = "clutter", class = WALL_LAMP.class,
      pos = { wx + nx * (WALL_LAMP.back + 0.01), ly - WALL_LAMP.up, wz + nz * (WALL_LAMP.back + 0.01) }, yaw = yaw,
      name = name .. "_fixture" .. i, tags = K.tags_for(fn, name) }
    f:tag("kit.part", "fixture")
    local o = WALL_LAMP.back + WALL_LAMP.out
    return wx + nx * o, ly, wz + nz * o, { f }
  end
  -- A small detail bracket flush on the wall; its full-bright underside lights the wall below it.
  local b = K.emit(fn, name, "fixture", { shape = "box", pos = { wx + nx * 0.16, ly + 0.25, wz + nz * 0.16 }, size = { 0.6, 0.25, 0.3 },
    rot = { yaw = yaw }, detail = true }, KIT.defaults.fixture_texture, nil)
  for _, fc in ipairs(b:faces_facing("down")) do fc:set_flag("full_bright", true) end
  return wx + nx * 0.42, ly, wz + nz * 0.42, { b }
end

-- The median floor light over floor samples {x, y, z} from lights {x, y, z, range, intensity}: RF's linear falloff
-- (1 - d/r) times the point light's wrap (2 N.L + 1) / 3 on an upward floor, summed (no shadows).
local function floor_light(samples, lights)
  local e = {}
  local step = max(1, ceil(#samples / 1500)) -- at most about 1,500 samples (every 2 m up to 6,000 m²)
  for si = 1, #samples, step do
    local s = samples[si]
    local sum = 0
    for li = 1, #lights do
      local l = lights[li]
      local dx, dy, dz = l[1] - s[1], l[2] - s[2], l[3] - s[3]
      local d = sqrt(dx * dx + dy * dy + dz * dz)
      if d < l[4] and d > 1e-3 then sum = sum + l[5] * (1 - d / l[4]) * (2 * max(0, dy / d) + 1) / 3 end
    end
    e[#e + 1] = sum
  end
  table.sort(e)
  return e[max(1, floor(#e / 2 + 0.5))] or 0
end

-- The distance from (x, z) to the segment (ax, az)-(bx, bz).
local function seg_dist(x, z, ax, az, bx, bz)
  local vx, vz = bx - ax, bz - az
  local l2 = vx * vx + vz * vz
  local f = l2 > 1e-9 and max(0, min(1, ((x - ax) * vx + (z - az) * vz) / l2)) or 0
  local qx, qz = ax + vx * f - x, az + vz * f - z
  return sqrt(qx * qx + qz * qz)
end

-- True when (x, z) lies inside the plan polygon pts ({x, z} pairs; even-odd rule).
local function in_outline(pts, x, z)
  local inside = false
  local j = #pts
  for i = 1, #pts do
    local xi, zi, xj, zj = pts[i][1], pts[i][2], pts[j][1], pts[j][2]
    if (zi > z) ~= (zj > z) and x < (xj - xi) * (z - zi) / (zj - zi) + xi then inside = not inside end
    j = i
  end
  return inside
end

-- The sides of a plan polygon as outdoor_plan walks them: start point, unit direction, inward normal, length.
local function outline_sides(pts)
  local sides = {}
  for i = 1, #pts do
    local a, b = pts[i], pts[i % #pts + 1]
    local dx, dz = b[1] - a[1], b[2] - a[2]
    local len = sqrt(dx * dx + dz * dz)
    if len > 0.5 then
      dx, dz = dx / len, dz / len
      local nx, nz = -dz, dx
      local mx, mz = (a[1] + b[1]) / 2, (a[2] + b[2]) / 2
      if not in_outline(pts, mx + nx * 0.05, mz + nz * 0.05) then nx, nz = -nx, -nz end
      sides[#sides + 1] = { a[1], a[2], dx, dz, nx, nz, len }
    end
  end
  return sides
end

-- The area of a plan polygon ({x, z} pairs).
local function outline_area(pts)
  local s = 0
  for i = 1, #pts do
    local a, b = pts[i], pts[i % #pts + 1]
    s = s + a[1] * b[2] - b[1] * a[2]
  end
  return abs(s) / 2
end

-- The outdoor plan of one box r {x0, z0, x1, z1, y0, y1, floor}: fixtures along its edges (lamps on the walls, posts
-- beside its doorways and along its open edges), inner posts only where the edges leave floor out of reach, never on
-- the straight way between two doorways. o: {api, name, after, clearance, height, count (fixtures, or nil = by
-- area), stock_post, stock_lamp, skip(x, z), outline}. With outline (a plan polygon of {x, z} pairs inside the box),
-- the fixtures follow the polygon's edges and only floor inside it is lit (a traced region that is not a rectangle).
-- Returns plan entries {kind = "wall" | "post", ...} and the floor samples.
local function outdoor_plan(r, o)
  local probe = P.shared()
  local floor0 = r.floor or r.y0
  local w, d = r.x1 - r.x0, r.z1 - r.z0
  local lp = 2 * (w + d)
  local function floor_ok(g) return g ~= nil and g >= r.y0 - 0.5 and g <= r.y1 - 2.2 end
  -- The sides, walked counter-clockwise from (x0, z0): start point, direction along the side, inward normal.
  local sides = {
    { r.x0, r.z0, 1, 0, 0, 1, w }, { r.x1, r.z0, 0, 1, -1, 0, d },
    { r.x1, r.z1, -1, 0, 0, -1, w }, { r.x0, r.z1, 0, -1, 1, 0, d },
  }
  local outline = o.outline
  local skip = o.skip
  local area = w * d
  -- The first fixture's spot: the middle of the longest side.
  local first_t = w >= d and (w / 2) or (w + d / 2)
  if outline ~= nil then
    sides = outline_sides(outline)
    lp, area = 0, outline_area(outline)
    local longest = -1
    for _, s in ipairs(sides) do
      if s[7] > longest then longest, first_t = s[7], lp + s[7] / 2 end
      lp = lp + s[7]
    end
    skip = function(x, z) return o.skip(x, z) or not in_outline(outline, x, z) end
  end
  -- Stations every 0.5 m along the edges: the floor there, a wall at lamp height, an opening at body height.
  local st, t0 = {}, 0
  for si, s in ipairs(sides) do
    local len = s[7]
    local u = 0.25
    while u < len do
      local ex, ez = s[1] + s[3] * u, s[2] + s[4] * u
      local nx, nz = s[5], s[6]
      local e = { t = t0 + u, side = si, u = u, ex = ex, ez = ez, nx = nx, nz = nz, corner = u < OUTDOOR.corner or u > len - OUTDOOR.corner }
      local qx, qz = ex + nx * 0.3, ez + nz * 0.3
      local g = K.ground_point(qx, qz, r.y1 - 0.05).y
      if floor_ok(g) then
        e.gy = g
        e.hh = r.y1 - g
        e.open = probe:wall_distance(qx, g + 1.2, qz, -nx, -nz, 1.5) >= 1.5
        e.lh = o.height or min(OUTDOOR.wall_h, e.hh - 0.8)
        local dw = probe:wall_distance(qx, g + e.lh, qz, -nx, -nz, 1.5)
        if dw < 1.0 and e.lh >= 2.5 then e.wall = { qx - nx * dw, qz - nz * dw } end
      end
      if not skip(ex + nx * 0.5, ez + nz * 0.5) then st[#st + 1] = e end
      u = u + 0.5
    end
    t0 = t0 + len
  end
  -- Openings: runs of open stations on one side; up to door_max wide they are doorways.
  local doors = {}
  do
    local i = 1
    while i <= #st do
      if st[i].open then
        local j = i
        while j < #st and st[j + 1].open and st[j + 1].side == st[i].side and st[j + 1].t - st[j].t < 0.75 do j = j + 1 end
        local width = st[j].t - st[i].t + 0.5
        doors[#doors + 1] = { t0 = st[i].t - 0.25, t1 = st[j].t + 0.25, side = st[i].side, width = width,
          x = (st[i].ex + st[j].ex) / 2, z = (st[i].ez + st[j].ez) / 2, door = width <= OUTDOOR.door_max }
        i = j + 1
      else
        i = i + 1
      end
    end
  end
  local function arc(a, b)
    local q = abs(a - b) % lp
    return min(q, lp - q)
  end
  local function near_door(t)
    for _, dr in ipairs(doors) do
      if dr.door and t > dr.t0 - OUTDOOR.door_clear and t < dr.t1 + OUTDOOR.door_clear then return true end
    end
    return false
  end
  -- A station's fixture: a lamp on the wall, else a post along the edge (never in or beside a doorway's opening).
  local function post_at(px, pz, gy, dx, dz)
    if K.solid_near(px, gy + 1.2, pz, 0.35, o.after) or K.solid_near(px, gy + 4.2, pz, 0.35, o.after) then return nil end
    local hh = r.y1 - gy
    if o.stock_post and o.height == nil and hh >= POLE.top + 0.15 then
      local hx, hz = px + dx * POLE.arm, pz + dz * POLE.arm
      if hx > r.x0 + 0.3 and hx < r.x1 - 0.3 and hz > r.z0 + 0.3 and hz < r.z1 - 0.3
        and not K.solid_near(hx, gy + POLE.light, hz, 0.4, o.after) then
        return { kind = "post", x = px, z = pz, gy = gy, dx = dx, dz = dz, stock = true, l = { hx, gy + POLE.light, hz } }
      end
    end
    local lh = o.height or min(hh - 1.0, POLE.light)
    local hx, hz = px + dx * DETAIL_ARM, pz + dz * DETAIL_ARM
    if lh < 2.5 or K.solid_near(hx, gy + lh, hz, 0.3, o.after) then return nil end
    return { kind = "post", x = px, z = pz, gy = gy, dx = dx, dz = dz, lh = lh, l = { hx, gy + lh, hz } }
  end
  local function fixture_at(e, want_post)
    if e.gy == nil or e.corner then return nil end
    if e.wall and not want_post then
      local lx, lz = e.wall[1] + e.nx * 0.42, e.wall[2] + e.nz * 0.42
      if not K.solid_near(lx, e.gy + e.lh, lz, 0.2, o.after) then
        return { kind = "wall", wx = e.wall[1], wz = e.wall[2], nx = e.nx, nz = e.nz, ly = e.gy + e.lh, l = { lx, e.gy + e.lh, lz }, t = e.t }
      end
    end
    if near_door(e.t) then return nil end
    local px, pz = e.ex + e.nx * OUTDOOR.post_off, e.ez + e.nz * OUTDOOR.post_off
    local g = K.ground_point(px, pz, r.y1 - 0.05).y
    if not floor_ok(g) then return nil end
    local f = post_at(px, pz, g, e.nx, e.nz)
    if f then f.t = e.t end
    return f
  end

  local n = o.count or max(1, floor(area * OUTDOOR.per100 / 100 + 0.5))
  local plan = {}
  local function min_arc(t)
    local m = math.huge
    for _, f in ipairs(plan) do if f.t then m = min(m, arc(t, f.t)) end end
    return m
  end
  -- A post beside each doorway (2-3.5 m along the wall from its side), while the count allows.
  for _, dr in ipairs(doors) do
    if dr.door and #plan < n then
      local best, bs = nil, math.huge
      for _, e in ipairs(st) do
        local off = e.t < dr.t0 and dr.t0 - e.t or (e.t > dr.t1 and e.t - dr.t1 or -1)
        if e.side == dr.side and off >= OUTDOOR.door_clear + 0.5 and off <= 3.5 and min_arc(e.t) > 4 then
          local score = abs(off - 2.5)
          if score < bs - 1e-9 then
            local f = fixture_at(e, true)
            if f and f.kind == "post" then best, bs = f, score end
          end
        end
      end
      if best then plan[#plan + 1] = best end
    end
  end
  -- The rest spread evenly along the edges: each next fixture as far along the edges from the others as it can be.
  local spacing = lp / n
  while #plan < n do
    local best, bd = nil, -math.huge
    local target = nil
    if #plan == 0 then
      -- The first: the middle of the longest side.
      target = first_t
    end
    for _, e in ipairs(st) do
      local dd = target and -arc(e.t, target) or min_arc(e.t)
      if dd > bd + 1e-9 then
        local f = fixture_at(e, false)
        if f then best, bd = f, dd end
      end
    end
    if best == nil or (target == nil and bd < 0.4 * spacing) then break end
    plan[#plan + 1] = best
  end

  -- Floor samples every 2 m; inner posts where the edges leave floor too far from every light.
  local L = probe:lattice(r.x0, r.z0, r.x1, r.z1, floor0 - 0.1, max(floor0, r.y1 - 1.0), P.HEADROOM, 4)
  local samples = {}
  for _, nd in ipairs(L.nodes) do
    if not skip(nd.x, nd.z) then samples[#samples + 1] = { nd.x, nd.y, nd.z } end
  end
  -- Each sample's distance to its nearest light, kept up to date as fixtures are added.
  local dn = {}
  local function reach_of(f)
    for i = 1, #samples do
      local s = samples[i]
      local dx, dy, dz = f.l[1] - s[1], f.l[2] - s[2], f.l[3] - s[3]
      local q = sqrt(dx * dx + dy * dy + dz * dz)
      if q < dn[i] then dn[i] = q end
    end
  end
  for i = 1, #samples do dn[i] = math.huge end
  for _, f in ipairs(plan) do reach_of(f) end
  -- The ways between doorways (one doorway: to the room's centre), kept clear of inner posts.
  local routes = {}
  local dlist = {}
  for _, dr in ipairs(doors) do dlist[#dlist + 1] = dr end
  if #dlist == 1 then dlist[2] = { x = (r.x0 + r.x1) / 2, z = (r.z0 + r.z1) / 2 } end
  for a = 1, #dlist do
    for b = a + 1, #dlist do routes[#routes + 1] = { dlist[a].x, dlist[a].z, dlist[b].x, dlist[b].z } end
  end
  local free = {}
  for i, s in ipairs(samples) do
    local ok = true
    for _, rt in ipairs(routes) do
      if seg_dist(s[1], s[3], rt[1], rt[2], rt[3], rt[4]) < OUTDOOR.route then ok = false break end
    end
    if ok then
      for _, dr in ipairs(doors) do
        if (s[1] - dr.x) ^ 2 + (s[3] - dr.z) ^ 2 < 16 then ok = false break end
      end
    end
    free[i] = ok
  end
  -- Inner posts, each on the free floor sample farthest from every light, until the floor is in reach (edges with
  -- no floor or walls, as around floating platforms, leave every light to this step).
  local rcx, rcz = (r.x0 + r.x1) / 2, (r.z0 + r.z1) / 2
  local inner, cap = 0, n + 8
  while inner < cap do
    local far = 0
    for i = 1, #samples do if dn[i] > far then far = dn[i] end end
    if far <= OUTDOOR.reach then break end
    local placed = false
    for _ = 1, 24 do
      local bi, bd = nil, -1
      for i = 1, #samples do
        if free[i] and dn[i] > bd then bi, bd = i, dn[i] end
      end
      if bi == nil or bd < OUTDOOR.reach * 0.6 then break end
      local s = samples[bi]
      local dx, dz = rcx - s[1], rcz - s[3]
      local l = sqrt(dx * dx + dz * dz)
      if l < 1e-6 then dx, dz = 0, 1 else dx, dz = dx / l, dz / l end
      local f = post_at(s[1], s[3], s[2], dx, dz)
      if f then
        plan[#plan + 1] = f
        reach_of(f)
        placed = true
        break
      end
      free[bi] = false
    end
    if not placed then break end
    inner = inner + 1
  end
  local far = 0
  for i = 1, #samples do if dn[i] < math.huge and dn[i] > far then far = dn[i] end end
  return plan, samples, far
end

-- ---------------------------------------------------------------------------------------------
-- kit.light_room (v3): lights under visible fixtures; outdoors on the edges.
-- ---------------------------------------------------------------------------------------------

---@kit kit.light_room{room, spacing?, color?, range?, height?, name?, fixture?, theme?} → object[] — Lights over a room or corridor record, each with a visible fixture. Indoors a room gets a grid sized from its floor area and headroom (about 2.2 lights per 100 m² up to 5 m of headroom, easing to 1.8 in tall halls; never under 1.7 — DESIGN-QUALITY-SPEC §a.3, 1.7–4.5) and a corridor one row, a light every 4 m, along every leg of a kit.corridor{path = …}; each light hangs just under a ceiling fixture (a stock clutter light when the game tables are loaded, else a small full-bright detail lamp) attached to the ceiling measured over its spot. A room open to the sky is lit as the stock outdoor maps are: fewer, stronger lights (about 0.5 per 100 m², range and intensity raised so the floor gets at least 90 % of the light a ceiling grid would give it) on its edges — a lamp on the wall where a wall stands at lamp height, a lamp post beside each doorway and along open edges (a stock post when the tables are loaded and the headroom allows, else a detail post with a full-bright head) — and a post inside the room only where floor would be more than 18 m from every light, never on the straight way between two doorways (a record carrying its plan outline as points, as kit.trace's regions do, is lit as one space along that outline); each fixture also gets a short 5 m glow light (on the wall under a wall lamp, at a post's foot), as the stock maps light their outdoor fixtures. Raised floors, ramps and lowered ceilings move every fixture with them; a light never goes inside a solid (it moves to a free spot, or is skipped with a warning). The fixtures are in the returned list's fixtures field (outdoors the glow lights in its glows field).
---@opt room: record — Required: the room or corridor record to light (min/max box; its air brush decides which solids stand in it).
---@opt spacing: number = from the floor area and headroom; 4 along a corridor — Grid cell size in metres (one light per cell); giving it overrides the area rule (open to the sky: the distance between fixtures along the edges).
---@opt color: {r, g, b} = the theme's colour, else kit.defaults.light_color — Light colour, 0-255 per channel.
---@opt range: number = reaches the cell's floor corners (at least kit.defaults.light_range_min); open to the sky: 1.25-1.8 × the farthest floor from its nearest light — Light range in metres.
---@opt height: number = just under the fixture (fixture = false: 3/4 of the headroom, at most 0.5 m below the ceiling) — Height above the floor under each light, metres (outdoors it sets the wall lamps' height and sizes detail lamp posts instead of the stock one).
---@opt name: string = the room's name — Prefix of the lights' names (<name>_light<i>) and their kit.name tag.
---@opt fixture: boolean|string = true — true: a fixture per light; "brush": always the detail lamp (outdoors: detail lamps and posts); a clutter class name: that class (indoors); false: bare lights.
---@opt theme: string = none — A kit.dress theme: its fixture classes and light colour.
function kit.light_room(t)
  local api = "kit.light_room"
  K.check_keys(api, t, { "room", "spacing", "color", "range", "height", "name", "fixture", "theme" })
  local r = K.record_box(api, "room", t.room)
  local name = t.name ~= nil and K.name_for(api, "light_room", t.name) or (t.room.name or K.name_for(api, "light_room", nil))
  local th = t.theme ~= nil and theme_of(api, t.theme) or nil
  local fixture = t.fixture
  if fixture == nil then fixture = true end
  if type(fixture) ~= "boolean" and type(fixture) ~= "string" then
    K.fail(api, "'fixture' must be true, false, \"brush\" or a clutter class name.")
  end
  -- A multi-leg corridor (kit.corridor{path = ...}): each leg is lit on its own, one row along it.
  local legs = nil
  if t.room.kind == "corridor" and type(t.room.brushes) == "table" and #t.room.brushes > 1 then
    legs = {}
    for _, b in ipairs(t.room.brushes) do
      if type(b) == "userdata" and b.air then
        local bb = b.bounds
        legs[#legs + 1] = { x0 = bb.min.x, y0 = bb.min.y, z0 = bb.min.z, x1 = bb.max.x, y1 = bb.max.y, z1 = bb.max.z, floor = bb.min.y }
      end
    end
    if #legs == 0 then legs = nil end
  end
  local w, d, h = r.x1 - r.x0, r.z1 - r.z0, r.y1 - r.floor
  local corridor = (t.room.kind == "corridor") or min(w, d) <= 6
  local spacing = K.positive(api, "spacing", t.spacing, corridor and 4 or KIT.defaults.light_spacing)
  local nx = max(1, floor(w / spacing + 0.5))
  local nz = max(1, floor(d / spacing + 0.5))
  -- A room: about 2.2 lights per 100 m² up to 5 m of headroom, easing to 1.8 in tall halls (lights hang higher and
  -- reach further), never under the exemplar 1.7 (DESIGN-QUALITY-SPEC §a.3: 1.7-4.5). Also the reference the
  -- outdoor plan's floor light is held to.
  local function area_grid(bw, bd, bh)
    local per100 = 2.2 * max(0.82, min(1, 5 / max(bh, 1)))
    local cell = sqrt(100 / per100)
    local gx, gz = max(1, floor(bw / cell + 0.5)), max(1, floor(bd / cell + 0.5))
    while gx * gz * 100 / max(bw * bd, 1) < 1.7 do
      if bw / gx >= bd / gz then gx = gx + 1 else gz = gz + 1 end
    end
    return gx, gz
  end
  if corridor and t.spacing == nil then
    if w >= d then nz = 1 else nx = 1 end
  elseif t.spacing == nil then
    nx, nz = area_grid(w, d, h)
  end
  -- Open to the sky: fixtures on the edges.
  local brushes = t.room.brushes or (t.room.brush and { t.room.brush }) or {}
  local sky = false
  for _, b in ipairs(brushes) do
    if type(b) == "userdata" and b.air then
      for _, f in ipairs(b:faces_facing("up")) do if f.show_sky then sky = true end end
    end
  end
  local classes = clutter_classes()
  local height_opt = t.height ~= nil and K.positive(api, "height", t.height) or nil
  local range_opt = t.range ~= nil and K.positive(api, "range", t.range) or nil
  local color = t.color or (th and th.light_color) or KIT.defaults.light_color
  local after = K.record_time_index(t.room)
  local clearance = KIT.defaults.light_clearance
  local lights, fixtures = {}, {}

  if sky then
    local stock = fixture ~= false and fixture ~= "brush" and classes ~= nil
    local glows = {}
    local boxes = legs or { r }
    -- A record carrying its plan outline (points: {x, z} pairs, as kit.trace regions do) is lit as one outdoor space
    -- along that outline, so a traced region that is not a rectangle gets one plan for all its floor.
    local outline = nil
    if legs == nil and type(t.room.points) == "table" and #t.room.points >= 3 then
      outline = {}
      for i, q in ipairs(t.room.points) do
        local x, z = type(q) == "table" and tonumber(q[1]) or nil, type(q) == "table" and tonumber(q[2]) or nil
        if x == nil or z == nil then outline = nil break end
        outline[i] = { x, z }
      end
    end
    for bi, b in ipairs(boxes) do
      local function skip(x, z)
        for q = 1, bi - 1 do
          local ob = boxes[q]
          if x >= ob.x0 and x <= ob.x1 and z >= ob.z0 and z <= ob.z1 then return true end
        end
        return false
      end
      local bw, bd = b.x1 - b.x0, b.z1 - b.z0
      local edges = 2 * (bw + bd)
      if outline ~= nil then
        edges = 0
        for i = 1, #outline do
          local p, q = outline[i], outline[i % #outline + 1]
          edges = edges + sqrt((q[1] - p[1]) ^ 2 + (q[2] - p[2]) ^ 2)
        end
      end
      local count = t.spacing ~= nil and max(1, floor(edges / spacing + 0.5)) or nil
      local plan, samples, far = outdoor_plan(b, { after = after, height = height_opt, count = count,
        stock_post = stock and classes[POLE.class] ~= nil, skip = skip, outline = outline })
      if #plan == 0 then
        log.warn(api .. ": " .. name .. " has no edge or floor spot for an outdoor light, so it gets no light.")
      end
      -- Range and intensity: every floor sample in reach of its nearest light, and the median floor light at
      -- least 90 % of what the area rule's ceiling grid would give it.
      local range, inten = range_opt, 1
      if range == nil and #plan > 0 then
        local bh = b.y1 - (b.floor or b.y0)
        local gx, gz = area_grid(bw, bd, bh)
        local cw, cd = bw / gx, bd / gz
        local rr = max(KIT.defaults.light_range_min, 0.9 * sqrt(cw * cw + cd * cd + bh * bh))
        local ref = {}
        for iz = 1, gz do
          for ix = 1, gx do
            ref[#ref + 1] = { b.x0 + (ix - 0.5) * cw, (b.floor or b.y0) + min(bh - 1.0, POLE.light), b.z0 + (iz - 0.5) * cd, rr, 1 }
          end
        end
        local want = 0.9 * floor_light(samples, ref)
        local function with(rg, it)
          local ls = {}
          for i, f in ipairs(plan) do ls[i] = { f.l[1], f.l[2], f.l[3], rg, it } end
          return floor_light(samples, ls)
        end
        local fac = 1.25
        range = min(OUTDOOR.max_range, max(KIT.defaults.light_range_min, fac * far))
        while with(range, 1) < want and fac < 1.8 - 1e-9 and range < OUTDOOR.max_range do
          fac = fac + 0.05
          range = min(OUTDOOR.max_range, max(KIT.defaults.light_range_min, fac * far))
        end
        local got = with(range, 1)
        if got < want and got > 0 then inten = min(OUTDOOR.max_i, want / got) end
      end
      range = range or KIT.defaults.light_range_min
      for _, f in ipairs(plan) do
        local i = #lights + 1
        local lx, ly, lz, fx = nil, nil, nil, {}
        if fixture == false then
          lx, ly, lz = f.l[1], f.l[2], f.l[3]
        elseif f.kind == "wall" then
          lx, ly, lz, fx = emit_wall_lamp("light_room", name, i, f.wx, f.wz, f.nx, f.nz, f.ly, stock and classes[WALL_LAMP.class] ~= nil)
        else
          lx, ly, lz, fx = emit_post("light_room", name, i, f.x, f.gy, f.z, f.dx, f.dz, f.lh, f.stock)
        end
        local props = { color = color, size_range = range }
        if inten > 1 then props.on_intensity = floor(inten * 100 + 0.5) / 100 end
        lights[i] = level.place{ kind = "light", name = name .. "_light" .. i, pos = { lx, ly, lz }, props = props,
          tags = K.tags_for("light_room", name) }
        for _, h in ipairs(fx) do fixtures[#fixtures + 1] = h end
        -- The lamp's own glow, as the stock maps add short lights at their outdoor fixtures: a 5 m light on the wall
        -- under a wall lamp, or beside a post at 2 m (the pool of light at its foot).
        local ax, ay, az
        if f.kind == "wall" then
          ax, ay, az = f.wx + f.nx * 0.5, f.ly - 0.8, f.wz + f.nz * 0.5
        else
          ax, ay, az = f.x + f.dx * 0.5, f.gy + 2.0, f.z + f.dz * 0.5
        end
        if not K.solid_near(ax, ay, az, 0.2, after) then
          local a = level.place{ kind = "light", name = name .. "_glow" .. i, pos = { ax, ay, az },
            props = { color = color, size_range = OUTDOOR.glow_range }, tags = K.tags_for("light_room", name) }
          a:tag("kit.part", "glow")
          glows[#glows + 1] = a
        end
      end
    end
    lights.fixtures = fixtures
    lights.glows = glows
    return lights
  end

  -- Indoors. The fixture: a stock clutter class (tables loaded), else a detail lamp brush.
  local fclass, fdef = nil, nil
  if fixture == true or (type(fixture) == "string" and fixture ~= "brush") then
    if type(fixture) == "string" then
      if classes and not classes[fixture] then
        local near = catalog.nearest("Clutter", fixture, 3)
        K.fail(api, "'" .. fixture .. "' is not a clutter class of the loaded game tables.", #near > 0 and ("Nearest: " .. table.concat(near, ", ") .. ".") or nil)
      end
      fclass, fdef = fixture, FIXTURES[fixture] or { top = 0.1, drop = 0.4 }
    elseif classes then
      local list = th and th.fixtures or { "Double Mine Light 4", "Mine Light 8" }
      for _, c in ipairs(list) do
        local def = FIXTURES[c]
        if classes[c] and def and (def.min_height == nil or h >= def.min_height) then fclass, fdef = c, def break end
      end
    end
  end
  local drop = (fdef and fdef.drop) or 0.4
  -- The light's height over a floor with headroom hh (the floor and the ceiling are measured under and over
  -- each light: raised floors, ramps and lowered ceilings move the fixture with them).
  local function light_height(hh)
    if height_opt ~= nil then return height_opt end
    if fixture == false then return min(hh - 0.5, 0.75 * hh) end
    return hh - drop
  end

  -- The boxes to light: the room, or each leg of a multi-leg corridor (one row, a light every 4 m; a
  -- cell whose centre lies in an earlier leg, the shared corner, is skipped).
  local boxes = {}
  if legs then
    for _, g in ipairs(legs) do
      local gw, gd = g.x1 - g.x0, g.z1 - g.z0
      local gs = t.spacing ~= nil and spacing or 4
      local gx, gz = max(1, floor(gw / gs + 0.5)), max(1, floor(gd / gs + 0.5))
      if gw >= gd then gz = 1 else gx = 1 end
      boxes[#boxes + 1] = { b = g, nx = gx, nz = gz }
    end
  else
    boxes[1] = { b = r, nx = nx, nz = nz }
  end
  for bi, bx in ipairs(boxes) do
  local r, nx, nz = bx.b, bx.nx, bx.nz
  local floor0 = r.floor or r.y0
  local bh = r.y1 - floor0
  local cw, cd = (r.x1 - r.x0) / nx, (r.z1 - r.z0) / nz
  local rcx, rcz = (r.x0 + r.x1) / 2, (r.z0 + r.z1) / 2
  local wall = min(clearance, cw / 2, cd / 2)
  local range = range_opt or max(KIT.defaults.light_range_min, 0.9 * sqrt(cw * cw + cd * cd + bh * bh))
  local y = floor0 + light_height(bh)
  for iz = 1, nz do
    for ix = 1, nx do
      local x0, z0 = r.x0 + (ix - 1) * cw, r.z0 + (iz - 1) * cd
      local x1, z1 = x0 + cw, z0 + cd
      local shared = false
      for q = 1, bi - 1 do
        local o = boxes[q].b
        local mx, mz = x0 + cw / 2, z0 + cd / 2
        if mx >= o.x0 and mx <= o.x1 and mz >= o.z0 and mz <= o.z1 then shared = true end
      end
      local px, pz = nil, nil
      -- The light height over the cell's own floor and under its own ceiling (a platform, a ramp or a lowered
      -- ceiling over the cell moves it), measured at the cell centre.
      local cy0 = y
      if not shared then
        local g0 = K.ground_point(x0 + cw / 2, z0 + cd / 2, r.y1 - 0.05)
        if g0.y ~= nil and g0.y >= r.y0 - 0.5 and g0.y <= r.y1 - 2 then
          local c0 = K.ceiling_point(x0 + cw / 2, z0 + cd / 2, g0.y + 0.5)
          if c0 == nil or c0 > r.y1 + 0.5 then c0 = r.y1 end
          if c0 - g0.y >= 2.2 then cy0 = g0.y + light_height(c0 - g0.y) end
        end
        px, pz = K.free_spot(x0 + cw / 2, cy0, z0 + cd / 2,
          max(x0, r.x0 + wall), min(x1, r.x1 - wall), max(z0, r.z0 + wall), min(z1, r.z1 - wall),
          rcx, rcz, clearance, after)
      end
      -- The floor under the spot and the ceiling over it.
      local gy, cy, why = nil, nil, nil
      local function floor_ok(g) return g ~= nil and g >= r.y0 - 0.5 and g <= r.y1 - 2 end
      if px ~= nil then
        gy = K.ground_point(px, pz, cy0).y
        if not floor_ok(gy) then
          gy = floor0 -- the fixture hangs from the ceiling; a pit or a hole under it changes nothing
        end
        cy = K.ceiling_point(px, pz, gy + 0.5)
        if cy == nil or cy > r.y1 + 0.5 then cy = r.y1 end
        if cy - gy < 2.2 then why, px = "has too little headroom for a light", nil end
      end
      -- The light and its fixture, clear of solids.
      local ly = nil
      if px ~= nil then
        ly = (fixture ~= false and height_opt == nil) and (cy - drop) or (gy + light_height(cy - gy))
        if ly <= gy + 0.5 or K.solid_near(px, ly, pz, 0.2, after) then why, px = "is filled with solid at its light", nil end
      end
      if shared then
        -- lit by the earlier leg
      elseif px == nil then
        log.warn(api .. ": " .. name .. " cell " .. ix .. "," .. iz .. " " .. (why or ("is filled with solid at height " .. K.fmt(y - floor0) .. " m")) ..
          ", so it gets no light.")
      else
        local i = #lights + 1
        lights[i] = level.place{
          kind = "light", name = name .. "_light" .. i,
          pos = { px, ly, pz },
          props = { color = color, size_range = range },
          tags = K.tags_for("light_room", name),
        }
        if fixture ~= false then
          local f = nil
          if fclass then
            f = level.place{ kind = "clutter", class = fclass, pos = { px, cy - fdef.top - 0.01, pz }, name = name .. "_fixture" .. i,
              tags = K.tags_for("light_room", name) }
            f:tag("kit.part", "fixture")
          else
            -- A small lamp housing flush under the ceiling over the light; its lens (the bottom face) is full-bright.
            f = K.emit("light_room", name, "fixture", { shape = "box", pos = { px, cy - 0.075, pz }, size = { 0.8, 0.15, 0.8 }, detail = true },
              KIT.defaults.fixture_texture, nil)
            for _, fc in ipairs(f.faces) do fc:fit() end
            for _, fc in ipairs(f:faces_facing("down")) do fc:set_flag("full_bright", true) end
          end
          fixtures[#fixtures + 1] = f
        end
      end
    end
  end
  end
  lights.fixtures = fixtures
  return lights
end

-- A fixture for one kit.light_path light at (x, y, z) on the path, with the unit side (sx, sz) toward the wall it
-- may be mounted on and the path's width: a lamp on the wall when one stands within reach (the light moved in front
-- of it), else a detail lamp post at the walkway's edge (its head toward the path). Returns the light position and
-- the fixture handles, or nil when there is no room for either.
local function path_fixture(name, i, x, y, z, gy, sx, sz, width, after)
  local probe = P.shared()
  local classes = clutter_classes()
  local reach = width / 2 + 4
  local dw = probe:wall_distance(x, y, z, sx, sz, reach)
  if dw < reach and dw > 0.6 then
    local wx, wz = x + sx * dw, z + sz * dw
    local lx, lz = wx - sx * 0.42, wz - sz * 0.42
    if not K.solid_near(lx, y, lz, 0.2, after) then
      return emit_wall_lamp("light_path", name, i, wx, wz, -sx, -sz, y, classes ~= nil and classes[WALL_LAMP.class] ~= nil)
    end
  end
  -- No wall in reach: a post at the walkway's edge on this side, its head over the path.
  for _, sgn in ipairs({ 1, -1 }) do
    local off = max(0.8, min(width / 2 - 0.6, 3))
    local px, pz = x + sgn * sx * off, z + sgn * sz * off
    local g = K.ground_point(px, pz, y).y
    if g ~= nil and abs(g - gy) <= 1.5 and not K.solid_near(px, g + 1.2, pz, 0.35, after) then
      local lh = y - g
      local hx, hz = px - sgn * sx * DETAIL_ARM, pz - sgn * sz * DETAIL_ARM
      if lh >= 2.0 and not K.solid_near(hx, y, hz, 0.3, after) then
        return emit_post("light_path", name, i, px, g, pz, -sgn * sx, -sgn * sz, lh, false)
      end
    end
  end
  return nil
end

-- The installer: copies the functions above into the kit and keeps its helpers.
return function(target, internals)
  KIT, K = target, internals
  if target.defaults.fixture_texture == nil then target.defaults.fixture_texture = "mtl_lite03.tga" end
  target.dress = kit.dress
  target.light_room = kit.light_room
  internals.light_fixture = path_fixture
  internals.dress_space = new_space -- the generative fill's engine space (kit_modules.lua places through it too)
  target.themes = THEME_NAMES
end
