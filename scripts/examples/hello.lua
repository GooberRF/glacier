--@name  Hello
--@id    hello
--@category Scripts
--@desc  A tiny starter — prints level stats to the Script Log.
--@api   1

-- Everything a script can touch hangs off a few globals:
--   level, selection, assets, ops, lint, log, rng, ged
-- Read-only scripts (like this one) add no undo step.

log.info("Hello from Lua!")
log.info(string.format("This level has %d object(s) and %d brush(es).", level.count, level.brush_count))
return level.count
