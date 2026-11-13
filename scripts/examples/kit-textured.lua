--@name Example: textured zones (palettes, trims, aligned UVs)
--@id example-kit-textured
--@category Examples
--@desc Two zones with their own stock palettes and wall kits (base and top bands, posts, door frames, hazard stripes), stairs and a ramp with slope-aligned UVs, a fitted crate and a column wrapped around its corners.
--@api 2
local kit = ged.import("kit")

level.props:set_many{ level_name = "Kit Textures", multiplayer = true }

-- One palette per zone: an industrial hall and a concrete store room (variant 2 picks other entries).
local ind = kit.palette{ theme = "industrial" }
local bun = kit.palette{ theme = "bunker", variant = 2 }

-- Rooms (air boxes) and the doorway between them.
local hall  = kit.room{ name = "hall",  pos = {  0, 0, 0 }, size = { 20, 8, 16 }, textures = ind.textures }  -- x -10..10, z -8..8
local store = kit.room{ name = "store", pos = { 24, 0, 0 }, size = { 12, 6, 12 }, textures = bun.textures }  -- x 18..30, z -6..6
kit.doorway{ name = "door", a = hall, b = store, textures = ind.textures }

-- A deck in the hall's -x/+z corner, reached by stairs climbing toward -x (treads start at each nosing).
kit.platform{ name = "deck", pos = { -6, 0, 4 }, size = { 8, 8 }, height = 3, textures = ind.textures }       -- x -10..-2, z 0..8
kit.stairs{ name = "steps", pos = { 4, 0, 4 }, dir = "-x", rise = 3, textures = ind.textures }               -- x -2..4

-- A ledge in the store reached by a ramp (its texture runs up the slope at true length).
kit.platform{ name = "ledge", pos = { 26, 0, 4 }, size = { 8, 4 }, height = 1.5, textures = bun.textures }    -- x 22..30, z 2..6
kit.ramp{ name = "ramp", pos = { 19, 0, 4 }, dir = "+x", rise = 1.5, textures = bun.textures }               -- x 19..22

-- A crate with one texture per face, and a column whose texture wraps around its corners.
local crate = level.brush{ pos = { 5, 1, -4 }, size = { 2, 2, 2 }, texture = "mtl_cargo01_p01.tga" }
crate:fit_faces()
local column = level.brush{ pos = { -4, 4, -4 }, size = { 1.5, 8, 1.5 }, texture = ind.accent[1] }
column:box_map{ ppm = kit.defaults.ppm }
local function side(dx, dz)
  for _, f in ipairs(column:faces_facing("side")) do
    if f.normal.x * dx + f.normal.z * dz > 0.99 then return f end
  end
end
local ring = { side(0, -1), side(1, 0), side(0, 1), side(-1, 0) }  -- around the column
for i = 2, #ring do ring[i]:wrap_from(ring[i - 1]) end

-- The wall kits, after the openings and the solids standing in each room.
local hall_trim = kit.trim{ room = hall, palette = ind }
local store_trim = kit.trim{ room = store, palette = bun }

kit.light_room{ room = hall }
kit.light_room{ room = store }
kit.spawn_points{ room = hall, count = 4 }

return string.format("trim: %d bands, %d posts, %d frames, %d stripes; holes = %d",
  hall_trim.bands + store_trim.bands, hall_trim.posts + store_trim.posts, hall_trim.frames + store_trim.frames,
  hall_trim.stripes + store_trim.stripes, ops.holes().count)
