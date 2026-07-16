using System;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Editing;

/// <summary>
/// Constructs <see cref="RflEvent"/> instances for a given <see cref="EventSchema"/>:
/// a blank event ready to place, or one pre-filled with representative field
/// values through the slot-aware <see cref="EventFieldAccess"/> (used by the
/// acceptance round-trip). Orientation is attached only when the class + target
/// version actually persist one, so a place→save→reload matches exactly.
/// </summary>
public static class EventFactory
{
    public static RflEvent Create(EventSchema schema, int uid, Vec3 pos, int version)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var ev = new RflEvent
        {
            Uid = uid,
            ClassName = schema.ClassName,
            Position = pos,
            ScriptName = string.Empty,
            Delay = 0f,
        };

        if (RflEvent.HasRotation(schema.ClassName, version))
        {
            ev.Rotation = Mat3.Identity;
        }

        return ev;
    }

    /// <summary>An event pre-filled with representative, round-trip-safe values across every field.</summary>
    public static RflEvent CreateSample(EventSchema schema, int uid, Vec3 pos, int version)
    {
        ArgumentNullException.ThrowIfNull(schema);
        RflEvent ev = Create(schema, uid, pos, version);
        ev.ScriptName = schema.ClassName + "_script";
        ev.Delay = 1.25f;
        if (version >= 0xB0)
        {
            ev.Color = new RfColor(11, 22, 33, 255);
        }

        int i = 1;
        foreach (EventFieldSpec f in schema.Fields)
        {
            EventFieldAccess.Set(f, ev, Sample(f, i++));
        }

        return ev;
    }

    private static object Sample(EventFieldSpec f, int i)
    {
        return f.Editor switch
        {
            EventEditor.Text => "s" + i,
            EventEditor.FilePicker => "file" + i + Ext(f.FileKind),
            EventEditor.Int or EventEditor.UidPicker => i * 3,
            EventEditor.Float => i * 1.5f,
            EventEditor.Bool => i % 2 == 1,
            EventEditor.IntAsFloat => i * 7,
            EventEditor.BoolAsInt => true,
            EventEditor.FlagChar => true,
            EventEditor.Dropdown when f.SaveIndex => Math.Min(i - 1, (f.Options?.Count ?? 1) - 1),
            EventEditor.Dropdown => f.Options is { Count: > 0 } o ? o[Math.Min(i - 1, o.Count - 1)] : string.Empty,
            _ => 0,
        };
    }

    private static string Ext(EventFileKind kind) => kind switch
    {
        EventFileKind.Sound => ".wav",
        EventFileKind.Vclip => ".vcm",
        EventFileKind.Bitmap => ".tga",
        EventFileKind.Mesh => ".v3m",
        EventFileKind.Mvf => ".mvf",
        EventFileKind.Video => ".bik",
        EventFileKind.Animation => ".rfa",
        _ => string.Empty,
    };
}
