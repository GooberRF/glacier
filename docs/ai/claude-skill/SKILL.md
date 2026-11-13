---
name: glacier-level-design
description: Build, edit, check and render Red Faction (2001) levels (.rfl) with the Glacier level editor through its MCP server tools or its glacier-cli command-line tool, and its Lua scripting API. Use when the user asks to design, block out, generate, modify, light, populate, verify or render a Red Faction / Alpine Faction map, or mentions Glacier, glacier-cli, RFL files, or driving the open Glacier editor.
---

# Glacier level design

You drive Glacier through its MCP tools when they are configured, otherwise through `glacier-cli`:
write a Lua script, run it against a level (headless on a file, or in the editor the designer is
watching), render a floor plan and look at it, measure with the level summary, fix, then finish
with a sealed, lit, hole-free, lint-clean build.

**Before starting, read `AGENT-GUIDE.md` in full.** It is the source of truth; this file is only a
pointer. Look for it, in order:

1. next to this `SKILL.md` (when the guide was installed with the skill);
2. in the Glacier install folder, `ai/AGENT-GUIDE.md` (the folder that holds `glacier-cli`);
3. in a Glacier source checkout, `docs/ai/AGENT-GUIDE.md`.

If none is found, ask the user where Glacier is installed. `glacier-cli api` prints the full,
version-exact Lua API reference; `glacier-cli help` lists every command.

## MCP tools or the command line

- **If Glacier's MCP tools are available** (a `glacier` server with tools such as `session_info`,
  `run_lua`, `render_plan`), use them: call `session_info` first, read `glacier://guide/index` and
  then the guide sections it lists (`glacier://guide/<n>`; one read of the whole `glacier://guide`
  can be cut short) instead of searching for the file, and look at `render_plan` images directly.
  Each `run_lua` is one undo step labelled `AI: <name>`. Permissions (read-only, destructive actions)
  come from how the user started the server; if something is refused, tell the user rather than
  looking for a way around it. While the server is attached to the editor, `glacier-cli --attach`
  cannot connect: keep using the tools.
- **Otherwise use `glacier-cli`** as the guide describes (headless on files, or `--attach` to the
  open editor). The user can connect the tools with
  `claude mcp add glacier -- "<Glacier folder>/glacier-cli" mcp` (see the guide, section 2).

## The five rules that matter most

1. **The designer directs; you assist.** Leave the level easy to edit by hand: prefer the design kit
   (`ged.import("kit")`), give every object and kit part a unique name, keep brushes simple.
2. **Never claim it works without checking.** Finish in this order: a sealed build, a hole check
   until it is watertight (`glacier-cli holes` / `check_holes`; holes only count on a sealed build),
   `lint`, then bake the lighting and save; and look at a plan render of every storey.
3. **The world starts solid and CSG is time-ordered.** Rooms are air brushes; create the air before
   any solid that stands in it. Units are metres; +X right, +Y up, +Z forward.
4. **Look names up instead of guessing:** `ged.describe(...)`, `catalog.classes/describe/textures`,
   `x:fields()`. Errors carry hints with the nearest valid name. For proportions (doorway, corridor and
   ceiling sizes, light spacing) use the corpus norms: `catalog.norms("opening.width")`,
   `glacier-cli norms`; lint's `outOfNorm` info findings are hints, not failures.
5. **Nothing destructive without explicit permission.** No deletes, overwrites or discarded editor
   changes unless the user asked (`--allow-destructive`). In attach mode each run is one undo step
   (`glacier-cli undo --attach` takes back your own latest run); iterate headless on a scratch file
   and apply the finished script once.
