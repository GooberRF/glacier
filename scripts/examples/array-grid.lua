--@name  Array Grid
--@id    array-grid
--@category Scripts
--@desc  Places a rectangular grid of pillar brushes.
--@key   Ctrl+Shift+G
--@api   1

-- Procedural generation (plan use-case 2.4). The whole loop is ONE undo step:
-- press Ctrl+Z once to remove the entire grid.

local cols, rows = 5, 5
local spacing    = 5      -- metres between pillars
local pw, ph, pd = 1, 8, 1  -- pillar width / height / depth

for i = 0, cols - 1 do
  for j = 0, rows - 1 do
    level.place_box(i * spacing, ph / 2, j * spacing, pw, ph, pd)
  end
end

log.info(string.format("Placed %d pillars in a %dx%d grid.", cols * rows, cols, rows))
