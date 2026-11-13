using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ged.Core;
using Ged.Core.Compiler;
using Ged.Core.Editing;
using Ged.Core.Lighting;

namespace Ged.Automation;

/// <summary>
/// The handful of editor settings a headless host needs to behave like the editor: the RF install
/// to mount, the Geometry ▸ Build method, the global default Lightmap Method, the default brush
/// textures (so <c>level.place_box</c> textures match the Draw Brush tool), and whether the editor's
/// command-line bridge is enabled. Read-only: the editor owns and writes the settings file.
/// </summary>
public sealed record EditorSettingsSnapshot
{
    /// <summary>The configured Red Faction install directory, or null.</summary>
    public string? RfInstallDir { get; init; }

    /// <summary>Geometry ▸ Build method: true = RED-authentic Shared BSP (default), false = Incremental.</summary>
    public bool UseSharedBspBuild { get; init; } = true;

    /// <summary>Geometry ▸ Repair holes when building (Alpine RED's build repair; default true).</summary>
    public bool RepairBuildHoles { get; init; } = true;

    /// <summary>The global default lightmap method (the per-level sidecar override wins over it).</summary>
    public LightingMethod LightingMethod { get; init; } = new();

    /// <summary>Default floor texture for new brushes.</summary>
    public string DefaultFloorTexture { get; init; } = BrushCreateParams.StockFloorTexture;

    /// <summary>Default wall texture for new brushes.</summary>
    public string DefaultWallTexture { get; init; } = BrushCreateParams.StockWallTexture;

    /// <summary>Default ceiling texture for new brushes.</summary>
    public string DefaultCeilingTexture { get; init; } = BrushCreateParams.StockCeilingTexture;

    /// <summary>Whether the editor accepts command-line / agent connections (default true).</summary>
    public bool EnableAutomationBridge { get; init; } = true;

    /// <summary>The configured prefab library folder, or null for the default library.</summary>
    public string? PrefabDirectory { get; init; }

    /// <summary>The settings file these values were read from, or null (no file: fresh-install defaults).</summary>
    public string? SettingsFile { get; init; }

    /// <summary>The settings a fresh install has (no settings file).</summary>
    public static EditorSettingsSnapshot Defaults { get; } = new();

    /// <summary>The build settings these choices produce, with an optional per-level lightmap override.</summary>
    public BuildSettings ToBuildSettings(LightingMethod? levelOverride = null) => new()
    {
        UseSharedBsp = UseSharedBspBuild,
        RepairBuildHoles = RepairBuildHoles,
        LightingMethod = (levelOverride ?? LightingMethod).Clone(),
    };
}

/// <summary>
/// Reads <see cref="EditorSettingsSnapshot"/> from the editor's settings file
/// (as <see cref="EditorSettingsLocator"/> finds it, or an explicit path). Tolerant by design: a missing,
/// unreadable or malformed file, a missing field, a wrongly-typed field, and unknown fields all fall
/// back to the fresh-install defaults for the affected values — exactly how the editor loads it.
/// Field names and defaults mirror the editor's <c>AppSettings</c>.
/// </summary>
public static class EditorSettingsReader
{
    /// <summary>The pre-fix default texture name that never existed in stock RF (migrated like the editor does).</summary>
    private const string DeadDefaultTexture = "Rck_Default01.tga";

    /// <summary>Reads the editor's settings file as <see cref="EditorSettingsLocator.FindDefault"/> finds it.</summary>
    public static EditorSettingsSnapshot ReadDefault() => Read(EditorSettingsLocator.FindDefault());

    /// <summary>Reads settings from <paramref name="path"/>; defaults when absent or unreadable.</summary>
    public static EditorSettingsSnapshot Read(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return EditorSettingsSnapshot.Defaults;
        }

        try
        {
            if (!File.Exists(path))
            {
                return EditorSettingsSnapshot.Defaults;
            }

            return Parse(File.ReadAllText(path)) with { SettingsFile = path };
        }
        catch (Exception)
        {
            return EditorSettingsSnapshot.Defaults;
        }
    }

    /// <summary>Parses settings JSON text; defaults for anything missing or malformed.</summary>
    public static EditorSettingsSnapshot Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EditorSettingsSnapshot.Defaults;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return EditorSettingsSnapshot.Defaults;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return EditorSettingsSnapshot.Defaults;
            }

            EditorSettingsSnapshot d = EditorSettingsSnapshot.Defaults;
            var method = new LightingMethod
            {
                Base = Int(root, "LightingMethodBase", 0) == (int)LightingBase.Bounced ? LightingBase.Bounced : LightingBase.RedClassic,
                Bounces = Int(root, "LightingMethodBounces", 1) >= 2 ? 2 : 1,
                AmbientOcclusion = Bool(root, "LightingAmbientOcclusion", false),
                SoftShadows = Bool(root, "LightingSoftShadows", false),
                HighResLightmaps = Bool(root, "LightingHighRes", false),
                SeamBlend = Bool(root, "LightingSeamBlend", false),
                CornerLeakFix = Bool(root, "LightingCornerLeakFix", false),
                SmoothGutters = Bool(root, "LightingSmoothGutters", false),
                MoverShadows = Bool(root, "LightingMoverShadows", true),
            };

            string? rfDir = Str(root, "RfInstallDir", null);
            string? prefabDir = Str(root, "PrefabDirectory", null);
            return new EditorSettingsSnapshot
            {
                RfInstallDir = string.IsNullOrWhiteSpace(rfDir) ? null : rfDir,
                PrefabDirectory = string.IsNullOrWhiteSpace(prefabDir) ? null : prefabDir,
                UseSharedBspBuild = Bool(root, "UseSharedBspBuild", d.UseSharedBspBuild),
                RepairBuildHoles = Bool(root, "RepairBuildHoles", d.RepairBuildHoles),
                LightingMethod = method,
                DefaultFloorTexture = Texture(root, "DefaultFloorTexture", BrushCreateParams.StockFloorTexture),
                DefaultWallTexture = Texture(root, "DefaultWallTexture", BrushCreateParams.StockWallTexture),
                DefaultCeilingTexture = Texture(root, "DefaultCeilingTexture", BrushCreateParams.StockCeilingTexture),
                EnableAutomationBridge = Bool(root, "EnableAutomationBridge", d.EnableAutomationBridge),
            };
        }
    }

    private static bool Bool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static int Int(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : fallback;

    private static string? Str(JsonElement root, string name, string? fallback) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

    private static string Texture(JsonElement root, string name, string stock)
    {
        string? value = Str(root, name, null);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, DeadDefaultTexture, StringComparison.OrdinalIgnoreCase)
            ? stock
            : value;
    }
}

/// <summary>
/// Finds the editor's settings file from a tool that may not run from the editor's folder (glacier-cli).
/// The editor itself always uses <see cref="AppPaths.SettingsFile"/>: <c>settings.cfg</c> beside its executable,
/// else the per-user profile copy (<see cref="AppPaths.ProfileSettingsFile"/>). A packaged glacier-cli sits beside
/// the editor, so the first candidate is the editor's own file. The candidates, first existing file wins:
/// <list type="number">
/// <item><see cref="AppPaths.SettingsFile"/> as this process resolves it (beside glacier-cli = beside Glacier);</item>
/// <item>the per-user profile settings (<c>%APPDATA%\Glacier\settings.cfg</c>);</item>
/// <item>when glacier-cli runs from a source checkout (a <c>Glacier.sln</c> above its executable), the editor's
/// build output there (<c>src/Ged.App/bin/&lt;config&gt;/&lt;framework&gt;/settings.cfg</c>, newest first): the
/// editor and glacier-cli build into different folders.</item>
/// </list>
/// </summary>
public static class EditorSettingsLocator
{
    /// <summary>The settings file name the editor writes (see <see cref="AppPaths"/>).</summary>
    public const string FileName = "settings.cfg";

    /// <summary>The editor's settings file for this process, or null when none of the candidates exists.</summary>
    public static string? FindDefault() =>
        Find(Candidates(AppPaths.SettingsFile, AppPaths.ProfileSettingsFile, Analysis.CorpusConfig.FindRepoRoot(AppContext.BaseDirectory)));

    /// <summary>The first existing file of <paramref name="candidates"/>, or null.</summary>
    public static string? Find(IEnumerable<string> candidates)
    {
        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// The candidate settings files in search order (see the class summary): <paramref name="activeSettingsFile"/>,
    /// <paramref name="profileSettingsFile"/>, then, when <paramref name="checkoutRoot"/> is a source checkout, the
    /// editor's build-output settings files, most recently written first.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string activeSettingsFile, string? profileSettingsFile, string? checkoutRoot)
    {
        var list = new List<string> { activeSettingsFile };
        if (!string.IsNullOrEmpty(profileSettingsFile) && !SamePath(profileSettingsFile, activeSettingsFile))
        {
            list.Add(profileSettingsFile);
        }

        if (string.IsNullOrEmpty(checkoutRoot))
        {
            return list;
        }

        string bin = Path.Combine(checkoutRoot, "src", "Ged.App", "bin");
        try
        {
            if (Directory.Exists(bin))
            {
                List<string> built = Directory.EnumerateDirectories(bin)
                    .SelectMany(Directory.EnumerateDirectories)
                    .Select(d => Path.Combine(d, FileName))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
                list.AddRange(built.Where(f => list.TrueForAll(c => !SamePath(c, f))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A checkout that cannot be listed adds no candidates.
        }

        return list;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Which Red Faction install a headless run mounts, and where that choice came from.</summary>
/// <param name="Dir">The install directory, or null when none was given or configured.</param>
/// <param name="Source">Where it came from: <see cref="RfInstallResolver.FromFlag"/>, <see cref="RfInstallResolver.FromEnvironment"/>,
/// <see cref="RfInstallResolver.FromSettings"/> or <see cref="RfInstallResolver.FromNothing"/>.</param>
/// <param name="SettingsFile">The editor settings file that was read (null when none was found).</param>
public readonly record struct RfInstallChoice(string? Dir, string Source, string? SettingsFile)
{
    /// <summary>One line for verbose output, e.g. "D:\Games\RedFaction (from editor settings: C:\Glacier\settings.cfg)".</summary>
    public string Describe() => Source switch
    {
        RfInstallResolver.FromFlag => $"{Dir} (from --rf-dir)",
        RfInstallResolver.FromEnvironment => $"{Dir} (from {RfInstallResolver.EnvironmentVariable})",
        RfInstallResolver.FromSettings => $"{Dir} (from editor settings: {SettingsFile})",
        _ => SettingsFile is null
            ? $"none (no --rf-dir, no {RfInstallResolver.EnvironmentVariable}, and no editor settings file was found)"
            : $"none (no --rf-dir, no {RfInstallResolver.EnvironmentVariable}, and the editor settings at {SettingsFile} name no install)",
    };
}

/// <summary>Resolves which Red Faction install a headless run mounts.</summary>
public static class RfInstallResolver
{
    /// <summary>The environment variable that names an RF install (also used by the test suites).</summary>
    public const string EnvironmentVariable = "GED_RF_DIR";

    /// <summary><see cref="RfInstallChoice.Source"/>: the <c>--rf-dir</c> flag.</summary>
    public const string FromFlag = "--rf-dir";

    /// <summary><see cref="RfInstallChoice.Source"/>: the <c>GED_RF_DIR</c> environment variable.</summary>
    public const string FromEnvironment = EnvironmentVariable;

    /// <summary><see cref="RfInstallChoice.Source"/>: the install configured in the editor's settings.</summary>
    public const string FromSettings = "editor settings";

    /// <summary><see cref="RfInstallChoice.Source"/>: nothing named an install.</summary>
    public const string FromNothing = "none";

    /// <summary>
    /// Priority: the explicit <paramref name="flag"/> (<c>--rf-dir</c>), then <c>GED_RF_DIR</c>, then
    /// the editor's configured install. Returns null when none is set.
    /// </summary>
    public static string? Resolve(string? flag, EditorSettingsSnapshot settings, Func<string, string?>? env = null) =>
        ResolveWithSource(flag, settings, env).Dir;

    /// <summary>As <see cref="Resolve"/>, also naming where the install came from.</summary>
    public static RfInstallChoice ResolveWithSource(string? flag, EditorSettingsSnapshot? settings, Func<string, string?>? env = null)
    {
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return new RfInstallChoice(flag, FromFlag, settings?.SettingsFile);
        }

        string? fromEnv = (env ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new RfInstallChoice(fromEnv, FromEnvironment, settings?.SettingsFile);
        }

        return settings?.RfInstallDir is { } configured
            ? new RfInstallChoice(configured, FromSettings, settings.SettingsFile)
            : new RfInstallChoice(null, FromNothing, settings?.SettingsFile);
    }
}
