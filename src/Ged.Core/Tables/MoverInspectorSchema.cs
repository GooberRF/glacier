using System.Collections.Generic;

namespace Ged.Core.Tables;

/// <summary>
/// The data-driven field registry for the mover / keyframe inspector — the moving-group
/// analogue of <see cref="ObjectInspectorCatalog"/>, split into the fields that live on a
/// <see cref="Model.Keyframe"/> (per-waypoint) and those on a <see cref="Model.MovingGroupData"/>
/// (per-mover). Reflection <see cref="InspectorField.Get"/>/<see cref="InspectorField.Set"/>
/// resolve directly against those model types, so the App inspector renders every field from this
/// list (undo routed through <c>MoverService.EditKeyframe</c> / <c>EditMover</c>) and a
/// completeness test asserts it against the RED Keyframe-Properties checklist
/// (red-stock-inventory §8). Every RFL <c>moving_group_data</c> / <c>keyframe</c> field RED lets
/// the author edit is present, including <c>no_player_collide</c> — the RED "no player collide"
/// option Glacier previously stored but never surfaced.
/// <para>
/// Two entries are behavioural, not plain model properties, and are marked
/// <see cref="InspectorField.Virtual"/> so the App supplies their accessor: <c>Movement Type</c>
/// (a 1-based enum rendered as a combo) and <c>Hold Open [Alpine]</c> (persisted in
/// <c>alpine_level_properties</c>, not on the group). They still appear here so the field
/// inventory — and its coverage test — is complete.
/// </para>
/// </summary>
public static class MoverInspectorSchema
{
    /// <summary>Movement-type option labels, index+1 = RFL <c>movement_type</c> value (1 one_way … 6 lift).</summary>
    public static readonly IReadOnlyList<string> MovementTypes = new[]
    {
        "One Way", "Ping Pong Once", "Ping Pong Infinite", "Loop Once", "Loop Infinite", "Lift",
    };

    /// <summary>Per-keyframe fields (edit through <c>MoverService.EditKeyframe</c>).</summary>
    public static readonly IReadOnlyList<InspectorField> KeyframeFields = new[]
    {
        new InspectorField("Travel Time to Next", "DepartTravelTime", InspectorEditor.Float),
        new InspectorField("Return Travel", "ReturnTravelTime", InspectorEditor.Float),
        new InspectorField("Pause Time", "PauseTime", InspectorEditor.Float),
        new InspectorField("Accel Time", "AccelTime", InspectorEditor.Float),
        new InspectorField("Decel Time", "DecelTime", InspectorEditor.Float),
        new InspectorField("Degrees About Axis", "DegreesAboutAxis", InspectorEditor.Float),
        new InspectorField("Triggered Event UID", "EventUid", InspectorEditor.Uid),
        new InspectorField("Item UID 1", "ItemUid1", InspectorEditor.Uid),
        new InspectorField("Item UID 2", "ItemUid2", InspectorEditor.Uid),
        new InspectorField("Script Name", "ScriptName", InspectorEditor.Text),
    };

    /// <summary>Per-mover fields (edit through <c>MoverService.EditMover</c>, save for the two virtual ones).</summary>
    public static readonly IReadOnlyList<InspectorField> MoverFields = new[]
    {
        new InspectorField("Movement Type", "MovementType", InspectorEditor.Enum) { Options = MovementTypes, Virtual = true },
        new InspectorField("Is Door", "IsDoor", InspectorEditor.Bool),
        new InspectorField("Rotate In Place", "RotateInPlace", InspectorEditor.Bool),
        new InspectorField("Starts Backwards", "StartsBackwards", InspectorEditor.Bool),
        new InspectorField("Use Travel Time as Speed", "UseTravelTimeAsSpeed", InspectorEditor.Bool),
        new InspectorField("Force Orient", "ForceOrient", InspectorEditor.Bool),
        new InspectorField("No Player Collide", "NoPlayerCollide", InspectorEditor.Bool),
        new InspectorField("Starting Keyframe", "StartingKeyframe", InspectorEditor.Int),
        new InspectorField("Start Sound", "StartSound", InspectorEditor.Text),
        new InspectorField("Start Volume", "StartVol", InspectorEditor.Float),
        new InspectorField("Looping Sound", "LoopingSound", InspectorEditor.Text),
        new InspectorField("Looping Volume", "LoopingVol", InspectorEditor.Float),
        new InspectorField("Stop Sound", "StopSound", InspectorEditor.Text),
        new InspectorField("Stop Volume", "StopVol", InspectorEditor.Float),
        new InspectorField("Close Sound", "CloseSound", InspectorEditor.Text),
        new InspectorField("Close Volume", "CloseVol", InspectorEditor.Float),
        new InspectorField("Hold Open [Alpine]", "HoldOpen", InspectorEditor.Bool) { Virtual = true },
    };
}
