using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ged.Automation.Rendering;
using Ged.Core.Assets;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Modules;
using Ged.Rendering.Graphics;

namespace Ged.Automation;

/// <summary>What a module contact sheet shows and where it goes.</summary>
public sealed class ModuleSheetRequest
{
    /// <summary>The library to draw (curation applied; rejected modules are shown too, with their verdict).</summary>
    public required ModuleLibrary Library { get; init; }

    /// <summary>The output folder (<c>index.html</c> and <c>thumbs/&lt;id&gt;.png</c>).</summary>
    public required string OutDir { get; init; }

    public string? Group { get; init; }

    public string? Category { get; init; }

    public string? Style { get; init; }

    /// <summary>Modules per (group, category), best harvest score first.</summary>
    public int Top { get; init; } = ModuleSheet.DefaultTop;

    /// <summary>The thumbnail edge in pixels.</summary>
    public int Size { get; init; } = ModuleSheet.DefaultSize;

    /// <summary>The RF install whose stock textures are mounted (null = untextured thumbnails).</summary>
    public string? RfInstall { get; init; }

    /// <summary>Progress lines (one per module).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The GPU device (null = <see cref="RenderDevices.Create"/>, disposed afterwards).</summary>
    public Func<(GraphicsDevice Device, string Name)>? Device { get; init; }
}

/// <summary>What a contact sheet run did.</summary>
public sealed class ModuleSheetReport
{
    public int Selected { get; init; }

    public int Rendered { get; init; }

    /// <summary>Modules whose thumbnail could not be drawn (id, group, message); the sheet still lists them.</summary>
    public IReadOnlyList<(string Id, string Group, string Message)> Failed { get; init; } = Array.Empty<(string, string, string)>();

    /// <summary>Rendered / failed counts per group.</summary>
    public IReadOnlyDictionary<string, (int Rendered, int Failed)> ByGroup { get; init; } = new SortedDictionary<string, (int, int)>();

    public string IndexPath { get; init; } = string.Empty;

    /// <summary>Rendered modules flagged on their card (they would not show where placed).</summary>
    public int Flagged { get; init; }

    public bool Textured { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>The run as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["selected"] = Selected,
        ["rendered"] = Rendered,
        ["failed"] = new JsonArray(Failed.Select(f => (JsonNode?)new JsonObject { ["id"] = f.Id, ["group"] = f.Group, ["message"] = f.Message }).ToArray()),
        ["by_group"] = new JsonObject(ByGroup.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject { ["rendered"] = kv.Value.Rendered, ["failed"] = kv.Value.Failed }))),
        ["flagged"] = Flagged,
        ["textured"] = Textured,
        ["index"] = IndexPath,
        ["elapsed_s"] = Math.Round(Elapsed.TotalSeconds, 1),
    };

    /// <summary>A readable summary.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"Contact sheet: {Rendered} of {Selected} module(s) rendered, {Failed.Count} failed, in {Elapsed.TotalSeconds:0.0} s{(Textured ? string.Empty : " (no RF install: untextured)")}.");
        if (Flagged > 0)
        {
            sb.AppendLine(inv, $"  {Flagged} flagged on their card: they would not show where placed (behind their wall plane, inside-out brushes, or no face survives the build).");
        }
        foreach ((string g, (int r, int f)) in ByGroup)
        {
            sb.AppendLine(inv, $"  {g}: {r} rendered, {f} failed");
        }

        foreach ((string id, string g, string msg) in Failed)
        {
            sb.AppendLine(inv, $"failed: {g}/{id}: {msg}");
        }

        sb.AppendLine(inv, $"Sheet: {IndexPath}");
        return sb.ToString();
    }
}

/// <summary>
/// The module library's visual curation sheet: each selected module is placed alone in a small private level (a room
/// shaped round its attachment — a backing wall for wall and corner modules, a wall with the opening for frames, a
/// floor under everything else) with two point lights, baked (no shadows) and drawn with the headless <c>render</c>
/// command (lit; an iso view framed on the backdrop box, or from below for hanging modules), and listed in a self-contained <c>index.html</c> where the mapper keeps,
/// rejects and relabels modules and downloads <c>curation.json</c> (<see cref="ModuleCuration"/>). Never writes inside
/// the library; a module that fails to render is reported and listed without a thumbnail.
/// </summary>
public static class ModuleSheet
{
    public const int DefaultTop = 24;

    public const int DefaultSize = 256;

    public const int MinSize = 64;

    public const int MaxSize = 1024;

    /// <summary>The texture of the backdrop room (stock, a flat mid grey that every module texture reads against).</summary>
    public const string BackdropTexture = "gray16x16.tga";

    private const double BehindTolerance = 0.01;

    /// <summary>
    /// Why a module would not show where its anchor puts it (a wall or corner module lying wholly behind its wall plane),
    /// or null.
    /// </summary>
    public static string? PlacementWarning(ModuleInfo m)
    {
        ArgumentNullException.ThrowIfNull(m);
        bool behind = (m.Attach is "wall" or "corner" && m.BoundsMax.Z <= BehindTolerance) || (m.Attach == "corner" && m.BoundsMax.X <= BehindTolerance);
        return behind ? "lies wholly behind its wall plane: invisible when placed as is (drawn here against its own back)" : null;
    }

    /// <summary>The selected modules in sheet order: group, category (vocabulary order), then harvest score and id.</summary>
    public static IReadOnlyList<ModuleInfo> Select(ModuleLibrary lib, string? group, string? category, string? style, int top)
    {
        ArgumentNullException.ThrowIfNull(lib);
        int CatOrder(string c)
        {
            int i = ModuleVocabulary.Categories.ToList().IndexOf(c);
            return i < 0 ? int.MaxValue : i;
        }

        return lib.AllModules
            .Where(m => (group is null || string.Equals(m.Group, group, StringComparison.OrdinalIgnoreCase))
                && (category is null || string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase))
                && (style is null || m.Styles.Contains(style, StringComparer.OrdinalIgnoreCase)))
            .GroupBy(m => (m.Group, m.Category))
            .OrderBy(g => g.Key.Group, StringComparer.Ordinal)
            .ThenBy(g => CatOrder(g.Key.Category))
            .ThenBy(g => g.Key.Category, StringComparer.Ordinal)
            .SelectMany(g => g.OrderByDescending(m => m.Score).ThenBy(m => m.Id, StringComparer.Ordinal).Take(Math.Max(1, top)))
            .ToList();
    }

    /// <summary>Draws the thumbnails and writes the sheet. Throws <see cref="AutomationException"/> for bad input or no GPU.</summary>
    public static async Task<ModuleSheetReport> BuildAsync(ModuleSheetRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModuleLibrary lib = request.Library;
        if (lib.AllModules.Count == 0)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"No module library: {lib.Reason}");
        }

        if (request.Size < MinSize || request.Size > MaxSize)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, $"The thumbnail size must be {MinSize}-{MaxSize} pixels (got {request.Size}).");
        }

        if (request.Top < 1)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "--top must be at least 1.");
        }

        string outDir = Path.GetFullPath(request.OutDir);
        string libDir = Path.GetFullPath(lib.Directory!);
        if (IsInside(outDir, libDir))
        {
            throw new AutomationException(AutomationErrorCodes.PathNotAllowed, $"The sheet folder may not lie inside the module library ({libDir}).");
        }

        var sw = Stopwatch.StartNew();
        IReadOnlyList<ModuleInfo> selected = Select(lib, request.Group, request.Category, request.Style, request.Top);
        string thumbs = Path.Combine(outDir, "thumbs");
        Directory.CreateDirectory(thumbs);

        // The folder above library/ resolves custom textures' packfiles (index textures[].source).
        string? sourceRoot = Path.GetDirectoryName(Path.GetDirectoryName(libDir.TrimEnd(Path.DirectorySeparatorChar)));
        var failed = new List<(string, string, string)>();
        var byGroup = new SortedDictionary<string, (int Rendered, int Failed)>(StringComparer.Ordinal);
        var status = new Dictionary<string, string?>(StringComparer.Ordinal);
        var warnings = new Dictionary<string, string>(StringComparer.Ordinal);
        int rendered = 0;

        (GraphicsDevice device, string deviceName) = (request.Device ?? RenderDevices.Create)();
        using var mounts = new RenderMountCache();
        RenderMountLease? lease = null;
        try
        {
            if (request.RfInstall is { } rf)
            {
                lease = mounts.Acquire(rf);
            }

            using var host = new HeadlessAutomationHost();
            host.PathPolicy.AddRoot(thumbs);
            var openPacks = new Dictionary<string, VppAssetSource?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int n = 0;
                foreach (ModuleInfo m in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    n++;
                    string png = Path.Combine(thumbs, m.Id + ".png");
                    string? error;
                    try
                    {
                        MountFor(host, lease, m, sourceRoot, openPacks);
                        (error, string? warning) = await RenderOneAsync(host, m, png, request.Size, () => (device, deviceName), ct).ConfigureAwait(false);
                        if (warning is not null)
                        {
                            warnings[m.Id] = warning;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One module that cannot be read, built or drawn never stops the sheet.
                        error = ex is AutomationException or IOException or InvalidDataException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                    }

                    (int r, int f) = byGroup.GetValueOrDefault(m.Group);
                    if (error is null)
                    {
                        rendered++;
                        byGroup[m.Group] = (r + 1, f);
                    }
                    else
                    {
                        failed.Add((m.Id, m.Group, error));
                        byGroup[m.Group] = (r, f + 1);
                        TryDelete(png);
                    }

                    status[m.Id] = error;
                    request.Log?.Invoke($"[{n}/{selected.Count}] {m.Group}/{m.Category}/{m.Id}: {error ?? "ok"}");
                }
            }
            finally
            {
                foreach (VppAssetSource? p in openPacks.Values)
                {
                    p?.Dispose();
                }
            }
        }
        finally
        {
            lease?.Dispose();
            if (request.Device is null)
            {
                device.Dispose();
            }
        }

        string index = Path.Combine(outDir, "index.html");
        File.WriteAllText(index, ModuleSheetHtml.Build(lib, selected, status, warnings, request), new UTF8Encoding(false));
        return new ModuleSheetReport
        {
            Selected = selected.Count,
            Rendered = rendered,
            Failed = failed,
            ByGroup = byGroup,
            IndexPath = index,
            Flagged = selected.Count(m => warnings.ContainsKey(m.Id) || PlacementWarning(m) is not null),
            Textured = lease is not null,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Mounts the stock textures, plus the packfiles that supply the module's custom textures (when present beside the
    /// library), on the session for this module's render.
    /// </summary>
    private static void MountFor(HeadlessAutomationHost host, RenderMountLease? lease, ModuleInfo m, string? sourceRoot, Dictionary<string, VppAssetSource?> packs)
    {
        if (lease is null)
        {
            return;
        }

        var extra = new List<IAssetSource>();
        if (sourceRoot is not null)
        {
            foreach (string src in m.Textures.Where(t => !t.Stock && t.Source is not null).Select(t => t.Source!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal))
            {
                if (!packs.TryGetValue(src, out VppAssetSource? pack))
                {
                    string full = Path.GetFullPath(Path.Combine(sourceRoot, src.Replace('/', Path.DirectorySeparatorChar)));
                    try
                    {
                        pack = File.Exists(full) ? VppAssetSource.Open(full) : null;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
                    {
                        pack = null;
                    }

                    packs[src] = pack;
                }

                if (pack is not null)
                {
                    extra.Add(pack);
                }
            }
        }

        AssetVfs vfs = extra.Count == 0 ? lease.Assets : new AssetVfs(extra.Concat(lease.Assets.Sources));
        if (!ReferenceEquals(host.Assets, vfs))
        {
            host.MountShared(vfs, lease.RfInstallDir, extra.Count == 0 ? lease.Traits : null, release: null);
        }
    }

    /// <summary>Builds the module's private level and renders it; returns null on success, else why it failed.</summary>
    private static async Task<(string? Error, string? Warning)> RenderOneAsync(HeadlessAutomationHost host, ModuleInfo m, string png, int size,
        Func<(GraphicsDevice Device, string Name)> device, CancellationToken ct)
    {
        IReadOnlyList<Brush> local = ModuleLibrary.LoadBrushes(m);
        await host.NewAsync(ct).ConfigureAwait(false);
        var doc = host.Document!;
        BrushEditor ed = host.Brushes!;

        // The module turned 180°: its local +Z (the room side) faces the iso camera, which looks from -X, -Z.
        var lo = new Vector3((float)-m.BoundsMax.X, (float)m.BoundsMin.Y, (float)-m.BoundsMax.Z);
        var hi = new Vector3((float)-m.BoundsMin.X, (float)m.BoundsMax.Y, (float)-m.BoundsMin.Z);
        Vector3 ext = hi - lo;
        float big = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
        float margin = Math.Clamp(big * 0.12f, 0.2f, 4f);
        bool fromBelow = m.Attach == "ceiling" || (m.Attach == "span" && m.BoundsMax.Y <= 1e-3 && m.BoundsMin.Y < -1e-3);
        bool backWall = m.Attach is "wall" or "corner";

        // The backdrop room (air): a floor under the module (at the floor it was found on, when close), a wall behind a
        // wall module (z = 0) and beside a corner module (x = 0), a ceiling above a hanging one (y = 0).
        float floorY = lo.Y - (float)Math.Min(m.MountHeight ?? 0, 1.5);
        var rmin = new Vector3(lo.X - margin, floorY, lo.Z - margin);
        var rmax = new Vector3(hi.X + margin, hi.Y + margin, hi.Z + margin);
        // The wall plane (a recess's air members carve behind it). A module lying wholly behind its wall plane would be
        // invisible there; it is drawn against its own back instead, and the sheet flags it.
        if (backWall)
        {
            rmax.Z = m.BoundsMax.Z <= BehindTolerance ? hi.Z : 0f;
        }

        if (m.Attach == "corner")
        {
            rmax.X = m.BoundsMax.X <= BehindTolerance ? hi.X : 0f;
        }

        if (fromBelow)
        {
            rmax.Y = MathF.Max(hi.Y, 0f);
            rmin.Y = lo.Y - MathF.Max(3f, margin);
        }

        Box(ed, rmin, rmax, air: true);
        int airWinding = Orientation(LevelOperations.Brushes(doc)[^1]);

        // A small solid box far down in solid space (it adds no faces) tells how a solid brush is wound.
        Box(ed, new Vector3(0f, rmin.Y - 1000f, 0f), new Vector3(1f, rmin.Y - 999f, 1f), air: false);
        int solidWinding = Orientation(LevelOperations.Brushes(doc)[^1]);
        if (m.Attach == "opening" && m.Opening is { Width: > 0.05, Height: > 0.05 } op)
        {
            // A wall through the frame's mid-plane with the clear opening carved through it.
            float t = (float)Math.Clamp(op.WallThickness, 0.1, 4.0);
            Box(ed, new Vector3(rmin.X, rmin.Y, -t * 0.5f), new Vector3(rmax.X, rmax.Y, t * 0.5f), air: false);
            Box(ed, new Vector3((float)-op.Width * 0.5f, 0f, -t), new Vector3((float)op.Width * 0.5f, (float)op.Height, t), air: true);
        }

        ModulePlaceResult placed = ModuleInstancer.Place(doc, m, local, new ModulePlacement { Position = Vec3.Zero, YawDegrees = 180, ForceDetail = true });
        if (!placed.Ok)
        {
            return (placed.Error, null);
        }

        // Light it so its form reads (textures alone are flat): a key light above the camera side, a dim fill from the
        // right, a grey ambient, baked without shadows on a preview build of this private level.
        Vector3 centre = (lo + hi) * 0.5f;
        float reach = MathF.Max(ext.Length() * 0.5f, 0.5f);
        void AddLight(Vector3 dir, float distance, byte value)
        {
            Vector3 p = centre + (Vector3.Normalize(dir) * reach * distance);
            if (doc.PlaceObject(LevelObjectKind.Light, new Vec3(p.X, p.Y, p.Z))?.Model is Light light)
            {
                light.Range = reach * distance * 2.2f;
                light.Color = new RfColor(value, value, value, 255);
            }
        }

        AddLight(fromBelow ? new Vector3(-0.8f, -1f, -0.6f) : new Vector3(-0.6f, 1.4f, -0.9f), 2.2f, 255);
        AddLight(fromBelow ? new Vector3(1f, -0.4f, -0.3f) : new Vector3(1f, 0.6f, -0.2f), 2.5f, 120);
        foreach (LevelPropertiesSection props in doc.Rfl.Sections.Select(x => x.Content).OfType<LevelPropertiesSection>())
        {
            props.AmbientColor = new RfColor(60, 60, 60, 255);
        }

        await host.BuildAsync(sealedBuild: false, bakeLighting: true, shadows: false, ct).ConfigureAwait(false);

        // Brushes wound inside out (their faces point inward) draw nothing from outside; nothing of the module surviving
        // the build (degenerate or open brushes) leaves an empty backdrop.
        string? warning = PlacementWarning(m);
        int inverted = local.Count(b => Orientation(b) == -((b.Flags & (uint)BrushFlags.Air) != 0 ? airWinding : solidWinding));
        if (inverted > 0)
        {
            warning = $"{inverted} of its {local.Count} brush(es) {(inverted == 1 ? "is" : "are")} inside out (faces point inward): {(inverted == local.Count ? "invisible" : "partly invisible")} when placed";
        }
        else if (LevelOperations.CompiledGeometry(doc) is { } geo
            && !geo.Faces.Any(f => f.Texture >= 0 && f.Texture < geo.Textures.Count && !string.Equals(geo.Textures[f.Texture], BackdropTexture, StringComparison.OrdinalIgnoreCase)))
        {
            warning = "no face of it survives the build (degenerate or open brushes): invisible when placed";
        }

        var args = new JsonObject
        {
            ["out"] = png,
            ["mode"] = "lit",
            ["width"] = size,
            ["height"] = size,
            ["overlays"] = "none",
            ["build"] = "none",
        };
        if (fromBelow)
        {
            // A hanging module reads from below: a perspective camera under it, fitted on its bounding sphere.
            Vector3 c = (lo + hi) * 0.5f;
            float radius = MathF.Max(ext.Length() * 0.5f, 0.25f);
            const float Fov = 45f;
            float dist = radius / MathF.Sin(Fov * 0.5f * MathF.PI / 180f);
            Vector3 eye = c + (Vector3.Normalize(new Vector3(-1f, -0.75f, -1f)) * dist);
            args["view"] = "camera";
            args["camera"] = new JsonArray(eye.X, eye.Y, eye.Z);
            args["look"] = new JsonArray(c.X, c.Y, c.Z);
            args["fov"] = Fov;
        }
        else
        {
            args["view"] = "iso";
            // Framed on the backdrop box: the module with its floor and wall, nothing beyond.
            args["region"] = new JsonArray(rmin.X - 0.01f, rmin.Y - 0.01f, rmin.Z - 0.01f, rmax.X + 0.01f, rmax.Y + 0.01f, rmax.Z + 0.01f);
        }

        AutomationResponse r = await RenderCommand.ExecuteAsync(host, new AutomationRequest(RenderCommand.Name, args), device, ct).ConfigureAwait(false);
        return (r.Ok ? null : r.Error?.Message ?? "the render failed", warning);
    }

    /// <summary>
    /// The sign of the brush's signed volume from its faces' vertex winding (+1, -1, or 0 when flat): it holds for
    /// concave brushes too, and flips for a brush wound inside out.
    /// </summary>
    private static int Orientation(Brush b)
    {
        List<Vec3> v = b.Geometry.Vertices;
        double volume = 0;
        foreach (Face f in b.Geometry.Faces)
        {
            if (f.Vertices.Count < 3 || f.Vertices.Any(fv => fv.Index < 0 || fv.Index >= v.Count))
            {
                continue;
            }

            Vec3 a = v[f.Vertices[0].Index];
            for (int i = 1; i + 1 < f.Vertices.Count; i++)
            {
                Vec3 p = v[f.Vertices[i].Index], q = v[f.Vertices[i + 1].Index];
                volume += (a.X * ((p.Y * q.Z) - (p.Z * q.Y))) - (a.Y * ((p.X * q.Z) - (p.Z * q.X))) + (a.Z * ((p.X * q.Y) - (p.Y * q.X)));
            }
        }

        return Math.Abs(volume) < 1e-9 ? 0 : Math.Sign(volume);
    }

    private static void Box(BrushEditor ed, Vector3 min, Vector3 max, bool air)
    {
        Vector3 size = Vector3.Max(max - min, new Vector3(0.05f));
        Vector3 c = (min + max) * 0.5f;
        ed.CreateBrush(new BrushCreateParams
        {
            Shape = BrushShape.Box,
            Width = size.X,
            Height = size.Y,
            Depth = size.Z,
            Air = air,
            Texture = BackdropTexture,
        }, new Vec3(c.X, c.Y, c.Z), Mat3.Identity);
    }

    private static bool IsInside(string path, string folder)
    {
        string a = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string b = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale thumbnail stays; the sheet marks the module as failed either way.
        }
    }
}
