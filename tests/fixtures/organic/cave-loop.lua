-- Organic corpus (ORGANIC-GEOMETRY-SPEC §4.3): a closed cave loop (its path ends exactly where it starts,
-- so the last segment closes onto the first segment's own start ring), and a lower branch cave starting
-- inside the loop. Run by OrganicCorpusTests with SEED set; returns the tube station lines ("x,y,z;…" per
-- tube, tubes separated by "|").
local seed = SEED or 1
local loop = kit.cave{
  name = "loop", seed = seed,
  path = { { 0, 0, 0 }, { 20, 0, 0 }, { 30, 0, 15 }, { 20, 0, 30 }, { 0, 0, 30 }, { -10, 0, 15 }, { 0, 0, 0 } },
}
local branch = kit.cave{
  name = "branch", seed = seed + 1, radius = 2.5, height = 3.25,
  path = { { 10, 0, 30 }, { 10, 0, 46 }, { 24, 1, 60 } },
}

local lines = {}
for _, t in ipairs{ loop, branch } do
  local out = {}
  for _, s in ipairs(t.stations) do
    local p = s.pos
    out[#out + 1] = string.format("%.6f,%.6f,%.6f", p.x, s.floor_y, p.z)
  end
  lines[#lines + 1] = table.concat(out, ";")
end
return table.concat(lines, "|")
