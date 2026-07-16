using System;
using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;

namespace Ged.Core.Scripting;

/// <summary>
/// A script handle onto one brush, identified by UID so it survives edits that rebuild the
/// brush list (plan §5.3). Every mutation routes through <see cref="BrushEditor.EditBrushes"/>
/// (undoable, snapshot-rollback-on-failure), so brush edits join the run's single transaction.
/// </summary>
public sealed class ScriptBrushHandle
{
    private readonly ScriptContext _ctx;

    internal ScriptBrushHandle(ScriptContext ctx, int uid)
    {
        _ctx = ctx;
        Uid = uid;
    }

    /// <summary>Lua: <c>brush.uid</c>.</summary>
    public int Uid { get; }

    private Brush Model => _ctx.Brushes.FindBrush(Uid)
        ?? throw new ScriptApiException($"Brush {Uid} no longer exists.");

    /// <summary>Lua: <c>brush.pos</c> — the brush origin.</summary>
    public Vec3 Pos => Model.Position;

    /// <summary>Lua: <c>brush.face_count</c>.</summary>
    public int FaceCount => Model.Geometry.Faces.Count;

    /// <summary>Lua: <c>brush.air</c> — true when the brush is an air (subtractive) brush.</summary>
    public bool Air => HasFlag(BrushFlags.Air);

    /// <summary>Lua: <c>brush.solid</c> — the inverse of <c>air</c>.</summary>
    public bool Solid => !HasFlag(BrushFlags.Air);

    /// <summary>Lua: <c>brush.portal</c>.</summary>
    public bool Portal => HasFlag(BrushFlags.Portal);

    /// <summary>Lua: <c>brush.detail</c>.</summary>
    public bool Detail => HasFlag(BrushFlags.Detail);

    /// <summary>Lua: <c>brush.geoable</c> — Alpine geo-modifiable.</summary>
    public bool Geoable => HasFlag(BrushFlags.Geoable);

    /// <summary>Lua: <c>brush.emits_steam</c>.</summary>
    public bool EmitsSteam => HasFlag(BrushFlags.EmitsSteam);

    /// <summary>Lua: <c>brush.faces</c> — handles for each face (iterate with <c>ipairs</c>).</summary>
    public ScriptFaceHandle[] Faces
    {
        get
        {
            int n = Model.Geometry.Faces.Count;
            var faces = new ScriptFaceHandle[n];
            for (int i = 0; i < n; i++)
            {
                faces[i] = new ScriptFaceHandle(_ctx, Uid, i);
            }

            return faces;
        }
    }

    /// <summary>Lua: <c>brush:move(dx, dy, dz)</c> — translates the brush (undoable).</summary>
    public void Move(double dx, double dy, double dz)
    {
        var delta = new Vec3((float)dx, (float)dy, (float)dz);
        Edit("Move brush", b => BrushTransform.Move(b, delta));
        _ctx.Changes.Record("moved");
    }

    /// <summary>Lua: <c>brush:rotate(axis, degrees)</c> — rotates about its origin; axis = "x"|"y"|"z" (undoable).</summary>
    public void Rotate(string axis, double degrees)
    {
        Mat3 rot = RotationFor(axis, degrees);
        Edit("Rotate brush", b => BrushTransform.Rotate(b, rot));
        _ctx.Changes.Record("rotated");
    }

    /// <summary>Lua: <c>brush:set_texture("name")</c> — sets every face's texture (undoable).</summary>
    public void SetTexture(string texture)
    {
        Edit($"Set brush texture {texture}", b =>
        {
            int tex = GeometryUtil.EnsureTexture(b.Geometry, texture);
            foreach (Face f in b.Geometry.Faces)
            {
                if (!f.IsPortalFace)
                {
                    f.Texture = tex;
                }
            }
        });
        _ctx.Changes.Record("retextured");
    }

    /// <summary>Lua: <c>brush:set_detail(true|false)</c> — toggles the detail flag (undoable).</summary>
    public void SetDetail(bool value) => SetFlag(BrushFlags.Detail, value, "detail");

    /// <summary>Lua: <c>brush:set_portal(true|false)</c>.</summary>
    public void SetPortal(bool value) => SetFlag(BrushFlags.Portal, value, "portal");

    /// <summary>Lua: <c>brush:set_air(true|false)</c>.</summary>
    public void SetAir(bool value) => SetFlag(BrushFlags.Air, value, "air");

    /// <summary>Lua: <c>brush:select(additive?)</c>.</summary>
    public void Select(bool additive = false) => _ctx.Brushes.SelectBrush(Uid, additive);

    /// <summary>Lua: <c>brush:delete()</c> — deletes the brush (undoable). Destructive.</summary>
    public void Delete()
    {
        _ctx.RequireDestructive($"delete brush {Uid}");
        _ctx.Brushes.DeleteBrushes(new[] { Uid });
        _ctx.Changes.Record("deleted");
    }

    private bool HasFlag(BrushFlags flag) => (Model.Flags & (uint)flag) != 0;

    private void SetFlag(BrushFlags flag, bool value, string label)
    {
        Edit($"Set brush {label}", b =>
        {
            if (value)
            {
                b.Flags |= (uint)flag;
            }
            else
            {
                b.Flags &= ~(uint)flag;
            }
        });
        _ctx.Changes.Record("flag");
    }

    private void Edit(string description, Action<Brush> mutate)
    {
        OpResult r = _ctx.Brushes.EditBrushes(new[] { Uid }, description, b =>
        {
            mutate(b);
            return OpResult.Ok();
        });
        if (!r.Success)
        {
            throw new ScriptApiException($"{description} failed: {r.Message}");
        }
    }

    internal static Mat3 RotationFor(string axis, double degrees)
    {
        float rad = (float)(degrees * Math.PI / 180.0);
        return (axis ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "x" => Mat3Math.RotationX(rad),
            "y" => Mat3Math.RotationY(rad),
            "z" => Mat3Math.RotationZ(rad),
            _ => throw new ScriptApiException($"Unknown axis '{axis}'.", "Use \"x\", \"y\", or \"z\"."),
        };
    }

    public override string ToString() => $"brush#{Uid}";
}
