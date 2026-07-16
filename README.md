# Glacier (GED)

**Glacier** — GED, short for **Glacier EDitor** — is a modern, from-scratch level editor for Red Faction
(2001) `.rfl` levels. It is a clean-room reimplementation built in .NET 8 + Avalonia, that aims for **stock-RED editor
parity**, **Alpine Faction editor parity**, and a layer of **modern editing
conveniences** on top of both.

GED reads every stock RFL version (`0xB4`–`0xC8`) and every Alpine RFL version
(`300`–`305`), and **always saves Alpine v305** — a loaded pre-305 level is
upgraded on save, exactly as Alpine RED (which stamps v305 on every save). A v305
source re-saves byte-identically (apart from the header timestamp); a pre-305
source upgrades once and every save after that is byte-stable (fixpoint). A
compatibility check reports which Alpine-specific features a level uses
(informational — the save is always v305).

## What GED can do

- **File I/O & round-trip** — Open / Save / Save As `.rfl`, New Level, MRU,
  drag-drop open, `--open` CLI. Lossless read/write of every section with a
  byte-preserving round-trip guarantee across the test corpus. Always saves
  Alpine v305 (pre-305 levels upgraded on save); a compatibility check reports
  which Alpine-specific features a level uses. `.rfg` group save/load with UID remap
  and link repair. Autosave with crash-recovery restore prompt.
- **Viewports & rendering** — Persistent 4-pane layout (three ortho wireframe +
  a Free Look perspective) with active-under-mouse focus, F4 solo / F5 reset,
  and 1/2/4-pane presets. Direct3D 11 renderer with GPU id-buffer picking,
  selection outlines, and a viewport HUD (mode / grid / scheme / stats / live
  room). Stock render modes, room-graph portal culling, current-room-only, and
  path-node connections. Four camera schemes (RED Classic, Modern FPS, Orbit,
  UnrealEd-style) with full stock camera-control parity.
- **Live previews** — Table-accurate particle and bolt emitters, distance fog,
  gas regions, coronas, light ranges, event direction arrows, and skybox, with
  an animate-emitters toggle.
- **Brush / CSG editing** — Cookie-cutter creation (Box / Cone / Cylinder /
  Face / Mesh / Sphere / Wedge), air/solid boolean model with time ordering,
  portal and detail brushes, steam emitters and breakable-glass rules. Clip,
  Carve, Fuse, Stretch, Bend, Twist, Mirror X/Y/Z, snap-to-grid, reorient, and
  more. Interactive move/rotate/scale gizmos plus keyboard transforms, live CSG
  preview, and **unlimited undo/redo** that survives save.
- **Face / vertex editing** — Extrude, Bevel, Flip Edge, Collapse, Triangulate,
  Pinwheel, Mesh Smooth, Combine, Split, Delete, portal-from-faces; vertex Weld,
  Collapse, Jitter, Align, Bridge, snap-to-grid — with stock validation
  messaging surfaced as non-modal feedback.
- **Texturing & UV** — Dockable Asset Browser (textures / meshes / sounds) with
  search, favorites, rendered mesh thumbnails, and where-used lookup. Box /
  planar / cylinder mapping, per-face properties (scroll, full-bright, alpha,
  holes, lightmap resolution, smoothing groups, liquid, and more), and a full
  UV Unwrap editor. Decoders for TGA, VBM, DDS (BCn), PNG, JPG, and ATX with the
  game-matching supersede chain.
- **Objects, events & links** — Every object type with data-driven inspectors,
  all 90 stock events (IDs 0–89) and all 58 Alpine events (IDs 100–157), links
  (K / break / edit, one-to-many, back-links) with the interactive **Link Graph
  2.0** editor (drag a node's port to create a link, click an edge + Delete to
  break it — all undo-able), and multi-select property editing with mixed-value
  handling.
- **Graph views** — Two interactive dock panels on a shared pan/zoom canvas with
  a minimap: **Link Graph 2.0** (the node-graph link editor above; node positions
  persist per level in a `<level>.gedlayout.json` sidecar) and the **Dependency
  Graph** — a level → category → file view of everything the level relies on, with
  Included / BaseGameSkipped / Missing colour-coding, "why is this included"
  referencer jump-to buttons, and per-node include checkboxes wired to the
  Create-Packfile dialog.
- **Triggers, movers, groups, cutscenes** — Full trigger inspector, movers with
  keyframes and an animated path-preview ghost, the Master / User-Defined /
  Moving group trees with duplicate and mirror, and cutscene paths with a
  viewport polyline and camera glyph.
- **Geometry & lighting** — Full compile pipeline (time-ordered brush booleans →
  portal chopping → room building → t-joint fixing → lightmap UV), cancelable
  background builds with a structured, clickable build report and hole/leak
  detection. Multithreaded, byte-exact lightmap baking (point / spot / tube
  lights, ambient, shadows) with Alpine smoothlights-quality output and a live
  preview.
- **Assets & packaging** — Create-Level-Packfile (`.vpp`) builder with a full
  dependency scanner (meshes, animations, textures, sounds, event file refs) and
  a review dialog (include / exclude, missing report, size stats), plus the
  Dependency Graph panel. Texture verifier, level linter, and a level-statistics
  dashboard with budget bars. **Editor-only sidecar files** — the Link Graph's
  per-level `<level>.gedlayout.json` layout, `.autosave.rfl` snapshots, and
  `.gedprefab` packages — live next to the `.rfl` but are never written into it
  and are excluded from the packfile scanner.
- **Import / export & prefabs** — Import OBJ / FBX / glTF / GLB / DAE to brushes
  or mesh objects with a scale/axis wizard; export static geometry to glTF, OBJ,
  and VRML, and brushes to `.v3m`. A prefab system that saves and places
  parameterized asset groups and bakes to plain RFL.
- **Playtest** — Play Level (**F7**), Play from Camera (**F8**), Play in Multi
  (**F9**) and Play in Multi from Camera (**F10**), with a configurable game exe
  (stock RF or the Alpine launcher) and save-before-launch.
- **Quality of life** — First-run wizard, unlimited undo with a history panel,
  fully bindable hotkeys with RED Classic / Modern presets and conflict
  detection, a command palette, a searchable settings dialog, dark/light themes,
  and stock-bug fixes by construction.

## Quick start

1. **First-run wizard.** On first launch, GED walks you through locating and
   validating your Red Faction install, picking a keymap preset (**RED Classic**
   or **Modern**), a camera scheme, and a theme. It writes and applies these
   settings; your install's VPPs are mounted automatically the first time you
   open a level.
2. **Open a level.** Use File ▸ Open (or drag a `.rfl` onto the window, or pass
   `--open <file>`).
3. **Build geometry.** Compile the level from the Level menu or the toolbar.
   Builds run in the background and never block the UI.
4. **Bake lighting.** Calculate Lightmaps / Calculate Lighting (with or without
   shadows) from the Level menu (or **L** / **Shift+L**).
5. **Create a level packfile.** Build a `.vpp` with the dependency scanner and
   review dialog; the level `.rfl` is written as the first entry.
6. **Play.** **F7** Play Level, **F8** Play from Camera, **F9** Play in Multi,
   **F10** Play in Multi from Camera.

## Files & settings

GED is a **portable app**: everything it writes lives **next to the executable**
(the folder `Ged.App.exe` runs from), so you can drop the app on a USB stick or in
any folder and keep your setup with it. No installer, no registry, nothing left in
your user profile.

| File / folder | What it holds |
| --- | --- |
| `settings.cfg` | Your settings (install path, viewport prefs, theme, MRU, colors). JSON content, `.cfg` extension. |
| `keymap.cfg` | Your key bindings (preset + overrides). |
| `logs\` | Crash logs (`crash-<timestamp>.log`) and a rolling `session.log`. |
| `cache\` | The texture/mesh thumbnail cache (safe to delete; regenerated on demand). |
| `prefabs\` | The default prefab library (you can point this elsewhere in Settings). |
| `recovery\` | Emergency autosaves written if the editor crashes mid-edit. |

`settings.cfg` is authoritative and is created on first save; a fresh copy starts
from defaults.

**Read-only install folder?** If the app's folder is not writable (for example when
installed under `Program Files`, or run from read-only media), GED falls back to your
user profile so settings are never lost silently — `settings.cfg`, `keymap.cfg` and
`prefabs\` under `%APPDATA%\Glacier`, and `logs\`, `cache\`, `recovery\` under
`%LOCALAPPDATA%\Glacier` — and shows a one-time notice on startup. Install into
a writable folder to keep everything portable.

On **Linux** the same layout applies with POSIX paths: portable next to the binary
when that directory is writable (a `tar.gz` extracted to your home), else the profile
fallback — settings/keymap/prefabs under `~/.config/Glacier` and logs/cache/recovery
under `~/.local/share/Glacier` (the XDG base dirs .NET maps `ApplicationData` /
`LocalApplicationData` to). An **AppImage** mounts read-only, so it always uses the
profile fallback — that is expected and correct.

## Requirements

- **Windows 10/11 x64** (Direct3D 11 GPU; WARP software rasterizer works as a
  fallback), or **Linux x64** (Wayland via XWayland or X11; renders through a
  cross-platform OpenGL 3.3 host, so Mesa's `llvmpipe` software GL works too).
- **A Red Faction installation** — your own game files. GED loads textures,
  meshes, and `.tbl` tables directly from the install's VPP packfiles. **GED
  never redistributes game assets**; they remain your own copy.
- **Alpine Faction** — powers Alpine-only editor features and multiplayer
  playtest launching.

## Building from source

Requires the **.NET 8 SDK**.

```
dotnet build Glacier.sln -c Release
dotnet run --project src/Ged.App
```

Tests that exercise a real Red Faction install resolve it from the `GED_RF_DIR`
environment variable or a `research/rf-dirs.txt` file (one candidate path per line);
they skip automatically when neither points at an install.

Package a self-contained, single-file distribution with `tools/package.ps1`:

```
pwsh tools/package.ps1                     # win-x64  -> dist/Glacier-<ver>-win-x64.zip
pwsh tools/package.ps1 -Runtime linux-x64  # linux-x64 -> tar.gz + AppImage
pwsh tools/package.ps1 -Runtime all        # win zip + linux tar.gz + AppImage
```

Each archive is the single `Glacier` binary (native libraries self-extracted) plus
`scripts/`, `README.md`, `LICENSE` and `licensing-info.txt`. The linux-x64 target also
produces `dist/Glacier-<ver>-linux-x86_64.AppImage`: on a **Windows** host the AppDir
assembly and `appimagetool` run under **WSL2** (invoked automatically; pass `-NoAppImage`
to skip), and the same script (`tools/appimage/build-appimage.sh`) runs directly on a
Linux packaging host. `appimagetool` is a build tool, fetched once into your Linux/WSL
home — it is never committed to the repo and is not part of the shipped app.

## Linux

GED runs natively on Linux x64. It ships in two forms — pick whichever suits you:

| | **AppImage** (recommended) | **tar.gz** |
| --- | --- | --- |
| Install | none — `chmod +x` and run | extract anywhere |
| File | one `Glacier-<ver>-linux-x86_64.AppImage` | a folder of files |
| User data / settings | always `~/.config/Glacier` + `~/.local/share/Glacier` (the mount is read-only) | **portable** next to the binary if that folder is writable, else the same profile fallback |
| Bundled example scripts | in the profile `scripts/` (not the read-only mount) | beside the binary in `scripts/` |
| Best for | quick single-file download, USB, no-install | a portable working copy you fully own (settings + scripts beside it) |

**AppImage** — the whole app in one executable file:

```
chmod +x Glacier-<ver>-linux-x86_64.AppImage
./Glacier-<ver>-linux-x86_64.AppImage
```

Because an AppImage mounts **read-only**, GED can't write next to the binary, so your
settings, keymap, logs, cache, prefabs and recovery data live in your profile
(`~/.config/Glacier` and `~/.local/share/Glacier`) — that is expected and correct. If
your desktop can't run AppImages directly (no FUSE), run it with `--appimage-extract-and-run`.

**tar.gz** — a portable folder you extract yourself:

```
tar xzf Glacier-<ver>-linux-x64.tar.gz
cd Glacier-linux-x64
chmod +x Glacier      # the Windows tar drops the +x bit; set it once
./Glacier
```

Extracted somewhere writable (e.g. your home directory), the tar.gz build keeps
everything **portable** next to the binary, exactly like the Windows build.

The editor, compiler, lightmapper and viewport are all cross-platform. Ambient-sound
previews play through your system audio (`paplay`, falling back to `aplay`). Fonts come
from `fontconfig` (any standard desktop has a usable monospace + UI font); the code
panels request a `monospace` fallback so they render everywhere.

### System prerequisites

The build is self-contained (the .NET 8 runtime and Skia/HarfBuzz/Assimp natives are
bundled), so you only need a handful of system libraries that every desktop already
provides — plus a system **OpenGL** (the viewport renders through a cross-platform
OpenGL 3.3 host, and **Mesa's `llvmpipe` software GL works** if you have no GPU driver).
On a minimal/headless Debian/Ubuntu, install:

```
sudo apt install libice6 libsm6 libx11-6 libxext6 libxrandr2 libxcursor1 \
                 libfontconfig1 fonts-dejavu-core libgl1-mesa-dri libglx-mesa0
```

(Fedora/Arch have the same libraries under their own package names.) A full desktop
environment already satisfies all of these. Wayland sessions work through XWayland;
force X11 with `AVALONIA_PLATFORM=x11` if a native-Wayland quirk appears.

**Window-manager note:** many X11 WMs claim plain **Alt+drag** to move windows, which
is also GED's "temporarily invert snap" modifier during a transform drag. Hold
**Ctrl+Alt** during the drag instead (it reaches the viewport with Alt down and inverts
the snap), or rebind your WM's move modifier to Super. See `docs/HOTKEYS.md`.

### Playtesting under Wine

Red Faction / the Alpine Faction launcher are Windows executables, so on Linux GED
launches them through a **launch template** (Settings ▸ General ▸ *Playtest launch
template*). The Linux default is:

```
wine {exe} {args}
```

`{exe}` expands to the configured game executable and `{args}` to the `-level` /
`-levelm` playtest arguments. Any wrapper works — a Proton launch script,
`protontricks-launch`, or a custom shell script — as long as it ends up running the
exe with the arguments. Blank = launch the exe directly (the Windows default).

Recommended setup:

1. Install Red Faction + Alpine Faction into a Wine prefix (e.g. `~/.wine`), or use
   the Steam/Proton install.
2. In GED's first-run wizard (or Settings ▸ General), point **Red Faction install
   folder** at that install directory — e.g.
   `~/.wine/drive_c/Program Files (x86)/Steam/steamapps/common/Red Faction`. GED reads
   the VPPs directly from that path; because the whole staging flow goes through GED's
   VFS mount, a Wine-prefixed RF directory works unchanged.
3. Point **Game executable** at the Windows `AlpineFactionLauncher.exe` (or `RF.exe`)
   inside that prefix. GED stages the level into `<install>/user_maps/…` and launches
   it via the template, so Wine sees the exact same on-disk layout the game expects.
4. Make sure `wine` is on your `PATH` (or replace the template's first token with an
   absolute path to your Wine/Proton runner).

## License

Copyright (c) 2026 Chris "Goober" Parsons. See `LICENSE` for the terms that apply
to Glacier. Third-party and adapted-code attributions are in `licensing-info.txt`.
Red Faction game assets are your own files and are never shipped with GED.

## Trademarks

The **Glacier** name is not covered by the MIT license. You are welcome to use it
to refer to the official, unmodified builds. If you distribute a modified version,
please give it a different name and do not imply endorsement by the author.
