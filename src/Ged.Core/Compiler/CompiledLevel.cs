using System.Collections.Generic;
using Ged.Core.Lighting;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// The product of a geometry build: the compiled <see cref="Geometry"/> (ready
/// to drop into a static_geometry section), the lightmap atlas pages, the build
/// report, and the Alpine room↔brush uid map for geoable/breakable brushes.
/// </summary>
public sealed class CompiledLevel
{
    public Geometry Geometry { get; set; } = new();

    public List<Lightmap> Lightmaps { get; set; } = new();

    public BuildReport Report { get; set; } = new();

    /// <summary>
    /// [ALPINE] Compiled room UID each isolated (geoable/breakable) brush landed in,
    /// keyed by brush UID. Mirrors RED's save-time <c>compute_geoable_room_uids</c> /
    /// <c>compute_breakable_room_uids</c> (editor_patch/level.cpp): the game matches these
    /// room UIDs against the compiled detail rooms to apply geomod destruction and breakable
    /// materials, so they must be recomputed whenever the geometry is rebuilt.
    /// </summary>
    public Dictionary<int, int> BrushRoomUid { get; } = new();

    /// <summary>
    /// True when this build ran the Alpine path (<see cref="CompileOptions.Alpine"/>). Gates the
    /// geoable/breakable room-uid write-back so a non-Alpine compile of an Alpine file never
    /// zeroes valid tables.
    /// </summary>
    public bool AlpineBuild { get; set; }

    /// <summary>Lighting bake measurements (null when no bake ran).</summary>
    public BakeStats? BakeStats { get; set; }

    /// <summary>
    /// Mover brush UIDs whose geometry was re-baked (lightmap surfaces + per-vertex UVs rebuilt into this
    /// build's atlas by <see cref="MoverLighting"/>). RED bakes mover surfaces into the shared atlas at the
    /// rest position; GED must too, or the movers keep stale references into the regenerated atlas and render
    /// dark. Non-empty ⇒ the caller re-serialises the movers section.
    /// </summary>
    public HashSet<int> BakedMoverUids { get; set; } = new();

    /// <summary>
    /// Per-brush-face survival through the CSG solve, keyed by brush UID; the array
    /// index is the brush's local face index and the value is true when at least one
    /// fragment of that face reached the open (visible) set. Only CSG-participating
    /// brushes (plain air/solid) get an entry — portal and detail/geoable brushes
    /// never lose faces to the boolean solve, and the brush overlays always draw
    /// brushes without an entry in full ("Draw unmerged brushwork" toggle).
    /// </summary>
    public Dictionary<int, bool[]> SurvivingBrushFaces { get; } = new();

    /// <summary>
    /// The first global FaceId assigned to each CSG-participating brush (same keys as
    /// <see cref="SurvivingBrushFaces"/>). A compiled fragment in <see cref="Geometry"/>
    /// belongs to brush uid with local face index <c>fragment.FaceId - BrushFaceIdStart[uid]</c>
    /// — the inverse of the survival mapping. Lets the brush overlay index compiled
    /// fragments back to authored brush faces (item 5: partial-clip fragment overlay).
    /// </summary>
    public Dictionary<int, int> BrushFaceIdStart { get; } = new();
}
