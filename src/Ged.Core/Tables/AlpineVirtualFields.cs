using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>
/// The virtual inspector fields of the Alpine Faction 1.5 data that is not a plain model property: a rope's decoration
/// block (an optional nested record), a room effect's liquid emission and a keyframe's / mover's path settings (both
/// stored in their own chunks, keyed by UID — <see cref="AlpineAttachedData"/>). Read and written through
/// <see cref="InspectorAccess"/>, so the Properties panel, scripts and MCP share one write path; every write is one
/// undoable command.
/// </summary>
internal static class AlpineVirtualFields
{
    private static readonly HashSet<string> RopeKeys = new(StringComparer.Ordinal)
    {
        "decorations", "decoration_meshes", "decoration_spacing_mode", "decoration_count", "decoration_spacing",
        "decoration_random_order", "decoration_orientation",
    };

    private static readonly HashSet<string> EmissionKeys = new(StringComparer.Ordinal)
    {
        "liquid_emission", "emission_color", "emission_intensity", "emission_distance",
    };

    private static readonly HashSet<string> KeyframeKeys = new(StringComparer.Ordinal)
    {
        "path_segment", "path_tangent", "path_ease", "segment_orientation", "in_handle", "out_handle",
    };

    private static readonly HashSet<string> MoverKeys = new(StringComparer.Ordinal)
    {
        "path_orientation", "riders_turn", "riders_detach_on_leave",
    };

    public static bool IsRopeKey(string key) => RopeKeys.Contains(key);

    public static bool IsEmissionKey(string key) => EmissionKeys.Contains(key);

    public static bool IsKeyframePathKey(string key) => KeyframeKeys.Contains(key);

    public static bool IsMoverPathKey(string key) => MoverKeys.Contains(key);

    // ---- Rope decorations (rope_emitter.cpp:217-270, mfc_types.h:859-929) -----------------------

    /// <summary>
    /// The hint for a virtual field that does not apply right now (null when it applies): a rope's decoration settings
    /// exist only while it has decoration meshes (Alpine writes the block only then, rope_emitter.cpp:217-223).
    /// </summary>
    public static string? Unavailable(object model, string key) =>
        model is AlpineRopeEmitter rope && RopeKeys.Contains(key) && key != "decoration_meshes"
            && rope.Decorations is not { Meshes.Count: > 0 }
            ? "Set decoration_meshes first: Alpine stores the decoration settings only for a rope with decoration meshes."
            : null;

    public static object? GetRope(AlpineRopeEmitter rope, string key)
    {
        AlpineRopeDecorations? d = rope.Decorations is { Meshes.Count: > 0 } block ? block : null;
        return key switch
        {
            "decorations" => d is null ? null : d.Enabled != 0,
            "decoration_meshes" => d is null ? string.Empty : string.Join(";", d.Meshes.Select(m => m.Mesh)),
            "decoration_spacing_mode" => d is null ? null : (int)d.SpacingMode,
            "decoration_count" => d?.Count,
            "decoration_spacing" => d?.Spacing,
            "decoration_random_order" => d is null ? null : d.RandomOrder != 0,
            "decoration_orientation" => d is null ? null : (int)d.OrientMode,
            _ => null,
        };
    }

    public static void SetRope(EditorDocument doc, LevelObject obj, AlpineRopeEmitter rope, InspectorField field, object? value)
    {
        AlpineRopeDecorations? before = rope.Decorations;
        AlpineRopeDecorations? after = before is null ? null : (AlpineRopeDecorations)ModelCloner.Clone(before);
        string key = field.EffectiveKey;
        if (key == "decoration_meshes")
        {
            after = WithMeshes(after, ParseMeshes(value as string ?? string.Empty));
        }
        else
        {
            if (after is not { Meshes.Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Field '{key}' does not apply right now. Set decoration_meshes first: Alpine stores the decoration settings only for a rope with decoration meshes.");
            }

            switch (key)
            {
                case "decorations":
                    after.Enabled = value is true ? (byte)1 : (byte)0;
                    break;
                case "decoration_spacing_mode":
                    after.SpacingMode = (byte)Math.Clamp(ToInt(value), 0, 4);
                    break;
                case "decoration_count":
                    after.Count = Math.Clamp(ToInt(value), AlpineRopeLimits.MinDecorationCount, AlpineRopeLimits.MaxDecorationCount);
                    break;
                case "decoration_spacing":
                    after.Spacing = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), AlpineRopeLimits.MinDecorationSpacing, AlpineRopeLimits.MaxDecorationSpacing);
                    break;
                case "decoration_random_order":
                    after.RandomOrder = value is true ? (byte)1 : (byte)0;
                    break;
                case "decoration_orientation":
                    after.OrientMode = (byte)Math.Clamp(ToInt(value), 0, 1);
                    break;
            }
        }

        RflSection section = obj.Section;
        doc.Undo.Execute(new RelayCommand("Edit " + field.Label,
            () =>
            {
                rope.Decorations = after;
                section.Dirty = true;
            },
            () =>
            {
                rope.Decorations = before;
                section.Dirty = true;
            }));
    }

    /// <summary>
    /// The decoration block for a new mesh list: slots keep their offsets and effects by index (Alpine keeps the mesh
    /// names and their per-slot effects index-locked, mfc_types.h:904-910); new slots start at Alpine's slot defaults;
    /// an empty list drops the block (Alpine writes it only for a rope with meshes, rope_emitter.cpp:217-223). A rope
    /// that had no block gets one with Decorations on and Alpine's placement defaults (mfc_types.h:906-916).
    /// </summary>
    private static AlpineRopeDecorations? WithMeshes(AlpineRopeDecorations? block, IReadOnlyList<string> meshes)
    {
        if (meshes.Count == 0)
        {
            return null;
        }

        block ??= new AlpineRopeDecorations { Enabled = 1, SpacingMode = 0, Count = 10, Spacing = 1f, RandomOrder = 0, OrientMode = 0 };
        var slots = new List<AlpineRopeDecorationMesh>();
        for (int i = 0; i < meshes.Count; i++)
        {
            AlpineRopeDecorationMesh slot = i < block.Meshes.Count ? block.Meshes[i] : NewSlot();
            slot.Mesh = meshes[i];
            slots.Add(slot);
        }

        block.Meshes = slots;
        return block;
    }

    /// <summary>A decoration slot at Alpine's defaults (DedRopeSlotFx, mfc_types.h:859-886).</summary>
    private static AlpineRopeDecorationMesh NewSlot() => new()
    {
        Offset = Vec3.Zero,
        FxFlags = 0,
        GlareBitmap = "LightCorona04.tga",
        GlareColor = new RfColor(255, 255, 255, 255),
        GlareConeAngle = 90f,
        GlareIntensity = 1f,
        GlareRadiusDistance = 0.6f,
        GlareRadiusScale = 0.8f,
        GlareDiminishDistance = -0.05f,
        VolumetricBitmap = string.Empty,
        LightR = 255,
        LightG = 255,
        LightB = 255,
        LightRadius = 5f,
        LightIntensity = 1f,
    };

    private static List<string> ParseMeshes(string text)
    {
        List<string> meshes = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (meshes.Count > AlpineRopeLimits.MaxDecorationMeshes)
        {
            throw new InvalidOperationException($"A rope takes at most {AlpineRopeLimits.MaxDecorationMeshes} decoration meshes (got {meshes.Count}).");
        }

        return meshes;
    }

    // ---- Liquid emission (liquid_emission.h:18-36) ----------------------------------------------

    public static object? GetEmission(EditorDocument doc, int roomEffectUid, string key)
    {
        AlpineLiquidEmission e = AlpineAttachedData.Emission(doc.Rfl, roomEffectUid);
        return key switch
        {
            "liquid_emission" => (int)e.Mode,
            "emission_color" => new RfColor(e.ColorR, e.ColorG, e.ColorB, 255),
            "emission_intensity" => e.Intensity,
            "emission_distance" => e.Distance,
            _ => null,
        };
    }

    public static void SetEmission(EditorDocument doc, int roomEffectUid, InspectorField field, object? value)
    {
        AlpineAttachedData.EditEmission(doc, roomEffectUid, "Edit " + field.Label, e =>
        {
            switch (field.EffectiveKey)
            {
                case "liquid_emission":
                    e.Mode = (byte)Math.Clamp(ToInt(value), 0, 2);
                    break;
                case "emission_color" when value is RfColor c:
                    e.ColorR = c.R;
                    e.ColorG = c.G;
                    e.ColorB = c.B;
                    break;
                case "emission_intensity":
                    e.Intensity = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0f, AlpineLiquidEmissionLimits.MaxIntensity);
                    break;
                case "emission_distance":
                    e.Distance = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0f, AlpineLiquidEmissionLimits.MaxDistance);
                    break;
            }

            return e;
        });
    }

    // ---- Keyframe / mover path settings (mover_path.h:51-151, mover_path.cpp:560-577, 1604-1747) ---------

    public static object? GetKeyframePath(EditorDocument doc, int keyframeUid, string key)
    {
        AlpineMoverPathKeyframe k = AlpineAttachedData.KeyframeData(doc.Rfl, keyframeUid);
        return key switch
        {
            "path_segment" => (int)k.SegmentMode,
            "path_tangent" => (int)k.TangentMode,
            "path_ease" => (int)k.Ease,
            "segment_orientation" => k.SegmentOrient & 0x3,
            "in_handle" => ResolvedOrStored(doc, keyframeUid, k).In,
            "out_handle" => ResolvedOrStored(doc, keyframeUid, k).Out,
            _ => null,
        };
    }

    public static void SetKeyframePath(EditorDocument doc, int keyframeUid, InspectorField field, object? value)
    {
        Group? group = FindGroupOfKeyframe(doc.Rfl, keyframeUid);
        AlpineAttachedData.EditKeyframe(doc, keyframeUid, "Edit " + field.Label, k =>
        {
            switch (field.EffectiveKey)
            {
                case "path_segment":
                    k.SegmentMode = (byte)Math.Clamp(ToInt(value), 0, 1);
                    break;
                case "path_tangent":
                    byte tangent = (byte)Math.Clamp(ToInt(value), 0, 2);
                    if (k.TangentMode == 0 && tangent != 0 && group is not null
                        && MoverPathGeometry.ResolvedHandles(doc.Rfl, group, keyframeUid) is { } resolved)
                    {
                        // Leaving Auto keeps the shape: the handles start where Auto had them (mover_path.cpp:1704-1717).
                        k.InHandle = resolved.In;
                        k.OutHandle = resolved.Out;
                    }

                    k.TangentMode = tangent;
                    break;
                case "path_ease":
                    k.Ease = (byte)Math.Clamp(ToInt(value), 0, 3);
                    break;
                case "segment_orientation":
                    // Only the low two bits are the segment's orient mode; the others are reserved and kept.
                    k.SegmentOrient = (byte)((k.SegmentOrient & ~0x3) | Math.Clamp(ToInt(value), 0, 2));
                    break;
                case "in_handle" or "out_handle" when value is Vec3 v:
                    // A handle edit (Alpine's handle drag, mover_path.cpp:560-577): handles start as drawn, Auto
                    // becomes Aligned, and an Aligned keyframe turns the other handle opposite at its own length.
                    (Vec3 In, Vec3 Out) start = ResolvedOrStored(doc, keyframeUid, k);
                    k.InHandle = start.In;
                    k.OutHandle = start.Out;
                    if (k.TangentMode == 0)
                    {
                        k.TangentMode = 1;
                    }

                    Vec3 moved = AlpineAttachedData.SanitizeHandle(v);
                    bool isOut = field.EffectiveKey == "out_handle";
                    Vec3 other = isOut ? k.InHandle : k.OutHandle;
                    if (k.TangentMode == 1)
                    {
                        float len = moved.Length();
                        other = len > 1e-6f ? moved.Scale(-other.Length() / len) : Vec3.Zero;
                    }

                    if (isOut)
                    {
                        k.OutHandle = moved;
                        k.InHandle = other;
                    }
                    else
                    {
                        k.InHandle = moved;
                        k.OutHandle = other;
                    }

                    break;
            }

            return k;
        });
    }

    public static object? GetMoverPath(EditorDocument doc, Group mover, string key)
    {
        AlpineMoverPathMover m = AlpineAttachedData.MoverSettings(doc.Rfl, mover);
        return key switch
        {
            "path_orientation" => (int)m.OrientMode,
            "riders_turn" => (m.RiderFlags & 0x1) != 0,
            "riders_detach_on_leave" => (m.RiderFlags & 0x2) != 0,
            _ => null,
        };
    }

    public static void SetMoverPath(EditorDocument doc, Group mover, InspectorField field, object? value)
    {
        AlpineAttachedData.EditMover(doc, mover, "Edit " + field.Label, m =>
        {
            switch (field.EffectiveKey)
            {
                case "path_orientation":
                    m.OrientMode = (byte)Math.Clamp(ToInt(value), 0, 2);
                    break;
                case "riders_turn":
                    m.RiderFlags = value is true ? (byte)(m.RiderFlags | 0x1) : (byte)(m.RiderFlags & ~0x1);
                    break;
                case "riders_detach_on_leave":
                    m.RiderFlags = value is true ? (byte)(m.RiderFlags | 0x2) : (byte)(m.RiderFlags & ~0x2);
                    break;
            }

            return m;
        });
    }

    /// <summary>The handles as drawn: resolved for an Automatic tangent, stored otherwise.</summary>
    private static (Vec3 In, Vec3 Out) ResolvedOrStored(EditorDocument doc, int keyframeUid, AlpineMoverPathKeyframe k)
    {
        if (k.TangentMode == 0 && FindGroupOfKeyframe(doc.Rfl, keyframeUid) is { } group
            && MoverPathGeometry.ResolvedHandles(doc.Rfl, group, keyframeUid) is { } resolved)
        {
            return resolved;
        }

        return (k.InHandle, k.OutHandle);
    }

    private static Group? FindGroupOfKeyframe(RflFile rfl, int keyframeUid)
    {
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is GroupsSection gs)
            {
                foreach (Group g in gs.Groups)
                {
                    if (g.IsMoving != 0 && g.MovingData is { } data && data.Keyframes.Any(k => k.Uid == keyframeUid))
                    {
                        return g;
                    }
                }
            }
        }

        return null;
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        int i => i,
        _ => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };
}
