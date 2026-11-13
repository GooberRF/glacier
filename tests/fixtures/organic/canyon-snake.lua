-- Organic corpus (ORGANIC-GEOMETRY-SPEC §4.3): a 300 m snaking canyon centred on the origin — 14 path
-- points, two basins (widths 12 → 28 → 12), a 4 m descent, sky — with a flat-topped ledge, two rock walls
-- (one structural) and 40 boulders along its floor. Run by OrganicCorpusTests with SEED set; returns the
-- tube station lines ("x,y,z;…" per tube, tubes separated by "|").
local seed = SEED or 1
local canyon = kit.canyon{
  name = "canyon", seed = seed, height = 16,
  path = {
    { 0, 0, -150 }, { 8, 0, -128 }, { 2, 0, -106 }, { 12, 0, -84 }, { 26, -1, -62 }, { 30, -1, -40 }, { 18, -2, -18 },
    { 6, -2, 4 }, { 10, -2, 26 }, { 24, -3, 48 }, { 28, -3, 70 }, { 16, -4, 92 }, { 6, -4, 118 }, { 10, -4, 150 },
  },
  widths = { 12, 12, 14, 20, 28, 20, 14, 12, 14, 20, 28, 18, 13, 12 },
}
kit.rock_wall{ name = "outcrop", from = { 35.5, -1, -58 }, to = { 37.5, -1, -44 }, height = 5, depth = 3, structural = true, seed = seed + 10 }
kit.rock_wall{ name = "scree_wall", from = { 18.5, -1, -58 }, to = { 20.5, -1, -44 }, height = 4, depth = 3, seed = seed + 11 }
kit.rock_wall{ name = "ledge", from = { 1, -2, 10 }, to = { 3.5, -2, 22 }, height = 2.5, depth = 3, flat_top = true, seed = seed + 12 }
kit.boulders{ name = "boulders", along = canyon, count = 40, size = { 0.6, 2.0 }, seed = seed + 20 }

local out = {}
for _, s in ipairs(canyon.stations) do
  local p = s.pos
  out[#out + 1] = string.format("%.6f,%.6f,%.6f", p.x, s.floor_y, p.z)
end
return table.concat(out, ";")
