using System.Collections.Generic;
using System.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// Data-pinned proof that GED's brush local→world convention matches RF's:
/// transform every corpus brush's local vertices with <see cref="Mat3Math"/> and
/// assert a large fraction land within 1&#160;mm of a vertex in that level's
/// ORIGINAL compiled static geometry. CSG clipping means not every brush vertex
/// survives, but the great majority do. This test is immune to "self-consistent
/// but wrong" transform math: the cyclic-permuted convention fails it even for
/// identity-rotation brushes (which it maps x,y,z → y,z,x).
/// </summary>
public sealed class BrushConventionTests
{
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Brush_World_Vertices_Match_Compiled_Geometry(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        RflFile file = RflFile.Load(Path.Combine(Corpus.Directory!, fileName));
        file.ParseAllKnownSections();

        Geometry? staticGeo = null;
        BrushesSection? brushes = null;
        foreach (RflSection s in file.Sections)
        {
            if (s.Content is GeometrySection g)
            {
                staticGeo ??= g.Geometry;
            }
            else if (s.Content is BrushesSection b)
            {
                brushes ??= b;
            }
        }

        if (staticGeo is null || brushes is null || staticGeo.Vertices.Count == 0 || brushes.Brushes.Count == 0)
        {
            return; // nothing to compare (uncompiled or brush-less level)
        }

        var grid = new VertexGrid(staticGeo.Vertices);
        int total = 0, match = 0, rotTotal = 0, rotMatch = 0;
        foreach (Brush b in brushes.Brushes)
        {
            bool rotated = !b.Rotation.ApproxEquals(Mat3.Identity, 1e-4f);
            foreach (Vec3 local in b.Geometry.Vertices)
            {
                Vec3 world = b.Position.Add(b.Rotation.Transform(local));
                bool near = grid.HasNear(world, 0.001f);
                total++;
                if (near)
                {
                    match++;
                }

                if (rotated)
                {
                    rotTotal++;
                    if (near)
                    {
                        rotMatch++;
                    }
                }
            }
        }

        // A correct convention lands the overwhelming majority of brush vertices on a
        // compiled vertex; the permuted convention scores near zero here.
        Assert.True(match >= total * 0.6,
            $"{fileName}: only {match}/{total} brush vertices matched compiled geometry (convention likely wrong)");

        // When the level has rotated brushes, they must match well too (the real gate).
        if (rotTotal > 200)
        {
            Assert.True(rotMatch >= rotTotal * 0.5,
                $"{fileName}: only {rotMatch}/{rotTotal} ROTATED brush vertices matched");
        }
    }

    /// <summary>A coarse spatial hash of vertices for fast near-point queries.</summary>
    private sealed class VertexGrid
    {
        private const float Cell = 0.05f;
        private readonly Dictionary<(int, int, int), List<Vec3>> _cells = new();

        public VertexGrid(IReadOnlyList<Vec3> points)
        {
            foreach (Vec3 p in points)
            {
                (int, int, int) c = C(p);
                if (!_cells.TryGetValue(c, out List<Vec3>? list))
                {
                    _cells[c] = list = new List<Vec3>();
                }

                list.Add(p);
            }
        }

        public bool HasNear(Vec3 p, float tol)
        {
            (int cx, int cy, int cz) = C(p);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out List<Vec3>? list))
                        {
                            foreach (Vec3 q in list)
                            {
                                if (p.Distance(q) < tol)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static (int, int, int) C(Vec3 p) =>
            ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
    }
}
