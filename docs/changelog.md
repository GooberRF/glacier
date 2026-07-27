❄⛏ Glacier Changelog ⛏❄
===================================

Version 1.2.0-HASH (Beta 3): Released 26-07-2026
--------------------------------

### Major features
[@GooberRF](https://github.com/GooberRF)
- Rebuilt the mover, group, and keyframe workflow to match RED. Mover brushes stay normal, fully-editable world brushes, and movers are stored the way RED stores them so they work correctly in-game. Levels made with the earlier movers are repaired automatically when opened — resave to keep the fix.

### Minor features, changes, and enhancements
[@GooberRF](https://github.com/GooberRF)
- The mover inspector now exposes every mover and keyframe setting RED does, including No Player Collide, Starts Backwards, Use Travel Time as Speed, Force Orient, all four movement sounds with volumes, and per-keyframe Script Name and Item UIDs.
- New movers now match RED's defaults: one-way motion, full sound volume, and keyframes with no triggered event or items.
- Added a Dissolve command to the Moving Groups list that turns a mover back into ordinary static brushes.
- In Object and Group mode, clicking any member of a group now selects the whole group; hold Alt to select the individual member.
- Mover keyframes now show RED's gold start diamond and solid silver diamonds, and the keyframe path draws in RED's dedicated red instead of the trigger-link yellow. In the Link Graph, mover structure shows as dashed red edges, distinct from real links.
- New levels now include a Player Start (a from-scratch level previously had no spawn point and opened to a black screen in-game), and Move Player Start Here now creates one when the level has none.
- The linter now flags a level that has no Player Start and no multiplayer respawn points.

### Bug fixes
[@GooberRF](https://github.com/GooberRF)
- Fixed levels built from scratch in Glacier rendering fully black in game even though they played normally: baked lighting is now written where the game reads it, matching RED. Older levels are corrected automatically when opened — resave and repack.
- Fixed a mover's collision not following it as it moved, and mover collision shapes being generated inside-out; movers now collide exactly where they are drawn, throughout their motion. Rebuild geometry and resave to update an existing mover level.
- Fixed movers built away from the world origin being displaced in-game, pinning or sticking the player; mover parts are now recorded relative to the start keyframe, the way RED does.
- Fixed linking a trigger or event to a mover attaching to its brush, which the game cannot resolve (the trigger fired but nothing moved); links now attach to the mover's start keyframe, the connection the game actually follows.
- Fixed a newly added keyframe dropping an inert, unselectable marker; it is now a real object the instant it is created, visible in the Outliner and draggable into position.
- Fixed deleting a keyframe removing the mover's brush. Keyframes now seed exactly at the mover's origin like RED and draw on top of geometry so they can always be clicked ("Add @ Cam" still drops one at the camera).
- Fixed deleting a mover's last keyframe leaving an invalid mover behind (it now dissolves back to static geometry), and deleting a mover's brushes or keyframes no longer leaves an empty moving group.
- Fixed locking a group not actually locking its members from selection, movement, or deletion.
- Fixed a freshly placed trigger recording its "attached to", "use clutter", and "airlock room" references as object 0 instead of RED's "none" value (-1).
- Fixed baked lighting being thrown away when geometry was edited after baking; the preview rebuild now keeps the existing bake (stale until you recalculate, exactly as RED does).
- Fixed every save after the first reporting "Saved with unbaked lighting changes" when nothing had changed; the reminder now appears only after a real lighting-relevant edit.
- Fixed Box, Planar, and Cylinder texture mapping following each brush's rotation; mapping now projects in world space like RED, with continuous tiling across adjacent brushes.
- Fixed the editor crashing when editing a trigger's numeric parameters; numeric fields in every inspector now tolerate partial or invalid text while typing and simply revert instead of crashing.
- Fixed the Properties panel not scrolling far enough to clear the last field on tall inspectors.
- Fixed building from source failing when a .NET 9 or newer SDK is installed.
- Saving a level with movers now records that in its file info, matching how RED marks a mover level.

[@natarii](https://github.com/natarii)
- Fixed the editor crashing on startup on some Linux desktops: the built-in renderer's offscreen graphics path now shares the UI's graphics context system.

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
