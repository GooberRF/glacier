-- Organic corpus (ORGANIC-GEOMETRY-SPEC §4.3): a 30 × 8 × 30 room lined with four structural rock walls
-- (standing just clear of the room's walls) and one detail rock wall in the middle. Run by
-- OrganicCorpusTests with SEED set; returns "" (no tubes).
local seed = SEED or 1
kit.room{ name = "hall", pos = { 0, 0, 0 }, size = { 30, 8, 30 } }
kit.rock_wall{ name = "north", from = { -11, 0, 12.5 }, to = { 11, 0, 12.5 }, height = 5, depth = 3, structural = true, seed = seed }
kit.rock_wall{ name = "south", from = { -11, 0, -12.5 }, to = { 11, 0, -12.5 }, height = 4, depth = 3, structural = true, seed = seed + 1 }
kit.rock_wall{ name = "east", from = { 12.5, 0, -9 }, to = { 12.5, 0, 9 }, height = 6, depth = 3, structural = true, seed = seed + 2 }
kit.rock_wall{ name = "west", from = { -12.5, 0, -9 }, to = { -12.5, 0, 9 }, height = 3, depth = 3, structural = true, seed = seed + 3 }
kit.rock_wall{ name = "middle", from = { -5, 0, -1 }, to = { 5, 0, 2 }, height = 2, depth = 2, seed = seed + 4 }
return ""
