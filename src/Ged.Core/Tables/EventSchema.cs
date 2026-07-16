using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Tables;

/// <summary>The physical generic-record slot an event field reads and writes.</summary>
public enum EventSlot
{
    None,
    Bool1,
    Bool2,
    Int1,
    Int2,
    Float1,
    Float2,
    Str1,
    Str2,
}

/// <summary>
/// How an event field is edited and how its editor value maps onto its
/// <see cref="EventSlot"/>. Most kinds are a straight read of the slot; the
/// last four encode the disassembly-confirmed slot traps.
/// </summary>
public enum EventEditor
{
    /// <summary>Free text into a str slot.</summary>
    Text,

    /// <summary>Signed integer into an int slot.</summary>
    Int,

    /// <summary>Float into a float slot.</summary>
    Float,

    /// <summary>Checkbox into a bool slot (byte 0/1).</summary>
    Bool,

    /// <summary>Choice list; <see cref="EventFieldSpec.SaveIndex"/> stores the index into an
    /// int slot, otherwise the option text into a str slot.</summary>
    Dropdown,

    /// <summary>A level object UID edited into an int slot (a link-target picker).</summary>
    UidPicker,

    /// <summary>A VFS filename edited into a str slot (see <see cref="EventFieldSpec.FileKind"/>).</summary>
    FilePicker,

    /// <summary>Integer value stored in a <em>float</em> slot (the runtime __ftol's it back).
    /// Message's third integer lives in float1 this way.</summary>
    IntAsFloat,

    /// <summary>Checkbox stored in an <em>int</em> slot as 1/0 (Goto's int1==1 / int2==1 flags).</summary>
    BoolAsInt,

    /// <summary>Checkbox stored as a single-character <em>str</em> slot; the runtime reads str1[0]
    /// (Skybox_State). On = <see cref="EventFieldSpec.OnChar"/>, off = empty.</summary>
    FlagChar,
}

/// <summary>File-browser filter kind for a <see cref="EventEditor.FilePicker"/> field.</summary>
public enum EventFileKind
{
    None,
    Sound,
    Vclip,
    Bitmap,
    Mesh,
    Mvf,
    Video,
    Animation,
}

/// <summary>Coarse expected link-target kinds, used by the link validator.</summary>
public enum EventLinkTarget
{
    Any,
    None,
    Entity,
    Mover,
    Trigger,
    Event,
    Object,
    Item,
    Clutter,
    NavPoint,
    ParticleEmitter,
    BoltEmitter,
    PushRegion,
    Light,
    AmbientSound,
    RespawnPoint,
    Room,
    Monitor,
}

/// <summary>One type-specific inspector field of an event class.</summary>
public sealed class EventFieldSpec
{
    public EventFieldSpec(EventSlot slot, EventEditor editor, string label)
    {
        Slot = slot;
        Editor = editor;
        Label = label;
    }

    public EventSlot Slot { get; }

    public EventEditor Editor { get; }

    public string Label { get; }

    /// <summary>Dropdown options, in save-index order.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Dropdown stores its selected index into the int slot (vs the option text into a str slot).</summary>
    public bool SaveIndex { get; init; }

    public EventFileKind FileKind { get; init; }

    /// <summary>The character written to the str slot when a <see cref="EventEditor.FlagChar"/> is on.</summary>
    public string OnChar { get; init; } = "1";
}

/// <summary>
/// The full data-driven definition of one event class (stock or Alpine): its
/// game ID, browser category, orientation/version/forwarding traits, expected
/// link targets, and the ordered type-specific inspector fields. This is the
/// single source the event inspector and factory render from — there is no
/// per-event dialog code.
/// </summary>
public sealed class EventSchema
{
    public EventSchema(string className, int gameId, string category)
    {
        ClassName = className;
        GameId = gameId;
        Category = category;
    }

    /// <summary>The RFL <c>class_name</c> (the catalog key).</summary>
    public string ClassName { get; }

    /// <summary>Authoritative game EventType id (0–89 stock, 100–157 Alpine).</summary>
    public int GameId { get; }

    /// <summary>Browser tree category (stock section or AF_* for Alpine).</summary>
    public string Category { get; }

    public bool IsAlpine => GameId >= 100;

    /// <summary>Minimum save-target RFL version (Alpine events require ≥300 = 0x12C).</summary>
    public int MinVersion { get; init; }

    /// <summary>Persists a 3×3 orientation (the 7 directional classes).</summary>
    public bool HasOrientation { get; init; }

    /// <summary>Forwards the on/off signal to its links (false for the 7 self-signalling stock events).</summary>
    public bool ForwardsSignal { get; init; } = true;

    /// <summary>Appears in the placeable event browser (false for the 3 auto/legacy classes).</summary>
    public bool Placeable { get; init; } = true;

    public string Description { get; init; } = string.Empty;

    /// <summary>Type-specific inspector fields (script name, delay, color, links, position are always shown separately).</summary>
    public IReadOnlyList<EventFieldSpec> Fields { get; init; } = Array.Empty<EventFieldSpec>();

    /// <summary>Expected link-target object kinds (validation hint; empty = any).</summary>
    public IReadOnlyList<EventLinkTarget> LinkTargets { get; init; } = Array.Empty<EventLinkTarget>();

    public EventFieldSpec? Field(EventSlot slot) => Fields.FirstOrDefault(f => f.Slot == slot);
}
