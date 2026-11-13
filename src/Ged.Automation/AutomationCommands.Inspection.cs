using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Editor;
using Ged.Core.Scripting;

namespace Ged.Automation;

// The read-only perception commands (inspect / query / catalog / api), the structural checkpoints
// (checkpoint / diff) and redo. None of them changes the document, the undo history, the dirty flag or
// the compiled-geometry state except redo, which re-applies steps this command set itself undid.
public sealed partial class AutomationCommands
{
    /// <summary>The most checkpoints kept per session (the oldest is dropped).</summary>
    public const int MaxCheckpoints = 16;

    /// <summary>The longest checkpoint name.</summary>
    public const int MaxCheckpointName = 64;

    /// <summary>The <c>query</c> page-size default and cap.</summary>
    public const int QueryDefaultLimit = 50;

    public const int QueryMaxLimit = 500;

    private readonly List<LevelInspector.Snapshot> _checkpoints = new();

    /// <summary>The undo nodes this command set undid (top = the next to redo) and the document they belong to.</summary>
    private readonly List<UndoNode> _undone = new();
    private EditorDocument? _undoneDocument;

    // ---- redo -----------------------------------------------------------------------------------

    /// <summary>Records the nodes the next <c>undo</c> of <paramref name="steps"/> takes back, so <c>redo</c> can re-apply exactly them.</summary>
    private void RememberUndone(int steps)
    {
        EditorDocument doc = RequireDocument();
        if (!ReferenceEquals(_undoneDocument, doc))
        {
            _undone.Clear();
            _undoneDocument = doc;
        }

        // Anything redone/undone by hand since invalidates the remembered chain beyond the current branch.
        var taken = new List<UndoNode>();
        UndoNode? n = doc.Undo.Current;
        for (int i = 0; i < steps && n is not null && n != doc.Undo.Root; i++, n = n.Parent)
        {
            taken.Add(n);
        }

        if (_undone.Count > 0 && !ReferenceEquals(doc.Undo.Current.RedoChild, _undone[^1]))
        {
            _undone.Clear(); // the history moved on since the last undo: older redo steps are no longer next
        }

        // Newest first is undone first, so the oldest of them is redone first: push newest, then older.
        foreach (UndoNode node in taken)
        {
            _undone.Add(node);
        }
    }

    /// <summary>
    /// <c>redo</c> — args: <c>steps?</c> (default 1). Re-applies the next <c>steps</c> redo steps, but only
    /// when every one of them is a step this session undid with <c>undo</c> (and nothing has been done since):
    /// an agent may restore its own undo, never re-apply something the designer undid.
    /// </summary>
    private async Task<AutomationResponse> RedoCommandAsync(AutomationRequest r, CancellationToken ct)
    {
        double requested = Num(r, "steps") ?? 1;
        if (requested < 1 || requested != Math.Floor(requested) || requested > UndoStack.MaxNodes)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"'steps' must be a whole number from 1 to {UndoStack.MaxNodes}.");
        }

        int steps = (int)requested;
        await EnsureLevelAsync(r, ct);
        EditorDocument doc = RequireDocument();
        if (!ReferenceEquals(_undoneDocument, doc))
        {
            _undone.Clear();
            _undoneDocument = doc;
        }

        var labels = new List<string>();
        UndoNode? next = doc.Undo.Current.RedoChild;
        for (int i = 0; i < steps; i++)
        {
            int slot = _undone.Count - 1 - i;
            if (next is null)
            {
                throw new AutomationException(AutomationErrorCodes.RedoRefused,
                    labels.Count == 0 ? "There is nothing to redo." : $"Only {labels.Count} redo step(s) exist; nothing was redone.");
            }

            if (slot < 0 || !ReferenceEquals(next, _undone[slot]))
            {
                throw new AutomationException(AutomationErrorCodes.RedoRefused, labels.Count == 0
                    ? $"The next redo step is \"{next.Description}\", which this session did not undo; nothing was redone. Redo it in the editor (Ctrl+Y) if that is intended."
                    : $"Only the next {labels.Count} redo step(s) were undone by this session; nothing was redone. Use steps {labels.Count} or fewer.");
            }

            labels.Add(next.Description);
            next = next.RedoChild;
        }

        await _host.RedoAsync(steps, ct);
        _undone.RemoveRange(_undone.Count - steps, steps);
        _host.ReportActivity(steps == 1 ? $"{Prefix(r)} redid \"{labels[0]}\"" : $"{Prefix(r)} redid {steps} automation runs");
        return Ok(r.Command, new UndoResult
        {
            Undone = new List<string>(),
            Redone = labels,
            Next = _host.RecentUndoLabels(1).FirstOrDefault(),
        });
    }

    // ---- checkpoint / diff ------------------------------------------------------------------------

    /// <summary>
    /// <c>checkpoint</c> — args: <c>name</c> (1-64 characters). Remembers the level's structure in memory: every
    /// object's kind, class and field values, every brush's position, flags, geometry and textures. No document
    /// change, no undo step. At most <see cref="MaxCheckpoints"/> are kept (the oldest is dropped); reusing a
    /// name replaces that checkpoint.
    /// </summary>
    private async Task<AutomationResponse> CheckpointAsync(AutomationRequest r, CancellationToken ct)
    {
        string name = Str(r, "name") ?? throw new AutomationException(AutomationErrorCodes.Usage, "checkpoint needs a 'name'.");
        if (name.Length is 0 or > MaxCheckpointName || name.Any(char.IsControl))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"A checkpoint name must be 1-{MaxCheckpointName} characters without control characters.");
        }

        await EnsureLevelAsync(r, ct);
        EditorDocument doc = RequireDocument();
        LevelInspector.Snapshot snap = LevelInspector.Take(LevelInspector.Context(_host, doc), name, doc);
        bool replaced = _checkpoints.RemoveAll(c => c.Name == name) > 0;
        _checkpoints.Add(snap);
        if (_checkpoints.Count > MaxCheckpoints)
        {
            _checkpoints.RemoveAt(0);
        }

        _host.ReportActivity($"{Prefix(r)} checkpoint \"{name}\"");
        return new AutomationResponse
        {
            Ok = true,
            Command = r.Command,
            Result = new JsonObject
            {
                ["name"] = name,
                ["objects"] = snap.Objects,
                ["brushes"] = snap.Brushes,
                ["replaced"] = replaced,
                ["kept"] = new JsonArray(_checkpoints.Select(c => (JsonNode?)c.Name).ToArray()),
            },
        };
    }

    /// <summary>
    /// <c>diff</c> — args: <c>since</c> (a checkpoint name), <c>maxChanges?</c> (default 100, at most 1000). What
    /// was added, removed and changed since the checkpoint, by UID; object changes list each field's before and
    /// after value, brush changes name the aspect (position / rotation / flags / tags, with values; geometry /
    /// textures, without).
    /// </summary>
    private async Task<AutomationResponse> DiffAsync(AutomationRequest r, CancellationToken ct)
    {
        string since = Str(r, "since") ?? throw new AutomationException(AutomationErrorCodes.Usage, "diff needs 'since' (a checkpoint name).");
        double max = Num(r, "maxChanges") ?? 100;
        if (max < 1 || max > 1000 || max != Math.Floor(max))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'maxChanges' must be a whole number from 1 to 1000.");
        }

        await EnsureLevelAsync(r, ct);
        EditorDocument doc = RequireDocument();
        LevelInspector.Snapshot before = _checkpoints.LastOrDefault(c => c.Name == since)
            ?? throw new AutomationException(AutomationErrorCodes.Usage, _checkpoints.Count == 0
                ? $"No checkpoint named '{since}': none has been taken in this session."
                : $"No checkpoint named '{since}'. Checkpoints: {string.Join(", ", _checkpoints.Select(c => c.Name))}.");
        if (!ReferenceEquals(before.Document, doc))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Checkpoint '{since}' was taken on a different level than the one now open.");
        }

        LevelInspector.Snapshot now = LevelInspector.Take(LevelInspector.Context(_host, doc), "now", doc);
        JsonObject diff = LevelInspector.Diff(before, now, (int)max);
        _host.ReportActivity($"{Prefix(r)} diff since \"{since}\"");
        return new AutomationResponse { Ok = true, Command = r.Command, Result = diff };
    }

    // ---- inspect / query / catalog / api ------------------------------------------------------------

    /// <summary>
    /// <c>inspect</c> — args: <c>uid</c>, <c>level?</c>. One object or brush in full: kind, class, name, position,
    /// orientation, every field with its type and value (the same fields as Lua <c>obj:fields()</c>), links out and
    /// in, tags, and for a brush its flags, face count, bounds and textures.
    /// </summary>
    private async Task<AutomationResponse> InspectAsync(AutomationRequest r, CancellationToken ct)
    {
        int uid = Int(r, "uid") ?? throw new AutomationException(AutomationErrorCodes.Usage, "inspect needs a 'uid'.");
        await EnsureLevelAsync(r, ct);
        EditorDocument doc = RequireDocument();
        JsonObject result = Facade(() => LevelInspector.Inspect(LevelInspector.Context(_host, doc), uid));
        return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
    }

    /// <summary>
    /// <c>query</c> — args: <c>what?</c> (objects | brushes, default objects), <c>kind?</c>, <c>class?</c> and
    /// <c>name?</c> (globs with * and ?), <c>tag?</c> (key or key=value), <c>region?</c> (6 numbers: two corners),
    /// <c>limit?</c> (default 50, max 500), <c>offset?</c>, <c>level?</c>. Matching UIDs sorted ascending, paged.
    /// </summary>
    private async Task<AutomationResponse> QueryAsync(AutomationRequest r, CancellationToken ct)
    {
        string what = (Str(r, "what") ?? "objects").ToLowerInvariant();
        int limit = Int(r, "limit") ?? QueryDefaultLimit;
        int offset = Int(r, "offset") ?? 0;
        if (limit < 1 || limit > QueryMaxLimit)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"'limit' must be 1 to {QueryMaxLimit}.");
        }

        if (offset < 0)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'offset' cannot be negative.");
        }

        double[]? region = null;
        if (r.Args["region"] is JsonNode rn)
        {
            double? Number(JsonNode? v) =>
                v is not JsonValue jv ? null
                : jv.TryGetValue(out double d) ? d
                : jv.TryGetValue(out int i) ? i
                : jv.TryGetValue(out long l) ? l
                : null;
            double?[] values = rn is JsonArray arr && arr.Count == 6 ? arr.Select(Number).ToArray() : Array.Empty<double?>();
            region = values.Length == 6 && values.All(v => v is double d && double.IsFinite(d))
                ? values.Select(v => v!.Value).ToArray()
                : throw new AutomationException(AutomationErrorCodes.Usage, "'region' must be 6 numbers: minx, miny, minz, maxx, maxy, maxz.");
        }

        var spec = new LevelInspector.QuerySpec(what, Str(r, "kind"), Str(r, "class"), Str(r, "name"), Str(r, "tag"), region, limit, offset);
        await EnsureLevelAsync(r, ct);
        EditorDocument doc = RequireDocument();
        JsonObject result = Facade(() => LevelInspector.Query(LevelInspector.Context(_host, doc), spec));
        return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
    }

    /// <summary>
    /// <c>catalog</c> — args: <c>op</c> (kinds | classes | describe | textures), <c>kind?</c>, <c>class?</c>,
    /// <c>filter?</c>, <c>limit?</c>, <c>stockOnly?</c>. The same backing code as the Lua <c>catalog</c> global:
    /// placeable kinds, class names (game tables need a mounted RF install; events always), one kind's or
    /// class's fields and links, texture names with provenance. Needs no open level.
    /// </summary>
    private Task<AutomationResponse> CatalogAsync(AutomationRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string op = (Str(r, "op") ?? throw new AutomationException(AutomationErrorCodes.Usage, "catalog needs an 'op': kinds, classes, describe or textures.")).ToLowerInvariant();
        int? limit = Int(r, "limit");
        if (limit is < 1 or > 5000)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'limit' must be 1 to 5000.");
        }

        JsonObject result = Facade(() => LevelInspector.Catalog(
            LevelInspector.Context(_host, _host.Document), _host.Assets is not null, op, Str(r, "kind"), Str(r, "class"), Str(r, "filter"), limit, Bool(r, "stockOnly")));
        return Task.FromResult(new AutomationResponse { Ok = true, Command = r.Command, Result = result });
    }

    /// <summary>
    /// <c>api</c> — args: <c>member?</c>, <c>format?</c> (markdown | lua). The scripting reference of this build:
    /// one global, type or member; without <c>member</c> an index (one line per global, type and kit function), or
    /// with <c>format</c> the whole Markdown reference / the Lua completion stub.
    /// </summary>
    private static AutomationResponse Api(AutomationRequest r)
    {
        string? member = Str(r, "member");
        string? format = Str(r, "format")?.ToLowerInvariant();
        if (format is not (null or "markdown" or "lua"))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"Unknown format '{format}' (use markdown or lua).");
        }

        string name;
        string text;
        if (member is not null)
        {
            if (format == "lua")
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "A member is returned as text; drop format lua.");
            }

            (name, text) = ApiReferenceText.Member(member);
        }
        else if (format == "lua")
        {
            (name, text) = ("stub", ScriptApiReference.GenerateLuaStub());
        }
        else if (format == "markdown")
        {
            (name, text) = ("reference", ApiReferenceText.Markdown());
        }
        else
        {
            (name, text) = ("index", ApiReferenceText.Index());
        }

        return new AutomationResponse
        {
            Ok = true,
            Command = r.Command,
            Result = new JsonObject
            {
                ["name"] = name,
                ["apiVersion"] = ScriptApiV1.Version,
                ["text"] = text,
                ["truncated"] = false,
            },
        };
    }

    /// <summary>Runs a facade read, turning the facade's own errors (unknown kind, …) into usage errors with their hint.</summary>
    private static T Facade<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (ScriptApiException ex)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, string.IsNullOrEmpty(ex.Hint) ? ex.Message : $"{ex.Message} {ex.Hint}");
        }
    }

    private static int? Int(AutomationRequest r, string name)
    {
        double? d = Num(r, name);
        if (d is null)
        {
            return null;
        }

        return d.Value == Math.Floor(d.Value) && d.Value is >= int.MinValue and <= int.MaxValue
            ? (int)d.Value
            : throw new AutomationException(AutomationErrorCodes.Usage, $"Argument '{name}' must be a whole number.");
    }
}
