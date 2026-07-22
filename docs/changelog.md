❄⛏ Glacier Changelog ⛏❄
===================================

Version 1.2.0-HASH (Beta 3): Not yet released
--------------------------------

### Bug fixes
[@GooberRF](https://github.com/GooberRF)
- Fixed Box, Planar, and Cylinder texture mapping (Texture/UV tools) orienting to each brush's local frame after a brush was rotated, so textures flowed in a different direction on every brush. Mapping now projects in world space like RED: the selected faces map in the same direction with continuous tiling across adjacent brushes, whatever their rotation.

Version 1.1.0-b9e8ed6 (Beta 2): Released 22-07-2026
--------------------------------

### Major features
[@GooberRF](https://github.com/GooberRF)
- Editing brush vertices, edges, or faces now keeps every face flat: any face an edit bends off its plane is automatically triangulated, exactly as RED does.
- Added an Undo application setting (Settings ▸ General): Instant (the default) snaps straight to the chosen state in a single step, while Replay walks visibly through each intermediate history entry. This now governs plain Undo and Redo (Ctrl+Z / Ctrl+Y) as well as History-panel jumps. Both reach the same result.

### Minor features, changes, and enhancements
[@GooberRF](https://github.com/GooberRF)
- Under the UnrealEd camera scheme, a left click selects and a left drag flies the camera, instead of drawing a selection box.
- Clicking empty space, a locked object, or a wrong-kind object now always clears the current selection, including in Group mode.
- Keyboard nudge and rotate now work on vertex and face selections, not just edges.
- EAX environmental-audio effect zones are now selectable objects with a labelled inspector, and appear in the Outliner like other objects.

### Bug fixes
[@GooberRF](https://github.com/GooberRF)
- Fixed Undo, Redo, and other shortcuts going dead after alt-tabbing away or after a dialog took focus.
- Play from Camera now spawns you at the editor camera's exact position and heading, in both single-player and multiplayer, matching how RED hands off the camera.
- Fixed the faces of an air brush flagged as a portal disappearing in Brush mode while showing correctly in Object mode.
- Fixed Group mode not allowing brush selection; clicking a brush now selects it just like clicking an object.
- Fixed a stale selection highlight in Group mode: selecting an object now clears a previously selected brush, and selecting a brush clears a previously selected object, so only what is actually selected stays highlighted. Hold Ctrl to add across both kinds as before.
- Fixed a box-select over a large area in Object mode locking the editor up for minutes: the whole catch now applies as a single selection update instead of rebuilding the Outliner, Properties, and graph panels once per caught object.
- Fixed Unlock All (Shift+Q) and the Layers panel's Unlock button not unlocking brushes that shipped locked in the level file; they now clear the persisted lock state so file-locked brushes can be selected and edited again.
- Fixed a selection lingering after a mode switch: entering a mode now deselects anything that mode cannot select — an object is dropped when entering Brush, Face, Vertex, or Edge mode — while Group mode keeps both object and brush selections.
- Fixed vertex selection occasionally grabbing the wrong vertex, or none, when vertices overlap on screen or sit behind a face.
- Fixed the Layers panel so double-clicking any brush row always jumps the camera to it, and a locked row can be highlighted.
- Fixed Undo (Ctrl+Z) of a click-and-drag move visibly replaying the brush backward through every step under the Instant undo setting; it now snaps straight to the pre-drag state in a single refresh, and Redo does the same. The Replay setting still steps through the motion deliberately.
- Fixed a box-select started near the transform gizmo being swallowed by the gizmo instead of selecting.
- Fixed clicks landing just beside a vertex failing to select it.
- Fixed new lights being created disabled and without a light type; they now start enabled, switched on, omnidirectional, and shadow-casting.
- Fixed sluggishness while dragging or transforming geometry.
- Fixed the surrounding world not refreshing after moving a brush on a large level: committing a move, rotate, or keyboard nudge — or undoing or redoing one — now rebuilds the merged brushwork in the background whatever the brush count, so the brush's old location no longer keeps showing stale compiled geometry until the next manual build.
- Fixed camera lag in the perspective viewport.

Version 1.0.0-92c843e (Beta 1): Released 19-07-2026
--------------------------------
[@GooberRF](https://github.com/GooberRF)
- Initial beta release
