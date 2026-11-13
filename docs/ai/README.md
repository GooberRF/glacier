# AI-assisted level design with Glacier

This folder (`ai` beside the Glacier executable; `docs/ai` in the source repository) holds the
documentation an AI agent needs to build and edit Red Faction levels with Glacier on your behalf.

| File | For | What it is |
|---|---|---|
| `AGENT-GUIDE.md` | the agent | How to drive Glacier: setup, the build-look-check loop, the world model, the design kit, discovery, editing existing levels, the verification checklist, pitfalls, and a worked example. |
| `claude-skill/` | Claude Code | A small skill (`SKILL.md`) that tells Claude Code when to use Glacier and to read the guide first. In the Glacier package it also holds a copy of the guide. |
| `README.md` | you | This page. |

The agent does the work through `glacier-cli`, which ships beside the Glacier executable (on Linux,
in the tar.gz package). It can work on level files by itself, or on the level open in the editor
while you watch; each of its runs is then one undo step. See *Help ▸ Help Topics*, "AI-assisted
level design", for what it can do, how to watch and undo its changes, and the safety settings.

## Claude Code

Install the skill once:

- **For all your projects:** copy the `claude-skill` folder to `~/.claude/skills/glacier-level-design/`
  (on Windows, `%USERPROFILE%\.claude\skills\glacier-level-design\`).
- **For one project only:** copy it to `<project>/.claude/skills/glacier-level-design/`.

The packaged `claude-skill` folder already contains a copy of `AGENT-GUIDE.md`, so the skill finds the
guide wherever you put it (from a source checkout, copy `docs/ai/AGENT-GUIDE.md` into the skill folder
yourself). Restart Claude Code; the skill is picked up when you ask for Red Faction level
work (or invoke it as `/glacier-level-design`).

Tell Claude where Glacier is installed the first time (or put the Glacier folder on your `PATH`),
and which Red Faction install to use if Glacier is not already set up with one.

### Glacier's tools in Claude Code (MCP)

`glacier-cli mcp` is a Model Context Protocol server: it gives Claude Code (or any other MCP host)
typed tools for opening, scripting, building, checking, rendering and saving levels, and lets it
look at the floor-plan images directly. Add it once:

```
claude mcp add glacier -- "<Glacier folder>\glacier-cli.exe" mcp
```

(on Linux, `"<Glacier folder>/glacier-cli"`). By default the tools work on the level open in Glacier
when the editor is running, and on level files otherwise; add `--attach` or `--headless` after `mcp`
to choose. The skill and the server work together: the skill tells Claude when to use Glacier, the
server gives it the tools. `glacier-cli help mcp` lists the options, and *Help ▸ Help Topics*, "AI-assisted
level design", explains the safety settings and how to undo what the agent did.

## Other agents

Any agent that can run command-line programs and look at image files can use Glacier. Give it
`AGENT-GUIDE.md` as context (paste it, attach it, or point it at the file), and tell it where
`glacier-cli` is. The guide is written to be agent-neutral. `glacier-cli api` prints the full Lua
scripting reference for your build, which an agent can read alongside the guide.

## Watching it work

To watch changes happen, open Glacier with the level you want changed and keep *Settings ▸ General ▸
Allow command-line tools and AI agents to control the open level* switched on (the default). Ask
the agent to work "in the open editor". Its script output appears in the Script Log, the viewport
updates after each run, and Undo (Ctrl+Z) removes a whole run at a time (runs through the MCP tools
appear in the undo history as `AI: <script name>`, command-line runs as `CLI: <script name>`). The agent can
take back its own latest run, but never your edits. Everything it
creates is tagged `ai.session` / `ai.script`; in the Script Console,
`level.brushes_tagged("ai.session"):select()` selects the brushes it made.

## Example first prompts

- "Using Glacier, block out a small deathmatch level: two 14 m rooms joined by a corridor, two
  pillars in one room, 8 spawns, a shotgun and a medkit. Work headless in `work/`, show me the plan
  render, then build, check holes and lint, and save it as `work/twin.rfl`."
- "In the open Glacier editor, add a raised platform with stairs in the north-east corner of the
  room I have selected, light it, and render the plan so we can both check it."
- "Open `dm_canyon.rfl`, summarise it, and tell me which rooms have no lights and which spawns are
  closer than 1.5 m to a wall. Don't change anything yet."
- "Add a sliding door with Use buttons on both sides to the doorway between `hall` and `annex`."
- "Make every light in the selected rooms warmer and 20% brighter, then show me a lit render."
