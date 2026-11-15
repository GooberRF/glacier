-- Glacier design kit: texture palettes, trims and UV helpers (installed into the kit by kit.lua; use
-- them through local kit = ged.import("kit")).
--
-- What exemplar levels do with textures (docs/internal/DESIGN-QUALITY-SPEC.md §(a.4), §(c.3)), and what
-- this module gives the kit:
--   * One texel density everywhere: RED maps every stock texture at 64 px/m (a 256 px texture repeats
--     every 4 m, 128 px every 2 m, 32 px every 0.5 m), and exemplars keep 4 m tiles on 256 px textures.
--     kit.defaults.ppm = 64 maps every kit brush that way; rock is no exception.
--   * Zone palettes: each zone keeps one coherent family per role (floor, wall, ceiling) plus trim,
--     posts, frames, an accent and hazard stripes; floor and wall textures never overlap. kit.palette.
--   * A wall kit: a base band 0.5-1.2 m tall, a top band under the ceiling, posts at panel joints every
--     4-6 m, frames around openings and hazard stripes on drop edges. kit.trim.
--   * Slope-aligned stairs and ramps: treads start their texture at the nosing, risers hang from it, and
--     a ramp's texture runs up the slope at true length (installed into kit.stairs and kit.ramp).
--   * Mapper-style alignment (kit.defaults.uv_align, level.uv_align): wall textures stand on their floor
--     line, wrap around corners and fit their height to whole repeats; rock folds across its facets at
--     true length; nothing reads mirrored (stock levels: 98 % of wall area one handedness). Every kit
--     part is aligned as it is built, organic pieces as a whole, and kit.build_layout re-aligns the whole
--     layout at the end so textures continue across neighbouring parts (trims and stair steps keep their
--     own fitted / nosing alignment).
--
-- The kit functions below are written at the left margin inside install() so the kit reference
-- generator (---@kit / ---@opt lines, ged.describe("kit")) reads them like the ones in kit.lua.

local M = {}

-- ---------------------------------------------------------------------------------------------
-- Palettes. Every name is a stock Red Faction texture (every player has it), chosen from what the
-- stock multiplayer levels use per role (tests/Ged.Core.Tests/Scripting/KitUvTests verifies each one
-- against a mounted install). Lists are ordered most-used first; floor and wall lists never share a
-- texture (floor/wall overlap target <= 0.15). Team frames and accents key red/blue halves.
-- ---------------------------------------------------------------------------------------------

local PALETTES = {
  industrial = {
    family = "mtl",
    floor = { "mtl_merc_floor01.tga", "mtl_merc_floor02.tga", "mtl_grayfloor01.tga", "mtl_grittymetal.tga" },
    wall = { "mtl_outerwall02.tga", "mtl_conveyor_wall01.tga", "mtl_industrialwall01.tga", "mtl_merc_black05.tga",
      "mtl_mercwall01.tga", "mtl_filthypanels01.tga", "mtl_filthypanels02.tga", "mtl_baseruststreaks.tga" },
    ceiling = { "mtl_dirtytechceiling01.tga", "mtl_merc_ceiling02.tga", "mtl_basedrkbrwn.tga" },
    trim = { "mtl_techtrim01.tga", "mtl_graytrim.tga" },
    post = { "mtl_girder07.tga", "mtl_girder06.tga" },
    frame = { "mtl_doortrim.tga" },
    accent = { "mtl_redpipe02_drt.tga", "mtl_baseyell01.tga" },
    stripe = { "mtl_hazardstripe01.tga", "mtl_nat_hazard.tga" },
    red = { frame = "mtl_doortrim_red01.tga", accent = "mtl_ctf03_red.tga" },
    blue = { frame = "mtl_doortrim_blue01.tga", accent = "mtl_ctf03_blu.tga" },
  },
  bunker = {
    family = "cem",
    floor = { "cem_dark_floor01.tga", "cem_plainyel.tga", "cem_stainedyell01.tga", "cem_dark_asphalt01.tga" },
    wall = { "cem_bunker04.tga", "cem_cemblu02.tga", "cem_bunker01.tga", "cem_bunker05.tga", "cem_poured04.tga",
      "cem_poured05.tga", "cem_greypanel02.tga", "cem_cement_panel06.tga", "cem_lvl13dirty02.tga" },
    ceiling = { "cem_cementflr01.tga", "cem_darktile01.tga", "cem_poured02_drt.tga" },
    trim = { "mtl_graytrim.tga", "mtl_techtrim01.tga" },
    post = { "mtl_girder06.tga", "mtl_girder07.tga" },
    frame = { "mtl_doortrim.tga" },
    accent = { "cem_blue01.tga", "cem_lvl13dirty07.tga" },
    stripe = { "mtl_hazardstripe01.tga", "mtl_nat_hazard.tga" },
    red = { frame = "cem_doortrim_red01.tga", accent = "cem_bunker04_red.tga" },
    blue = { frame = "cem_doortrim_blue01.tga", accent = "cem_bunker04_blue.tga" },
  },
  admin = {
    family = "sld",
    floor = { "sld_adminfloor01.tga", "sld_adminfloor02.tga", "sld_adminfloor05.tga", "sld_admin_carpet03.tga" },
    wall = { "sld_admin_new01.tga", "sld_admin_new02.tga", "sld_admin_wall005.tga", "sld_admin_wall001.tga",
      "sld_admin_new03.tga", "sld_admin_new08.tga", "sld_admin_wall004.tga", "sld_executive_area01.tga" },
    ceiling = { "sld_admin_ceiling01.tga", "sld_admin_plain01.tga", "pls_ceiling01.tga" },
    trim = { "sld_admin_trim.tga", "mtl_chrome_trim01.tga" },
    post = { "sld_cleanpanel03_mrbl.tga", "mtl_girder07.tga" },
    frame = { "mtl_med_door_trim01.tga" },
    accent = { "sld_cleanpanel03_mrbl.tga", "mtl_baseyell01.tga" },
    stripe = { "mtl_hazardstripe01.tga" },
    red = { frame = "mtl_doortrim_red01.tga", accent = "cem_bunker04_red.tga" },
    blue = { frame = "mtl_doortrim_blue01.tga", accent = "cem_bunker04_blue.tga" },
  },
  cave = {
    family = "rck",
    floor = { "rck_cavernflr01.tga", "rck_tanrock01_flr.tga", "rck_brownflr01.tga", "rck_redsonya_flr.tga" },
    wall = { "rck_canyon_rock03.tga", "rck_canyon_rock02.tga", "rck_canyon_rock01.tga", "rck_redsoyna01.tga",
      "rck_redsoyna02.tga", "rck_brown01.tga", "rck_brown09.tga" },
    ceiling = { "rck_coal01.tga", "rck_drill_wall01.tga" },
    trim = { "sld_woodpost01.tga", "mtl_girder07.tga" },
    post = { "sld_woodpost01.tga", "mtl_girder07.tga" },
    frame = { "sld_woodpost01.tga" },
    accent = { "mtl_baserust03.tga", "mtl_redpipe02_drt.tga" },
    stripe = { "mtl_nat_hazard.tga", "mtl_hazardstripe01.tga" },
    red = { frame = "mtl_doortrim_red01.tga", accent = "cem_bunker04_red.tga" },
    blue = { frame = "mtl_doortrim_blue01.tga", accent = "cem_bunker04_blue.tga" },
  },
  marsbase = {
    family = "mtl",
    floor = { "mtl_merc_floor01.tga", "mtl_grittymetal.tga", "rck_redsonya_flr.tga", "rck_comflr01.tga" },
    wall = { "rck_redsoyna01.tga", "mtl_outerwall02.tga", "rck_redsoyna02.tga", "mtl_merc_black05.tga",
      "mtl_conveyor_wall01.tga", "rck_brown01.tga" },
    ceiling = { "mtl_dirtytechceiling01.tga", "rck_coal01.tga" },
    trim = { "mtl_techtrim01.tga", "mtl_graytrim.tga" },
    post = { "mtl_girder07.tga", "mtl_girder06.tga" },
    frame = { "mtl_doortrim.tga" },
    accent = { "mtl_redpipe02_drt.tga", "mtl_baseyell01.tga" },
    stripe = { "mtl_hazardstripe01.tga", "mtl_nat_hazard.tga" },
    red = { frame = "mtl_doortrim_red01.tga", accent = "mtl_ctf03_red.tga" },
    blue = { frame = "mtl_doortrim_blue01.tga", accent = "mtl_ctf03_blu.tga" },
  },
}

-- Other names a brief may use for the same palettes.
local ALIASES = { tech = "admin", office = "admin", rock = "cave", mine = "cave", concrete = "bunker", mars = "marsbase" }

local ROLES = { "floor", "wall", "ceiling", "trim", "post", "frame", "accent", "stripe" }

-- Opposite directions, for the stair nosing (the edge of a tread toward the foot of the stairs).
local OPPOSITE = { ["+x"] = "-x", ["-x"] = "+x", ["+z"] = "-z", ["-z"] = "+z" }

local function copy(list)
  local out = {}
  for i, v in ipairs(list) do out[i] = v end
  return out
end

--- Installs kit.palette and kit.trim on `kit` (H = the kit's private helpers) and returns the hooks kit.lua
--- calls from its own builders: stairs(brushes, dir), ramp(brush), rock(t, name).
function M.install(kit, H)
  local fail, check_keys, number, positive, boolean = H.fail, H.check_keys, H.number, H.positive, H.boolean
  local EPS, fmt = H.EPS, H.fmt

  -- The pixels per metre a face maps at: kit.defaults.ppm, else the texture's pixel width over
  -- kit.defaults.tile_metres (256 px when the size is unknown, like brush:box_map).
  local function ppm_of(face)
    if kit.defaults.ppm then return kit.defaults.ppm end
    local info = catalog.texture_info(face.texture)
    local w = (info and info.width > 0) and info.width or 256
    return w / kit.defaults.tile_metres
  end

  local hooks = {}

  local function aligning() return kit.defaults.box_map and kit.defaults.uv_align and kit.defaults.ppm end

  -- Stairs: the steps' sides wrap as one wall standing on the stair's foot; then each tread's texture
  -- stands on its nosing (U across the width, V up the climb) and each riser hangs from it, at the
  -- kit's texel density (true length, no box-map stretch).
  function hooks.stairs(brushes, dir)
    if not kit.defaults.box_map then return end
    if aligning() then level.uv_align{ brushes = brushes, ppm = kit.defaults.ppm, keep_fitted = false } end
    local dx, dz = 0, 0
    if dir == "+x" then dx = 1 elseif dir == "-x" then dx = -1 elseif dir == "+z" then dz = 1 else dz = -1 end
    for _, b in ipairs(brushes) do
      local part = b:tags()["kit.part"] or ""
      if part:sub(1, 4) == "step" then
        for _, f in ipairs(b.faces) do
          local n = f.normal
          if n.y > 0.98 then
            f:align_to_edge(OPPOSITE[dir], 0, ppm_of(f))
          elseif n.x * dx + n.z * dz < -0.98 then
            f:align_to_edge("top", 0, ppm_of(f))
          end
        end
      end
    end
  end

  -- Ramps: the slope's texture stands on its foot edge and runs up the slope at true length.
  function hooks.ramp(brush)
    if not kit.defaults.box_map then return end
    for _, f in ipairs(brush.faces) do
      local ny = f.normal.y
      if ny > 0.2 and ny < 0.98 then f:align_to_edge("bottom", 0, ppm_of(f)) end
    end
  end

  -- Organic pieces: the geo builders box-map at a tile size, so re-map them by pixels per metre (each
  -- texture's own size) unless the call or kit.defaults.rock_tile_metres asked for a tile.
  function hooks.rock(t, name)
    if not kit.defaults.box_map or not kit.defaults.ppm or t.tile ~= nil or kit.defaults.rock_tile_metres ~= nil then return end
    local q = level.brushes_tagged("kit.name", name)
    q:box_map{ ppm = kit.defaults.ppm }
    -- One piece of rock: the texture folds across every facet of every brush at true length.
    if kit.defaults.uv_align then q:uv_align{ ppm = kit.defaults.ppm, keep_fitted = false } end
  end

  -- The end of a layout build: align every kit part together so textures run on across neighbouring
  -- parts (a room's wall into the corridor beside it). Trims keep their fitted strips, stair steps their
  -- nosing alignment, dressing its own per-prop mapping, and the room detail (library modules and their
  -- hand-authored stand-ins) the mapping it was placed with.
  local KEEP = { trim = true, dress = true, light_room = true, wall_detail = true, frame_opening = true, ceiling_detail = true,
    machinery = true, railing = true, catwalk = true, pipe_run = true, stair_detail = true, rock_detail = true }
  function hooks.finish()
    if not aligning() then return end
    level.brushes_tagged("kit"):where(function(b)
      local tg = b:tags()
      if KEEP[tg.kit] then return false end
      return not (tg.kit == "stairs" and (tg["kit.part"] or ""):sub(1, 4) == "step")
    end):uv_align{ ppm = kit.defaults.ppm }
  end

  local build_layout = kit.build_layout
  if build_layout ~= nil then
    kit.build_layout = function(t)
      local r = build_layout(t)
      hooks.finish()
      return r
    end
  end

---@kit kit.palette{theme, variant?, team?} → palette — A coherent set of stock textures for one zone: lists by role (floor, wall, ceiling, trim, post, frame, accent, stripe; floor and wall never share a texture), the role table one room uses (textures, for kit.room), and the trim set (trims, for kit.trim). Give each zone its own theme or variant.
---@opt theme: string — Required: "industrial", "bunker", "admin", "cave" or "marsbase" (also "tech", "office", "rock", "mine", "concrete", "mars").
---@opt variant: integer = 1 — Which entry of each list textures and trims pick (wrapping around): rooms of one zone with different variants share the family but not the exact textures.
---@opt team: string = neutral — "red" or "blue": team-keyed frame and accent textures, so the two halves of a team map read apart.
function kit.palette(t)
  local api = "kit.palette"
  check_keys(api, t, { "theme", "variant", "team" })
  if type(t.theme) ~= "string" then
    local names = {}
    for k in pairs(PALETTES) do names[#names + 1] = k end
    table.sort(names)
    fail(api, "'theme' is required (a palette name).", "Themes: " .. table.concat(names, ", ") .. ".")
  end
  local theme = ALIASES[t.theme:lower()] or t.theme:lower()
  local p = PALETTES[theme]
  if p == nil then
    local names = {}
    for k in pairs(PALETTES) do names[#names + 1] = k end
    table.sort(names)
    fail(api, "unknown theme '" .. t.theme .. "'.", "Themes: " .. table.concat(names, ", ") .. " (aliases: tech, office, rock, mine, concrete, mars).")
  end
  local variant = number(api, "variant", t.variant, 1)
  if variant < 1 or variant ~= math.floor(variant) then fail(api, "'variant' must be a whole number of at least 1.") end
  if t.team ~= nil and t.team ~= "red" and t.team ~= "blue" then fail(api, "'team' must be \"red\" or \"blue\" (omit it for a neutral zone).") end

  local out = { kind = "palette", theme = theme, family = p.family, variant = variant, team = t.team }
  for _, role in ipairs(ROLES) do out[role] = copy(p[role]) end
  if t.team ~= nil then
    local keyed = p[t.team]
    table.insert(out.frame, 1, keyed.frame)
    table.insert(out.accent, 1, keyed.accent)
  end
  local function pick(role, k) local list = out[role] return list[((variant - 1 + (k or 0)) % #list) + 1] end
  out.textures = { floor = pick("floor"), wall = pick("wall"), ceiling = pick("ceiling") }
  out.trims = {
    base = pick("trim"), top = pick("trim", 1), post = pick("post"),
    frame = t.team ~= nil and out.frame[1] or pick("frame"), stripe = out.stripe[1],
  }
  return out
end

  -- ---- kit.trim helpers -------------------------------------------------------------------------

  -- The walls of a box record: for each, the axis it runs along, its span, the plane and inward sign.
  local function walls_of(r)
    return {
      { side = "-x", along = "z", a = r.z0, b = r.z1, plane = r.x0, sign = 1 },
      { side = "+x", along = "z", a = r.z0, b = r.z1, plane = r.x1, sign = -1 },
      { side = "-z", along = "x", a = r.x0, b = r.x1, plane = r.z0, sign = 1 },
      { side = "+z", along = "x", a = r.x0, b = r.x1, plane = r.z1, sign = -1 },
    }
  end

  -- Air brushes (other than the room's own) that reach through a wall: each is an opening
  -- {a, b, y0, y1} along the wall, clipped to it.
  local function openings_of(r, wall, own)
    local probe = 0.05
    local box
    if wall.along == "z" then
      local x = wall.plane - wall.sign * probe
      box = { min = { math.min(x, wall.plane), r.floor, r.z0 }, max = { math.max(x, wall.plane), r.y1, r.z1 } }
    else
      local z = wall.plane - wall.sign * probe
      box = { min = { r.x0, r.floor, math.min(z, wall.plane) }, max = { r.x1, r.y1, math.max(z, wall.plane) } }
    end
    local list = {}
    for _, b in ipairs(level.brushes_in(box).brushes) do
      if b.air and not own[b.uid] then
        local lo, hi = b.bounds.min, b.bounds.max
        -- It must extend beyond the wall plane (into the solid), not merely touch it from inside.
        local reach_lo = wall.along == "z" and lo.x or lo.z
        local reach_hi = wall.along == "z" and hi.x or hi.z
        local outside
        if wall.sign > 0 then outside = reach_lo < wall.plane - EPS else outside = reach_hi > wall.plane + EPS end
        if outside then
          local a = wall.along == "z" and lo.z or lo.x
          local c = wall.along == "z" and hi.z or hi.x
          a, c = math.max(a, wall.a), math.min(c, wall.b)
          local y0, y1 = math.max(lo.y, r.floor), math.min(hi.y, r.y1)
          if c - a > 0.1 and y1 - y0 > 0.1 then list[#list + 1] = { a = a, b = c, y0 = y0, y1 = y1 } end
        end
      end
    end
    table.sort(list, function(p, q) return p.a < q.a end)
    return list
  end

  -- The parts of [a, b] not covered by the openings that overlap the height band [y0, y1].
  local function free_spans(a, b, openings, y0, y1)
    local spans, at = {}, a
    for _, o in ipairs(openings) do
      if o.y0 < y1 - EPS and o.y1 > y0 + EPS and o.b > at then
        if o.a > at + EPS then spans[#spans + 1] = { at, math.min(o.a, b) } end
        at = math.max(at, o.b)
      end
    end
    if b > at + EPS then spans[#spans + 1] = { at, b } end
    return spans
  end

  local function overlaps_opening(c0, c1, openings, margin)
    for _, o in ipairs(openings) do
      if c1 > o.a - margin and c0 < o.b + margin then return true end
    end
    return false
  end

---@kit kit.trim{room, name?, base?, top?, posts?, post_width?, depth?, frames?, stripes?, palette?, textures?, group?} → trim — The wall kit for a room: a base band, a top band under the ceiling, posts at panel joints, frames around every opening through its walls and hazard stripes on its drop edges, as thin detail brushes with UVs fitted to each strip. Build it after the room's openings and the solids standing in it.
---@opt room: record — Required: a kit room or corridor record (its min/max box); openings are the air brushes that reach through its walls.
---@opt name: string = trim<n> — Names the trim (tags, group).
---@opt base: number|false = 0.75 — Base band height in metres (exemplar wall kits: 0.5 to 1.2); false = none.
---@opt top: number|false = 0.4 — Top band height under the ceiling in metres; false = none.
---@opt posts: number|false = 5 — Post spacing along each wall in metres (exemplars: a post or frame every 4 to 6 m); false = none.
---@opt post_width: number = 0.4 — Post width in metres.
---@opt depth: number = 0.1 — How far the bands stand out from the wall, metres (posts and frames stand 0.05 m further).
---@opt frames: boolean = true — Frame every opening through the walls (jambs and a header; a sill under a raised opening).
---@opt stripes: boolean|record[] = true — Hazard stripes along the drop edges of the kit.platform tops standing in the room (or of the given platform records or brushes): tops at least kit.limits.max_riser above the floor, on every side not against a wall or met by stairs, a ramp or another solid within a step.
---@opt palette: palette = kit.palette{theme = "industrial"} — The palette whose trims set supplies the textures.
---@opt textures: {base=, top=, post=, frame=, stripe=} = the palette's trims — Textures per trim part (any subset; the rest come from the palette).
---@opt group: boolean = true — Group the trim's brushes under its name.
function kit.trim(t)
  local api = "kit.trim"
  check_keys(api, t, { "room", "name", "base", "top", "posts", "post_width", "depth", "frames", "stripes", "palette", "textures", "group" })
  local r = H.record_box(api, "room", t.room)
  if type(t.room.brush) ~= "userdata" then
    fail(api, "'room' must be a single-box record: a kit.room or a straight kit.corridor (a path corridor has several boxes).",
      "Trim each box of a bent corridor separately, or give a room.")
  end
  local name = H.name_for(api, "trim", t.name)
  local function height_or_false(key, v, default)
    if v == false then return nil end
    return positive(api, key, v, default)
  end
  local base = height_or_false("base", t.base, 0.75)
  local top = height_or_false("top", t.top, 0.4)
  local spacing = height_or_false("posts", t.posts, 5)
  local pw = positive(api, "post_width", t.post_width, 0.4)
  local depth = positive(api, "depth", t.depth, 0.1)
  local frames = boolean(api, "frames", t.frames, true)
  local stripes = t.stripes
  if stripes == nil then stripes = true end
  if type(stripes) ~= "boolean" and type(stripes) ~= "table" then
    fail(api, "'stripes' must be true, false or a list of platform records or brushes.")
  end
  local room_h = r.y1 - r.floor
  if base and (base < 0.5 - EPS or base > 1.2 + EPS) then
    log.warn(api .. ": base band " .. fmt(base) .. " m is outside the exemplar wall-kit range 0.5 to 1.2 m.")
  end
  if spacing and (spacing < 4 - EPS or spacing > 6 + EPS) then
    log.warn(api .. ": posts every " .. fmt(spacing) .. " m; exemplar wall kits put a post or frame every 4 to 6 m.")
  end
  if (base or 0) + (top or 0) > room_h - kit.limits.min_headroom + EPS then
    fail(api, "the bands (" .. fmt((base or 0) + (top or 0)) .. " m together) leave less than " .. kit.limits.min_headroom ..
      " m of plain wall in the " .. fmt(room_h) .. " m room.", "Lower base or top, or pass false for one of them.")
  end
  if depth > 1 then fail(api, "'depth' " .. fmt(depth) .. " m is not a trim (at most 1 m).") end

  local pal = t.palette
  if pal ~= nil and (type(pal) ~= "table" or pal.kind ~= "palette") then fail(api, "'palette' must be a kit.palette record.") end
  pal = pal or kit.palette{ theme = "industrial" }
  local tex = {}
  for k, v in pairs(pal.trims) do tex[k] = v end
  if t.textures ~= nil then
    if type(t.textures) ~= "table" then fail(api, "'textures' must be a table {base=, top=, post=, frame=, stripe=}.") end
    check_keys(api .. " textures", t.textures, { "base", "top", "post", "frame", "stripe" })
    for k, v in pairs(t.textures) do
      if type(v) ~= "string" or v == "" then fail(api, "textures." .. k .. " must be a texture name.") end
      tex[k] = v
    end
  end

  local own = { [t.room.brush.uid] = true }

  local brushes = {}
  local counts = { bands = 0, posts = 0, frames = 0, stripes = 0, openings = 0 }
  -- One thin detail box against a wall: [s0, s1] along it, [y0, y1] high, d deep into the room.
  local function piece(part, wall, s0, s1, y0, y1, d, texture, axes)
    if s1 - s0 < 0.05 or y1 - y0 < 0.02 then return end
    local x0, x1, z0, z1
    local inner = wall.plane + wall.sign * d
    if wall.along == "z" then
      x0, x1 = math.min(wall.plane, inner), math.max(wall.plane, inner)
      z0, z1 = s0, s1
    else
      z0, z1 = math.min(wall.plane, inner), math.max(wall.plane, inner)
      x0, x1 = s0, s1
    end
    local b = H.emit("trim", name, part, {
      shape = "box", pos = { (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2 }, size = { x1 - x0, y1 - y0, z1 - z0 }, detail = true,
    }, texture, nil)
    b:fit_faces{ axes = axes, facing = "side" }
    -- A post, jamb or header in a grained texture (a girder, a door trim) carries its grain along its length, one
    -- repeat at most across it, as a mapper turns a girder texture up a post; bands fitted along the wall stay.
    if kit.defaults.uv_align then level.uv_align{ brushes = b, ppm = kit.defaults.ppm or 64, keep_fitted = false, members = "only" } end
    brushes[#brushes + 1] = b
    return b
  end

  for _, wall in ipairs(walls_of(r)) do
    local openings = openings_of(r, wall, own)
    counts.openings = counts.openings + #openings
    -- The x-running walls' bands stop short of the corners, where the z-running walls' bands are.
    local a, b = wall.a, wall.b
    if wall.along == "z" then a, b = a + depth, b - depth end
    if base then
      for i, s in ipairs(free_spans(a, b, openings, r.floor, r.floor + base)) do
        if piece("base" .. wall.side .. i, wall, s[1], s[2], r.floor, r.floor + base, depth, tex.base, "v") then counts.bands = counts.bands + 1 end
      end
    end
    if top then
      for i, s in ipairs(free_spans(a, b, openings, r.y1 - top, r.y1)) do
        if piece("top" .. wall.side .. i, wall, s[1], s[2], r.y1 - top, r.y1, depth, tex.top, "v") then counts.bands = counts.bands + 1 end
      end
    end
    if spacing then
      local len = wall.b - wall.a
      local n = math.max(1, math.floor(len / spacing + 0.5))
      for k = 1, n - 1 do
        local c = wall.a + k * len / n
        -- Clear of every opening and its frame (0.25 m) by at least half a metre.
        if not overlaps_opening(c - pw / 2, c + pw / 2, openings, 0.75) then
          if piece("post" .. wall.side .. k, wall, c - pw / 2, c + pw / 2, r.floor, r.y1, depth + 0.05, tex.post, "u") then
            counts.posts = counts.posts + 1
          end
        end
      end
    end
    if frames then
      local fw = 0.25
      for i, o in ipairs(openings) do
        if o.a - fw >= wall.a - EPS and o.b + fw <= wall.b + EPS then
          local head = o.y1 < r.y1 - EPS and math.min(o.y1 + fw, r.y1) or o.y1
          local id = wall.side .. i
          piece("jamb" .. id .. "a", wall, o.a - fw, o.a, o.y0, head, depth + 0.05, tex.frame, "u")
          piece("jamb" .. id .. "b", wall, o.b, o.b + fw, o.y0, head, depth + 0.05, tex.frame, "u")
          if head > o.y1 + EPS then piece("header" .. id, wall, o.a - fw, o.b + fw, o.y1, head, depth + 0.05, tex.frame, "v") end
          if o.y0 > r.floor + fw - EPS then piece("sill" .. id, wall, o.a - fw, o.b + fw, o.y0 - fw, o.y0, depth + 0.05, tex.frame, "v") end
          counts.frames = counts.frames + 1
        end
      end
    end
  end

  if stripes then
    -- Walkable tops standing in the room: solids later than the room's air, with a flat top at least
    -- kit.limits.max_riser above the floor, standing headroom above it and a walkable footprint.
    local after = t.room.brush.time_index
    local band = 0.2
    local candidates = {}
    if stripes == true then
      for _, b in ipairs(level.brushes_in{ min = { r.x0, r.floor, r.z0 }, max = { r.x1, r.y1, r.z1 } }.brushes) do
        if b:tags().kit == "platform" then candidates[#candidates + 1] = b end
      end
    else
      for i, rec in ipairs(stripes) do
        if type(rec) == "userdata" then candidates[#candidates + 1] = rec
        elseif type(rec) == "table" and type(rec.brushes) == "table" then
          for _, b in ipairs(rec.brushes) do candidates[#candidates + 1] = b end
        else
          fail(api, "'stripes[" .. i .. "]' must be a platform record or a brush.")
        end
      end
    end
    for _, b in ipairs(candidates) do
      if b.solid and b.time_index > after and b.mover == nil then
        local lo, hi = b.bounds.min, b.bounds.max
        local topy = hi.y
        local flat = false
        for _, f in ipairs(b:faces_facing("up")) do
          if math.abs(f.center.y - topy) < 1e-3 and f.normal.y > 0.999 and f.area >= 0.9 * (hi.x - lo.x) * (hi.z - lo.z) then flat = true end
        end
        if flat and topy >= r.floor + kit.limits.max_riser - EPS and r.y1 - topy >= kit.limits.min_headroom - EPS
            and math.min(hi.x - lo.x, hi.z - lo.z) >= kit.limits.min_clear_width - EPS then
          -- Each side of the top is a drop edge unless it is against a wall or another solid reaches
          -- within a step of the top there (stairs or a ramp arriving, a neighbouring ledge).
          local sides = {
            { side = "-x", along = "z", plane = lo.x, a = lo.z, b = hi.z, sign = -1 },
            { side = "+x", along = "z", plane = hi.x, a = lo.z, b = hi.z, sign = 1 },
            { side = "-z", along = "x", plane = lo.z, a = lo.x, b = hi.x, sign = -1 },
            { side = "+z", along = "x", plane = hi.z, a = lo.x, b = hi.x, sign = 1 },
          }
          for _, s in ipairs(sides) do
            local at_wall = (s.side == "-x" and lo.x <= r.x0 + 0.05) or (s.side == "+x" and hi.x >= r.x1 - 0.05)
              or (s.side == "-z" and lo.z <= r.z0 + 0.05) or (s.side == "+z" and hi.z >= r.z1 - 0.05)
            local reached = false
            if not at_wall then
              local probe = s.plane + s.sign * 0.05
              local qmin = s.along == "z" and { math.min(s.plane, probe), topy - kit.limits.max_riser, s.a + 0.05 } or { s.a + 0.05, topy - kit.limits.max_riser, math.min(s.plane, probe) }
              local qmax = s.along == "z" and { math.max(s.plane, probe), topy, s.b - 0.05 } or { s.b - 0.05, topy, math.max(s.plane, probe) }
              for _, o in ipairs(level.brushes_in{ min = qmin, max = qmax }.brushes) do
                if o.uid ~= b.uid and o.solid and (o:tags().kit ~= "trim") and o.time_index > after then reached = true end
              end
            end
            if not at_wall and not reached then
              local w = { along = s.along, plane = s.plane, sign = s.sign }
              if piece("stripe" .. counts.stripes + 1, w, s.a, s.b, topy - band, topy, 0.02, tex.stripe, "v") then
                counts.stripes = counts.stripes + 1
              end
            end
          end
        end
      end
    end
  end

  local group = H.group_if_many(name, brushes, boolean(api, "group", t.group, true))
  return {
    kind = "trim", name = name, brushes = brushes, group = group, textures = tex,
    bands = counts.bands, posts = counts.posts, frames = counts.frames, stripes = counts.stripes, openings = counts.openings,
  }
end

  return hooks
end

return M
