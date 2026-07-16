# Building & running

Everything needed to build Glacier (GED) from source, package a distribution, and run it on
Windows or Linux. For the feature overview see the [README](../README.md); for the full
feature breakdown see [catalog.md](catalog.md).

## Contents

- [Building from source](#building-from-source)
- [Packaging a distribution](#packaging-a-distribution)
- [Where files are stored](#where-files-are-stored)
- [Running on Windows](#running-on-windows)
- [Running on Linux](#running-on-linux)
- [Playtesting under Wine](#playtesting-under-wine)

---

## Building from source

Requires the **.NET 8 SDK**. From the repository root:

```
dotnet build Glacier.sln -c Release
dotnet run --project src/Ged.App
```

The Core, compiler, lightmapper, and linter layers are pure BCL C# with no UI dependency; the
viewport uses Direct3D 11 on Windows and OpenGL 3.3 elsewhere. Windows is the reference
platform, but the build is fully cross-platform.

Tests that exercise a real Red Faction install resolve it from the `GED_RF_DIR` environment
variable or a `research/rf-dirs.txt` file (one candidate path per line); they skip
automatically when neither points at an install.

## Packaging a distribution

Package a self-contained, single-file distribution with `tools/package.ps1`:

```
pwsh tools/package.ps1                     # win-x64   -> dist/Glacier-<ver>-win-x64.zip
pwsh tools/package.ps1 -Runtime linux-x64  # linux-x64 -> tar.gz + AppImage
pwsh tools/package.ps1 -Runtime all        # win zip + linux tar.gz + AppImage
```

Each archive contains the single `Glacier` binary (native libraries self-extracted) plus
`scripts/`, `README.md`, `LICENSE`, and `licensing-info.txt`.

The linux-x64 target also produces `dist/Glacier-<ver>-linux-x86_64.AppImage`. On a
**Windows** host the AppDir assembly and `appimagetool` run under **WSL2** (invoked
automatically; pass `-NoAppImage` to skip); the same script
(`tools/appimage/build-appimage.sh`) runs directly on a Linux packaging host. `appimagetool`
is a build tool, fetched once into your Linux/WSL home — it is never committed to the repo and
is not part of the shipped app.

## Where files are stored

GED is a **portable app**: by default everything it writes lives **next to the executable**
(the folder `Ged.App.exe` / `Glacier` runs from). No installer, no registry, nothing left in
your user profile.

| File / folder | What it holds |
| --- | --- |
| `settings.cfg` | Settings (install path, viewport prefs, theme, MRU, colors). JSON content, `.cfg` extension. |
| `keymap.cfg` | Key bindings (preset + overrides). |
| `logs\` | Crash logs (`crash-<timestamp>.log`) and a rolling `session.log`. |
| `cache\` | Texture/mesh thumbnail cache (safe to delete; regenerated on demand). |
| `prefabs\` | The default prefab library (relocatable in Settings). |
| `recovery\` | Emergency autosaves written if the editor crashes mid-edit. |

`settings.cfg` is authoritative and is created on first save; a fresh copy starts from
defaults.

**Read-only install folder?** If the app's folder is not writable (for example when installed
under `Program Files`, or run from read-only media), GED falls back to your user profile so
settings are never lost silently, and shows a one-time notice on startup:

- **Windows** — `settings.cfg`, `keymap.cfg`, and `prefabs\` under `%APPDATA%\Glacier`; `logs\`,
  `cache\`, `recovery\` under `%LOCALAPPDATA%\Glacier`.
- **Linux** — settings/keymap/prefabs under `~/.config/Glacier`; logs/cache/recovery under
  `~/.local/share/Glacier` (the XDG base dirs .NET maps `ApplicationData` /
  `LocalApplicationData` to).

Install into a writable folder to keep everything portable.

## Running on Windows

- **Windows 10/11 x64.** Direct3D 11 by default; the WARP software rasterizer works as a
  fallback with no GPU driver.
- The OpenGL 3.3 backend is selectable in **Settings ▸ Viewport ▸ Renderer**.

## Running on Linux

GED runs natively on Linux x64 and ships in two forms — pick whichever suits you:

| | **AppImage** (recommended) | **tar.gz** |
| --- | --- | --- |
| Install | none — `chmod +x` and run | extract anywhere |
| File | one `Glacier-<ver>-linux-x86_64.AppImage` | a folder of files |
| User data / settings | always in your profile (the mount is read-only) | **portable** next to the binary if writable, else the profile fallback |
| Bundled example scripts | in the profile `scripts/` | beside the binary in `scripts/` |
| Best for | quick single-file download, USB, no-install | a portable working copy you fully own |

**AppImage** — the whole app in one executable file:

```
chmod +x Glacier-<ver>-linux-x86_64.AppImage
./Glacier-<ver>-linux-x86_64.AppImage
```

Because an AppImage mounts **read-only**, GED can't write next to the binary, so your settings,
keymap, logs, cache, prefabs, and recovery data live in your profile (`~/.config/Glacier` and
`~/.local/share/Glacier`) — that is expected and correct. If your desktop can't run AppImages
directly (no FUSE), run it with `--appimage-extract-and-run`.

**tar.gz** — a portable folder you extract yourself:

```
tar xzf Glacier-<ver>-linux-x64.tar.gz
cd Glacier-linux-x64
chmod +x Glacier      # the Windows tar drops the +x bit; set it once
./Glacier
```

Extracted somewhere writable (e.g. your home directory), the tar.gz build keeps everything
**portable** next to the binary, exactly like the Windows build.

The editor, compiler, lightmapper, and viewport are all cross-platform. Ambient-sound previews
play through your system audio (`paplay`, falling back to `aplay`). Fonts come from
`fontconfig`; the code panels request a `monospace` fallback so they render everywhere.

### System prerequisites

The build is self-contained (the .NET 8 runtime and Skia/HarfBuzz/Assimp natives are
bundled), so you only need a handful of system libraries that every desktop already provides —
plus a system **OpenGL** (the viewport renders through a cross-platform OpenGL 3.3 host, and
Mesa's `llvmpipe` software GL works if you have no GPU driver). On a minimal/headless
Debian/Ubuntu, install:

```
sudo apt install libice6 libsm6 libx11-6 libxext6 libxrandr2 libxcursor1 \
                 libfontconfig1 fonts-dejavu-core libgl1-mesa-dri libglx-mesa0
```

(Fedora/Arch have the same libraries under their own package names.) A full desktop
environment already satisfies all of these. Wayland sessions work through XWayland; force X11
with `AVALONIA_PLATFORM=x11` if a native-Wayland quirk appears.

**Window-manager note:** many X11 window managers claim plain **Alt+drag** to move windows,
which is also GED's "temporarily invert snap" modifier during a transform drag. Hold
**Ctrl+Alt** during the drag instead (it reaches the viewport with Alt down and inverts the
snap), or rebind your WM's move modifier to Super.

## Playtesting under Wine

Red Faction and the Alpine Faction launcher are Windows executables, so on Linux GED launches
them through a **launch template** (Settings ▸ General ▸ *Playtest launch template*). The Linux
default is:

```
wine {exe} {args}
```

`{exe}` expands to the configured game executable and `{args}` to the `-level` / `-levelm`
playtest arguments. Any wrapper works — a Proton launch script, `protontricks-launch`, or a
custom shell script — as long as it ends up running the exe with the arguments. Blank = launch
the exe directly (the Windows default).

Recommended setup:

1. Install Red Faction + Alpine Faction into a Wine prefix (e.g. `~/.wine`), or use the
   Steam/Proton install.
2. In GED's first-run wizard (or Settings ▸ General), point **Red Faction install folder** at
   that install directory — e.g.
   `~/.wine/drive_c/Program Files (x86)/Steam/steamapps/common/Red Faction`. GED reads the VPPs
   directly from that path; because the whole staging flow goes through GED's VFS mount, a
   Wine-prefixed RF directory works unchanged.
3. Point **Game executable** at the Windows `AlpineFactionLauncher.exe` (or `RF.exe`) inside
   that prefix. GED stages the level into `<install>/user_maps/…` and launches it via the
   template, so Wine sees the exact on-disk layout the game expects.
4. Make sure `wine` is on your `PATH` (or replace the template's first token with an absolute
   path to your Wine/Proton runner).
