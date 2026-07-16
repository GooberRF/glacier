using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Assets;

namespace Ged.Core.Scripting;

/// <summary>
/// The root of the host-neutral script facade (plan §5.1). It projects the existing editor
/// handles — it does not clone the model — and exposes the fixed set of Lua globals
/// (<see cref="Globals"/>): <c>ged</c>, <c>level</c>, <c>selection</c>, <c>assets</c>,
/// <c>ops</c>, <c>lint</c>, <c>log</c>, <c>rng</c>. This class has no engine dependency, so the
/// entire API is unit-testable without a window and without MoonSharp.
/// </summary>
public sealed class ScriptContext
{
    public ScriptContext(ScriptServices services, ScriptRunOptions options, ScriptLog log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        Document = services.Document ?? throw new ArgumentException("ScriptServices.Document is required.", nameof(services));
        Services = services;
        Options = options;
        Log = log ?? new ScriptLog();
        Rng = new ScriptRng(options.Seed);

        Brushes = services.Brushes ?? new BrushEditor(Document);
        Links = services.Links ?? new LinkService(Document);
        Groups = services.Groups ?? new GroupService(Document);
        Assets = services.Assets;
        Confirmation = options.DryRun
            ? new DenyAllConfirmation()
            : (options.AllowDestructive ? new AllowAllConfirmation() : services.Confirmation ?? new DenyAllConfirmation());
        Operations = services.Operations ?? new CoreScriptOperations(this, services);

        Ged = new ScriptGed(this);
        Level = new ScriptLevel(this);
        Selection = new ScriptSelection(this);
        AssetsApi = new ScriptAssets(this);
        Ops = new ScriptOps(this);
        Lint = new ScriptLint(this);
    }

    // ---- Lua-facing globals ---------------------------------------------------

    public ScriptGed Ged { get; }

    public ScriptLevel Level { get; }

    public ScriptSelection Selection { get; }

    public ScriptAssets AssetsApi { get; }

    public ScriptOps Ops { get; }

    public ScriptLint Lint { get; }

    public ScriptLog Log { get; }

    public ScriptRng Rng { get; }

    // ---- Shared internal state (reachable because the facade lives in Ged.Core) ----

    internal EditorDocument Document { get; }

    internal BrushEditor Brushes { get; }

    internal LinkService Links { get; }

    internal GroupService Groups { get; }

    internal AssetVfs? Assets { get; }

    internal IScriptOperations Operations { get; }

    internal IScriptConfirmation Confirmation { get; }

    internal ScriptServices Services { get; }

    internal ScriptRunOptions Options { get; }

    /// <summary>The run's cancellation token (timeout + Stop button), honored by long operations.</summary>
    internal System.Threading.CancellationToken Cancellation { get; set; } = System.Threading.CancellationToken.None;

    /// <summary>True while running in dry-run/preview (transaction always rolled back).</summary>
    public bool DryRun => Options.DryRun;

    /// <summary>The number of contributed lint findings + a running mutation summary for dry-run reporting.</summary>
    internal ScriptChangeSummary Changes { get; } = new();

    /// <summary>
    /// The globals the engine binds by name (plan §5.1 / §5.8). The host maps each to a Lua global;
    /// the API-surface snapshot test asserts these against <see cref="ScriptApiV1.Globals"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object> Globals => new Dictionary<string, object>
    {
        ["ged"] = Ged,
        ["level"] = Level,
        ["selection"] = Selection,
        ["assets"] = AssetsApi,
        ["ops"] = Ops,
        ["lint"] = Lint,
        ["log"] = Log,
        ["rng"] = Rng,
    };

    /// <summary>Throws when a destructive op is not permitted; used by delete/save/package/playtest.</summary>
    internal void RequireDestructive(string what)
    {
        if (DryRun)
        {
            throw new ScriptApiException(
                $"'{what}' is a destructive operation and is disabled in dry-run mode.",
                "Run the script for real (not Dry-Run) to allow it.");
        }

        if (!Confirmation.Confirm("Script action", $"The script wants to {what}. Allow it?"))
        {
            throw new ScriptApiException(
                $"'{what}' was not confirmed.",
                "Add a --@allow-destructive header, or confirm when prompted.");
        }
    }
}

/// <summary>Accumulates a lightweight change summary for dry-run / result reporting (plan §5.7).</summary>
internal sealed class ScriptChangeSummary
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Record(string kind, int count = 1)
    {
        _counts.TryGetValue(kind, out int cur);
        _counts[kind] = cur + count;
    }

    public IReadOnlyDictionary<string, int> Counts => _counts;

    public int Total
    {
        get
        {
            int sum = 0;
            foreach (int v in _counts.Values)
            {
                sum += v;
            }

            return sum;
        }
    }

    public string Describe()
    {
        if (_counts.Count == 0)
        {
            return "no changes";
        }

        var parts = new List<string>();
        foreach (KeyValuePair<string, int> kv in _counts)
        {
            parts.Add($"{kv.Value} {kv.Key}");
        }

        return string.Join(", ", parts);
    }
}
