using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Model;
using Brush = Ged.Core.Model.Brush;

namespace Ged.App.Dialogs;

/// <summary>
/// Pure builder for the UV Unwrap editor's working set: flattens EVERY selected brush face into one
/// shared list of corner UVs, per-face corner rings (indices into that list), the brush/face/corner
/// back-references an edit commits through, and per-face identity (brush uid, face index in that
/// brush, texture name). Kept window-free so the multi-face loading is unit-testable independent of
/// Avalonia: N selected faces always yield N rings that partition the UV list, no matter how many
/// brushes they span, and every downstream draw / edit op works off that flat set + rings.
/// </summary>
internal static class UvWorkingSet
{
    /// <summary>Identity of one loaded face: the brush uid, the face index within that brush, and its texture.</summary>
    internal readonly record struct FaceRef(int BrushUid, int FaceIndex, string? Texture);

    /// <summary>The flattened working set: back-references, corner UVs, per-face rings and identities.</summary>
    internal sealed class Data
    {
        public List<(int Brush, int Face, int Corner)> Refs { get; } = new();

        public List<Uv> Uvs { get; } = new();

        public List<IReadOnlyList<int>> Rings { get; } = new();

        public List<FaceRef> Faces { get; } = new();

        /// <summary>The first loaded face's texture — the canvas backdrop (a mixed selection shows only this one).</summary>
        public string? FirstTexture { get; set; }
    }

    /// <summary>Flattens the brush editor's current face selection into a <see cref="Data"/> working set.</summary>
    internal static Data Build(BrushEditor be)
    {
        var data = new Data();
        foreach ((int uid, int fi) in be.SelectedFaces.OrderBy(f => f.Brush).ThenBy(f => f.Face))
        {
            Brush? b = be.FindBrush(uid);
            if (b is null || fi < 0 || fi >= b.Geometry.Faces.Count)
            {
                continue;
            }

            Face face = b.Geometry.Faces[fi];
            string? tex = face.Texture >= 0 && face.Texture < b.Geometry.Textures.Count ? b.Geometry.Textures[face.Texture] : null;
            data.FirstTexture ??= tex;

            var ring = new int[face.Vertices.Count];
            for (int c = 0; c < face.Vertices.Count; c++)
            {
                ring[c] = data.Uvs.Count;
                data.Refs.Add((uid, fi, c));
                data.Uvs.Add(face.Vertices[c].TextureCoords);
            }

            data.Rings.Add(ring);
            data.Faces.Add(new FaceRef(uid, fi, tex));
        }

        return data;
    }
}
