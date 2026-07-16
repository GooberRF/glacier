--- Glacier scripting API stub (generated). For editor completion only.
--- API version 1. Do not require() this file.

--- Meta: API versioning, scoped undo groups, run-mode flags, vec().
ged = {}
ged.allow_destructive = nil  --- boolean
ged.api_version = nil  --- number
function ged:confirm(message) end  --- returns boolean
ged.dry_run = nil  --- boolean
function ged:group(name, body) end  --- returns nil
ged.level_name = nil  --- string
function ged:require_api(major) end  --- returns nil
function ged:vec(x, y, z) end  --- returns vec

--- The open level: enumerate, look up, and place objects/brushes/events.
level = {}
level.brush_count = nil  --- number
level.brushes = nil  --- brush[]
level.count = nil  --- number
function level:find_brush(uid) end  --- returns brush
function level:find_uid(uid) end  --- returns object
level.objects = nil  --- object[]
function level:objects_of(kind) end  --- returns object[]
function level:place(kind, x, y, z, class_name) end  --- returns object
function level:place_box(x, y, z, w, h, d, texture) end  --- returns brush
function level:place_event(class_name, x, y, z) end  --- returns object
function level:save(path) end  --- returns nil

--- Query and mutate the current selection.
selection = {}
function selection:add(objects) end  --- returns nil
function selection:all() end  --- returns object_query
function selection:by_uid(uid) end  --- returns object
function selection:clear() end  --- returns nil
selection.count = nil  --- number
function selection:delete() end  --- returns number
function selection:invert() end  --- returns nil
selection.objects = nil  --- object[]
function selection:of_kind(kind) end  --- returns object_query
function selection:select_all() end  --- returns nil
function selection:set(objects) end  --- returns nil
function selection:where(predicate) end  --- returns object_query

--- Texture/asset lookup, where-used, and the bulk replace_texture op.
assets = {}
assets.available = nil  --- boolean
function assets:brushes_using(texture) end  --- returns brush_query
function assets:exists(name) end  --- returns boolean
function assets:is_used(name) end  --- returns boolean
function assets:replace_texture(old_texture, new_texture) end  --- returns number
function assets:textures() end  --- returns string[]
function assets:used_textures() end  --- returns string[]
function assets:where_used_by(name) end  --- returns asset_usage[]

--- Heavy operations: build, light, check_holes, save, package, playtest. `playtest` is editor-only: it drives the interactive editor's Alpine launch flow and is a no-op outside a running editor session.
ops = {}
function ops:build() end  --- returns op_report
function ops:check_holes() end  --- returns number
function ops:compat() end  --- returns string
function ops:light(shadows) end  --- returns op_report
function ops:package(path, multiplayer) end  --- returns op_report
function ops:playtest(multiplayer) end  --- returns nil
function ops:save(path) end  --- returns nil

--- Run the level linter and contribute custom findings.
lint = {}
function lint:add(severity, message, uid) end  --- returns nil
function lint:contributed() end  --- returns lint_finding[]
function lint:run() end  --- returns lint_report

--- Write to the Script Log (info/warn/error). Lua print() also lands here.
log = {}
function log:clear() end  --- returns nil
log.entries = nil  --- log_entry[]
function log:error(message) end  --- returns nil
function log:info(message) end  --- returns nil
function log:output(message) end  --- returns nil
function log:warn(message) end  --- returns nil

--- Seeded, deterministic random source (reproducible procedural scripts).
rng = {}
function rng:bool() end  --- returns boolean
function rng:float() end  --- returns number
function rng:int(min, max) end  --- returns number
function rng:pick(items) end  --- returns any
function rng:range(min, max) end  --- returns number
rng.seed = nil  --- number
function rng:set_seed(seed) end  --- returns nil

