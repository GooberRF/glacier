using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Modules;

namespace Ged.Automation;

// The module library commands: "modules" (read-only: stats, a filtered list, or one module) and "harvest" (reads
// level files read-only and writes the library folder, which must lie inside the allowed write folders). Neither
// touches the open level, its undo history or its compiled geometry.
public sealed partial class AutomationCommands
{
    /// <summary>
    /// <c>modules</c> — args: <c>id?</c> (show one), <c>category?</c>, <c>attach?</c>, <c>role?</c>, <c>style?</c>,
    /// <c>group?</c>, <c>stockOnly?</c>, <c>includeRejected?</c> (also modules the mapper rejected in curation.json),
    /// <c>limit?</c>, <c>library?</c> (a <c>library/modules</c> folder; default: located).
    /// No level is needed.
    /// </summary>
    private Task<AutomationResponse> ModulesAsync(AutomationRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ModuleLibrary lib = ModuleLibraryCommands.Open(Str(r, "library"));
        if (Str(r, "id") is { } id)
        {
            return Task.FromResult(new AutomationResponse { Ok = true, Command = r.Command, Result = ModuleLibraryCommands.Show(lib, id) });
        }

        double limit = Num(r, "limit") ?? ModuleLibraryCommands.DefaultLimit;
        if (limit < 1 || limit != Math.Floor(limit))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'limit' must be a whole number of at least 1.");
        }

        var q = new ModuleQuery
        {
            Category = Str(r, "category"),
            Attach = Str(r, "attach"),
            Role = Str(r, "role"),
            Style = Str(r, "style"),
            Group = Str(r, "group"),
            StockOnly = Bool(r, "stockOnly"),
            IncludeRejected = Bool(r, "includeRejected"),
        };
        JsonObject result = ModuleLibraryCommands.List(lib, q, (int)Math.Min(limit, ModuleLibraryCommands.MaxLimit));
        return Task.FromResult(new AutomationResponse { Ok = true, Command = r.Command, Result = result });
    }

    /// <summary>
    /// <c>harvest</c> — args: <c>source?</c> (one level or packfile; needs <c>group</c> and <c>out</c>), <c>sources?</c>
    /// (a manifest; default <c>library/sources.json</c> beside the located library), <c>root?</c>, <c>out?</c>,
    /// <c>group?</c>, <c>jobs?</c>. Level files are only read; the output folder must be inside the allowed write
    /// folders and may not be, contain or lie inside a source folder. The host's RF install supplies stock textures.
    /// </summary>
    private async Task<AutomationResponse> HarvestAsync(AutomationRequest r, CancellationToken ct)
    {
        string? outDir = Str(r, "out");
        string? source = Str(r, "source");
        if (outDir is null && source is null)
        {
            ModuleLibraryLocation loc = ModuleLibraryLocator.Locate();
            outDir = loc.Directory;
        }

        if (outDir is null)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "harvest needs an output folder ('out').");
        }

        // The library folder must be writable for this session (checked as a file inside it).
        string full = _host.PathPolicy.Require(System.IO.Path.Combine(outDir, "index.json"), "write the module library into");
        outDir = System.IO.Path.GetDirectoryName(full)!;
        double jobs = Num(r, "jobs") ?? Math.Max(1, Environment.ProcessorCount - 1);
        if (jobs < 1 || jobs != Math.Floor(jobs))
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "'jobs' must be a whole number of at least 1.");
        }

        // Off the document thread: a harvest reads many levels and must not freeze an attached editor.
        string? rf = _host.RfInstallDir;
        string? sources = Str(r, "sources"), root = Str(r, "root"), group = Str(r, "group");
        (ModuleHarvestReport report, JsonObject result) = await Task.Run(() => ModuleLibraryCommands.Harvest(
            source, sources, root, outDir, group, rf, (int)jobs, log: null, environment: null, ct), ct).ConfigureAwait(true);
        _host.ReportActivity($"{Prefix(r)} harvested {report.Modules.Count} module(s) from {report.LevelsRead} level(s)");
        return new AutomationResponse { Ok = true, Command = r.Command, Result = result };
    }
}
