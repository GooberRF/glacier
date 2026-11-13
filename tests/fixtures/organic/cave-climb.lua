-- Organic corpus (ORGANIC-GEOMETRY-SPEC §4.3): a cave climbing 12 m over about 35 m at a varying heading,
-- then a 45° leg (the slope limit) and a level landing. Run by OrganicCorpusTests with SEED set; returns
-- the tube station lines ("x,y,z;…").
local seed = SEED or 1
local cave = kit.cave{
  name = "climb", seed = seed,
  path = { { 0, 0, 0 }, { 0, 0, 10 }, { 8, 4, 20 }, { 10, 8, 32 }, { 4, 12, 40 }, { 4, 12, 48 }, { 4, 18, 54 }, { 4, 18, 62 } },
}

local out = {}
for _, s in ipairs(cave.stations) do
  local p = s.pos
  out[#out + 1] = string.format("%.6f,%.6f,%.6f", p.x, s.floor_y, p.z)
end
return table.concat(out, ";")
