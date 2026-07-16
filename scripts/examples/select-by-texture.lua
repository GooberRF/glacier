--@name  Select By Texture
--@id    select-by-texture
--@category Scripts
--@desc  Selects every brush that has a face using a given texture.
--@api   1

-- Selection query (2.3). brushes_using returns a chainable brush query; :select() commits
-- it to the selection. Selection changes are not undoable (they are a view concern).

local texture = "metal01.tga"

local q = assets.brushes_using(texture)
q:select()
log.info(string.format("Selected %d brush(es) using '%s'.", q.count, texture))
return q.count
