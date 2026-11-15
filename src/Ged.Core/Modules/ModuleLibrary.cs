using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;

namespace Ged.Core.Modules;

/// <summary>Where the module library was found, and how.</summary>
/// <param name="Directory">The <c>library/modules</c> folder, or null when none was found.</param>
/// <param name="How">explicit | environment | install | checkout | none.</param>
public sealed record ModuleLibraryLocation(string? Directory, string How);

/// <summary>
/// The one place the module library is located (CLI, MCP and the Lua layer all use it): an explicit folder
/// (<c>--modules</c>) → the <see cref="EnvironmentVariable"/> → <c>&lt;base&gt;/library/modules</c> → walking up from
/// the base directory to the folder holding <c>Glacier.sln</c> and using its <c>library/modules</c> → none.
/// </summary>
public static class ModuleLibraryLocator
{
    /// <summary>Environment variable naming the module library folder.</summary>
    public const string EnvironmentVariable = "GED_MODULE_LIBRARY";

    /// <summary>The library folder relative to an install or checkout root.</summary>
    public static readonly string RelativeDirectory = Path.Combine("library", "modules");

    /// <summary>Locates the library; never throws.</summary>
    public static ModuleLibraryLocation Locate(string? explicitDirectory = null, Func<string, string?>? environment = null, string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return new ModuleLibraryLocation(Full(explicitDirectory), "explicit");
        }

        string? env = (environment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new ModuleLibraryLocation(Full(env), "environment");
        }

        string start = baseDirectory ?? AppPaths.BaseDirectory;
        string beside = Path.Combine(start, RelativeDirectory);
        if (System.IO.Directory.Exists(beside))
        {
            return new ModuleLibraryLocation(Full(beside), "install");
        }

        if (FindCheckoutRoot(start) is { } repo)
        {
            return new ModuleLibraryLocation(Full(Path.Combine(repo, RelativeDirectory)), "checkout");
        }

        return new ModuleLibraryLocation(null, "none");
    }

    /// <summary>The folder holding <c>Glacier.sln</c> at or above <paramref name="start"/>, or null.</summary>
    public static string? FindCheckoutRoot(string? start)
    {
        try
        {
            var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // An unreadable ancestor ends the walk.
        }

        return null;
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>A module library query (every filter optional; results ordered by score, then id).</summary>
public sealed class ModuleQuery
{
    public string? Category { get; init; }

    public string? Attach { get; init; }

    public string? Role { get; init; }

    /// <summary>Matches a module listing this style anywhere in its style list.</summary>
    public string? Style { get; init; }

    public string? Group { get; init; }

    /// <summary>Only modules whose every texture is stock.</summary>
    public bool StockOnly { get; init; }

    /// <summary>Only modules whose every texture resolves in this asset library.</summary>
    public AssetVfs? ResolvesIn { get; init; }

    /// <summary>Minimum size per local axis (null components = no bound).</summary>
    public double? MinX { get; init; }

    public double? MaxX { get; init; }

    public double? MinY { get; init; }

    public double? MaxY { get; init; }

    public double? MinZ { get; init; }

    public double? MaxZ { get; init; }

    /// <summary>Only modules that can stretch on this axis ("x", "y" or "z").</summary>
    public string? Stretchable { get; init; }

    public double MinScore { get; init; }

    public int? Limit { get; init; }

    /// <summary>Also modules the mapper rejected in <c>curation.json</c> (left out by default).</summary>
    public bool IncludeRejected { get; init; }
}

/// <summary>Counts over a library (or one query).</summary>
public sealed record ModuleLibraryStats(
    int Modules,
    int Occurrences,
    IReadOnlyDictionary<string, int> ByGroup,
    IReadOnlyDictionary<string, int> ByCategory,
    IReadOnlyDictionary<string, int> ByAttach,
    IReadOnlyDictionary<string, int> ByRole,
    IReadOnlyDictionary<string, int> ByStyle,
    int CustomTextureModules,
    ModuleCurationCounts Curation);

/// <summary>
/// The harvested module library: every group's <c>index.json</c> under one <c>library/modules</c> folder, with
/// deterministic queries. An absent or empty library is a normal state: <see cref="Available"/> is false with a
/// human <see cref="Reason"/>, queries return nothing, and nothing throws. The mapper's <c>curation.json</c>
/// (<see cref="ModuleCuration"/>) is applied on load: rejected modules are left out of <see cref="Modules"/>,
/// <see cref="Find"/> and every query unless asked for, relabels replace the harvested category / role / styles, and
/// kept modules rank by <see cref="ModuleInfo.SelectionScore"/>.
/// </summary>
public sealed class ModuleLibrary
{
    private readonly Dictionary<string, ModuleInfo> _byId;

    private ModuleLibrary(string? directory, string how, IReadOnlyList<ModuleInfo> modules, string? reason, IReadOnlyList<string> warnings,
        IReadOnlyList<string>? unknownCurationIds = null, int ignoredCuration = 0, int curationEntries = 0)
    {
        Directory = directory;
        How = how;
        AllModules = modules;
        Modules = modules.Where(m => !m.Rejected).ToList();
        Reason = reason ?? (Modules.Count == 0 && AllModules.Count > 0
            ? $"Every module in the library at {directory} is rejected by its {ModuleCuration.FileName}."
            : null);
        Warnings = warnings;
        UnknownCurationIds = unknownCurationIds ?? Array.Empty<string>();
        IgnoredCurationEntries = ignoredCuration;
        CurationEntries = curationEntries;
        _byId = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleInfo m in modules)
        {
            _byId.TryAdd(m.Id, m);
        }
    }

    /// <summary>The library folder (<c>…/library/modules</c>), or null when none was located.</summary>
    public string? Directory { get; }

    /// <summary>How the folder was located (see <see cref="ModuleLibraryLocation.How"/>).</summary>
    public string How { get; }

    /// <summary>True when at least one module is loaded.</summary>
    public bool Available => Modules.Count > 0;

    /// <summary>Why the library is unavailable (null when available).</summary>
    public string? Reason { get; }

    /// <summary>Indexes that could not be read (skipped).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Every module the mapper has not rejected, ordered by group, category, id.</summary>
    public IReadOnlyList<ModuleInfo> Modules { get; }

    /// <summary>Every module, rejected ones included, ordered by group, category, id.</summary>
    public IReadOnlyList<ModuleInfo> AllModules { get; }

    /// <summary>Ids <c>curation.json</c> names that the library does not hold (sorted; ignored).</summary>
    public IReadOnlyList<string> UnknownCurationIds { get; }

    /// <summary>How many <c>curation.json</c> entries were ignored for a bad value.</summary>
    public int IgnoredCurationEntries { get; }

    /// <summary>How many valid <c>curation.json</c> entries were read.</summary>
    public int CurationEntries { get; }

    /// <summary>The groups present, sorted.</summary>
    public IReadOnlyList<string> Groups => Modules.Select(m => m.Group).Distinct(StringComparer.Ordinal).OrderBy(g => g, StringComparer.Ordinal).ToList();

    /// <summary>Locates (<see cref="ModuleLibraryLocator"/>) and loads the library.</summary>
    public static ModuleLibrary Locate(string? explicitDirectory = null, Func<string, string?>? environment = null, string? baseDirectory = null)
    {
        ModuleLibraryLocation loc = ModuleLibraryLocator.Locate(explicitDirectory, environment, baseDirectory);
        return loc.Directory is null
            ? new ModuleLibrary(null, loc.How, Array.Empty<ModuleInfo>(), "No module library was found (no library/modules folder beside Glacier or in a source checkout; set " + ModuleLibraryLocator.EnvironmentVariable + " or pass --modules).", Array.Empty<string>())
            : Load(loc.Directory, loc.How);
    }

    /// <summary>Loads the library at <paramref name="directory"/> (a <c>library/modules</c> folder); never throws.</summary>
    public static ModuleLibrary Load(string directory, string how = "explicit")
    {
        ArgumentNullException.ThrowIfNull(directory);
        string dir;
        try
        {
            dir = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ModuleLibrary(directory, how, Array.Empty<ModuleInfo>(), $"The module library path is not valid: {ex.Message}", Array.Empty<string>());
        }

        if (!System.IO.Directory.Exists(dir))
        {
            return new ModuleLibrary(dir, how, Array.Empty<ModuleInfo>(), $"The module library folder does not exist: {dir}. Run 'glacier-cli modules harvest' to build it.", Array.Empty<string>());
        }

        var modules = new List<ModuleInfo>();
        var warnings = new List<string>();
        List<string> indexes;
        try
        {
            indexes = System.IO.Directory.EnumerateDirectories(dir)
                .Select(d => Path.Combine(d, "index.json"))
                .Where(File.Exists)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModuleLibrary(dir, how, Array.Empty<ModuleInfo>(), $"The module library folder could not be read: {ex.Message}", Array.Empty<string>());
        }

        foreach (string index in indexes)
        {
            try
            {
                modules.AddRange(ModuleJson.ReadIndex(File.ReadAllText(index), Path.GetDirectoryName(index)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                warnings.Add($"{index}: {ex.Message}");
            }
        }

        // The mapper's curation (never written by a harvest): verdicts, relabels, notes.
        var curationWarnings = new List<string>();
        IReadOnlyDictionary<string, ModuleCurationEntry> curation = ModuleCuration.Load(dir, curationWarnings);
        int ignored = curationWarnings.Count(w => w.EndsWith("(entry ignored).", StringComparison.Ordinal));
        warnings.AddRange(curationWarnings);
        var unknown = new List<string>();
        if (curation.Count > 0)
        {
            var known = new HashSet<string>(modules.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            unknown.AddRange(curation.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
            for (int i = 0; i < modules.Count; i++)
            {
                if (curation.TryGetValue(modules[i].Id, out ModuleCurationEntry? entry))
                {
                    modules[i] = modules[i].WithCuration(entry);
                }
            }

            if (unknown.Count > 0)
            {
                warnings.Add($"{ModuleCuration.FileName}: {unknown.Count} entr{(unknown.Count == 1 ? "y names a module" : "ies name modules")} the library does not hold (ignored): "
                    + string.Join(", ", unknown.Take(10)) + (unknown.Count > 10 ? ", ..." : string.Empty));
            }
        }

        List<ModuleInfo> ordered = modules
            .OrderBy(m => m.Group, StringComparer.Ordinal)
            .ThenBy(m => m.Category, StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        string? reason = ordered.Count > 0 ? null
            : indexes.Count == 0 ? $"The module library at {dir} has no group indexes yet. Run 'glacier-cli modules harvest' to build it."
            : $"The module library at {dir} holds no modules.";
        return new ModuleLibrary(dir, how, ordered, reason, warnings, unknown, ignored, curation.Count);
    }

    /// <summary>An empty library with a reason (e.g. for hosts that disable it).</summary>
    public static ModuleLibrary Empty(string reason) => new(null, "none", Array.Empty<ModuleInfo>(), reason, Array.Empty<string>());

    /// <summary>The module with this id (case-insensitive), or null; a rejected module only with <paramref name="includeRejected"/>.</summary>
    public ModuleInfo? Find(string id, bool includeRejected = false) =>
        id is not null && _byId.TryGetValue(id.Trim(), out ModuleInfo? m) && (includeRejected || !m.Rejected) ? m : null;

    /// <summary>The modules matching <paramref name="q"/>, by selection score (descending; kept modules rank higher) then id.</summary>
    public IReadOnlyList<ModuleInfo> Query(ModuleQuery? q = null)
    {
        q ??= new ModuleQuery();
        int stretchAxis = ModuleVocabulary.AxisIndex(q.Stretchable);
        IEnumerable<ModuleInfo> hits = (q.IncludeRejected ? AllModules : Modules).Where(m =>
            Eq(q.Category, m.Category) && Eq(q.Attach, m.Attach) && Eq(q.Role, m.Role) && Eq(q.Group, m.Group)
            && (q.Style is null || m.Styles.Contains(q.Style, StringComparer.OrdinalIgnoreCase))
            && (!q.StockOnly || m.Textures.All(t => t.Stock))
            && In(m.Size.X, q.MinX, q.MaxX) && In(m.Size.Y, q.MinY, q.MaxY) && In(m.Size.Z, q.MinZ, q.MaxZ)
            && (stretchAxis < 0 || m.Stretch(stretchAxis) is not null)
            && m.Score >= q.MinScore
            && (q.ResolvesIn is null || m.Textures.All(t => q.ResolvesIn.ResolveTexture(t.Name) is not null)))
            .OrderByDescending(m => m.SelectionScore)
            .ThenBy(m => m.Id, StringComparer.Ordinal);
        if (q.Limit is int limit)
        {
            hits = hits.Take(Math.Max(0, limit));
        }

        return hits.ToList();
    }

    /// <summary>
    /// Counts over the library's modules (rejected ones left out), or over <paramref name="subset"/>; the curation counts
    /// always cover every module.
    /// </summary>
    public ModuleLibraryStats Stats(IReadOnlyList<ModuleInfo>? subset = null)
    {
        IReadOnlyList<ModuleInfo> mods = subset ?? Modules;
        static SortedDictionary<string, int> Count(IEnumerable<string> keys)
        {
            var d = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                d[k] = d.GetValueOrDefault(k) + 1;
            }

            return d;
        }

        return new ModuleLibraryStats(
            mods.Count,
            mods.Sum(m => m.Occurrences),
            Count(mods.Select(m => m.Group)),
            Count(mods.Select(m => m.Category)),
            Count(mods.Select(m => m.Attach)),
            Count(mods.Select(m => m.Role)),
            Count(mods.Select(m => m.PrimaryStyle)),
            mods.Count(m => m.CustomTextures),
            new ModuleCurationCounts(
                AllModules.Count(m => m.Kept),
                AllModules.Count(m => m.Rejected),
                AllModules.Count(m => m.Relabelled),
                AllModules.Count(m => m.Verdict is null),
                UnknownCurationIds.Count,
                IgnoredCurationEntries));
    }

    /// <summary>
    /// The module's brushes in its local frame (from its .rfg), in the module's own order. Throws
    /// <see cref="IOException"/> / <see cref="InvalidDataException"/> when the file is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<Brush> LoadBrushes(ModuleInfo module)
    {
        ArgumentNullException.ThrowIfNull(module);
        string path = module.RfgPath ?? throw new IOException($"Module '{module.Id}' has no .rfg path (it was not loaded from a library folder).");
        RfgFile rfg;
        try
        {
            rfg = RfgFile.Load(path);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Module '{module.Id}': {path} is not a readable .rfg ({ex.Message}).", ex);
        }

        return rfg.Groups.SelectMany(g => g.Brushes.Brushes).ToList();
    }

    private static bool Eq(string? want, string have) => want is null || string.Equals(want, have, StringComparison.OrdinalIgnoreCase);

    private static bool In(double v, double? min, double? max) => (min is not double lo || v >= lo - 1e-9) && (max is not double hi || v <= hi + 1e-9);
}
