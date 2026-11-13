--@name Example: tiny deathmatch level (design kit)
--@id example-kit-tiny-dm
--@category Examples
--@desc Three rooms, a corridor, a button door, stairs, pillars, lights, spawns and items.
--@api 2
local kit = ged.import("kit")

level.props:set_many{ level_name = "Kit Tiny DM", multiplayer = true, ambient_color = {60, 60, 60} }

-- Rooms: air boxes. hall x[-8,8] z[-8,8]; annex x[9,21] (1 m wall); vault z[14,30] (6 m gap).
local hall  = kit.room{ name = "hall",  pos = {0, 0, 0},  size = {16, 6, 16} }
local annex = kit.room{ name = "annex", pos = {15, 0, 0}, size = {12, 6, 12} }
local vault = kit.room{ name = "vault", pos = {0, 0, 22}, size = {12, 5, 16} }

local way  = kit.doorway{ name = "annex_way", a = hall, b = annex }                 -- 3 x 3 through the wall
local link = kit.corridor{ name = "vault_link", a = hall, b = vault }               -- 4 x 4, 6 m long

-- Solids go in after the air that contains them (time order).
kit.platform{ name = "ledge", pos = {-5, 0, -5}, size = {4, 4}, height = 1.5 }
kit.stairs{ name = "ledge_stairs", pos = {-5, 0, 0}, dir = "-z", rise = 1.5, width = 2 }
kit.pillar_row{ name = "vault_pillars_w", from = {-3, 0, 18}, to = {-3, 0, 26}, count = 3, height = 5 }
kit.pillar_row{ name = "vault_pillars_e", from = { 3, 0, 18}, to = { 3, 0, 26}, count = 3, height = 5 }

local door = kit.door{ name = "annex_door", opening = way,
                       buttons = { {7.5, 1.5, 2.5}, {9.5, 1.5, 2.5} } }

for _, r in ipairs{ hall, annex, vault } do
  kit.light_room{ room = r }
  kit.spawn_points{ room = r, count = 2 }
end

level.place{ kind = "item", class = "Shotgun",       name = "item_shotgun", pos = {15, 0.5, 0} }
level.place{ kind = "item", class = "Medical Kit",   name = "item_medkit",  pos = {0, 0.5, 26} }
level.place{ kind = "item", class = "Assault Rifle", name = "item_rifle",   pos = {-5, 2.0, -5} }

level.player_start{ pos = {0, 1, -4}, yaw = 0 }

local holes = ops.holes()
assert(holes.is_watertight, "kit example leaked: " .. holes.count .. " hole(s)")
return string.format("brushes=%d movers=%d", level.brush_count, #level.movers)
