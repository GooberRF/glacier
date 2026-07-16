--@name  Mass Texture Replace
--@id    mass-texture-replace
--@category Scripts
--@desc  Replaces one texture with another on every face that uses it.
--@allow-destructive
--@api   1

-- Batch property edit (2.1). assets.replace_texture is a vectorized C# op: it changes
-- every matching face across the whole level as ONE undo step, so it stays fast even on
-- a 100k-brush level. Edit the two names below, then run.

local old_texture = "metal01.tga"
local new_texture = "rck_dm01.tga"

local n = assets.replace_texture(old_texture, new_texture)
if n == 0 then
  log.warn(string.format("No faces use '%s'.", old_texture))
else
  log.info(string.format("Replaced '%s' -> '%s' on %d face(s).", old_texture, new_texture, n))
end
return n
