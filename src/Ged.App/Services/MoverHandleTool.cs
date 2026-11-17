using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Services;

/// <summary>A curve handle of one keyframe: its mover, keyframe, side (out = toward the next keyframe) and drawn point.</summary>
internal sealed record MoverHandle(Group Mover, Keyframe Keyframe, bool Out, Vector3 Point);

/// <summary>
/// Dragging a selected keyframe's Bézier handles in the viewport, as Alpine RED does (editor_patch/mover_path.cpp:
/// 323-625, 1749-1854): only the handles a curved segment uses are shown and grabbable (handle_shown, :355-363); a
/// press within a few pixels grabs the nearest one; the drag runs in the plane through the handle facing the camera,
/// keeping the grab offset; an Automatic keyframe becomes Aligned and an Aligned one turns its other handle opposite at
/// its own length (apply_handle, :560-577, through the same field write the Lua API uses). The whole drag is one undo
/// step; Cancel puts the handle back.
/// </summary>
internal sealed class MoverHandleTool
{
    /// <summary>How close (pixels) a press must land to grab a handle (find_handle, mover_path.cpp:496).</summary>
    public const float PickPixels = 8f;

    private readonly EditorDocument _doc;
    private Drag? _drag;

    public MoverHandleTool(EditorDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    private sealed class Drag
    {
        public required MoverHandle Handle { get; init; }

        public required Vector3 PlaneNormal { get; init; }

        public required Vector3 GrabOffset { get; init; }

        public required UndoStack.Transaction Tx { get; init; }

        public bool Moved { get; set; }
    }

    /// <summary>The handle being dragged, or null.</summary>
    public MoverHandle? Dragging => _drag?.Handle;

    /// <summary>Every handle shown for the movers of the selected keyframes.</summary>
    public IReadOnlyList<MoverHandle> Visible(IEnumerable<LevelObject> selection)
    {
        var movers = new MoverService(_doc);
        var groups = new List<Group>();
        foreach (LevelObject o in selection)
        {
            if (o.Model is Keyframe k && movers.FindKeyframe(k.Uid)?.Group is { } g && !groups.Contains(g))
            {
                groups.Add(g);
            }
        }

        var handles = new List<MoverHandle>();
        foreach (Group g in groups)
        {
            List<Keyframe> keys = g.MovingData!.Keyframes;
            foreach (MoverPathSegment seg in MoverPathGeometry.Segments(_doc.Rfl, g))
            {
                if (!seg.Curved)
                {
                    continue;
                }

                Keyframe from = keys.First(k => k.Uid == seg.FromUid);
                Keyframe to = keys.First(k => k.Uid == seg.ToUid);
                if (from.HiddenInEditor == 0)
                {
                    handles.Add(new MoverHandle(g, from, true, V(seg.P1)));
                }

                if (to.HiddenInEditor == 0)
                {
                    handles.Add(new MoverHandle(g, to, false, V(seg.P2)));
                }
            }
        }

        return handles;
    }

    /// <summary>
    /// The handle under <paramref name="cursor"/>: within <see cref="PickPixels"/> on screen, nearest the camera.
    /// <paramref name="project"/> maps a world point to the screen (null when behind the camera).
    /// </summary>
    public static MoverHandle? Pick(IEnumerable<MoverHandle> handles, Func<Vector3, Vector2?> project, Vector2 cursor, Vector3 rayOrigin, Vector3 rayDir)
    {
        MoverHandle? best = null;
        float bestDepth = float.MaxValue;
        foreach (MoverHandle h in handles)
        {
            if (project(h.Point) is not { } screen || Vector2.Distance(screen, cursor) > PickPixels)
            {
                continue;
            }

            float depth = Vector3.Dot(h.Point - rayOrigin, rayDir);
            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = h;
            }
        }

        return best;
    }

    /// <summary>The same handle re-read from the level (it moves while dragged).</summary>
    public MoverHandle? Live(MoverHandle h, IEnumerable<LevelObject> selection) =>
        Visible(selection).FirstOrDefault(v => ReferenceEquals(v.Keyframe, h.Keyframe) && v.Out == h.Out);

    /// <summary>Grabs <paramref name="handle"/> with the press ray (mover_path.cpp:1764-1777).</summary>
    public void Begin(MoverHandle handle, Vector3 rayOrigin, Vector3 rayDir)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Cancel();
        float depth = Vector3.Dot(handle.Point - rayOrigin, rayDir);
        _drag = new Drag
        {
            Handle = handle,
            PlaneNormal = rayDir,
            GrabOffset = handle.Point - (rayOrigin + (rayDir * depth)),
            Tx = _doc.Undo.BeginTransaction(handle.Out ? "Drag out handle" : "Drag in handle"),
        };
    }

    /// <summary>Moves the grabbed handle under the cursor ray (update_handle_drag, mover_path.cpp:579-607). True when it moved.</summary>
    public bool Move(Vector3 rayOrigin, Vector3 rayDir)
    {
        if (_drag is not { } d)
        {
            return false;
        }

        float denom = Vector3.Dot(rayDir, d.PlaneNormal);
        if (MathF.Abs(denom) < 1e-4f)
        {
            return false;
        }

        float t = Vector3.Dot(d.Handle.Point - rayOrigin, d.PlaneNormal) / denom;
        if (!float.IsFinite(t) || _doc.FindByUid(d.Handle.Keyframe.Uid) is not { } kf)
        {
            return false;
        }

        Vector3 offset = rayOrigin + (rayDir * t) + d.GrabOffset - V(d.Handle.Keyframe.Position);
        InspectorField field = MoverInspectorSchema.KeyframePathFields.First(f => f.EffectiveKey == (d.Handle.Out ? "out_handle" : "in_handle"));
        InspectorAccess.Set(_doc, kf, field, new CoreVec3(offset.X, offset.Y, offset.Z));
        d.Moved = true;
        return true;
    }

    /// <summary>Releases: one undo step when the handle moved (mover_path.cpp:1787-1806). True when it changed the level.</summary>
    public bool End()
    {
        if (_drag is not { } d)
        {
            return false;
        }

        _drag = null;
        if (d.Moved)
        {
            d.Tx.Commit();
            return true;
        }

        d.Tx.Rollback();
        return false;
    }

    /// <summary>Puts the handle back (cancel_handle_drag, mover_path.cpp:619-625). True when a drag was cancelled.</summary>
    public bool Cancel()
    {
        if (_drag is not { } d)
        {
            return false;
        }

        _drag = null;
        d.Tx.Rollback();
        return true;
    }

    private static Vector3 V(CoreVec3 v) => new(v.X, v.Y, v.Z);
}
