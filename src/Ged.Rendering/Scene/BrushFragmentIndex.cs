using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Rendering.Scene;

/// <summary>
/// Maps each CSG-participating brush face to the compiled fragments that survived the
/// boolean solve (item 5). Built once per geometry build from the compiled static
/// geometry plus the per-brush FaceId ranges, it lets <see cref="BrushEmitter"/> draw a
/// partially-clipped brush face as its surviving fragment(s) — the real visible area —
/// instead of the full authored polygon. The fragment faces index into
/// <see cref="Geometry"/> (world-space, so no brush transform is applied).
/// </summary>
public sealed class BrushFragmentIndex
{
    // brush uid → per-local-face list of compiled fragment faces (empty list = fully clipped).
    private readonly IReadOnlyDictionary<int, IReadOnlyList<Face>[]> _byBrush;

    private BrushFragmentIndex(Geometry geometry, IReadOnlyDictionary<int, IReadOnlyList<Face>[]> byBrush)
    {
        Geometry = geometry;
        _byBrush = byBrush;
    }

    /// <summary>The compiled static geometry whose faces the fragments reference (world-space vertex pool + textures).</summary>
    public Geometry Geometry { get; }

    /// <summary>True when this brush was a CSG participant and has a fragment mapping (built, not dirty).</summary>
    public bool Covers(int brushUid) => _byBrush.ContainsKey(brushUid);

    /// <summary>
    /// The compiled fragments of one authored brush face, or null when the brush is not
    /// covered. An empty list means the face was fully clipped (draw nothing).
    /// </summary>
    public IReadOnlyList<Face>? Fragments(int brushUid, int localFace) =>
        _byBrush.TryGetValue(brushUid, out IReadOnlyList<Face>[]? faces)
            && localFace >= 0 && localFace < faces.Length
                ? faces[localFace]
                : null;

    /// <summary>
    /// Builds the index from a compiled geometry, its per-brush FaceId starts and the
    /// survival table (which also gives each brush's authored face count). Only faces
    /// whose FaceId falls in a CSG brush's range are indexed — portal / detail / membrane
    /// faces are ignored, so those brushes still draw their authored polygons.
    /// </summary>
    public static BrushFragmentIndex Build(
        Geometry geometry,
        IReadOnlyDictionary<int, int> brushFaceIdStart,
        IReadOnlyDictionary<int, bool[]> survivingBrushFaces)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(brushFaceIdStart);
        ArgumentNullException.ThrowIfNull(survivingBrushFaces);

        // Reverse the FaceId range map: global FaceId → (brush uid, local face index).
        var faceKey = new Dictionary<int, (int Uid, int Local)>();
        var buckets = new Dictionary<int, List<Face>[]>(brushFaceIdStart.Count);
        foreach (KeyValuePair<int, int> kv in brushFaceIdStart)
        {
            int uid = kv.Key;
            int start = kv.Value;
            int count = survivingBrushFaces.TryGetValue(uid, out bool[]? bits) ? bits.Length : 0;
            buckets[uid] = new List<Face>[count]; // every covered brush is present (even if all clipped)
            for (int local = 0; local < count; local++)
            {
                faceKey[start + local] = (uid, local);
            }
        }

        foreach (Face f in geometry.Faces)
        {
            if (faceKey.TryGetValue(f.FaceId, out (int Uid, int Local) k))
            {
                List<Face>[] arr = buckets[k.Uid];
                (arr[k.Local] ??= new List<Face>()).Add(f);
            }
        }

        var byBrush = new Dictionary<int, IReadOnlyList<Face>[]>(buckets.Count);
        foreach (KeyValuePair<int, List<Face>[]> kv in buckets)
        {
            List<Face>[] src = kv.Value;
            var dst = new IReadOnlyList<Face>[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = src[i] ?? (IReadOnlyList<Face>)Array.Empty<Face>();
            }

            byBrush[kv.Key] = dst;
        }

        return new BrushFragmentIndex(geometry, byBrush);
    }
}
