--@name Example: cave network (organic kit)
--@id example-kit-cave
--@category Examples
--@desc A hub room with three ways out: a cave climbing 6 m to a gallery, a passage to an armoury, and a loop through a chamber, down a pit and back up an underpass.
--@api 2
local kit = ged.import("kit")
local SEED = 3          -- every organic piece takes its own seed; change SEED for new rock shapes, same layout
rng.set_seed(SEED)

level.props:set_many{ level_name = "Kit Caves", multiplayer = true, ambient_color = {40, 40, 46} }

-- One rock texture set for the rooms and every organic piece.
local rock = { floor = "rck_cavernflr01.tga", wall = "rck_canyon_rock03.tga", ceiling = "rck_canyon_rock02.tga" }
kit.defaults.textures = rock
kit.defaults.rock_textures = rock

-- Rooms (air boxes). A cave or passage joins a room by starting or ending at least 1 m inside it, at
-- its floor height: the floors are then one plane and the walls cross cleanly.
local hub     = kit.room{ name = "hub",     pos = {   0, 0,   0 }, size = { 16, 6, 16 } }  -- x -8..8,   z -8..8
local gallery = kit.room{ name = "gallery", pos = { -36, 6,  30 }, size = { 14, 6, 14 } }  -- x -43..-29, z 23..37, floor 6
local chamber = kit.room{ name = "chamber", pos = {  28, 0,  30 }, size = { 14, 6, 14 } }  -- x 21..35,  z 23..37
local armoury = kit.room{ name = "armoury", pos = { -12, 0, -34 }, size = { 12, 5, 10 } }  -- x -18..-6, z -39..-29

-- A cave climbing 6 m from the hub to the gallery: the sloped leg (points 2-3) is a smooth ramp with a
-- level landing at each turn.
local climb = kit.cave{ name = "climb", seed = SEED,
  path = { { -5, 0, 3 }, { -18, 0, 3 }, { -28, 6, 15 }, { -34, 6, 28 } } }

-- A level cave from the hub to the chamber, and a clean 3 m passage to the armoury on a 30-degree heading.
local tunnel = kit.cave{ name = "tunnel", seed = SEED + 1, radius = 3.5,
  path = { { 5, 0, 3 }, { 18, 0, 3 }, { 28, 0, 12 }, { 28, 0, 26 } } }
local hall = kit.passage{ name = "armoury_hall", width = 3, height = 3.5,
  path = { { -4, 0, -6 }, { -4, 0, -14 }, { -12, 0, -26 }, { -12, 0, -31 } } }

-- A pit in the chamber floor (its mouth opens through that floor), and an underpass that starts on the
-- pit's floor and climbs back to the hub: the loop hub -> tunnel -> chamber -> pit -> underpass -> hub.
local pit = kit.pit{ name = "pit", pos = { 28, 0, 32 }, radius = 3.5, depth = 5, seed = SEED + 2 }
local under = kit.cave{ name = "underpass", seed = SEED + 3,
  path = { { 28, pit.floor_y, 32 }, { 40, -5, 32 }, { 44, -5, 14 }, { 36, -2, -6 }, { 18, 0, -14 }, { 0, 0, -5 } } }

-- Dressing after the air it stands in: detail boulders off the walkway.
kit.boulders{ name = "tunnel_rocks", along = tunnel, count = 8, size = { 0.5, 1.4 }, seed = SEED + 4 }
kit.boulders{ name = "underpass_rocks", along = under, count = 12, size = { 0.5, 1.4 }, seed = SEED + 5 }

-- Light every room and every route.
for _, r in ipairs{ hub, gallery, chamber, armoury } do kit.light_room{ room = r, color = { 255, 214, 170 } } end
for _, t in ipairs{ climb, tunnel, hall, under } do kit.light_path{ along = t, spacing = 10, color = { 200, 214, 255 } } end

-- Spawns: rings in the rooms without a pit, and two on the underpass walkway.
kit.spawn_points{ room = hub, count = 4 }
kit.spawn_points{ room = gallery, count = 2 }
kit.spawn_points{ room = armoury, count = 2 }
for _, i in ipairs{ 4, math.floor(#under.stations * 2 / 3) } do
  local s = under.stations[i]
  level.place{ kind = "respawn", name = "underpass_spawn" .. i, pos = { s.pos.x, s.floor_y + 1, s.pos.z }, yaw = s.heading }
end

level.place{ kind = "item", class = "Shotgun",       name = "item_shotgun", pos = { -12, kit.defaults.item_height, -34 } }
level.place{ kind = "item", class = "Assault Rifle", name = "item_rifle",   pos = { -36, 6 + kit.defaults.item_height, 30 } }
level.place{ kind = "item", class = "Medical Kit",   name = "item_medkit",  pos = { 28, pit.floor_y + kit.defaults.item_height, 32 } }

level.player_start{ pos = { 0, 1, -2 }, yaw = 0 }

local holes = ops.holes()
assert(holes.is_watertight, "cave example leaked: " .. holes.count .. " hole(s)")
return string.format("caves: %d brushes, %d lights, climb %.1f m", level.brush_count, #level.objects_of("light"), climb.climbs[1].rise)
