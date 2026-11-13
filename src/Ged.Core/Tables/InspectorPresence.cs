using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Tables;

/// <summary>
/// One model member that the RFL format stores only under a condition — the nullable half of a
/// discriminated record (a trigger's sphere radius vs its box dimensions, a nav point's orientation
/// that exists only when it is directional, …). Mirrors the section writers exactly: while
/// <see cref="IsPresent"/> is false the writer ignores the member, while it is true a null member is
/// written as <see cref="Default"/> (the writer's own <c>??</c> fallback).
/// </summary>
public sealed class ConditionalMember
{
    internal ConditionalMember(string path, Func<object, bool> isPresent, object defaultValue, string hint,
        Func<object>? createDefault = null, bool clearWhenAbsent = true)
    {
        Path = path;
        IsPresent = isPresent;
        Default = defaultValue;
        Hint = hint;
        _createDefault = createDefault;
        ClearWhenAbsent = clearWhenAbsent;
    }

    private readonly Func<object>? _createDefault;

    /// <summary>
    /// False for a nested block the writer simply skips while absent (a room effect's liquid block):
    /// switching the discriminator away keeps it, so switching back restores the earlier values, as
    /// the Properties panel always has.
    /// </summary>
    public bool ClearWhenAbsent { get; }

    /// <summary>A fresh default for a newly present member (a new instance for a reference-type block).</summary>
    public object NewDefault() => _createDefault?.Invoke() ?? Default;

    /// <summary>The dotted model path (same form as <see cref="InspectorField.Path"/>).</summary>
    public string Path { get; }

    /// <summary>Whether the member is stored for the model's current discriminator value.</summary>
    public Func<object, bool> IsPresent { get; }

    /// <summary>The value the writer substitutes for null while the member is present.</summary>
    public object Default { get; }

    /// <summary>What to change first so the member applies (e.g. <c>Set shape to "Box" first.</c>).</summary>
    public string Hint { get; }
}

/// <summary>
/// The conditional (discriminated) members of every object model, derived from the RFL section
/// writers (<c>TriggersSection</c>, <c>GeoRegionsSection</c>, <c>GasRegionsSection</c>,
/// <c>PushRegionsSection</c>, <c>EntitiesSection</c>, <c>AlpineCoronaObjectsSection</c>,
/// <c>NavPoint.Write</c>, <c>EventsSection</c>). The shared field accessor
/// (<see cref="InspectorAccess"/>) uses it so a generic edit can never strand a value the writer
/// ignores (setting a box dimension on a sphere trigger) and so switching a discriminator (shape,
/// directional, …) leaves the record in the same shape a load would produce: newly present members
/// get the writer's fallback value, members that no longer apply are cleared. Neither step changes
/// the saved bytes on its own.
/// </summary>
public static class InspectorPresence
{
    private const string Sphere = "Set shape to \"Sphere\" first.";
    private const string Box = "Set shape to \"Box\" first.";

    private static readonly Dictionary<Type, ConditionalMember[]> Map = new()
    {
        [typeof(Trigger)] = new[]
        {
            M("SphereRadius", o => ((Trigger)o).Shape == Trigger.ShapeSphere, 0f, Sphere),
            M("Rotation", o => ((Trigger)o).Shape != Trigger.ShapeSphere, Mat3.Identity, Box),
            M("BoxHeight", o => ((Trigger)o).Shape != Trigger.ShapeSphere, 0f, Box),
            M("BoxWidth", o => ((Trigger)o).Shape != Trigger.ShapeSphere, 0f, Box),
            M("BoxDepth", o => ((Trigger)o).Shape != Trigger.ShapeSphere, 0f, Box),
            M("OneWay", o => ((Trigger)o).Shape != Trigger.ShapeSphere, (byte)0, Box),
        },
        [typeof(GeoRegion)] = new[]
        {
            M("ShallowGeomodDepth", o => (((GeoRegion)o).Flags & GeoRegion.FlagUseShallowGeomods) != 0, 0f,
                "Set use_shallow_geomods = true first."),
            M("Rotation", o => ((GeoRegion)o).HasRotation, Mat3.Identity,
                "Set shape to \"Box\" (or use shallow geomods on a sphere) first."),
            M("Width", o => ((GeoRegion)o).IsBox, 0f, Box),
            M("Height", o => ((GeoRegion)o).IsBox, 0f, Box),
            M("Depth", o => ((GeoRegion)o).IsBox, 0f, Box),
            M("Radius", o => ((GeoRegion)o).IsSphere || ((GeoRegion)o).Shape == GeoRegion.ShapeTwoPoint, 0f, Sphere),
        },
        [typeof(GasRegion)] = new[]
        {
            M("Radius", o => ((GasRegion)o).Shape == GasRegionsSection.ShapeSphere, 0f, Sphere),
            M("Height", o => ((GasRegion)o).Shape == GasRegionsSection.ShapeBox, 0f, Box),
            M("Width", o => ((GasRegion)o).Shape == GasRegionsSection.ShapeBox, 0f, Box),
            M("Depth", o => ((GasRegion)o).Shape == GasRegionsSection.ShapeBox, 0f, Box),
        },
        [typeof(PushRegion)] = new[]
        {
            M("Radius", o => ((PushRegion)o).Shape == PushRegionsSection.ShapeSphere, 0f, Sphere),
            M("Extents", o => ((PushRegion)o).Shape != PushRegionsSection.ShapeSphere, default(Vec3),
                "Set shape to \"Axis-Aligned Box\" or \"Oriented Box\" first."),
        },
        [typeof(Entity)] = new[]
        {
            M("CustomAttackRange", o => ((Entity)o).UseCustomAttackRange == 1, 0f, "Set use_custom_attack_range = true first."),
        },
        [typeof(AlpineCoronaObject)] = new[]
        {
            M("VolumetricHeight", o => ((AlpineCoronaObject)o).VolumetricBitmap.Length != 0, 0f, "Set volumetric_bitmap first."),
            M("VolumetricLength", o => ((AlpineCoronaObject)o).VolumetricBitmap.Length != 0, 0f, "Set volumetric_bitmap first."),
        },
        [typeof(NavPoint)] = new[]
        {
            M("Rotation", o => ((NavPoint)o).Directional != 0, Mat3.Identity, "Set directional = true first."),
        },
        [typeof(RoomEffect)] = new[]
        {
            // RoomEffectsSection writes the ambient colour only for Ambient Light and the liquid block only for
            // Liquid Room; a newly applicable block gets the Properties panel's starting values.
            new ConditionalMember("AmbientLightColor", o => ((RoomEffect)o).EffectType == RoomEffectsSection.EffectAmbientLight,
                new RfColor(128, 128, 128, 255), "Set type to \"Ambient Light\" first.", clearWhenAbsent: false),
            new ConditionalMember("LiquidProperties", o => ((RoomEffect)o).EffectType == RoomEffectsSection.EffectLiquidRoom,
                new RoomEffectLiquidProperties(), "Set type to \"Liquid Room\" first.",
                createDefault: () => new RoomEffectLiquidProperties { Waveform = 1, LiquidType = 1 }, clearWhenAbsent: false),
        },
        [typeof(RflEvent)] = new[]
        {
            // Saves always write the Alpine version, so presence is judged at that version.
            M("Rotation", o => RflEvent.HasRotation(((RflEvent)o).ClassName, RflFile.AlpineSaveVersion), Mat3.Identity,
                "Only these event classes are oriented: " + string.Join(", ", OrientedEventClasses()) + "."),
        },
    };

    /// <summary>The conditional members of <paramref name="model"/>'s type (empty for unconditional models).</summary>
    public static IReadOnlyList<ConditionalMember> For(object model) =>
        model is not null && Map.TryGetValue(model.GetType(), out ConditionalMember[]? m) ? m : Array.Empty<ConditionalMember>();

    /// <summary>The conditional member at <paramref name="path"/> on <paramref name="model"/>, or null.</summary>
    public static ConditionalMember? Find(object model, string path) =>
        For(model).FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal));

    /// <summary>The hint for a member that does not apply right now, or null when it applies (or is unconditional).</summary>
    public static string? UnavailableHint(object model, string path) =>
        For(model).FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal)
                || (path ?? string.Empty).StartsWith(m.Path + ".", StringComparison.Ordinal)) is { } m && !m.IsPresent(model)
            ? m.Hint
            : null;

    /// <summary>The event classes that persist an orientation at the save version, in catalog order.</summary>
    public static IReadOnlyList<string> OrientedEventClasses() =>
        EventSchemaCatalog.All.Where(s => RflEvent.HasRotation(s.ClassName, RflFile.AlpineSaveVersion))
            .Select(s => s.ClassName).ToList();

    /// <summary>Captures the raw value of every conditional member (for an exact undo).</summary>
    internal static IReadOnlyList<(ConditionalMember Member, object? Raw)> Snapshot(object model) =>
        For(model).Select(m => (m, Read(model, m.Path))).ToList();

    /// <summary>Restores a <see cref="Snapshot"/> verbatim.</summary>
    internal static void Restore(object model, IReadOnlyList<(ConditionalMember Member, object? Raw)> snapshot)
    {
        foreach ((ConditionalMember m, object? raw) in snapshot)
        {
            Write(model, m.Path, raw);
        }
    }

    /// <summary>
    /// Brings the model to its canonical discriminated shape: a present member that is null gets the
    /// writer's fallback; a member that no longer applies is cleared. Never changes the saved bytes.
    /// </summary>
    internal static void Normalize(object model)
    {
        foreach (ConditionalMember m in For(model))
        {
            object? cur = Read(model, m.Path);
            bool present = m.IsPresent(model);
            if (present && cur is null)
            {
                Write(model, m.Path, m.NewDefault());
            }
            else if (!present && cur is not null && m.ClearWhenAbsent)
            {
                Write(model, m.Path, null);
            }
        }
    }

    private static ConditionalMember M(string path, Func<object, bool> present, object def, string hint) => new(path, present, def, hint);

    private static object? Read(object model, string path) =>
        InspectorField.TryResolve(model, path, out object owner, out PropertyInfo prop) ? prop.GetValue(owner) : null;

    private static void Write(object model, string path, object? value)
    {
        if (InspectorField.TryResolve(model, path, out object owner, out PropertyInfo prop) && prop.CanWrite)
        {
            prop.SetValue(owner, value);
        }
    }
}
