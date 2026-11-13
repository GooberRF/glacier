-- Organic corpus (ORGANIC-GEOMETRY-SPEC §4.3): kit room → cave → kit room (the path starts and ends inside
-- both rooms, at their floor heights), a kit.passage at 30° from the second room to a third, and a kit.pit
-- in the third. Run by OrganicCorpusTests with SEED set; returns the tube station lines ("x,y,z;…" per
-- tube, tubes separated by "|").
local seed = SEED or 1
kit.room{ name = "a", pos = { 0, 0, 0 }, size = { 12, 6, 12 } }
kit.room{ name = "b", pos = { 40, 2, 30 }, size = { 12, 6, 12 } }
kit.room{ name = "c", pos = { 80, 2, 53 }, size = { 16, 6, 16 } }
local cave = kit.cave{ name = "link", seed = seed, path = { { 3, 0, 0 }, { 14, 0, 4 }, { 26, 2, 18 }, { 37, 2, 30 } } }
local pass = kit.passage{ name = "pass", path = { { 43, 2, 33.5 }, { 76, 2, 52.5 } }, width = 4, height = 4 }
kit.pit{ name = "pit", pos = { 82, 2, 55 }, radius = 3, depth = 5, seed = seed + 1 }

local lines = {}
for _, t in ipairs{ cave, pass } do
  local out = {}
  for _, s in ipairs(t.stations) do
    local p = s.pos
    out[#out + 1] = string.format("%.6f,%.6f,%.6f", p.x, s.floor_y, p.z)
  end
  lines[#lines + 1] = table.concat(out, ";")
end
return table.concat(lines, "|")
