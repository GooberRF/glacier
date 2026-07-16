using System;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one face of a brush (plan §5.3), addressed by brush UID + face index.
/// Texture and flag edits route through <see cref="BrushEditor.EditBrushes"/> so they are
/// undoable and join the run's transaction.
/// </summary>
public sealed class ScriptFaceHandle
{
    private readonly ScriptContext _ctx;
    private readonly int _brushUid;
    private readonly int _faceIndex;

    internal ScriptFaceHandle(ScriptContext ctx, int brushUid, int faceIndex)
    {
        _ctx = ctx;
        _brushUid = brushUid;
        _faceIndex = faceIndex;
    }

    /// <summary>Lua: <c>face.index</c> — the face's index within its brush.</summary>
    public int Index => _faceIndex;

    /// <summary>Lua: <c>face.brush_uid</c>.</summary>
    public int BrushUid => _brushUid;

    private Brush Brush => _ctx.Brushes.FindBrush(_brushUid)
        ?? throw new ScriptApiException($"Brush {_brushUid} no longer exists.");

    private Face Face
    {
        get
        {
            Geometry g = Brush.Geometry;
            if (_faceIndex < 0 || _faceIndex >= g.Faces.Count)
            {
                throw new ScriptApiException($"Face {_faceIndex} is out of range for brush {_brushUid}.");
            }

            return g.Faces[_faceIndex];
        }
    }

    /// <summary>Lua: <c>face.texture</c> — the texture name, or "" for a portal/untextured face.</summary>
    public string Texture
    {
        get
        {
            Geometry g = Brush.Geometry;
            Face f = Face;
            return f.Texture >= 0 && f.Texture < g.Textures.Count ? g.Textures[f.Texture] : string.Empty;
        }
    }

    /// <summary>Lua: <c>face.is_portal</c>.</summary>
    public bool IsPortal => Face.IsPortalFace;

    /// <summary>Lua: <c>face.detail</c> — the per-face detail flag.</summary>
    public bool Detail => FaceProps.Get(Face, FaceFlags.IsDetail);

    /// <summary>Lua: <c>face.show_sky</c>.</summary>
    public bool ShowSky => FaceProps.Get(Face, FaceFlags.ShowSky);

    /// <summary>Lua: <c>face.full_bright</c>.</summary>
    public bool FullBright => FaceProps.Get(Face, FaceFlags.FullBright);

    /// <summary>Lua: <c>face:set_texture("name")</c> — sets this face's texture (undoable).</summary>
    public void SetTexture(string texture)
    {
        Edit($"Set face texture {texture}", (g, fi) =>
            g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, texture));
        _ctx.Changes.Record("retextured");
    }

    /// <summary>Lua: <c>face:set_flag("detail"|"show_sky"|"full_bright"|"mirrored"|"has_alpha", true|false)</c>.</summary>
    public void SetFlag(string flag, bool value)
    {
        FaceFlags f = ParseFlag(flag);
        Edit($"Set face {flag}", (g, fi) => FaceProps.Set(g.Faces[fi], f, value));
        _ctx.Changes.Record("flag");
    }

    private void Edit(string description, Action<Geometry, int> action)
    {
        int idx = _faceIndex;
        OpResult r = _ctx.Brushes.EditBrushes(new[] { _brushUid }, description, b =>
        {
            if (idx < 0 || idx >= b.Geometry.Faces.Count)
            {
                return OpResult.Fail($"Face {idx} out of range.");
            }

            action(b.Geometry, idx);
            return OpResult.Ok();
        });
        if (!r.Success)
        {
            throw new ScriptApiException($"{description} failed: {r.Message}");
        }
    }

    private static FaceFlags ParseFlag(string flag) => (flag ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "detail" => FaceFlags.IsDetail,
        "show_sky" or "sky" => FaceFlags.ShowSky,
        "full_bright" or "fullbright" => FaceFlags.FullBright,
        "mirrored" or "mirror" => FaceFlags.Mirrored,
        "has_alpha" or "alpha" => FaceFlags.HasAlpha,
        "scroll" or "scroll_texture" => FaceFlags.ScrollTexture,
        "liquid" or "liquid_surface" => FaceFlags.LiquidSurface,
        _ => throw new ScriptApiException($"Unknown face flag '{flag}'.",
            "Valid: detail, show_sky, full_bright, mirrored, has_alpha, scroll, liquid."),
    };

    public override string ToString() => $"face#{_faceIndex}@brush#{_brushUid}";
}
