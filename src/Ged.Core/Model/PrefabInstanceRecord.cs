using System.Collections.Generic;

namespace Ged.Core.Model;

/// <summary>
/// Lineage metadata for one placed prefab instance. The instance's objects and
/// brushes are REAL level content (baking is inherent); this record only remembers which UIDs
/// belong to the instance, the prefab it came from, and where it was placed — so the editor
/// can re-instantiate the members when the prefab is updated (propagation), preserving external
/// inbound links via the stable member-index→UID order and preserving the placement transform.
/// </summary>
public sealed class PrefabInstanceRecord
{
    /// <summary>Level-unique instance id (allocated when the instance is placed).</summary>
    public int InstanceId { get; set; }

    /// <summary>The source prefab's name (matches the <c>.gedprefab</c> stem).</summary>
    public string PrefabName { get; set; } = string.Empty;

    /// <summary>A content hash of the prefab payload at placement time (staleness detection).</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>The instance's member UIDs in placement order (index = stable member index).</summary>
    public List<int> MemberUids { get; set; } = new();

    /// <summary>The placement pivot position (the import offset).</summary>
    public Vec3 PivotPosition { get; set; }

    /// <summary>The placement pivot rotation (stored for fidelity; identity for camera placement).</summary>
    public Mat3 PivotRotation { get; set; } = Mat3.Identity;

    /// <summary>Set when a member has been locally edited; propagation skips modified instances by default.</summary>
    public bool Modified { get; set; }

    public PrefabInstanceRecord Clone() => new()
    {
        InstanceId = InstanceId,
        PrefabName = PrefabName,
        SourceHash = SourceHash,
        MemberUids = new List<int>(MemberUids),
        PivotPosition = PivotPosition,
        PivotRotation = PivotRotation,
        Modified = Modified,
    };
}
