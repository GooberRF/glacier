using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ged.Core.Compiler;
using Ged.Core.Editor;
using Ged.Core.IO.Tex;
using Ged.Core.Model;
using Ged.Rendering.Graphics;

namespace Ged.Automation.Rendering;

/// <summary>
/// The <c>render</c> command: draws the level to a PNG (see <see cref="LevelRenderer"/>) and returns
/// the pixel ↔ world mapping (<see cref="RenderResult"/>). It always runs in the calling process on a
/// <see cref="HeadlessAutomationHost"/> — never inside the editor, which keeps the editor free of a
/// second GPU device and guarantees the render cannot touch the user's document: with
/// <c>--attach</c>, glacier-cli first copies the editor's open level (<c>snapshot</c>) into a private
/// headless session and renders that.
/// <para>
/// Geometry: the image shows the level's compiled geometry. Build policy <c>auto</c> (default) runs a
/// fast preview build on the private copy when the level has brushes but no compiled geometry, or its
/// geometry is out of date; <c>preview</c> / <c>sealed</c> always build; <c>none</c> never does. The
/// build is never saved or applied to the editor. The <c>holes</c> overlay runs a separate sealed
/// hole check (not applied).
/// </para>
/// </summary>
public static class RenderCommand
{
    /// <summary>The command name.</summary>
    public const string Name = "render";

    /// <summary>Error code: no GPU device could be created.</summary>
    public const string RenderUnavailable = "render_unavailable";

    /// <summary>
    /// Runs a render request against <paramref name="host"/> (opening <c>level</c> first when given).
    /// Never throws: failures become an error response. <paramref name="deviceFactory"/> overrides the
    /// GPU device (tests); the default is <see cref="RenderDevices.Create"/>.
    /// </summary>
    public static async Task<AutomationResponse> ExecuteAsync(
        HeadlessAutomationHost host,
        AutomationRequest request,
        Func<(GraphicsDevice Device, string Name)>? deviceFactory = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await RunAsync(host, request, deviceFactory, cancellation).ConfigureAwait(false);
        }
        catch (AutomationException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(AutomationErrorCodes.Canceled, "The render was canceled before it finished.");
        }
        catch (Exception ex)
        {
            return Fail(AutomationErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<AutomationResponse> RunAsync(
        HeadlessAutomationHost host,
        AutomationRequest request,
        Func<(GraphicsDevice Device, string Name)>? deviceFactory,
        CancellationToken ct)
    {
        RenderSpec spec = RenderSpec.FromArgs(request.Args);
        if (request.Args["level"] is System.Text.Json.Nodes.JsonValue levelNode && levelNode.TryGetValue(out string? level) && level.Length > 0)
        {
            string full = PathAllowList.Normalize(level);
            if (!full.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase))
            {
                throw new AutomationException(AutomationErrorCodes.Usage, $"Only level files (.rfl) can be rendered: {full}");
            }

            if (host.Document?.Path is not { } current || !AutomationScriptOperations.PathsEqual(current, full))
            {
                await host.OpenAsync(full, ct).ConfigureAwait(false);
            }
        }

        EditorDocument doc = host.Document ?? throw new AutomationException(AutomationErrorCodes.NoDocument,
            "No level is open. Pass a level file (or --attach to render the level open in the editor).");
        string outPath = host.PathPolicy.Require(spec.OutPath, "write");

        // ---- geometry ----
        string before = GeometryState(host, doc);
        string action = spec.Build switch
        {
            "preview" => "preview",
            "sealed" => "sealed",
            "none" => "none",
            _ => before is "stale" || (before == "none" && LevelOperations.Brushes(doc).Count > 0) ? "preview" : "none",
        };
        var notes = new List<string>();
        if (action != "none")
        {
            CompiledLevel built = await host.BuildAsync(sealedBuild: action == "sealed", bakeLighting: false, shadows: true, ct).ConfigureAwait(false);
            if (built.Report.HasErrors)
            {
                notes.Add("The build for this render reported errors; the image shows what compiled.");
            }

            if (spec.Build == "auto")
            {
                notes.Add(before == "stale"
                    ? "The level's compiled geometry was out of date: rendered from a fresh preview build (not saved)."
                    : "The level had no compiled geometry: rendered from a fresh preview build (not saved).");
            }
        }

        string rendered = action switch
        {
            "sealed" => "sealed",
            "preview" => "preview",
            _ => before,
        };

        IReadOnlyList<Vec3>? holes = null;
        if ((spec.Overlays & RenderOverlays.Holes) != 0)
        {
            holes = LevelOperations.CompiledGeometry(doc) is null && LevelOperations.Brushes(doc).Count == 0
                ? Array.Empty<Vec3>()
                : await host.CheckHolesAsync(ct).ConfigureAwait(false);
        }

        var geometry = new RenderGeometryDto { Before = before, BuiltForRender = action, Rendered = rendered };

        // ---- GPU ----
        (GraphicsDevice device, string deviceName) = (deviceFactory ?? RenderDevices.Create)();
        byte[] png;
        RenderResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            (byte[] rgba, result) = LevelRenderer.Render(device, deviceName, doc, host.Assets, spec, holes, geometry, outPath);
            png = PngWriter.Encode(result.Width, result.Height, rgba);
        }
        finally
        {
            if (deviceFactory is null)
            {
                device.Dispose();
            }
        }

        try
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(outPath, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationException(AutomationErrorCodes.Io, $"Could not write '{outPath}': {ex.Message}", ex);
        }

        if (notes.Count > 0)
        {
            notes.AddRange(result.Notes ?? new List<string>());
            result.Notes = notes;
        }

        return new AutomationResponse { Ok = true, Command = Name, Result = AutomationJson.ToNode(result) };
    }

    /// <summary>The open document's compiled-geometry state: none | preview | stale | built.</summary>
    internal static string GeometryState(IAutomationHost host, EditorDocument doc)
    {
        Geometry? geo = LevelOperations.CompiledGeometry(doc);
        LevelBuildState state = host.BuildState;
        return geo is null || (geo.Faces.Count == 0 && geo.Rooms.Count == 0) ? "none"
            : state.GeometryIsPreview ? "preview"
            : state.GeometryDirty ? "stale"
            : "built";
    }

    private static AutomationResponse Fail(string code, string message) =>
        new() { Ok = false, Command = Name, Error = new AutomationError(code, message) };
}

/// <summary>Creates the offscreen GPU device a render uses.</summary>
public static class RenderDevices
{
    /// <summary>
    /// Windows: Direct3D 11 (hardware, falling back to the WARP software rasterizer), then OpenGL 3.3.
    /// Elsewhere: OpenGL 3.3 (EGL/GLX offscreen). Throws a <c>render_unavailable</c>
    /// <see cref="AutomationException"/> when no device can be created.
    /// </summary>
    public static (GraphicsDevice Device, string Name) Create()
    {
        var errors = new List<string>();
        GraphicsBackend[] order = OperatingSystem.IsWindows()
            ? new[] { GraphicsBackend.Direct3D11, GraphicsBackend.OpenGl }
            : new[] { GraphicsBackend.OpenGl };
        foreach (GraphicsBackend backend in order)
        {
            try
            {
                var gd = new GraphicsDevice(backend);
                string name = backend == GraphicsBackend.Direct3D11 ? "Direct3D11" : "OpenGL 3.3";
                return (gd, $"{name} ({(gd.IsWarp ? "software" : "hardware")})");
            }
            catch (Exception ex)
            {
                errors.Add($"{backend}: {ex.Message}");
            }
        }

        throw new AutomationException(RenderCommand.RenderUnavailable,
            "No GPU device is available for rendering (" + string.Join("; ", errors) + ").");
    }
}
