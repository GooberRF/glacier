--@name  Spiral Stairs
--@id    spiral-stairs
--@category Scripts
--@desc  Builds a spiral staircase out of box brushes.
--@api   1

-- Procedural generation (2.4). Each step is a box, rotated to face the centre.
-- One undo step reverts the whole staircase.

local steps  = 16
local radius = 6
local rise   = 1.0          -- vertical gap per step
local sw, sh, sd = 3, 0.4, 1.5  -- step size

for i = 0, steps - 1 do
  local angle = (i / steps) * math.pi * 2
  local x = math.cos(angle) * radius
  local z = math.sin(angle) * radius
  local y = i * rise
  local step = level.place_box(x, y, z, sw, sh, sd)
  step:rotate("y", math.deg(angle))
end

log.info(string.format("Built a %d-step spiral staircase (radius %g).", steps, radius))
