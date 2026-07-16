# Feature catalog

The complete, detailed breakdown of what Glacier (GED) can do. For a quick overview see the
[README](../README.md); for build and packaging instructions see
[building.md](building.md).

Throughout, features fall into three families: **stock** (Red Faction RED.exe parity),
**Alpine** (Alpine Faction editor parity), and **modern** (conveniences new to GED).

## Contents

- [File I/O & round-trip](#file-io--round-trip)
- [Viewports & rendering](#viewports--rendering)
- [Live previews](#live-previews)
- [Brush / CSG editing](#brush--csg-editing)
- [Face / vertex editing](#face--vertex-editing)
- [Texturing & UV](#texturing--uv)
- [Objects, events & links](#objects-events--links)
- [Graph views](#graph-views)
- [Triggers, movers, groups & cutscenes](#triggers-movers-groups--cutscenes)
- [Geometry & lighting](#geometry--lighting)
- [Assets & packaging](#assets--packaging)
- [Import / export & prefabs](#import--export--prefabs)
- [Playtest](#playtest)
- [Quality of life](#quality-of-life)

---

## File I/O & round-trip

- **Open / Save / Save As `.rfl`**, New Level, a most-recently-used list, drag-drop open, and
  an `--open <file>` command-line switch.
- **Lossless read/write of every section** with a byte-preserving round-trip guarantee across
  the test corpus: unknown or untouched sections are re-emitted verbatim, and a no-op
  open→save is byte-identical apart from the header timestamp.
- **Reads every stock RFL version** (`0xB4`–`0xC8`) and every Alpine version (`300`–`305`),
  and **always saves Alpine v305** — a loaded pre-305 level is upgraded on save, exactly as
  Alpine RED, which stamps v305 on every save. A v305 source re-saves byte-identically (apart
  from the header timestamp); a pre-305 source upgrades once and every save after that is
  byte-stable.
- **Compatibility check** reports which Alpine-specific features a level uses.
- **`.rfg` group save/load** with full UID remap and intra-import link repair.
- **Autosave** (toggle + interval), deferred while a viewport drag is in progress, with a
  crash-recovery restore prompt on next launch.

## Viewports & rendering

- **Persistent 4-pane layout** — three orthographic wireframe views plus a Free Look
  perspective — with active-under-mouse focus, **F4** solo / **F5** reset, and 1 / 2 / 4-pane
  presets. Any pane can show any view type.
- **Direct3D 11 renderer** (default on Windows) and a cross-platform **OpenGL 3.3** backend
  (always on Linux; selectable on Windows), pixel-faithful to each other.
- **GPU id-buffer picking**, selection outlines, and a viewport HUD showing mode, grid,
  camera scheme, scene stats, and the live current room.
- **Stock render modes** — textures, textures + lightmaps, lightmaps only, room colors,
  see-through, brushes-only, everything — plus room-graph portal culling (as in-game),
  current-room-only, portal-face draw modes, sky, bounding boxes, and path-node connections.
- **Four camera schemes** — RED Classic, Modern FPS, Orbit, and UnrealEd-style — with full
  stock camera-control parity (numpad pitch/heading/bank/slide, dolly, teleport, axis orient,
  scroll mode, and more).

## Live previews

Table-accurate previews rendered live in the viewport:

- **Particle and bolt emitters** driven by a deterministic, table-accurate simulation.
- **Distance fog**, **gas regions**, **coronas**, **light ranges**, **liquid surfaces**, and
  **event direction arrows**.
- **Skybox** kept visible under portal culling.
- An **Animate Emitters** toggle (per-emitter and global) drives the time-based effects.

## Brush / CSG editing

- **Cookie-cutter creation** — Box, Cone, Cylinder, Face, Mesh, Sphere, Wedge — with
  height/width/depth and split parameters, material, and snap-to-camera.
- **Air/solid boolean model** with strict time ordering (Start / End of Time), plus portal
  brushes, detail brushes, steam emitters (with the ≤3-jet rule), and breakable-glass rules.
- **Operators** — Clip (two-point plane, split/cut, flip normal), Carve, Fuse, Stretch
  (numeric dims + copy/paste dims), Bend, Twist, Mirror X/Y/Z, snap-to-grid, reorient,
  move-centers, and Update.
- **Interactive move / rotate / scale gizmos** (local/world, snapping) plus keyboard
  transforms, a **live CSG preview**, marquee box-select, and time-order visualization.
- **Unlimited undo/redo that survives save** — no more backing up via groups before a
  destructive boolean.

## Face / vertex editing

- **Face ops** — Extrude, Bevel, Flip Edge, Collapse, Triangulate, Pinwheel, Mesh Smooth,
  Combine (coplanar), Split (N-way along U/V), Delete, Flip Normal, and portal-from-faces.
- **Vertex ops** — Weld, Collapse, Jitter, Align X/Y/Z, Bridge, Delete, and snap-to-grid,
  with per-vertex dot picking.
- **Stock validation messaging** (e.g. "Faces aren't coplanar") is preserved as helpful,
  non-modal feedback; operations validate and roll back rather than corrupting the model.

## Texturing & UV

- **Dockable Asset Browser** (Textures / Meshes / Sounds) with instant search, favorites,
  rendered mesh thumbnails, tile-size control, Show Only Used, and a where-used lookup.
- **Mapping** — box / planar / cylinder, snap map, resize map, flip X/Y, UV copy/paste, and a
  ceiling/wall/floor default system with pixels-per-meter apply.
- **Per-face properties** — scroll, full-bright, alpha, holes, invisible, lightmap
  resolution, smoothing groups, show-sky, mirrored, liquid, and more — multi-select and
  mixed-value aware, fully undo-able.
- **UV Unwrap editor** — a floating 2D editor over the tiled texture with move/rotate/scale
  increments, grid snap, flips, aligns, decal-vs-texture modes, non-uniform scale, show-tiled,
  and print-to-TGA.
- **Decoders** for TGA, VBM, DDS (BCn), PNG, JPG, and ATX with the game-matching supersede
  chain; texture subdirectories appear as "Custom - <dir>" categories, and textures/meshes
  reload without a restart.

## Objects, events & links

- **Every object type** with data-driven inspectors — Player Start, MP respawn points,
  lights, ambient sounds, triggers, events, regions (geo/gas/climb/push), nav points, cutscene
  cameras, particle and bolt emitters, decals, targets, clutter, entities, items, and the
  Alpine mesh / note / corona / bag objects.
- **All 90 stock events** (IDs 0–89) and **all 58 Alpine events** (IDs 100–157) with a
  data-driven inspector (labels, dropdowns, index-vs-text handling, version gating), including
  directional events with an in-viewport facing arrow.
- **Links** — K link, break, edit; one-to-many; back-links; navpoint→event links; with
  duplicate/invalid-link error feedback and the event→object direction rule enforced.
- **Multi-select property editing everywhere**, with mixed-value handling.
- **Entity / clutter / item catalogs** read from the game's `.tbl` files, with rendered V3M
  thumbnail previews and search.

## Graph views

Two interactive dock panels on a shared pan/zoom canvas with a minimap:

- **Link Graph** — a node-graph link editor: drag a node's port to create a link, click an
  edge and press Delete to break it, all undo-able. Node positions persist per level in a
  `<level>.gedlayout.json` sidecar.
- **Dependency Graph** — a level → category → file view of everything the level relies on,
  with Included / BaseGameSkipped / Missing color-coding, "why is this included" referencer
  jump buttons, and per-node include checkboxes wired to the Create-Packfile dialog.

## Triggers, movers, groups & cutscenes

- **Triggers** — a full inspector with shape, resets, activated-by, key, airlock/attached/
  use-clutter UIDs, times, team, one-way, and the Alpine multiplayer flags.
- **Movers** — moving groups with keyframes (travel/pause/accel/decel, rotate-in-place,
  degrees-about-axis), all movement types, door behavior, triggered events and sounds, the
  Alpine Hold Open flag, and an animated path-preview ghost with a spline.
- **Groups** — Master / User-Defined / Moving trees with create/dissolve/add/remove/rename/
  lock, **Duplicate** (deep-copy with fresh UIDs + remapped links), and **Mirror X/Y/Z**.
- **Cutscenes** — cameras and path nodes with a viewport polyline and camera glyph.

## Geometry & lighting

- **Full compile pipeline** — time-ordered brush booleans → portal chopping → cleanup → room
  building → t-joint fixing → lightmap UV — running in a cancelable background build that
  never blocks the UI.
- **Structured build report** — rooms, subrooms, portals, faces, vertices, brushes, surfaces,
  lightmap pages, UIDs, and timing — with clickable errors/warnings and hole/leak detection
  (Check for Holes draws hole lines; Remove Hole Lines clears them).
- **Byte-exact lightmap baking** — point, spot, and tube lights, ambient, and shadows,
  reproducing RED's per-texel kernel, multithreaded (seconds, not minutes).
- **Alpine smoothlights quality** — per-texel room ambient, gutter replication, and
  within-fragment smoothing on by default, plus opt-in cross-room seam blending, corner-leak
  fixes, and smoothed gutter normals that close artifacts RED itself leaves.
- **Live lighting preview** using the exact CPU kernel, so light placement is visible before a
  full bake.

## Assets & packaging

- **Create-Level-Packfile (`.vpp`) builder** with a full dependency scanner covering meshes,
  animations, textures, sounds, and event file references, and a review dialog (include /
  exclude, missing report, size stats). The level `.rfl` is written as the first entry.
- **Dependency Graph panel** (see [Graph views](#graph-views)).
- **Texture verifier**, a **level linter**, and a **level-statistics dashboard** with budget
  bars.
- **Editor-only sidecar files** — the Link Graph's per-level `<level>.gedlayout.json` layout,
  `.autosave.rfl` snapshots, and `.gedprefab` packages — live next to the `.rfl` but are never
  written into it and are excluded from the packfile scanner.

## Import / export & prefabs

- **Import** OBJ / FBX / glTF / GLB / DAE to brushes or mesh objects, with a scale/axis
  wizard.
- **Export** static geometry to glTF, OBJ, and VRML, and brushes to `.v3m`.
- **Prefab system** — save and place parameterized asset groups; a placed prefab bakes to
  plain RFL, and prefabs interoperate with `.rfg` import/export.

## Playtest

- **Play Level** (**F7**), **Play from Camera** (**F8**), **Play in Multi** (**F9**), and
  **Play in Multi from Camera** (**F10**).
- A **configurable game executable** — stock `RF.exe` or the Alpine Faction launcher — with
  save-before-launch, and (on Linux) a Wine launch template. See
  [building.md](building.md#playtesting-under-wine) for the Wine setup.

## Quality of life

- **First-run wizard**, **unlimited undo** with a history panel, and stock-bug fixes by
  construction.
- **Fully bindable hotkeys** with RED Classic / Modern presets and conflict detection.
- A **command palette**, a **searchable settings dialog**, and **dark/light themes**.
