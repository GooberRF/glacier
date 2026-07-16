using System;
using System.Globalization;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>
/// Reads and writes an event field's <em>editor value</em> against the raw
/// <see cref="RflEvent"/> slots, honouring the disassembly-confirmed traps
/// (Message's int-from-float, Goto's int==1 bools, Skybox_State's single-char
/// str flag, keyword dropdowns, index-vs-text dropdown saves). The inspector and
/// factory drive events entirely through this — never by poking slots directly.
/// </summary>
public static class EventFieldAccess
{
    /// <summary>Boxed editor value for a field: string, int, float, or bool per the field's editor kind.</summary>
    public static object? Get(EventFieldSpec f, RflEvent ev)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(ev);
        return f.Editor switch
        {
            EventEditor.Text or EventEditor.FilePicker => ReadStr(f.Slot, ev),
            EventEditor.Int or EventEditor.UidPicker => ReadInt(f.Slot, ev),
            EventEditor.Float => ReadFloat(f.Slot, ev),
            EventEditor.Bool => ReadBool(f.Slot, ev),
            EventEditor.IntAsFloat => (int)MathF.Round(ReadFloat(f.Slot, ev)),
            EventEditor.BoolAsInt => ReadInt(f.Slot, ev) == 1,
            EventEditor.FlagChar => !string.IsNullOrEmpty(ReadStr(f.Slot, ev)),
            EventEditor.Dropdown => f.SaveIndex ? ReadInt(f.Slot, ev) : (object)ReadStr(f.Slot, ev),
            _ => null,
        };
    }

    /// <summary>Writes a boxed editor value back onto the raw slots.</summary>
    public static void Set(EventFieldSpec f, RflEvent ev, object? value)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(ev);
        switch (f.Editor)
        {
            case EventEditor.Text:
            case EventEditor.FilePicker:
                WriteStr(f.Slot, ev, value as string ?? string.Empty);
                break;
            case EventEditor.Int:
            case EventEditor.UidPicker:
                WriteInt(f.Slot, ev, ToInt(value));
                break;
            case EventEditor.Float:
                WriteFloat(f.Slot, ev, ToFloat(value));
                break;
            case EventEditor.Bool:
                WriteBool(f.Slot, ev, ToBool(value));
                break;
            case EventEditor.IntAsFloat:
                WriteFloat(f.Slot, ev, ToInt(value));
                break;
            case EventEditor.BoolAsInt:
                WriteInt(f.Slot, ev, ToBool(value) ? 1 : 0);
                break;
            case EventEditor.FlagChar:
                WriteStr(f.Slot, ev, ToBool(value) ? f.OnChar : string.Empty);
                break;
            case EventEditor.Dropdown:
                if (f.SaveIndex)
                {
                    WriteInt(f.Slot, ev, ToInt(value));
                }
                else
                {
                    WriteStr(f.Slot, ev, value as string ?? string.Empty);
                }

                break;
        }
    }

    private static string ReadStr(EventSlot s, RflEvent e) => s == EventSlot.Str2 ? e.Str2 : e.Str1;

    private static void WriteStr(EventSlot s, RflEvent e, string v)
    {
        if (s == EventSlot.Str2)
        {
            e.Str2 = v;
        }
        else
        {
            e.Str1 = v;
        }
    }

    private static int ReadInt(EventSlot s, RflEvent e) => s == EventSlot.Int2 ? e.Int2 : e.Int1;

    private static void WriteInt(EventSlot s, RflEvent e, int v)
    {
        if (s == EventSlot.Int2)
        {
            e.Int2 = v;
        }
        else
        {
            e.Int1 = v;
        }
    }

    private static float ReadFloat(EventSlot s, RflEvent e) => s == EventSlot.Float2 ? e.Float2 : e.Float1;

    private static void WriteFloat(EventSlot s, RflEvent e, float v)
    {
        if (s == EventSlot.Float2)
        {
            e.Float2 = v;
        }
        else
        {
            e.Float1 = v;
        }
    }

    private static bool ReadBool(EventSlot s, RflEvent e) => (s == EventSlot.Bool2 ? e.Bool2 : e.Bool1) != 0;

    private static void WriteBool(EventSlot s, RflEvent e, bool v)
    {
        byte b = v ? (byte)1 : (byte)0;
        if (s == EventSlot.Bool2)
        {
            e.Bool2 = b;
        }
        else
        {
            e.Bool1 = b;
        }
    }

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        bool b => b ? 1 : 0,
        float f => (int)MathF.Round(f),
        double d => (int)Math.Round(d),
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) => p,
        _ => 0,
    };

    private static float ToFloat(object? v) => v switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float p) => p,
        _ => 0f,
    };

    private static bool ToBool(object? v) => v switch
    {
        bool b => b,
        int i => i != 0,
        string s => s is "1" or "true" or "True",
        _ => false,
    };
}
