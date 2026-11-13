--@name Example: snaking canyon (organic kit)
--@id example-kit-canyon
--@category Examples
--@desc A 240 m outdoor canyon under the sky: two basins, a descent, rock outcrops, a walkable ledge, boulders, lights, spawns and items.
--@api 2
local kit = ged.import("kit")
local SEED = 7          -- every organic piece takes its own seed; change SEED for new rock shapes, same layout
rng.set_seed(SEED)

level.props:set_many{ level_name = "Kit Canyon", multiplayer = true, ambient_color = {70, 66, 60} }

-- Stock rock textures for every organic piece below (mapped at the kit's 64 px/m, like every surface).
kit.defaults.rock_textures = { floor = "rck_dirt01.tga", wall = "rck_canyon_rock01.tga", ceiling = "rck_canyon_rock02.tga" }

-- The floor path, centred on the origin (keep large organic builds within about 200 m of it). Level legs
-- keep one flat floor; the legs that drop (points 4-5 and 7-8) are smooth slopes with a level landing at
-- each turn. Basins at points 5-6 and 8. Open to the sky: the canyon adds a sky room (canyon_sky).
local canyon = kit.canyon{
  name = "canyon", seed = SEED, height = 14,
  path = {
    { -4,  0, -120 }, {  6,  0, -96 }, {  0,  0, -72 }, { 14,  0, -48 }, { 24, -2, -24 }, { 20, -2,   0 },
    {  6, -2,   24 }, { -6, -3,  48 }, { -2, -3,  72 }, { 10, -3,  96 }, {  4, -3, 120 },
  },
  widths = { 12, 12, 14, 22, 28, 24, 14, 18, 14, 12, 12 },
}

-- Rock dressing goes after the air it stands in, on a level stretch of floor.
kit.rock_wall{ name = "outcrop", from = { 30, -2, -18 }, to = { 28.5, -2, -8 }, height = 5, depth = 3,
               structural = true, seed = SEED + 1 }                              -- one sealed ridge in the basin
kit.rock_wall{ name = "scree", from = { 14, -2, -18 }, to = { 13, -2, -6 }, height = 3, depth = 3, seed = SEED + 2 }
kit.rock_wall{ name = "ledge", from = { -10, -3, 54 }, to = { -8, -3, 66 }, height = 1.5, depth = 3,
               flat_top = true, seed = SEED + 3 }                                -- a flat top players can jump onto
kit.boulders{ name = "boulders", along = canyon, count = 36, size = { 0.6, 2.2 }, seed = SEED + 4 }

kit.light_path{ along = canyon, spacing = 20, color = { 255, 236, 205 } }

-- Spawns on the station line (the guaranteed walkway, clear of every boulder), facing along the canyon.
for i, s in ipairs(canyon.stations) do
  if i % 5 == 3 then
    level.place{ kind = "respawn", name = "canyon_spawn" .. i, pos = { s.pos.x, s.floor_y + 1, s.pos.z }, yaw = s.heading }
  end
end

level.place{ kind = "item", class = "Shotgun",       name = "item_shotgun", pos = { 22, -2 + kit.defaults.item_height, -12 } }
level.place{ kind = "item", class = "Medical Kit",   name = "item_medkit",  pos = { 20, -2 + kit.defaults.item_height, 0 } }
level.place{ kind = "item", class = "Assault Rifle", name = "item_rifle",   pos = { -2, -3 + kit.defaults.item_height, 72 } }

local start = canyon.stations[1]
level.player_start{ pos = { start.pos.x, start.floor_y + 1, start.pos.z }, yaw = start.heading }

local holes = ops.holes()
assert(holes.is_watertight, "canyon example leaked: " .. holes.count .. " hole(s)")
return string.format("canyon: %d brushes, %d m long, %d stations", level.brush_count, math.floor(canyon.length), #canyon.stations)
