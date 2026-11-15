using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Modules;

namespace Ged.Automation;

/// <summary>
/// The module library as automation results: the <c>modules</c> command (stats, filtered list, one module) and the
/// <c>harvest</c> command. Shared verbatim by <c>glacier-cli modules</c> and the MCP tools, so both print the same JSON.
/// </summary>
public static class ModuleLibraryCommands
{
    /// <summary>The default list page size.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest list page.</summary>
    public const int MaxLimit = 500;

    /// <summary>Locates and loads the library (an explicit folder wins; see <see cref="ModuleLibraryLocator"/>).</summary>
    public static ModuleLibrary Open(string? libraryDir, Func<string, string?>? environment = null) =>
        ModuleLibrary.Locate(libraryDir, environment);

    /// <summary>The library header every result carries.</summary>
    public static JsonObject Header(ModuleLibrary lib) => new()
    {
        ["available"] = lib.Available,
        ["reason"] = lib.Reason,
        ["library"] = lib.Directory,
        ["located"] = lib.How,
        ["warnings"] = lib.Warnings.Count == 0 ? null : new JsonArray(lib.Warnings.Select(w => (JsonNode?)w).ToArray()),
    };

    /// <summary>Counts over the library.</summary>
    public static JsonObject Stats(ModuleLibrary lib)
    {
        JsonObject o = Header(lib);
        o["stats"] = StatsNode(lib.Stats());
        return o;
    }

    /// <summary>A filtered page of the library (ordered by score, then id) plus the stats of the whole library.</summary>
    public static JsonObject List(ModuleLibrary lib, ModuleQuery query, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(query);
        IReadOnlyList<ModuleInfo> all = lib.Query(query);
        int take = Math.Clamp(limit, 1, MaxLimit);
        JsonObject o = Header(lib);
        o["stats"] = StatsNode(lib.Stats());
        o["total"] = all.Count;
        o["returned"] = Math.Min(take, all.Count);
        o["modules"] = new JsonArray(all.Take(take).Select(m => (JsonNode?)Summary(m)).ToArray());
        return o;
    }

    /// <summary>One module in full (the index entry plus its .rfg path), or a usage error naming close ids.</summary>
    public static JsonObject Show(ModuleLibrary lib, string id)
    {
        ModuleInfo m = lib.Find(id, includeRejected: true) ?? throw new AutomationException(AutomationErrorCodes.Usage,
            lib.Available
                ? $"No module '{id}' in the library ({lib.Modules.Count} modules). Use module_library / 'modules list' to see ids."
                : $"No module library: {lib.Reason}");
        JsonObject o = Header(lib);
        JsonObject full = Full(m);
        full["rfg"] = m.RfgPath;
        full["curation"] = new JsonObject
        {
            ["verdict"] = m.Verdict,
            ["note"] = m.CurationNote,
            ["selection_score"] = Math.Round(m.SelectionScore, 4),
            ["relabelled"] = m.Relabelled,
            ["harvested"] = m.Harvested is { } h
                ? new JsonObject { ["category"] = h.Category, ["role"] = h.Role, ["styles"] = new JsonArray(h.Styles.Select(s => (JsonNode?)s).ToArray()) }
                : null,
        };
        o["module"] = full;
        return o;
    }

    /// <summary>The compact list row of a module.</summary>
    public static JsonObject Summary(ModuleInfo m) => new()
    {
        ["id"] = m.Id,
        ["group"] = m.Group,
        ["category"] = m.Category,
        ["role"] = m.Role,
        ["attach"] = m.Attach,
        ["styles"] = new JsonArray(m.Styles.Select(s => (JsonNode?)s).ToArray()),
        ["size"] = new JsonArray(ModuleJson.R(m.Size.X), ModuleJson.R(m.Size.Y), ModuleJson.R(m.Size.Z)),
        ["stretch"] = new JsonArray(m.StretchAxes.Select(a => (JsonNode?)a).ToArray()),
        ["tiles"] = new JsonArray(m.Tiles.Select(t => (JsonNode?)new JsonObject { ["axis"] = t.Axis, ["count"] = t.Count, ["pitch"] = ModuleJson.R(t.Pitch) }).ToArray()),
        ["brushes"] = m.Brushes,
        ["score"] = Math.Round(m.SelectionScore, 4),
        ["verdict"] = m.Verdict,
        ["occurrences"] = m.Occurrences,
        ["mount_height"] = m.MountHeight is double h ? ModuleJson.R(h) : null,
        ["suggested_pitch"] = m.SuggestedPitch is double p ? ModuleJson.R(p) : null,
        ["custom_textures"] = m.CustomTextures,
        ["source"] = m.Provenance.Count > 0 ? m.Provenance[0].Level : null,
    };

    /// <summary>The full index entry of a module as JSON.</summary>
    public static JsonObject Full(ModuleInfo m)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            ModuleJson.WriteModule(w, m);
        }

        return JsonNode.Parse(ms.ToArray())!.AsObject();
    }

    private static JsonObject StatsNode(ModuleLibraryStats s)
    {
        static JsonObject Map(IReadOnlyDictionary<string, int> d)
        {
            var o = new JsonObject();
            foreach ((string k, int v) in d.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                o[k] = v;
            }

            return o;
        }

        return new JsonObject
        {
            ["modules"] = s.Modules,
            ["occurrences"] = s.Occurrences,
            ["custom_texture_modules"] = s.CustomTextureModules,
            ["by_group"] = Map(s.ByGroup),
            ["by_category"] = Map(s.ByCategory),
            ["by_attach"] = Map(s.ByAttach),
            ["by_role"] = Map(s.ByRole),
            ["by_style"] = Map(s.ByStyle),
            ["curation"] = new JsonObject
            {
                ["kept"] = s.Curation.Kept,
                ["rejected"] = s.Curation.Rejected,
                ["relabelled"] = s.Curation.Relabelled,
                ["undecided"] = s.Curation.Undecided,
                ["unknown_ids"] = s.Curation.UnknownIds,
                ["ignored_entries"] = s.Curation.Ignored,
            },
        };
    }

    /// <summary>
    /// Runs a harvest. With <paramref name="source"/> (one level or packfile) the manifest is that file alone (group
    /// <paramref name="group"/>, root its folder); otherwise the manifest at <paramref name="manifestPath"/> (default:
    /// <c>library/sources.json</c> beside the located library) under <paramref name="root"/> (default: the folder above
    /// <c>library/</c>). Output defaults to the located library folder.
    /// </summary>
    public static (ModuleHarvestReport Report, JsonObject Result) Harvest(
        string? source, string? manifestPath, string? root, string? outDir, string? group, string? rfInstall, int jobs,
        Action<string>? log, Func<string, string?>? environment, CancellationToken ct)
    {
        HarvestManifest manifest;
        string rootDir;
        if (source is not null)
        {
            string full = Path.GetFullPath(source);
            if (!File.Exists(full))
            {
                throw new AutomationException(AutomationErrorCodes.Io, $"Level or packfile not found: {full}");
            }

            string ext = Path.GetExtension(full);
            if (!ext.Equals(".rfl", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".vpp", StringComparison.OrdinalIgnoreCase))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"A single harvest source must be a .rfl level or a .vpp packfile: {full}");
            }

            if (manifestPath is not null)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "Give either one level/packfile or a sources manifest, not both.");
            }

            string g = group ?? throw new AutomationException(AutomationErrorCodes.Usage, "A single-source harvest needs a group name (--group).");
            if (!HarvestManifest.IsValidGroup(g))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The group '{g}' is not a valid folder name (letters, digits, '-' and '_').");
            }

            manifest = HarvestManifest.Single(Path.GetFileName(full), g);
            rootDir = Path.GetDirectoryName(full)!;
            if (outDir is null)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "A single-source harvest needs an output folder (--out).");
            }
        }
        else
        {
            ModuleLibraryLocation loc = ModuleLibraryLocator.Locate(null, environment);
            string? located = loc.Directory;
            string? manifestFile = manifestPath ?? (located is null ? null : Path.Combine(Path.GetDirectoryName(located)!, "sources.json"));
            if (manifestFile is null || !File.Exists(manifestFile))
            {
                throw new AutomationException(AutomationErrorCodes.Io, manifestFile is null
                    ? "No sources manifest: pass --sources <file> (none was found beside a module library)."
                    : $"The sources manifest was not found: {manifestFile}");
            }

            try
            {
                manifest = HarvestManifest.Parse(File.ReadAllText(manifestFile));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The sources manifest {manifestFile} is not valid: {ex.Message}");
            }

            // Default root: the folder above library/ (the manifest's own repository).
            rootDir = root ?? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(manifestFile))!)!;
            outDir ??= located ?? Path.Combine(rootDir, "library", "modules");
            if (group is not null && !manifest.Groups.Contains(group, StringComparer.Ordinal))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"The manifest has no group '{group}' (groups: {string.Join(", ", manifest.Groups)}).");
            }
        }

        ModuleHarvestReport report;
        try
        {
            report = ModuleHarvestRunner.Run(new ModuleHarvestRequest
            {
                Manifest = manifest,
                Root = rootDir,
                OutputDir = outDir,
                Group = source is null ? group : null,
                RfInstall = rfInstall,
                Jobs = Math.Max(1, jobs),
                Log = log,
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"The module harvest could not read or write a file: {ex.Message}");
        }
        catch (ModuleHarvestException ex)
        {
            throw new AutomationException(AutomationErrorCodes.PathNotAllowed, ex.Message);
        }

        JsonObject result = JsonNode.Parse(report.ToJson(includeTiming: true))!.AsObject();
        result["output"] = report.OutputDir;
        result["root"] = Path.GetFullPath(rootDir);
        return (report, result);
    }
}
