using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ged.Automation.Tools;

namespace Ged.Cli;

/// <summary>The <c>glacier-cli help</c> text (kept in step with the user help's command-line section).</summary>
internal static class CliHelp
{
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["info"] = """
            glacier-cli info <level.rfl>
              Shows the level's name, format version, brush/object/link counts, and whether its
              compiled geometry is built, stale, or an unsealed preview.
            """,
        ["new"] = """
            glacier-cli new --out <path.rfl>
              Creates a new level from the File > New template (level properties, a Player Start,
              no geometry) and saves it to --out. With --attach, creates it in the editor instead
              (--out optional; unsaved changes there need --allow-destructive).
            """,
        ["open"] = """
            glacier-cli open <level.rfl> --attach
              Opens a level in the running editor. Discarding unsaved changes needs --allow-destructive.
            """,
        ["run"] = """
            glacier-cli run <script.lua> [<level.rfl>] [--save | --save=<path> | --out <path>]
                            [--build | --light [--no-shadows]] [--dry-run] [--allow-destructive]
                            [--timeout <s>] [--budget <n>] [--label <text>] [--no-tag] [--no-build]
                            [--allow-import <file>]…
              Runs a Lua script against the level as ONE undo step labelled "CLI: <script>".
              Without a level (and without --attach) the script builds a new level, so pass --out.
              Everything the run creates is tagged ai.script=<script file> and ai.session=<id>
              (one id per glacier-cli invocation); a tag the script sets itself wins. Find them
              later with level.objects_tagged / level.brushes_tagged. The created UIDs are listed.
              ged.import("name") also finds name.lua beside the script (its own folder only, literal
              names); those modules are sent with the run, so --attach works the same.
              --save            save the level back to its own file after a successful run
              --save=<path>     save to another path (same as --out <path>)
              --build           after a successful run, compile a sealed build (before any save)
              --light           after a successful run, compile sealed and bake lighting
              --dry-run         run, report what would change, then roll everything back (nothing
                                is saved: --dry-run cannot be combined with --save or --out)
              --label <text>    a short description appended to the undo label (max 60 characters)
              --allow-destructive  allow the script's delete / save / package operations
              --timeout <s>     script execution time limit (default 30; 0 = none, which also lifts
                                the instruction budget; builds don't count)
              --budget <n>      Lua instruction budget (default 200000000; 0 = unlimited). A run that
                                stops with "Instruction budget exceeded" needs a larger budget, or
                                splitting into several runs; it overrides what --timeout 0 implies
              --no-tag          do not add the ai.script / ai.session tags
              --no-build        save without building first (see 'save'; the game cannot load an unbuilt level)
              --allow-import <file>  let level.import_mesh read this model file (repeatable; read-only;
                                besides the level's folder, user_maps and the scripts library)
              Saving builds the geometry first (sealed) when it is missing or out of date; lighting is
              only baked by 'build --light' (or ops.light() in the script). Every save also writes the
              editor-only sidecar <level>.gedlayout.json (see 'help save').
            """,
        ["build"] = """
            glacier-cli build <level.rfl> [--sealed] [--light] [--no-shadows] [--save | --out <path>]
              Compiles the level and applies the result, exactly like Geometry > Build.
              Default is the fast unsealed preview; --sealed runs the full sealed build;
              --light also bakes lighting (always sealed). Saving after a preview re-seals first.
              With Geometry > Repair Holes When Building on (the default) a sealed build also repairs
              small holes and lists what it repaired ('repair' in --json).
            """,
        ["light"] = """
            glacier-cli light <level.rfl> [--no-shadows] [--save | --out <path>]
              Compiles a sealed build and bakes lighting, exactly like Calculate Lighting.
              The same as 'build --sealed --light'.
            """,
        ["holes"] = """
            glacier-cli holes <level.rfl> [--fail-on-holes]
              Checks a SEALED build for leaks and lists their positions. Leaks are reported as data
              (exit 0); --fail-on-holes exits 5 when any are found.
            """,
        ["lint"] = """
            glacier-cli lint <level.rfl> [--min-severity info|warning|error]
              Runs the level linter (links, spawns, budgets, leaks, missing assets, visible faces left
              with the placeholder texture new brushes start with) with locations.
              --min-severity <s>  list only findings at or above info (default), warning or error;
                                  the counts stay totals
            """,
        ["summary"] = """
            glacier-cli summary <level.rfl> [--detail brief|normal|full]
              A structured level summary: bounds, brushes, objects by kind (with positions and key
              properties above brief), links, rooms with portal neighbours, lint counts. Use --json.
            """,
        ["analyze"] = """
            glacier-cli analyze <level.rfl> [--json] [--timeout <s>]
              Measures the level and prints its fingerprint: floor area, headroom, corridor and
              doorway widths, stairs, vertical layers, room graph, textures, lighting, spawn and
              item layout, and event link patterns. Measured from the compiled geometry stored in
              the level (levels without compiled geometry get object metrics only). Read-only: the
              level is never opened for editing or modified. --timeout limits the analysis
              (default 120 s).
            """,
        ["norms"] = """
            glacier-cli norms [--metric <id> [--gametype <bucket>]] [--json]
            glacier-cli norms --compare <level.rfl> [--json] [--timeout <s>]
            glacier-cli norms --compare --attach
              The corpus norms built into Glacier: how levels are typically proportioned
              (doorway and corridor widths and heights, headroom, light density, texture
              tiling, ...), as percentiles over a large set of released levels, one vote per
              level. Aggregates only. Without options, lists every metric (unit, reliability,
              doc). --metric prints one metric in every published bucket ("exemplar" = a
              curated reference set, "overall", gametypes such as "dm", "ctf", "sp", origins
              "retail"/"community"); --gametype (or --bucket) narrows it to one bucket.
              --compare measures the level (read-only, like analyze) and prints each metric
              that drives the design kit's defaults with the level's value, the reference
              range (p5-p95) and below / within / above.
            """,
        ["corpus"] = """
            glacier-cli corpus sweep    [--root <dir>]... [--out <dir>] [--jobs <n>] [--timeout <s>]
                                        [--limit <n>] [--debug-render <n>] [--no-collapse] [--no-snapshot]
                                        [--rf-dir <dir> | --no-install]
            glacier-cli corpus snapshot [--root <dir>]... [--out <dir> | <file.json>]
            glacier-cli corpus verify   [--out <dir> | <file.json>] [--snapshot <file.json>]
            glacier-cli corpus aggregate [--out <dir>] [--no-collapse] [--promote]
              Fingerprints a collection of levels (loose .rfl files and the levels inside .vpp
              packfiles) into an output folder, READ-ONLY: level files are opened for reading
              only and nothing is ever written inside the level folders. The output folder may
              not be, contain, or lie inside a level folder.
              sweep     one fingerprint per distinct level (by content hash) under
                        <out>/fingerprints, plus index.jsonl (every file found, duplicates,
                        exclusions), failures.jsonl and manifest.json. Autosaves, backups and
                        levels tagged by automated runs are excluded. Interrupted sweeps resume
                        from the fingerprints already written. Each level gets --timeout seconds
                        (default 60). Takes corpus-snapshot.json first unless one exists.
                        --debug-render <n> also writes plan images of n randomly chosen levels
                        to <out>/debug (detected openings, corridors and stairs drawn on top).
              snapshot  records every file's size, time stamp and SHA-256 under the folders.
              verify    re-hashes the folders recorded in a snapshot and reports any changed,
                        missing or new file (exit 1 when anything differs).
              aggregate turns a sweep's fingerprints into <out>/norms/corpus-norms.json
                        (percentiles per metric and bucket; no level names, authors or
                        texture file names) and norms-report.md. An optional curated list
                        (GED_EXEMPLAR_LEVELS, else research/exemplar-levels.txt: one level
                        file name per line) forms the "exemplar" reference bucket. --promote
                        (source checkout only) copies the result, size-checked, into
                        src/Ged.Core/Analysis/corpus-norms.json for review and rebuild.
              Folders: --root (repeatable), else GED_CORPUS_DIRS (';'-separated), else
              research/corpus-dirs.txt in a source checkout. Without --root, the Red Faction
              install (--rf-dir, GED_RF_DIR, research/rf-dirs.txt) adds its packfiles.
              Output: --out, else artifacts/corpus in a source checkout, else
              %LOCALAPPDATA%/Glacier/corpus.
            """,
        ["ref"] = """
            glacier-cli ref add <image> --refs <dir> --id <id> [--crop x,y,w,h] [--snap] [--area <text>]
                            [--notes <text>] [--replace]
            glacier-cli ref view <image> [--crop x,y,w,h] [--snap] --out <png> [--side <px>]
            glacier-cli ref view <id> --refs <dir> --out <png>
            glacier-cli ref list --refs <dir>
            glacier-cli ref camera <id> --refs <dir> [--camera x,y,z] [--look x,y,z | --heading <deg> --pitch <deg>]
                            [--move f,r,u] [--turn h,p] [--hfov <deg>] [--eye-height <m>|eye|crouch|jump]
                            [--horizon y | x1,y1,x2,y2] [--vp x,y] [--vp-dir x,z] [--object x,base,top,m]
                            [--measure x,base,top,...] [--ignore x,y,w,h,...] [--clear-marks] [--apply-estimate]
            glacier-cli ref compare <id> <level.rfl> --refs <dir> --out <png> [--panel <px>] [--mode <mode>]
                            [camera overrides as for 'ref camera', not saved]
            glacier-cli ref scale [<count> <unit>]
              Reference matching: compare the level with the concept screenshots it should look like.
              add      cuts a reference picture out of a concept image (--crop in the image's pixels;
                       --snap moves the edges onto the panel's frame) and stores it as <id>.png in the
                       reference set (--refs: a folder with references.json; the level is never changed).
              view     an image with a coordinate grid: a concept image in its pixels (to read crops),
                       or a stored reference in fractions 0-1 with its marks (to read cue positions).
              camera   sets the reference's matched camera: position and look (or heading / pitch),
                       nudges in the camera's frame (--move forward,right,up metres; --turn heading,pitch
                       degrees), the horizontal field of view (default 90) and the eye height above the
                       floor (renders stand the camera on the floor under it). Marks (fractions of the
                       picture) give an estimate: the horizon gives pitch and roll, a vanishing point
                       (with the lines' world direction) the heading, a known-height object the eye
                       height and its distance, measure spans their heights in metres.
                       --apply-estimate puts the estimate on the camera.
              compare  renders the level from the reference's camera (like 'render': headless, or the
                       editor's level with --attach) at the reference's aspect, and writes one sheet:
                       reference | render, blend | edge overlay, with both horizons and skylines, plus
                       the raw render (<out>.render.png). --json adds the cues (detail density, plain
                       top, skyline, brightness, warmth, horizons, edge overlap) and hints.
              scale    known Red Faction dimensions measured on the stock levels (door 2.5 m, doorway,
                       corridor, room height, stairs, player, eye, jump) and count x unit in metres.
            """,
        ["modules"] = """
            glacier-cli modules harvest [--sources <file.json>] [--root <dir>] [--out <dir>] [--group <name>]
                                        [--rf-dir <dir> | --no-install] [--jobs <n>]
            glacier-cli modules harvest <level.rfl | pack.vpp> --group <name> --out <dir>
            glacier-cli modules list  [--category <c>] [--attach <kind>] [--role <r>] [--style <s>]
                                      [--group <name>] [--stock-only] [--include-rejected] [--limit <n>]
                                      [--modules <dir>] [--json]
            glacier-cli modules show <id> [--modules <dir>] [--json]
            glacier-cli modules stats [--modules <dir>] [--json]
            glacier-cli modules sheet --out <dir> [--modules <dir>] [--group <g>] [--category <c>] [--style <s>]
                                      [--top <n>] [--size <px>] [--rf-dir <dir>] [--verbose] [--json]
              The detail and architecture module library: recurring assemblies (pilasters, trims,
              door and window frames, beams, trusses, columns, railings, catwalks, stairs, pipes,
              machinery, consoles, rocks, ...) harvested from levels, each stored as an .rfg in its
              own local frame with its anchor, stretchable axes, repeating units, styles and score.
              harvest   reads the levels a sources manifest lists (--sources, else library/sources.json
                        beside the library), or one level or packfile, READ-ONLY, and writes
                        <out>/<group>/index.json plus <out>/<group>/<category>/<id>.rfg and
                        <out>/harvest-report.json. Paths in the manifest are relative to --root
                        (default: the folder above library/). Nothing is ever written inside a
                        source folder: an output folder that is, contains, or lies inside one is
                        refused. The same inputs always give byte-identical files. The Red Faction
                        install (--rf-dir, GED_RF_DIR, research/rf-dirs.txt) tells stock textures
                        from custom ones; --group harvests one manifest group (or names the group
                        of a single level or packfile); --jobs sets the parallel workers.
              list      modules matching the filters, best score first (default 50; --limit).
                        --attach here is the attachment filter: wall, corner, floor, ceiling,
                        column, span, edge, opening or free.
              show      one module in full: frame, stretch slices, tiles, textures, provenance, curation.
              stats     counts by group, category, attachment, role and style, and the curation counts
                        (kept, rejected, relabelled, undecided).
              sheet     the curation contact sheet: renders each module alone (lit, 3/4 view framed
                        on it) to <out>/thumbs/<id>.png and writes <out>/index.html, where you keep,
                        reject and relabel modules and download curation.json. --top modules per
                        category (default 24, best score first), --size thumbnail pixels (default 256).
              Curation: library/modules/curation.json (written by you, from the sheet; never by a
                        harvest) is applied whenever the library loads: rejected modules leave list,
                        fit, place and the kit (--include-rejected lists them), relabels replace the
                        category, role and styles, and kept modules rank 0.5 higher.
              Library: --modules <dir>, else GED_MODULE_LIBRARY, else library/modules beside
              glacier-cli, else library/modules in the source checkout.
            """,
        ["save"] = """
            glacier-cli save <level.rfl> [--out <path.rfl>] [--no-build]
              Saves the level (Alpine v306) with the editor's save rules — e.g. to upgrade an older
              level, or (with --attach) to save the editor's open level. A level whose geometry is
              missing or out of date is built (sealed, unlit) first, because Red Faction crashes
              loading a level with brushes and no compiled geometry; --no-build skips that and warns.
              Every save (here, run --save/--out, build/light --save/--out, the editor's File > Save)
              also writes <level>.gedlayout.json beside the .rfl: Glacier's editor-only sidecar
              (Link Graph node positions, measurement annotations, the level's own lightmap method
              when it overrides the global one). The game never reads it, packfiles leave it out,
              and the .rfl does not depend on it; keep it with the level to keep those editor
              settings, or delete it to drop them.
            """,
        ["render"] = """
            glacier-cli render <level.rfl> --out <image.png> [view] [framing] [options]
              Draws the level to a PNG (default: a floor plan) and, with --json, reports how image
              pixels map to world coordinates. Read-only: the level is never modified or saved.
              Views (default --plan):
                --plan [--y <h>] [--thickness <m>] [--below <m>]
                                  top-down slice of one storey: geometry above y + thickness
                                  (default 2) is cut away, and below y - below (default 1) is
                                  hidden. Default y = the floor under the player start (else the
                                  first MP spawn, else the lowest floor). Walls show as dark cut
                                  edges, solid space is hatched, openings to a lower floor are
                                  shown dimmed.
                --view top|bottom|front|back|left|right   orthographic, whole level
                --view iso [--fov <deg>]                  three-quarter perspective from above
                --camera x,y,z --look x,y,z [--fov <deg> | --hfov <deg>] [--eye-height <m>]
                                  perspective camera (vertical fov default 60; --hfov gives the
                                  horizontal field of view instead, e.g. 90 like Red Faction's
                                  default; --eye-height stands the camera that high above the
                                  highest floor at or below the camera point, keeping the pitch)
              Framing (plan / ortho / iso; default: the whole level, or the plan's storey):
                --region minx,miny,minz,maxx,maxy,maxz | --room <index> | --uid <uid>
              Options:
                --mode lit|textured|lightmaps|rooms|wireframe|seethrough
                                  default lit (lightmaps; falls back to textures, then room
                                  colours, when the level is not lit or no RF install is mounted)
                --size WxH        image size (default 1024x1024, at most 4096 per side)
                --overlays <list> any of grid,scale,objects,labels,links,holes,legend, or none
                                  (default for --plan: grid,scale,objects,labels,links,legend;
                                  other views: none). The legend comes with any other overlay
                                  unless nolegend is listed. holes runs a sealed leak check.
                                  icons / noicons in the list work like --icons / --no-icons.
                --no-overlays     same as --overlays none (and no 3-D object icons)
                --icons | --no-icons
                                  draw the editor's 3-D object icons (lights, spawns, events,
                                  triggers, keyframes, facing arrows) in a non-plan view. Default:
                                  off for --view iso and --camera (a clean view: objects with a
                                  game mesh - clutter, items - show as that mesh), on for the
                                  other orthographic views, off whenever --overlays is none
                --rebuild         render from a fresh preview build
                --sealed          render from a fresh sealed build
              Geometry: the image shows the level's compiled geometry. When it is missing or out
              of date, a fast preview build is made for the render only (reported in the output).
              The level's sky room (its skybox) is never framed or drawn as part of the level; a
              camera view shows it through show-sky faces as the game does (drawn from the sky
              room's centre; a flat sky colour when the level has none). Orthographic views without
              --size take the framed geometry's proportions (a plan of a region: the part of it with
              geometry in the slice); --view iso frames the drawn geometry (with --region: only the
              region, the rest cut away).
              Plan view: +X is image right and +Z is image up. Overlay glyphs: player start (green),
              MP spawns (red / blue / purple = both teams / white = any), items (yellow diamonds),
              triggers (cyan squares + extent), events (orange triangles), keyframes and movers
              (magenta), lights (pale stars), clutter (grey squares), labels = UID and short script
              name. With --attach, the editor's open level (including unsaved edits) is copied to
              glacier-cli and rendered there.
            """,
        ["undo"] = """
            glacier-cli undo --attach [--steps <n>]
              Undoes the editor's latest command-line run (undo step labelled "CLI: <script>"), or
              the latest n. Only command-line runs are ever undone: if any of the n latest undo
              steps is the designer's own edit, nothing is undone and the step is named. The
              designer can still undo anything in the editor (Ctrl+Z).
            """,
        ["api"] = """
            glacier-cli api [--markdown | --lua] [--member <name>]
              Prints the Lua scripting reference generated from this build: the conventions, every
              global and type with its members, table options and examples, and the design kit.
              No level, editor or RF install is needed.
              --markdown        the full reference as Markdown (the default)
              --lua             the Lua stub of the globals (for editor completion)
              --member <name>   one global or type (level, brush, kit) or one member
                                (level.brush, brush:set_pos, kit.room)
            """,
        ["hello"] = """
            glacier-cli hello --attach
              Checks that a running editor accepts command-line connections.
            """,
        ["redo"] = """
            glacier-cli redo --attach [--steps <n>]
              Re-applies the command-line runs that 'undo --attach' took back, in order.
              Refuses when the next redo step was not undone this way, or when anything was done since.
            """,
        ["checkpoint"] = """
            glacier-cli checkpoint <name> --attach
              Remembers the structure of the editor's open level under a name: every object's fields,
              every brush's position, flags, geometry and textures. Nothing in the level changes and no
              undo step is added. Compare later with 'diff --since <name>'. Up to 16 are kept.
            """,
        ["diff"] = """
            glacier-cli diff --since <name> --attach [--max-changes <n>]
              Lists what was added, removed and changed in the editor's open level since a checkpoint,
              by UID: each changed object field with its before and after value; for brushes, which of
              position, rotation, flags, tags, geometry or textures changed.
            """,
        ["inspect"] = """
            glacier-cli inspect <uid> <level.rfl>
              Everything about one object or brush: kind, class, name, position, rotation, every field
              with its type and value (the fields Lua obj:fields() lists), links out and in, tags, and
              for a brush its flags, face count, size, bounds and textures. With --attach, the level
              open in the editor. Use --json for the full result.
            """,
        ["query"] = """
            glacier-cli query <level.rfl> [--what objects|brushes] [--kind <kind>] [--class <glob>]
                              [--name <glob>] [--tag <key[=value]>] [--region <6 numbers>]
                              [--limit <n>] [--offset <n>]
              Lists the matching UIDs (sorted, paged) with kind, class, name and position. Objects
              filter by kind, class, name, tag and region; brushes by tag and region.
            """,
        ["catalog"] = """
            glacier-cli catalog kinds|classes|describe|textures [--kind <kind>] [--class <class>]
                                [--filter <text>] [--limit <n>] [--stock-only]
              What can be placed: 'kinds' lists the placeable kinds, 'classes --kind Item' a kind's
              class names (game tables need the RF install; events always), 'describe --kind Event
              --class Message' a kind's or class's fields and links, 'textures --filter rock' texture
              names with provenance. The same data as the Lua catalog global; no level needed.
            """,
        ["spatial"] = """
            glacier-cli spatial <op> <level.rfl> [options]
              Spatial queries and design measurements on the level's compiled geometry (read-only; a
              level with missing or stale geometry is measured on a private preview build unless
              --fresh none). Operations: raycast, los, point_info, clearance (instant); walk, routes,
              graph, sightlines, cover, cells, area_report, spawn_report, item_report, locations,
              setpieces (bridge spans, landmark heights, atria, largest spaces), purpose (bridges
              and flights that serve no route, route ledges, area ways out, misplaced rocks, orphan
              cover, hidden landmarks) (they measure the level once: a few seconds). Points are x,y,z; --region is 6 numbers.
              Every result reports the geometry's freshness (built / preview / stale, and what was
              measured). Use --json.
            """,
        ["grade"] = """
            glacier-cli grade <level.rfl> [--gametype <g>] [--players <n>] [--class standard|open_air|payload]
                              [--max-locations <n>] [--metrics] [--fresh auto|preview|sealed|none]
                              [--max-samples <n>] [--timeout <s>] [--fail-on review|fail] [--json]
              The design grader: measures the level and runs the self-review checks (scale, shape,
              flow, population, texturing; CTF, KOTH and payload rules; open-air and payload maps
              get their own limits). Prints the verdict (PASS: at most 2 failing checks; REVIEW: 3-4;
              FAIL: 5 or more, or a hard check), then each failing check with the measured values,
              a fix hint and the locations to act on, then the advisory player-class targets
              (playable area, area per spawn, loops, floor-height classes: reported, never counted)
              and the set pieces (bridge span, landmark and atrium heights). The verdict is data
              (exit 0); --fail-on review|fail exits 6 when the verdict is at least that bad.
            """,
        ["mcp"] = """
            glacier-cli mcp [--attach | --headless] [--read-only] [--allow-destructive] [--allow-write <dir>]…
                            [--scratch <dir>] [--tools <a,b>] [--deny-tools <a,b>] [--max-image <px>]
                            [--rf-dir <dir>] [--log <file>]
              Runs a Model Context Protocol server on standard input/output, so an AI assistant (for
              example Claude Code or Claude Desktop) can open, script, build, check, render and save
              levels through typed tools, one document at a time. Standard output carries only protocol
              messages; diagnostics go to standard error (or --log).
              Without --attach or --headless the server looks for a running editor once at startup:
              if one answers, the tools work on the level open there; otherwise a window-less session
              starts with no level open. The choice holds until the server exits.
              --attach          always work in the running editor (if none answers yet, every tool says
                                so and retries; it reconnects after the editor restarts)
              --headless        always run a window-less session
              --read-only       offer only the tools that never change the level or write files
              --allow-destructive  let tools discard unsaved changes, overwrite other existing files,
                                and let scripts delete, save and package (off by default); in the
                                editor this also needs Settings ▸ General ▸ "Allow destructive AI
                                actions"
              --allow-write <dir>  another folder tools may write under (repeatable); the level's
                                folder, the RF install's user_maps and the scripts library always are
              --scratch <dir>   where rendered images are written (default: a temporary folder that
                                is removed when the server exits)
              --tools <a,b>     offer only these tools; --deny-tools <a,b> never offer these
              --max-image <px>  the largest rendered image side (default 1024, at most 1568)
              --log <file>      write diagnostics to this file
              Every script run is one undo step labelled "AI: <script>"; the undo tool only takes back
              automation steps. Permissions come only from these options (and, attached, the editor's
              settings), never from a tool call.
            """,
    };

    /// <summary>The verbs whose option lines are generated from the tool catalog (their tools' CLI flags).</summary>
    private static readonly HashSet<string> GeneratedOptionVerbs = new(StringComparer.Ordinal)
    {
        "redo", "checkpoint", "diff", "inspect", "query", "catalog", "spatial", "grade", "ref",
    };

    /// <summary>
    /// The option lines of <paramref name="verb"/> generated from <see cref="ToolCatalog"/>: every tool argument
    /// with a CLI flag, once, with its documentation (so a verb and its tools cannot drift apart).
    /// </summary>
    internal static string GeneratedOptions(string verb)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (ToolArg arg in ToolCatalog.All.Where(t => t.CliVerb == verb).SelectMany(t => t.Args))
        {
            if (arg.CliFlag is not { } flag || !seen.Add(flag))
            {
                continue;
            }

            string value = arg.Type == "boolean" ? string.Empty : arg.Type == "array" ? " <list>" : $" <{arg.Name}>";
            string head = $"  {flag}{value}";
            sb.Append(head.PadRight(22)).Append(head.Length >= 22 ? " " : string.Empty).AppendLine(arg.Doc);
        }

        return sb.Length == 0 ? string.Empty : "  Options:" + Environment.NewLine + sb;
    }

    /// <summary>The full help, or one verb's.</summary>
    public static string Text(string? verb)
    {
        if (verb is not null && Verbs.TryGetValue(verb, out string? one))
        {
            return GeneratedOptionVerbs.Contains(verb)
                ? one + Environment.NewLine + GeneratedOptions(verb)
                : one + Environment.NewLine;
        }

        return $$"""
            Glacier command-line tools {{Ged.Automation.AutomationCommands.ProductVersion}}

            Usage: glacier-cli <command> [arguments] [options]

            Commands:
              info <level>                 level name, version, counts, geometry state
              new --out <path>             create a new level from the File > New template
              run <script.lua> [<level>]   run a Lua script as one undoable step
              build <level>                compile geometry (--sealed, --light)
              light <level>                sealed build + lighting (= build --sealed --light)
              holes <level>                leak check on a sealed build, with positions
              lint <level>                 linter findings with locations
              summary <level>              structured level summary (--detail brief|normal|full)
              analyze <level>              level fingerprint: scale, layout, materials, lighting, links
              norms [--compare <level>]    corpus norms (typical proportions); compare a level with them
              spatial <op> <level>         spatial queries: raycast, walk, routes, graph, sightlines, ...
              grade <level>                design grader: PASS / REVIEW / FAIL with fixes and locations
              corpus sweep|snapshot|verify|aggregate
                                           fingerprint a folder of levels read-only (help corpus)
              modules harvest|list|show|stats
                                           the detail/architecture module library (help modules)
              save <level>                 save with the editor's save rules (--out <path>)
              render <level> --out <png>   floor plan / view image + pixel-to-world mapping
              ref add|view|list|camera|compare|scale
                                           match the level to reference screenshots (help ref)
              open <level> --attach        open a level in the running editor
              undo --attach                undo the editor's latest command-line run (--steps <n>)
              redo --attach                redo what undo took back (--steps <n>)
              checkpoint <name> --attach   remember the open level's structure under a name
              diff --since <name> --attach what changed since a checkpoint
              inspect <uid> <level>        every field, link and tag of one object or brush
              query <level>                find objects or brushes by kind, class, name, tag, region
              catalog kinds|classes|describe|textures
                                           placeable kinds, class names, fields, textures
              mcp                          run a Model Context Protocol server for AI assistants
              hello --attach               check the connection to the running editor
              api                          the Lua scripting reference (--markdown, --lua, --member <name>)
              help [command]               this help, or one command's

            Options:
              --json                 machine-readable output: {"ok", "command", "result", "error"}
              --attach               send the command to the running Glacier editor instead of a
                                     file; the level argument may be omitted to use its open level.
                                     The editor shows script output in its Script Log, updates the
                                     viewport, and records each run as one undo step.
              --rf-dir <dir>         Red Faction install to mount (default: GED_RF_DIR, then the
                                     install configured in the editor's settings; render --attach
                                     defaults to the editor's install)
              --verbose              report diagnostics on stderr, e.g. which RF install was mounted
                                     and where it came from (--rf-dir, GED_RF_DIR, editor settings)
              --timeout <s>          script time limit; with --attach also how long to wait for a reply
              --allow-destructive    allow discarding unsaved changes / overwriting another file
              --version              print the version

            Files are written only under the level's folder, the RF install's user_maps, the scripts
            library, or (without --attach) paths you name on the command line.

            Exit codes:
              0  success          1  the command failed (script error, build errors, save aborted,
                                     undo refused, no GPU device for render)
              2  usage error      3  --attach: no running editor accepted the connection
              4  file error (missing, unreadable, unwritable, or outside the allowed folders)
              5  holes --fail-on-holes found leaks
              6  grade --fail-on review|fail: the verdict is at least that bad

            """;
    }

    /// <summary>One verb's usage block (or the command list).</summary>
    public static string Usage(string? verb) =>
        verb is not null && Verbs.TryGetValue(verb, out string? one) ? one + Environment.NewLine : "Run 'glacier-cli help' for usage." + Environment.NewLine;
}
