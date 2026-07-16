--@name  Move Selection Up
--@id    move-selection-up
--@category Scripts
--@desc  Nudges every selected object up by 4 units.
--@key   Ctrl+Shift+U
--@api   1

-- Transform (2.2). selection.all() is a query over the current selection; :move applies a
-- single undoable batch. Select some objects first, then run.

local n = selection.count
if n == 0 then
  log.warn("Nothing selected — select some objects first.")
  return 0
end

selection.all():move(0, 4, 0)
log.info(string.format("Moved %d selected object(s) up 4.", n))
return n
