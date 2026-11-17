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
--- The Alpine level properties: legacy timers/movers, headlamp, RF2-style geomod, payload_* rules, and the Alpine 1.5 lighting, sun_*, minimap_*, flight ceiling and far clip settings.
level.alpine = nil  --- props
--- Craft audit: sunk/floating/random props, stairs and bridges leading nowhere, perches, openings into solid or void, purpose-off pieces, flat rock, basic areas. {clean, total, counts, findings}.
---   checks: string | list of strings = all — Only these checks: props, stairs, perches, openings, purpose, rock, density.
---   max: integer = 40 — At most this many findings listed per check (counts stay complete).
---   build: string = "none" — "sealed" runs ops.build() first so the current brushes are audited; "none" audits the stored geometry.
---   max_samples: integer = 600000 — Floor-lattice sample budget; above it the 0.5 m pitch doubles (coarser, faster).
---   timeout_s: number = 120 — Measurement time limit in seconds.
function level:audit(args) end  --- returns table
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
--- The level object with this UID, or nil — the handle for a uid an audit finding, a report or a log names (find_uid under another name; brushes: level.find_brush).
function level:object(uid) end  --- returns object
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
---   seat: boolean = false — Clutter, items and mesh objects: treat pos as the floor point and lift the object so the bottom of its mesh (as rotated) rests on it. Stock clutter meshes have their origin at their centre, so without seat they sink half their height (an Ultor crate about 0.46 m). Needs an RF install for the meshes.
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
--- The Level Flags Outside / Inside buttons: flags every built room and every room effect outside (true) or inside (false), as RED does. Returns how many rooms and room effects changed.
function level:set_all_rooms_outside(outside) end  --- returns number
--- Counts from the last build: {brushes, objects, built, faces, rooms, surfaces, surface_cap (32767), unlit_faces (lit faces with no lightmap surface), unlit_min/max, surface_budget_ok}.
function level:stats() end  --- returns table
--- The level's Alpine terrains: level.terrain.create{…}, all(), get(uid or name). Build Geometry compiles each into detail rooms, one per chunk.
level.terrain = nil  --- terrains
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
--- Compiles and bakes lightmaps (shadows default true) with the lightmap method and the level's pipeline (ops.lightmaps). Returns op_report (lightmaps = lightmap_report). Not in Dry-Run.
function ops:light(shadows) end  --- returns op_report
--- Reads or sets the lightmap pipeline ("alpine" or "legacy"), density (0 = 8 per unit, 1-128, 255 = off, -1 keeps) and bake codec ("bc7" or "raw"; not saved). Returns lightmap_settings.
function ops:lightmaps(pipeline, density, codec) end  --- returns lightmap_settings
--- Builds <level>.vpp beside the saved level (or at path) from the level file and its custom assets, saving unsaved changes first (destructive). multiplayer has no effect. Returns op_report.
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
--- Runs the level linter (plus findings this script added with lint.add) and returns a lint_report; the craft audit is level.audit{}. Read-only.
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

--- Recreating from an overhead reference picture (map, mockup, drawing): ref.open, ref.grid (a labelled pixel grid to read exact coordinates), ref.calibrate (pixels to world metres; to_world / to_px / world_points), ref.mask (trace the playable area by colour into polygons), ref.compare (the level's walkable floor against the mask: IoU, wall error in metres, the worst missing / extra regions, an overlay), ref.heights (a heights picture decoded into floor heights, flat regions, ramps, stairs and drop edges). Build what you traced with kit.trace{...}.
ref = {}
--- Sets the picture-to-world mapping: scale from bar = {x1, y1, x2, y2, metres} (scale bar or known length), ppm or mpp; origin px lands at world at = {x, z}; up = "+z" or rotation. Returns it.
---   bar: {x1, y1, x2, y2, metres} — Scale from two pixels a known distance apart (a scale bar, a door, a corridor width).
---   ppm: number — Scale as picture pixels per metre (instead of bar).
---   mpp: number — Scale as metres per picture pixel (instead of bar).
---   origin: {x, y} = the picture's centre — The pixel that lands on world point at.
---   at: {x, z} = {0, 0} — World x, z of origin.
---   up: string = "+z" — World direction the picture's top points to: "+z" (north up, like render_plan), "+x", "-z", "-x". Picture down is then -Z.
---   rotation: number = 0 — Instead of up: the heading of the picture's top in degrees (0 = +Z, 90 = +X).
---   path: string = current — Use this picture (and make it current) instead of the one ref.open registered.
function ref:calibrate(args) end  --- returns table
--- The level's walkable floor vs the mask: {score, iou, iou_tol, walls_mean_m, missing_m2, extra_m2, worst = {{kind, area_m2, box_world, center}}}; out writes an overlay.
---   mask: string = the last ref.mask — A mask .png the picture's size (white = playable) instead of the last ref.mask.
---   out: string = none — Write the overlay .png: picture dimmed, floor cyan, missing red, extra orange, mask edge yellow, worst regions boxed.
---   crop: {x, y, w, h} = the mask's bounds + margin — Compare only this window of the picture.
---   y: {min, max} = all heights — Only floor between these heights (one storey).
---   worst: number = 5 — How many worst regions to list.
---   build: string = "none" — "sealed" runs ops.build() first so the current brushes are measured.
---   path: string = current — Use this picture (and make it current) instead of the one ref.open registered.
function ref:compare(args) end  --- returns table
--- Writes the picture (or a zoomed crop) with a labelled pixel grid; overlay draws your trace (a kit.trace table or polygons) or "level" (built floor per height) on it. Returns {path, step, crop, zoom}.
---   out: string — Required: the .png to write (relative to the level's folder).
---   path: string = current — Use this picture (and make it current) instead of the one ref.open registered.
---   crop: {x, y, w, h} = whole picture — Only this part of the picture: {x, y, w, h} source pixels.
---   zoom: number = fit about 1400 px — Enlargement (crisp pixels above 1); 2-8 on a crop reads corners to a pixel.
---   step: number = auto (lines >= 70 px apart) — Major grid step in source pixels; minor lines at a fifth of it.
---   overlay: table | "level" = none — Draw outlines with names and corner dots: a kit.trace table (its units; default m), a kit.trace result, a list of regions {name, poly} / ref.mask polygons / point lists, or "level" (the built floor, one outline per height band).
---   overlay_units: string = "px" — Units of overlay lists that are not a kit.trace table: "px" or "m" (world x, z through the calibration).
function ref:grid(args) end  --- returns table
--- Decodes a heights picture (render --mode heights, or a heights overlay) by its legend: {grid, flats, ramps, stairs, drops, regions (for kit.trace{units = "px"}), counts}; out writes a check overlay.
---   image: string = the current picture — The heights picture (render --mode heights, or a plan rendered with the heights overlay); it becomes the current picture.
---   legend: {min, max} — Required: the heights (m) at the colour bar's two ends (the legend's "walkable floor height, m (min to max)", or the render's --json heights.range).
---   ppm: number = the calibration's — Picture pixels per metre (1 / worldPerPixel of the render) when the picture is not calibrated.
---   crop: {x, y, w, h} = whole picture — Only decode this part of the picture.
---   ignore: {{x, y, w, h}, ...} = none — Rectangles to leave out (a legend panel, a caption); the legend's striped colour bar is skipped on its own.
---   cell: number = about 0.25 m — The height grid's cell in pixels.
---   min_area: number = 2 — The smallest flat region, ramp or flight of stairs reported (m²).
---   simplify: number = 0.1 — Outline tolerance in metres (neighbouring regions keep their shared seams within it).
---   out: string = none — Write a check overlay .png: the picture with every region outlined and named with its height (flat +y, ramp / stairs low→high) and the drop edges.
---   path: string = current — Same as image.
function ref:heights(args) end  --- returns table
--- The current picture, its size and calibration: {path, width, height, calibrated, calibration?, mask_pixels?}.
function ref:info() end  --- returns table
--- Traces the playable area by colour (samples or colors; mode rgb or hsv) or from polygons; reports each colour's share; returns polygons with holes (px, world), or zone parts. Becomes the compare mask.
---   samples: {{x, y}, ...} — Pixels whose colour (5x5 average) is playable; give several per floor colour. samples_report gives each one's colour and share of the picture.
---   mode: string = "rgb" — "rgb": RGB distance (tolerance). "hsv": hue within hue_tol, saturation within sat_tol, value within val_tol (separates saturated rock from yellow or grey tiles).
---   hue_tol: number = 15 — hsv: hue tolerance in degrees (ignored for grey references, saturation < 0.12).
---   sat_tol: number = 0.2 — hsv: saturation tolerance (0-1).
---   val_tol: number = 0.3 — hsv: value (brightness) tolerance (0-1).
---   ranges: {{hue = {min, max}, sat = {min, max}, val = {min, max}}, ...} — hsv: explicit boxes (hue degrees, wrapping when min > max; sat / val 0-1; any part optional).
---   polygons: table — Instead of colours: rasterise these polygons (a kit.trace table, regions, ref.mask polygons or point lists) into the mask.
---   units: string = "px" — Units of polygons and split polys: "px" or "m".
---   holes: string = "keep" — "keep": outlines carry holes (holes_px / holes). "cut": pieces without holes, cut along pixel rows through each hole (they share the cut edges), for kit.trace.
---   split: "colors" | {{name, rect | poly, ...}, ...} = none — Separate outlines per colour class, or per zone (rect = {x, y, w, h} or poly in px). Extra zone fields (floor, kind...) are copied into the result's ready-made regions.
---   colors: {{r, g, b}, ...} — Playable colours (0-255), with or instead of samples.
---   tolerance: number = 40 — RGB distance a pixel may be from a listed colour.
---   invert: boolean = false — Playable = pixels NOT matching (list rock / background colours instead).
---   connected: boolean = false — Keep only the regions that contain a sample.
---   crop: {x, y, w, h} = whole picture — Only segment this rectangle (e.g. the map panel).
---   close: number = 2 — Closing radius px: bridges grout lines and gaps up to about twice it.
---   open: number = 1 — Opening radius px: removes specks and threads.
---   min_area: number = 64 — Drop regions smaller than this many pixels.
---   fill_holes: number = 64 — Fill holes smaller than this many pixels.
---   simplify: number = 0.5 — Outline tolerance in metres (calibrated).
---   simplify_px: number = 2 — Outline tolerance in pixels when not calibrated.
---   max_polygons: number = 32 — Return at most this many outlines, largest first.
---   out: string = none — Write an overlay .png (mask green, outlines yellow, corners red).
---   mask_out: string = none — Write the mask itself as a black / white .png (for compare's mask).
---   path: string = current — Use this picture (and make it current) instead of the one ref.open registered.
function ref:mask(args) end  --- returns table
--- Edits a mask: fill and erase polygons (px or world) on the last ref.mask or a mask .png; the result becomes the compare mask. Returns {pixels, area_m2, filled, erased, path}.
---   fill: table — Polygons to add (a list of point lists, regions {name, poly}, ref.mask polygons or a kit.trace table).
---   erase: table — Polygons to remove (same forms as fill); applied after fill.
---   units: string = "px" — Units of fill / erase lists: "px" or "m" (a kit.trace table uses its own units).
---   mask: string = the last ref.mask — Start from this mask .png (white = playable, the picture's size).
---   out: string = none — Write the edited mask as a black / white .png.
---   overlay: string = none — Write an overlay .png (the picture, mask green, outlines).
---   path: string = current — Use this picture (and make it current) instead of the one ref.open registered.
function ref:mask_edit(args) end  --- returns table
--- Ranks library textures against a crop of a shot (a wall, floor, trim) by Lab colour and pattern; returns {matches = {{name, score}}, best, path} and writes a contact sheet.
---   shot: string = the current picture — The screenshot, render or mockup to read (does not change ref's current picture).
---   path: string = the current picture — Same as shot.
---   crop: {x, y, w, h} = the whole picture — The patch to match in source pixels: one surface (a wall panel, floor, ceiling, trim), as square-on as the shot allows.
---   polygon: {{x, y}, ...} = none — Instead of crop: only the pixels inside this outline (a floor seen in perspective).
---   n: number = 8 — How many textures to return (1-40).
---   stock: boolean = true — Only textures every player has (a retail packfile carries them); false ranks the whole mounted library.
---   category: string = all — Only textures in stock texture categories whose name contains this (the editor's texture browser categories).
---   filter: string = none — Only texture names containing this (e.g. "mtl", "rck", "flr").
---   out: string = none — Write the contact sheet .png: the crop beside the top n textures, ranked and scored.
function ref:match_textures(args) end  --- returns table
--- Registers a reference picture (png, jpg, bmp, tga, gif) as the current one and returns {path, width, height, calibrated}. A new picture clears the calibration and mask.
function ref:open(path) end  --- returns table
--- Renders the build from a pose (lit, textured, the shot's aspect) beside the shot with an edge score: {score, offset_px, render, path}; edges = true adds an edge-overlay panel.
---   shot: string — Required: the eye-level screenshot (png, jpg...), relative to the level's folder.
---   pose: table — Required: {x, y?, z, yaw, pitch?, fov?} (a shot_locate pose as is); y = eye height in world (default: floor below + eye); yaw 0 = +Z, 90 = +X.
---   out: string — Required: the side-by-side .png (shot | build render | edges?).
---   fov: number = pose.fov or 90 — Horizontal field of view override (degrees).
---   eye: number = 1.7 — Eye height over the floor when pose.y is not given.
---   panel: number = 360 — Panel height in pixels (width follows the shot's aspect).
---   edges: boolean = false — Add a third panel: shot edges red, build edges cyan, both yellow.
---   mode: string = "lit" — lit | textured (the game renderer; grey ray-cast shading where none) | shaded.
---   build: string = "none" — "sealed" runs ops.build() first.
function ref:shot_compare(args) end  --- returns table
--- Estimates a shot's lighting: light colours from its highlights, ambient from its darkest quarter, fog from its top; returns light_color, intensity, ambient_color, fog_color, summary.
---   shot: string = the current picture — The screenshot or render to read (does not change ref's current picture).
---   path: string = the current picture — Same as shot.
---   crop: {x, y, w, h} = the whole picture — Only this part of the shot (one area of a multi-area mockup).
---   polygon: {{x, y}, ...} = none — Instead of crop: only the pixels inside this outline.
function ref:shot_lighting(args) end  --- returns table
--- Finds the camera of an eye-level screenshot in the current build (edge match, coarse to fine): {best = {pose, score}, poses = {...}, seconds}; out writes shot | render | edges.
---   shot: string — Required: the eye-level screenshot (png, jpg...), relative to the level's folder.
---   fov: number = 90 — The shot's horizontal field of view in degrees (RF's default view is 90).
---   eye: number = 1.7 — Camera height above the floor it stands on (m).
---   search: {box?, step?, yaw?, floor?} = whole floor, 1.5 m — box = {x0, z0, x1, z1}; step = position spacing (m); yaw = {heading, +-range}; floor = only floors within 0.75 m of this y.
---   pitch: number = 10 — Largest |pitch| searched (degrees); 0 = level camera.
---   keep: number = 3 — How many distinct poses to return (1-6).
---   out: string = none — Write shot | build render | edge overlay (.png) for the best pose.
---   panel: number = 300 — Panel height in pixels for out.
---   edges: boolean = true — Add the edge-overlay panel to out.
---   mode: string = "lit" — Render panel: lit | textured (the game renderer) | shaded (ray-cast grey).
---   build: string = "none" — "sealed" runs ops.build() first.
function ref:shot_locate(args) end  --- returns table
--- From a pose and a shot pixel: the ray, what it hits in the build {hit, distance, height (above the camera's floor), surface}, and at_distance = the pixel's height at a given distance.
---   pose: table — Required: {x, y?, z, yaw, pitch?, fov?} (a shot_locate pose as is); y = eye height in world (default: floor below + eye); yaw 0 = +Z, 90 = +X.
---   px: {x, y} — Required: a pixel of the shot (its own pixel coordinates).
---   shot: string — The screenshot (for its size); or give size.
---   size: {w, h} — The screenshot's pixel size instead of shot.
---   distance: number = none — Also return at_distance: the pixel's height above the camera's floor at this horizontal distance (m).
---   fov: number = pose.fov or 90 — Horizontal field of view override (degrees).
---   eye: number = 1.7 — Eye height over the floor when pose.y is not given.
function ref:shot_measure(args) end  --- returns table
--- Writes the reference shot and a render of the build side by side at the same height, labelled, plus each one's lighting summary; returns {path, shot, render}.
---   shot: string — Required: the reference picture.
---   render: string — Required: a render of the build from the matching camera (glacier-cli render --camera= --look= --hfov).
---   out: string — Required: the .png to write.
---   height: number = 360 — The height both pictures are scaled to (64-1440 px).
function ref:side_by_side(args) end  --- returns table
--- The picture pixel {x, y} over world point (x, z).
function ref:to_px(x, z) end  --- returns number[]
--- The world point {x, z} under picture pixel (x, y) through the calibration (pixel centres are at +0.5).
function ref:to_world(x, y) end  --- returns number[]
--- Converts a list of picture pixels {{x, y}, ...} to world points {{x, z}, ...} (for kit.trace polygons in metres).
function ref:world_points(points) end  --- returns number[][]

--- The design kit: local kit = ged.import("kit") (a shipped module, not a global; listed here for completion). Each function takes one option table and rejects unknown keys.
kit = {}
--- One air box (the room's clear interior), floor at pos.y; returns {brush, min, max, center, floor_y}.
---   name: string = room<n> — Names the room (tags, and the default name of its lights and spawns); a default skips names already in the level.
---   pos: vec — Required: the floor point at the centre of the footprint (metres).
---   size: vec — Required: interior clear size {w, h, d} in metres, each > 0; leave h out ({w, nil, d}) for the kit.proportion height of the footprint.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   detail: boolean = false — Make the air brush detail.
function kit.room(t) end  --- returns room
--- Air box(es) joining two records across their gap, or along an axis-aligned floor path.
---   name: string = corridor<n> — Names the corridor (tags).
---   a: record — One end: a room, corridor or opening record (give a and b, or path).
---   b: record — The other end; separated from a along x or z by solid.
---   path: vec[] — Floor points (at least 2, one height, each step along x or z only) for a corridor with bends.
---   width: number = kit.defaults.corridor_width (4.5) — Clear width in metres (at least 1.8 unless allow_tight).
---   height: number = kit.defaults.corridor_height (4.5) — Clear height in metres (at least 2 unless allow_tight).
---   offset: number = 0 — Shift across the shared span, metres (a/b form only).
---   floor_y: number = the rooms' common floor — Floor height of the corridor (a/b form only).
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   allow_tight: boolean = false — Allow a width or height below the player's size.
---   detail: boolean = false — Make the air brushes detail.
function kit.corridor(t) end  --- returns corridor
--- A door-sized air opening through the wall between two records (abutting both); portal = true makes the opening an air portal brush (two portals, the doorway a small room between them).
---   name: string = doorway<n> — Names the opening (tags).
---   a: record — Required: the room (or corridor/opening record) on one side of the wall.
---   b: record — Required: the record on the other side.
---   width: number = kit.defaults.door_width (3) — Clear width in metres.
---   height: number = kit.defaults.door_height (4) — Clear height in metres.
---   offset: number = 0 — Shift across the shared span, metres.
---   floor_y: number = the rooms' common floor — Floor height of the opening.
---   portal: boolean = false — Make the opening an air portal brush that cuts the wall itself: the two walls it opens become portals and the doorway a small room between the two sides (RED's portal construct; splits the level for visibility — use it where a kit.door goes).
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   allow_tight: boolean = false — Allow a width or height below the player's size.
function kit.doorway(t) end  --- returns opening
--- Full-height solid steps from the floor point pos climbing toward dir, with an invisible collision ramp over them; carve = true first cuts an air envelope through solid. A flight must serve a route: before building, it is refused (with the reason) when its top climbs into a wall or solid, when an end that has floor lacks 1 m clear to step on or off, when the floors are already joined on foot nearby (a walk under 1.5x its length + 4 m), or when an end leads on to under 16 m2 of floor (purpose); an end whose floor is not built yet is noted, not judged. Every brush is tagged kit.role = "route"; the record carries purpose {ok, fault, detour, top_area, foot_area, foot_clear, top_clear}.
---   name: string = stairs<n> — Names the stairs (tags, group).
---   pos: vec — Required: the floor point at the foot of the stairs, centre of the width.
---   dir: string — Required: the climbing direction, "+x", "-x", "+z" or "-z".
---   rise: number — Required: total height climbed, a whole number of risers (metres).
---   width: number = kit.defaults.corridor_width (4.5) — Width in metres.
---   riser: number = kit.defaults.riser (0.25) — Step height in metres (at most 1).
---   tread: number = kit.defaults.tread (0.5) — Step depth in metres (riser/tread at most 1).
---   carve: boolean = false — First cut an air envelope (rise + headroom high) through solid, for stairs between floors.
---   headroom: number = kit.defaults.corridor_height (4.5) — Envelope clearance above the steps (carve only).
---   detail: boolean = false — Make the steps detail.
---   group: boolean = true — Group the brushes under the stairs' name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   allow_tight: boolean = false — Allow a width or headroom below the player's size.
---   collision_ramp: boolean = true — Add an invisible detail ramp over the step edges (kit.defaults.invisible_texture) so players walk up smoothly.
---   purpose: string = "require" (inside kit.space and kit.build_layout: "quiet") — "require" refuses a flight that serves no route or whose landing is blocked; "report" warns and builds; "quiet" or "off" builds without measuring (the grader's G30 reads it once built).
function kit.stairs(t) end  --- returns stairs
--- One solid wedge from the floor point pos rising toward dir (default slope kit.defaults.ramp_slope, max 45 degrees). Like kit.stairs, a ramp must serve a route (purpose): it is refused when its top climbs into a wall or solid, when an end lacks 1 m clear, when the floors are already joined on foot nearby, or when an end leads nowhere; an end whose floor is not built yet is noted, not judged.
---   name: string = ramp<n> — Names the ramp (tags, group).
---   pos: vec — Required: the floor point at the foot of the ramp, centre of the width.
---   dir: string — Required: the rising direction, "+x", "-x", "+z" or "-z".
---   rise: number — Required: height gained in metres.
---   run: number = rise / kit.defaults.ramp_slope — Horizontal length in metres.
---   width: number = kit.defaults.corridor_width (4.5) — Width in metres.
---   carve: boolean = false — First cut an air envelope through solid.
---   headroom: number = kit.defaults.corridor_height (4.5) — Envelope clearance above the ramp (carve only).
---   detail: boolean = false — Make the wedge detail.
---   group: boolean = true — Group the brushes (with carve) under the ramp's name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   allow_tight: boolean = false — Allow a width or headroom below the player's size.
---   allow_steep: boolean = false — Allow steeper than 45 degrees.
---   purpose: string = "require" (inside kit.space and kit.build_layout: "quiet") — As kit.stairs: "require" refuses a ramp that serves no route or whose landing is blocked; "report" warns and builds; "quiet" or "off" builds without measuring.
function kit.ramp(t) end  --- returns ramp
--- A sliding mover door filling a doorway/corridor opening, opened by use-key buttons or an automatic trigger.
---   name: string = door<n> — Names the door, its mover, keyframes and triggers.
---   opening: record — Required: a kit.doorway record or a single-segment kit.corridor record.
---   slide: string = "up" — Where the door goes when open: "up", "down", "left" or "right" (as seen walking from a to b).
---   thickness: number = kit.defaults.door_thickness (0.25) — Slab thickness in metres.
---   travel: number = kit.defaults.door_travel (1) — Seconds to open (and to close).
---   pause: number = kit.defaults.door_pause (3) — Seconds the door stays open.
---   trigger: string|boolean = "auto" — "auto" adds a walk-in trigger at the door; false adds none (ignored with buttons).
---   buttons: vec[] — Points of use-key triggers that open the door, instead of the automatic trigger.
---   button_class: string = none — A clutter class placed at each button point so players can see where to press Use.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.door(t) end  --- returns door
--- A raised walkable surface at pos.y + height: one solid block, or a slab of thickness (plus 4 legs with supports = true).
---   name: string = platform<n> — Names the platform (tags, group).
---   pos: vec — Required: the floor point under the centre of the platform.
---   size: {w, d} — Required: footprint in metres along x and z.
---   height: number — Required: height of the walkable top above pos.y, metres.
---   thickness: number = none (a solid block) — Make a slab this thick instead of a block reaching the floor.
---   supports: boolean = false — Add four kit.defaults.pillar_size legs under a slab (needs thickness).
---   detail: boolean = false — Make the brushes detail.
---   group: boolean = true — Group the brushes under the platform's name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.platform(t) end  --- returns platform
--- count solid pillars standing on the floor, evenly spaced from from to to (inclusive), box or cylinder.
---   name: string = pillar_row<n> — Names the row (tags, group).
---   from: vec — Required: the floor point of the first pillar.
---   to: vec = from — The floor point of the last pillar.
---   count: integer — Required: number of pillars (at least 1).
---   height: number — Required: pillar height in metres.
---   size: number = kit.defaults.pillar_size (1) — Side (box) or diameter (cylinder) in metres.
---   shape: string = "box" — "box" or "cylinder".
---   sides: integer = 12 — Cylinder segments (cylinder only).
---   detail: boolean = false — Make the pillars detail.
---   group: boolean = true — Group the pillars under the row's name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.pillar_row(t) end  --- returns pillars
--- An outdoor sky: a small sealed air box away from the play space holding a Sky Room effect (the view every show-sky face displays, built like the stock dm01/dm04 skies), plus show_sky on the ceilings of the open rooms.
---   name: string = sky_room<n> — Names the sky box (tags) and its effect (<name>_effect).
---   pos: vec = 64 m beyond the level's +X edge — The floor point of the sky box; keep it out of the play space.
---   size: vec = {6, 6, 6} — Interior size of the sky box in metres.
---   texture: string = kit.defaults.sky_texture — The sky texture on every face of the box.
---   open: record[] — Rooms or corridors whose ceilings show the sky: their up-facing air faces get show_sky and the sky texture (as kit.road, kit.canyon and kit.space do), so they read as sky in the editor too.
function kit.sky_room(t) end  --- returns sky
--- A rough cave along a floor path on any heading (geo.tube): structural air segments meeting on identical faces, a floor that is level or a smooth slope (at most 45°) on each leg, and a 1.8 m wide, 2 m high walkway along the path.
---   name: string = cave<n> — Names the cave (tags, group).
---   path: vec[] — Required: 2 to 256 floor points; legs at least 2 m apart, at most 90° of turn per point and 45° of climb per leg (a climbing leg that turns keeps a level landing at the turn). Start or end it at least 1 m inside a room it joins, at the room's floor height, with the room at least height × (1 + roughness) + 0.1 m high; end it on its start point for a closed loop.
---   radius: number = kit.defaults.cave_radius (3) — Half-width in metres.
---   radii: number[] = none — Half-width per path point, interpolated along the path.
---   height: number = kit.defaults.cave_height (4.5) — Clear height at the crown, metres.
---   heights: number[] = none — Clear height per path point.
---   roughness: number = kit.defaults.roughness (0.2) — Wall jitter as a fraction of the size (0.05 to 0.5).
---   floor_flatness: number = 0.6 — Floor half-width as a fraction of the radius (0.3 to 1).
---   segments: integer = from spacing and the climb — Segments per leg (give segments or spacing).
---   spacing: number = max(3, radius) — Target distance between stations, metres.
---   sides: integer = 8 — Points per ring (6 to 14).
---   seed: integer = 1 — The random stream; the same options and seed give the same cave.
---   allow_tight: boolean = false — Skip the walkway width and headroom guarantee.
---   group: boolean = true — Group every brush the call made under the cave's name.
---   texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---   tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.cave(t) end  --- returns cave
--- An open canyon along a floor path on any heading (geo.tube): structural air segments meeting on identical faces, a floor that is level or a smooth slope on each leg, leaning rough walls and one common rim height, open to the sky (show_sky tops plus a kit.sky_room when the level has none).
---   name: string = canyon<n> — Names the canyon (tags, group; its sky room is <name>_sky).
---   path: vec[] — Required: 2 to 256 floor points; legs at least 2 m apart, at most 90° of turn per point and 45° of climb per leg. A canyon that runs into a room needs the room's ceiling at least 0.1 m above the canyon's rim.
---   width: number = kit.defaults.canyon_width (12) — Floor width in metres.
---   widths: number[] = none — Floor width per path point (basins and narrows), interpolated along the path.
---   height: number = kit.defaults.canyon_height (16) — Rim height above the highest floor, metres.
---   roughness: number = kit.defaults.roughness (0.2) — Wall jitter as a fraction of the size (0.05 to 0.5).
---   lean: number = 0.15 — How far the walls lean out per metre of height (0 to 0.5).
---   sky: boolean = true — Show the sky through the canyon's top faces (adds kit.sky_room{name = <name>_sky} when the level has no sky room).
---   segments: integer = from spacing and the climb — Segments per leg (give segments or spacing).
---   spacing: number = width / 2 — Target distance between stations, metres.
---   seed: integer = 1 — The random stream; the same options and seed give the same canyon.
---   group: boolean = true — Group the canyon's segments under its name.
---   texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---   tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.canyon(t) end  --- returns canyon
--- A clean level corridor along a floor path on any heading (geo.passage): one exact air prism per leg, mitred so neighbours meet on identical faces.
---   name: string = passage<n> — Names the passage (tags, group).
---   path: vec[] — Required: 2 to 256 floor points at one height, turning at most 120° per point. An end may stop exactly on an axis-aligned room wall when its last leg is axis-aligned.
---   width: number = kit.defaults.corridor_width (4.5) — Clear width in metres (at least 1.8 unless allow_tight).
---   height: number = kit.defaults.corridor_height (4.5) — Clear height in metres (at least 2 unless allow_tight).
---   allow_tight: boolean = false — Allow a width or height below the player's size.
---   detail: boolean = false — Make the air brushes detail.
---   group: boolean = true — Group the prisms under the passage's name.
---   texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---   tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.passage(t) end  --- returns passage
--- A row of overlapping rough detail rocks along the floor line from → to (geo.rock_row), or with structural = true one rough structural ridge standing exactly on the floor. Build it after the air it stands in.
---   name: string = rock_wall<n> — Names the rocks (tags, group).
---   from: vec — Required: the floor point the wall starts at.
---   to: vec — Required: the floor point the wall ends at.
---   height: number — Required: visible height above the floor, metres.
---   depth: number = 2 — Thickness across the line, metres.
---   roughness: number = kit.defaults.roughness (0.2) — Jitter as a fraction of the rock size (0.05 to 0.5).
---   flat_top: boolean = false — Clip every rock at one height: a flat, walkable ledge.
---   structural: boolean = false — Build one structural ridge (seals and splits rooms) instead of detail rocks; keep it clear of walls it would graze.
---   spacing: number = 0.8 × depth — Distance between rock centres, metres (neighbours overlap).
---   seed: integer = 1 — The random stream.
---   group: boolean = true — Group the rocks under the wall's name.
---   texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---   tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.rock_wall(t) end  --- returns rocks
--- count detail boulders on a rectangle, a kit room's floor, or a cave/canyon/passage floor clear of its walkway (geo.scatter). Rocks belong in organic spaces and against walls and edges: on a rectangle or room the space must be open to the sky or rock-walled (a built interior or walled yard is refused), and the boulders are scattered only in the bands along its sides (a cave, canyon or passage scatter keeps its walkway clear), never over the open middle; the record's count says how many fit. Every boulder is tagged kit.role = "theme_detail" and kit.purpose. Build them after the air they stand in.
---   name: string = boulders<n> — Names the boulders (tags, group).
---   area: {min=, max=} — A floor rectangle (its floor is min.y); give one of area, room or along.
---   room: record — A kit room (or other min/max record): its floor rectangle.
---   along: record — A kit.cave, kit.canyon or kit.passage record: its floor, clear of the walkway.
---   count: integer — Required: how many boulders (1 to 500).
---   size: number|{min, max} = {0.6, 2} — Boulder diameter in metres, or the range it is drawn from.
---   margin: number = kit.defaults.spawn_margin (room), 0.5 (area), 0.25 (along) — Inset from the edges, metres; along a tube, how far a boulder's centre stays inside the floor's edge (boulders may lean into the wall foot).
---   seed: integer = 1 — The random stream.
---   group: boolean = true — Group the boulders under their name.
---   texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---   tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
---   purpose: string = "require" — Theme fit: "require" refuses a built rectangle or room (an interior or a walled yard: walls not natural rock) and scatters only in the bands along the rectangle's sides; "report" warns and scatters over the whole rectangle; "off" skips both.
function kit.boulders(t) end  --- returns rocks
--- A rough pit dug down from a point on a level floor (geo.pit): a stack of structural air segments meeting on identical faces, narrowing toward a flat floor depth metres down; its top ring stands a little above the floor, so the mouth opens through the floor at any height.
---   name: string = pit<n> — Names the pit (tags, group).
---   pos: vec — Required: the centre of the pit's mouth, on a level floor (a room, or a level stretch of cave or canyon).
---   radius: number — Required: radius of the mouth, metres.
---   depth: number — Required: depth of the flat floor below pos, metres (rounded to 1/16, at least 1).
---   roughness: number = kit.defaults.roughness (0.2) — Radial jitter as a fraction of the radius (0.05 to 0.5).
---   taper: number = 0.7 — Floor radius as a fraction of the mouth radius (0.2 to 2).
---   seed: integer = 1 — The random stream.
---   group: boolean = true — Group the segments under the pit's name.
---   texture: string = kit.defaults.rock_texture — One texture for every face (with textures: for the roles textures leaves out).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (with texture: these win for the roles they name).
---   tile: number = kit.defaults.rock_tile_metres (tile_metres) — Metres per texture repeat.
function kit.pit(t) end  --- returns pit
--- Light objects along a path record — the station line of a kit.cave, kit.canyon, kit.passage or kit.road, or the legs of a kit.corridor (straight or path) — every spacing metres, each with a visible fixture. Lights hang low enough to light the floor (60 % of the clear height, at most 4.5 m), alternating sides: in front of a lamp on the wall (rock or brush) at that height where one stands within half the path's width plus 4 m (a stock wall lamp when the game tables are loaded, else a small full-bright detail lamp), else under the head of a detail lamp post standing at the walkway's edge, never in its middle. The fixtures are in the returned list's fixtures field.
---   along: record — Required: a kit.cave, kit.canyon, kit.passage, kit.road or kit.corridor record.
---   spacing: number = kit.defaults.light_spacing (5) — Distance between lights along the path, metres.
---   color: {r, g, b} = kit.defaults.light_color — Light colour, 0-255 per channel.
---   range: number = reaches across the path and to the next light (at least kit.defaults.light_range_min) — Light range in metres.
---   height: number = 60 % of the clear height, at most 4.5 m — Height above the floor, metres.
---   name: string = the record's name — Prefix of the lights' names (<name>_light<i>) and their kit.name tag.
---   fixture: boolean = true — false: bare lights (on paths wider than 8 m still mounted 0.6 m off the walls, alternating sides).
function kit.light_path(t) end  --- returns object[]
--- The kit's reference as text: every function with its options (types, defaults, meanings); the same as ged.describe("kit").text.
function kit.help(t) end  --- returns string
--- Generative fill: dresses spaces by 8 m cells with the theme's detail props, cover, overhead beams, stock clutter and decals (5–10 elements per 100 m², every cell dressed), every element placed for a reason and tagged with it (kit.role = cover or theme_detail, kit.purpose, kit.theme, kit.space): cover only against measured threats (lanes of 14 m or more and the approaches to spawns and objectives, a tall breaker on lanes of 24 m or more, each cluster on the lane it breaks with its long side across it), rocks only in organic spaces (caves, canyons, rock-walled spaces, open ground under the sky — a built interior or walled yard gets the theme's built pieces instead) and only against walls or as cover, keeping doorways, stairs, spawns and items clear, never cutting a walk route, and changing the walkable floor by at most budget (default 1.5 %). No decal touches a compiled face with more than 19 vertex records (the game and the editor crash loading such a level): decal spots are checked on the stored build when it is current, else on a private sealed build, and unsafe ones are left out. Returns {name, theme, seed, brushes, objects, group, count (elements placed, a number), by_kind {props, cover, breakers, lanes (threats given cover), overhead, clutter, decals, decals_unsafe (spots left out), brushes}, decal_check (what the decals were measured on), area, existing, density (elements per 100 m², existing included), walk {before, after, change}, cells {total, dressed, undressed}, spaces[]}.
---   room: record|string — One space: a kit record (room, corridor, cave, canyon, passage), a kit.name, or a kit.build_layout zone's name (report.zones[i].name).
---   rooms: (record|string)[] — Several spaces.
---   area: {min=, max=} — A box: every floor inside it (give one of room, rooms, area; none = every air space of the level).
---   theme: string = "industrial" — industrial (yard), construction (site), storage, control (control room), mine or canyon (outdoor); aliases yard, site, warehouse, tech, mars_base, cave, rock, outdoor.
---   density: number = 1 — Scales the element target (6 per 100 m² at 1, on top of light fixtures; 0.25 to 2).
---   seed: integer = 1 — The random stream: the same options, level and seed give the same dressing.
---   name: string = dress<n> — Names the dressing (tags, group, object names).
---   cover: boolean = true — Free-standing cover against the space's measured threats: one cluster on every open lane of 14 m or more not already broken (a tall sightline breaker at the middle of lanes of 24 m or more) and on the long approach into each spawn, flag or hill, at least 5 m apart and at most one per 45 m²; a space with no such lane (a small room) gets low cover against the wall beside its doorway-to-doorway route instead (one per 120 m², from 60 m²).
---   overhead: boolean = true — Beams, pipes or cable trays under ceilings at least 4.5 m high.
---   decals: boolean = true — Wall signs, streaks and floor grime (stock decal textures, about 0.3 per 100 m²).
---   clutter: boolean = true — Stock clutter props from the theme when the game tables are loaded (catalog.available).
---   keep_clear: table[] = none — Extra places to keep clear: points {x, y, z, r = metres} or boxes {min=, max=}.
---   budget: number = 0.015 — Largest share of a space's walkable floor that tall props may take (at most 0.02).
---   group: boolean = true — Group the dressing brushes under the dressing's name.
function kit.dress(t) end  --- returns dressing
--- Lights over a room or corridor record, each with a visible fixture. Indoors a room gets a grid sized from its floor area and headroom (about 2.2 lights per 100 m² up to 5 m of headroom, easing to 1.8 in tall halls; never under 1.7 — DESIGN-QUALITY-SPEC §a.3, 1.7–4.5) and a corridor one row, a light every 4 m, along every leg of a kit.corridor{path = …}; each light hangs just under a ceiling fixture (a stock clutter light when the game tables are loaded, else a small full-bright detail lamp) attached to the ceiling measured over its spot. A room open to the sky is lit as the stock outdoor maps are: fewer, stronger lights (about 0.5 per 100 m², range and intensity raised so the floor gets at least 90 % of the light a ceiling grid would give it) on its edges — a lamp on the wall where a wall stands at lamp height, a lamp post beside each doorway and along open edges (a stock post when the tables are loaded and the headroom allows, else a detail post with a full-bright head) — and a post inside the room only where floor would be more than 18 m from every light, never on the straight way between two doorways (a record carrying its plan outline as points, as kit.trace's regions do, is lit as one space along that outline); each fixture also gets a short 5 m glow light (on the wall under a wall lamp, at a post's foot), as the stock maps light their outdoor fixtures. Raised floors, ramps and lowered ceilings move every fixture with them; a light never goes inside a solid (it moves to a free spot, or is skipped with a warning). The fixtures are in the returned list's fixtures field (outdoors the glow lights in its glows field).
---   room: record — Required: the room or corridor record to light (min/max box; its air brush decides which solids stand in it).
---   spacing: number = from the floor area and headroom; 4 along a corridor — Grid cell size in metres (one light per cell); giving it overrides the area rule (open to the sky: the distance between fixtures along the edges).
---   color: {r, g, b} = the theme's colour, else kit.defaults.light_color — Light colour, 0-255 per channel.
---   range: number = reaches the cell's floor corners (at least kit.defaults.light_range_min); open to the sky: 1.25-1.8 × the farthest floor from its nearest light — Light range in metres.
---   height: number = just under the fixture (fixture = false: 3/4 of the headroom, at most 0.5 m below the ceiling) — Height above the floor under each light, metres (outdoors it sets the wall lamps' height and sizes detail lamp posts instead of the stock one).
---   name: string = the room's name — Prefix of the lights' names (<name>_light<i>) and their kit.name tag.
---   fixture: boolean|string = true — true: a fixture per light; "brush": always the detail lamp (outdoors: detail lamps and posts); a clutter class name: that class (indoors); false: bare lights.
---   theme: string = none — A kit.dress theme: its fixture classes and light colour.
function kit.light_room(t) end  --- returns object[]
--- Brush or terrain ground for an open area (ALPINE-COMPAT-SPEC §4.4): "auto" builds an Alpine terrain only when the area is large (kit.defaults.terrain_min_area / terrain_min_side), open to the sky, needs no overhang, is natural ground (caves, canyons and built decks stay brushes) and the level targets Direct3D 11 (require_d3d11_setting, d3d11_only_lightmaps_setting, a terrain already in the level, or d3d11 = true); "terrain" builds one unless the area cannot take it (an error); "brush" keeps the brush floor. A terrain gets heights from the kit's ground shape (seeded mounds, level at the edges, the flat places and the openings), holes outside the outline and at the openings, layers painted from the material intent (ground, worn paths, rock on the crests), rests 1/16 m above the brush floor and is enclosed in air (a sky volume when the area is not already in air). Returns {kind = "ground", name, mode, choice ("brush" or "terrain"), reason, inputs = {area, min_side, kind, large, open_topped, open_share, overhang, natural, d3d11, d3d11_signals, possible}, built, terrain, uid, min, max, y, top, cell, cells = {x, z}, holes, layers, sky, warnings}.
---   area: record = none — The area: a kit room, a kit.build_layout zone report, a kit.sky_room or any record with min / max (its outline, floor_y, sky, kind or archetype are read when present). Give area, or min and max.
---   min: vec — One corner of the ground rectangle (its y is the floor height unless y is given).
---   max: vec — The opposite corner.
---   outline: {x, z}[] = the area's outline, else the rectangle — The ground's plan in world metres; terrain cells not wholly inside it are holes.
---   y: number = the area's floor — The brush floor height the ground rests on (the terrain sits terrain_lift above it).
---   mode: string = "auto" — "auto", "terrain" or "brush".
---   explain: boolean = false — Only decide: return the choice, reason and inputs (and the grid a terrain would get) without changing the level.
---   name: string = ground<n> — The terrain's script name and the tag of the sky volume.
---   relief: number = kit.defaults.terrain_relief (1.5) — Mound height in metres (0 = level ground).
---   seed: integer = 1 — The mounds' stream; the same options and seed give the same ground.
---   cell: number = kit.defaults.terrain_cell (0.5) — Grid spacing in metres (doubled until the footprint fits 257 vertices a side).
---   holes: table[] = none — Openings cut through the ground: records, {min, max} boxes, polygons or {x, z, r} circles.
---   flat: table[] = none — Places kept level at the base height (doorways, landings, what stands on the ground): records, boxes, polygons or {x, z, r}.
---   paths: {x, z}[][] = none — Walkways painted with the path layer (polylines in world metres).
---   layers: {ground=, path=, rock=} = kit.defaults.terrain_textures — The three layer textures by material intent.
---   d3d11: boolean = false — Declare that the level targets Direct3D 11 (an "auto" input).
---   sky: boolean = true — Build a sky volume around the terrain when the area is not already in air (false: refuse instead).
function kit.ground(t) end  --- returns ground
--- count multiplayer respawn points spread over the spaces (by floor area, at least one per space while they last), each beside cover or in an alcove, clear of doorways, facing the nearest way out (the doorway when in sight, else the longest open heading), nearest neighbour 8–15 m where the spaces allow, chosen for low exposure and few spawns in sight within 30 m — measured exactly as the design grader measures them (spatial.spawn_report, G19: exposure = share of the playable floor within 40 m the eye 1.6 m above the spawn sees), on the compiled level (a private sealed build when the brushes changed since the last build), on the grader's own floor set and samples, so a populated level meets G19 by construction where its spaces allow. 1 m above the floor. The list's report field: nn_min, nn_median, los_pairs_within30, exposure_median, exposure_max and measure (what was measured).
---   room: record|string — One space: a kit record, a kit.name or a kit.build_layout zone's name (give one of room, rooms, area; none = every air space).
---   rooms: (record|string)[] — Several spaces: the spawns are spread over them (at least 60 % of them get one when count allows).
---   area: {min=, max=} — A box: every floor inside it.
---   count: integer — Required: number of respawn points (at least 1).
---   team: string = both teams — "red" or "blue" for a team-only spawn group.
---   margin: number = kit.defaults.spawn_margin (1.5) — Clear floor around each spawn (from walls and solids), metres.
---   name: string = the space's name — Prefix of the spawns' names (<name>_spawn<i>) and their kit.name tag.
---   seed: integer = 1 — The random stream for ties (and for the exposure samples when the level cannot be built).
---   checkpoint: object|number = none — A Payload_Checkpoint event (or its UID) the group serves: the spawns are linked to it, so they are live while that checkpoint is the cart's target (Alpine also keeps the last checkpoint's group live once the cart passes it) and groups advance with the cart.
---   spacing: number = 8 — Smallest distance between two spawns of the group (and to existing spawns), metres; a spawn hidden from the others may come a fifth closer, and the spacing is relaxed (1 m at a time) only when the spaces are too small.
function kit.spawn_points(t) end  --- returns object[]
--- The item economy, level-aware: the target is 1.6 items per 100 m² (× density) of the WHOLE level's walkable floor up to 4,500 m², easing on bigger levels the way the exemplars' item counts grow with their floor (about 1.2 at 10,000 m², never under 1.0), less every item already in the level, and a call places at most its spaces' share of it (their floor at that density, less the items already in them) — so per-zone calls add up to one level-wide economy, and calling again adds nothing once the level is stocked. Items never bunch: none goes where 6 already lie within 10 m, and each prefers the emptier neighbourhoods. Shares are the exemplar medians (weapons 31 %, ammo 34 %, health 17 %, armour 14 %, powerups 3 %); weapons 7–10 m apart and spread over the zones; powerups in the most contested cells, never in pockets; an item 3.5–5.5 m in front of every spawn without one within 6 m (counted in the target); health and armour on the flanks; ammo beside the weapons; CTF halves mirrored item for item; payload health and ammo on the cart route's flanks and at the defenders' holds. Place spawns first. Returns {items, count (number placed), by_category {weapon, ammo, health, armor, powerup}, target, target_per100 (the level density aimed at), local_max (most other items within 10 m of one item), area, per100 (these spaces, existing items included), level_area, level_per100, weapon_nn {min, median, max}, spawn_to_item {median, max}}.
---   gametype: string = "dm" — "dm", "tdm", "ctf", "koth", "cp" or "payload".
---   room: record|string — One space: a kit record, a kit.name or a kit.build_layout zone's name (give one of room, rooms, area; none = every air space).
---   rooms: (record|string)[] — Several spaces.
---   area: {min=, max=} — A box: every floor inside it.
---   density: number = 1 — Scales the level's item density (1.6 per 100 m² on DM-sized levels; 0.75 to 1.25 keeps it in the exemplar range).
---   seed: integer = 1 — The random stream.
---   name: string = items<n> — Prefix of the items' names (<name>_<n>) and their kit.name tag.
---   weapons: string[] = Shotgun, Assault Rifle, Machine Pistol, Sniper Rifle, rocket launcher, heavy machine gun, … — Weapon classes to use, in order (stock items.tbl names); their ammo comes with them.
---   powerups: boolean = true — Place powerups (Multi Damage Amplifier, Multi Super Armor, …) in contested spaces (from 900 m² of floor).
---   path: vec[] = none — Payload: the cart route's floor points (health and ammo go 6–15 m to its sides; weapons along it).
---   hill: vec = none — KOTH: the hill's centre (armour and health on its approaches, 8–16 m out).
function kit.items(t) end  --- returns items
--- Checks a layout graph against the c.1-1 rules before anything is built: schema and geometry (zones apart, every link fits its walls, levels and gaps), degree >= 2 except reward nooks, no articulation zone with >= 10% of the area behind it, cycle rank per 1,000 m2, 2 node-disjoint routes between hubs and objectives, 3+ floor levels 4.5-10 m apart joined by 2+ kinds of vertical link, archetype variety, mirror symmetry only for team modes, the CTF / KOTH / payload / open-air rules, the size budget, and each zone's ground request (zone.ground "brush", "terrain" or "auto"; terrain only under open sky). A link without a_y / b_y meets each zone at its floor (a graded road's top floor at its exit end). Payload tracks run on any heading: a road zone's params.heading is the way the cart travels through it (entered on the opposite end), the cart turns inside the zones between roads, consecutive cart zones need a road, bridge, doorway or corridor between them, and a bridge outside open-air layouts spans a chasm between two floors at one level. Free-form plans: zone.rot turns a zone's plan (its sides stay its own), a poly zone's outline is its world plan, a link that is not straight between facing walls of two rectangle zones of one rot is free (path waypoints, a_side / b_side, a_at / b_at, or routed clear of the zones and links), and a join opens two touching zones into one space; the checker reports overlaps and clearances in world coordinates, free links that bend more than 90 degrees, run under 1 m a segment, enter or pass within 1 m of a zone or cross another link on its storey, or lack the straight a stair or ramp needs, and joins that do not touch. A hand-made zone without levels, height or chamfer gets its archetype's. Each violation is {severity ("error": cannot build; "rule": a design rule), rule, message, zone?, link?}.
---   graph: table — Required: a kit.layout graph (possibly edited).
function kit.check_layout(t) end  --- returns violation[]
--- Generates a layout graph before any brush: zones (archetype, plan, floor level, palette, role, ground), typed links, objectives (spawn groups, flags, the hill, the payload cart and checkpoints with the spawn hand-off) and the size budget from the player class (DESIGN-QUALITY-SPEC §a.1, §c.1-1, §h). A plain table: edit it, then kit.check_layout, kit.layout_preview and kit.build_layout. The same seed gives the same graph.
---   gametype: string = "dm" — "dm", "ctf", "koth" or "payload".
---   players: integer = 8 — Intended players: 2-4 an arena (XS), up to 8 S, up to 16 M, more L; payload 12+ adds segments.
---   class: string = "standard" — "standard" (indoor, with courtyards) or "open_air" (floating platforms, sky bridges and decks); payload is its own class.
---   brief: string = none — A one-line brief; theme words (industrial, bunker, concrete, admin, tech, cave, canyon, mine, mars) set the zone palettes.
---   seed: integer = 1 — The layout's seed.
---   area: number = the class budget's middle — Target layout floor area in m2 (zones plus links).
---   style: table = none — {theme = "bunker"} or {themes = {...}}: palette themes in order of use.
---   step: number = 5 (payload 4.5) — Metres between floor levels (4.5-10).
---   segments: integer = by players (3-5) — Payload segments (checkpoints).
---   ground: string = "brush" — The ground of the open-air and sky zones (zone.ground): "brush" (brush slabs and floors), "terrain" (an Alpine terrain over the natural-ground zones: isles and gardens; built decks stay brushes) or "auto" (kit.ground decides per zone when the layout is built). The graph is the same either way; only zone.ground changes.
function kit.layout(t) end  --- returns graph
--- The zone archetypes kit.layout and kit.build_layout use: {name, class ("indoor", "open_air", "payload" or "traced" for the poly zone), min = {w, d} (kit.check_layout refuses smaller zones; a road's {w, d} turns with its heading), max = {w, d} (the sizes kit.layout generates; larger hand-made zones are allowed), levels (port levels; "step" = one level up), loops (inside the zone), summary, natural_ground (true: its ground may be an Alpine terrain, zone.ground)}.
function kit.layout_archetypes(t) end  --- returns archetype[]
--- A plan of a layout graph as text (north = +z up, one character per scale metres): zones filled with their level digit and labelled with their letter (turned zones and traced poly zones drawn as their world outlines), links drawn along their routes with their type's character (= corridor/doorway, s stair, r ramp, v drop, b bridge, ~ window, l lift, # cart road, + join), objectives marked (R/B flags, H hill, C checkpoints, S spawns). Returns {text, svg, legend[], scale, bounds}.
---   graph: table — Required: a kit.layout graph.
---   scale: number = fits about 100 columns — Metres per character.
function kit.layout_preview(t) end  --- returns preview
--- Builds a layout graph: kit.check_layout first (errors refuse; design-rule violations warn, or refuse with strict = true), then in CSG time order the zone shells (and the open-air void), every link's air, the zones' interiors (stairs, galleries, courts, cover, ...), the links' steps, ramps, landings, lifts and bridges (outside open-air layouts a bridge's deck spans a chasm cut to the sky, link.depth metres deep, default 8), details, the ground (a zone whose ground is "terrain" or "auto", kit.layout{ground}, gets kit.ground's choice: an Alpine terrain over natural ground such as isles and gardens, its spots lifted onto it), the room detail pass (detail: wall trims and pilasters, door frames, ceiling beams, railings, stair finishes, rock formations), lights, and the objectives: spawn groups, CTF flags, the KOTH hill (a Capture_Point_Handler with its trigger) and the payload cart (One Way mover on the track on any heading, Payload_Handler, checkpoints on its keyframes, each linking the attacker and defender spawns that are live while it is the target). A turned zone (rot) is built in its own frame and turned into place about the world origin; free links are built along their routes (one air prism per segment on shared mitre points, half a metre into each zone, the stair or ramp on the longest straight, door jambs set 1/16 m behind the wall planes); joins open the shared boundary. Every brush of a zone is tagged kit.zone = the zone's name (<name>_<zone id>), and that name works as a room name in kit.dress, kit.items and kit.spawn_points. Returns {name, zones[] (id, archetype, name, min, max (world bounds), outline (world plan), rot, floor_y, spots (world), brushes, room, detail), links[] (id, type, a, b, mode (classic, free or join), width, offset, a_side, b_side, path (a free link's route in world metres), length), spawns, objectives, warnings[], violations[], area, detail ({zones, parts, by_part, roles, fallback, notes}: what the detail pass placed), ground ({terrain, brush}: how many zones that asked for terrain or auto got each; such a zone's report carries ground = {mode, choice, reason, inputs, uid, cells, holes})}.
---   graph: table — Required: a kit.layout graph (possibly edited).
---   name: string = "L" — Prefix of every part's name (and its kit.name tags).
---   lights: boolean = true — Place lights in every zone and link.
---   strict: boolean = false — Refuse to build when any design rule is violated (errors always refuse).
---   force: boolean = false — Build even with check errors that do not stop the geometry (use with care).
---   populate: function = none — Called as populate(zone_report, zone, graph) after each zone is built: the hook for a population pass (dressing, items, spawns) per zone (theme and density in zone.dress).
---   detail: boolean|table = true — The room detail pass, run on every zone once the geometry stands (before lights, spawns and populate): pilasters, base and top trims on the long walls, frames on the doorways and windows, beams or trusses under roofed ceilings (never under open sky), railings on gallery, raised-floor, pit and bridge edges that drop away, stringers and handrails on the zones' stairs, rock formations in rock spaces — chosen by the zone's archetype and palette theme (industrial, bunker, admin, mine timber in cave palettes, mars base), from the module library when it is present (one donor set per level and style) else hand-authored parts, kept off doorways, routes, stairs, spawn spots, the cart lane and bridge decks and inside the walk budget. false skips it; a table turns parts off or on: {walls, frames, ceilings, railings, stairs, rocks} (booleans, default true), style (one detail style for every zone) and seed (default the graph's seed). The report's detail field counts what was placed.
function kit.build_layout(t) end  --- returns layout_report
--- Layers a room's walls the way RF mappers do: pilasters at a regular pitch (one each side of every doorway, 0.4 m off the jamb, corners kept clear) stretched floor to ceiling, base and top trims running between them, a band on tall walls and a panel centred in every bay of 2.5 m or more — one coherent set of library modules from one donor level, sitting exactly on the wall plane; hand-authored primitives without a library. Returns {name, placed, counts, roles, skipped, fallback, note, brushes, pitch, pilaster_positions, donor}.
---   room: record|string — The room: a kit record (room, corridor, shaped room) or a kit.name (give room or space).
---   space: {min=, max=} — A box whose walls are detailed (instead of room).
---   style: string = "industrial" — Style family: industrial, bunker, admin, mine, cave, canyon, mars_base, lab, tech, generic (or a kit.dress theme name).
---   seed: integer = 1 — Picks among the best-scoring coherent sets; the same options, level and seed give the same result.
---   walls: string[] = all four — The sides to detail: "+x" (the wall at max x), "-x", "+z", "-z".
---   pitch: number = the pilaster's suggested pitch (3 to 8 m), else 4 — Pilaster spacing in metres.
---   parts: {pilasters=, base=, top=, band=, panels=, recesses=} = pilasters, base, top, panels on; band on walls 6 m or taller — Which layers to build.
---   keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---   custom: boolean = false — Allow modules that need custom (non-stock) textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the room's own wall texture.
---   name: string = wall_detail<n> — Names the detail (tags).
function kit.wall_detail(t) end  --- returns detail
--- Frames a door or window opening: a library door/window frame stretched (X and Y slices) to the opening's exact clear size (never narrower), centred in the wall or one on each face when the frame is thinner than the wall; else a primitive frame of jambs and lintel on both faces. Frames each opening once (kit.opening tag). Returns the detail record.
---   doorway: record — A kit.doorway / kit.opening record (give doorway, or pos + dir + width + height).
---   pos: vec — The floor point at the middle of the opening on the wall face it is framed from.
---   dir: string — Through the wall, away from that face: "+x", "-x", "+z" or "-z".
---   width: number — The clear width in metres.
---   height: number — The clear height in metres (from pos.y).
---   thickness: number = 1 — The wall thickness in metres.
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Picks among the best-scoring frames.
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on the placed module.
---   retexture: boolean = false — Map every module texture onto the wall's texture.
---   name: string = frame_opening<n> — Names the frame (tags).
function kit.frame_opening(t) end  --- returns detail
--- Real ceiling structure: library beams or trusses stretched wall to wall across the room's shorter axis at a regular pitch, aligned to the pilasters a kit.wall_detail laid (read back from their tags), never lower than 2.2 m above the floor; coffers add a lighter cross grid; pipes = true adds a pipe run along the long axis. I-beams of three boxes without a library. Returns the detail record plus axis, kind, positions.
---   room: record|string — The room (a kit record or kit.name; give room or space).
---   space: {min=, max=} — A box instead of a room.
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Picks among the best-scoring beams.
---   kind: string = "auto" — "beams", "trusses" (rooms 8 m or taller), "coffers" (a beam grid) or "auto".
---   pitch: number = the wall detail's pilaster positions, else 4 — Beam spacing in metres.
---   axis: string = the shorter side — The axis the beams span: "x" or "z".
---   pipes: boolean = false — Also run a pair of pipes under the ceiling along the long axis.
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the room's wall texture.
---   name: string = ceiling_detail<n> — Names the detail (tags).
function kit.ceiling_detail(t) end  --- returns detail
--- Floor-standing machinery, consoles and crate stacks placed with purpose: backs against the walls facing into the room, kept off doorways, spawns, items and the route between doorways, and within the walk budget (the generative fill's engine space); a mix of the best-scoring modules of the style. Primitive machines without a library. Roles: cover for chunky pieces, fixture for consoles (or role = …).
---   room: record|string — The room (a kit record or kit.name; give room or space).
---   space: {min=, max=} — A box instead of a room.
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Picks the spots and the mix.
---   count: integer = 1 per 60 m² (2 to 10) — How many pieces.
---   kinds: string[] = {"machinery", "console", "crate_stack"} ({"console", "machinery"} for admin, tech and lab) — Module categories to draw from.
---   role: string = from the module — Record every piece as this role (cover, route, landmark, theme_detail, fixture).
---   keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---   budget: number = 0.02 — Largest share of the walkable floor the pieces may take (at most 0.02).
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the room's wall texture.
---   name: string = machinery<n> — Names the pieces (tags).
function kit.machinery(t) end  --- returns detail
--- A railing along a floor edge: a library edge railing stretched to each leg (its posts re-spaced by the module's repeat), corner posts at the joints of a path; post-and-rail primitives without a library. Role route.
---   from: vec — The edge's first floor point (give from + to, or path).
---   to: vec — The edge's last floor point.
---   path: vec[] — Floor points along the edge (at least 2).
---   side: string = the side with floor — Where the walkable floor is: "left" / "right" of the direction of travel, or "+x", "-x", "+z", "-z".
---   height: number = the module's (about 1.05 m) — Railing height in metres.
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Picks among the best-scoring railings.
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the wall texture (no effect without a room).
---   name: string = railing<n> — Names the railing (tags).
function kit.railing(t) end  --- returns detail
--- A raised walkway: a deck whose top runs from → to (a stretchable library catwalk or grate when one fits, else a primitive deck with a grating top and edge beams), railings on its sides (inside the deck), and supports to the floor below every pitch metres (columns), or wall brackets where no floor is in reach. The clear width between the rails stays at least the player width. Role route.
---   from: vec — Required: the deck top's start point (centre line).
---   to: vec — Required: the deck top's end point (level with from; along x or z).
---   width: number = 2.5 — Deck width in metres (rails inside it; at least kit.limits.min_clear_width + 0.25).
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Picks among the best-scoring parts.
---   rails: string = "both" — "both", "left", "right" (of the direction from → to) or "none".
---   supports: boolean = true — Columns down to the floor (or brackets to a wall) under the deck.
---   pitch: number = 4 — Support spacing in metres.
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the wall texture (no effect without a room).
---   name: string = catwalk<n> — Names the catwalk (tags).
function kit.catwalk(t) end  --- returns detail
--- Axis-aligned pipe legs through the points (or routed from → to along a wall, ceiling or floor as an L), up to count parallel pipes, flanges at the ends and every 4 m, elbow collars at the turns, and brackets or hangers to the nearest wall or ceiling at a pitch. Library pipe modules stretched along a leg when one of the diameter exists; primitive cylinders otherwise. Role theme_detail.
---   points: vec[] — The pipe's centre line (at least 2 points; each leg along x, y or z). Give points, or from + to.
---   from: vec — Start of a routed run.
---   to: vec — End of a routed run (an L: first along x, then z, then y).
---   along: string = "ceiling" — With from/to: "ceiling", "wall" or "floor" (only labels the brackets' side; the route is the same L).
---   diameter: number = 0.3 — Pipe diameter in metres.
---   count: integer = 1 — Parallel pipes (1 to 4), spaced 1.6 diameters apart.
---   brackets: number = 2.5 — Bracket / hanger spacing in metres (0 for none).
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Picks among the best-scoring pipe modules.
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the wall texture (no effect without a room).
---   name: string = pipe_run<n> — Names the pipes (tags).
function kit.pipe_run(t) end  --- returns detail
--- Finishes a kit.stairs flight: sloped stringers along both sides and handrails (a post every few treads and a sloped rail through their tops), hand-authored in the style's stock textures; set inside the stair width only while the clear width stays at least the player width, else just outside it where there is open air. Role route.
---   stairs: record — Required: a kit.stairs record.
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — Kept for the kit's rules (the result does not depend on it).
---   stringers: boolean = true — Build the two side stringers.
---   rails: string = "both" — Handrails: "both", "left", "right" (looking up the stairs) or "none".
---   posts_every: integer = 3 — A rail post every this many treads.
---   name: string = stair_detail<n> — Names the detail (tags).
function kit.stair_detail(t) end  --- returns detail
--- Rock formations at the foot of cliff, canyon and cave walls: a library rock (or a large roughened boulder) with smaller boulders clustered beside it, partly sunk into the floor so nothing floats, plus a few free-standing clusters as cover where the floor is wide — through the generative fill's engine space so routes, doorways and spawns stay clear and the walk budget holds. Rocks take the space's own wall texture (retexture, default on) so they match the cliff. Roles: cover for clusters 1-2.6 m tall, landmark for big formations, theme_detail otherwise (or role = …).
---   space: record|string|{min=, max=} — The space: a kit record (canyon, cave, room …), a kit.name, or a box (give space or path).
---   path: record — A kit.canyon / kit.cave / kit.passage record whose floor the rocks line.
---   style: string = "canyon" — "canyon", "cave" or "mine" (or any style family).
---   seed: integer = 1 — Picks the spots and the formations.
---   count: integer = 1 per 40 m² (2 to 16) — How many wall clusters.
---   role: string = by size — Record every formation as this role.
---   keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---   budget: number = 0.02 — Largest share of the walkable floor the formations may take (at most 0.02).
---   custom: boolean = false — Allow modules that need custom textures.
---   retexture: boolean = true — Give library rocks the space's own wall texture (false keeps theirs).
---   name: string = rock_detail<n> — Names the formations (tags).
function kit.rock_detail(t) end  --- returns detail
--- The detailing pass an agent reaches for once a room's shell is built: kit.wall_detail (pilasters, trims, band, panels: theme_detail), kit.frame_opening on every door and window opening of the room first (found by probing its walls, plus the doorways given; the pilasters then keep clear of the frames), and with level = "full" also kit.ceiling_detail (beams on the pilaster pitch: theme_detail) and kit.machinery (cover / fixture), kit.stair_detail for the stairs given and kit.railing along the edges given (route) — one style, one seed, one donor set. Use "light" for corridors and small rooms, "full" for halls and set-piece rooms. Returns the merged record plus parts = {wall, frames, ceiling, machinery, stairs, railings}.
---   room: record|string — The room (a kit record or kit.name; give room or space).
---   space: {min=, max=} — A box instead of a room.
---   style: string = "industrial" — Style family (see kit.wall_detail).
---   seed: integer = 1 — One seed for every part.
---   level: string = "full" — "light" (walls and frames) or "full" (plus ceiling, machinery, stairs and railings).
---   doorways: record[] = none — Extra kit.doorway / kit.opening records to frame (openings in the room's walls are found anyway).
---   stairs: record[] = none — kit.stairs records in the room to finish (full only).
---   edges: vec[][] = none — Floor-edge paths that get railings (full only).
---   keep: table[] = none — Extra places to keep clear: points {x, y, z, r =} or boxes {min =, max =}.
---   custom: boolean = false — Allow modules that need custom textures.
---   textures: {old = new} = none — Texture replacements on every placed module.
---   retexture: boolean = false — Map every module texture onto the room's wall texture.
---   name: string = detail<n> — Names the detail; the parts are named <name>_walls, <name>_frame<i>, <name>_ceiling, <name>_machinery.
function kit.detail(t) end  --- returns detail
--- A rock mass that follows a plan outline (mesa, outcrop, cliff, buttress) as eroded rock: a talus apron, rounded strata stepping in with ledges, a wandering rim; the foot keeps the outline. Build it after the air it stands in.
---   name: string = rock_mass<n> — Names the mass (tags, group).
---   poly: {x, z}[] — Required: the plan outline (3 to 256 points, world metres or with units = "px" picture pixels); concave allowed.
---   y0: number — Required: the foot height (the mass sinks 0.25 m below it so it never floats).
---   y1: number — Required: the top height (the highest stratum reaches it; others stop lower).
---   units: string = "m" — "m" world metres, "px" picture pixels through ref.calibrate.
---   band: number = (y1 - y0) / 4 (1.2 to 4) — Stratum height in metres.
---   step: number = 0.5 — How far each stratum steps in from the one below, metres on average (0 = sheer sides).
---   roughness: number = 0.25 + 0.06 × height (at most 1.2) — How far outline corners push out at random, metres.
---   talus: number = 0.18 × height (0.5 to 3; none when step is 0) — Height of the talus apron at the foot, metres (0 = none); it slopes up about 34° from the outline.
---   top_vary: number = 0.35 — The share of the top stratum's height the rim may lose around the outline (a wandering skyline).
---   seed: integer = 1 — The random stream.
---   group: boolean = true — Group the mass's brushes under its name.
---   texture: string = kit.defaults.rock_texture — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (tops are floor); without it the default rock is one texture on every face.
function kit.rock_mass(t) end  --- returns rock_mass
--- The exemplar proportion rule for a space: {kind, height, min, max, area}, and with height given also ok and message. Kinds: corridor 4-5 m, room (< 100 m²) 3.5-6 m, hall (100-400 m²) 6-9 m, atrium/set piece (> 400 m²) at least 12 m, hub at least 2 x the corridors that feed it.
---   area: number = from size — Floor area in m² (give area or size).
---   size: {w, d} — Footprint in metres.
---   kind: string = from the area — "corridor", "room", "hall", "atrium" or "hub".
---   height: number = none — A height to check against the rule (adds ok and message).
---   feeds: number = kit.defaults.corridor_height — Height of the corridors feeding a hub, metres.
function kit.proportion(t) end  --- returns proportion
--- A room that is not a box: a rect, octagon, L, T, U or cross plan (or any simple polygon footprint), corners chamfered, with a flat, stepped, gabled, shed, coffered or beamed ceiling. Air pieces are convex prisms meeting on identical faces; the record works with kit.corridor/doorway/opening like a kit.room (min/max is the plan's bounds, max.y the base ceiling).
---   name: string = shaped_room<n> — Names the room (tags).
---   pos: vec — Required: the floor point at the centre of the plan.
---   size: {w, d} — The plan's bounds along x and z before facing (give size, or footprint).
---   footprint: {{x, z}...} = none — A simple polygon (3 to 64 points, either winding) relative to pos, instead of a named shape.
---   shape: string = "rect" — "rect", "octagon", "L", "T", "U" or "cross".
---   arm: number = 45% of the short side — Arm width of an L, T, U or cross plan, metres.
---   chamfer: number = octagon: 29% of the short side, others 0 — Cut every outer corner by this many metres (45° walls; 1-3 m reads as a built corner).
---   height: number = kit.proportion for the plan's area — Base clear height, metres.
---   ceiling: string = "flat" — "flat", "stepped" (a raised bay), "gable" (a ridged bay), "shed" (a bay sloping up along its long axis), "coffered" (a grid of recesses) or "beamed" (detail beams under a flat ceiling).
---   rise: number = stepped/gable/shed: max(2, 0.6 x height); coffered: 0.75 — How far the ceiling feature rises above the base height, metres.
---   inset: number = 25% of the short side — How far the raised bay stays in from the walls (stepped, gable, shed, coffered), metres.
---   region: {x0, z0, x1, z1} = from inset — The raised bay as a rectangle relative to pos (before facing), instead of inset.
---   facing: string = "+z" — Rotates the plan: the local +z side faces this way ("+x", "-x", "+z", "-z").
---   detail: boolean = false — Make the air pieces detail.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.shaped_room(t) end  --- returns room
--- Air insets cut outward from one wall of a room (only where that side really is a wall): count evenly spaced alcoves, or one at each offset in at. Fails when an alcove would break into another space.
---   name: string = alcoves<n> — Names the alcoves (tags).
---   room: record — Required: a kit.room or kit.shaped_room record.
---   side: string — Required: the wall, "+x", "-x", "+z" or "-z" (world).
---   count: integer = as many as fit at spacing — How many alcoves (give count or at).
---   at: number[] = none — Offsets of the alcove centres along the wall from its middle, metres.
---   width: number = 3 — Alcove width, metres.
---   depth: number = 1.5 — How far the alcove goes into the wall, metres (1-2 reads as an inset).
---   height: number = 3.5 (at most the room height) — Alcove clear height, metres.
---   spacing: number = width + 2 — Centre spacing when placing by count, metres.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.alcoves(t) end  --- returns alcoves
--- Pilasters (shallow piers) against a room's walls every spacing metres, only where the side really is a wall; detail by default.
---   name: string = pilasters<n> — Names the pilasters (tags).
---   room: record — Required: a kit.room or kit.shaped_room record.
---   sides: string[] = all four — The walls to line ({"+x", "-z"}, ...).
---   spacing: number = 5 — Centre spacing, metres (4-6 m reads as a panelled wall).
---   width: number = 1 — Pilaster width along the wall, metres.
---   depth: number = 0.5 — How far a pilaster stands out of the wall, metres.
---   height: number = the room height — Pilaster height, metres.
---   detail: boolean = true — Make the pilasters detail.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.pilasters(t) end  --- returns pilasters
--- A raised (rise > 0, a solid block) or sunken (rise < 0, an air pit) floor section over a floor rectangle; 0.5-1.5 m reads as a level change. A rise above the 1 m step limit needs stairs or a ramp (needs_steps in the record). With room, the rectangle must lie in the room's plan; in a convex plan with chamfered corners (room.chamfer > 0, room.outline) a rectangle that only crosses a chamfer is cut to the plan (the record's outline and clipped say so).
---   name: string = floor_section<n> — Names the section (tags).
---   room: record = none — The room it stands in (its floor height, and the rectangle must lie inside its plan).
---   min: vec — Required: one floor corner {x, y, z} of the rectangle (y ignored with room).
---   max: vec — Required: the opposite corner.
---   rise: number — Required: height change in metres (non-zero; positive raises, negative sinks).
---   y: number = the room's floor, else min.y — The floor height the section starts from.
---   detail: boolean = false — Make a raised block detail.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.floor_section(t) end  --- returns floor_section
--- A wide road cut along a floor path on any heading: one sloped or level air prism per leg and a level pad at every turn (at most 90°, half the road width deep on each side of the turn point), so grades and turns meet on identical faces. Legs listed in tunnels get a lower roof and no sky. A leg's grade is its rise over its sloped stretch: the leg's length less the level pads at its ends (grades[i]); warns when a grade is steeper than max_grade. The payload cart route.
---   name: string = road<n> — Names the road (tags).
---   path: vec[] — Required: 2 to 64 floor points; each leg at least width + 2 m long; a leg that climbs must climb at least 2% (a level leg is exactly level).
---   width: number = 8 — Road width, metres (the cart plus 2-3 players abreast).
---   height: number = 8 — Height of the cut's walls above the road, metres (the design grader's median corridor height limit is 8.5 m).
---   sky: boolean = true — Open the road to the sky (show_sky tops plus a kit.sky_room when the level has none).
---   tunnels: integer[] = none — Leg numbers (leg i runs from path point i to i + 1) roofed as tunnels.
---   tunnel_height: number = 6 — Clear height inside a tunnel leg, metres.
---   max_grade: number = 0.15 — The steepest grade before a warning: rise over the sloped stretch, which excludes the level turn pads (a 4.5 m climb on a 35 m leg between two turns slopes over 27 m: 17%). Uphill pushes are the tense moments; 45° is the hard limit.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.road(t) end  --- returns road
--- Organic moderation of a kit.cave or kit.canyon record (spec c.1-8): its floor area, flat_share (level floor, target at least 0.3), sloped_share, and the built elements standing in it (kit beams, trusses, bridges, platforms, stairs...) per 100 m² (target at least 1), with ok and warnings.
---   along: record — Required: a kit.cave, kit.canyon or kit.passage record.
function kit.organic_report(t) end  --- returns organic_report
--- The kit.space archetype library: {name, class ("indoor", "open_air" or "payload"), size = {w, d}, min = {w, d}, summary} for every archetype, in library order.
function kit.archetypes(t) end  --- returns archetype[]
--- Builds a whole space from the archetype library at exemplar scale (kit.archetypes lists them: hall, atrium, arena, ring, sunken_court, stair_hall, courtyard, canyon_bridge, cave_chamber, pit_room, control_room, storage; open-air platform, sky_bridge, open_deck; payload cart_road, overlook, gate, finale). Returns {kind = "space", name, archetype, class, summary, min, max (the main room's bounds), floor_y, pos, size, facing, parts, brushes, group, anchors, lights, warnings, organic}; every brush is tagged kit.space = name. parts maps each part name to a record with world bounds min/max: kit records as returned (rooms, corridors, stairs, bridges ...), single brushes as {kind = "brush", brush, brushes, min, max}, part groups as {kind = "group", items, brushes, min, max} - so for _, p in pairs(s.parts) do print(p.min, p.max) end always works. anchors lists the attach points: {kind ("door" or "road"), pos (vec: the floor point where the opening's centre meets the space's outer wall), dir ("+x", "-x", "+z" or "-z": outward), width, height}; join a corridor by ending a kit.passage{path = {..., a.pos}} (or a kit.corridor path) exactly at pos along dir, at most width wide and height high.
---   archetype: string — Required: an archetype name from kit.archetypes{}.
---   name: string = <archetype><n> — Names the space (prefix of every part's name, the group and the kit.space tag).
---   pos: vec — Required: the floor point at the centre of the space's plan (the canyon floor, the main deck top, the road start's centre line at its middle).
---   size: {w, d} = the archetype's size — The plan size before facing; at least the archetype's minimum.
---   facing: string = "+z" — Rotates the space: its local +z side faces this way.
---   seed: integer = 1 — The random stream of any organic part (canyon, cave, pit, rocks).
---   lights: boolean = true — Place Light objects through the space.
---   group: boolean = true — Group every brush under the space's name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face of the space's structure.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   trim_texture: string = the space's textures; posts, rails, beams and truss frames a stock girder (timber in a cave chamber) — The texture of trims, rails, posts, beams and parapets.
function kit.space(t) end  --- returns space
--- The ground under a point through the kit's ground provider: {y, normal, slope, kind, pos, provider}; kind "brush" (a brush floor), "detail" (a detail top), "terrain" (an Alpine terrain's top, where it lies over the brush floor) or "void" (nothing below, no y). Every kit placement rule reads the ground this way.
---   x: number — Required: world x, metres.
---   z: number — Required: world z, metres.
---   y: number = the top of the column — Look for the highest floor at or below this height (pick a storey).
function kit.ground_at(t) end  --- returns ground
--- Samples the ground provider on a grid over a floor rectangle: {count, found, min_y, max_y, mean_y, flat_share (slope under 5°), samples = {{x, z, y, kind, slope}...}, provider}.
---   min: vec — Required: one corner {x, y, z} of the rectangle (y is ignored).
---   max: vec — Required: the opposite corner.
---   step: number = 2 — Grid spacing in metres (at most 4,096 samples).
---   y: number = the top of each column — Look for floors at or below this height.
function kit.ground_region(t) end  --- returns ground_region
--- Swaps the ground provider every kit placement rule reads (the terrain seam): at(x, z, y?) returns {y, normal = {x, y, z}, kind} or nil; region(box) is optional. No options restores the built-in "brush" provider. Returns the previous provider's name.
---   name: string = "custom" — The provider's name (reported by kit.ground_at).
---   at: function — The point query; required unless resetting.
---   region: function = sampled from at — The rectangle query (optional fast path).
function kit.ground_provider(t) end  --- returns string
--- One straight beam (any direction) from from to to: a convex brush with a size section, detail by default. Use it for posts, rafters, braces, rails and supports.
---   name: string = beam<n> — Names the beam (tags).
---   from: vec — Required: one end of the beam's centre line.
---   to: vec — Required: the other end (at least 0.05 m away).
---   size: number|{w, h} = 0.3 — Section: a square side, or width x height (height along up), metres.
---   up: vec = {0, 1, 0} — Which way the section's height faces (made perpendicular to the beam).
---   detail: boolean = true — Make the beam detail (it never seals or splits rooms).
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.beam(t) end  --- returns beam
--- A truss of detail beams: bottom chord from → to, top chord depth above it, posts and diagonals per panel; width > 0 makes two parallel trusses tied by floor beams (a bridge or gantry frame).
---   name: string = truss<n> — Names the truss (tags, group).
---   from: vec — Required: one end of the bottom chord's centre line.
---   to: vec — Required: the other end (at least 2 m away).
---   depth: number = 2 — Distance from the bottom to the top chord, metres.
---   width: number = 0 — Distance between two truss planes (0 = one plane), metres.
---   panels: integer = length / depth, rounded (at least 2) — Number of panels.
---   style: string = "warren" — "warren" (zig-zag diagonals), "pratt" (posts, diagonals falling toward mid-span) or "howe" (posts, diagonals rising toward mid-span).
---   chord: number = 0.3 — Chord section side, metres.
---   web: number = 0.2 — Post and diagonal section side, metres.
---   up: vec = {0, 1, 0} — The direction from the bottom chord to the top chord (made perpendicular to the span).
---   group: boolean = true — Group the members under the truss's name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.truss(t) end  --- returns truss
--- The intent layer: every kit-placed element with its role and what it serves, read from the kit.role / kit.purpose tags the kit writes on brushes and objects (they are saved with the level): cover (the lane or approach it breaks), route (the walkable places a bridge, catwalk or platform joins), landmark (what sees it), theme_detail (its theme and space: props, clutter, rubble, decals, overhead runs) and fixture (the lights it carries). Elements are grouped by kit.name and role. Returns {counts = {role = n elements}, elements[] {role, name, kit, purpose, brushes, objects, min, max}, unassigned (kit dressing and set-piece brushes and objects without a role)}.
---   role: string = all — Only elements of this role (cover, route, landmark, theme_detail, fixture).
---   name: string = all — Only elements whose kit.name is this.
function kit.roles(t) end  --- returns roles
--- A landmark-scale bridge of detail brushes: a deck whose top runs from → to (ends without y sit on the ground), carried by a through truss, an arch, girders or as a plank deck with rails. Reports the span, the unsupported gap and the drop under mid-span (all read through the ground provider) and warns below landmark scale (span < 12 m, drop < 6 m). A bridge must serve a route: before building, its ends must land on walkable floor and enough floor on each side must reach the far end sooner across it (purpose; a bridge over stairs or floor that already joins its ends, or whose ends lead nowhere, is refused with the reason). Every brush is tagged kit.role = "route" and kit.purpose = what it joins; the record carries role and purpose {ok, fault, catch_a, catch_b, detour, raised, text}.
---   name: string = bridge<n> — Names the bridge (tags, group).
---   from: vec — Required: one end of the deck's walking surface (centre of its width); {x, z} takes y from the ground.
---   to: vec — Required: the other end (the deck may climb, at most 45°).
---   width: number = 3.5 — Walkable deck width, metres (exemplar landmark bridges are 2-4 m).
---   style: string = "truss" — "truss" (a through truss whose chords are the rails), "arch" (an arch rib under each deck edge with spandrel posts), "girder" (two deep girders under the deck) or "plank" (a plank deck on cross beams with post-and-rail sides).
---   deck: number = 0.4 — Deck thickness, metres.
---   depth: number = by style (truss: span / 10 within 1.6-3; arch: rise span / 5; girder: span / 15 at least 0.8) — Structure depth, metres.
---   railing: boolean = true — Posts and a hand rail along both edges (truss style: the chords are the rails).
---   landmark: boolean = true — Warn when the bridge is below landmark scale (span under 12 m or drop under 6 m).
---   group: boolean = true — Group every brush under the bridge's name.
---   texture: string = kit.defaults.texture (nil: textures below) — The deck's texture (every face).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — The deck's textures by role.
---   frame_texture: string = the deck's — The truss/arch/girder/rail texture.
---   purpose: string = "require" — The route check (measured on the walk lattice before anything is built, the grader's G27 rule): both ends must land on walkable floor, at least 150 m² of floor on EACH side must reach the far end sooner across the deck than any other way (the floor below, stairs at both ends or a bridge beside it must not already join them), and at most 30 % of the free span may sit over stairs or raised floor. "require" refuses a bridge that fails with the reason, "report" warns and builds, "quiet" builds without measuring and tags it for the grader (the kit's own spaces and layouts), "off" skips the check (build the approaches first).
function kit.bridge(t) end  --- returns bridge
--- An outdoor landmark standing on the floor point pos: a tapering tower, a lattice pylon of trusses, or a spire on a plinth. Exemplar outdoor landmarks stand 25-40 m tall; it warns outside that range. A landmark is there to be seen: before building, the walkable floor within 80 m that sees its upper part is measured, and one seen from fewer than 2 spaces and under 200 m² of floor is refused (purpose). Its brushes are tagged kit.role = "landmark" and kit.purpose = what sees it; the record carries role and purpose {ok, fault, area, spaces, text}.
---   name: string = landmark<n> — Names the landmark (tags, group).
---   pos: vec — Required: the floor point at the centre of its base.
---   height: number = 30 — Total height, metres.
---   base: number = height / 4 — Base width, metres.
---   style: string = "tower" — "tower" (a tapering solid shaft with a wider cap), "pylon" (four truss faces tapering to a top platform) or "spire" (a plinth and a tall tapering needle).
---   sides: integer = 8 — Sides of the tower or spire section (4 to 16).
---   group: boolean = true — Group the brushes under the landmark's name.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   purpose: string = "require" — The visibility check (G-rule: a landmark is seen from several spaces): "require" refuses a hidden landmark with the reason, "report" warns and builds, "quiet" builds without measuring (the kit's own spaces), "off" skips it. With no walkable floor within 80 m yet it builds unchecked.
function kit.landmark(t) end  --- returns landmark
--- An air opening through the wall between two records (abutting both, like kit.doorway): a window above a sill (a sight link), a round-topped arch, a rough seeded mouth, or a plain door. Warns when the opening is wider than 0.8x the clear width or taller than 0.8x the headroom of either space (the two would merge into one).
---   name: string = opening<n> — Names the opening (tags).
---   a: record — Required: the space on one side of the wall.
---   b: record — Required: the space on the other side.
---   shape: string = "window" — "window" (rectangle above a sill), "arch" (round top), "rough" (seeded broken outline) or "door" (plain rectangle on the floor).
---   width: number = window 4, arch 4, rough 4, door kit.defaults.door_width — Clear width, metres.
---   height: number = window 2, arch 5, rough 4.5, door kit.defaults.door_height — Clear height above the sill, metres.
---   sill: number = window 1.2, others 0 — Height of the opening's bottom above a's floor, metres.
---   floor_y: number = a's floor + sill — The opening's bottom height (overrides sill).
---   offset: number = 0 — Shift across the shared span, metres.
---   seed: integer = 1 — The random stream of a rough outline.
---   portal: boolean = false — Make the opening an air portal brush: it cuts the wall itself, so the walls it opens become portals and the opening a small room between the two spaces (RED's portal construct).
---   allow_tight: boolean = false — Allow a walkable shape (arch, rough, door) below the player's size.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face.
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
function kit.opening(t) end  --- returns opening
--- Builds exactly the plan given (from a reference) through the layout builder: literal zones, doorways, kit ramps/stairs, decks, paths, storeys, blocks; trims, frames, lights; rock elsewhere.
---   name: string = trace<n> — Names the plan (tags, group).
---   regions: table[] — Required: {name, poly = {{x, z}...}, kind = room|corridor|yard|pit|deck|ramp|stairs|canyon|cave|path, floor (ramp/stairs: {low, high}), ceiling? or height?, sky?, storey?, climb = {dx, dz} (ramp/stairs uphill), riser?, thickness? (deck), material?|texture?|textures?}. kind = path: path = {{x, z}...} centre line, width|widths, floor|floors per point (a sloped or curved ramp), split into name.1, name.2... A ramp or stairs may have any outline (one sloped floor from floor[1] at its foot to floor[2] along climb); standing on its own its ceiling runs parallel to its floor at its height (an absolute ceiling keeps it flat). Ramps, stairs and decks may lie inside another region; others overlap only as stacked storeys. Organic (detail rock over the traced air; the floor keeps the outline within 0.35 m): kind = "canyon" (open sky, 16 m, rock walls in strata that step back above 0.3 x height by lean? = 1.5 m where no other space is near, overhang?) or "cave" (rock walls with a vault and hanging rock, pillars? = count or points); organic = true on a path or room (a rough tunnel); walls = "rock" or rock_edges = {i...} (edge i runs from point i to i + 1); ground = "organic" (gentle mounds, relief? = 0.5) or "terrain" (kit.ground); boulders = count or {count?, density? (per 100 m²), size?}; rock_detail = true (kit.rock_detail); seed?. Doorways, keep_clear routes and hosted ramps stay clear; rocky regions default to material "rock".
---   keep_clear: {x, z}[][] = none — Routes (polylines in the plan's units, e.g. a cart route) that boulders, mounds and rock formations keep at least 1.75 m clear of.
---   seed: integer = 1 — The random stream of the organic rock (each region adds its own seed).
---   storeys: table[] = none — {name, floor, ceiling? | height?}: a storey a region joins with storey = name (its floor and ceiling defaults); stacked regions need lower ceiling <= upper floor - 0.25.
---   openings: table[] = none — {a, b, at = {x, z}, width = 3, height = 3}: a doorway of width cut through the gap between regions a and b at the point at (on or between their walls, at most 4 m from each); floors must agree within 0.5 m.
---   solids: table[] = none — {poly | circle = {x, z, r}, region?, base?, top? (a height, or "ceiling") | height? (1.5), detail? (true)}: a block standing in a region (pillar, machine footprint); base defaults to the floor of the region it stands in.
---   units: string = "m" — "m": points are world {x, z} metres; "px": points (and at, circle, climb, path widths) are picture pixels {x, y} mapped through ref.calibrate.
---   tolerance: number = 0.15 — Weld distance (m): corners this close to another region's corner or wall move onto it (and are inserted into that wall), so shared walls match; a thinner wedge left between two regions is refused.
---   check: boolean = false — After building, run the leak check (a sealed build, a few seconds): trace.holes and trace.hole_positions near the plan; holes also log a warning.
---   build: boolean = true — false validates and reports (areas, connections with their drop and ledge flag, hosts, stacked storeys, warnings, and with via = "layout" the graph and layout_check) without building.
---   via: string = "layout" — "layout": the plan becomes a kit.layout graph of literal zones (shared walls as joins, doorways as links at their traced places) built by kit.build_layout: kit.check_layout first (trace.layout_check {ok, errors, flow, rules}), ramps and stairs that are rectangles along climb through kit.ramp / kit.stairs (purpose = "require": one that serves no route is warned and still built), the room detail pass and lights (trace.layout, trace.detail, trace.literal_notes: where literal and generated building differ). "prism": the plan's own prisms only.
---   lights: boolean = true — via = "layout": the layout builder's lights in every zone (false when kit.dress_trace or a script lights the plan).
---   detail: boolean|table = true — via = "layout": kit.build_layout's room detail pass (wall trims and pilasters, door frames, ceiling beams, railings at drops, stair stringers and handrails, rock formations); a table turns parts off as there.
---   texture: string = kit.defaults.texture (nil: textures below) — One texture for every face (a region's own texture/textures wins).
---   textures: {floor=, wall=, ceiling=} = kit.defaults.textures (stock concrete) — Textures by player-relative role.
---   material: string|palette = none — Textures for every region by material (a region's own material/texture wins): concrete, metal, metal_floor, grate, rock, or a kit.palette theme or record. Mapped at 64 px/m, aligned across the plan.
function kit.trace(t) end  --- returns trace
--- Themes a kit.trace build from its reference shots without moving a wall or floor: per region its textures by role (ref.match_textures picks), lights in the shot's colours (ref.shot_lighting), structural detail (pilasters, beams, pipes, deck railings, opening frames, machinery) and props (a kit.dress theme or stock clutter with counts). Detail brushes and objects only, so the level stays sealed. Returns {name, regions = {name = {textured, lights, accents, details, props, dress}}, lights, objects, brushes, warnings}.
---   trace: record — Required: the record kit.trace{...} returned (built).
---   regions: table — Required: {region_name = {floor?, wall?, ceiling?, trim? (textures), texture? (all roles), lighting? (a ref.shot_lighting result), light? {color?, intensity?, fixture?, spacing?, accents?} (a region gets lights and fixtures only when it gives lighting or light), theme? (kit.dress theme for props and fixtures), style? (detail style family), details? {railings, frames, pillars, beams, pipes, machinery}, props? ("theme" or {{class, count}}), density?, seed?, uv? (ppm)}}; "*" applies to every region not listed.
---   ambient: {r, g, b}|table = unchanged — The level ambient colour, or a ref.shot_lighting result (its ambient_color).
---   fog: {r, g, b}|table = unchanged — The level fog colour, or a ref.shot_lighting result (its fog_color).
---   uv: number = 64 — Texel density (pixels per metre) for level.uv_align on the retextured brushes (RED's stock 256 px per 4 m); a region's uv wins.
---   name: string = dress_trace<n> — Names the dressing (tags, light names).
function kit.dress_trace(t) end  --- returns dressing
--- A coherent set of stock textures for one zone: lists by role (floor, wall, ceiling, trim, post, frame, accent, stripe; floor and wall never share a texture), the role table one room uses (textures, for kit.room), and the trim set (trims, for kit.trim). Give each zone its own theme or variant.
---   theme: string — Required: "industrial", "bunker", "admin", "cave" or "marsbase" (also "tech", "office", "rock", "mine", "concrete", "mars").
---   variant: integer = 1 — Which entry of each list textures and trims pick (wrapping around): rooms of one zone with different variants share the family but not the exact textures.
---   team: string = neutral — "red" or "blue": team-keyed frame and accent textures, so the two halves of a team map read apart.
function kit.palette(t) end  --- returns palette
--- The wall kit for a room: a base band, a top band under the ceiling, posts at panel joints, frames around every opening through its walls and hazard stripes on its drop edges, as thin detail brushes with UVs fitted to each strip. Build it after the room's openings and the solids standing in it.
---   room: record — Required: a kit room or corridor record (its min/max box); openings are the air brushes that reach through its walls.
---   name: string = trim<n> — Names the trim (tags, group).
---   base: number|false = 0.75 — Base band height in metres (exemplar wall kits: 0.5 to 1.2); false = none.
---   top: number|false = 0.4 — Top band height under the ceiling in metres; false = none.
---   posts: number|false = 5 — Post spacing along each wall in metres (exemplars: a post or frame every 4 to 6 m); false = none.
---   post_width: number = 0.4 — Post width in metres.
---   depth: number = 0.1 — How far the bands stand out from the wall, metres (posts and frames stand 0.05 m further).
---   frames: boolean = true — Frame every opening through the walls (jambs and a header; a sill under a raised opening).
---   stripes: boolean|record[] = true — Hazard stripes along the drop edges of the kit.platform tops standing in the room (or of the given platform records or brushes): tops at least kit.limits.max_riser above the floor, on every side not against a wall or met by stairs, a ramp or another solid within a step.
---   palette: palette = kit.palette{theme = "industrial"} — The palette whose trims set supplies the textures.
---   textures: {base=, top=, post=, frame=, stripe=} = the palette's trims — Textures per trim part (any subset; the rest come from the palette).
---   group: boolean = true — Group the trim's brushes under its name.
function kit.trim(t) end  --- returns trim

