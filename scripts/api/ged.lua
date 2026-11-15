--- Glacier scripting API stub (generated). For editor completion only.
--- API version 2. Do not require() this file.

--- Meta: API versioning, module loading (import), scoped undo groups, run-mode flags, vec() / rot() constructors, describe() and the conventions text.
ged = {}
--- True when destructive ops (delete, save, package, playtest) are pre-authorized for this run (the --@allow-destructive header).
ged.allow_destructive = nil  --- boolean
--- The script API major version this build implements (2).
ged.api_version = nil  --- number
--- Asks the user to confirm message and returns true or false; always false in Dry-Run, always true with --@allow-destructive.
function ged:confirm(message) end  --- returns boolean
--- The scripting conventions as plain text: units, axes, rotation, CSG time order, texture roles, undo, hole checks.
ged.conventions = nil  --- string
--- Describes a type or global (a name like "brush", or any handle/record/global value): members, signatures and docs; a handle's text also lists its fields and values. Returns type_info.
function ged:describe(x) end  --- returns type_info
--- True while previewing (Dry-Run): every change is rolled back afterwards and destructive ops are refused.
ged.dry_run = nil  --- boolean
--- Raises an API error with an optional one-line hint; the error is reported at the line of your script that called into the module (like a built-in function's error). Never returns.
function ged:fail(message, hint) end  --- returns nil
--- Runs fn and labels its changes as a named step in the Script Log; they stay part of the run's single undo step.
function ged:group(name, body) end  --- returns nil
--- Loads a Lua module once per session and returns its value: shipped modules ("kit") first, then <name>.lua beside a script run from a file (literal names), then <scripts>/lib/<name>.lua.
function ged:import(name) end  --- returns any
--- The open level's file name, or "untitled" for a level that has never been saved.
ged.level_name = nil  --- string
--- A list as plain strings for table.concat: strings stay, numbers and booleans are written out, records and handles give their name (class_info its class_name), tables their name field.
function ged:names(list) end  --- returns string[]
--- Errors (with an update hint) unless this build supports script API major version n.
function ged:require_api(major) end  --- returns nil
--- Constructs a rotation from degrees (pitch and roll default to 0). Returns a rotation record.
function ged:rot(yaw, pitch, roll) end  --- returns rotation
--- Calls fn(...) and returns true and fn's results, or false and an error {message, hint, kind} after undoing every level change fn made. Time limits and Stop still end the run.
function ged:try(fn) end  --- returns boolean
--- Constructs a vec in metres (x right, y up, z forward). Returns the vec.
function ged:vec(x, y, z) end  --- returns vec

--- The open level: enumerate, look up, and place objects/brushes/events; create brushes with level.brush{…} or as convex hulls of points with level.brush_from_points{…}; import models with level.import_mesh{…}; spatial and predicate brush/object queries.
level = {}
--- A brush_query over every brush in CSG time order.
function level:all_brushes() end  --- returns brush_query
--- The Alpine level properties (legacy timers/movers, headlamp, static-mesh ambient, RF2-style geomod, payload_* rules), plus the read-only Alpine 1.5 lighting, sun and level settings.
level.alpine = nil  --- props
--- Creates a brush (metres; pos = centre, size = {w,h,d} full extents) at the end of time. Returns the brush. air=true carves, solid adds.
---   shape: string = "box" — box, cylinder, cone, sphere, wedge or quad.
---   pos: vec = {0, 0, 0} — The brush centre in world metres.
---   size: number | vec = {4, 4, 4} — Full extents {w, h, d} in metres, each > 0.01 and <= 4096; a number makes a cube; a quad takes {w, h}.
---   rot: rotation | {yaw=, pitch=, roll=} | {forward=, up=} = no rotation — Orientation about the centre, in degrees.
---   sides: integer = 12 — Segments around a cylinder, cone or sphere (3 to 64).
---   stacks: integer = 1 (sphere 6) — Segments along a cylinder or sphere's height (1 to 64; a sphere needs at least 2).
---   splits: vec = {0, 0, 0} — Extra subdivisions of a box or quad per axis (0 to 32 each).
---   air: boolean = false — true carves space out of the solid world; false adds solid.
---   portal: boolean = false — A portal brush (always air): applied after every other brush, it opens the solid it covers and the walls it opens become portals that split rooms for visibility (none in space already open).
---   detail: boolean = false — A detail brush (does not split rooms; can be breakable or geoable).
---   geoable: boolean = false — Geomod can destroy it (implies detail).
---   emits_steam: boolean = false — Faces emit steam.
---   life: integer = -1 — Hit points: -1 is infinite; finite life on a detail brush makes it breakable.
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on, for air brushes too); omitted roles use the defaults.
---   name: string = none — A name for the brush, written as its kit.name tag (as every kit function names its brushes), so level.brushes_tagged("kit.name", name) and the kit's by-name lookups find it.
---   tags: {key = value} = none — Provenance tags written on the new brush.
function level:brush(args) end  --- returns brush
--- The number of brushes in the level.
level.brush_count = nil  --- number
--- Creates a convex brush: the hull of 4 to 256 world points (metres, snapped to 1/1024 m), unrotated and box-mapped, at the end of time; air=true carves. Returns the brush.
---   points: vec[] — Required: 4 to 256 world points in metres; the brush is their convex hull. Points inside the hull are ignored.
---   air: boolean = false — true carves space out of the solid world; false adds solid.
---   detail: boolean = false — A detail brush (never seals or splits rooms).
---   geoable: boolean = false — Geomod can destroy it (implies detail, as level.brush).
---   emits_steam: boolean = false — Faces emit steam.
---   life: integer = -1 — Hit points: -1 is infinite; finite life on a detail brush makes it breakable (as level.brush).
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on, for air brushes too); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   snap: number = 1/1024 — The grid the points are snapped to: a power of two from 1/1024 to 1 metre.
---   strict: boolean = false — true: error when a point is not a corner of the hull (inside it, a repeat after snapping, or removed as a sliver).
---   name: string = none — A name for the brush, written as its kit.name tag (as every kit function names its brushes), so level.brushes_tagged("kit.name", name) and the kit's by-name lookups find it.
---   tags: {key = value} = none — Provenance tags written on the new brush.
function level:brush_from_points(args) end  --- returns brush
--- Every brush as brush[], in CSG time order.
level.brushes = nil  --- brush[]
--- Creates one convex brush per point list in hulls (1 to 2000 lists; same options as brush_from_points, applied to every brush) as one undo command. Returns a brush_query in list order.
---   hulls: vec[][] — Required: 1 to 2000 point lists (4 to 256 world points each, metres); one convex brush per list, in list order.
---   air: boolean = false — true carves space out of the solid world; false adds solid (every brush).
---   detail: boolean = false — Detail brushes (never seal or split rooms).
---   geoable: boolean = false — Geomod can destroy them (implies detail, as level.brush).
---   emits_steam: boolean = false — Faces emit steam.
---   life: integer = -1 — Hit points: -1 is infinite; finite life on a detail brush makes it breakable (as level.brush).
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on, for air brushes too); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   snap: number = 1/1024 — The grid the points are snapped to: a power of two from 1/1024 to 1 metre.
---   strict: boolean = false — true: error when a point is not a corner of its hull (inside it, a repeat after snapping, or removed as a sliver).
---   name: string = none — A name for every brush, written as their kit.name tag (as level.brush).
---   tags: {key = value} = none — Provenance tags written on every new brush.
function level:brushes_from_points(args) end  --- returns brush_query
--- A brush_query of the brushes whose world bounds intersect the box {min=, max=} (metres, touching counts).
---   min: vec — Required: one corner of the box, metres.
---   max: vec — Required: the opposite corner (the corners may be given in either order).
function level:brushes_in(box) end  --- returns brush_query
--- A brush_query of the brushes tagged key (with exactly value when given), in time order.
function level:brushes_tagged(key, value) end  --- returns brush_query
--- A brush_query of the brushes for which fn(brush) returns true, in time order.
function level:brushes_where(predicate) end  --- returns brush_query
--- The number of level objects (lights, triggers, events, keyframes, the Player Start…), brushes not included.
level.count = nil  --- number
--- Groups brushes and/or objects (handles or UIDs) under a name (default "Group"); the editor then selects them together. Returns the group.
---   name: string = "Group" — The group's name.
---   brushes: list of handles or UIDs = none — Member brushes (at least one brush or object is required).
---   objects: list of handles or UIDs = none — Member objects.
function level:create_group(args) end  --- returns group
--- Makes a mover from brushes (and/or objects) — handles or UIDs — with a start keyframe at their centre; name defaults to "Mover"; props = mover fields; tags go on the start keyframe. Returns the mover.
---   name: string = "Mover" — The mover's name.
---   brushes: list of handles or UIDs = none — The member brushes (at least one brush or object is required).
---   objects: list of handles or UIDs = none — Member objects that ride the mover (triggers, lights, clutter…).
---   props: {field = value} = the editor defaults — Mover fields (movement_type, is_door, sounds…; see m:fields()).
---   tags: {key = value} = none — Provenance tags written on the start keyframe.
function level:create_mover(args) end  --- returns mover
--- The brush with this UID, or nil.
function level:find_brush(uid) end  --- returns brush
--- The first user group named name (case-insensitive), or nil.
function level:find_group(name) end  --- returns group
--- The first mover named name (case-insensitive), or nil.
function level:find_mover(name) end  --- returns mover
--- The level object with this UID, or nil (brushes: use find_brush).
function level:find_uid(uid) end  --- returns object
--- Every user group in the level, in file order (group[]).
level.groups = nil  --- group[]
--- Imports a .obj/.gltf/.glb/.fbx/.dae model: as detail brushes (default, one per material, UVs kept) or as an Alpine Mesh object with a .v3m. Returns mesh_import.
---   path: string — Required: a .obj, .gltf, .glb, .fbx or .dae file. Relative paths resolve against the level's folder, then <scripts>/import. Only the model file (and an OBJ's .mtl in the same folder) is read.
---   as: string = "detail" — "detail": one detail brush per material group (triangle faces, UVs kept). "mesh": an Alpine Mesh object using a .v3m written to <install>/user_maps/meshes (a destructive write).
---   pos: vec = {0, 0, 0} — Where the model's origin lands, metres.
---   rot: rotation | {yaw=, pitch=, roll=} | {forward=, up=} = no rotation — The brushes' rotation / the mesh object's orientation, in degrees (never baked into the vertices).
---   scale: number = 1 — Uniform scale applied to the model (0.0001 to 10000; 0.01 converts centimetres).
---   axis: string = by format (glTF: "gltf", others: "rf") — "rf" (+Y up, +Z forward), "gltf" (Y up, −Z forward) or "z_up" (Blender / 3ds Max).
---   flip_winding: boolean = false — Reverse every triangle (when the model renders inside out).
---   name: string = the file name without extension — The group name ("detail") or the mesh object's script name and .v3m file name ("mesh").
---   tags: {key = value} = none — Provenance tags written on every brush / the mesh object.
function level:import_mesh(args) end  --- returns mesh_import
--- Every mover in the level, in file order (mover[]).
level.movers = nil  --- mover[]
--- Every level object as object[] (iterate with ipairs).
level.objects = nil  --- object[]
--- An object_query of the objects whose position lies inside the box {min=, max=} (metres, inclusive).
---   min: vec — Required: one corner of the box, metres.
---   max: vec — Required: the opposite corner (the corners may be given in either order).
function level:objects_in(box) end  --- returns object_query
--- The objects whose script name equals name (case-insensitive), in level order. Returns object[].
function level:objects_named(name) end  --- returns object[]
--- The objects of one kind as object[]: kind names as o.kind spells them ("Light", "MpRespawnPoint", "Keyframe", …) or as level.place{} accepts them ("respawn", "corona", …).
function level:objects_of(kind) end  --- returns object[]
--- An object_query of the objects tagged key (with exactly value when given), in level order.
function level:objects_tagged(key, value) end  --- returns object_query
--- Places an object: kind (+ class for entity/item/clutter/event), pos (metres), optional rot or yaw, name, props {field = value}, links {handles/UIDs}. Returns the object.
---   kind: string — Required: the object kind (light, trigger, item, clutter, entity, event, respawn, …; see catalog.kinds()).
---   class: string — The class for entity, item, clutter and event (required for those; validated against the game tables when an install is mounted).
---   pos: vec — Required: the world position in metres.
---   rot: rotation | {yaw=, pitch=, roll=} | {forward=, up=} = the blueprint's orientation — Orientation in degrees (give rot or yaw, not both); applied after props.
---   yaw: number = the blueprint's orientation — Heading in degrees about +Y (0 faces +Z, positive turns toward +X).
---   name: string = none — The script name.
---   props: {field = value} = the blueprint values — Field values (keys as listed by catalog.describe(kind [, class])), applied in schema order.
---   links: list of handles or UIDs = none — Objects (or movers) to link to.
---   tags: {key = value} = none — Provenance tags written on the new object.
function level:place(args) end  --- returns object
--- Places a new object of kind at (x, y, z) metres, with an optional class name, and returns it. Prefer level.place{…} for options.
function level:place(kind, x, y, z, class_name) end  --- returns object
--- Adds a solid box brush centred at (x, y, z), w×h×d metres, at the end of time; texture defaults to the editor's per-orientation defaults. Returns the brush.
function level:place_box(x, y, z, w, h, d, texture) end  --- returns brush
--- Places a new event of the given class at (x, y, z) metres and returns it.
function level:place_event(class_name, x, y, z) end  --- returns object
--- The level's Player Start: with {pos=, rot=|yaw=} creates it (or moves/rotates the existing one); with no table returns it or nil.
---   pos: vec = unchanged (required to create one) — The Player Start position in metres.
---   rot: rotation | {yaw=, pitch=, roll=} | {forward=, up=} = unchanged — Orientation in degrees (give rot or yaw, not both).
---   yaw: number = unchanged — Heading in degrees about +Y (0 faces +Z).
function level:player_start(args) end  --- returns object
--- The stock level properties (level_info / level_properties): level_name, author, date, multiplayer, geomod_texture, hardness, ambient_color, fog_*, rfl_version.
level.props = nil  --- props
--- Saves the level (destructive: needs confirmation or --@allow-destructive; refused in Dry-Run). Same as ops.save.
function level:save(path) end  --- returns nil
--- Lines textures up like a mapper (default every brush): walls on the floor line, wrapped round corners, never mirrored; slopes and rock at true length; girders run up posts. Returns uv_align_report.
---   brushes: brush | brush[] | uid[] | brush_query = every brush — The brushes to align (groups never reach outside them).
---   selection: boolean = false — Align the selected brushes instead.
---   ppm: number = 64 — Texel density: pixels of each texture per metre (RED's stock density).
---   fit: boolean = true — Scale each wall group so its height spans whole repeats when that changes the density by at most fit_tolerance.
---   fit_tolerance: number = 0.15 — Largest density change a height fit may make (0 to 0.5).
---   keep_fitted: boolean = true — Leave faces already fitted to whole tiles (crate sides, panels: both axes span whole repeats from a tile corner).
---   max_fold: number = 100 — Largest angle in degrees between two faces' normals for the texture to continue around their shared edge.
---   max_twist: number = 30 — Largest turn away from upright (degrees) a texture folded around an edge may take before the face is set upright again.
---   members: string = "auto" — Thin members (beams, posts, pilasters, rails: at least 3 times longer than their widest side, at most 1.25 m wide): "auto" lays a grained texture (girder, trim strip, post) along each member, one repeat at most across it, and leaves members already laid that way; "only" does just that and leaves every other face; "off" aligns members like walls.
function level:uv_align(options) end  --- returns table
--- Measures texture alignment on brush faces (default every brush): seams, density, stretch, mirrored walls, tile lines, thin members, the worst places (issues). Read-only; grader G21 checks the same.
---   brushes: brush | brush[] | uid[] | brush_query = every brush — The brushes to measure.
---   selection: boolean = false — Measure the selected brushes instead.
---   issues: integer = 20 — List at most this many worst problems (0 to 500), worst first.
function level:uv_report(options) end  --- returns table

--- Query and mutate the current selection (objects, brushes, faces).
selection = {}
--- Adds the given object[] to the object selection.
function selection:add(objects) end  --- returns nil
--- A chainable object_query over the current object selection.
function selection:all() end  --- returns object_query
--- The selected brushes in CSG time order (brush[]).
selection.brushes = nil  --- brush[]
--- Selects the object with this UID (replacing the selection) and returns it, or nil when there is none.
function selection:by_uid(uid) end  --- returns object
--- Deselects every object.
function selection:clear() end  --- returns nil
--- The number of selected level objects (brushes: selection.brushes).
selection.count = nil  --- number
--- Deletes the selected objects (destructive: needs confirmation or --@allow-destructive) and returns how many were deleted.
function selection:delete() end  --- returns number
--- The selected faces, ordered by brush time order then face index (face[]).
selection.faces = nil  --- face[]
--- Inverts the object selection.
function selection:invert() end  --- returns nil
--- The selected level objects as object[].
selection.objects = nil  --- object[]
--- An object_query over all level objects of one kind (e.g. "Light").
function selection:of_kind(kind) end  --- returns object_query
--- Selects every level object.
function selection:select_all() end  --- returns nil
--- Replaces the object selection with the given object[].
function selection:set(objects) end  --- returns nil
--- Replaces the brush selection with the given brushes (an empty list clears it).
function selection:set_brushes(brushes) end  --- returns nil
--- Replaces the face selection with the given faces (an empty list clears it).
function selection:set_faces(faces) end  --- returns nil
--- An object_query over ALL level objects for which fn(object) returns true (not limited to the selection).
function selection:where(predicate) end  --- returns object_query

--- Texture/asset lookup, where-used (where_used_by), texture provenance of the level (texture_sources), and the bulk replace_texture op.
assets = {}
--- True when a game asset library (an RF install) is mounted; asset lookups find nothing without one.
assets.available = nil  --- boolean
--- A brush_query over the brushes with at least one face using this texture.
function assets:brushes_using(texture) end  --- returns brush_query
--- True when the mounted asset library has a file with this name (e.g. "rock02.tga").
function assets:exists(name) end  --- returns boolean
--- True when the level references this asset (texture, mesh, sound…) anywhere.
function assets:is_used(name) end  --- returns boolean
--- Replaces texture old with new on every brush face in the level (one undo step) and returns how many faces changed.
function assets:replace_texture(old_texture, new_texture) end  --- returns number
--- Every texture the level uses as texture_info[], sorted by name: stock = every player has it; unresolved ones have exists = false. The portability check.
function assets:texture_sources() end  --- returns texture_info[]
--- Every texture name in the mounted asset library as string[] (empty without one); catalog.textures() filters and limits.
function assets:textures() end  --- returns string[]
--- The base names of the textures the level references, as string[].
function assets:used_textures() end  --- returns string[]
--- Every place the level references this asset, as asset_usage[] (kind, description, referenced_as, uid).
function assets:where_used_by(name) end  --- returns asset_usage[]

--- Heavy operations: build, light, check_holes / holes, save, package, playtest. Builds use the same options as the editor's own Build / Calculate Lighting (the user's build method, Repair Holes When Building setting and lightmap method in the editor; the editor defaults elsewhere); hole checks always measure a sealed build and count what remains after that build's repair (they never repair on their own). Build, light, save and package first refresh the mover copies of member brushes the script edited. `playtest` saves the level and launches it through the editor's Play flow once the script finishes; outside the interactive editor it raises an error.
ops = {}
--- Compiles a sealed build with the editor's build method and hole-repair setting and applies it (no lighting). Returns op_report (count = faces; repair = repair_report or nil). Not in Dry-Run.
function ops:build() end  --- returns op_report
--- Runs the leak check on a sealed build (never a preview) and returns the hole count; positions go to the log. ops.holes() returns them.
function ops:check_holes() end  --- returns number
--- A plain-text summary of what in the level needs Alpine Faction rather than stock RF. Read-only.
function ops:compat() end  --- returns string
--- Runs the leak check on a sealed build and returns hole_report (count, is_watertight, positions). Does not change the level.
function ops:holes() end  --- returns hole_report
--- Compiles geometry and bakes lightmaps (shadows default true) with the editor's lightmap method. Returns op_report. Not in Dry-Run.
function ops:light(shadows) end  --- returns op_report
--- Builds a .vpp package of the level and its custom assets (destructive), optionally to path and as multiplayer. Returns op_report.
function ops:package(path, multiplayer) end  --- returns op_report
--- Saves and launches the level in-game once the script finishes (destructive; interactive editor only, an error elsewhere).
function ops:playtest(multiplayer) end  --- returns nil
--- Saves the level, optionally to path (destructive: needs confirmation or --@allow-destructive; refused in Dry-Run).
function ops:save(path) end  --- returns nil

--- Run the level linter and contribute custom findings.
lint = {}
--- Adds a custom finding (severity "error", "warning" or "info", message, optional uid) to later lint.run() reports.
function lint:add(severity, message, uid) end  --- returns nil
--- The findings this script added with lint.add, as lint_finding[].
function lint:contributed() end  --- returns lint_finding[]
--- Runs the level linter (plus findings this script added with lint.add) and returns a lint_report. Read-only.
function lint:run() end  --- returns lint_report

--- Write to the Script Log (info/warn/error). Lua print() also lands here.
log = {}
--- Clears this run's collected log entries.
function log:clear() end  --- returns nil
--- The log entries this run has written so far (level, message, timestamp).
log.entries = nil  --- log_entry[]
--- Writes an error line to the Script Log without stopping the run (use error() to fail and roll back).
function log:error(message) end  --- returns nil
--- Writes an info line to the Script Log.
function log:info(message) end  --- returns nil
--- Writes a plain output line to the Script Log (what print() does).
function log:output(message) end  --- returns nil
--- Writes a warning line to the Script Log (the run still succeeds).
function log:warn(message) end  --- returns nil

--- Seeded, deterministic random source (reproducible procedural scripts). Re-seed with rng.set_seed(n); rng.seed reads the current seed.
rng = {}
--- A random boolean (50/50).
function rng:bool() end  --- returns boolean
--- A random number in [0, 1).
function rng:float() end  --- returns number
--- A random integer in [min, max], both inclusive.
function rng:int(min, max) end  --- returns number
--- A uniformly chosen element of the list, or nil when it is empty.
function rng:pick(items) end  --- returns any
--- A random number in [min, max).
function rng:range(min, max) end  --- returns number
--- The seed the deterministic stream started from (the run's seed, or the last rng.set_seed).
rng.seed = nil  --- number
--- Restarts the deterministic random stream at seed n: the same seed gives the same sequence.
function rng:set_seed(seed) end  --- returns nil

--- Discover what can be placed: kinds, classes (from the game tables when an RF install is mounted, and the event catalog), each class's fields, textures with their provenance (stock, origin, source), and nearest-name lookups.
catalog = {}
--- True when the game's entity/item/clutter tables are loaded (an RF install is mounted).
catalog.available = nil  --- boolean
--- The class names of a kind: Entity/Item/Clutter from the game tables (empty when not loaded), Event from the event catalog; other kinds {}.
function catalog:classes(kind) end  --- returns string[]
--- Describes a kind, or one class of it: category, description, links and every settable field. Returns class_info.
function catalog:describe(kind, class_name) end  --- returns class_info
--- Every placeable event class with its fields (class_info[]), stock then Alpine.
function catalog:events() end  --- returns class_info[]
--- The kinds level.place{} accepts, in kind order (e.g. "Entity", "Light", "Event").
function catalog:kinds() end  --- returns string[]
--- The n (default 5) class names of a kind nearest to name, best first.
function catalog:nearest(kind, name, n) end  --- returns string[]
--- Every metric id catalog.norms accepts, sorted.
function catalog:norm_metrics() end  --- returns string[]
--- Typical value range of a level metric (norm_info). No bucket: the curated reference set (else overall); a bucket ("overall", "dm", "exemplar.dm", …): that one, or nil.
function catalog:norms(metric, bucket) end  --- returns norm_info
--- The norms for a kind of level (norm_info): the most specific published bucket for its gametype, level class and player count (such as "dm.S"), else catalog.norms' reference chain.
---   metric: string — Required: the metric id (catalog.norm_metrics()).
---   gametype: string = any — The level's gametype bucket: "dm", "ctf", "koth", "pay", … (none: the reference set, else overall).
---   class: string = any — The level class: "standard", "open_air" or "payload" (the design grader's classes).
---   players: integer = any — The intended player count: picks the player-class bucket like the design grader (up to 8 players S, up to 16 M, more L; corpus levels are classed by spawn count).
function catalog:norms_for(options) end  --- returns norm_info
--- One texture's provenance (texture_info): stock, origin, source, overridden, size; nil when it does not resolve or no install is mounted.
function catalog:texture_info(name) end  --- returns texture_info
--- Textures as texture_info[] from {filter=, limit=, stock=, origin=}: provenance per texture, sorted by name; {} without an install.
---   filter: string = none — A substring, or a glob with * and ?, matched case-insensitively against the texture name.
---   limit: integer = 200 — The most results (1 to 5000).
---   stock: boolean = either — true: only textures every player has (a retail packfile contains them); false: only the others.
---   origin: string = any — Only textures whose shown file comes from "stock", "alpine", "packfile" (another .vpp) or "loose" (a folder).
function catalog:textures(options) end  --- returns texture_info[]
--- Texture names in the mounted library, sorted; filter is a substring, or a glob with * and ?; limit defaults to 200 (max 5000). {} without an install.
function catalog:textures(filter, limit) end  --- returns string[]

--- The prefab library (the editor's prefab folder): list the .gedprefab files and place tracked instances — the same placement as dropping a prefab in the editor. Files are only ever read from the library folder.
prefab = {}
--- Every prefab in the library folder (recursive), sorted by name: name, file, description, author, brush_count, object_count. Returns prefab_info[].
function prefab:list() end  --- returns prefab_info[]
--- Places a prefab by name (or library file) with its pivot at pos (metres), optional rot or yaw and tags {k = v} on every member. Returns prefab_instance.
---   name: string — Required: the prefab's name, or its file as prefab.list() shows it.
---   pos: vec — Required: where the prefab's pivot lands, metres.
---   rot: rotation | {yaw=, pitch=, roll=} | {forward=, up=} = no rotation — Orientation in degrees (give rot or yaw, not both).
---   yaw: number = 0 — Heading in degrees about +Y.
---   tags: {key = value} = none — Provenance tags written on every placed member.
function prefab:place(args) end  --- returns prefab_instance

--- The detail module library (library/modules): architectural and set-dressing modules harvested from approved levels — pilasters, base/top trims, bands, wall panels, recesses, door and window frames, beams, trusses, columns, railings, catwalks, pipes, machinery, consoles, rocks — each with its local frame, stretch slices and repeating units. list{…} filters them (best score first), fit{…} previews a size, place{…} instances one as detail brushes tagged kit.module / kit.role / kit.part. An absent library is normal: available = false, reason says why, lists are empty. The kit's detailing functions (kit.wall_detail, kit.detail …) build on it.
modules = {}
--- True when a module library with at least one module was found.
modules.available = nil  --- boolean
--- The number of modules in the library (0 when absent).
modules.count = nil  --- number
--- The size a module would get for the requested length / height / depth, and which axes cannot stretch — without changing the level. Returns {ok, size, tile_counts, fixed = {axis…}, message}.
---   id: string — Required: the module id.
---   length: number = natural — Wanted size on local x (metres).
---   height: number = natural — Wanted size on local y (metres).
---   depth: number = natural — Wanted size on local z (metres).
function modules:fit(args) end  --- returns table
--- The module with this id (case-insensitive), or nil. A module the mapper rejected is returned too (its verdict says so); fit and place refuse it.
function modules:get(id) end  --- returns module_info
--- The modules matching the filters, best score first (then id): module_info[]. An absent library gives an empty list.
---   category: string = any — Only this category (pilaster, base_trim, top_trim, band, wall_panel, recess, door_frame, beam, truss, column, railing, catwalk, pipe, machinery, console, rock, …).
---   attach: string = any — Only this attachment kind: wall, corner, floor, ceiling, column, span, edge, opening or free.
---   role: string = any — Only this role: cover, route, landmark, theme_detail or fixture.
---   style: string = any — Only modules listing this style family (industrial, bunker, admin, mine, cave, canyon, mars_base, lab, tech, generic).
---   group: string = any — Only this library group (e.g. "stock").
---   stock_only: boolean = false — Only modules whose every texture is stock.
---   resolvable: boolean = true — Only modules whose every texture resolves in the mounted assets (stock textures when no assets are mounted).
---   min_size: {x, y, z} = none — Smallest natural size per local axis (metres).
---   max_size: {x, y, z} = none — Largest natural size per local axis (metres).
---   stretch: string = any — Only modules that can stretch on this axis: "x", "y" or "z".
---   source: string = any — Only modules found in a level whose path contains this text.
---   include_rejected: boolean = false — Also modules the mapper rejected in the library's curation.json.
---   limit: integer = all — At most this many results.
function modules:list(args) end  --- returns module_info[]
--- The library folder (…/library/modules), or nil when none was located.
modules.path = nil  --- string
--- Places a module: local origin at pos, turned by yaw (local +Z = the room side), stretched to length/height/depth; brushes are tagged kit.module, kit.role, kit.part. Returns {brushes, size, id, role}.
---   id: string — Required: the module id (modules.list / modules.get).
---   pos: vec — Required: the world point the module's local origin lands on (module_info.anchor says what it is).
---   yaw: number = 0 — Heading in degrees about +Y: 0 keeps local +Z on world +Z, 90 turns it onto world +X.
---   length: number = natural — Size on local x (metres; the module must stretch on x).
---   height: number = natural — Size on local y (metres; the module must stretch on y).
---   depth: number = natural — Size on local z (metres; the module must stretch on z).
---   mirror: boolean = false — Flip local x (faces stay outward, textures readable).
---   detail: boolean = true — Make every solid member a detail brush, so a placed module never unseals the level (air members stay air).
---   textures: {old = new} = none — Texture replacements by name.
---   tags: {key = value} = none — Extra tags on every brush (kit.module, kit.role and kit.part are always set).
---   name: string = none — Tags every brush kit.name = name.
function modules:place(args) end  --- returns table
--- Why the library is unavailable (nil when it is available).
modules.reason = nil  --- string
--- The mean brightness (0-1) of a texture as the mounted assets decode it, or nil without assets or when it cannot be decoded (for matching module materials to a room's).
function modules:texture_brightness(texture) end  --- returns number?

--- Geometry tools: check, roughen and deform brushes (results stay convex and planar), extrude/bevel faces, weld/snap vertices, and builders for organic brush chains (tube, passage, rock_row, scatter, pit). Build convex shapes with level.brush_from_points{…}.
geo = {}
--- Bends brushes: their extent along axis wraps into an arc of degrees curling toward toward; cut into pieces that meet on exact shared faces. Returns brush_query.
---   brushes: brush | list | brush_query — Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.
---   axis: string = "z" — The length axis that wraps into the arc: "x", "y" or "z": a world axis through the brush's bounds centre (mode = "triangulate": the brush's own axis).
---   toward: string = "y" — The axis the ends curl toward (not the length axis).
---   degrees: number — Required: the total arc (−180 to 180, not 0); negative curls the other way.
---   pieces: integer = max(2, ceil(|degrees| / 15)) — Slabs along the length axis (1 to 32), each rebuilt convex; neighbours overlap by a small lap. Hull mode only.
---   mode: string = "hull" — "hull" (default): world vertices are moved, snapped to 1/1024 m and rebuilt as a convex hull (needs a convex brush; UVs are re-box-mapped). "triangulate": the editor's deformer, then bent faces are triangulated (keeps UVs; may leave a non-convex brush).
---   tile: number = 4 — Metres per texture repeat of the world box map given to rebuilt faces (0.25 to 64).
function geo:bend(args) end  --- returns brush_query
--- Bevels (insets) faces: each face shrinks toward its centroid by amount (0.01 to 0.99) and a ring of coplanar faces fills the border. Returns brush_query.
---   faces: face | face[] — Required: the faces to bevel.
---   amount: number — Required: how far each corner moves toward the face centroid, as a fraction (0.01 to 0.99).
---   tile: number = 4 — Metres per texture repeat of the world box map on the new ring faces (0.25 to 64).
function geo:bevel(args) end  --- returns brush_query
--- Audits a brush: closed (every edge shared by two faces, once each way), wound as RED stores it (solid out, air in), convex and planar (corners within 0.0001 m). Changes nothing. Returns geo_report.
function geo:check(brush) end  --- returns geo_report
--- Extrudes faces out of their brush (air brushes too) by distance metres, walling the sides (new walls box-mapped at tile). A non-convex result is kept and warned about. Returns brush_query.
---   faces: face | face[] — Required: the faces to extrude (several faces of one brush are extruded in descending face index).
---   distance: number — Required: metres along each face's outward normal (0.001 to 4096 in size; negative pushes the face in).
---   tile: number = 4 — Metres per texture repeat of the world box map on the new side walls (0.25 to 64).
function geo:extrude(args) end  --- returns brush_query
--- Roughens brushes: each vertex moves up to amount metres per axis (seeded), then the brush is rebuilt as a convex hull on the 1/1024 m grid. Returns brush_query.
---   brushes: brush | list | brush_query — Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.
---   amount: number | vec — Required: the largest move per axis in metres (0 to 16); a vec sets each axis — {0.3, 0, 0.3} keeps every vertex at its height, so flat floors stay flat.
---   seed: integer = 1 — The random stream; brush k of the (time-ordered) list draws from stream (seed, k), so the result does not depend on UIDs.
---   mode: string = "hull" — "hull" (default): world vertices are moved, snapped to 1/1024 m and rebuilt as a convex hull (needs a convex brush; UVs are re-box-mapped). "triangulate": the editor's deformer, then bent faces are triangulated (keeps UVs; may leave a non-convex brush).
---   tile: number = 4 — Metres per texture repeat of the world box map given to rebuilt faces (0.25 to 64).
function geo:jitter(args) end  --- returns brush_query
--- Builds a level corridor along a floor path on any heading: one exact air prism per leg, mitred so neighbours meet on identical faces. Returns tube.
---   path: vec[] — Required: 2 to 256 floor points at one height (after rounding to 1/16 m), turning at most 120° per point.
---   width: number = 4 — Clear width in metres; corners are mitred so the width holds on every heading.
---   height: number = 4 — Clear height in metres (rounded to 1/16 m).
---   detail: boolean = false — Make the air brushes detail.
---   min_clear_width: number = 1.8 — The smallest width allowed, metres (0 allows any).
---   min_headroom: number = 2 — The smallest height allowed, metres (0 allows any).
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   tags: {key = value} = none — Provenance tags written on every brush.
---   part: string = none — When given, every brush is tagged kit.part = part followed by its 1-based number. Not padded (seg1).
function geo:passage(args) end  --- returns tube
--- Digs a rough pit down from a level floor point: a stack of convex air brushes on tapering rings meeting on identical faces, with a flat floor depth metres down. Returns pit.
---   pos: vec — Required: a point on a level floor where the pit opens (y rounds to 1/16 m; the pit's mouth lies exactly in that floor's plane).
---   radius: number = 4 — Radius at the top, metres (0.5 to 512).
---   depth: number = 4 — Depth of the flat floor below pos, metres (rounded to 1/16, 1 to 1024).
---   sides: integer = 8 — Points per ring (6 to 16).
---   taper: number = 0.7 — Bottom radius as a fraction of the top radius (0.2 to 2).
---   roughness: number = 0.2 — Radial jitter as a fraction of the radius (0.05 to 0.5).
---   seed: integer = 1 — The random stream (an integer); the same options and seed give the same brushes. Every brush is tagged geo.seed.
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   tags: {key = value} = none — Provenance tags written on every brush.
---   part: string = none — When given, every brush is tagged kit.part = part followed by its 1-based number. Not padded (ring1).
function geo:pit(args) end  --- returns pit
--- Builds a row of overlapping rough detail rocks along the floor line from → to (flat_top: a walkable top); structural = true builds one rock ridge standing on the floor. Returns rocks.
---   from: vec — Required: the floor point the row starts at (y rounds to 1/16 m).
---   to: vec — Required: the floor point the row ends at.
---   height: number — Required: visible height above the floor, metres (0.1 to 256).
---   depth: number = 2 — Thickness across the line, metres (0.25 to 256).
---   spacing: number = 0.8 × depth — Distance between rock centres, metres; each rock is 1.5 × spacing long, so neighbours overlap.
---   flat_top: boolean = false — Clip every rock at one 1/16 m-aligned height: a flat, walkable top.
---   structural: boolean = false — A structural ridge instead of detail rocks: one chain of convex pieces meeting on identical faces, standing exactly on the floor (seals and splits rooms; audited against the level).
---   roughness: number = 0.2 — Jitter as a fraction of the rock size (0.05 to 0.5).
---   points: integer = 12 — Samples per rock (8 to 24).
---   seed: integer = 1 — The random stream (an integer); the same options and seed give the same brushes. Every brush is tagged geo.seed.
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   tags: {key = value} = none — Provenance tags written on every brush.
---   part: string = none — When given, every brush is tagged kit.part = part followed by its 1-based number. Zero-padded to 3 digits (rock001).
function geo:rock_row(args) end  --- returns rocks
--- Scatters count detail boulders on a rectangle (area = {min=, max=}) or on a tube's floor (along = tube, clear of its walkway), never closer than their radii plus gap. Returns rocks.
---   area: {min=, max=} — A floor rectangle (its floor is min.y); give area or along.
---   along: tube — A tube (geo.tube / geo.passage record): rocks go on its floor, clear of its walkway.
---   count: integer = 10 — How many rocks (1 to 500); an error names how many fit when not all do.
---   size: number | {min, max} = {0.6, 2} — Rock diameter in metres, or the range it is drawn from.
---   margin: number = 0.5 (area) / 0.25 (along) — Area: how far every rock stays inside the rectangle's edges. Along: how far a rock's centre stays inside the floor's edge (rocks may lean into the wall foot), metres.
---   gap: number = 0.25 — Extra clearance between neighbouring rocks, metres.
---   band: number = none — Along only: keep each rock's centre within this many metres of the floor's edge (rubble at the wall foot, the open middle left clear); none = anywhere outside the walkway.
---   roughness: number = 0.2 — Jitter as a fraction of the rock size (0.05 to 0.5).
---   points: integer = 12 — Samples per rock (8 to 24).
---   seed: integer = 1 — The random stream (an integer); the same options and seed give the same brushes. Every brush is tagged geo.seed.
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   tags: {key = value} = none — Provenance tags written on every brush.
---   part: string = none — When given, every brush is tagged kit.part = part followed by its 1-based number. Zero-padded to 3 digits (rock001).
function geo:scatter(args) end  --- returns rocks
--- Snaps every vertex to a world grid (default 1/1024 m) and rebuilds the convex hull — the repair for float noise on hand-made or imported brushes. Returns brush_query.
---   brushes: brush | list | brush_query — Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.
---   grid: number = 1/1024 — The world grid every vertex snaps to: a power of two from 1/1024 to 16 metres.
---   mode: string = "hull" — "hull" (default): snap, then rebuild the convex hull (needs a convex brush). "triangulate": the editor's Snap to Grid, then bent faces are triangulated.
---   tile: number = 4 — Metres per texture repeat of the world box map given to rebuilt faces (0.25 to 64).
function geo:snap(args) end  --- returns brush_query
--- Tapers brushes along a world axis: the cross-section scales linearly from 1 at the low end to scale at the high end, about the bounds centre. Returns brush_query.
---   brushes: brush | list | brush_query — Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.
---   axis: string = "y" — The taper axis: "x", "y" or "z": a world axis through the brush's bounds centre (mode = "triangulate": the brush's own axis).
---   scale: number — Required: the cross-section scale at the high end of the axis (0.05 to 20); 1 at the low end.
---   pieces: integer = 1 — Slabs along the axis (1 to 32), each rebuilt convex; neighbours meet on identical flat faces. Hull mode only.
---   mode: string = "hull" — "hull" (default): world vertices are moved, snapped to 1/1024 m and rebuilt as a convex hull (needs a convex brush; UVs are re-box-mapped). "triangulate": the editor's deformer, then bent faces are triangulated (keeps UVs; may leave a non-convex brush).
---   tile: number = 4 — Metres per texture repeat of the world box map given to rebuilt faces (0.25 to 64).
function geo:taper(args) end  --- returns brush_query
--- Builds a cave or canyon along a floor path: convex air brushes meeting on identical faces, a level or smoothly sloped floor per leg, a guaranteed walkway, seeded rough walls. Returns tube.
---   path: vec[] — Required: 2 to 256 floor points (x, z snap to 1/1024 m; y rounds to 1/16 m); legs at least 2 m apart, turning at most 90° per point, climbing at most max_slope (the floor slopes smoothly along each leg). Each leg's end moves by up to half a grid step per segment so its stations are exact, and later points move with it (a centimetre or two on very long paths); read stations for the points used.
---   profile: string = "cave" — "cave" (flat floor under a rough arch) or "canyon" (flat floor, leaning walls, one common horizontal top).
---   radius: number = 3 (cave) / 6 (canyon) — Half-width in metres (give one of radius, radii, width, widths).
---   radii: number[] = none — Half-width per path point, interpolated along the path.
---   width: number = 6 (cave) / 12 (canyon) — Full width in metres (2 × radius); a canyon's floor width.
---   widths: number[] = none — Full width per path point, interpolated along the path.
---   height: number = 4.5 (cave) / 16 (canyon) — Cave: clear height at the crown. Canyon: rim height above the highest floor.
---   heights: number[] = none — Cave only: clear height per path point.
---   roughness: number = 0.2 — Wall jitter as a fraction of the size (0.05 to 0.5).
---   floor_flatness: number = 0.6 — Cave only: floor half-width as a fraction of the radius (0.3 to 1).
---   lean: number = 0.15 — Canyon only: how far the walls lean out per metre of height (0 to 0.5).
---   sides: integer = 8 — Cave only: points per ring (6 to 14).
---   segments: integer = from spacing and the climb — Segments per leg (give segments or spacing).
---   spacing: number = max(3, radius) (cave) / width / 2 (canyon) — Target distance between stations, metres.
---   seed: integer = 1 — The random stream (an integer); the same options and seed give the same brushes. Every brush is tagged geo.seed.
---   min_clear_width: number = 1.8 — The walkway width the floor must hold along the path, metres (0 skips the check).
---   min_headroom: number = 2 — The clear height over that walkway, metres (0 skips the check).
---   max_slope: number = 1 — The steepest average rise/run of a leg (1 = 45°).
---   texture: string = the editor's per-role defaults — One texture for every face (give texture or textures, not both).
---   textures: {floor=, wall=, ceiling=} = the editor's per-role defaults — Textures by player-relative role (floor = the surface you stand on); faces steeper than 45° are walls.
---   tile: number = 4 — Metres per texture repeat of the world box map (0.25 to 64).
---   tags: {key = value} = none — Provenance tags written on every brush.
---   part: string = none — When given, every brush is tagged kit.part = part followed by its 1-based number. Zero-padded to 3 digits (seg001).
function geo:tube(args) end  --- returns tube
--- Twists brushes about a world axis through their bounds centre: 0° at the low end, degrees at the high end; pieces > 1 slices first. Results stay convex. Returns brush_query.
---   brushes: brush | list | brush_query — Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.
---   axis: string = "y" — The twist axis: "x", "y" or "z": a world axis through the brush's bounds centre (mode = "triangulate": the brush's own axis).
---   degrees: number — Required: the rotation at the high end of the axis (−360 to 360); 0 at the low end.
---   pieces: integer = 1 — Slabs along the axis (1 to 32), each rebuilt convex; neighbours meet on identical flat faces. Hull mode only.
---   mode: string = "hull" — "hull" (default): world vertices are moved, snapped to 1/1024 m and rebuilt as a convex hull (needs a convex brush; UVs are re-box-mapped). "triangulate": the editor's deformer, then bent faces are triangulated (keeps UVs; may leave a non-convex brush).
---   tile: number = 4 — Metres per texture repeat of the world box map given to rebuilt faces (0.25 to 64).
function geo:twist(args) end  --- returns brush_query
--- Welds vertices closer than tolerance metres (default 0.001) and removes faces that collapse; errors if a brush would lose its volume. Returns brush_query.
---   brushes: brush | list | brush_query — Required: the brushes to edit (one brush, a list of handles or UIDs, or a brush_query). Locked and portal brushes are errors.
---   tolerance: number = 0.001 — Vertices closer than this on every axis merge (metres, 0.00001 to 1); faces that collapse are removed.
function geo:weld(args) end  --- returns brush_query

--- Spatial queries on the compiled geometry (build first): raycast, los, point_info and clearance (instant), and the measurements behind the design grader: walk (reachable floor, movement links), routes (k node-disjoint routes), graph (cell / space graphs with loops, chokepoints, pockets), sightlines and exposure, cover, cells (undressed / bare), area_report (vs the exemplar targets), spawn_report, item_report, locations, setpieces (bridge spans, landmark heights, atria), purpose (the function checks: bridges that serve no route, misplaced rocks, orphan cover, hidden landmarks). Every result carries geometry freshness; measurements are cached and budgeted (max_samples, timeout_s).
spatial = {}
--- An area report (region, around + radius, or the level): floor, widths, headroom, sky (outdoor_fraction counts sky faces), ceilings, walls, spaces, dressing, textures, items, spawns vs targets.
---   region: list of 6 numbers = nil — Only this box: {minx, miny, minz, maxx, maxy, maxz} in metres.
---   around: vec | {x, y, z} | handle = nil — Instead of region: everything within radius metres of this point.
---   radius: number = nil — With around: the radius (m).
---   player_class: string = from the spawns — The targets' player class: "S", "M" or "L".
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:area_report(args) end  --- returns table
--- The 8 m cells with their dressing count, visible planes, undressed and bare flags (the cells kit.dress fills).
---   region: list of 6 numbers = nil — Only this box: {minx, miny, minz, maxx, maxy, maxz} in metres.
---   max: integer = 200 — At most this many cells listed.
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:cells(args) end  --- returns table
--- Does a player fit at a floor point: {fits, floor_y, headroom, nearest_wall, reason}. profile "player" (2 m) or "crouch" (1.375 m), radius 0.5 m. Instant.
function spatial:clearance(pos, profile) end  --- returns table
--- Cover: share of the floor beside cover, walk distance to cover (all and open floor), and the cells farthest from cover.
---   region: list of 6 numbers = nil — Only this box: {minx, miny, minz, maxx, maxy, maxz} in metres.
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:cover(args) end  --- returns table
--- How fresh the compiled geometry is: {state = built | preview | stale | none | unknown, measured = "stored", note}. stale = brushes changed since the last build.
function spatial:freshness() end  --- returns table
--- A layout graph: the 8 m / 4 m cell graph or the space graph, with loops, chokepoints (and the share each cuts off), pockets, dead ends and nodes.
---   kind: string = "cell" — "cell" (connected floor per cell) or "space" (spaces and connectors).
---   scale: integer = 8 — Cell size for kind "cell": 8 (layout) or 4 (fine loops).
---   region: list of 6 numbers = nil — List only the nodes inside this box (the totals stay level-wide).
---   max_nodes: integer = 200 — At most this many nodes listed.
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:graph(args) end  --- returns table
--- Every item: category, route-traffic percentile, pocket membership, nearest weapon/powerup; plus the level figures.
function spatial:item_report() end  --- returns table
--- Locations of one kind, worst first: chokepoints, pockets, dead_ends, bare_cells, undressed_cells, sightlines, exposed_spawns, exposed_spots, items, spawns_far_from_items, spaces, …
function spatial:locations(kind, max) end  --- returns table
--- True when nothing blocks the straight line between two points (vec, {x, y, z} or an object/brush handle). Instant.
function spatial:los(a, b) end  --- returns boolean
--- What is at a point: {in_solid, floor{y, distance}, ceiling{y, distance}, headroom, open_sky, room, geometry}. Instant.
function spatial:point_info(pos) end  --- returns table
--- Function checks (grader G27-G31): bridges and stair flights with the route each serves or its fault, route ledges, area ways out, misplaced rocks, orphan cover, landmarks. Seconds, then cached.
---   max: integer = 20 — At most this many entries per list (bridges, mismatched, orphans, landmarks).
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:purpose(args) end  --- returns table
--- The first face along a ray: {hit, distance, pos, normal, back_face, face, room, texture, sky, geometry}. Instant.
---   from: vec | {x, y, z} | handle — Required: the ray origin (m).
---   dir: vec | {x, y, z} — Required: the direction (any length).
---   max: number = 200 — The longest distance to look (m).
function spatial:raycast(args) end  --- returns table
--- Routes between two points: the shortest walk (length, waypoints) and up to k node-disjoint routes on the 8 m cell graph (lengths, waypoints).
---   a: vec | {x, y, z} | handle — Required: the start (snapped to the playable floor within 6 m).
---   b: vec | {x, y, z} | handle — Required: the end.
---   k: integer = 3 — How many node-disjoint routes to look for (1-8).
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:routes(args) end  --- returns table
--- Set pieces: bridges (span = deck end to end, gap_width, width, gap_depth), walkways that are not bridges (with why), landmarks (height), atria and the largest spaces. Seconds, then cached.
---   max: integer = 10 — At most this many entries per list (bridges, walkways, landmarks, atria, largest spaces).
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:setpieces(args) end  --- returns table
--- Sightlines and AREA exposure (G20: floor within 50 m a spot sees; not the spawn measure): engagement, longest sightlines, visible pairs, exposed spots; with region, from spots in it.
---   region: list of 6 numbers = nil — Only this box: {minx, miny, minz, maxx, maxy, maxz} in metres.
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:sightlines(args) end  --- returns table
--- Every playable spawn: nearest spawn, SPAWN exposure (G19: playable floor within 40 m seen; kit.spawn_points uses it), nearest item, spawns in sight within 30 m; plus the level figures.
function spatial:spawn_report() end  --- returns table
--- The walk graph: floor components, unreachable spawns and items, movement links (lifts, jump pads, teleports); with from, the floor reachable from there. Seconds, then cached.
---   from: vec | {x, y, z} | handle = nil — Also report the floor reachable on foot (and by lifts, jump pads, teleports) from this point.
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 60 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function spatial:walk(args) end  --- returns table

--- The self-review grader: design.grade{} returns PASS / REVIEW / FAIL with each failing check's values, fix hint and locations (the same code as glacier-cli grade and the grade_level tool); design.checks() lists the checks.
design = {}
--- The grader's checks: a list of {id, group, hard, check, fix, class} from the shipped threshold table.
function design:checks() end  --- returns any[]
--- Grades like glacier-cli grade: {verdict, failures_count, class, failures[{id, check, values, fix, locations}], passed, targets, advisories, set_pieces, geometry}. Grade, fix by location, regrade.
---   gametype: string = detected — dm, ctf, koth, payload, …; detected from the level (Payload_Handler, CTF flags, capture points, file name) when omitted.
---   players: integer = from the spawns — The intended player count (sets the player class whose targets are reported).
---   class: string = detected — Force the level class: "standard", "open_air" or "payload".
---   max_locations: integer = 6 — Locations listed per failure.
---   metrics: boolean = false — Also return the whole metric table.
---   build: string = "none" — "sealed" runs ops.build() first (applied, like ops.build; not in a dry run) so the current brushes are measured; "none" measures the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 120 — Measurement time limit in seconds; past it the call fails with a hint (raise it or lower max_samples).
function design:grade(args) end  --- returns table

