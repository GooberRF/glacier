-- Glacier design kit: population — item economy (kit.items) and protected spawns (kit.spawn_points).
--
-- Installed into the kit by kit.lua (ged.import("kit_items")(kit, internals)). Both functions follow
-- the kit's rules (one option table, unknown keys rejected, tagged named objects, an explicit seed).
-- Both plan in the kit's engine (ged.import("kit_core"), C#) and place what it returns here.
--
-- Targets (docs/internal/DESIGN-QUALITY-SPEC.md §a.3, §c.2-2/3, §h; exemplar p25-p75):
--   items 1.6 per 100 m^2 up to 4,500 m^2 of floor, easing on bigger levels (never under 1.0; the exemplars' item
--   counts grow with area^0.58-0.77), never 6 or more within 10 m of a new one (weapons 31 %, ammo 34 %, health 17 %, armour 14 %, powerups 3 %: the
--   exemplar medians); weapon/powerup nearest neighbour 7-10 m, spread over the zones; every spawn
--   3.4-5.4 m from an item; powerups in contested spaces (traffic percentile >= 0.6), never in pockets;
--   health and armour on flanks. Spawns: nearest neighbour 8-15 m, at most 20 % of the pairs within
--   30 m see each other, exposure <= 0.2, beside cover or in alcoves, facing a way out, spread over
--   >= 60 % of the zones. Payload: health and ammo on the flanks of the cart route and at the
--   defenders' holds; spawn groups advance per checkpoint (spawn_points{checkpoint = ...}).
-- Class names are stock items.tbl classes; each ammo class is the one items.tbl gives "$Ammo For:"
-- that weapon.
--
-- This module only defines functions and constants at its top level.

local P = ged.import("kit_probe")
local CORE = ged.import("kit_core")

local floor, ceil, sqrt, abs, min, max = math.floor, math.ceil, math.sqrt, math.abs, math.min, math.max
local PITCH = P.PITCH

local KIT, K
local kit = {}

-- Weapons with their ammo (items.tbl $Ammo For), in the order a level adds them; rare = at most one
-- per level and only in a contested spot.
local WEAPONS = {
  { "Shotgun", "10gauge_ammo" },
  { "Assault Rifle", "5.56mm_ammo" },
  { "Machine Pistol", "12mm_ammo" },
  { "Sniper Rifle", ".50cal_ammo", rare = true },
  { "rocket launcher", "rocket_launcher_ammo", rare = true },
  { "heavy machine gun", "7.62mm_ammo" },
  { "grenades", "grenades" },
  { "scope assault rifle", "explosive 5.56mm rounds" },
  { "flamethrower", "Napalm" },
  { "rail gun", "railgun_bolts", rare = true },
}
local HEALTH = { "Medical Kit", "First Aid Kit" }
local ARMOR = { "Suit Repair" }
local POWERUPS = { "Multi Damage Amplifier", "Multi Super Armor", "Multi Super Health", "Multi Invulnerability" }
local SHARE = { weapon = 0.31, ammo = 0.34, health = 0.17, armor = 0.14, powerup = 0.03 }

local GAMETYPES = { dm = true, tdm = true, ctf = true, koth = true, cp = true, payload = true }

local function seed_of(api, t)
  local seed = t.seed
  if seed == nil then return 1 end
  if type(seed) ~= "number" or seed ~= floor(seed) then K.fail(api, "'seed' must be an integer (got " .. tostring(seed) .. ").") end
  return seed
end

local function gametype_of(api, g)
  if g == nil then return "dm" end
  if type(g) ~= "string" or not GAMETYPES[g:lower()] then
    K.fail(api, "'gametype' must be \"dm\", \"tdm\", \"ctf\", \"koth\", \"cp\" or \"payload\" (got " .. tostring(g) .. ").")
  end
  return g:lower()
end

local function points_of(api, key, list)
  if list == nil then return nil end
  if type(list) ~= "table" or #list == 0 then K.fail(api, "'" .. key .. "' must list points {x, y, z}.") end
  local out = {}
  for i, p in ipairs(list) do
    local x, y, z = nil, nil, nil
    if type(p) == "userdata" and p.pos ~= nil then x, y, z = p.pos.x, p.pos.y, p.pos.z
    else x, y, z = K.vec3(api, key .. "[" .. i .. "]", p) end
    out[i] = { x = x, y = y, z = z }
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- The spaces of a call as plain boxes for the planners.
-- ---------------------------------------------------------------------------------------------

local function boxes_of(spaces)
  local out = {}
  for i, s in ipairs(spaces) do
    out[i] = { name = s.name, kind = s.kind, x0 = s.x0, y0 = s.y0, z0 = s.z0, x1 = s.x1, y1 = s.y1, z1 = s.z1 }
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- kit.spawn_points (v3)
-- ---------------------------------------------------------------------------------------------

---@kit kit.spawn_points{room?|rooms?|area?, count, team?, margin?, name?, seed?, checkpoint?, spacing?} → object[] — count multiplayer respawn points spread over the spaces (by floor area, at least one per space while they last), each beside cover or in an alcove, clear of doorways, facing the nearest way out (the doorway when in sight, else the longest open heading), nearest neighbour 8–15 m where the spaces allow, chosen for low exposure and few spawns in sight within 30 m — measured exactly as the design grader measures them (spatial.spawn_report, G19: exposure = share of the playable floor within 40 m the eye 1.6 m above the spawn sees), on the compiled level (a private sealed build when the brushes changed since the last build), on the grader's own floor set and samples, so a populated level meets G19 by construction where its spaces allow. 1 m above the floor. The list's report field: nn_min, nn_median, los_pairs_within30, exposure_median, exposure_max and measure (what was measured).
---@opt room: record|string — One space: a kit record, a kit.name or a kit.build_layout zone's name (give one of room, rooms, area; none = every air space).
---@opt rooms: (record|string)[] — Several spaces: the spawns are spread over them (at least 60 % of them get one when count allows).
---@opt area: {min=, max=} — A box: every floor inside it.
---@opt count: integer — Required: number of respawn points (at least 1).
---@opt team: string = both teams — "red" or "blue" for a team-only spawn group.
---@opt margin: number = kit.defaults.spawn_margin (1.5) — Clear floor around each spawn (from walls and solids), metres.
---@opt name: string = the space's name — Prefix of the spawns' names (<name>_spawn<i>) and their kit.name tag.
---@opt seed: integer = 1 — The random stream for ties (and for the exposure samples when the level cannot be built).
---@opt checkpoint: object|number = none — A Payload_Checkpoint event (or its UID) the group serves: the spawns are linked to it, so they are live while that checkpoint is the cart's target (Alpine also keeps the last checkpoint's group live once the cart passes it) and groups advance with the cart.
---@opt spacing: number = 8 — Smallest distance between two spawns of the group (and to existing spawns), metres; a spawn hidden from the others may come a fifth closer, and the spacing is relaxed (1 m at a time) only when the spaces are too small.
function kit.spawn_points(t)
  local api = "kit.spawn_points"
  K.check_keys(api, t, { "room", "rooms", "area", "count", "team", "margin", "name", "seed", "checkpoint", "spacing" })
  local count = K.number(api, "count", t.count)
  if count < 1 or count ~= floor(count) then K.fail(api, "'count' must be a whole number of at least 1.") end
  local margin = K.number(api, "margin", t.margin, KIT.defaults.spawn_margin)
  if margin < 0 then K.fail(api, "'margin' must be 0 or more metres.") end
  local spacing = K.positive(api, "spacing", t.spacing, 8)
  local props = {}
  if t.team == "red" then props = { red_team = true, blue_team = false }
  elseif t.team == "blue" then props = { red_team = false, blue_team = true }
  elseif t.team ~= nil then K.fail(api, "'team' must be \"red\" or \"blue\" (omit it for both teams).") end
  local checkpoint = t.checkpoint
  if checkpoint ~= nil then
    if type(checkpoint) == "number" then checkpoint = level.find_uid(checkpoint) end
    if type(checkpoint) ~= "userdata" or checkpoint.class_name ~= "Payload_Checkpoint" then
      K.fail(api, "'checkpoint' must be a Payload_Checkpoint event (or its UID).", "Place one with level.place{kind = \"event\", class = \"Payload_Checkpoint\", ...}.")
    end
  end
  local spaces = P.spaces(api, t, K.fail)
  local first = (t.room ~= nil and type(t.room) == "table" and t.room.name) or (type(t.room) == "string" and t.room) or nil
  local name = t.name ~= nil and K.name_for(api, "spawn_points", t.name) or first or (#spaces == 1 and spaces[1].name) or K.name_for(api, "spawn_points", nil)
  P.mark("spawn plan")
  local existing, items = {}, {}
  for _, o in ipairs(level.objects_of("respawn")) do local p = o.pos existing[#existing + 1] = { p.x, p.y, p.z } end
  for _, o in ipairs(level.objects_of("item")) do local p = o.pos items[#items + 1] = { p.x, p.y, p.z } end
  local plan = CORE:spawn_plan{
    spaces = boxes_of(spaces), count = count, margin = margin, spacing = spacing, seed = seed_of(api, t),
    spawn_height = KIT.defaults.spawn_height, existing = existing, items = items,
  }
  if plan.error then K.fail(api, (name or "the spaces") .. ": " .. plan.error, plan.hint) end
  P.mark("spawn planned")
  local spawns = {}
  for i, c in ipairs(plan.spawns) do
    local y = c.y + KIT.defaults.spawn_height
    local o = level.place{
      kind = "respawn", name = name .. "_spawn" .. i, pos = { c.x, y, c.z }, props = props,
      tags = K.tags_for("spawn_points", name),
    }
    if abs(c.fx) + abs(c.fz) > 1e-6 then o:look_at(c.x + c.fx, y, c.z + c.fz) end
    spawns[i] = o
  end
  if checkpoint then checkpoint:link_many(spawns) end
  spawns.report = plan.report
  return spawns
end

-- ---------------------------------------------------------------------------------------------
-- kit.items
-- ---------------------------------------------------------------------------------------------

---@kit kit.items{gametype?, room?|rooms?|area?, density?, seed?, name?, weapons?, powerups?, path?, hill?} → items — The item economy, level-aware: the target is 1.6 items per 100 m² (× density) of the WHOLE level's walkable floor up to 4,500 m², easing on bigger levels the way the exemplars' item counts grow with their floor (about 1.2 at 10,000 m², never under 1.0), less every item already in the level, and a call places at most its spaces' share of it (their floor at that density, less the items already in them) — so per-zone calls add up to one level-wide economy, and calling again adds nothing once the level is stocked. Items never bunch: none goes where 6 already lie within 10 m, and each prefers the emptier neighbourhoods. Shares are the exemplar medians (weapons 31 %, ammo 34 %, health 17 %, armour 14 %, powerups 3 %); weapons 7–10 m apart and spread over the zones; powerups in the most contested cells, never in pockets; an item 3.5–5.5 m in front of every spawn without one within 6 m (counted in the target); health and armour on the flanks; ammo beside the weapons; CTF halves mirrored item for item; payload health and ammo on the cart route's flanks and at the defenders' holds. Place spawns first. Returns {items, count (number placed), by_category {weapon, ammo, health, armor, powerup}, target, target_per100 (the level density aimed at), local_max (most other items within 10 m of one item), area, per100 (these spaces, existing items included), level_area, level_per100, weapon_nn {min, median, max}, spawn_to_item {median, max}}.
---@opt gametype: string = "dm" — "dm", "tdm", "ctf", "koth", "cp" or "payload".
---@opt room: record|string — One space: a kit record, a kit.name or a kit.build_layout zone's name (give one of room, rooms, area; none = every air space).
---@opt rooms: (record|string)[] — Several spaces.
---@opt area: {min=, max=} — A box: every floor inside it.
---@opt density: number = 1 — Scales the level's item density (1.6 per 100 m² on DM-sized levels; 0.75 to 1.25 keeps it in the exemplar range).
---@opt seed: integer = 1 — The random stream.
---@opt name: string = items<n> — Prefix of the items' names (<name>_<n>) and their kit.name tag.
---@opt weapons: string[] = Shotgun, Assault Rifle, Machine Pistol, Sniper Rifle, rocket launcher, heavy machine gun, … — Weapon classes to use, in order (stock items.tbl names); their ammo comes with them.
---@opt powerups: boolean = true — Place powerups (Multi Damage Amplifier, Multi Super Armor, …) in contested spaces (from 900 m² of floor).
---@opt path: vec[] = none — Payload: the cart route's floor points (health and ammo go 6–15 m to its sides; weapons along it).
---@opt hill: vec = none — KOTH: the hill's centre (armour and health on its approaches, 8–16 m out).
function kit.items(t)
  local api = "kit.items"
  t = t or {}
  K.check_keys(api, t, { "gametype", "room", "rooms", "area", "density", "seed", "name", "weapons", "powerups", "path", "hill" })
  local gametype = gametype_of(api, t.gametype)
  local density = K.positive(api, "density", t.density, 1)
  if density < 0.5 or density > 1.5 then K.fail(api, "'density' must be between 0.5 and 1.5 (got " .. K.fmt(density) .. ").") end
  local name = K.name_for(api, "items", t.name)
  local seed = seed_of(api, t)
  local weapon_list = WEAPONS
  if t.weapons ~= nil then
    if type(t.weapons) ~= "table" or #t.weapons == 0 then K.fail(api, "'weapons' must list weapon item classes.") end
    weapon_list = {}
    for i, w in ipairs(t.weapons) do
      local found = nil
      for _, e in ipairs(WEAPONS) do if e[1]:lower() == tostring(w):lower() then found = e end end
      if not found then
        local names = {}
        for _, e in ipairs(WEAPONS) do names[#names + 1] = e[1] end
        K.fail(api, "'weapons[" .. i .. "]' (" .. tostring(w) .. ") is not a multiplayer weapon item.", "Weapons: " .. table.concat(names, ", ") .. ".")
      end
      weapon_list[i] = found
    end
  end
  local powerups = K.boolean(api, "powerups", t.powerups, true)
  local path = points_of(api, "path", t.path)
  if path and #path < 2 then K.fail(api, "'path' needs at least 2 points.") end
  local hill = nil
  if t.hill ~= nil then
    local x, y, z = K.vec3(api, "hill", t.hill)
    hill = { { x, y, z } }
  end
  -- When the tables are loaded, every class used must be in them.
  if catalog.available then
    local have = {}
    for _, c in ipairs(catalog.classes("Item")) do have[c] = true end
    local function check(c) if not have[c] then K.fail(api, "item class '" .. c .. "' is not in the loaded items.tbl.") end end
    for _, e in ipairs(weapon_list) do check(e[1]) check(e[2]) end
    for _, c in ipairs(HEALTH) do check(c) end
    for _, c in ipairs(ARMOR) do check(c) end
    for _, c in ipairs(POWERUPS) do check(c) end
  end

  local spaces = P.spaces(api, t, K.fail)
  local whole = (t.room == nil and t.rooms == nil and t.area == nil)
  local level_spaces = whole and nil or boxes_of(P.all_spaces(api, K.fail))
  P.mark("items plan")
  local spawns = {}
  for _, o in ipairs(level.objects_of("respawn")) do
    local p = o.pos
    local f = o.rot and o.rot.forward or { x = 0, z = 1 }
    spawns[#spawns + 1] = { x = p.x, y = p.y - KIT.defaults.spawn_height, z = p.z, fx = f.x, fz = f.z,
      team = o:get("red_team") and (o:get("blue_team") and 0 or 1) or 2 }
  end
  local existing = {}
  for _, o in ipairs(level.objects_of("item")) do
    local p = o.pos
    existing[#existing + 1] = { x = p.x, y = p.y, z = p.z, class = o.class_name }
  end
  local weapons = {}
  for i, e in ipairs(weapon_list) do weapons[i] = { e[1], e[2], rare = e.rare or false } end
  local pts = nil
  if path then
    pts = {}
    for i, p in ipairs(path) do pts[i] = { p.x, p.y, p.z } end
  end
  local plan = CORE:item_plan{
    spaces = boxes_of(spaces), level_spaces = level_spaces, gametype = gametype, density = density, seed = seed,
    powerups = powerups, weapons = weapons, health = HEALTH, armor = ARMOR, powerup_classes = POWERUPS,
    path = pts, hill = hill, spawns = spawns, existing = existing, item_height = KIT.defaults.item_height,
  }
  if plan.error then K.fail(api, plan.error, plan.hint) end
  P.mark("items planned")
  local items = {}
  for i, c in ipairs(plan.items) do
    local o = level.place{ kind = "item", class = c.class, name = name .. "_" .. i,
      pos = { c.x, c.y + KIT.defaults.item_height, c.z }, tags = K.tags_for("items", name) }
    o:tag("kit.part", c.cat)
    items[i] = o
  end
  return {
    kind = "items", name = name, gametype = gametype, items = items, count = #items, by_category = plan.by_category,
    target = plan.target, target_per100 = plan.target_per100, local_max = plan.local_max, area = plan.area, existing = plan.existing, per100 = plan.per100,
    level_area = plan.level_area, level_existing = plan.level_existing, level_per100 = plan.level_per100,
    weapon_nn = plan.weapon_nn, spawn_to_item = plan.spawn_to_item,
  }
end

-- The installer: copies the functions above into the kit and keeps its helpers.
return function(target, internals)
  KIT, K = target, internals
  target.spawn_points = kit.spawn_points
  target.items = kit.items
end
