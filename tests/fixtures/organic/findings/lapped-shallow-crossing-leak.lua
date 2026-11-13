-- COMPILER FINDING: convex air hulls of a straight cave joined by a lapped overlap (the W3 joint) — each hull
-- reaches 0.75 m past the shared station ring into its neighbour. The rings carry about 5 % of jitter, so
-- neighbouring hulls' walls cross each other at angles of a few degrees inside every overlap. Sealed builds
-- leave open edges along those shallow crossings (measured with this joint: up to 20 holes per 8-hull straight
-- cave at roughness 0.05, and single holes at the default 0.2 on some seeds, under both build methods). The
-- generators do not build this joint: their rings are exactly planar and neighbouring hulls abut on one
-- identical cap. Kept as a minimal repro for a general compiler fix.
local K = {
  { 1.03, 0.97, 1.04, 0.96, 1.05, 0.98, 1.02, 0.95 },
  { 0.96, 1.04, 0.97, 1.05, 0.95, 1.03, 0.98, 1.02 },
  { 1.04, 0.98, 1.02, 0.97, 1.05, 0.96, 1.03, 0.99 },
  { 0.97, 1.03, 0.99, 1.04, 0.96, 1.02, 0.95, 1.05 },
}

local function ring(z, k)
  local pts = { { -1.8 * k[1], 0, z }, { 1.8 * k[2], 0, z } }
  for j = 1, 6 do
    local th = j * math.pi / 7
    pts[#pts + 1] = { 3 * math.cos(th) * k[j + 2], 4.5 * math.sin(th) * k[(j % 6) + 3], z }
  end
  return pts
end

local function hull(a, b)
  local out = {}
  for _, p in ipairs(a) do out[#out + 1] = p end
  for _, p in ipairs(b) do out[#out + 1] = p end
  level.brush_from_points{ air = true, points = out }
end

local lap = 0.75
for i = 1, 3 do
  local z0, z1 = (i - 1) * 3, i * 3
  hull(ring(z0 - (i > 1 and lap or 0), K[i]), ring(z1 + (i < 3 and lap or 0), K[i + 1]))
end
