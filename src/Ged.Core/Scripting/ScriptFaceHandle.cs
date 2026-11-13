using System;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one face of a brush, addressed by brush UID + face index.
/// Texture and flag edits route through <see cref="BrushEditor.EditBrushes"/> so they are
/// undoable and join the run's transaction.
/// </summary>
public sealed partial class ScriptFaceHandle
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
    [ScriptDoc("The face's 0-based index within its brush.", Example = "log.info(f.index)")]
    public int Index => _faceIndex;

    /// <summary>Lua: <c>face.brush_uid</c>.</summary>
    [ScriptDoc("The UID of the brush that owns this face.", Example = "local b = level.find_brush(f.brush_uid)")]
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
    [ScriptDoc("The face's texture name, or \"\" for a portal or untextured face.", Example = "if f.texture == \"rock02.tga\" then f:set_texture(\"rock03.tga\") end")]
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
    [ScriptDoc("True for a portal face (no texture mapping).", Example = "if not f.is_portal then f:box_map() end")]
    public bool IsPortal => Face.IsPortalFace;

    /// <summary>Lua: <c>face.detail</c> — the per-face detail flag.</summary>
    [ScriptDoc("The per-face detail flag.", Example = "log.info(tostring(f.detail))")]
    public bool Detail => FaceProps.Get(Face, FaceFlags.IsDetail);

    /// <summary>Lua: <c>face.show_sky</c>.</summary>
    [ScriptDoc("True when the face shows the sky.", Example = "log.info(tostring(f.show_sky))")]
    public bool ShowSky => FaceProps.Get(Face, FaceFlags.ShowSky);

    /// <summary>Lua: <c>face.full_bright</c>.</summary>
    [ScriptDoc("True when the face is full-bright (ignores lighting).", Example = "log.info(tostring(f.full_bright))")]
    public bool FullBright => FaceProps.Get(Face, FaceFlags.FullBright);

    /// <summary>Lua: <c>face:set_texture("name")</c> — sets this face's texture (undoable).</summary>
    [ScriptDoc("Sets this face's texture.", Example = "f:set_texture(\"rock02.tga\")")]
    public void SetTexture(string texture)
    {
        Edit($"Set face texture {texture}", (g, fi) =>
            g.Faces[fi].Texture = GeometryUtil.EnsureTexture(g, texture));
        _ctx.Changes.Record("retextured");
    }

    /// <summary>Lua: <c>face:set_flag("detail"|"show_sky"|"full_bright"|"mirrored"|"has_alpha", true|false)</c>.</summary>
    [ScriptDoc("Sets a face flag: \"detail\", \"show_sky\", \"full_bright\", \"mirrored\" or \"has_alpha\".", Example = "f:set_flag(\"show_sky\", true)")]
    public void SetFlag(string flag, bool value)
    {
        FaceFlags f = ParseFlag(flag);
        Edit($"Set face {flag}", (g, fi) => FaceProps.Set(g.Faces[fi], f, value));
        _ctx.Changes.Record("flag");
    }

    // ---- V2 world-geometry reads ------------------------------------------------

    [ScriptDoc("The world-space outward unit normal (vec).", Example = "if f.normal.y > 0.7 then log.info('floor') end", Since = 2)]
    public Vec3 Normal
    {
        get
        {
            Brush b = Brush;
            return ScriptBrushHandle.WorldNormal(b, FaceOf(b));
        }
    }

    [ScriptDoc("The world-space corner positions in face order (vec[]).", Since = 2)]
    public Vec3[] Vertices
    {
        get
        {
            Brush b = Brush;
            return GeometryUtil.Corners(b.Geometry, FaceOf(b)).Select(v => BrushTransform.WorldVertex(b, v)).ToArray();
        }
    }

    [ScriptDoc("The world-space centroid of the corners (vec).", Since = 2)]
    public Vec3 Center => GeometryUtil.Centroid(Vertices);

    [ScriptDoc("The face area in square metres.", Since = 2)]
    public double Area
    {
        get
        {
            Brush b = Brush;
            return GeometryUtil.Area(GeometryUtil.Corners(b.Geometry, FaceOf(b)));
        }
    }

    [ScriptDoc("Per-corner texture coordinates in tile units, in face order (uv[]).", Since = 2)]
    public ScriptUv[] Uvs => Face.Vertices.Select(v => new ScriptUv(v.TextureCoords.U, v.TextureCoords.V)).ToArray();

    [ScriptDoc("The number of corners.", Since = 2)]
    public int VertexCount => Face.Vertices.Count;

    // ---- V2 UV mapping (each one undo-able edit on the owning brush; returns the face) ----

    [ScriptDoc("Box-maps the face in world space: no ppm = one repeat per 4 m (a new brush's tiling); ppm = pixels per metre by texture size (RED: 256). Returns the face.", Example = "f:box_map()", Since = 2)]
    public ScriptFaceHandle BoxMap(double ppm = ScriptBrushHandle.NewBrushTiling)
    {
        BoxMapScale s = ScriptBrushHandle.ScaleFromPpm("face:box_map", ppm);
        EditUv("Box map face", (b, f, w, h) =>
        {
            (float p, int pw, int ph) = s.For(w, h);
            UvOps.BoxMap(b.Geometry, f, b.Rotation, b.Position, p, pw, ph);
        });
        return this;
    }

    [ScriptDoc("Planar-maps the face onto its own world plane: no ppm = one repeat per 4 m (a new brush's tiling); ppm = pixels per metre by texture size. Returns the face.", Example = "f:planar_map() f:planar_map(128)", Since = 2)]
    public ScriptFaceHandle PlanarMap(double ppm = ScriptBrushHandle.NewBrushTiling)
    {
        BoxMapScale s = ScriptBrushHandle.ScaleFromPpm("face:planar_map", ppm);
        int idx = _faceIndex;
        EditUv("Planar map face", (b, f, w, h) =>
        {
            (float p, int pw, int ph) = s.For(w, h);
            UvOps.PlanarMap(b.Geometry, new[] { idx }, b.Rotation, b.Position, ScriptBrushHandle.WorldNormal(b, f), p, pw, ph);
        });
        return this;
    }

    [ScriptDoc("Cylinder-maps the face around world axis \"x\"|\"y\"|\"z\" (default \"y\"): no ppm = one repeat per 4 m; ppm = pixels per metre by texture size. Returns the face.", Example = "f:cylinder_map(\"y\")", Since = 2)]
    public ScriptFaceHandle CylinderMap(string axis = "y", double ppm = ScriptBrushHandle.NewBrushTiling)
    {
        int a = ScriptBrushHandle.AxisIndex(axis, "face:cylinder_map");
        BoxMapScale s = ScriptBrushHandle.ScaleFromPpm("face:cylinder_map", ppm);
        EditUv("Cylinder map face", (b, f, w, h) =>
        {
            (float p, int pw, int ph) = s.For(w, h);
            UvOps.CylinderMap(b.Geometry, f, b.Rotation, b.Position, a, p, pw, ph);
        });
        return this;
    }

    [ScriptDoc("Fits the texture to the face so its world footprint spans one tile (aspect kept unless preserve_aspect is false). Returns the face.", Example = "f:fit()", Since = 2)]
    public ScriptFaceHandle Fit(bool preserveAspect = true)
    {
        EditUv("Fit face texture", (b, f, _, _) =>
        {
            UvOps.UvFitTransform t = UvOps.ComputeFitTransform(new[] { new UvOps.FitFace(b.Geometry, f, b.Rotation, b.Position) }, preserveAspect);
            UvOps.ApplyFit(b.Geometry, f, b.Rotation, b.Position, t);
        });
        return this;
    }

    [ScriptDoc("Scales the UVs about their centroid by (su, sv); factors must be non-zero (negative flips). Returns the face.", Example = "f:scale_uv(2, 2)", Since = 2)]
    public ScriptFaceHandle ScaleUv(double su, double sv)
    {
        RequireScale("face:scale_uv", su, sv);
        EditUv("Scale face UVs", (_, f, _, _) => UvOps.Scale(f, (float)su, (float)sv));
        return this;
    }

    [ScriptDoc("Offsets the UVs by (du, dv) tile units. Returns the face.", Example = "f:offset_uv(0.5, 0)", Since = 2)]
    public ScriptFaceHandle OffsetUv(double du, double dv)
    {
        ScriptBrushHandle.RequireFinite("face:offset_uv", du, dv);
        EditUv("Offset face UVs", (_, f, _, _) => UvOps.Offset(f, (float)du, (float)dv));
        return this;
    }

    [ScriptDoc("Rotates the UVs counter-clockwise by degrees about their centroid. Returns the face.", Example = "f:rotate_uv(90)", Since = 2)]
    public ScriptFaceHandle RotateUv(double degrees)
    {
        ScriptBrushHandle.RequireFinite("face:rotate_uv", degrees);
        EditUv("Rotate face UVs", (_, f, _, _) => RotateFaceUvs(f, (float)degrees));
        return this;
    }

    [ScriptDoc("Mirrors the UVs about their centroid along \"u\" or \"v\". Returns the face.", Example = "f:flip_uv(\"u\")", Since = 2)]
    public ScriptFaceHandle FlipUv(string axis)
    {
        string a = (axis ?? string.Empty).Trim().ToLowerInvariant();
        if (a is not ("u" or "v"))
        {
            throw new ScriptApiException($"face:flip_uv: unknown axis '{axis}'.", "Use \"u\" or \"v\".");
        }

        EditUv("Flip face UVs", (_, f, _, _) =>
        {
            if (a == "u")
            {
                UvOps.FlipU(f);
            }
            else
            {
                UvOps.FlipV(f);
            }
        });
        return this;
    }

    [ScriptDoc("Sets every corner's UV from a list {{u, v}, ...} (tile units) whose length equals vertex_count. Returns the face.", Example = "f:set_uvs{{0, 0}, {1, 0}, {1, 1}, {0, 1}}", Since = 2)]
    public ScriptFaceHandle SetUvs(ScriptArgs uvs)
    {
        const string Api = "face:set_uvs";
        if (uvs is null || uvs.Named.Count > 0)
        {
            throw new ScriptApiException($"{Api}: pass a list of {{u, v}} pairs, one per corner.");
        }

        var parsed = new Uv[uvs.Items.Count];
        for (int i = 0; i < parsed.Length; i++)
        {
            parsed[i] = ToUv(Api, i + 1, uvs.Items[i]);
        }

        int corners = VertexCount;
        if (parsed.Length != corners)
        {
            throw new ScriptApiException($"{Api}: got {parsed.Length} UV(s) but the face has {corners} corners.",
                "Pass exactly one {u, v} per corner (see face.vertex_count).");
        }

        EditUv("Set face UVs", (_, f, _, _) =>
        {
            for (int i = 0; i < parsed.Length; i++)
            {
                f.Vertices[i].TextureCoords = parsed[i];
            }
        });
        return this;
    }

    // ---- Helpers ------------------------------------------------------------------

    /// <summary>Rotates all of a face's UVs about their centroid (the UV Unwrap editor's rotate).</summary>
    internal static void RotateFaceUvs(Face f, float degrees)
    {
        var uvs = f.Vertices.Select(v => v.TextureCoords).ToList();
        UnwrapOps.Rotate(uvs, Enumerable.Range(0, uvs.Count).ToList(), degrees);
        for (int i = 0; i < uvs.Count; i++)
        {
            f.Vertices[i].TextureCoords = uvs[i];
        }
    }

    internal static void RequireScale(string api, double su, double sv)
    {
        if (!double.IsFinite(su) || !double.IsFinite(sv) || su == 0 || sv == 0)
        {
            throw new ScriptApiException($"{api}: scale factors must be finite and non-zero (got {ScriptArgs.Format(su)}, {ScriptArgs.Format(sv)}).");
        }
    }

    private static Uv ToUv(string api, int index, object? value)
    {
        switch (value)
        {
            case ScriptUv uv:
                return new Uv((float)uv.U, (float)uv.V);
            case ScriptArgs t when t.Items.Count == 2 && t.Named.Count == 0 && t.Items[0] is double u && t.Items[1] is double v && double.IsFinite(u) && double.IsFinite(v):
                return new Uv((float)u, (float)v);
            case ScriptArgs t when t.Items.Count == 0 && t.Named.Count == 2 && t.Named.TryGetValue("u", out object? ou) && t.Named.TryGetValue("v", out object? ov)
                                   && ou is double u2 && ov is double v2 && double.IsFinite(u2) && double.IsFinite(v2):
                return new Uv((float)u2, (float)v2);
            default:
                throw new ScriptApiException($"{api}: entry #{index} must be {{u, v}}, {{u=, v=}} or a uv record (got {ScriptArgs.LuaTypeOf(value)}).");
        }
    }

    private Face FaceOf(Brush b)
    {
        Geometry g = b.Geometry;
        if (_faceIndex < 0 || _faceIndex >= g.Faces.Count)
        {
            throw new ScriptApiException($"Face {_faceIndex} is out of range for brush {_brushUid}.");
        }

        return g.Faces[_faceIndex];
    }

    /// <summary>One undo-able UV edit of this face; portal/untextured faces are an error.</summary>
    private void EditUv(string description, Action<Brush, Face, int, int> action)
    {
        Brush brush = Brush;
        Face face = FaceOf(brush);
        if (!ScriptBrushHandle.IsMappable(brush.Geometry, face))
        {
            throw new ScriptApiException($"Face {_faceIndex} is a portal face and has no texture mapping.",
                "Skip faces where face.is_portal is true.");
        }

        (int w, int h) = _ctx.TextureSize(brush.Geometry.Textures[face.Texture]);
        int idx = _faceIndex;
        OpResult r = _ctx.Brushes.EditBrushes(new[] { _brushUid }, description, b =>
        {
            action(b, b.Geometry.Faces[idx], w, h);
            return OpResult.Ok();
        });
        if (!r.Success)
        {
            throw new ScriptApiException($"{description} failed: {r.Message}");
        }

        _ctx.NoteBrushEdited(_brushUid);
        _ctx.Changes.Record("uv");
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

        _ctx.NoteBrushEdited(_brushUid);
    }

    internal static FaceFlags ParseFlag(string flag) => (flag ?? string.Empty).Trim().ToLowerInvariant() switch
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
