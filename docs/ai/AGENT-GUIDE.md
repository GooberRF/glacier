# Glacier agent guide

<!-- Conventions for this file: every ```lua block whose first line is "-- sample: <id>" is a
     complete script that runs against a fresh level (glacier-cli new) and is executed by the test
     suite, so the guide cannot drift from the API. Shell blocks are illustrative. -->

You are an AI agent helping a level designer build or edit **Red Faction (2001)** levels with
**Glacier**, a level editor. You work through Glacier's MCP tools when your host has them, or
through `glacier-cli`, a command-line tool: both run Lua scripts against a level, build it, check it
and draw it as an image. Examples show the `glacier-cli` command; section 3 maps each one to its MCP
tool. This guide gets you productive fast. The complete, always-current API reference is generated
from your build: run `glacier-cli api`, or call the `api_reference` tool (see [Reference](#12-reference)).

---

## 1. Your role

- **Assist; the designer decides.** Follow their direction. Ask when a request is ambiguous in a way
  that changes the layout (sizes, how many rooms, game mode). Make small, reviewable steps.
- **Leave the level easy to keep editing by hand.** Use simple box-like brushes, name every object
  you place, group multi-brush parts, tag what you make. Prefer the design kit (section 6), which
  does all of this for you. Never leave an opaque pile of unnamed geometry.
- **Verify before you claim.** Never say a level "works", "is sealed" or "is lit" without having run
  the check that proves it (section 9). Report what you checked and what you could not check (for
  example, behaviour that only playing the level shows).
- **Report what you did:** files written, what was created (the CLI lists UIDs and tags), checks and
  their results, open issues.
- **Ask before anything destructive.** Deleting the designer's content, overwriting their files or
  discarding unsaved editor changes requires their explicit go-ahead (`--allow-destructive`).

## 2. Setup

### Find glacier-cli

| Situation | Location |
|---|---|
| Windows install | `glacier-cli.exe` in the same folder as `Glacier.exe` |
| Linux | `glacier-cli` inside the tar.gz package (the AppImage starts only the editor) |
| Source checkout | `src/Ged.Cli/bin/<Debug or Release>/net8.0/glacier-cli(.exe)` after a build |

If it is not on `PATH`, ask the designer where Glacier is installed. Check it runs:
`glacier-cli --version`, and `glacier-cli help [command]` for any command's options.

### The Red Faction install

Textures, the entity/item/clutter class tables and textured renders come from a Red Faction install.
`glacier-cli` uses `--rf-dir <dir>`, else the `GED_RF_DIR` environment variable, else the install
configured in Glacier's settings (the `settings.cfg` beside `glacier-cli`, else the one in your profile);
`--verbose` prints which install was mounted and where it came from. Without one, levels still build and save, but
`catalog.classes("Item")` is empty, texture names cannot be checked, and renders use flat room colours.
Check with a dry run (section 7: `print(assets.available, catalog.available)`).

### Two ways to work

| | Visible (attach) | Headless (files) |
|---|---|---|
| How | add `--attach`; leave out the level argument to use the level open in the editor | pass a level file path |
| Designer | watches the viewport update live; Script Log shows your output | not involved until you hand over files |
| Undo | every `run` is **one** undo step labelled `CLI: <script file>`; `glacier-cli undo --attach` takes your latest run back | each command loads the file fresh; nothing persists unless you save |
| Saving | `glacier-cli save --attach` (or let the designer save) | `--save` (in place) or `--out <path>` on `run` / `build` / `light` |
| Files it may write | under the level's folder, the install's `user_maps`, the scripts library | the same, plus any path you name on the command line |

Attach mode needs Glacier running with *Settings ▸ General ▸ Allow command-line tools and AI agents
to control the open level* switched on (the default). `glacier-cli hello --attach` checks the
connection. Never edit a level file headless while the editor has the same file open: use `--attach`.

Everything a `run` creates is tagged `ai.script=<script file name>` and `ai.session=<id>` (one id per
`glacier-cli` invocation, printed in the run output), so the designer can find AI-made content with
`level.objects_tagged("ai.session")` / `level.brushes_tagged("ai.session")`. Tags your script sets win.

### Output, JSON and exit codes

Default output is short readable text. Add `--json` for `{"ok", "command", "result", "error"}`; a
failed script still returns `result` with `error = {kind, message, chunk, line, column, hint, display}`
and the full log. Use `--json` whenever you need numbers, positions or UIDs.

| Exit | Meaning |
|---|---|
| 0 | success |
| 1 | the command failed (script error, build errors, save aborted, undo refused, no GPU device for `render`) |
| 2 | usage error (bad flags) |
| 3 | `--attach`: no running editor accepted the connection |
| 4 | file error (missing, unreadable, unwritable, or outside the allowed folders) |
| 5 | `holes --fail-on-holes` found leaks |

### Commands

`info`, `new --out <rfl>`, `run <script.lua> [<rfl>]`, `build <rfl> [--sealed] [--light]`,
`light <rfl>` (= `build --sealed --light`), `holes <rfl> [--fail-on-holes]`, `lint <rfl>`,
`summary <rfl> [--detail brief|normal|full]`, `save <rfl> [--out <rfl>]`,
`render <rfl> --out <png> [view options]`, `open <rfl> --attach`, `undo --attach [--steps <n>]`,
`redo --attach [--steps <n>]`, `inspect <uid> <rfl>`, `query <rfl> [--kind|--class|--name|--tag|--region]`,
`catalog kinds|classes|describe|textures`, `ref add|view|list|camera|compare|scale` (match the level to
reference screenshots, section 6), `checkpoint <name> --attach`, `diff --since <name> --attach`,
`hello --attach`, `api [--markdown|--lua] [--member <name>]`, `help [command]`. Useful `run` flags:
`--build` / `--light` (compile, or compile and bake, after the run and before saving), `--dry-run`
(run, report, roll back), `--timeout <s>` (script time limit, default 30), `--label <text>` (appended
to the undo label), `--no-tag`, `--allow-destructive`.

### Using the MCP tools

When your host has Glacier's MCP server configured (`glacier-cli mcp`), use its typed tools instead
of shelling out to `glacier-cli`: they run the same commands and return the same JSON, plus images
you can see directly. Call `session_info` first; it reports the mode, the open level, the folders
you may write to and which safety classes are enabled.

**Connecting** (the designer does this once; a shell-capable agent can do it when asked). The
server is `glacier-cli mcp`, beside `Glacier.exe`. For Claude Code:
`claude mcp add glacier -- "<Glacier folder>\glacier-cli.exe" mcp` (on Linux,
`"<Glacier folder>/glacier-cli"`); add `--headless` or `--attach` after `mcp` to fix the mode, and
`--allow-write <dir>` for a working folder outside the usual places. Other hosts take the same
command in their MCP configuration, for example:

```json
{ "mcpServers": { "glacier": { "command": "C:\\Games\\Glacier\\glacier-cli.exe", "args": ["mcp"] } } }
```

`glacier-cli help mcp` lists every option. The host starts the server itself; nothing listens on a
network port.

**Modes.** The person who configured the server chose one; you cannot switch it from a tool.

| Mode | Started with | What the tools act on |
|---|---|---|
| Headless | `glacier-cli mcp --headless` | A window-less session with one level at a time and none at start: call `open_level` (a `.rfl` path) or `new_level`. The level persists across calls until you open another; nothing is written until `save_level`. |
| Attached | `glacier-cli mcp --attach` | The level open in the running editor, while the designer watches. `open_level` / `new_level` replace the editor's level. With no editor running, every tool answers `editor_unavailable` and retries on the next call. |
| Default | `glacier-cli mcp` | Attached when an editor answered at startup, headless otherwise; fixed until the server restarts. |

**Permissions come from how the server was started, never from a tool call; do not ask a tool for
more.** No tool has an argument that widens them. `--read-only` hides every tool that changes the
level. Destructive actions (discarding unsaved changes with `open_level` / `new_level`, overwriting
another existing file, a script that deletes, or calls `ops.save` / `ops.package`) are refused unless
the server was started with `--allow-destructive`; attached, the designer must also switch on
*Settings ▸ General ▸ Allow destructive AI actions* in the editor. Both are off by default. When a
destructive step is refused, tell the designer what you wanted to do and let them decide.

**Undo.** Every committed `run_lua` is exactly one undo step labelled `AI: <name>` (`AI: <name> —
<label>` with a `label`), so give each script a meaningful `name`. The designer can undo any of
them with Ctrl+Z; the `undo` tool takes back only automation steps (`AI:` or `CLI:`) and refuses
when the designer edited after you. `redo` re-applies only steps this session undid. Read tools
never touch the undo history, the unsaved-changes flag or the compiled geometry.

**`build` is sealed here** (the CLI defaults to a preview build), because your next step is almost
always `check_holes` and `lint`.

**Renders run on a copy.** `render_plan` and `render_view` draw a private copy of the level in the
server process: they never build or change the level you are working on, and never run inside the
editor. A render that needs fresher geometry makes a preview build of the copy and says so in
`notes`. The result is the mapping JSON (pixel ↔ world, legend, objects; section 4) followed by
the image (1024 px by default, at most the server's `--max-image`, up to 1568; 2 MB at most, else it
is re-rendered smaller). The PNG is also written to the server's scratch folder (`path`).

**One connection to the editor.** While attached, the server holds the editor's only automation
connection, so `glacier-cli --attach` commands cannot connect until the server stops. Use the tools
instead. If the editor is busy (its own build, a dialog), a call waits and then answers `busy`;
retry when it is free. If the editor restarts, the next call reconnects and its result says the open
level may have changed.

**Time limits and output size.**

- One call runs at a time; calls queue in arrival order, and one that waits more than 60 s answers
  `busy`.
- `run_lua`: 30 s of Lua time by default (`timeout_s`, up to 300 s); `build` and `check_holes`:
  15 minutes; renders: 120 s; every other tool: 60 s. A call past its limit, or cancelled by your
  host, answers `canceled` and leaves the level unchanged.
- Results are kept under about 24,000 characters. Long lists come back with `truncated`, `total`,
  `returned` and a `hint` naming the argument that narrows them (`query` `limit` / `offset`,
  `lint` `min_severity`, `level_summary` `detail`); `max_chars` raises the budget on list-like tools
  (up to 60,000). A `run_lua` log keeps its first 40 and last 160 lines; `check_holes` lists the
  first 100 positions; `lint` the first 100 findings, errors first.

Each tool matches a `glacier-cli` verb:

<!-- tools:begin -->
| MCP tool | glacier-cli | Changes the level? | What it does |
|---|---|---|---|
| `session_info` | - | no | The session: mode, version, open level, write folders, enabled safety classes. |
| `open_level` | `open` | yes | Open a .rfl level file as the session's document. |
| `new_level` | `new` | yes | Start a new level from the File > New template. |
| `save_level` | `save` | yes | Save the level (Alpine v306) with the editor's save rules. |
| `undo` | `undo` | yes | Undo the latest automation run(s). |
| `redo` | `redo` | yes | Redo steps this session undid. |
| `checkpoint` | `checkpoint` | no | Remember the level's structure under a name (for diff_since). |
| `diff_since` | `diff` | no | What changed since a checkpoint. |
| `run_lua` | `run` | yes | Run a Lua script against the level as one undo step. |
| `level_summary` | `summary` | no | Structured level summary: bounds, brushes, objects, links, rooms, lint counts. |
| `inspect` | `inspect` | no | Everything about one object or brush. |
| `query` | `query` | no | Find objects or brushes by kind, class, name, tag or region. |
| `render_plan` | `render` | no | Top-down floor plan of one storey (image + pixel-to-world mapping). |
| `render_view` | `render` | no | Orthographic, isometric or free-camera view (image + mapping). |
| `reference_view` | `ref` | no | Reference pictures: a concept image or a stored reference with a grid, or the list of references. |
| `reference_edit` | `ref` | yes | Register a reference crop (add) or set its matched camera and marks (camera). |
| `reference_compare` | `ref` | no | Render the level from a reference's camera beside the reference: one comparison sheet plus cues. |
| `build` | `build` | yes | Compile the level's geometry (sealed by default), optionally bake lighting. |
| `check_holes` | `holes` | no | Leak check on a sealed build (positions of open edges). |
| `lint` | `lint` | no | Level linter: links, spawns, budgets, leaks, missing assets. |
| `norms` | `norms` | no | Corpus norms: typical level proportions (doorways, corridors, headroom, light density). |
| `compare_to_norms` | `norms` | no | Compare the open level with the corpus norms (below / within / above). |
| `spatial_query` | `spatial` | no | Spatial queries and design measurements. |
| `align_textures` | `run` | yes | Line textures up like a mapper (level.uv_align), or report UV problems. |
| `terrain` | `run` | yes | Create, generate, edit, sculpt, query or convert a terrain. |
| `grade_level` | `grade` | no | Design grader: verdict, fixes and locations. |
| `list_classes` | `catalog` | no | Class names of a placeable kind (items, entities, clutter, events). |
| `describe_class` | `catalog` | no | Fields, links and description of a kind or class. |
| `list_textures` | `catalog` | no | Texture names in the mounted RF install, with provenance. |
| `module_library` | `modules` | no | Detail/architecture module library: stats, best matches, or one module. |
| `harvest_modules` | `modules` | yes | Harvest modules from levels (read-only) into a library folder. |
| `api_reference` | `api` | no | The Lua scripting reference: one global, type or member, or the index. |
<!-- tools:end -->

**Resources and prompts.** The server also offers read-only resources (this guide, the conventions,
the Lua reference and the design kit, generated from the running build) and prompts that start a
task with the right steps. The whole guide is `glacier://guide`, but it is longer than some hosts
return in one read: read `glacier://guide/index` (the section list with sizes) and then the
sections themselves, `glacier://guide/<section>` (at least 1-3 and 9 before your first edit). Read
`glacier://api/lua/<name>` (or `api_reference`) for any name you are unsure of.

<!-- mcp-resources:begin -->
| Resource | What it holds |
|---|---|
| `glacier://guide/index` | The agent guide's sections with their numbers, titles and sizes. Start here: read the sections you need with glacier://guide/<section> (1-3 and 9 before your first edit), each small enough for one read. |
| `glacier://guide` | How to build and edit Red Faction levels with Glacier, as one document: the build-look-check loop, the world model, the design kit, discovery, editing existing levels, the verification checklist, pitfalls and a worked example. It is long; a client that cuts long reads short should read glacier://guide/index and the sections instead. |
| `glacier://conventions` | Units, axes, rotation, CSG time order, texture scale and the other rules every script follows (the same text as ged.conventions). |
| `glacier://api/lua` | One line per global, type and design-kit function. Read one entry with glacier://api/lua/<name> (e.g. level.brush, kit.room). |
| `glacier://api/stub` | The scripting API as an annotated Lua stub (signatures and types of every global and member). |
| `glacier://kit` | Every design-kit function (local kit = ged.import("kit")) with its option keys, types, defaults and meaning. |
| `glacier://kit/source` | The design kit's Lua source as shipped with this build (scripts/lib/kit.lua, then each kit_* module it installs functions from): read it to see exactly what a kit function builds. |
| `glacier://reference/scale` | Red Faction dimensions measured on the stock levels (door panel, doorway, corridor, room height, stair step) plus player, eye and jump heights, with ranges and sources: count them in a reference screenshot to read its scale. |
| `glacier://norms` | The corpus norms built into Glacier: every level metric (doorway and corridor sizes, headroom, light density, texture tiling, ...) with unit, reliability, use and published buckets. Read one metric with glacier://norms/<metric> (e.g. opening.width). |
| `glacier://level/current/summary` | A brief structured summary of the level open in this session (the level_summary tool with detail brief). Read it again after a change; it is not pushed. |
| `glacier://guide/{section}` | One section of the agent guide by number (glacier://guide/3 is the core loop, 9 the checklist); a long section is split into lettered parts (6a, 6b). glacier://guide/index lists them all. |
| `glacier://api/lua/{member}` | One global, type or member of the Lua scripting API, or a design-kit function: level, brush, level.brush, brush:set_pos, kit.room. |
| `glacier://norms/{metric}` | One metric of the corpus norms with its percentiles (p5, p25, p50, p75, p95, n) in every published bucket (exemplar, overall, dm, ctf, sp, ...). |

| Prompt | Arguments | What it does |
|---|---|---|
| `blockout_from_brief` | `brief`, `gametype` (optional) | Plan, then block out a new level with the design kit from a written brief, verify it, and report what the designer should refine. |
| `audit_level` | `focus` (optional) | Read-only review of the open level: summary, plan renders of every storey, holes and lint; findings with UIDs and positions, no edits. |
| `extend_level` | `goal` | Add to the open level: checkpoint, study the surroundings, build the addition with the design kit, diff to confirm, verify. |
| `fix_leaks` | - | Find the open level's holes with check_holes, render around them, make the smallest repair, and re-check until it is watertight. |
<!-- mcp-resources:end -->

**Saving always compiles.** `save`, and `run` / `build` with `--save` or `--out`, first make a sealed
(unlit) geometry build when the compiled geometry is missing or out of date, because Red Faction
crashes loading a level with brushes and no compiled geometry. Lighting is baked only by `light` (or
`--light`). `--no-build` skips the build and warns; do not use it for a level that will be played.
Saves always write the current Alpine RFL version (306), so a level opened at an older version is
upgraded when it is saved (a new level already reports 306); the save result's `rflVersion` is the
version written. A level from a newer Alpine Faction opens read-only (`info` reports `readOnly` and a
note) and every save of it is refused.

## 3. The core loop

Generate, look, measure, fix. Repeat until the checks pass.

1. **Write a Lua script** (sections 5-8). Make it deterministic and return a one-line summary string.
2. **Run it.** Headless, regenerate the whole level each iteration so re-runs never stack:
   `glacier-cli run arena.lua --out work/arena.rfl` (no level argument = a new level from the File ▸
   New template; a path you name may be overwritten; the save compiles sealed geometry first). To
   change an existing level, read the original and write a copy:
   `glacier-cli run edit.lua their.rfl --out work/try.rfl`.
3. **Look.** `glacier-cli render work/arena.rfl --out work/plan.png`, then open the PNG and read it
   (section 4). If the geometry is missing or stale the render makes a quick preview build for the
   image only; nothing is saved.
4. **Measure.** `glacier-cli summary work/arena.rfl --json` for exact counts, positions, links, tags
   and rooms; `glacier-cli holes work/arena.rfl` for leaks.
5. **Fix** the script and go back to 2.
6. **Finish**, in this order: a sealed build (the `--out` save above already made one), then
   `glacier-cli holes work/arena.rfl --fail-on-holes` until it exits 0 (fix the script and rerun 2
   for any leak), `glacier-cli lint work/arena.rfl`, and only then bake and save:
   `glacier-cli light work/arena.rfl --save` (sealed geometry + lightmaps, saved). Then the
   checklist in section 9.

The order is always: **sealed build, check holes, fix, bake light, save.** Baking is the slow step,
so it comes last, on geometry you have already shown is watertight. `lint` reports an error for a
level that has no compiled geometry, and skips the leak check on an unsealed preview.

**The same loop with the MCP tools.** Each `glacier-cli` command has a tool (the full list is in
section 2); the tools act on the session's open level, so there is no level path to pass:

| Step | glacier-cli | MCP tool |
|---|---|---|
| Start a level | `new --out <rfl>` / `open <rfl>` | `new_level` (optional `out`) / `open_level{path}` |
| Run a script | `run <script.lua> [<rfl>] --out <rfl>` | `run_lua{code, name}` (then `save_level{out}`); `dry_run = true` for `--dry-run` |
| Build | `build <rfl> --sealed` | `build` (sealed by default) |
| Check holes | `holes <rfl> --fail-on-holes` (exit 5 on leaks) | `check_holes`, then read `watertight` (and `count`, `positions`) |
| Lint | `lint <rfl>` | `lint` |
| Bake light | `light <rfl>` (= `build --sealed --light`) | `build{light = true}`, or `run_lua{build = "lit"}` |
| Save | `save <rfl> [--out <rfl>]`, or `--save` / `--out` on `run` / `build` / `light` | `save_level` (optional `out`) |
| Look | `render <rfl> --out <png>` (plan) / `--view …` | `render_plan` / `render_view` (the image comes back with the result) |
| Measure | `summary <rfl> --json` | `level_summary` |
| Find, inspect | `query`, `inspect <uid>` | `query`, `inspect` |
| Undo a run | `undo --attach` | `undo` |

With the tools, the finish is: `build`, `check_holes` until `watertight` is true, `lint`,
`build{light = true}`, `save_level`, then the section 9 checklist.

**Visible mode** runs the same loop against the open level. Because every run adds to the level, do
your trial-and-error headless on a scratch file, then apply the finished script once with `--attach`
so the designer gets one clean, undoable step:

```powershell
glacier-cli hello --attach
glacier-cli run arena.lua --attach                  # one undo step: "CLI: arena.lua"
glacier-cli render --attach --out work/live.png     # draws the open level, unsaved edits included
glacier-cli holes --attach
glacier-cli light --attach
glacier-cli save --attach                           # or ask the designer to save
```

If a visible run went wrong, `glacier-cli undo --attach` takes it back (`--steps <n>` for several).
It only ever undoes command-line runs: when the designer has edited since, it refuses and names their
step, so tell the designer instead (they can press Ctrl+Z). Use `--dry-run` first when you are unsure
what a script will change.

**`summary` or `render`?** Use `summary --json` for facts (counts, UIDs, positions, links, room
graph, lint totals) and to check your own changes. Use `render` for layout: whether rooms connect,
whether things sit inside walls, spacing, symmetry. Use both before you hand back.

## 4. Reading a plan render

`render` draws a top-down slice of one storey by default (`--plan`). Look at the PNG every time.

- **Axes.** Image right is **+X**, image up is **+Z** (so "north" in a plan is +Z). Grid lines are
  labelled `x=…` along the top and `z=…` down the left. A scale bar is bottom-left, an axis gizmo
  bottom-right.
- **Legend** (top-left): the first line is `PLAN y=<floor> (<bottom>..<cut>)`, the second the axes
  and metres per pixel, the third the shading mode and geometry state; then every glyph with its
  count. Compare the counts with what you meant to place.
- **Shading.** Light areas are open floor at this storey. Dark hatching is solid (or outside). Dark
  lines are walls where they cross the cut height. Dimmed areas are a lower floor seen through an
  opening.
- **Glyphs.** Player Start green (labelled `START`), multiplayer spawns red, blue, purple (both
  teams) or white (a short tick shows which way each faces), items yellow diamonds, triggers cyan
  squares with their extent, events orange triangles, keyframes and movers magenta, lights pale
  stars, clutter grey squares, links arrows toward their target. Labels are `<UID> <script name>`.
- **Slices.** The slice keeps geometry from `y - below` (default 1 m) to `y + thickness` (default 2 m).
  `y` defaults to the floor under the Player Start. Render every storey: `--y 4` for a floor at 4 m.
- **What to look for:** rooms that should connect but are separated by hatching; glyphs on or inside
  dark areas (a light star on a dark pillar square is a light inside solid); spawns hugging walls;
  doors (a magenta line) not sitting in their doorway; missing lights (a dark room in `lit` mode).
- **Floor heights** (`--overlays …,heights`, plan and `--view top`): every walkable floor is tinted by its
  height over the texture (the legend's colour bar, in metres: blue lowest, then cyan, green, yellow, red
  highest). Floor that climbs gradually (a ramp or slope) gets thin dark contour lines every 0.5 m (1 m when
  the level spans more than 24 m), white chevrons pointing uphill and a `y0 → y1 m` label; stairs get light
  tread lines, a white uphill arrow and the same label; an abrupt edge where walkable floor ends more than a
  step (0.6 m) above a lower floor, with no ramp or stairs joining them, is a heavy violet line with ticks on
  its LOW side and the drop (`↓ 3.5 m`); `+y` labels give each flat floor's height. Steps you can walk up are
  never marked. `--mode heights` draws the same with the colour alone (other surfaces grey): the clean image
  `ref.heights` reads back. `--json` lists what was marked under `heights`: `range` (the colour bar), `ramps`
  and `stairs` (`low`, `high`, `foot`, `top`, `climb` = uphill `{dx, dz}`, `run`, `width`), `drops` (`points`
  `{x, z}`, `high`, `low`, `drop`) and `flats` (`y`, `area`, `at`).

**Pixels and world.** `render --json` gives `worldPerPixel`, `origin` (the world point at the image's
top-left corner), `pixelAxes` and a `mapping` sentence. For a plan:
`x = origin[0] + (px + 0.5) * worldPerPixel` and `z = origin[2] - (py + 0.5) * worldPerPixel`.
`result.objects` lists every glyph's `uid`, `kind`, `pixel` and world `pos`, so you never need to
guess which glyph is which. Other views: `--view top|bottom|front|back|left|right|iso`,
`--camera x,y,z --look x,y,z` (with `--hfov 90` for a horizontal field of view and `--eye-height eye` to stand
1.7 m above the floor under the camera);
framing with `--region`, `--room <index>` or `--uid <uid>`; `--overlays …,holes` marks leaks.

**Clean views for review.** Iso and camera views are clean by default: no editor icons for lights,
spawns, events, triggers or keyframes (`--icons` adds them; MCP: `overlays = "icons"`), with clutter and
items drawn as their game meshes when an RF install is mounted. Show-sky faces show the level's sky room as
the game draws it (from its centre, turned with the camera; a plain sky colour without a sky room) and the
result says which (`sky`). The sky room is never framed or drawn as part of the level: `--view iso`
frames the level's own geometry, and with `--region` shows only the region. A plan of a region frames
the part of it that has geometry in the slice. Without `--size`, plans and the other orthographic views
take the proportions of what they frame, and the legend goes in a corner the level leaves empty, so leave
`--size` off a plan unless you need an exact image size.

## 5. World model essentials

- **Units are metres** (degrees, seconds; colours 0-255). Axes are **left-handed: +X right, +Y up,
  +Z forward**. Yaw 0 faces +Z; positive yaw turns toward +X (`yaw = 90` faces +X).
- **The world starts solid.** A room is an **air** brush carved out of solid. A solid brush adds
  geometry (a pillar, a platform, a step).
- **CSG is strictly time-ordered.** Brushes apply in creation order: later air carves earlier solid,
  later solid fills earlier air. New brushes go at the end of time, so **create the air a solid stands
  in before the solid**.
- **Touching air merges.** Two air brushes that touch become one space. Keep at least 1 m of solid
  between rooms that should stay separate; join them on purpose with a doorway or corridor.
- **Brush `pos` is the centre** of the brush and `size = {w, h, d}` its full extents. The kit is
  different: **kit `pos` is a point on the floor** and `size` is the clear interior (section 6).
- **Detail brushes** (`detail = true`) do not split the level into rooms; use them for props and
  decoration inside a room. Structure (walls, floors, ceilings) stays non-detail.
- **Portals** split the level into rooms for visibility. They are opt-in; a small level plays fine
  without them. Add one only where asked (`kit.doorway{portal = true}`), and only between two
  genuinely separate rooms. An air portal brush makes portals only where it **cuts through solid**:
  the two walls it opens become the portals and the opening between them a small room of its own.
  `kit.doorway{portal = true}` makes exactly that (the doorway itself is the portal brush); a portal
  slab placed inside an opening that is already carved builds nothing (lint `PortalOpensNothing`).
- **Decals crash on long faces.** The game and the editor clip a decal to every compiled face its box
  touches in fixed buffers; a face with more than 19 vertex records (big floors, walls and ceilings
  collect extra points where other pieces meet their edges) crashes both while the level loads. Keep
  hand-placed decals small and on short faces, build, and lint: `decalOnComplexFace` names each one to
  move or delete. `kit.dress` already checks its own.
- **Movers** (doors, lifts) are brushes plus keyframes. The level stores a copy of each mover brush;
  when a member brush is edited (by your script or by hand in the editor), Glacier copies the edit over
  before `ops.build` / `light` / `save` / `package`, when the run ends, and whenever the editor or
  `glacier-cli` builds or saves (`mover:sync()` does it at any point).
- **Outdoors:** a face flagged `show_sky` shows the sky, which is drawn from a separate small sealed
  air box holding a Sky Room effect. `kit.sky_room{open = {room}}` builds that box and flags the
  room's ceiling; do not just leave a room open to the void.
- **Facing:** objects face along their Forward axis (`yaw`, `look_at`); a corona aims along its Up
  axis.
- **A playable level needs:** sealed geometry with zero holes, a Player Start standing in open space
  (a new level has one at (0, 1, 0); move it with `level.player_start{…}`), lights, and for
  multiplayer `level.props` `multiplayer = true` plus multiplayer respawn points (team games need red
  and blue ones) and items.

The raw API, without the kit:

```lua
-- sample: first-room
-- Carve a 12 x 4 x 12 m room whose floor is y = 0: an air box centred at y = 2.
local room = level.brush{ shape = "box", pos = {0, 2, 0}, size = {12, 4, 12}, air = true,
                          textures = { floor = "cem_dark_floor01.tga", wall = "cem_bunker04.tga", ceiling = "cem_cementflr01.tga" },
                          tags = { area = "first_room" } }
-- A solid crate standing in that room: created AFTER the air it sits in.
local crate = level.brush{ shape = "box", pos = {3, 0.5, 3}, size = {1, 1, 1}, detail = true,
                           texture = "mtl_conveyor_wall01.tga" }
crate:tag("area", "first_room")
level.place{ kind = "light", name = "first_room_light", pos = {0, 3, 0}, props = { size_range = 12 } }
level.player_start{ pos = {0, 1, -4}, yaw = 0 }
return "first room: holes = " .. ops.holes().count
```

## 6. The design kit

`local kit = ged.import("kit")` gives level-building functions that already follow every rule above:
floor-anchored, rooms that meet openings exactly (watertight, grid-clean), brushes tagged `kit`,
`kit.name` and `kit.part`, named objects, grouped multi-brush parts, textures mapped at the
texel density of RED's own levels (`kit.defaults.ppm = 64`) and lined up like a mapper's
(`kit.defaults.uv_align`; "Texturing" below). Each function takes one table and rejects unknown keys with the nearest valid name.
**Always pass a unique `name`**: it names the tags, groups and objects the designer will see. A brush
you make yourself takes one too: `level.brush{name = "plinth", ...}` writes the same `kit.name` tag, so
`kit.dress`, `kit.items` and `kit.spawn_points` accept that name as a room.
`kit.help()` (or `ged.describe("kit").text`, or `glacier-cli api --member kit.room`) lists every
function with its options, types and defaults. **For a whole level, start with a layout graph**
(`kit.layout`, "Layout first" and "Layout graphs" at the end of this section) and build rooms by hand only for the parts
you add to it.

| Function | Makes |
|---|---|
| `kit.room{name, pos, size}` | one air box; returns `{brush, min, max, center, floor_y}` |
| `kit.corridor{name, a, b, width?, height?, offset?}` or `{name, path = {p1, p2, …}}` | air joining two records across their gap, or along an axis-aligned floor path with bends |
| `kit.doorway{name, a, b, width?, height?, portal?}` | a door-sized opening through the wall between two rooms (default 3 m wide, 4 m high) |
| `kit.stairs{name, pos, dir, rise, width?, riser?, tread?, carve?, purpose?}` | solid steps from floor point `pos` climbing toward `dir` (`"+x"`, `"-x"`, `"+z"`, `"-z"`), with an invisible collision ramp over them so players walk up smoothly; refused when the flight serves no route (see "Placing with purpose") |
| `kit.ramp{name, pos, dir, rise, run?, width?, carve?}` | one solid wedge (default slope 1:2, max 45 degrees) |
| `kit.platform{name, pos, size = {w, d}, height, thickness?, supports?}` | a raised walkable block, or a slab (with legs) |
| `kit.pillar_row{name, from, to?, count, height, size?, shape?}` | evenly spaced box or cylinder pillars |
| `kit.door{name, opening, slide?, buttons? or trigger?, button_class?}` | a sliding mover door in a doorway; Use-key buttons (with a visible clutter panel at each when you give `button_class`) or an automatic trigger |
| `kit.light_room{room, spacing?, color?, range?, height?, fixture?, theme?}` | a grid of named lights over a room (a row every 4 m along a corridor), each hanging just under a visible fixture (a stock light clutter when the game tables are loaded, else a small full-bright lamp) attached to the ceiling over it; a room open to the sky instead gets fewer, stronger lights on its edges like the stock outdoor maps (about 0.5 per 100 m²: lamps on the walls, a lamp post beside each doorway, posts inside only where floor would be over 18 m from every light and never on the way between two doorways, a short glow light at each lamp); decks, landings and lowered ceilings move every fixture with them, and a light never lands inside a solid |
| `kit.spawn_points{room? or rooms?, count, team?, margin?, checkpoint?}` | multiplayer spawns 1 m above the floor, spread over the rooms at least 8 m apart (8-15 m where they fit), beside cover or in alcoves, clear of doorways, facing the way out, chosen for low exposure and few spawns in sight within 30 m, measured exactly as the grader's G19 measures them (`report.measure` says on what); `checkpoint` makes a payload group that advances with the cart; returns the spawns as a list (with `report` beside them) that `object:link_many` and `level.create_group` take as it is |
| `kit.dress{room? or rooms?, theme?, density?, seed?}` | generative fill: detail props, cover, beams, wall pipes, stock clutter and decals by 8 m cells for a theme (`industrial`, `construction`, `storage`, `control`, `mine`, `canyon`), keeping doorways, stairs, spawns and items clear and the walkable floor within 2 %; props come in groups (a crate stack with barrels or clutter beside it), gather in corners and along walls and flank each doorway; a decal never touches a compiled face with more than 19 vertices (that crashes the game: `by_kind.decals_unsafe` counts the spots left out); cover goes only on measured lanes and spawn approaches, rocks only into organic spaces and against walls (see "Placing with purpose"); returns `count` (elements placed), `by_kind` (props, cover, breakers, lanes, overhead, clutter, decals, brushes), density and the walk change |
| `kit.items{gametype?, rooms?, density?, seed?, path?, hill?}` | the item economy, level-aware: about 1.6 items per 100 m² of the whole level's playable floor up to 4,500 m², fewer per area on bigger levels (1.2 at 10,000 m², 1.0 from about 17,000 m², as well-regarded big levels have), less the items already there, never 6 or more within 10 m of one another (`local_max` reports the worst), so calling it per zone adds up to one economy (`count` is the number placed, `by_category` the split, `level_per100` the level's density); weapons 7-10 m apart over the zones, ammo, health and armour on the flanks, powerups in contested spaces, an item near every spawn; CTF halves mirrored, payload supplies on the cart route's flanks |
| `kit.sky_room{name?, open = {rooms}}` | an outdoor sky: a sealed sky box away from the level plus `show_sky` on the open rooms' ceilings |

Defaults live in `kit.defaults` (edit them at the top of a script); hard limits in `kit.limits`:
clear width >= 1.8 m (player diameter), headroom >= 2 m, riser <= 1 m, slope <= 45 degrees. Respect
them: the kit refuses tighter openings unless you pass `allow_tight = true`, and a level that breaks
them is not walkable. Default stairs use 0.25 m risers and 0.5 m treads, so `rise` must be a multiple
of the riser. With `carve = true`, stairs and ramps first cut an air envelope, for climbing through
solid between storeys. Stairs climb *away* from `pos`: put `pos` at the bottom step, `dir` toward the
top.

Rooms, a doorway, a straight corridor and a corridor with bends:

```lua
-- sample: kit-rooms
local kit = ged.import("kit")
local west   = kit.room{ name = "west",   pos = {-12, 0, 0}, size = {10, 5, 10} }  -- x -17..-7, z -5..5
local middle = kit.room{ name = "middle", pos = {0, 0, 0},   size = {12, 5, 12} }  -- x -6..6: 1 m wall to west
local north  = kit.room{ name = "north",  pos = {0, 0, 16},  size = {10, 4, 8} }   -- z 12..20: 6 m gap to middle
kit.doorway{ name = "west_door", a = west, b = middle }                  -- 3 x 3 m through the 1 m wall
kit.corridor{ name = "north_hall", a = middle, b = north, width = 3 }    -- fills z 6..12
kit.corridor{ name = "west_loop", width = 3,                             -- an L from west's north wall to north
              path = { {-12, 0, 5}, {-12, 0, 16}, {-5, 0, 16} } }
return "kit-rooms: holes = " .. ops.holes().count
```

Changes of level: stairs and a ramp up to two platforms, plus a pillar row:

```lua
-- sample: kit-levels
local kit = ged.import("kit")
local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {24, 8, 16} }      -- x -12..12, z -8..8
-- Solids after the air they stand in.
kit.platform{ name = "deck", pos = {-8, 0, 4}, size = {6, 6}, height = 2 }        -- x -11..-5, z 1..7, top 2
kit.stairs{ name = "deck_stairs", pos = {-8, 0, -3}, dir = "+z", rise = 2, width = 2 }  -- 8 steps, z -3..1
kit.platform{ name = "perch", pos = {8, 0, 4}, size = {6, 6}, height = 1.5, thickness = 0.5, supports = true }
kit.ramp{ name = "perch_ramp", pos = {8, 0, -2}, dir = "+z", rise = 1.5, width = 3 }    -- run 3, z -2..1
kit.pillar_row{ name = "hall_pillars", from = {-4, 0, -5}, to = {4, 0, -5}, count = 3, height = 8 }
return "kit-levels: holes = " .. ops.holes().count
```

A door between two rooms, opened by Use-key buttons on both sides:

```lua
-- sample: kit-door
local kit = ged.import("kit")
local lobby = kit.room{ name = "lobby", pos = {0, 0, 0},  size = {10, 4, 10} }   -- x -5..5
local store = kit.room{ name = "store", pos = {11, 0, 0}, size = {10, 4, 10} }   -- x 6..16: wall x 5..6
local way = kit.doorway{ name = "store_way", a = lobby, b = store }             -- z -1.5..1.5, 3 m high
local door = kit.door{ name = "store_door", opening = way,
                       buttons = { {4.5, 1.5, 2.5}, {6.5, 1.5, 2.5} } }         -- one each side, beside the doorway
-- Without buttons, kit.door places an automatic proximity trigger instead (trigger = "auto").
return string.format("%s: %d keyframes, %d buttons", door.mover.name, #door.mover.keyframes, #door.triggers)
```

An outdoor courtyard under a sky:

```lua
-- sample: kit-outdoor
local kit = ged.import("kit")
local yard = kit.room{ name = "courtyard", pos = {0, 0, 0}, size = {16, 8, 16} }
kit.sky_room{ name = "sky", open = { yard } }   -- sealed sky box far outside; the courtyard ceiling shows it
kit.light_room{ room = yard }
return "courtyard: holes = " .. ops.holes().count
```

Lights, spawns, items and the Player Start:

```lua
-- sample: kit-populate
local kit = ged.import("kit")
level.props:set_many{ level_name = "Populate Demo", multiplayer = true }
local arena = kit.room{ name = "arena", pos = {0, 0, 0}, size = {16, 6, 16} }
local lights = kit.light_room{ room = arena }                  -- 2 x 2 grid, each named arena_light<n>
local spawns = kit.spawn_points{ room = arena, count = 6 }     -- both teams; team = "red" or "blue" for one
level.place{ kind = "item", class = "Shotgun",     name = "arena_shotgun", pos = {0, kit.defaults.item_height, 4} }
level.place{ kind = "item", class = "Medical Kit", name = "arena_medkit",  pos = {-3, kit.defaults.item_height, -2} }
level.player_start{ pos = {0, 1, -5}, yaw = 0 }
return string.format("%d lights, %d spawns", #lights, #spawns)
```

The kit is Lua: read its source (`scripts/lib/kit.lua` beside Glacier, or in a source checkout) when
you need the exact behaviour, and print `kit.defaults` / `kit.limits` to see the values in effect.
Doors: the editor-side setup is tested, but how a door feels in the game (blocking, re-triggering)
is only confirmed by playing the level; say so when you hand over a level with doors.

### Organic spaces: caves, canyons and rocks

For natural spaces, the kit has generators that follow a **floor path on any heading** and build rough
rock out of many convex brushes that meet exactly, so the result compiles watertight and stays
hand-editable. Use kit rooms, corridors and stairs for architecture on the grid; use these for caves,
canyons, tunnels, rock dressing and pits. The `geo` builders underneath (`geo.tube`, `geo.passage`,
`geo.rock_row`, `geo.scatter`, `geo.pit`) take the same geometry options without the kit's names,
groups, walkway defaults and sky; reach for them only when you need that control.

| Function | Makes | Recipe |
|---|---|---|
| `kit.cave` | a rough cave tube: structural air, a 1.8 x 2 m walkway along the path | `kit.cave{name = "c", path = {p1, p2, …}, seed = 1}`; `radius` (3), `height` (4.5), `radii` / `heights` per point, `roughness` (0.2), `sides` (8, 6 to 14) |
| `kit.canyon` | an open canyon: leaning walls, one common rim, show-sky tops (plus a sky room named `<name>_sky` if the level has none) | `kit.canyon{name = "k", path = {…}, width = 12, height = 16}`; `widths` per point for basins and narrows, `lean` (0.15) |
| `kit.passage` | a clean, level corridor of exact prisms on any heading (no jitter) | `kit.passage{name = "p", path = {…}, width = 3, height = 3.5}`; turns up to 120 degrees per point |
| `kit.rock_wall` | a row of overlapping detail rocks; `flat_top = true` gives a walkable top; `structural = true` builds one sealed ridge instead | `kit.rock_wall{name = "r", from = a, to = b, height = 4, depth = 3, seed = 2}` |
| `kit.boulders` | detail boulders on a rectangle (`area`), a kit room's floor (`room`) or a tube's floor (`along`, kept off the walkway) | `kit.boulders{name = "b", along = cave, count = 20, size = {0.6, 2}, seed = 3}` |
| `kit.pit` | a rough pit going down from a level floor, narrowing to a flat bottom | `kit.pit{name = "pit", pos = p, radius = 3, depth = 4, seed = 4}`; `taper` (0.7) |
| `kit.light_path` | named lights along a cave, canyon or passage, each on a lamp on the wall (or a post at the walkway's edge where no wall is in reach), at most 4.5 m up | `kit.light_path{along = cave, spacing = 8}`; `fixture = false` for bare lights |

**Paths and climbs.** A path is a list of floor points (2 to 256). Points must be at least 2 m apart
horizontally, turn at most 90 degrees each (a passage: 120), and each leg may climb at most
45 degrees. A leg that changes height is **one smooth slope**, not steps; a climbing leg that turns
keeps a short level landing at each turn, so it needs a little more run than the bare slope (the
error names the leg). Floors are level across the tube, so you never need stairs or ramps inside a
cave; for a sheer drop use `kit.pit`, or `kit.stairs` between two passages (passages are always
level). `x`/`z` snap to 1/1024 m and `y` rounds to 1/16 m; read the actual values back from the
record's `stations`. A path that ends on its own start point is a closed loop.

**The walkway guarantee.** Along every cave and canyon path a 1.8 m wide, 2 m high walkway is
clear and floored (`allow_tight = true` on a cave drops the check). `kit.boulders{along = …}` keeps
rocks off it, so the station line (`rec.stations[i].pos`, floor `floor_y`) is a safe line for spawns,
items and lights: `pos = {s.pos.x, s.floor_y + 1, s.pos.z}`, `yaw = s.heading`.

**Joining spaces.** A tube end is closed rock. To open a cave, canyon or passage into a room, **start
or end its path at least 1 m inside the room, at the room's floor height**: the floors become one
plane and the walls cross cleanly. The room must be taller than the cave's crown
(`height x (1 + roughness)`, 5.4 m for the defaults) by at least 0.1 m so the mouth opens in the room's
wall rather than across its ceiling edge; `kit.cave` (and `kit.canyon`, whose rim must stay under the
room's ceiling) checks this before building and names the room height to use. A joined canyon's mouth
is judged by its width only (wider than 0.8 x the room's clear width warns); a cave's also by its crown. A passage end may instead stop exactly on an axis-aligned room wall
when its last leg is axis-aligned. A branch starts inside another tube, or on a pit's floor
(`y = pit.floor_y`), the same way.

**Never hand-overlap organic brushes.** The generators place neighbouring pieces so they **abut** on
identical, exactly flat faces; that is what keeps them watertight. Two rough brushes that overlap at a
shallow angle, or a vertex you move by hand, leave open edges in the sealed build. Change a
generated space by changing its options and running it again; roughen your own brushes with
`geo.jitter` (never per-vertex moves). Rock dressing is the exception: detail rocks may overlap
anything, because detail never seals or splits rooms.

**Detail or structural.** `kit.rock_wall` (default) and `kit.boulders` are **detail**: cheap, never
seal, may lean into a wall. `kit.rock_wall{structural = true}` is one structural ridge standing exactly
on the floor (it seals and splits rooms; on a floor whose height is not a multiple of 1/16 m it sinks
just below it instead); keep it clear of walls it would graze. Every cave, canyon and pit is structural
air (a passage too, unless `detail = true`). Order: the organic air first, then the rocks that stand in
it; a pit after the floor it opens from, and its `pos` on a **level** floor (a room, or a level stretch
of a tube), never on a slope. A pit's top ring stands a little above that floor, so its mouth opens
through the floor at any height. On a sloped floor `kit.boulders` stands each rock on the lowest floor
under it, so its downhill side meets the floor.

**Stay near the origin.** The compiler fits each face plane in single precision on world
coordinates, so accuracy drops with distance. Keep large organic builds within about 200 m of the
origin and centre long canyons on it. The generators re-draw any piece whose faces the compiler would
fit badly; when they cannot, you get the "faces the compiler can fit" error below.

**Seeds and determinism.** Every organic function takes `seed` (an integer, default 1): the same
options and seed give the same brushes, run after run. The kit never reads `rng`, so `rng.set_seed`
does not change them. Give each piece its own seed (`SEED`, `SEED + 1`, …); a new seed gives new
shapes in the same layout, so re-check holes after changing one.

**Retry a seed.** A generator can refuse an unlucky seed (`could not place segment …`). Search for a
working one inside the script with `ged.try(fn, ...)`: it calls `fn(...)` and returns `true` and fn's
results, or, when fn fails, undoes everything fn changed and returns `false` and the error
(`err.message`, `err.hint`; `tostring(err)` gives both). The level is then exactly as before the
attempt, so the next seed starts clean. Only retry errors a seed can fix: a refusal whose hint says
to move or reshape the path fails for every seed. Time limits and Stop are never caught.

```lua
-- sample: retry-seed
local kit = ged.import("kit")
local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {12, 6, 12} }   -- x -6..6, z -6..6
local cave, last
for seed = 1, 8 do
  local ok, result = ged.try(kit.cave, { name = "north_cave", seed = seed,
                                         path = { {0, 0, 4}, {4, 0, 16}, {14, 0, 26} } })
  if ok then cave = result break end
  last = result
  print("seed " .. seed .. " refused: " .. tostring(result))
end
if not cave then ged.fail("no seed worked: " .. last.message, last.hint) end
kit.light_room{ room = hall }
kit.light_path{ along = cave, spacing = 8 }
return string.format("north_cave: seed %d, holes = %d", cave.seed, ops.holes().count)
```

**Records and tags.** `kit.cave` / `kit.canyon` / `kit.passage` return `{kind, name, brushes, group,
stations, climbs, min, max, floor_y, start, end, length, seed, tube}` (a canyon adds `top_y` and `sky`).
Each station is `{pos, floor_y, width, height, heading}`; each climb `{from, to, rise, run, heading,
width}`. `kit.rock_wall` / `kit.boulders` return `{brushes, group, count, min, max, seed}`; `kit.pit`
returns `{brushes, group, floor_y, center, radius, depth, min, max, seed}`. Every brush carries `kit`,
`kit.name` and `kit.part` (`seg001`…, `rock001`…, `ring1`…; passage prisms `seg1`…) plus `geo.seed`, and
each call's brushes are grouped under its name (a canyon's sky room is not in the group). Textures
default to `kit.defaults.rock_texture` / `rock_textures` (else the kit's defaults), mapped at the
kit's texel density like every other surface and folded across every facet at true length (`kit.defaults.ppm = 64`: a 256 px rock texture repeats every
4 m; don't enlarge rock tiles — 8 m tiles read blurred); look up stock rock textures with
`catalog.textures{filter = "rck_", stock = true}`.

A room, a cave that climbs to a second room, boulders, a pit and lights:

```lua
-- sample: kit-organic
local kit = ged.import("kit")
local west = kit.room{ name = "west", pos = {0, 0, 0},   size = {12, 6, 12} }   -- x -6..6, z -6..6
local east = kit.room{ name = "east", pos = {40, 2, 24}, size = {12, 6, 12} }   -- x 34..46, z 18..30, floor 2
-- The path starts and ends at least 1 m inside each room, at that room's floor height.
local cave = kit.cave{ name = "link", seed = 4,
                       path = { {3, 0, 0}, {16, 0, 4}, {28, 2, 18}, {37, 2, 24} } }  -- leg 2 climbs 2 m smoothly
-- After the air: detail boulders off the walkway, and a pit whose mouth lies in east's level floor.
kit.boulders{ name = "link_rocks", along = cave, count = 6, seed = 5 }
kit.pit{ name = "sump", pos = {42, 2, 21}, radius = 2, depth = 1.5, seed = 6 }
kit.light_path{ along = cave, spacing = 8 }
kit.light_room{ room = west }
kit.light_room{ room = east }
return string.format("link: %d segments, %.0f m, climbs %.1f m, holes = %d",
                     #cave.brushes, cave.length, cave.climbs[1].rise, ops.holes().count)
```

**Your own shapes.** `level.brush_from_points{points = …}` makes one convex brush from the hull of
4 to 256 points (snapped to 1/1024 m, unrotated, box-mapped; `level.brushes_from_points{hulls = …}`
makes hundreds in one call). `geo.jitter{amount = {0.3, 0, 0.3}}` roughens brushes sideways while
floors stay flat; `geo.twist`, `geo.bend`, `geo.taper` deform them and stay convex (with `pieces`, the
pieces meet on exact shared faces; a bend needs at least 0.5 degrees per piece); `geo.check(b)`
audits any brush (closed, outward, convex, planar).

```lua
-- sample: organic-shapes
local kit = ged.import("kit")
local yard = kit.room{ name = "rock_yard", pos = {0, 0, 0}, size = {16, 6, 16} }
-- A free convex rock: the hull of its points, standing on the floor near (4, 0, 4). Detail: it never
-- seals or splits rooms.
local rock = level.brush_from_points{ detail = true, tags = { area = "rock_yard" }, texture = "rck_canyon_rock03.tga",
  points = { {2, 0, 3}, {5.5, 0, 2.5}, {6, 0, 5}, {3, 0, 6}, {3.5, 2.5, 4}, {4.8, 2, 4.4} } }
geo.jitter{ brushes = rock, amount = {0.3, 0, 0.3}, seed = 3 }   -- sideways only: the base stays on the floor
local report = geo.check(rock)
assert(report.ok and report.convex, table.concat(report.issues, " "))
kit.light_room{ room = yard }
return string.format("rock: %d faces, holes = %d", report.face_count, ops.holes().count)
```

Errors from the organic functions name the path point, leg or segment and say what to change:

| Error (shortened) | Meaning and fix |
|---|---|
| `the path turns A° at point i` | more than 90 degrees (passage: 120) at one point: add a point to round the corner |
| `path points i and j are only D m apart` | legs must be at least 2 m long: remove or move a point |
| `the path climbs A° between points i and j` | steeper than 45 degrees: lengthen the leg, or use `kit.pit` / `kit.stairs` for a drop |
| `the leg … cannot climb within the 45° limit` | the level landings at its turns leave too little run for the climb: lengthen the leg, climb less, or straighten the turn |
| `the floor at path point i is W m wide; … under the 1.8 m walkway` | too narrow for the walkway at a bend: raise `radius` / `width` (or `floor_flatness`), or lower `roughness` |
| `the cave at path point i is H m high; … under the 2 m headroom` | the rough arch can come too low: raise `height` or lower `roughness` |
| `could not place segment N without a near-coplanar face` | a face, edge or corner would nearly (not exactly) meet an earlier brush, or two faces would fold almost flat: when the hint names an existing brush, move the path so it stays clear of it or meets it exactly; otherwise change `seed` or raise `roughness` |
| `… (its walls nearly line up with the walls of cave 'c' …)` | the new tube starts inside another one on the same line, so their walls run side by side a few millimetres apart; no seed fixes it: join it at an angle, or extend the existing tube (more path points, per-point `radii` / `heights`) instead |
| `room 'R' is H m high, but the cave's crown can reach C m …` | the cave (or canyon rim) would cross the room's ceiling edge: raise the room to the height the hint gives, or lower the cave's `height` / `roughness` |
| `could not place segment N with faces the compiler can fit` | too far from the origin for that facet: build closer to the origin, or change `seed` |
| `could not place segment N: its rings cross` | a turn too sharp for the station spacing: soften it, lengthen the legs beside it, or narrow the tube |
| `path point i is at y = …; a passage is level` | passages do not climb: use `kit.cave`, or `kit.stairs` / `kit.ramp` between two passages |
| `… the mitred corners of a W m passage cross over` | a passage leg too short for its turns: lengthen it, soften the turns, or narrow the passage |
| `only N of COUNT rocks fit` | lower `count` or `size`, or scatter over a larger area |
| `a B° bend in N pieces turns each piece by only …` | `geo.bend` pieces must fold by at least 0.5 degrees: use the piece count the hint gives, or bend more |
| `the N hulls hold W points² of hull-building work` | one `level.brushes_from_points` call is too heavy: use fewer points per hull (only the corners matter) or split the call |

A run that succeeds but leaks is still possible on an unlucky seed: always check `holes` on the
finished level, and if a generated space leaks, try another `seed` before anything else (inside a
script: pass `ged.try` a function that builds the space and calls `ged.fail` when
`ops.holes().is_watertight` is false, and loop over seeds as above). Two complete
examples ship in `scripts/examples`: `kit-canyon.lua` (a 240 m canyon centred on the origin) and
`kit-cave.lua` (a cave network with a climb, a passage, a pit and a loop).

### Texturing: palettes, alignment and the texture checks

Pick textures by zone with `kit.palette{theme, variant}` (floor and wall lists never share a texture) and give
rooms their wall kit with `kit.trim`. The kit maps every texture at `kit.defaults.ppm = 64` pixels per metre of its
real size, then **lines it up like a mapper** (`kit.defaults.uv_align = true`): each part as it is built, and the
whole layout again at the end of `kit.build_layout`. Wall textures stand on the floor line, wrap around corners,
and fit their height to whole repeats; ramps, stairs and rock map at true length with the texture folded across
every edge; nothing reads mirrored. Trims keep their fitted strips and stair treads start at the nosing. Beams,
posts, pilasters, rails and truss members in a girder or trim texture get it laid along their length with at most
one repeat across, as stock levels do; library modules already mapped that way keep their source mapping, so a final
`level.uv_align{}` no longer turns pilasters into zebra stripes.

Anything you build outside the kit (hand-placed `level.brush` set pieces, `level.brush_from_points` rock, edits
to an existing level) keeps whatever mapping it was given. **Run `level.uv_align{}` once after building and before
lighting** — for the whole level, or `level.uv_align{brushes = level.brushes_tagged("area", "rock_yard")}` for a
part (`selection = true` takes the designer's selection). It is one undo step and never runs on its own, so a
level you were asked to edit keeps its authored UVs unless you call it. Faces already fitted to whole tiles
(`brush:fit_faces`, crate sides) are kept.

Check with `level.uv_report{}` (MCP: `align_textures` with `report = true`): `walls.handedness_major` (exemplars 0.60-1.00, p50 0.98;
a plain box map gives 0.50), `seams.wall_corner.aligned` / `.continuous`, `seams.fold.*` (rock and lips),
`slope_stretched`, `walls.floor_line.tile` / `ceiling_line.tile`, and `issues` — the worst places with `pos`
(`mirrored`, `seam_*`, `stretched*`, `density`, `member_grain`, `member_repeats`), and `members` — on beams and posts
in a grained texture, `grain_along` (stock p50 0.98), `vertical_grain_along` (posts, pilasters, columns) and
`no_repeats_across` (reported, not gated: some stock columns stack a trim on purpose). The grader's G21 fails a level
whose walls read mirrored on about half their area and lists the same places, girder textures stacked across posts
among them; the fix is `level.uv_align{}`, a rebuild and a regrade.

### Proportions, spaces and set pieces

Good layouts are not uniform boxes. The kit carries the exemplar study's proportions
(`docs/internal/DESIGN-QUALITY-SPEC.md` §a): **corridors 4-5 m high, rooms under 100 m² 3.5-6 m, halls of
100-400 m² 6-9 m, atria and set pieces 12 m or more, and a hub at least twice the height of the corridors
that feed it.** `kit.proportion{area = …}` returns the height for a footprint; `kit.room{size = {w, nil, d}}`
(no height) uses it. Vary ceilings inside a space (exemplar headroom IQR/median 0.7-1.3) and put some walls
off the grid axes (exemplar off-axis wall share 0.08-0.35).

| Function | Makes |
|---|---|
| `kit.space{archetype, name, pos, size?, facing?}` | a whole space at exemplar scale; `kit.archetypes{}` lists them: hall, atrium, arena, ring, sunken_court, stair_hall, courtyard, canyon_bridge, cave_chamber, pit_room, control_room, storage; open-air platform, sky_bridge, open_deck; payload cart_road, overlook, gate, finale. Returns `parts` (connect corridors to `parts.room` etc.), `anchors` (door/road attach points) and `warnings` |
| `kit.shaped_room{name, pos, shape or footprint, size, chamfer?, ceiling?}` | a non-box room: rect, octagon, L, T, U, cross or any polygon; corners chamfered; flat, stepped, gable, shed, coffered or beamed ceiling |
| `kit.alcoves`, `kit.pilasters`, `kit.floor_section` | wall insets, piers every 4-6 m, raised or sunken floor sections of 0.5-1.5 m |
| `kit.opening{a, b, shape}` | a window above a sill (a sight link), an arch, a rough mouth or a door between two spaces; warns when it is wider than 0.8x a space's clear width (the spaces would merge) |
| `kit.beam`, `kit.truss`, `kit.bridge{from, to, style}`, `kit.landmark` | detail structure: beams, trusses, truss/arch/girder/plank bridges (landmark bridges span 12-27 m over 5-12 m drops; the bridge warns below that), 25-40 m outdoor landmarks |
| `kit.road{path, width?, height?, tunnels?}` | a wide graded road (the payload cart route), 8 m deep by default, with level pads at turns; a leg's grade is its rise over its sloped stretch (the leg less the turn pads, half the road width each side of a turn point), and the warning names both lengths |
| `kit.ground_at{x, z, y?}`, `kit.ground_region`, `kit.ground_provider` | the ground under a point, read through one provider: brush floors and the tops of Alpine terrains (kind `"terrain"`). Read floors this way instead of assuming a brush top is the floor |
| `kit.ground{area or min/max, mode?, explain?}` | brush or Alpine terrain ground for an open area (see "Ground: brush or terrain" below); `explain = true` only reports the choice and its inputs |

Caves and canyons warn when a mouth is wider than its room, or when most of the floor slopes;
`kit.organic_report{along = cave}` checks organic moderation (at least 30% level floor and one built element
- beam, truss, bridge, platform - per 100 m²).

```lua
-- sample: kit-spaces
local kit = ged.import("kit")
local hall = kit.space{ archetype = "hall", name = "hall", pos = {0, 0, 0} }          -- 24 x 16 m: x -12..12
local atrium = kit.space{ archetype = "atrium", name = "atrium", pos = {-33, 0, 0},    -- 28 x 28 m: x -47..-19
                          facing = "-x" }                                              -- its stair faces away
kit.corridor{ name = "hall_atrium", a = atrium.parts.room, b = hall.parts.room }      -- 4.5 m high: the atrium is 3x
local store = kit.shaped_room{ name = "store", pos = {0, 0, -24}, shape = "L", size = {20, 14}, chamfer = 1.5,
                               ceiling = "stepped", region = {-9, -6, 9, -1.5} }       -- x -10..10, z -31..-17
kit.corridor{ name = "store_hall", a = store, b = hall.parts.room, offset = -6, width = 3 }  -- the L's arm
kit.pilasters{ name = "store_piers", room = store, sides = {"-z"} }
local floor = kit.ground_at{ x = 0, z = -26 }                                          -- the store's floor
return string.format("spaces: floor %.1f, holes = %d", floor.y, ops.holes().count)
```

### Ground: brush or terrain

Alpine Faction terrain is an **option, not the outdoor default**. `kit.ground{…}` weighs it for one open area
and records why (`choice`, `reason`, and the `inputs` it used); pass `explain = true` to see the decision without
building anything.

- **Choose terrain** for large, open, natural ground: at least 400 m² and 16 m across, open to the sky, no
  overhang, on a level that already targets Direct3D 11 (`level.alpine:set("require_d3d11_setting", true)`, a
  D3D11-only lightmap setting, a terrain already in the level, or `d3d11 = true`). It is smooth, cheap to shape,
  blends painted layers on Direct3D 11 and, made geoable (`t:object():set("geoable", true)`), takes craters
  without a thick world brush (the kit's terrains are not geoable).
- **Keep brushes** for everything else: canyon walls, caves, passages and every built floor (decks, plazas,
  rooms) stay brushes. A terrain cannot seal, form rooms or portals, overhang, rotate, or look blended on the
  legacy renderers (they draw each cell's dominant layer, unlit by Alpine's terrain charts).
- `mode = "auto"` (the default) applies those rules; `"terrain"` builds one unless the area cannot take it (an
  error: roofed, a cave or canyon); `"brush"` keeps the brush floor.
- A kit terrain rests 1/16 m above the brush floor it covers, gets mounds from `seed` and `relief`, stays level at
  the edges, at `flat` places (doorways, landings, what stands on it) and around `holes` (openings cut through it),
  paints `paths` with the path layer and the crests with rock, and is always **enclosed in air**: an area not
  already inside an air volume gets a sky volume, so the level still seals. Air brushes do not cut terrain; cut
  openings with `holes`.
- In layouts, `kit.layout{…, ground = "terrain"}` (or `"auto"`) asks every open-air zone for terrain; only
  natural-ground zones (isles, gardens) take it, the rest keep their slabs with the reason in
  `report.zones[i].ground`. The graph is otherwise identical, so a seed keeps its layout.
- After building, the terrain is floor to every check: `kit.ground_at` reports `kind = "terrain"`, spawns stand
  1 m above its surface, the grader and `spatial.walk` sample its top, and the space under it is ground (a spawn
  there is reported by the spawn-height lint, `spatial.point_info` says `under_terrain`).

```lua
-- sample: kit-ground
local kit = ged.import("kit")
local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {40, 14, 32} }
kit.sky_room{ open = {yard} }
local why = kit.ground{ area = yard, explain = true }                 -- brush: no Direct3D 11 target yet
level.alpine:set("require_d3d11_setting", true)
local g = kit.ground{ area = yard, name = "yard_ground", seed = 2,   -- now terrain
                      paths = {{{-20, 0}, {0, 0}, {20, 6}}}, flat = {{-20, 0, 3}} }
local function stand(x, z) return {x, g.terrain:surface_at(x, z) + 1, z} end      -- 1 m above the terrain
level.place{ kind = "respawn", pos = stand(8, 6) }
level.player_start{ pos = stand(-8, 6), yaw = 0 }
return why.reason .. " / " .. g.reason
```

### Placing with purpose (roles)

Players notice when something stands there for no reason: a bridge over a gap that two staircases already
join, a lone rock in the middle of a concrete room. **Place nothing without a role.** The kit tags everything it
places with `kit.role` and `kit.purpose` (saved with the level; `kit.roles{}` lists them; MCP `query --tag
kit.role=cover` finds them):

| Role | What it serves | Rule the kit applies |
|---|---|---|
| `cover` | the lane or approach it breaks | `kit.dress` measures the space's open lines (14 m or more) and the long lines into each spawn, flag and hill, and puts one cluster ON each unbroken lane, long side across it; lanes of 24 m or more get a tall sightline breaker. A space with no such lane (a small room) gets low cover against the wall beside its doorway-to-doorway route instead. `kit.pillar_row` is cover too |
| `route` | the walkable places it joins | `kit.bridge` measures, before building, the floor on each side that reaches the far end sooner across it (at least 150 m² each), and refuses a bridge whose ends the floor below, stairs or another crossing already join (a walk under 1.6x its span), whose end lands on no floor, or whose span sits over stairs; `kit.stairs` and `kit.ramp` refuse a flight that climbs into a wall or solid, has a wall or prop within 1 m of its foot or top, joins floors already joined on foot nearby (another way up within 1.5x its length + 4 m: no twin stairs side by side), or whose top or foot leads on to under 16 m² (a solid's top, a hole); an end whose floor is not built yet is only noted (build landings first, and check `spatial.purpose{}` once built); `kit.platform` decks are routes |
| `landmark` | what sees it | `kit.landmark` refuses a landmark seen from fewer than 2 spaces and under 200 m² of floor within 80 m: stand it where long sightlines meet |
| `theme_detail` | its theme and space | props, clutter, decals, overhead runs, rock walls and boulders. Rocks only where rock belongs — caves, canyons, rock-walled spaces, open ground — and against walls and edges (a built room or a yard walled all round, dressed `mine` or `canyon`, gets the theme's built props; `kit.boulders` refuses built spaces and keeps rubble at the wall foot) |
| `fixture` | what it lights | `kit.light_room` / `kit.light_path` lamps and their lights |

A refused element names the reason (`kit.bridge: X serves no route: redundant: ...`). Fix the placement — join two
places that are otherwise far apart across a real drop, stand the landmark where it is seen, move rocks to a canyon
or a wall foot — rather than forcing it; `purpose = "report"` builds anyway with a warning, `purpose = "off"` skips
the check (only for pieces whose approaches you build later; check them with `spatial.purpose{}` once built).

**Check purpose on the built level** with `spatial.purpose{}` (CLI `spatial purpose`, MCP `spatial_query` op
`purpose`): every bridge with `catchment_a` / `catchment_b` (floor on each side that gains by crossing), `detour` (the
walk round without it) and `fault`; rock props that do not fit (`mismatched`: in built interiors or walled yards, or rubble
scattered on open floor); `orphans` (free-standing cover with no open line of 14 m through it); landmarks with
`seen_from_area` and `seen_from_spaces`; every flight of stairs or ramp (`flights`: `foot`, `top`, `rise`, `detour`
(another way up without it), `foot_clear` / `top_clear` (room to step on and off), `top_exit`, `pocket` (a dead-end pit
or perch it leads into), `bridge_gap`, `kind`, `counted` and `fault`); `ledges` (abrupt 0.3-1.5 m steps in a doorway or
corridor between two big floors with no way round on foot: the route makes players hop); and `connections` (each area's
ways out to other areas). The grader turns these into G27 (bridges that serve no route; at most one, as in the
exemplars), G28 (misplaced rocks; none), G29 (orphan cover; at most a quarter), G30 (flights into a wall or onto a
solid, blocked at an end, onto nothing, or down into a dead-end pit in a bridge's gap; at most one) and G31 (route
ledges; at most one), with locations (`locations` kinds `purposeless_bridges`, `mismatched_props`, `orphan_cover`,
`hidden_landmarks`, `purposeless_flights`, `route_ledges`, `poorly_connected`). Flights it notes but does not count
(`counted = false`: a twin flight beside another way up, a dead-end perch with nothing on it) are still worth fixing:
the well-regarded levels have a few, a generated level should have none. Join floors that should meet with a level
doorway, a ramp or stairs, never a bare step; give a pit or perch a reason (an item, a second way out) or leave it out;
and never put stairs down into the gap a bridge already crosses.

### Detailing: make a room read built

A kit room is a shell. What makes a Red Faction level read hand-built is the layer on top of the shell:
pilasters at a regular pitch, a base trim and a top trim running between them, framed doors, real structure
under the ceiling, railings on every edge you can fall from, pipes that go somewhere, machinery standing
against a wall. The kit builds that layer from the **module library** — assemblies collected from existing
levels (`glacier-cli modules harvest`), each stored with how it attaches, how it stretches or repeats, its
style and its role. **Detail every space after its shell, openings and stairs exist and before lighting and
population** (`kit.light_room`, `kit.dress`, `kit.spawn_points`, `kit.items`), so lights and items are planned
around what is there. A level built with `kit.build_layout` is already detailed (its `detail` option, on by
default); call these functions yourself for rooms you build by hand, or to add a set piece to a layout zone.

| Function | Use it for | Roles it records |
|---|---|---|
| `kit.detail{room, style, seed?, level?, doorways?, stairs?, edges?}` | the whole pass on one room: frames, machinery, walls, ceiling, stairs, edges, from one matching set | all below |
| `kit.wall_detail{room, style, seed?, walls?, pitch?, parts?}` | layered walls: pilasters each side of doorways and at a pitch, base / top trims, a band on tall walls, panels in wide bays | theme_detail |
| `kit.frame_opening{doorway, style}` | a frame stretched to a door or window opening (never narrows it) | theme_detail |
| `kit.ceiling_detail{room, style, kind?, pitch?, axis?, pipes?}` | beams, trusses or coffers wall to wall, aligned to the pilasters, 2.2 m or more above the floor | theme_detail |
| `kit.machinery{room, style, count?, kinds?, role?}` | machinery, consoles and crate stacks against walls, off doorways and routes, inside the walk budget | cover, landmark, fixture |
| `kit.railing{from, to}` / `kit.railing{path}` | a railing along a floor edge | route |
| `kit.catwalk{from, to, width?, rails?, supports?}` | a raised walkway with railings and supports | route |
| `kit.pipe_run{from, to, along}` / `{points}` | pipes along a wall, ceiling or floor with flanges and brackets | theme_detail |
| `kit.stair_detail{stairs, style}` | stringers and handrails on a `kit.stairs` flight | route |
| `kit.rock_detail{space or path, style}` | rock formations at cliff feet and as cover in canyons and caves | cover, landmark, theme_detail |

Rules that keep it purposeful:

- **One style per space, one seed per level.** `style` is `industrial`, `bunker`, `admin`, `mine`, `cave`,
  `canyon`, `mars_base`, `lab`, `tech` or `generic` (a `kit.dress` theme name also works). The parts of one
  call come from one donor set, so a wall's pilasters and trims belong together; pass the same `style` and
  `seed` to neighbouring rooms so a level keeps one hand. A different seed picks a different matching set.
- **Every part has a role.** Each brush is tagged `kit.role` (`cover`, `route`, `landmark`, `theme_detail`,
  `fixture`) and `kit.module` (the library id, or `primitive.<what>`); the record's `roles` counts them and
  `placed` lists each part. Place for the role you need: `kit.machinery{role = "cover"}` where a wide room has
  no cover, one `landmark` piece where players orient, `theme_detail` everywhere else. Do not add parts that
  have no reason to be there.
- **Clearances are kept for you** — doorways, the way between them, spawns, items and earlier dressing stay
  clear and floor-standing parts stay inside the walk budget — but read `skipped` (each entry says what was
  left out and why) and the `note`.
- **The library is curated by the mapper, not by you.** `library/modules/curation.json` holds the mapper's
  keep / reject / relabel decisions (made on the `glacier-cli modules sheet` contact sheet); it is applied on
  load — rejected modules are left out of `modules.list`, `fit`, `place` and the kit, kept ones rank first.
  Never write or edit `curation.json` yourself; if a module looks wrong, say which id and why in your hand-back.
- **No library, simpler parts.** `modules.available` is false when no library is found; every function then
  builds plain parts of its own and returns `fallback = true`. Say so in your hand-back, and tell the user
  that `glacier-cli modules harvest` builds a library.
- **Look before you keep it.** Render a camera view at eye height (`render --camera … --look …`) of each
  detailed space. Parts should sit on their wall, match the room's material and leave the room readable; if a
  set looks wrong for the room, change `seed` or `style`, or turn parts off (`parts = {panels = false}`).

For single placements, `modules.list{category, attach, style, max_size, limit}` finds modules and
`modules.place{id, pos, yaw, length?, height?, depth?}` places one (a wall module's origin is on the wall at
floor level, facing into the room); `modules.fit{id, length}` says what a stretch would give.

```lua
-- sample: detailed-room
local kit = ged.import("kit")
local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {16, 6, 20} }
local store = kit.room{ name = "store", pos = {0, 0, 17}, size = {8, 4, 8} }
local door = kit.doorway{ name = "hall_store", a = hall, b = store }
local d = kit.detail{ room = hall, style = "industrial", seed = 2, level = "full", doorways = { door } }
kit.detail{ room = store, style = "industrial", seed = 2, level = "light" }
kit.light_room{ room = hall }
kit.light_room{ room = store }
kit.spawn_points{ room = hall, count = 2 }
return string.format("detail: %d parts (theme_detail %d), fallback = %s, holes = %d",
  #d.placed, d.roles.theme_detail or 0, tostring(d.fallback), ops.holes().count)
```

### Layout first: brief → graph → check → build → grade

Do not start a whole level with rooms. Start with a **layout graph**: what the spaces are, how they
connect, which floor each sits on and where the objectives go. `kit.layout` turns a brief into that
graph at the right scale for the player count, before a single brush exists; you read it, edit it like
any Lua table, check it, look at it, and only then build it.

1. **Brief → graph.** `local g = kit.layout{ gametype = "dm"|"ctf"|"koth"|"payload", players = 8,
   class = "standard"|"open_air", brief = "…", seed = 1 }`. The graph is a plain table:
   - `g.zones`: each with `id`, `archetype` (`kit.layout_archetypes{}` lists them: hall, storage,
     colonnade, split, gallery, atrium, arena, sunken, mound, courtyard, pit, tunnel, nook; open-air
     isle, deck, tower, ring, terrace, hangar, summit, gantry, outpost, plaza, mast, garden; payload
     road, yard, finale, start, overlook; traced `poly`), `role` (hub, room, base, spawn, flank, hill,
     span, checkpoint, high, nook), `pos` (the floor point at the plan's centre, world), `size = {w, d}`,
     `rot` (yaw in degrees, default 0: the zone's plan turned about `pos`; 0 faces +z, 90 turns +z
     toward +x; `"+x"`… sides, `params.heading` and `params.split` always mean the zone's own walls),
     `height`, `levels` (port floors above `pos.y`, e.g. `{0, 5}` for a zone with a gallery a storey
     up), `palette` (`kit.palette` theme, variant, team), `ground` (`"brush"`, `"terrain"` or `"auto"`;
     `kit.layout{ground = …}` sets it on every outdoor zone) for outdoor zones, and `dress` (theme and density for the population pass). A `poly`
     zone has `outline = {{x, z}, …}` (3-64 world points, either winding) instead of `size`; its
     `pos` needs only the floor (`{nil, y, nil}`), `sky = true` opens it to the sky.
   - `g.links`: `type` (corridor, doorway, stair, ramp, drop, bridge, window, lift, road, join), `a`,
     `b`, `a_y`, `b_y` (port floors; left out, each end meets its zone's floor, or a graded road's
     top floor at its exit end), `width`, `offset` (where along the shared wall; `nil` = centred).
     A link between facing walls of two rectangle zones of one `rot` is straight (classic); any
     other corridor, doorway, road, bridge, stair or ramp is a free link: `path = {{x, z}, …}`
     (waypoints in world metres; left out, it is routed for you and `kit.layout` writes the route
     back), `a_side` / `b_side` (the wall it leaves by: `"+x"`…, or a poly zone's edge number, edge
     i running from outline point i to i + 1) and `a_at` / `b_at` (a world point the port sits
     near). `join` opens two touching or overlapping zones into one space (no wall between them).
   - `g.objectives`: spawn groups (`kind = "spawns"`, zone, team, count), CTF flags, the KOTH hill,
     and for payload the cart (its spine zones in order) and checkpoints (each lists the spawn groups
     that are live while it is the cart's next target).
   - `g.budget`: the size class's floor area, floor per spawn, zone and hub counts (spec §a.1).

   The same seed always gives the same graph; another seed gives another layout of the same brief.
   Zones are placed freely (no grid), around the origin:
   - **Indoor DM and KOTH**: rooms of very different sizes abut through shared 1 m walls with
     doorways and wide openings; a hub (KOTH: the hill, a 40 x 40 m sunken court with the rooms grown
     in a ring round it) with a two-storey hall beside it that carries the storeys; long galleries,
     small rooms, annexes joined to rooms (L and T plans), apses and canted bays joined to halls; a
     wing in a frame turned 15-60° (`rot`), tied on by a traced hinge room and closed into a loop by
     a vestibule, a corner room or a bent corridor; traced corner rooms in the angles the mass
     leaves, rooms re-outlined with canted corners and notches; a few corridors that bend round the
     mass.
   - **CTF**: the red half and the centre are composed and the red half is mirrored for blue across
     x = 0. The lanes are built in: a mid-lane room between the base and the hub, a long high-lane
     gallery, a low flank a climb below, and a turned loop wing off the base.
   - **Payload**: the track is designed first, one 90° corner inside a yard and bends of 25-50°
     through cart roads, and every segment (its road, its yard and the rooms beside them) is laid out
     in its own heading: rooms abut the road pads and yards, low galleries run along the flank, terraces
     stand on the high side after a bend, traced wedge rooms sit inside the bends, bays open off yards.
   - **Open air** (DM, CTF, KOTH): a hub platform (KOTH: the floating hill) with spokes hung off it
     at free angles and distances, rim platforms between the spokes and more outward, each platform
     turned to meet its straight bridges, stairs and ramps; CTF mirrors the red half.

   A generated graph therefore holds turned zones (`rot`), traced `poly` zones with their `outline`,
   `join` links, and free links with the route the generator chose written into them (`a_side` /
   `b_side`, `a_at` / `b_at`, and `path` where the link bends), so it builds the same way every time.
   Sizes follow the player class: 2-4 players an arena of 1,500-3,000 m², up to 8 a small level
   (3,300-4,900 m²), up to 16 a medium one (4,400-6,400 m²), more a large one with more and smaller
   zones; CTF 5,000-8,200 m², KOTH 7,400-10,000 m², payload about 3-4 times a DM-M map. Generation
   takes a few seconds (the slowest seeds about 7 s on a busy machine), and generated plans stay
   within about 200 m of the origin.
2. **Check.** `kit.check_layout{ graph = g }` returns every violation as `{severity, rule, message,
   zone?, link?}`. `"error"` means it cannot be built (zones overlap, a link has no room on its walls,
   a gap too short for a stair, a drop into a room too low for it); `"rule"` means a design rule is
   broken: a dead end (every zone needs two links except one reward nook per 1,500 m²), a zone that is
   the only way to 10% or more of the floor, too few loops (cycle rank of at least 1 per 1,000 m²),
   hubs and objectives without two separate routes, fewer than three storeys 4.5-10 m apart or a
   single kind of climb between two storeys, an archetype used more than twice, mirror symmetry in
   a non-team mode, and the mode rules (CTF: three lanes, one at another height or through a flank
   tunnel, flags 50-115 m apart, each base three exits on two heights, balanced walks; KOTH: five to
   eight approaches into a sunken or raised hill; payload: three to five checkpoints, three routes
   across every checkpoint, a climb and a turn on the track, a fortified finale, partly outdoors;
   open air: platforms joined by bridges and stairs). Fix the graph, check again.
3. **Look.** `kit.layout_preview{ graph = g }.text` draws the plan as text, north (+z) up, digits for
   the storey, letters for the zones (turned and traced zones drawn as their world outlines), one
   character per link type along its route (`=` corridor or doorway, `s` stair, `r` ramp, `v` drop,
   `b` bridge, `~` window, `l` lift, `#` cart road, `+` join) and `R`/`B`/`H`/`C`/`S` for flags, hill,
   checkpoints and spawns, then a legend line per zone and link. `.svg` is the same plan as an image.
4. **Build.** `kit.build_layout{ graph = g, name = "dm" }` checks again (errors refuse; rule
   violations are logged and listed in the report, or refuse with `strict = true`), then builds in
   time order: every zone's shell, every link's air, the zones' interiors (galleries, courts, stairs,
   cover), the links' steps, ramps, landings, door jambs, bridges and lifts, details, the **room
   detail pass**, lights, then the spawns, flags, hill (a Capture_Point_Handler with its zone trigger)
   and the payload cart, handler, checkpoints and spawn hand-off. The detail pass runs by default and
   finishes every zone the way a mapper does, from its archetype and palette theme: pilasters and
   base / top trims on the long walls (trims only under a gallery), frames on doorways and windows,
   beams or trusses flush under roofed ceilings (never under open sky), railings on gallery, raised-half,
   pit-rim and bridge edges that drop away, stringers and handrails on the zones' stairs, and rock
   formations in cave-palette zones — library modules when the library is present, else hand-authored
   parts, all kept off doorways, routes, stairs, spawn spots, the cart lane and bridge decks.
   `detail = false` skips it; `detail = {ceilings = false, railings = false}` (parts `walls`,
   `frames`, `ceilings`, `railings`, `stairs`, `rocks`; also `style`, `seed`) turns parts off.
   `report.detail` and each `report.zones[i].detail` count what was placed. A pit zone is a 10 m
   chasm crossed by its plank bridge: no stairs lead into it. A turned zone is built in its own frame and turned into place;
   a free link is built along its route. It returns `{zones, links, spawns, objectives, warnings,
   area}`; each zone report has its world `min` / `max`, `outline` (its world plan) and `rot`, its
   `spots` (clear floor points), `dress` for the population pass, and its
   `name` (`<name>_<zone id>`), which `kit.dress`, `kit.items` and `kit.spawn_points` take as a room
   name (see "Populating a built layout" below); each link report its `mode` (classic, free or join)
   and a free link's `path` as built. Or run the population pass per zone with
   `populate = function(zone_report, zone, graph) … end`.
5. **Grade.** `ops.build()` then `design.grade{ gametype = …, players = … }` (section 9). A layout
   straight from `kit.layout` passes the flow checks (no chokepoint cell, no big dead-end pockets,
   loops; CTF lanes, KOTH approaches, payload segments and hand-off); items, dressing and spawn
   safety are the population pass that follows.

```lua
-- sample: layout-first
local kit = ged.import("kit")
-- 1. Brief -> graph (an arena for 4 players), with its own seed.
local g = kit.layout{ gametype = "dm", players = 4, seed = 7, brief = "concrete bunker deathmatch" }
-- 2. Check it before any brush exists; every violation names its zone or link and its rule.
local issues = kit.check_layout{ graph = g }
for _, v in ipairs(issues) do print(v.severity, v.rule, v.message) end
-- 3. Look at the plan (north up, digits are storeys).
print(kit.layout_preview{ graph = g }.text)
-- 4. Edit it like any table, then check again: retheme the first zone.
g.zones[1].palette = { theme = "admin", variant = 2 }
assert(#kit.check_layout{ graph = g } == #issues)
-- 5. Build it and grade it.
local rep = kit.build_layout{ graph = g, name = "arena" }
ops.build()
local grade = design.grade{ gametype = "dm", players = 4 }
local flow = 0
for _, f in ipairs(grade.failures) do if f.id == "G12" or f.id == "G13" or f.id == "G14" then flow = flow + 1 end end
return string.format("%d zones, %d links, %d spawns; %s, %d flow failure(s)", #rep.zones, #rep.links, rep.spawns, grade.verdict, flow)
```

### Layout graphs: editing, tracing a sketch, populating

**Editing a generated graph.** Change it like any table and run `kit.check_layout` after every edit.

- **In place:** a zone's `palette`, `dress` or `height`; its archetype for another of the same class
  whose `min` / `max` (from `kit.layout_archetypes{}`) take its `size`; the objectives (spawn counts,
  which zone holds a spawn group); adding or removing links (the checker says when a rule breaks).
- **Moving, resizing or turning a zone:** a generated plan is packed (rooms share walls, joined zones
  touch), so a zone seldom moves alone: moved apart, its joins no longer touch; moved closer, it
  overlaps its neighbours. Move a joined group together (a traced zone moves by its `outline` points;
  its `pos` x and z follow from the outline). Then clear the route the generator wrote on every link
  of the zones you moved (`l.path, l.a_at, l.b_at, l.offset = nil, nil, nil, nil`) so the link is
  routed again, and clear `a_side` / `b_side` too where that wall no longer faces the other zone or
  a traced outline's points changed (edge numbers follow the points). When an edit does not settle,
  another seed is usually quicker than moving several zones.
- **Pinning a route:** give a link `path`, `a_side` and `a_at` yourself, as in the tracing rules below.

**From a sketch or a concept image: trace it into the same graph, never into brushes.** Trace the
plan's regions as polygons in metres, join the ones that are one space, connect the rest with links
that bend where the sketch's connectors bend, and use rectangle archetypes only where a set piece is
wanted. Then the same loop: `kit.check_layout` → `kit.layout_preview` → `kit.build_layout` → populate
→ grade.

- **Scale from the class budget, never from the image's pixels.** Take `g.budget` from a `kit.layout`
  call for the same gametype and player count (or the numbers in step 1), pick one metres-per-pixel
  factor so the traced areas add up to that budget, and read every coordinate off the sketch with it.
  A picture traced at its own pixel size gives 9-14 m-tall boxes and toy-sized set pieces.
- **Regions → `poly` zones.** `{ id = "yard", archetype = "poly", pos = {nil, 0, nil}, outline =
  {{x, z}, …}, sky = true }`: 3-64 world points in order around the region (either winding, no
  crossing edges, points at least 0.25 m apart), the floor in `pos[2]` (storeys 5 m apart, 4.5 m for
  payload). The zone is built from its outline: a room with a raised ceiling bay when it is big enough,
  or a yard under the sky with `sky = true`; low cover on its clear floor; spawn spots inside the
  outline. Leave `height` out to get the proportion rule's (by area; 10 m for a yard).
- **One space from several regions → `join`.** `{ type = "join", a = "hall", b = "bay" }` opens two
  zones that touch or overlap into one space: an L or U from two pieces, a bay or apse on a hall, two
  traced regions of one room. Floors within 1 m, at least 3 m of shared boundary; on a rectangle zone
  the shared boundary lies on one wall, clear of its corners (the checker says which stretch the wall
  takes). Give two traced polygons the same points along the edge they share, or overlap them. Zones
  that are not joined stand at least 1 m apart (2 m between open-air platforms).
- **Set pieces → rectangle archetypes, turned with `rot`.** A finale, an atrium, a graded `road`, a
  gallery hall: give it its world `pos` and the yaw that lines it up with the sketch (`rot = 35` turns
  its +z wall 35° toward +x). Its `"+x"`… sides, `params.heading` and `params.split` stay its own.
- **Connectors → links.** Every place the sketch connects two regions is a link; its type follows the
  height difference (corridor, doorway or road on one storey with a step of up to 1 m; stair or ramp
  for one storey; bridge over a chasm or between platforms). Between a poly zone and anything, or
  between zones of different `rot`, it is a free link: give `path = {{x, z}, …}` for a connector that
  bends (waypoints in world metres, at most a 90° turn per point, segments at least 1 m; a free link
  always leaves a wall square on a stub as long as the passage is wide plus 1 m, so keep the first and
  last waypoint a few metres out from the walls), or leave `path` out and the link is routed for you
  (straight, one bend or a dog-leg) clear of the other zones and links. `a_side` / `b_side` pin the
  wall (`"+x"`… or a poly edge number) and `a_at` / `b_at` the point a port sits near. A stair or ramp
  needs one straight stretch as long as its run plus 1 m (a 5 m stair runs 7.5 m, a 5 m ramp 10 m);
  a bridge runs straight, at most 45° off square to the walls it leaves. Drops, lifts and windows join
  facing walls of rectangle zones of one rot only. Between two rectangle zones of one `rot` a link is
  classic unless it gives a `path` or a side or point. Add the second routes the sketch leaves out until
  the checker is satisfied: a sketch that is a tree builds a tree.
- **Read the checker's answers.** A free link that cannot be built names itself and the reason ("its
  path from yard to depot does not work: it enters zone canyon or passes within 1 m of it"); move the
  waypoint or the zone and check again. `report.links[i].path` is the route that was built and
  `report.zones[i].outline` each zone's world plan.
- **Keep the plan central.** Put the trace's middle at the origin: turned zones, traced zones and
  bent links build watertight within about 450 m of it, a stair or ramp that bends within about
  250 m. Check for holes after the build as always.
- **Payload tracks on any heading.** A `road` zone's `params.heading` (`"+x"`, `"-x"`, `"+z"`,
  `"-z"`; default `"+z"`) is the way the cart travels along it (its own sides: turn the road with
  `rot` for any other bearing): it is entered on the opposite end and left on the heading end, `size`
  is `{width, length}` for `±z` and `{length, width}` for `±x`, `params.south` / `params.north` are
  the level pads at the entry / exit ends and `params.rise` the climb (the grade is the rise over the
  stretch between the pads, at most 15%). The cart turns inside the zones between roads (a yard
  entered from the south and left to the east is a turn) and along a free road that bends 20° or
  more; it follows a free link's path. A finale's `params.heading` is the way the cart enters it (its
  cart ramp faces that way). List the cart's zones in order in the `cart` objective; each consecutive
  pair needs a road, bridge, doorway, corridor or join between them. A `bridge` link in a payload
  layout is a deck over a chasm cut to the sky (`depth`, default 8 m) between two floors at one level:
  give a cart bridge the road's `width` (8) and the zone beyond it room for that width.

```lua
-- sample: layout-trace
local kit = ged.import("kit")
-- "Supply line", traced off a concept sketch at 1 m per sketch unit (its areas add up to the payload budget):
-- a walled loading yard, a gate yard, a canyon the road bends through, a chasm bridge to an angled depot, a
-- graded road up to the defenders' hold, and an overlook beside the road.
local function traced(id, y, outline, extra)
  local z = { id = id, archetype = "poly", pos = { nil, y, nil }, outline = outline, sky = true }
  for k, v in pairs(extra) do z[k] = v end
  return z
end
-- The depot, its road and the hold are set pieces in one frame turned 30 degrees: give each its spot in that
-- frame, turned into world coordinates, and the same rot (classic links join them wall to wall).
local function turned(id, arch, lx, lz, y, size, extra)
  local r = math.rad(-30)
  local z = { id = id, archetype = arch, pos = { lx * math.cos(r) + lz * math.sin(r), y, -lx * math.sin(r) + lz * math.cos(r) },
    size = size, rot = -30 }
  for k, v in pairs(extra) do z[k] = v end
  return z
end
local g = { kind = "layout", gametype = "payload", class = "payload", step = 4.5, zones = {
    traced("yard", 0, { { -26, -22 }, { 24, -22 }, { 30, 0 }, { 20, 20 }, { -26, 20 } }, { palette = { theme = "industrial", variant = 1 }, role = "spawn", segment = 1 }),
    traced("gate", 0, { { -14, 30 }, { 18, 30 }, { 24, 45 }, { 18, 60 }, { -14, 60 } }, { palette = { theme = "industrial", variant = 2 }, role = "checkpoint", segment = 1 }),
    traced("canyon", 0, { { 32, 34 }, { 66, 28 }, { 78, 62 }, { 56, 86 }, { 30, 72 } }, { palette = { theme = "cave", variant = 1 }, role = "span", segment = 2 }),
    traced("overlook", 4.5, { { 16, 100 }, { 40, 98 }, { 43, 116 }, { 20, 120 } }, { palette = { theme = "bunker", variant = 1 }, role = "high", segment = 2 }),
    turned("depot", "yard", 120, 37, 0, { 32, 32 }, { palette = { theme = "admin", variant = 1 }, role = "checkpoint", segment = 2 }),
    turned("climb", "road", 120, 79, 0, { 10, 50 }, { palette = { theme = "marsbase", variant = 1 }, role = "span", segment = 3, params = { heading = "+z", rise = 4.5, south = 10, north = 10 } }),
    turned("hold", "finale", 120, 127, 4.5, { 44, 44 }, { palette = { theme = "bunker", variant = 2 }, role = "checkpoint", segment = 3, params = { heading = "+z" } }),
  }, links = {
    { type = "road", a = "yard", b = "gate" },               -- free links: routed for you between traced zones
    { type = "corridor", a = "yard", b = "canyon" },
    { type = "road", a = "gate", b = "canyon" },
    { type = "stair", a = "gate", b = "overlook" },
    { type = "bridge", a = "canyon", b = "depot", width = 8 }, -- the cart's chasm bridge, straight across
    { type = "road", a = "depot", b = "climb" },             -- classic: facing walls in one frame
    { type = "ramp", a = "depot", b = "overlook" },
    { type = "road", a = "climb", b = "hold" },
    { type = "corridor", a = "overlook", b = "hold" },
    { type = "stair", a = "overlook", b = "hold", b_y = 9 },  -- up to the hold's terrace
  }, objectives = {
    { kind = "cart", zones = { "yard", "gate", "canyon", "depot", "climb", "hold" }, attacking_team = "blue" },
    { kind = "spawns", id = "a1", zone = "yard", team = "blue", role = "attack", count = 3 },
    { kind = "spawns", id = "d1", zone = "depot", team = "red", role = "defend", count = 3 },
    { kind = "spawns", id = "a2", zone = "gate", team = "blue", role = "attack", count = 3 },
    { kind = "spawns", id = "d2", zone = "overlook", team = "red", role = "defend", count = 3 },
    { kind = "checkpoint", index = 1, zone = "gate", spawns = { "a1", "d1" } },
    { kind = "checkpoint", index = 2, zone = "depot", spawns = { "a2", "d2" } },
    { kind = "checkpoint", index = 3, zone = "hold", spawns = { "a2", "d2" } },
  } }
-- Check, look, build. A free link that cannot be built names itself and why; move a waypoint or a zone.
local issues = kit.check_layout{ graph = g }
for _, v in ipairs(issues) do print(v.severity, v.rule, v.message) end
print(kit.layout_preview{ graph = g }.text)
local rep = kit.build_layout{ graph = g, name = "supply" }
-- The routes as built (world metres) and the zones' world plans; then populate by zone name.
local bends = 0
for _, l in ipairs(rep.links) do if l.path then bends = bends + #l.path - 2 end end
local names = {}
for _, z in ipairs(rep.zones) do names[#names + 1] = z.name end
local items = kit.items{ gametype = "payload", rooms = names, seed = 1 }
return string.format("%d issues, %d zones, %d links (%d bends), %d spawns, %d items, %.0f m2", #issues, #rep.zones, #rep.links, bends, rep.spawns, items.count, rep.area)
```

**Populating a built layout.** Every zone `kit.build_layout` builds answers to its name,
`report.zones[i].name` (`<name>_<zone id>`, e.g. `"arena_z3"` for zone `z3` of a layout built with
`name = "arena"`), wherever the kit takes a room name: `kit.dress{room = …}`, `kit.items{rooms = …}`,
`kit.spawn_points{rooms = …}`. Each zone report also carries `room` (the zone's record), `dress`
(the theme and density the layout chose) and `sky`. Spawns, flags, the hill and the payload objective
are already placed by the build; add dressing and items per zone, then grade.

```lua
-- sample: layout-populate
local kit = ged.import("kit")
local g = kit.layout{ gametype = "dm", players = 4, seed = 7, brief = "concrete bunker deathmatch" }
local rep = kit.build_layout{ graph = g, name = "arena" }
-- Every built zone is a kit room by its name; the layout's palette theme picks the dressing theme.
local by_palette = { admin = "control", bunker = "storage" }
local names, placed = {}, 0
for i, z in ipairs(rep.zones) do
  names[#names + 1] = z.name
  local d = kit.dress{ room = z.name, theme = by_palette[z.dress.theme] or "industrial", density = z.dress.density, seed = i }
  placed = placed + d.count
end
local items = kit.items{ gametype = "dm", rooms = names, seed = 1 }
return string.format("%d zones dressed by name (%d elements), %d items", #names, placed, items.count)
```

The graph and every function are Lua, so the MCP route is the same: send the script to `run_lua`
(or `glacier-cli run layout.lua --out work/level.rfl`), print the preview and the violations, edit,
and run again on a fresh level.

### Recreating from a reference (maps, mockups, drawings)

When the brief is "build THIS" from an overhead picture (a map, a mockup, a drawing, a top-down render),
do not eyeball it and do not use the generative kit (`kit.layout` snaps, reshapes, links and dresses).
Measure the picture, build the plan literally with `kit.trace`, and compare the build with the picture
after every change. Everything below is Lua (`run_lua`, or `glacier-cli run` with the level); picture
paths are relative to the level's folder, and the images it writes are PNGs you look at
(`reference_view image=<png>` shows any of them, with `step` / `zoom` for a fine grid).

1. **Open and grid.** `ref.open("map.png")`, then `ref.grid{out = "grid.png"}` for the whole picture and
   `ref.grid{crop = {x, y, w, h}, zoom = 4, out = "grid_a.png"}` for each area: every line is labelled
   in the picture's own pixels and crossings carry `x,y`, so you read a corner to a pixel or two. Read
   corners off the grid; never guess them from the overview.
2. **Calibrate.** Find a scale: a scale bar, or a length you know (a 2.5 m door, a 4.5 m corridor; see
   `ref scale` below). `ref.calibrate{bar = {x1, y1, x2, y2, metres}, origin = {px, py}, at = {0, 0}}`
   maps pixels to metres: the top of the picture is +Z (north up, like `render_plan`), picture down is
   -Z, picture right +X (`up = "+x"` or `rotation = deg` for other headings). `ref.to_world(x, y)`,
   `ref.to_px(x, z)` and `ref.world_points(list)` convert.
3. **Trace the playable area** (optional, best on clean mockups): `ref.mask{samples = {{x, y}, ...},
   tolerance = 45, connected = true, out = "mask.png"}` picks the floor colours under the sample pixels
   (sample every floor colour: tiles, platforms, ramps), closes grout lines, drops specks, and returns
   the outlines as `polygons[i].points` (world, simplified to 0.5 m) plus `px`. Look at `mask.png`:
   it is also what `ref.compare` scores against. On painted or busy art use `mode = "hsv"` and fix the
   mask with `ref.mask_edit` (see "Reading the picture" below); a mask PNG (white = playable, the
   picture's size) can also be passed as `mask = "my_mask.png"`.
4. **Build literally.** `kit.trace{units = "px", regions = {...}, openings = {...}, solids = {...}}`
   (or `units = "m"`): each region is a polygon of any shape (concave, curved: up to 256 corners) with a
   floor and a ceiling or the sky. `kind`: `room`, `corridor`, `yard` (sky), `pit` (a lower floor),
   `ramp` / `stairs` (`floor = {low, high}`, `climb = {dx, dz}` uphill), `deck` (a slab over another
   region's air) and `path` (a centre line plus a width; see below). Regions that share a wall line are
   open to each other along it; leave a wall-thick gap where the picture shows a wall, and add
   `openings = {{a, b, at, width}}` for doorways through it. `solids` are pillars and machine
   footprints (`top = "ceiling"` for a pillar). Nothing is snapped beyond 1/16 m and the weld
   `tolerance` (0.15 m), nothing is linked or dressed; it refuses, with the reason, two regions sharing
   the same space, an opening that is not between its two regions, a ramp steeper than 45 degrees, a
   wall wedge too thin to build and an outline that crosses itself. `build = false` validates only and
   lists `connections` (which regions touch, the drop between them, and `ledge = true` where that drop
   is more than a step: check those are meant), `stacked` storeys and each region's `hosts`.
5. **Compare.** `ops.build()`, then `local c = ref.compare{out = "cmp.png"}`: the level's walkable
   floor drawn on the picture (cyan), missing floor red, extra floor orange, the mask's edge yellow.
   It returns `iou`, `iou_tol` (within 0.5 m), `walls_mean_m` (how far the floor's edges are from the
   picture's, in metres), `missing_m2`, `extra_m2` and `worst`: the largest misses and extras with
   `center` and `box_world` in metres. `y = {lo, hi}` compares one storey.
6. **Fix the worst region, rebuild, compare again.** Re-read that area zoomed, correct the region's
   corners (more points on a curve, a step in a wall, the missing pillar), and repeat until
   `walls_mean_m` is under about 0.5 m and `worst` lists nothing larger than a few square metres. Then
   heights: every region's floor from the picture's labels, sections or elevation chart, and
   `render_view` (iso) to check ramps and ledges.

The picture, calibration and last mask stay with the level for the session, but set them again at the
top of each script (it is one line each) so the script can be rerun from scratch. A worked example
(metres; the picture-pixel version is the same with `units = "px"` and pixel corners):

```lua
-- sample: trace-literal
local kit = ged.import("kit")
-- a quarter-circle corridor: corners generated on its two arcs (centre 0, -2.5; radii 27.5 and 21.5)
local curve = {}
for i = 0, 8 do local a = math.rad(90 - i * 11.25) curve[#curve + 1] = { 27.5 * math.cos(a), -2.5 + 27.5 * math.sin(a) } end
for i = 8, 0, -1 do local a = math.rad(90 - i * 11.25) curve[#curve + 1] = { 21.5 * math.cos(a), -2.5 + 21.5 * math.sin(a) } end
local tr = kit.trace{ name = "site", regions = {
    { name = "hall", poly = {{-40, 30}, {0, 30}, {0, 15}, {-25, 15}, {-25, -5}, {-40, -5}}, floor = 0, height = 5 },
    { name = "curve", kind = "corridor", poly = curve, floor = 0 },
    { name = "yard", kind = "yard", floor = 0,
      poly = {{17.5, -2.5}, {45, -2.5}, {45, -15}, {30, -15}, {30, -20}, {22.5, -20}, {22.5, -30}, {17.5, -30}} },
    { name = "platform", kind = "yard", poly = {{30, -15}, {45, -15}, {45, -30}, {30, -30}}, floor = 2 },
    { name = "ramp", kind = "ramp", poly = {{22.5, -20}, {30, -20}, {30, -30}, {22.5, -30}}, floor = {0, 2}, climb = {1, 0}, sky = true },
  },
  solids = { { poly = {{-34, 6.5}, {-31, 6.5}, {-31, 3.5}, {-34, 3.5}}, region = "hall", top = "ceiling" } } }
level.player_start{ pos = {-32, 1, 22}, yaw = 90 }
local open = {}
for _, c in ipairs(tr.connections) do open[#open + 1] = c.a .. "-" .. c.b end
return string.format("%.0f m2 traced, %d brushes, open: %s", tr.area, #tr.brushes, table.concat(open, ", "))
```

The reference loop around it, for a calibrated picture (not a sample: it needs your picture):

```
ref.open("map.png")
ref.calibrate{bar = {40, 603, 120, 603, 10}, origin = {400, 320}}
ref.mask{samples = {{300, 140}, {700, 520}}, tolerance = 45, connected = true, out = "mask.png"}
local tr = kit.trace{units = "px", regions = {...}}   -- corners read off ref.grid
ops.build()
local c = ref.compare{out = "cmp.png"}
print(c.iou, c.walls_mean_m, c.worst[1] and c.worst[1].kind, c.worst[1] and c.worst[1].center[1])
```

#### Reading the picture

A new level works from the start: `glacier-cli run build.lua --out new.rfl` (or `glacier-cli new new.rfl`
first) treats `new.rfl`'s folder as the level's folder, so `ref.open("map.png")` and every `out` PNG
resolve there. Keep the picture, the scripts and the level in one folder.

- **Check the trace on the picture, every loop.** `ref.grid{overlay = plan, out = "o.png"}` draws your
  trace (the same table you give `kit.trace`, with its `units`) over the picture: each region outlined in
  its own colour with its name and a dot on every corner; `solids` in white. Add `crop` + `zoom = 3..6`
  per area and compare each dot with the art's corner. `overlay` also takes a list of regions
  `{name, poly}`, `ref.mask` polygons or point lists (pixels; `overlay_units = "m"` for world points).
  After `ops.build()`, `ref.grid{overlay = "level", out = "built.png"}` draws the floor you actually
  built, one outline per height band named `y 0` / `y 0..3`: a quick check that each area got its height.
- **Mask modes.** `mode = "rgb"` (default) matches by RGB distance (`tolerance`). On painted mockups and
  rendered overheads, where saturated rock sits next to grey or yellow tiles, use `mode = "hsv"`: a sample
  then matches by hue (`hue_tol`, degrees), saturation (`sat_tol`) and brightness (`val_tol`), and grey
  references ignore hue. Or give boxes: `ranges = {{sat = {0, 0.18}, val = {0.3, 0.85}}, {hue = {35, 60},
  sat = {0.4, 1}}}` (grey tiles, yellow tiles). Read `samples_report`: each sample's colour, `hsv`, the
  `fraction` of the picture it matched and its `border_fraction`. `warnings` flags a sample matching over
  half the picture or most of the border, and a mask covering almost everything; never use such a mask.
- **Invert on a flat background.** When the area around the map is one flat colour (a rendered overhead's
  backdrop), `ref.mask{colors = {{r, g, b}}, invert = true, tolerance = 10}` with that background colour
  takes every drawn pixel as floor, dark floors included; a sample's `border_fraction` near 1 says which
  colour is the background.
- **Holes and pieces.** Outlines keep their holes (`holes_px`, `holes`, `hole_count`; a ring corridor's
  island, a pillar). `kit.trace` regions have no holes, so pass `holes = "cut"`: every piece with a hole
  is cut along pixel rows through the hole into hole-free pieces that share the cut edges exactly.
  `split = "colors"` gives one set per colour class; `split = {{name = "hall", rect = {x, y, w, h},
  floor = 0}, {name = "deck", poly = {...}, floor = 3}}` gives one per zone (a storey or height tile),
  and `m.regions` is then ready for `kit.trace{units = "px", regions = m.regions}`: named pieces with
  `poly` and your zone's fields, neighbours sharing their seams exactly. Add `kind`, ramps and openings.
- **Edit the mask in Lua.** `ref.mask_edit{erase = {pit_poly}, fill = {gap_poly}, out = "mask_fixed.png",
  overlay = "mask_fixed_overlay.png"}` erases rock rims and fills shadowed floor on the last mask (or on
  `mask = "<png>"`); it becomes the compare mask. `ref.mask{polygons = plan, mask_out = "trace.png"}`
  rasterises a trace or any polygons into a mask (that compare then measures build fidelity only).

A painted mockup, end to end: sample the grey and yellow tiles with `mode = "hsv"` (the red rock differs
in hue and saturation), check `samples_report` and `warnings`, erase what is not floor and fill what the
art shades, then compare against the result.

```
ref.open("mockup.jpg")
ref.calibrate{ppm = 9, origin = {900, 500}, at = {0, 0}}
local m = ref.mask{mode = "hsv", samples = {{610, 420}, {700, 380}}, sat_tol = 0.15, val_tol = 0.3,
  connected = true, holes = "cut", out = "mask.png"}
for _, w in ipairs(m.warnings or {}) do print(w) end
ref.mask_edit{erase = {{{1200, 80}, {1260, 80}, {1260, 140}, {1200, 140}}}, out = "mask_fixed.png"}
ref.grid{overlay = m.polygons, crop = {500, 300, 400, 300}, zoom = 3, out = "check.png"}
```

**Multi-storey plans, ramps inside rooms, paths and seams.** Trace each floor once, as the picture
shows it; do not cut rooms into tiles around ramps.

- **Ramps, stairs and decks inside a room or yard**: draw them over it. Their fill (or slab) replaces
  the room's floor there, and they take the room's ceiling and sky unless you give their own;
  `tr.regions.<name>.hosts` names the room. A ramp whose top reaches an upper storey's floor is
  hosted by both, and its air opens the hole in the upper floor.
- **Stacked storeys**: two ordinary regions may overlap in plan only when one lies wholly above the
  other with at least a 0.25 m slab between (lower ceiling <= upper floor - 0.25); anything else
  that shares space is refused with the fix. `storeys = {{name, floor, ceiling | height}}` plus
  `storey = "<name>"` on a region give its floor and ceiling. `tr.stacked` lists the stacked pairs.
- **Paths** (`kind = "path"`): tunnels, roads, curved ramps. `path = {{x, z}, ...}` is the centre
  line, `width` (or `widths`, one per point) its width, `floor` (or `floors`, one per point) its
  height: a change of floor along it is a ramp, curved where the line bends. It becomes segment
  regions `name.1`, `name.2`... joined by mitres; an opening may name the path (the segment nearest
  `at` is used). A bend sharper than 120 degrees, a segment shorter than its mitres, or a slope
  over 45 degrees is refused.
- **Seams**: a corner within `tolerance` of another region's wall is moved onto it and inserted into
  that wall, so tiles cut from one mask meet exactly even when an edge is a pixel off. A wedge of
  wall thinner than 5 cm that no weld closes (two walls diverging at a shallow angle) is refused with
  its place: give both regions the same corners there, or leave a real wall. `check = true` runs the
  leak check after building (a sealed build, a few seconds): `tr.holes` counts the open edges near
  the plan (positions in `tr.hole_positions`, logged as a warning).
- **Materials**: `material = "concrete" | "metal" | "metal_floor" | "grate" | "rock"` (or any
  `kit.palette` theme or alias, or a `kit.palette{...}` record) on the call or on a region gives its
  floor, wall and ceiling textures; a region's own `material`, `texture` or `textures` wins. The plan
  is UV-aligned as a whole at the kit's 64 px/m, so textures run on across a region's pieces.
- **Dressing a trace**: every region is a kit zone. `tr.zone.<name>` (and the list `tr.zones`; a path
  is one corridor zone) is a room record with `min`/`max`, `floor_y`, `ceiling`, `storey`, `outline`,
  its air `brushes` and solid `fills`; pass it to `kit.light_room{room = tr.zone.hall}` or `kit.trim`,
  and pass the name to `kit.dress`, `kit.detail` and `kit.items` (`room = "hall"`). Per region,
  `tr.regions.<name>.walls` lists its solid wall spans `{a, b, length, floor, ceiling}`, `open_edges`
  the spans open to a neighbour `{a, b, to, drop}`, and `air` / `fills` its brushes. Each opening has
  `pos = {x, floor, z}`, `dir` (from `a` into `b`), `width` and `height` for a door frame. `tr.edges`
  lists the railing edges: `kind = "deck"` (a deck edge not against its host's wall or running on into
  a floor at its height; `below`, `drop`) and `kind = "ledge"` (a shared line between floors more than
  a step apart), each `{a, b, length, y}`. `tr.connections` lists every link between regions:
  `kind = "shared"` (open along a shared wall line) and `kind = "opening"` (a doorway from `openings`,
  with `opening` = its index, `at`, `pos`, `dir`, `width` and `height`).

```lua
-- sample: trace-storeys
local kit = ged.import("kit")
local tr = kit.trace{ name = "keep", check = true,
  storeys = { { name = "ground", floor = 0, ceiling = 4 }, { name = "first", floor = 4.5, height = 3.5 } },
  regions = {
    { name = "low", storey = "ground", poly = {{0, 0}, {16, 0}, {16, 10}, {0, 10}} },
    -- the upper storey runs on past the stairs' top step (stairs that end against a slab serve no route)
    { name = "high", storey = "first", poly = {{4, 0}, {16, 0}, {16, 10}, {0, 10}, {0, 6}, {4, 6}} },
    { name = "stairs", kind = "stairs", poly = {{0, 0}, {4, 0}, {4, 6}, {0, 6}}, floor = {0, 4.5}, climb = {0, 1}, ceiling = 8 },
    { name = "yard", kind = "yard", poly = {{16, 0}, {30, 0}, {30, 10}, {16, 10}}, floor = 0 },
    { name = "bridge", kind = "deck", poly = {{20, 0}, {24, 0}, {24, 10}, {20, 10}}, floor = 4.5 },
    -- a tunnel out of the yard that climbs 3 m round a bend
    { name = "tube", kind = "path", path = {{30, 5}, {38, 5}, {44, 11}, {44, 20}}, width = 4, floors = {0, 0, 3, 3}, height = 3.5 },
  } }
level.player_start{ pos = {8, 1, 5}, yaw = 90 }
return string.format("%d stacked, bridge in %s, stairs in %s, %d holes", #tr.stacked, tr.regions.bridge.hosts[1],
  tr.regions.stairs.hosts[1], tr.holes)
```

### Heights on the overhead: ramps, stairs and drops

A storey plan alone cannot tell a corridor that ramps up to the next storey from a flat floor that ends at a
cliff. When the reference has heights (a Glacier render with `--overlays …,heights`, an `overhead_heights.png`
made with `--mode heights`), read them before tracing:

- **Colour = height.** Walkable floor is tinted from blue (lowest) through cyan, green and yellow to red
  (highest); the legend's colour bar (or a strip under the plan) gives the range in metres. Floor far outside it
  (a pit below the play) keeps its texture or is grey, with its height label.
- **A ramp or slope** is a colour that changes gradually, with thin dark contour lines across it, white chevrons
  (`^`) pointing uphill and a label `low → high m`. Trace it as ONE `kind = "ramp"` region with `floor = {low,
  high}` and `climb` = the chevrons' direction, whatever its outline (it may widen as it climbs; see "Ramps of any
  shape" below). Never trace it as a flat floor ending at a ledge.
- **Stairs** are light tread lines across a flight with a white arrow uphill and the same label: a `kind =
  "stairs"` region.
- **A drop** is a heavy violet line with ticks on its LOW side and the drop (`↓ 3.5 m`): the two floors share a
  wall line but you cannot walk up (trace both floors touching along that line; `kit.trace` reports it as a
  `ledge` connection, and the detail pass rails it). No violet line between two floors of nearly the same colour
  means a walkable step.
- `+y` labels give each flat floor's height: use them for the regions' `floor`.

**`ref.heights` reads the picture for you.** On a calibrated heights picture (calibrate it from its scale bar, or
`mpp` = the render's `worldPerPixel`), `local h = ref.heights{legend = {min, max}, out = "heights_check.png"}`
returns:

- `grid`: the floor height of every cell (`values[row * cols + col + 1]`, `nodata` where no floor shows);
- `flats` `{name, poly, floor, area_m2}`, `ramps` and `stairs` `{name, kind, poly, floor = {low, high}, climb,
  rise, slope}` and `drops` `{points, high, low, drop}` (picture pixels);
- `regions`: the flats, ramps and stairs as `kit.trace` regions that share their seams, so
  `kit.trace{units = "px", regions = h.regions}` builds the floor plan with its heights straight away (add
  ceilings, doorways and walls from the other references).

Look at `heights_check.png` (every region outlined and named with its height, the drop edges) before you build.
A plan with the overlay over textures decodes too, but its dark wall cut lines (a lintel over a doorway or a stair
mouth included) separate regions like walls; `--mode heights` on the top view is the cleanest. Glyphs and labels over
the floor are skipped; pass `ignore = {{x, y, w, h}}` for a caption or panel in the way, and `crop` for one area.

**Hand-drawn mockups** have no decodable tint. Mark heights on them the same way so they read the same: draw an
uphill arrow along every ramp or stair flight with the height at each end (`0` at the foot, `3.5` at the top),
draw each drop as a line with short ticks on its low side and the drop beside it (`↓ 3`), and write `+y` on each
flat floor. Read those marks off `ref.grid` and give the regions their `floor`, `kind` and `climb` by hand.

#### Heights and storeys (from eye-level screenshots)

An overhead gives the footprint, not the heights. The screenshots give the heights. An area is done when
a render of it **looks like** its screenshot, so use this loop for every shot:

1. **Build the footprint first** (kit.trace with your best guess at the floors), then `ops.build()`.
2. **Find the camera.** `ref.shot_locate{shot = "s1.png", fov = 90, search = {box = {x0, z0, x1, z1}}}`
   ray-casts the build from walkable floor positions (eye 1.7 m up) and matches its edges against the
   shot's. It returns `best` and `poses` (`{x, y, z, yaw, pitch, fov, floor, score}`; yaw 0 = +Z,
   90 = +X), and `out` writes shot | build | edges. **Always give `search.box`** (the area the shot shows,
   read off the overhead, about 30 x 30 m) and, when you can tell, `search.yaw = {heading, 45}`. The
   whole-level search can lock onto a look-alike corridor elsewhere. Check the result by eye in `out`:
   the build panel must show the same space.
3. **Concept art and painted mockups.** Edges are unreliable on these, so set the pose by hand. Use the
   overhead to place the camera where the shot's vanishing lines meet the floor plan. A standing eye is
   1.7 m, so the horizon row is at eye height. Take the yaw from the room's visible walls, and the
   pitch from where the horizon sits (above the picture's centre = looking down). Then iterate
   `ref.shot_compare` until the two panels agree.
4. **Read heights.** `ref.shot_measure{pose = l.best, shot = "s1.png", px = {x, y}}` casts the ray
   through a shot pixel. `hit`, `height` (above the camera's floor) and `surface` tell you what the
   build has there. `distance = d` gives `at_distance`: how high that pixel row is at d metres
   (horizontal). Use it for a balcony edge or a ceiling line you can place on the overhead
   ("the gallery edge 8 m ahead is at row 160" gives its height). Change the trace's floors, ceilings
   and storeys to match.
5. **Compare.** `ref.shot_compare{shot = "s1.png", pose = l.best, out = "cmp_s1.png"}` renders the build
   **lit and textured**, as the game shows it, at the shot's aspect beside the shot. `edges = true`
   adds an edge-overlay panel (shot red, build cyan, both yellow).
   - `score` is the edge correlation; above ~0.35 the shapes line up on game renders.
   - `offset_px = {dx, dy}` is how far the build's edges sit from the shot's. Positive dy means the
     build reads lower: raise that geometry, or check the eye height.
   - Hosts without a GPU renderer fall back to a grey ray-cast panel (`render = "shaded"`).

```text
-- the height loop for one shot (Lua; needs your own screenshot, so it is not a runnable sample) (shot pixels read off ref.grid{path = "s1.png", ...})
local l = ref.shot_locate{shot = "s1.png", fov = 90, search = {box = {-30, -10, 0, 20}}, out = "loc_s1.png"}
local p = l.best
local edge = ref.shot_measure{pose = p, shot = "s1.png", px = {412, 160}, distance = 8}
print("gallery edge is", edge.at_distance, "m up; the build has", edge.height, edge.surface)
local c = ref.shot_compare{shot = "s1.png", pose = p, out = "cmp_s1.png", edges = true}
print(c.score, c.offset_px[1], c.offset_px[2])
```

### Organic rock and caves along a trace

Where the reference shows rock, trace it as rock. Do not stack `kit.trace` prisms or boxes to fake it: they read as
flat slabs. These options keep the footprint you traced and cover it with rough detail rock:

- `kind = "canyon"`: an open-sky region. Its walls are eroded rock in tiers that lean in or out 10 to 30 degrees,
  with buttresses and alcoves along them and scree on the ledges. They lean back above about a third of their
  height (`lean`, metres; only where no other region is near) and may overhang at the top (`overhang`). Its
  `height` / `ceiling` is the rim. Spurs and overhangs start above head height.
- `kind = "cave"`: rock walls, a rock haunch along every wall, and one continuous hanging vault with stalactite drops, plus
  `pillars = n` or a list of points.
- `kind = "path"` with `organic = true`: a rough tunnel along the centre line, with the same cave treatment on every
  segment.
- `walls = "rock"`, or `rock_edges = {i, ...}` on any region: those walls become rock. Edge i runs from point i to
  point i + 1 of the poly as given, so a yard can have one cliff side.
- `ground = "organic"` adds gentle mounds (`relief`); `ground = "terrain"` uses `kit.ground`. Doorways, openings and
  `keep_clear` routes stay level.
- `boulders = n` or `{density = per 100 m², size = {min, max}}` rests boulders at the walls' feet. `rock_detail = true`
  adds the kit's rock formations. Both keep clear of doorways, hosted ramps and stairs, and the trace's `keep_clear`
  polylines (for example a cart route).
- `kit.rock_mass{poly, y0, y1}` raises a mesa, outcrop or buttress from an outline: a talus apron (`talus`, metres),
  rounded strata that step in with ledges, and a rim of varied height. Use it instead of stacked prisms.

The rock is detail over the traced air, so the build stays sealed. The floor keeps the outline within 0.35 m (each
region's `rock.foot` reports it). Rock regions default to `material = "rock"`. Every piece is tagged `kit.zone`,
`kit.role` and `kit.purpose`.

```lua
-- sample: trace-organic
local kit = ged.import("kit")
local tr = kit.trace{ name = "gorge", seed = 2,
  keep_clear = { { {-10, 22}, {10, 22} } },                -- the cart route
  regions = {
    { name = "gorge", kind = "canyon", floor = 0, height = 14, overhang = 1.5,
      poly = {{-14, -12}, {6, -16}, {16, -6}, {14, 10}, {2, 14}, {-12, 8}} },
    { name = "yard", kind = "yard", floor = 0, height = 10, poly = {{-16, 15}, {16, 15}, {16, 35}, {-16, 35}},
      rock_edges = {3}, ground = "organic", boulders = 8 },
    { name = "cave", kind = "cave", floor = 0, height = 6, poly = {{30, -10}, {48, -12}, {52, 4}, {40, 12}, {28, 6}}, pillars = 2 },
    { name = "tun", kind = "path", organic = true, width = 4, floor = 0, height = 4, path = {{17, 0}, {22, 1}, {25, -3}, {27.5, -2}} },
  },
  openings = { { a = "gorge", b = "yard", at = {0, 14}, width = 4 }, { a = "gorge", b = "tun", at = {16, 0}, width = 3.5 },
    { a = "tun", b = "cave", at = {28.4, -2}, width = 3.5 } },
  check = true }
kit.rock_mass{ name = "mesa", poly = {{8, 28}, {14, 27}, {15, 32}, {9, 33}}, y0 = 0, y1 = 7 }
level.player_start{ pos = {0, 1, 0}, yaw = 0 }
return string.format("%d holes, gorge skin %d pieces, foot %.2f m", tr.holes, tr.regions.gorge.rock.skin, tr.regions.gorge.rock.foot)
```

### Traced builds are layout builds

A traced plan is built like a generated level; it just follows your design. `kit.trace` (default `via = "layout"`)
turns the regions into a `kit.layout` graph of literal zones and builds it with `kit.build_layout`. The footprints and
heights stay exactly as traced, and you get what a generated level gets:

- `kit.check_layout` runs first. `trace.layout_check.ok` must be true. `layout_check.flow` lists dead ends, separate
  parts and zones that are the only way on. Read it: a dead end you did not mean is a missing doorway.
- Regions that share a wall are joined; each traced doorway is a link at its traced place, and the detail pass
  frames it.
- Ramps and stairs that are rectangles along `climb` are built by `kit.ramp` / `kit.stairs`, with stringers and
  handrails. A flight that "serves no route" (it climbs into a wall, or its foot or top is blocked) is still built,
  but `trace.warnings` names it. Fix the plan: move the stairs, or open the wall it ends in.
- **Ramps of any shape.** A `kind = "ramp"` region may have any outline (a corridor that flares as it climbs, a
  curved or wedge-shaped landing): its floor is one plane from `floor[1]` along `climb` to `floor[2]` across the
  whole outline, its walls follow the outline, and when it stands on its own (not inside a room) its ceiling runs
  parallel to the floor at its `height` (default 4 m; an absolute `ceiling` keeps it flat). It joins the floors at
  its foot and top along the shared wall lines, stays sealed, and the layout check, the flight readings and
  `level.audit` see it as a flight between them. Use it for every ramp the heights picture shows, whatever its
  outline; `kind = "path"` is for ramps that bend along a centre line.
- The room detail pass adds wall trims, door frames, ceiling beams, and railings where decks and ledges drop away
  (`trace.detail` counts them). The layout lights each zone (`lights = false` when `kit.dress_trace` lights it).
- `trace.literal_notes` lists every place where the literal build differs from a generated one.

`via = "prism"` builds the bare plan only. Use it to debug a leak, never for a finished recreation.

### Match a reference screenshot

When the brief comes with concept art (screenshots of areas, a top-down map), look at it **during** the
build, not only at the start: for each area, render the level from the camera the screenshot was taken
from and compare the two pictures. Concept screenshots are usually far richer than a first blockout;
the comparison shows exactly where (height, openings, structure, detail, materials), so you fix the
biggest difference instead of guessing. The reference set is sidecar data (a folder of PNGs plus
`references.json`); the level is never changed by it.

Per area (CLI shown; the MCP tools are `reference_view` (view and list), `reference_edit` (`op = "add"`
and `op = "camera"`) and `reference_compare`, their reference set defaults to `references` in the
server's scratch folder, and the scale table is the resource `glacier://reference/scale`):

1. **Register the crop.** `glacier-cli ref view concept.jpg --out work/grid.png` draws the concept with a
   grid in its pixels; read the screenshot's rectangle, check it zoomed with
   `glacier-cli ref view concept.jpg --crop 10,126,208,112 --snap --out work/crop.png` (yellow: your
   box, green: snapped onto the panel's frame), then
   `glacier-cli ref add concept.jpg --refs work/refs --id yard --crop 10,126,208,112 --snap --area "The Yard" --notes "..."`.
2. **Read its cues.** `glacier-cli ref view yard --refs work/refs --out work/yard.png` shows it with a
   grid in fractions (0-1 from the top-left). Mark what you can see: the horizon (`--horizon 0.56`, where
   level floor lines meet, or two points `x1,y1,x2,y2`), the vanishing point of the main lines running
   away from the camera (`--vp 0.63,0.56`) and their world direction in your level (`--vp-dir 0,1` for
   +Z), a thing of known height standing on the floor (`--object x,base,top,metres`: a door is 2.5 m),
   spans to measure (`--measure x,base,top,...`), and what the level cannot show (`--ignore x,y,w,h`:
   the first-person weapon, a caption).
3. **Set the matched camera.** Stand where the screenshot's camera stood in your level (read the plan):
   `glacier-cli ref camera yard --refs work/refs --camera=-136,1.7,-146 --eye-height eye --apply-estimate`
   puts the estimated pitch and heading (and, with an object mark, the eye height) on it (write a value
   that starts with a minus sign as `--camera=-136,...`, or it reads as an option). The field of
   view defaults to 90° horizontal, about Red Faction's own; set `--hfov` when the shot is clearly wider
   or narrower. `--eye-height` stands the eye that high above the floor under the camera.
4. **Render and compare.**
   `glacier-cli ref compare yard work/level.rfl --refs work/refs --out work/yard_cmp.png --rf-dir <RF>`
   writes one sheet: reference | render on top, a 50/50 blend | an edge overlay below (orange =
   reference, cyan = render, white = both; the thick lines are each picture's skyline), all on one
   fraction grid with both horizons dashed. Look at it. `--json` adds the cues (detail density, plain
   top, skyline, brightness, warmth, horizons, edge overlap) and hints.
5. **Line the camera up before judging the level.** If the horizons or the main lines disagree, the
   camera is wrong, not the level: nudge it (`--move forward,right,up` metres, `--turn heading,pitch`
   degrees, in the camera's own frame; on `ref camera` they are saved, on `ref compare` they are
   one-off) until the floor line, the horizon and the main walls sit where the reference has them.
   If no spot in the level gives the reference's view, that is the first difference to write down.
6. **Write the difference list**, worst first, one line per heading, each with what to change:
   - *Massing*: which walls, roofs or volumes are missing, too low, too close or too far.
   - *Height*: storeys, walkways, ceiling height (measure with `--measure` and `ref scale`).
   - *Openings*: doors, arches, windows, gaps to the sky: where they are vs where they should be.
   - *Structure*: pillars, beams, trusses, gantries, railings, pipes the reference shows.
   - *Detail*: the density of panels, trim, props and clutter (the edge-density cue is the hint).
   - *Materials and light*: palette, warmth, hazard trim, lamp positions and colour.
7. **Fix the top items**, rebuild, and compare again from the same saved camera.

**Scale from cues.** `glacier-cli ref scale` (or `glacier://reference/scale`) lists sizes measured on the stock
levels: a door panel is 2.5 m tall (multiplayer doors 3 m) and 2 m wide, a doorway opening 2.25 m
wide, a corridor 4.75 m wide and 4.5 m high, a typical room 5 m high, a stair step 0.25 m by 0.75 m;
a player figure is about 1.8 m and the eye 1.7 m above the floor; a player jumps onto 1.33 m.
"The wall is about three doors tall" is `glacier-cli ref scale 3 door_height` = 7.5 m. Cross-check
with a second cue; with a matched camera, `--object` and `--measure` turn marks into metres directly.

**When to stop.** Stop on an area when the camera lines up (horizons and main lines agree) and your
difference list has no massing, height or opening items left, only style. Painted concept art never
matches a game render pixel for pixel; the cues are hints, not a score, so do not tune the level to
move a number. Re-compare every area after a change that touches it, and keep the sheets: they are
the evidence for the designer.

### Theme and dress from the shots

A traced shell with stock concrete everywhere is not a recreation: each area's shot has its own
materials, light colours and props. Carry them into the build with three readers and one dresser:

- `ref.match_textures{shot, crop = {x, y, w, h} | polygon = {{x, y}...}, n = 8, out}` ranks RED's stock
  level textures (the texture browser's categories) against one surface of the shot by Lab colour (as
  seen and white-balanced, so a lamp's colour cast matters less) and pattern (lightmap shading removed),
  at 0.5, 1, 2 and 4 repeats across the crop. It returns `matches = {{name, score, repeats}}`, `best` and a
  contact sheet. **Look at the sheet and pick by eye among the top 8**: the ranking is a shortlist, not an
  answer (on a lit render it put the true texture first for a marble pillar, but only in the top 8 for few
  other surfaces). Crop one flat surface, as square-on as the shot allows; `filter = "cem"` / `"mtl"` /
  `"rck"` or `category =` narrows the library. `repeats` gives the UV scale: a crop spanning S metres
  wants S / repeats metres per repeat.
- `ref.shot_lighting{shot, crop?}` returns `light_color`, `accent_color` (e.g. red emergency lamps),
  `intensity` (about 2.5 x the shot's mean brightness, 0.4-2), `ambient_color`, `fog_color` and a
  `summary` ("warm overall, mean 0.39: bright pale orange key 0.78 ..., dark black ambient 0.07").
- `kit.dress_trace{trace = tr, regions = {name = {...}}, ambient = L, fog = L}` dresses each traced region
  without moving a wall or floor: `floor` / `wall` / `ceiling` / `trim` textures (the region's pillars and
  deck edges take trim) with mapper UVs, `lighting = L` (kit.light_room in its colour and intensity plus
  accent lights), `details = {pillars, beams, pipes, railings (deck edges), frames (openings), machinery}`,
  `props = "industrial"` (a kit.dress theme) or `{{class = "Ultor Crate Small", count = 3}}`. `"*"` styles
  every region not listed. Detail and objects only: the level stays sealed. Failed parts are warnings.
  A region open to the sky is lit as one outdoor space along its traced outline (a few strong lights on its
  edges), however many air pieces it is built from. Do not dim outdoor regions afterwards to fix a glare:
  check `glacier-cli render --mode lightmaps` first, and lower `light.intensity` or drop `light.spacing`.
- `ref.side_by_side{shot, render, out}` puts the shot and a render of the build (glacier-cli render with
  the shot's camera) side by side with both lighting summaries; iterate until they read alike.

```text
-- theme and dress a trace from its shots (Lua; needs your own screenshots, so it is not a runnable sample)
local kit = ged.import("kit")
local tr = kit.trace{ name = "st", regions = {
  { name = "hall", poly = {{4, -36}, {24, -36}, {24, 9}, {4, 9}}, floor = -10, ceiling = 6 },
  { name = "bunker", poly = {{-12.5, -2}, {3.5, -2}, {3.5, 10}, {-12.5, 10}}, floor = -10, ceiling = -2 } },
  openings = {{ a = "bunker", b = "hall", at = {3.75, 3}, width = 3 }} }
local wall = ref.match_textures{ shot = "shot3.png", crop = {100, 270, 350, 90}, out = "wall.png" }
local L = ref.shot_lighting{ shot = "shot3.png" }
kit.dress_trace{ trace = tr, ambient = L, regions = {
  bunker = { wall = wall.best, lighting = L, details = { pipes = true, frames = true }, props = "industrial" },
  hall = { lighting = ref.shot_lighting{ shot = "shot2.png" }, details = { pillars = true } } } }
```

## 7. Discover; never guess names

Class names, field names, enum options and texture names vary. Look them up:

- `ged.describe("brush")` (any type or global name: `"level"`, `"object"`, `"mover"`, `"face"`,
  `"kit"`, …, or a handle) returns members, signatures, docs and the keys each table argument
  accepts (`member.options`); `.text` is a printable listing. From the shell,
  `glacier-cli api --member level.brush` prints the same entry without running anything.
- `ged.conventions`: the rules of section 5 in one text block.
- `catalog.kinds()`, `catalog.classes(kind)`, `catalog.describe(kind [, class])` (fields with type,
  options, range), `catalog.nearest(kind, name)`, `catalog.textures(filter [, limit])`.
- `catalog.norms(metric [, bucket])`: how released levels are typically proportioned (`p5`, `p25`,
  `p50`, `p75`, `p95`, `n`, `unit`), e.g. `catalog.norms("opening.width").p50` for a typical doorway
  width or `catalog.norms("corridor.width", "dm")` for deathmatch corridors; `catalog.norm_metrics()`
  lists the ids. Without a bucket you get the curated reference set the kit's defaults come from. Use
  the norms for proportions only; never copy a level. `glacier-cli norms --compare <rfl>` (MCP
  `compare_to_norms`) shows where a level sits.
- `x:fields()` on any object, brush, mover or `level.props` lists exactly what `get`/`set` accept.
- Errors point at the line of your script that made the call, name the nearest valid key or class,
  and carry a `hint`; read them, do not retry blindly. For example:
  `bad.lua:3: kit.corridor: unknown option 'widht'.  (Did you mean 'width'? Valid options: a, …)`

Run discovery scripts with `--dry-run` on a fresh level (nothing is written):
`glacier-cli run discover.lua --dry-run`.

```lua
-- sample: discover
print(assets.available, catalog.available)          -- is an RF install mounted?
print(ged.conventions)
print(ged.describe("brush").text)
print(table.concat(catalog.kinds(), ", "))
for _, f in ipairs(catalog.describe("Trigger").fields) do
  print(f.name, f.type, table.concat(f.options, "/"))
end
print(table.concat(catalog.nearest("Item", "shotgn", 3), ", "))   -- empty without an RF install
print(table.concat(catalog.textures("floor", 10), ", "))
local kit = ged.import("kit")
print(kit.help())                                   -- every kit function with its options
for k, v in pairs(kit.limits) do print("kit.limits." .. k, v) end
```

Fields use snake_case keys (the Properties-panel labels), enums take option names, colours
`{r, g, b}`. Set the deciding field first (a trigger's `shape` before its `box_width`); `set_many`
does this for you.

## 8. Editing existing levels

1. **Read first.** `glacier-cli summary <rfl> --json` (or `--attach`) and a plan render per storey.
2. **Query, do not loop over everything.** `level.objects_of(kind)` (the kind names `level.place`
   takes, such as `"light"` or `"respawn"`, or as `o.kind` spells them, such as `"MpRespawnPoint"`),
   `level.objects_named(name)`, `level.objects_tagged(k [, v])`, `level.objects_in{min =, max =}`,
   `level.brushes_in{…}`, `level.brushes_tagged(…)`, `level.find_uid(uid)`, `level.find_brush(uid)`,
   `level.find_mover(name)`, `level.find_group(name)`. Queries iterate directly:
   `for _, b in ipairs(level.brushes_tagged("kit.name", "hall")) do … end`.
   In visible mode, `selection.objects` and `selection.brushes` are what the designer has selected:
   "make these warmer" means the selection.
3. **Change through `get`/`set`/`set_many`** on objects, brushes, movers and `level.props`, and the
   bulk forms on queries (`q:set`, `q:move`, `q:tag`, `brush_query:set_textures`, `:box_map`, …),
   each one undo-safe.
4. **Never mass-delete or restructure unasked.** Deleting needs `--allow-destructive`, which you pass
   only when the designer asked for that deletion. Prefer adding and adjusting over replacing.

```lua
-- sample: edit-existing
local kit = ged.import("kit")
-- Setup so the sample runs on a fresh level; on a real level these already exist.
local hall = kit.room{ name = "hall", pos = {0, 0, 0}, size = {16, 5, 16} }
kit.light_room{ room = hall }

local lights = level.objects_of("light")
local hall_brushes = level.brushes_tagged("kit.name", "hall")
local centre = level.objects_in{ min = {-4, 0, -4}, max = {4, 5, 4} }
print(#lights .. " lights, " .. hall_brushes.count .. " hall brushes, " .. centre.count .. " objects near the centre")
for _, b in ipairs(hall_brushes) do print(b.uid, b.air and "air" or "solid", b.bounds.size) end

print(lights[1].display_name, lights[1]:get("size_range"))
level.objects_tagged("kit.name", "hall")
  :where(function(o) return o.kind == "Light" end)
  :set("color", {255, 220, 180})                    -- one bulk edit, warmer light

-- Links go from triggers, events, clutter and nav points to their targets.
local msg = level.place{ kind = "event", class = "Message", name = "hall_msg", pos = {0, 1, 6} }
local trig = level.place{ kind = "trigger", name = "hall_msg_trigger", pos = {0, 1, 4},
                          props = { shape = "Sphere", sphere_radius = 2 }, links = { msg } }
return "trigger links: " .. #trig:links()
```

### Payload setup (configuration, not layout)

An Alpine Faction Payload map is configured entirely through Lua (or `run_lua`). This is the
**wiring** the game checks, not a layout: where the track runs, how wide it is and where the teams
spawn are design decisions for the level, not something to copy from the sample.

- **Level settings:** `level.alpine` `legacy_movers = false` (a new level already has it off; Alpine
  does not run the cart with legacy movers). The payload rules are `payload_*` fields on
  `level.alpine` (push speed, rollback, acceleration, checkpoint bonus, heal, overtime grace, respawn
  delay modifiers); list them with `level.alpine:fields()`.
- **The cart** is a mover (`level.create_mover`) with `movement_type = "One Way"`, not Rotate in
  Place, and at least 2 keyframes: its keyframes, start first, are the track.
- **One `Payload_Handler` event** at the cart links the mover (the link is stored as the mover's start
  keyframe). Its fields: `payload_name`, `attacking_team` (Blue / Red), `push_radius`,
  `rollback_delay`, `no_healing_or_ammo`, `no_finale_explosion`.
- **Each `Payload_Checkpoint`** (`checkpoint_name`, `hold_seconds`, `time_bonus_seconds`) is named
  by a cart keyframe's `triggered_event_uid`; that is how the game puts it on the track. Never the
  start keyframe, and one keyframe per checkpoint. Link each checkpoint to the multiplayer respawn
  points that are live **while it is the cart's next target**: Alpine switches a checkpoint's
  respawns on while the cart heads for it and off once it is captured, and never switches a respawn
  that no checkpoint links (it stays on all game). So the first checkpoint links the starting spawns
  of both teams, each later one the attackers' forward spawns and the defenders' next hold, and every
  respawn belongs to exactly one checkpoint (the reference map links 8 per checkpoint, 4 red and
  4 blue). The final checkpoint's links count too.
- **Check it:** the linter reports every payload prerequisite Alpine checks when the map loads:
  category `AlpinePrerequisite` in `lint.run()`, `alpinePrerequisite` in `glacier-cli lint` and the
  `lint` tool.

```lua
-- sample: payload-setup
-- Configuration only: a single box room stands in for the real layout (which is a design question).
local kit = ged.import("kit")
local track = kit.room{ name = "track", pos = {6, 0, 24}, size = {24, 5, 56} }
kit.light_room{ room = track }

-- 1. Level settings: payload needs Legacy Movers off (a new level already has it off).
level.props:set("multiplayer", true)
level.alpine:set_many{ legacy_movers = false, payload_push_speed = 1.5, payload_checkpoint_time_bonus = 180 }

-- 2. The cart: a One Way moving group whose keyframes are the track, start first.
local route = { {0, 0.5, 4}, {0, 0.5, 20}, {12, 0.5, 32}, {12, 0.5, 46} }
local body = level.brush{ pos = route[1], size = {2, 1, 3}, detail = true, texture = "mtl_grittymetal.tga" }
local cart = level.create_mover{ name = "payload cart", brushes = { body },
                                 props = { movement_type = "One Way" } }
for i = 2, #route do
  cart:add_keyframe{ pos = route[i], props = { travel_time_to_next = 8, return_travel = 8 } }
end

-- 3. The handler sits at the cart and links the mover (stored as its start keyframe).
level.place{ kind = "event", class = "Payload_Handler", name = "payload", pos = {0, 2, 4},
             props = { attacking_team = "Blue", push_radius = 5 }, links = { cart } }

-- 4. Checkpoints: a cart keyframe names each one (triggered_event_uid); each links the respawns that
--    are live while the cart heads for it, 4 per team. Never on the start keyframe.
for n, k in ipairs({ 3, 4 }) do
  local kf = cart.keyframes[k]
  local cp = level.place{ kind = "event", class = "Payload_Checkpoint", pos = { kf.pos.x, 2, kf.pos.z },
                          props = { checkpoint_name = "Checkpoint " .. string.char(64 + n) } }
  kf:set("triggered_event_uid", cp.uid)
  local spawns = {}
  for j = 1, 8 do
    local red = j <= 4
    spawns[j] = level.place{ kind = "respawn", pos = { red and -4 or 16, 1, kf.pos.z - 10 + j },  -- 1 m above the floor
                             yaw = red and 90 or -90, props = { red_team = red, blue_team = not red } }
  end
  cp:link_many(spawns)
end

-- 5. Check: the linter mirrors Alpine's own cart checks (category "AlpinePrerequisite").
local issues = 0
for _, f in ipairs(lint.run().findings) do
  if f.category == "AlpinePrerequisite" then issues = issues + 1 print(f.message) end
end
return "payload prerequisites: " .. issues .. " issue(s)"
```

## 9. Verification checklist (before you hand back)

Run these on the final file (or with `--attach`), in this order, and report each result (the MCP
tool is in brackets):

- [ ] `glacier-cli holes <rfl> --fail-on-holes` exits 0 [`check_holes`: `watertight` is true]:
      **zero holes on the sealed build**. Fix any leak and check again before going on.
- [ ] `glacier-cli lint <rfl>` [`lint`]: 0 errors and 0 warnings, or each remaining finding explained.
      Info findings of category `outOfNorm` compare the level's proportions with the corpus norms
      (doorways, corridors, headroom, light density); they are design hints, never failures: mention
      them, change the level only when the brief allows.
- [ ] `glacier-cli light <rfl> --save` [`build{light = true}`, then `save_level`] succeeded with no
      error messages (read any warnings).
- [ ] A plan render of **every storey** reviewed (`--y <floor>`): rooms connect as intended, nothing
      sits inside solid, spawns and items are in open space, doors sit in their doorways.
- [ ] `summary --json`: `objects.spawns.playerStart` is true; enough multiplayer spawns (and red/blue
      ones for team modes); `links.broken` and `links.triggersWithoutLinks` are 0; `tagKeys` shows
      what you made (`ai.session`) and nothing else changed that you did not intend.
- [ ] Spawns have clearance: >= 1.5 m from walls and pillars, 1 m above the floor, >= 2 m headroom.
- [ ] Every room and corridor has light (a lit render shows no dark room).
- [ ] Doors: each has a trigger or buttons linked to its mover; say that in-game feel is untested.
- [ ] `glacier-cli grade <rfl>` [`grade_level`]: **PASS**, or each remaining REVIEW item listed with its
      reason (see "Self-review" below).
- [ ] `glacier-cli audit <rfl>` [`run_lua` with `return level.audit{}`]: **zero audit findings** (see "Craft audit" below). Never report done with findings left.
- [ ] Everything you placed is named, multi-brush parts are grouped, and the level is saved where the
      designer wants it.

You can also check inside a script, which is handy for generators:

```lua
-- sample: verify-in-script
local kit = ged.import("kit")
local r = kit.room{ name = "check_room", pos = {0, 0, 0}, size = {10, 4, 10} }
kit.light_room{ room = r }
local holes = ops.holes()                       -- always a sealed build
if not holes.is_watertight then
  ged.fail(holes.count .. " hole(s), first near " .. tostring(holes.positions[1]),
           "Look for rooms that do not meet their openings exactly.")
end
local report = lint.run()
for _, f in ipairs(report.findings) do print(f.severity, f.category, f.message) end
return string.format("holes %d, lint %d error(s) %d warning(s)", holes.count, report.error_count, report.warning_count)
```

### Craft audit

The craft audit is the gate for "done". It catches the mistakes a player notices at once, which a single matched
camera view hides. **Rule: zero audit findings before you report done.** If a finding is truly intended, name it
and give the reason in your report.

- **CLI:** `glacier-cli audit <rfl> [--json] [--out report.json] [--sealed] [--rf-dir <dir>]`. It exits 0 when
  clean and 1 when there are findings.
- **Lua:** `level.audit{checks?, max?, build?, timeout_s?}` returns `{clean, total, counts, findings, rock_regions,
  density_regions, notes, rules}`.
- **MCP:** `run_lua` with `return level.audit{build = "sealed"}`. The editor's Lint panel also shows the findings, as
  `Craft*` warnings (at most 20 per check). The automation `lint` request shows them only when you pass `craft = true`,
  so `glacier-cli lint` and `lint.run()` stay as they were.
- **Input:** the audit reads the compiled geometry (build sealed first) and the objects. It reads any level the same
  way: traced, generated or hand-made, tagged or not. There is no leniency for traced builds.
- **Output:** every finding has a `check`, a `kind`, a `pos`, a `uid` where there is one, and a `fix`.

| check / kind | what it means | fix |
|---|---|---|
| `props/sunk`, `floating`, `in_wall` | A clutter object, item, mesh or prop-sized detail box (not rock) is one of these: <br>• sunk: the floor is over 0.15 m above its real mesh base. <br>• floating: its base is over 0.3 m above the floor (items 1.2 m). <br>• in_wall: it pushes into a wall. <br>Bounds include the mesh's stored offset, so a stock barrel whose origin is its centre, placed at floor height, is half sunk. | Move it by the amount given. |
| `props/spawn_height` | A respawn or the Player Start is in solid or under 0.5 m above the floor (the `SpawnHeight` lint). | Stand it 1 m up. |
| `props/random` | The intent layer reads the prop as one of these: <br>• a rock on a built floor <br>• scattered rubble <br>• cover that blocks no sightline | Set it against a wall or edge, or on a sightline. |
| `props/in_doorway`, `on_stairs`, `lone` | A prop stands in a doorway or on a flight. Or a floor prop stands alone: no wall within 2.5 m and no other prop within 4 m. | Move or group it. |
| `stairs/<fault>` | A flight, ramp or bridge has a fault. The audit reads these from the geometry like `spatial.purpose{}`, so tags are not needed. The faults: <br>• `into_obstacle`, `blocked`, `onto_nothing` <br>• `in_gap` <br>• `low_headroom`: under 2 m past an end <br>• `purposeless_bridge` <br>• `route_ledge` | Give the flight a landing that leads on, with 2 m of headroom. Or remove it. |
| `perches/dead_end` | A flight leads to an empty perch or pit that only flights reach. | Join the perch to a route, give it a reason (an item, a sightline, a way down), or remove it. |
| `openings/into_nothing` | A doorway fails on one side. 1 m out, that side has solid, a wall, a drop over 1.5 m, no floor, or under 2 m headroom. The doorways read: <br>• an air cut through a wall, at most 8 m wide, with walkable floor in it, a jamb on both sides, and a wider space (a room or yard) beyond it on at least one side <br>• a door frame, each frame on its own (its `kit.opening` tag, else its touching pieces), at most 10 m across, with walkable floor at its sill <br>• a door mesh <br>The air slices of a cave, canyon or passage, and a corridor's segments, are not doorways. | Carve walkable floor on both sides, or remove the opening. |
| `purpose/purpose_failed`, `purpose_unread` | A piece built with `purpose = "off"`, `"quiet"` or `"report"` (tag `kit.purpose_mode`) still fails its purpose. Or the built level does not read it as a working route at all. | Rebuild it with `purpose = "require"`, or remove it. |
| `rock/flat_sided` | In a 16 m region, over 55% of the visible rock surface lies in large vertical planes: stacked prisms (`rough_prism`, extruded traces). Only rock a player can see counts: a face needs open air in front of it. The flat structural wall behind a detail rock skin, and the faces between, inside or behind rock pieces, are left out. `rock_regions` gives each region's `rock_area` (visible) and `hidden_area`. | Use the organic builders along the trace (see "Organic spaces"). |
| `density/basic` | In a 24 m region of playable floor, either: <br>• props and detail pieces number under 1 per 100 m² <br>• lights fall below the corpus p5 | Dress and light it (`kit.dress`, `kit.dress_trace`, `kit.light_room`). Vary floor and ceiling heights. |

**Roles:** every placed element needs a role. These are the role tags the audit reads:

| Tag | Values |
|---|---|
| `kit.role` | `route`, `cover`, `landmark` or `theme_detail` |
| `kit.purpose` | What the element serves |
| `kit.purpose_mode` | Written only when it is not `require` |

A piece built with `purpose = "off"` is not exempt. Once the level is built, the audit checks it like any other
piece.

- Use `f.uid` / `f.uids` to find what a finding is about: `rock/flat_sided` lists the brushes behind the flat
  sides (largest area first), `stairs/purposeless_bridge` lists the deck brushes. Inspect them with
  `glacier-cli inspect <uid>` or `level.find_brush(uid)` (objects: `level.object(uid)`).
- Check `a.complete` before trusting `a.clean`: if `a.skipped` is not empty, the walk-graph checks did not
  finish (raise `timeout_s` in `level.audit{}`, or use `glacier-cli audit`, which has no time limit). Never
  report "density 0" from an incomplete audit.
- Stacking is fine: props on detail tops (crate on crate, lamp on a trim or post) are supported; wall fixtures
  may hang flush on a wall or a curved silo. A crate-sized box (deeper than 0.6 m) in mid-air still floats. A
  prop under a balcony is measured to the floor it stands on.
- Ceiling lights count for the hall below them. Lintels, roofs, pipes and beams are not read as bridges: a
  real deck must be at least 1 m level across and reachable on foot.

### Self-review: grade, fix by location, grade again

A level that builds and lints clean can still be a poor layout: too big for its spawns, a tree of dead
ends, one long sightline, bare floor, no items near the spawns. The **design grader** measures that from
the compiled geometry, with the same code the benchmark uses, and checks it against limits calibrated on
curated reference levels. Run it after every generation pass:

- `glacier-cli grade <rfl> [--gametype dm|ctf|koth|payload] [--players <n>]` [`grade_level`], or
  `design.grade{}` in Lua. The verdict is **PASS** (at most 2 failing checks), **REVIEW** (3-4) or
  **FAIL** (5 or more, or a hard check: too simple, samey-confusing, or too big and open).
  `--fail-on review` exits 6 when the verdict is REVIEW or worse.
- Every failure lists the measured `values`, a `fix` naming the kit helper that addresses it, and
  `locations` to act on, worst first: chokepoint cell centres, pocket centroids, bare and undressed
  8 m cells, the longest sightlines (from, to), exposed spawns (and the spawns they see), items with
  their nearest-weapon distance, spawns far from any item. Fix **there**, not everywhere.
- Open-air / floating-platform maps and payload maps are detected and graded with their own limits
  (`class` in the result); payload maps also get per-segment flow and population checks, at least 3
  routes into every next segment, and a spawn hand-off at every checkpoint.
- The result says which geometry it measured (`geometry.state`: built, preview, stale, none). A
  missing or stale build is measured on a private preview build; build sealed before the final grade.
- **Playable floor** is floor a player can stand on and get to: prop-sized tops (16-80 m²) that can only be
  dropped off (2.5 m or more: a container stack, a hut roof) and strips under about 0.6 m across (wall trim,
  rails, pipe and beam tops) do not count, and sightlines and exposure (G02, G20) start only where a standing eye
  is below the ceiling. Their sample points are fixed by floor position, so an edit moves G02 and G20
  only through the floor it changes; a change of a few hundredths after adding cover is the cover's effect.
- The advisories compare a payload map's playable floor and floor per spawn with the Payload layout budget
  (12,000-24,000 m², 180-800 m² per spawn), the rest with its player class. P03 lists each segment's
  dead-end pockets (`segment_pockets`: position, segment, area, share) before the segments themselves.
- The set-piece line names the **landmark bridge** (span times depth, weighted by deck width: a road bridge
  over a chasm outranks a long catwalk a storey up) with its span, gap, depth and width, then the longest
  span. A span is the deck end to end, never into a corridor it runs on into; rails along a deck do not
  split it, and decks up to 8 m wide count.
- The measurements are also queries of their own (the `spatial_query` tool, `glacier-cli spatial`, Lua `spatial.*`):
  `walk` (floor reachable on foot, by lift, jump pad or teleport; unreachable spawns and items),
  `routes` (node-disjoint routes between two points), `graph` (loops, chokepoints, pockets),
  `sightlines`, `cover`, `cells`, `area_report` (an area against the targets), `spawn_report`,
  `item_report`, `locations`, and the instant `raycast`, `los`, `point_info` and `clearance`.

The loop: **generate, grade, fix by location, grade again**, until PASS. Hand back with the verdict and,
for any REVIEW item you keep, its reason.

**What the grader calls a space** (G06 median space size, G09 archetype mix, the space graph). Playable
floor is split by clear width at chest height: floor under 4 m wide, and corridor-shaped floor (at most
8 m wide and running on for at least 2.5 times its width), is a *connector*; the rest is *space*, and
pieces of space under 16 m² count as connector. Neighbouring floor of the same kind joins into one space,
so a corridor whose last metres read as room-shaped (a short leg, a bend, a wide mouth) and that opens at
full width into a room becomes part of that room's space. A doorway or door stub narrower than 4 m (a
`kit.doorway`, or a 3 m wide, 3 m long `kit.passage` stub at each corridor end) always separates the two,
and so does a junction room at a corridor bend. G06 takes the median area of spaces of at least 30 m²
(connectors excluded). `glacier-cli spatial graph <rfl> --kind space` lists the spaces with their areas
and archetypes, so you can see which rooms merged.

**Moving the median (G06).** A median moves only when spaces cross it: it falls to the limit (260 m²)
once the spaces of 30-260 m² outnumber the larger ones. Add side rooms, alcoves and galleries of
30-260 m² set off by openings under 4 m wide, or split a large space so that **every** piece is under
the limit (one such split into two counts as three added spaces). Splitting it into pieces that stay
over the limit raises the median, and pieces under 30 m² do not count. A failing G06 carries a `detail`:
every counted space with its area, location and floor box (largest first, `*` marks the ones nearest
the median in text output) and `spaces_needed`, the number of 30 m² spaces that would bring the
median to the limit.

**Floor-height classes** (G08, G24 and the player-class advisory). Playable floor is binned by height in
0.5 m steps on world multiples of 0.5 m (y 4.0 to 4.49 is one bin); a bin is a class when it holds at
least 2% of the playable floor (about 125 m² on a 6,000 m² level). A small raised or sunken piece adds a
class only if the floor in its own bin reaches that area (floor at the same height anywhere in the level
adds up), and stairs and ramps spread over many bins without filling any. The advisory's `detail` lists
the bins that count and the ones closest to counting with how many m² each is short.

**Sky.** `spatial area_report`'s `outdoor_fraction` and each space's `sky` are the share of floor under
open sky **or a sky face** (show_sky): a `kit.canyon`, `kit.road` or open yard reads as sky.
`open_sky_fraction` is the share with no ceiling at all, which a sealed level almost never has; the space
archetypes' `sky` height class reads that stricter share (the exemplar study's definition).

**Payload spawn safety (G19).** On a payload map a respawn a checkpoint links is live only while that
checkpoint is the cart's target (one no checkpoint links is always live), so G19's pair share counts only
pairs live at the same time (`spawn_los30_live`; `spatial spawn_report` shows both shares). Other
gametypes count every pair.

```lua
-- sample: self-review-loop
local kit = ged.import("kit")
local a = kit.room{ name = "west", pos = {0, 0, 0}, size = {16, 6, 16} }
local b = kit.room{ name = "east", pos = {30, 0, 0}, size = {16, 6, 16} }
kit.corridor{ a = a, b = b, width = 4.5, offset = 4 }
kit.light_room{ room = a }
kit.light_room{ room = b }
for i, x in ipairs({ -4, 4, 26, 34 }) do
  level.place{ kind = "respawn", name = "spawn" .. i, pos = { x, 1, -5 } }  -- spawns stand 1 m above the floor
end
ops.build()

-- 1. Grade: the verdict, then each failing check with its fix and where to act.
local before = design.grade{ gametype = "dm", players = 4 }
for _, f in ipairs(before.failures) do
  print(f.id .. ": " .. f.fix)
end

-- 2. Fix by location: every spawn the grader lists as far from an item gets one beside it.
for i, s in ipairs(spatial.locations("spawns_far_from_items").locations) do
  level.place{ kind = "item", class = "Medical Kit", name = "spawn_medkit" .. i,
               pos = { s.pos.x + 1.5, kit.defaults.item_height, s.pos.z } }
end
ops.build()

-- 3. Grade again and compare.
local after = design.grade{ gametype = "dm", players = 4 }
return string.format("%s (%d failing) -> %s (%d failing)", before.verdict, before.failures_count,
                     after.verdict, after.failures_count)
```

#### Worked example: brief to graded level, end to end

One pass of the whole loop on a small brief, "a small concrete-bunker deathmatch for 4 players": the layout
graph, its check, the build, population, the sealed build and its holes, lint, the grade, a fix at each
location the grade names, and the grade again. The steps and their command-line equivalents (the MCP tool
is in brackets):

1. **Graph and check** (`kit.layout`, `kit.check_layout`): stop on any `"error"`; a `"rule"` violation is a
   design warning to fix in the graph or explain.
2. **Build** (`kit.build_layout`): the zone reports carry each zone's box, floor spots and dressing density.
3. **Populate** with the kit's population functions, by their public names: `kit.items` (the item economy),
   `kit.dress` (cover, clutter, decals), `kit.spawn_points` and `kit.light_room` where the layout did not
   place them. Give each a `seed`, so a re-run reproduces the level. Dressing a whole level in the same run
   as its layout can exceed the script's instruction budget: dress zone by zone (`area = {min, max}` from
   the zone reports), or populate in a second `glacier-cli run` on the saved level.
4. **Watertight, then lint**: `ops.check_holes()` (a sealed build) must be 0 before anything else
   [`check_holes`; `glacier-cli holes <rfl> --fail-on-holes`], then `lint.run()` with no errors or warnings
   [`lint`; `glacier-cli lint <rfl>`].
5. **Grade**: `design.grade{}` [`grade_level`; `glacier-cli grade <rfl> --gametype dm --players 4`].
6. **Fix by location**: walk each failure's `locations` and act at that point only: an item beside a spawn
   that is far from items (G15), dressing in a bare or undressed 8 m cell or where cover is far (G16, G18,
   G20). A structural failure (space size G06, loops G14, chokepoints G12) goes back to the layout graph
   (another seed, a split zone, an extra link) or into the hand-off with its reason.
7. **Grade again** and repeat 6-7 until PASS; hand back the verdict and every kept failure with its reason.

The same loop from the command line is one `glacier-cli run brief.lua --out work/bunker.rfl` for steps 1-4,
`glacier-cli grade work/bunker.rfl --gametype dm --players 4 --json` for step 5, a second script run on the
saved level (`glacier-cli run fix.lua work/bunker.rfl --save`) for step 6, and the grade again.

```lua
-- sample: self-review-worked
-- Brief: "a small concrete-bunker deathmatch for 4 players".
local kit = ged.import("kit")

-- 1. Brief -> layout graph -> check. An "error" cannot be built: fix the graph and check again.
local g = kit.layout{ gametype = "dm", players = 4, seed = 7, brief = "concrete bunker deathmatch" }
for _, v in ipairs(kit.check_layout{ graph = g }) do
  if v.severity == "error" then error(v.message) end
end

-- 2. Build it: shells, links, interiors, lights and spawns, in CSG time order.
local rep = kit.build_layout{ graph = g, name = "bunker" }

-- 3. Populate with the kit's public functions: the item economy over the level, then dressing in the
--    two biggest zones (the grade below says where more is needed).
kit.items{ gametype = "dm", seed = 7 }
table.sort(rep.zones, function(a, b)
  return (a.max.x - a.min.x) * (a.max.z - a.min.z) > (b.max.x - b.min.x) * (b.max.z - b.min.z)
end)
for i = 1, 2 do
  local z = rep.zones[i]
  kit.dress{ area = { min = z.min, max = z.max }, theme = "control", density = z.dress.density, seed = i }
end

-- 4. Watertight first (a sealed build), then lint: no errors and no warnings.
local holes = ops.check_holes()
if holes > 0 then error(holes .. " hole(s): fix the leak before anything else") end
ops.build()
local problems = 0
for _, f in ipairs(lint.run().findings) do
  if f.severity ~= "info" then problems = problems + 1 print(f.severity, f.category, f.message) end
end

-- 5. Grade: the same check as `glacier-cli grade <rfl> --gametype dm --players 4`.
local before = design.grade{ gametype = "dm", players = 4 }

-- 6. Fix by location: act where each failing check points, not everywhere.
local fixes, kept = 0, {}
for _, f in ipairs(before.failures) do
  local acted = false
  for _, loc in ipairs(f.locations) do
    local x, y, z = loc.pos.x or loc.pos[1], loc.pos.y or loc.pos[2], loc.pos.z or loc.pos[3]
    if loc.kind == "spawns_far_from_items" then
      -- G15: an item beside the spawn (the spawn point stands 1 m above its floor).
      level.place{ kind = "item", class = "Medical Kit", name = "fix_medkit" .. fixes,
                   pos = { x + 1.5, y - kit.defaults.spawn_height + kit.defaults.item_height, z } }
      fixes, acted = fixes + 1, true
    elseif loc.kind == "undressed_cells" or loc.kind == "bare_cells" or loc.kind == "far_from_cover" then
      -- G16 / G18 / G20: dress that 8 m cell (cover, clutter, decals).
      kit.dress{ area = { min = { x - 4, y - 0.5, z - 4 }, max = { x + 4, y + 5, z + 4 } }, theme = "control", seed = 10 + fixes }
      fixes, acted = fixes + 1, true
    end
  end
  -- A structural failure (space sizes, loops, chokepoints) goes back to the layout graph, or into the
  -- hand-off with its reason when the brief wants it that way.
  if not acted then kept[#kept + 1] = f.id end
end
ops.build()

-- 7. Grade again and compare; repeat 6-7 until PASS.
local after = design.grade{ gametype = "dm", players = 4 }
return string.format("%d zones, %d holes, %d lint problems; %s (%d failing) -> %d fix(es) -> %s (%d failing); kept for the hand-off: %s",
  #rep.zones, holes, problems, before.verdict, before.failures_count, fixes, after.verdict, after.failures_count,
  #kept > 0 and table.concat(kept, ", ") or "none")
```

## 10. Pitfalls

- **Holes only mean something on a sealed build.** `holes`, `ops.holes()` and `ops.check_holes()`
  always seal; a preview build (the default `build`, and renders) has open edges by design. `lint` on
  preview geometry skips the leak check and says so.
- **Do not "fix" a hole by overlapping coplanar faces** or stacking extra brushes over it. Find why
  two spaces do not meet (a gap, a mis-sized brush, time order) and fix that. Kit openings abut rooms
  exactly; keep hand-made brushes on the same grid.
- **Time order:** a solid created before the air around it gets carved away. Create rooms first.
- **Re-runs stack.** Running the same script twice on the same level adds everything twice. Regenerate
  from a fresh level headless; apply once in visible mode.
- **Determinism:** use `rng` (seed with `rng.set_seed(n)`) for variation, and never let the order of
  `pairs` decide layout. Same script + same level = same result.
- **Sandbox:** no `io`, `os`, `require`, `pcall` or file access. Load the kit with `ged.import`.
  Split a long build into files with `ged.import("graph")`: a script run from a file
  (`glacier-cli run build.lua`, MCP `run_lua{file = ...}`) imports the `.lua` files beside it that it
  names with a literal string (that folder only, no sub-folders or parents; `--attach` runs carry them
  to the editor), after the modules shipped with Glacier and before the scripts folder's `lib/`.
  Fail on purpose with `ged.fail(message, hint)`; any error rolls the whole run back. To recover
  from an error instead, use `ged.try(fn, ...)` (section 6, "Retry a seed"): it undoes only what
  `fn` changed. `pcall` is not there (calling it fails with "attempt to call a nil value"); `ged.try` is
  the pcall of the sandbox: `local ok, r = ged.try(kit.rock_mass, {...})`, then read `r` as the error
  message when `ok` is false.
- **A `local` declared without a value starts as nil**, as in standard Lua (`local found` before a search
  loop). Glacier rewrites such declarations before running a chunk, because the Lua engine it uses kept a
  stale value there inside `for ... in` loops; a clustering loop that appended to "found" then walked its own
  list forever (an hours-long build). Older Glacier builds need `local found = nil`.
- **Lightmap surface cap.** A level holds at most 32,767 lightmap surfaces (Alpine RED's limit); every rock
  facet a player sees takes one. `level.stats()` gives `faces`, `surfaces`, `surface_cap` and
  `unlit_faces`; `ops.build()` / `ops.light()` reports carry `surfaces`, `surfaces_wanted` and
  `unlit_faces`; the build, the save and `lint` warn with the count and where. Faces nobody can see
  (inside neighbouring rock chunks or the wall) give up their surfaces first when a level would pass the cap.
  The kit's organic rock spends from one level-wide budget (`kit.defaults.organic_budget`, 14,000 seen
  facets; `organic_view` 5 m of full facets over the floor, `organic_far` 3x coarser above); railings,
  silos and other round detail are not in it, so thin those out when `unlit_faces` is not 0.
- **Stock clutter meshes have their origin at their centre:** `level.place{kind = "clutter", ...}` at a floor
  point sinks half the mesh (an Ultor crate about 0.46 m). Pass `seat = true` to stand it on that point
  (needs the RF install for the mesh bounds).
- **`kit.dress_trace` lights only regions that ask:** a region given only textures, details or props gets no
  lights or fixtures; one given `lighting =` (a `ref.shot_lighting` result) gets key lights unless it already
  holds lights (yours, a plan's, an earlier `kit.light_room`); `light = {...}` always adds them. Indoor key
  lights never hang lower than 3 m (or 60 % of the room height) over the floor.
- **Time limit** (`--timeout`, default 30 s) counts Lua time only; builds, bakes and hole checks do not
  count. A long Lua loop over thousands of items is the usual cause of a timeout.
- **Instruction budget** (`--budget`, default 200,000,000 Lua instructions; MCP `run_lua`: `budget`; 0 =
  none). The error names the line it stopped at. The kit's population calls (`kit.dress`,
  `kit.spawn_points`, `kit.items`) run natively, so dressing, spawns and items for a whole level fit in
  one run; your own loops over every brush or object are what use the budget.
- **Prefer bulk operations** (`brush_query:set_textures`, `object_query:set`, `level.brushes_in`) to
  per-item loops on big levels; each handle call crosses into the editor. `level.brushes_in`, `b.bounds`,
  `b:contains`, `b:tags()` and uid lookups are indexed (cheap on levels of ten thousand rock brushes), but a
  Lua loop that compares every piece with every other is still quadratic: cluster with `level.brushes_in`
  around each piece instead.
- **`light --no-shadows` (or `ops.light(false)`, MCP `build`/`light` with `shadows = false`) is RED's quick
  no-shadow bake:** no occlusion at all, so every light reaches every surface in its range through walls.
  Interiors next to a lit yard or under many lamps bake to full strength or brighter (white in
  `--mode lightmaps`). Use it only to check light placement and coverage; run a full `light` before judging
  brightness, dark areas or mood, or before `ref.shot_compare` and lighting matches (test_G1: 477 s full
  against 86 s without shadows).
- **The sky room renders full-bright in lit views,** as in RF and Alpine (the sky texture alone: no lightmap,
  no fog). Do not put lights in the sky box or raise the ambient to brighten the sky; pick a brighter or
  darker sky texture instead.
- **`level.object(uid)`** returns the object an audit finding, a report or a log names (`level.find_brush`
  for brushes).
- **Look textures up** with `catalog.textures(filter)` before using a name; an unknown texture
  renders white. `catalog.textures(filter)` returns names (strings); `catalog.textures{...}` and
  `o:fields()` return records, which `table.concat` cannot join: use `ged.names(list)` for plain strings.
- **Never move single vertices** to roughen a brush; it stops being convex and flat-faced. Use
  `geo.jitter` or build it with `level.brush_from_points`. For many rocks use `kit.boulders` /
  `kit.rock_wall` or one `level.brushes_from_points` call, not a loop of `level.brush` calls.
- **Saving:** `ops.save()` inside a script and overwriting files need `--allow-destructive`; prefer
  `--save` / `--out` on the command line. Every save also writes `<level>.gedlayout.json` beside the
  `.rfl`: Glacier's editor-only sidecar (Link Graph node positions, measurement annotations, and the
  level's own lightmap method when it overrides the global setting). The game never reads it and
  packfiles leave it out; keep it next to the level to keep those editor settings, and otherwise ignore it.

```lua
-- sample: deterministic-rng
local kit = ged.import("kit")
rng.set_seed(20261112)                          -- same seed, same crates, every run
local yard = kit.room{ name = "yard", pos = {0, 0, 0}, size = {20, 5, 20} }
local crates = {}
for i = 1, 6 do
  local angle = math.rad((i - 1) * 60 + rng.range(-15, 15))
  local dist = rng.range(4, 8)                  -- keeps the Player Start at (0, 1, 0) clear
  crates[i] = level.brush{ shape = "box", pos = {dist * math.sin(angle), 0.6, dist * math.cos(angle)},
                           size = {1.2, 1.2, 1.2}, detail = true, texture = "mtl_conveyor_wall01.tga", tags = { area = "yard" } }
end
level.create_group{ name = "yard_crates", brushes = crates }
return "seed " .. rng.seed .. ": " .. #crates .. " crates"
```

## 11. Worked example: a two-room deathmatch level

The designer asks for "a small two-room deathmatch map with a corridor, a couple of pillars,
spawns and a shotgun". Write `twin.lua`:

```lua
-- sample: twin-arena
ged.require_api(2)
local kit = ged.import("kit")
level.props:set_many{ level_name = "Twin Rooms", multiplayer = true, ambient_color = {50, 50, 50} }

-- Air first: two 14 x 5 x 14 m rooms 6 m apart, joined by a corridor (default 5 x 4.5 m).
local west = kit.room{ name = "west", pos = {-10, 0, 0}, size = {14, 5, 14} }   -- x -17..-3
local east = kit.room{ name = "east", pos = { 10, 0, 0}, size = {14, 5, 14} }   -- x 3..17
local link = kit.corridor{ name = "link", a = west, b = east }                    -- x -3..3, z -2.5..2.5

-- Solids after the air: two pillars that break the east room's sight lines.
kit.pillar_row{ name = "east_pillars", from = {7, 0, 0}, to = {13, 0, 0}, count = 2, height = 5 }

-- Light every space, spawns on a ring in each room, items, Player Start facing the corridor.
for _, r in ipairs{ west, east, link } do kit.light_room{ room = r } end
kit.spawn_points{ room = west, count = 4 }
kit.spawn_points{ room = east, count = 4 }
level.place{ kind = "item", class = "Shotgun",     name = "item_shotgun", pos = {10, 0.5, 0} }
level.place{ kind = "item", class = "Medical Kit", name = "item_medkit",  pos = {-12, 0.5, 3} }
level.player_start{ pos = {-10, 1, 0}, yaw = 90 }

local holes = ops.holes()
return string.format("twin: %d brushes, %d objects, holes %d", level.brush_count, level.count, holes.count)
```

Run it headless, look, check, finish (paths and the session id shortened):

```powershell
PS> glacier-cli run twin.lua --out work\twin.rfl
[info] No holes: geometry is watertight.
[info] Applied: 1 level property, 34 placed, 5 uv, 1 grouped, 29 edited, 7 rotated, 1 moved — 1 undo step.
Ran twin.lua: 78 changes (undo: "CLI: twin.lua").
Returned: twin: 5 brushes, 30 objects, holes 0
Created 34: UID 1, 2, 3, …, 20, … 14 more (use --json for all); tagged ai.script=twin.lua, ai.session=a636fbc1649e
Saved …\work\twin.rfl [Alpine v306]
  Geometry was built (sealed) for the save; lighting is not baked — build --light and save again for lit surfaces.

PS> glacier-cli render work\twin.rfl --out work\twin_plan.png
Rendered …\work\twin_plan.png (1024x1024, plan at y=0 (slice -1..2, floor under the player start), 0.0365 m/px, mode textured)
  world = origin + (px + 0.5) * pixelAxes.x + (py + 0.5) * pixelAxes.y; image right = +X, image up = +Z; x = -18.7 + (px + 0.5) * 0.03652344, z = 18.7 - (py + 0.5) * 0.03652344
  note: … The level has no lightmaps (not lit yet): drew textures only.
  30 object glyph(s) drawn (pixel positions are in the --json output).
```

Open `work\twin_plan.png`. Check: two rooms joined by a corridor along z = 0, two dark pillar
squares in the east room with no glyph on them, four purple spawns around each room's edge with
their ticks pointing inward, `START` in the west room, nineteen light stars, a yellow diamond between
the pillars and one in the west room. If something is off, fix `twin.lua` and run it again (the
`--out` file is regenerated).

Finish in the section 3 order: the `--out` save already made a sealed build, so check it for holes,
lint it, and only then bake the lighting and save.

```powershell
PS> glacier-cli holes work\twin.rfl --fail-on-holes
No holes: the sealed build is watertight.

PS> glacier-cli lint work\twin.rfl
Lint: 0 error(s), 0 warning(s), 1 info
  info outOfNorm: Clear width of doorways and other narrowings between spaces: typically 0.88–4.02 m in reference levels (corpus p5–p95); this level's median is 4.98 m.

PS> glacier-cli light work\twin.rfl --save
Built (sealed + lit, Shared BSP): 1 rooms, 0 subrooms, 0 portals, 40 faces, 60 vertices, 22 surfaces, 1 lightmap pages in 611 ms
Saved …\work\twin.rfl [Alpine v306]

PS> glacier-cli summary work\twin.rfl --detail brief
Level:   twin.rfl (RFL v306, Alpine)
Bounds:  (-17, 0, -7) .. (17, 5, 7)  size (34, 5, 14)
Brushes: 5 (2 solid, 3 air, 0 detail, 0 portal, 0 geoable)
Objects: 30 (2 Item, 19 Light, 8 MpRespawnPoint, 1 PlayerStart)
Spawns:  player start yes, 8 multiplayer spawns (8 usable by red, 8 by blue, 0 by bots)
Links:   0 (0 broken), 0 events (0 unlinked), 0 triggers (0 without links)
Rooms:   1 (1 main, 0 sub, 0 portals) — geometry built
Lint:    0 error(s), 0 warning(s), 0 info
Tags:    brushes (ai.script 5, ai.session 5, kit 5, kit.name 5, kit.part 5); objects (ai.script 29, ai.session 29, kit 27, kit.name 27)
```

The one info finding is a corpus-norms hint, not a failure: the corridor's 5 m mouth is wider than most
doorways in the reference levels. Mention it; narrow the join (a `kit.doorway`, or `width = 4`) only if
the brief wants tighter connections.

A spawn open to both teams counts once in the total and once for each team that may use it (here all
eight serve both teams); `objects.spawns.bothTeams` in the `--json` output gives that number.

Then render the lit result once more (`--mode lit` is the default) to confirm no room is dark, and
hand over: the file path, what was built (2 rooms, a corridor, 2 pillars, 9 lights, 8 spawns,
2 items, all named and tagged), the checks above with their results, and what is untested (in-game
play). On Linux the commands are the same with `/` paths:

```sh
glacier-cli run twin.lua --out work/twin.rfl && glacier-cli render work/twin.rfl --out work/twin_plan.png
```

To do the same **visibly**: the designer opens Glacier (File ▸ New, or `glacier-cli new --attach`),
then `glacier-cli run twin.lua --attach`, `render --attach --out work/twin_live.png`,
`holes --attach`, `light --attach`, and the designer saves (or `glacier-cli save --attach
--out <RF install>\user_maps\twin.rfl`, a folder attach mode may write to). If the run needs
redoing, `glacier-cli undo --attach` takes it back first.

**With the MCP tools** the same job is a sequence of tool calls (headless here; attached, skip
`new_level` when the designer already has the level open):

1. `new_level`, then `run_lua{name = "twin.lua", code = <the script above>}`. The result lists what
   was created and the returned line (`twin: 5 brushes, 20 objects, holes 0`).
2. `render_plan` and look at the image it returns; check the same things as above. Fix the script,
   `undo`, and run it again if something is off.
3. `build` (sealed), then `check_holes`: `watertight` must be `true` (otherwise read `positions`,
   `render_plan` with `holes` added to its `overlays` to see them, fix, and check again).
4. `lint`: `errors` and `warnings` must be 0, or each finding explained.
5. `build{light = true}`, then `render_plan` once more (it draws lit when lightmaps exist) to see no
   room is dark.
6. `save_level{out = "work/twin.rfl"}` (a path inside the session's write folders; `session_info`
   lists them). `rflVersion` in the result is the version written (306).
7. `level_summary{detail = "brief"}` for the hand-over facts, then report as above.

## 12. Reference

- `glacier-cli api`: the full Lua API reference for this build (every global, handle type, member,
  table option and kit function, plus the conventions), generated from the API itself.
  `--member <name>` prints one entry (`level`, `level.brush`, `brush:set_pos`, `kit.room`); `--lua`
  prints the Lua stub; `--json` wraps either for tools.
- `scripts/api/ged.lua` (beside Glacier): a Lua stub of the globals for editor completion.
- `scripts/lib/kit.lua`: the design kit source. `scripts/examples/kit-tiny-dm.lua`: a three-room
  example with a button door, stairs, pillars, lights, spawns and items; `kit-canyon.lua` and
  `kit-cave.lua`: an outdoor canyon and a cave network built with the organic functions.
- `glacier-cli help <command>`: every command's options.
- In Glacier: *Help ▸ Help Topics*, sections "Scripting (Lua)", "Command line" and "AI-assisted level
  design".
