using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ged.Core.Editing;

namespace Ged.App;

/// <summary>Persisted user settings (install path, viewport prefs, theme, MRU, colors).</summary>
public sealed class AppSettings
{
    /// <summary>Absolute path to the Red Faction install whose VPPs are mounted.</summary>
    public string? RfInstallDir { get; set; }

    /// <summary>
    /// Path to the Alpine Faction launcher (<c>AlpineFactionLauncher.exe</c>) that
    /// play-tests launch through. Empty → guessed from the install dir on first Play.
    /// A legacy stock <c>RF.exe</c> path from an older settings file is migrated on load
    /// (see <see cref="SettingsStore.Load(string)"/>) — adopted to the launcher beside it
    /// when present, otherwise cleared.
    /// </summary>
    public string GameExePath { get; set; } = string.Empty;

    /// <summary>Optional extra command-line arguments appended to every playtest launch.</summary>
    public string PlaytestExtraArgs { get; set; } = string.Empty;

    /// <summary>
    /// Playtest launch-command template. The tokens <c>{exe}</c> and <c>{args}</c> are
    /// substituted with the resolved <c>AlpineFactionLauncher.exe</c> path and its argument
    /// string; the first whitespace-delimited token becomes the launched program. Empty =
    /// launch the exe directly (the Windows default). On Linux the default is
    /// <c>wine {exe} {args}</c> so the Windows launcher runs under Wine; any wrapper works
    /// (a Proton launch script, <c>protontricks-launch</c>, etc.). Staging paths flow
    /// through the VFS mount unchanged, so a Wine-prefixed RF install works as-is.
    /// </summary>
    public string PlaytestLaunchTemplate { get; set; } =
        OperatingSystem.IsWindows() ? string.Empty : "wine {exe} {args}";

    /// <summary>Last render mode index (see <see cref="Ged.Rendering.RenderMode"/>).</summary>
    public int RenderMode { get; set; } = 1;

    /// <summary>Selected camera scheme (see <see cref="Camera.CameraSchemeKind"/>).</summary>
    public int CameraScheme { get; set; } = (int)Ged.App.Camera.CameraSchemeKind.ModernFps;

    /// <summary>Dark theme when true (default), light when false.</summary>
    public bool DarkTheme { get; set; } = true;

    /// <summary>
    /// GPU backend the viewports render on (see <see cref="Ged.Rendering.Graphics.GraphicsBackend"/>):
    /// 0 = Direct3D 11 (the Windows reference/default), 1 = OpenGL 3.3 core (the cross-platform,
    /// composited host). RESTART-SCOPED — the shared GPU device is created once at startup, so a
    /// change only takes effect on the next launch. Absent from an existing settings file = Direct3D 11,
    /// so upgrading never changes the default backend.
    /// </summary>
    public int Renderer { get; set; }

    // ---- General ----
    public bool AutosaveEnabled { get; set; } = true;

    public int AutosaveIntervalMinutes { get; set; } = 5;

    public bool PromptForSave { get; set; } = true;

    public float NavPointHeight { get; set; } = 3f;

    /// <summary>"Don't show again" for the legacy-level (pre-Alpine) open warning.</summary>
    public bool SuppressLegacyWarning { get; set; }

    /// <summary>Set once the first-run wizard has completed.</summary>
    public bool FirstRunComplete { get; set; }

    // ---- Viewport ----
    public float FarClip { get; set; } = 6000f;

    public float GridSize { get; set; } = 1f;

    public float GridBrightness { get; set; } = 1f;

    public float RotationStep { get; set; } = 15f;

    /// <summary>Scale-drag increment (fraction) when the magnet snap is on. 0.05 = 5%.</summary>
    public float ScaleStep { get; set; } = 0.05f;

    /// <summary>Magnet toggle: when true, mouse-driven transforms snap to grid/increment.
    /// Default on — preserves the gizmo's prior always-snap behaviour and extends proper
    /// absolute-grid snap to the M/N drags (the RED mouse-snap fix).</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>Active snap targets (B1 split-button flags — see <see cref="Ged.Core.Editing.SnapKinds"/>);
    /// default Grid + Vertices. Persisted as the flags int.</summary>
    public int SnapKinds { get; set; } = (int)Ged.Core.Editing.SnapKinds.Default;

    /// <summary>Show the transform manipulator when the selection is transformable (View ▸ Show Gizmo).</summary>
    public bool ShowGizmo { get; set; } = true;

    /// <summary>Manipulator orients to the selection's rotation (Local) vs the world axes (World).</summary>
    public bool GizmoLocal { get; set; }

    public float CameraSpeed { get; set; } = 12f;

    public bool ShowLinks { get; set; } = true;

    /// <summary>Draw the directional-event facing arrows in the viewport ("Show Event Arrows"). On by default.</summary>
    public bool ShowEventArrows { get; set; } = true;

    public bool ShowBoundingBoxes { get; set; }

    public bool ShowPathNodeConnections { get; set; }

    /// <summary>
    /// "Draw unmerged brushwork" (default off): when off, brush overlays hide faces
    /// the last build clipped away (outside the level / consumed by CSG), showing only
    /// the merged/surviving brushwork. (Formerly "Show Clipped Brush Faces"; the legacy
    /// settings key is migrated on load — see <see cref="SettingsStore.Load(string)"/>.)
    /// </summary>
    public bool DrawUnmergedBrushwork { get; set; }

    /// <summary>
    /// Global "Show all ranges": draw every light-range / region sphere regardless of
    /// selection. Default OFF — a range is otherwise only drawn for the selected object
    /// or one whose "Always Show Range" flag is set.
    /// </summary>
    public bool ShowAllRanges { get; set; }

    /// <summary>Animate particle/bolt emitter previews in the viewport.</summary>
    public bool AnimateEmitters { get; set; }

    /// <summary>Apply distance fog matching the level's fog colour + far clip.</summary>
    public bool ShowFog { get; set; }

    /// <summary>
    /// Render both faces of solid world/mesh geometry (disable RED-parity back-face
    /// culling). Default OFF — culling is on, matching RED. Double-sided (0x20) mesh
    /// triangles and the transparent passes always render both faces regardless.
    /// </summary>
    public bool DisableBackfaceCulling { get; set; }

    /// <summary>Draw sky-room geometry as a camera-locked background ("Draw Sky (Like in-game)").</summary>
    public bool DrawSky { get; set; }

    /// <summary>
    /// "Draw Decals" (perspective-only, default OFF): project each decal's texture onto the
    /// static geometry it faces, as an alpha-blended in-viewport preview.
    /// </summary>
    public bool DrawDecals { get; set; }

    /// <summary>
    /// Geometry build method (Geometry menu). When true, the compiler uses RED's authentic SINGLE ACCUMULATED
    /// SHARED BSP (<see cref="Ged.Core.Compiler.CompileOptions.SharedBsp"/>): the persistent shared
    /// boundary with both world faces and caps routed down one accumulated partition (hybrid volume-clip +
    /// partition re-cut). Measured equal-or-better than the incremental default on every corpus level (ctf07
    /// 74->42, dmedge 4->0 open edges; rooms/portals held), at ~1.0-1.7x build time on the largest levels. This is
    /// now the DEFAULT (owner decision — the RED-authentic method ships by default). When false the Incremental
    /// accumulator (GED's own modern construction) runs — selectable via Geometry ▸ Build Method. Persisted so the
    /// choice survives restarts; an existing settings file that explicitly chose a method is respected on load.
    /// </summary>
    public bool UseSharedBspBuild { get; set; } = true;

    /// <summary>Show measurement/dimension annotations in the viewport (B7 View toggle). Default on.</summary>
    public bool ShowAnnotations { get; set; } = true;

    // ---- Feature 1: global default lightmap bake method (per-level override in the sidecar) ----

    /// <summary>Global default base bake method: 0 = RED Classic (default), 1 = Bounced.</summary>
    public int LightingMethodBase { get; set; }

    /// <summary>Global default gather bounces (1 or 2) when the base is Bounced.</summary>
    public int LightingMethodBounces { get; set; } = 1;

    /// <summary>Global default: add the Ambient Occlusion modifier.</summary>
    public bool LightingAmbientOcclusion { get; set; }

    /// <summary>Global default: add the Soft Shadows modifier.</summary>
    public bool LightingSoftShadows { get; set; }

    /// <summary>Global default: add the High-Resolution Lightmaps modifier (item 6 amendment).</summary>
    public bool LightingHighRes { get; set; }

    /// <summary>Global default: add the cross-surface Seam Blend modifier (Alpine -smoothlights).</summary>
    public bool LightingSeamBlend { get; set; }

    /// <summary>Global default: add the Corner Leak Fix modifier (own-room ambient + edge-aware shadow bias).</summary>
    public bool LightingCornerLeakFix { get; set; }

    /// <summary>Global default: add the Smooth Gutter Normals modifier (weld gutter normals + angle-weighted average).</summary>
    public bool LightingSmoothGutters { get; set; }

    /// <summary>Item 9 — persisted UV Unwrap window X position (int.MinValue = never saved).</summary>
    public int UvWindowX { get; set; } = int.MinValue;

    /// <summary>Item 9 — persisted UV Unwrap window Y position (int.MinValue = never saved).</summary>
    public int UvWindowY { get; set; } = int.MinValue;

    /// <summary>Item 9 — persisted UV Unwrap window width (0 = never saved).</summary>
    public double UvWindowWidth { get; set; }

    /// <summary>Item 9 — persisted UV Unwrap window height (0 = never saved).</summary>
    public double UvWindowHeight { get; set; }

    /// <summary>Portal-face draw mode: 0 = don't draw (default), 1 = see-thru, 2 = non-see-thru
    /// (see <see cref="Ged.Rendering.Scene.PortalFaceDrawMode"/>).</summary>
    public int PortalFaceMode { get; set; }

    /// <summary>
    /// Build the object-icon atlas from RED's original icon bitmaps (read from the
    /// mounted game VFS — ui.vpp / alpinefaction.vpp). Default OFF = GED's own icon
    /// set. Licensing-clean: GED never ships those bitmaps, it reads the user's files.
    /// </summary>
    public bool UseOriginalIcons { get; set; }

    // ---- Texture preferences (stock RED "Texture" preferences page) ----

    /// <summary>Default floor texture applied to up-facing faces at brush creation.</summary>
    public string DefaultFloorTexture { get; set; } = BrushCreateParams.StockFloorTexture;

    /// <summary>Default wall texture applied to vertical faces at brush creation.</summary>
    public string DefaultWallTexture { get; set; } = BrushCreateParams.StockWallTexture;

    /// <summary>Default ceiling texture applied to down-facing faces at brush creation.</summary>
    public string DefaultCeilingTexture { get; set; } = BrushCreateParams.StockCeilingTexture;

    /// <summary>Texture-apply pixels-per-meter scale (≤ 8192, Alpine cap).</summary>
    public float PixelsPerMeter { get; set; } = 256f;

    // ---- Persisted window positions (stock parity) ----

    public int? ClipDialogX { get; set; }

    public int? ClipDialogY { get; set; }

    // ---- Element colors (hex #RRGGBB). Defaults are RED's stock element colours
    // (item 8); the background + axis triad keep GED's own defaults. Existing users'
    // settings.cfg values are untouched — these apply only when a value is unset. ----
    public string ColorBackground { get; set; } = "#1A1C21";

    public string ColorGrid { get; set; } = "#8080FF";

    public string ColorCookieCutter { get; set; } = "#00FFFF";

    public string ColorBrush { get; set; } = "#FFFFFF";

    public string ColorBrushLocked { get; set; } = "#A0A0A0";

    public string ColorBrushDetail { get; set; } = "#00FF00";

    public string ColorBrushPortal { get; set; } = "#FFFF00";

    public string ColorMover { get; set; } = "#00FF00";

    public string ColorLinks { get; set; } = "#0000FF";

    public string ColorNodes { get; set; } = "#00FF00";

    public string ColorBoundingBox { get; set; } = "#C8C864";

    public string ColorTriggers { get; set; } = "#0000FF";

    public string ColorRegions { get; set; } = "#00B03B";

    // Colorblind-safe manipulator axis triad (Okabe–Ito: vermillion / bluish-green /
    // sky-blue) — distinguishable under the common CVD types, unlike pure R/G/B.
    public string ColorAxisX { get; set; } = "#D55E00";

    public string ColorAxisY { get; set; } = "#009E73";

    public string ColorAxisZ { get; set; } = "#56B4E9";

    // ---- Asset browser ----

    /// <summary>Favorited texture names (the browser's ★ set).</summary>
    public List<string> TextureFavorites { get; set; } = new();

    /// <summary>Named texture collections: collection name → texture names.</summary>
    public Dictionary<string, List<string>> TextureCollections { get; set; } = new();

    /// <summary>Asset-browser thumbnail tile size in pixels.</summary>
    public int AssetTileSize { get; set; } = 56;

    /// <summary>
    /// Prefab library directory. Empty → the default <c>%APPDATA%\Glacier\prefabs</c>.
    /// The browser also scans a <c>prefabs</c> directory next to the open level.
    /// </summary>
    public string PrefabDirectory { get; set; } = string.Empty;

    // ---- MRU ----
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Adds a path to the top of the MRU list (deduped, capped).</summary>
    public void PushRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10)
        {
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        }
    }
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the portable settings file
/// <c>settings.cfg</c> (see <see cref="Ged.Core.AppPaths"/>): next to the executable
/// when its directory is writable, otherwise under <c>%APPDATA%\Glacier</c>.
/// <c>settings.cfg</c> is authoritative and is created on first save. All failures are
/// swallowed so a corrupt or unreadable settings file never blocks startup.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The active settings file path (portable, or the profile fallback).</summary>
    public static string SettingsPath => Ged.Core.AppPaths.SettingsFile;

    public static AppSettings Load() => Load(SettingsPath);

    public static void Save(AppSettings settings) => Save(settings, SettingsPath);

    /// <summary>Loads settings from an explicit path (exposed for tests); defaults on any failure.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                MigrateLegacyKeys(json, settings);
                return settings;
            }
        }
        catch (Exception)
        {
            // Ignore and fall back to defaults.
        }

        return new AppSettings();
    }

    /// <summary>
    /// Carries forward settings whose persisted key was renamed, so upgrading never
    /// silently resets a user's choice. Currently: the "Show Clipped Brush Faces" toggle
    /// was renamed to "Draw unmerged brushwork", moving its key
    /// <c>ShowClippedBrushFaces</c> → <c>DrawUnmergedBrushwork</c>.
    /// </summary>
    private static void MigrateLegacyKeys(string json, AppSettings settings)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && !root.TryGetProperty(nameof(AppSettings.DrawUnmergedBrushwork), out _)
                && root.TryGetProperty("ShowClippedBrushFaces", out JsonElement legacy)
                && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.DrawUnmergedBrushwork = legacy.GetBoolean();
            }
        }
        catch (Exception)
        {
            // A malformed legacy key is non-fatal — keep the deserialized value.
        }

        MigrateDeadDefaultTextures(settings);
        MigrateStockGameExe(settings);
    }

    /// <summary>
    /// Alpine-only launch consolidation: GED now play-tests exclusively through
    /// <c>AlpineFactionLauncher.exe</c>. A settings.cfg carried over from when a stock
    /// <c>RF.exe</c> could be configured may still hold that path; rather than break the load
    /// or silently launch stock RF, migrate it — adopt <c>AlpineFactionLauncher.exe</c> when it
    /// sits beside the stored exe, otherwise clear the path so play-test re-guesses or prompts.
    /// The unrelated RF install-directory mount is untouched.
    /// </summary>
    private static void MigrateStockGameExe(AppSettings settings)
    {
        string exe = settings.GameExePath;
        if (string.IsNullOrWhiteSpace(exe)
            || Ged.Core.Playtest.GameLauncher.DetectKind(exe) == Ged.Core.Playtest.GameKind.AlpineLauncher)
        {
            return; // unset, or already the Alpine launcher — nothing to migrate
        }

        // Migration of a stale legacy stock-RF.exe path is a deterministic filesystem operation
        // (adopt the AlpineFactionLauncher.exe beside the stored exe, else clear). It deliberately
        // does NOT consult the af:// registry — that belongs in the wizard prefill and the
        // play-test / settings guess (item 6), not in silently rewriting a saved path on load.
        string? dir = Path.GetDirectoryName(exe);
        settings.GameExePath = (dir is null ? null : Ged.Core.Playtest.GameLauncher.GuessExe(dir)) ?? string.Empty;
    }

    /// <summary>
    /// The pre-fix default brush texture name that never existed in stock RF. Builds that
    /// shipped it persisted it into every settings.cfg (settings save wholesale), so the
    /// code-side constant fix alone left upgraded installs creating white brushes: the
    /// stale persisted name overrode the corrected default on load, the factory stamped it
    /// onto every new face, and the renderer's white fallback took over (the face-props
    /// panel dutifully showing the dead name). Loading migrates it to the real stock name.
    /// </summary>
    internal const string DeadDefaultTexture = "Rck_Default01.tga";

    private static void MigrateDeadDefaultTextures(AppSettings settings)
    {
        if (string.Equals(settings.DefaultFloorTexture, DeadDefaultTexture, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultFloorTexture = Ged.Core.Editing.BrushCreateParams.StockFloorTexture;
        }

        if (string.Equals(settings.DefaultWallTexture, DeadDefaultTexture, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultWallTexture = Ged.Core.Editing.BrushCreateParams.StockWallTexture;
        }

        if (string.Equals(settings.DefaultCeilingTexture, DeadDefaultTexture, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultCeilingTexture = Ged.Core.Editing.BrushCreateParams.StockCeilingTexture;
        }
    }

    /// <summary>Saves settings to an explicit path (exposed for tests); non-fatal on failure.</summary>
    public static void Save(AppSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception)
        {
            // Non-fatal.
        }
    }
}
