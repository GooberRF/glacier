using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ged.Core.Modules;

/// <summary>One module member in the module's local frame.</summary>
internal sealed class LocalMember
{
    public required int Index { get; init; }

    public required D3[] Vertices { get; init; }

    public required int[][] Faces { get; init; }

    public required string[] FaceTextures { get; init; }

    public bool Air { get; init; }

    public D3 Min { get; init; }

    public D3 Max { get; init; }

    public D3 Centre => (Min + Max) * 0.5;

    public D3 Size => Max - Min;

    public static LocalMember Create(int index, D3[] verts, int[][] faces, string[] textures, bool air)
    {
        D3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        foreach (D3 v in verts)
        {
            min = D3.Min(min, v);
            max = D3.Max(max, v);
        }

        if (verts.Length == 0)
        {
            min = max = D3.Zero;
        }

        return new LocalMember { Index = index, Vertices = verts, Faces = faces, FaceTextures = textures, Air = air, Min = min, Max = max };
    }
}

/// <summary>
/// Shape analysis of a module in its local frame: identity keys (exact, mirror-folded), 3-slice stretch zones and
/// tile families.
/// </summary>
internal static class ModuleShape
{
    /// <summary>The order-independent geometry hash (vertices quantised to 1/16 m per brush), optionally mirrored on X.</summary>
    public static string GeometryHash(IReadOnlyList<LocalMember> members, bool mirror)
    {
        var perBrush = new List<string>(members.Count);
        foreach (LocalMember m in members)
        {
            var pts = m.Vertices
                .Select(v => (X: Q16(mirror ? -v.X : v.X), Y: Q16(v.Y), Z: Q16(v.Z)))
                .Distinct()
                .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}"));
            perBrush.Add(Hex((m.Air ? "A:" : "S:") + string.Join(";", pts)));
        }

        perBrush.Sort(StringComparer.Ordinal);
        return Hex(string.Join("|", perBrush));
    }

    /// <summary>The mirror-folded geometry key (the smaller of the plain and X-mirrored hash).</summary>
    public static string FoldedGeometryHash(IReadOnlyList<LocalMember> members)
    {
        string a = GeometryHash(members, mirror: false), b = GeometryHash(members, mirror: true);
        return string.CompareOrdinal(a, b) <= 0 ? a : b;
    }

    public static string Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static long Q16(double v) => (long)Math.Round(v * 16.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The 3-slice of an axis: the largest interval containing no vertex of any member (tile-family members on this
    /// axis ignored) that some member spans; null when shorter than max(<paramref name="minLength"/>,
    /// <paramref name="minFraction"/> × extent).
    /// </summary>
    public static ModuleSlice? Slice(IReadOnlyList<LocalMember> members, int axis, ISet<int> ignore, double minLength, double minFraction)
    {
        var used = members.Where(m => !ignore.Contains(m.Index) && m.Vertices.Length > 0).ToList();
        if (used.Count == 0)
        {
            return null;
        }

        double extentMin = members.Min(m => m.Min[axis]), extentMax = members.Max(m => m.Max[axis]);
        double extent = extentMax - extentMin;
        var coords = used.SelectMany(m => m.Vertices.Select(v => Math.Round(v[axis] * 1024.0) / 1024.0)).Distinct().OrderBy(c => c).ToList();
        ModuleSlice? best = null;
        for (int i = 0; i + 1 < coords.Count; i++)
        {
            double a = coords[i], b = coords[i + 1];
            if (best is not null && b - a <= best.Length + 1e-9)
            {
                continue;
            }

            bool spanned = used.Any(m => m.Min[axis] < a + 1e-4 && m.Max[axis] > b - 1e-4);
            if (spanned)
            {
                best = new ModuleSlice(a, b);
            }
        }

        if (best is null || best.Length < Math.Max(minLength, minFraction * extent) - 1e-9)
        {
            return null;
        }

        return best;
    }

    /// <summary>True when every member is a prism along <paramref name="axis"/> between the same two end planes.</summary>
    public static bool SingleExtrusion(IReadOnlyList<LocalMember> members, int axis)
    {
        double? lo = null, hi = null;
        foreach (LocalMember m in members)
        {
            var c = m.Vertices.Select(v => Math.Round(v[axis] * 1024.0) / 1024.0).Distinct().ToList();
            if (c.Count != 2)
            {
                return false;
            }

            double a = c.Min(), b = c.Max();
            if (lo is null)
            {
                lo = a;
                hi = b;
            }
            else if (Math.Abs(lo.Value - a) > 1e-3 || Math.Abs(hi!.Value - b) > 1e-3)
            {
                return false;
            }
        }

        return lo is not null;
    }

    /// <summary>
    /// Tile families on <paramref name="axis"/>: runs of ≥ <paramref name="minUnits"/> congruent members whose centres
    /// line up along the axis at a constant pitch (± <paramref name="tol"/>); families sharing pitch, count and phase
    /// merge into multi-brush units.
    /// </summary>
    public static List<(int Axis, double Pitch, List<List<int>> Units, double FirstCentre)> TileFamilies(
        IReadOnlyList<LocalMember> members, int axis, int minUnits, double tol)
    {
        int o1 = (axis + 1) % 3, o2 = (axis + 2) % 3;
        var runs = new List<(double Pitch, List<LocalMember> Units)>();
        var groups = members
            .Where(m => m.Vertices.Length > 0)
            .GroupBy(m => ShapeKey(m) + "@" + Bucket(m.Centre[o1], tol) + "," + Bucket(m.Centre[o2], tol), StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var sorted = g.OrderBy(m => m.Centre[axis]).ThenBy(m => m.Index).ToList();
            if (sorted.Count < minUnits)
            {
                continue;
            }

            // Greedy maximal runs of constant pitch.
            int start = 0;
            while (start + minUnits - 1 < sorted.Count)
            {
                double pitch = sorted[start + 1].Centre[axis] - sorted[start].Centre[axis];
                if (pitch < 0.05)
                {
                    start++;
                    continue;
                }

                int end = start + 1;
                while (end + 1 < sorted.Count && Math.Abs(sorted[end + 1].Centre[axis] - sorted[end].Centre[axis] - pitch) <= tol)
                {
                    end++;
                }

                int count = end - start + 1;
                if (count >= minUnits)
                {
                    List<LocalMember> units = sorted.GetRange(start, count);
                    double mean = (units[^1].Centre[axis] - units[0].Centre[axis]) / (count - 1);
                    runs.Add((mean, units));
                    start = end + 1;
                }
                else
                {
                    start++;
                }
            }
        }

        // Merge families with the same pitch, count and phase into multi-brush units.
        var families = new List<(int Axis, double Pitch, List<List<int>> Units, double FirstCentre)>();
        foreach (var run in runs.OrderBy(r => r.Units[0].Centre[axis]).ThenBy(r => r.Units[0].Index))
        {
            int found = families.FindIndex(f => Math.Abs(f.Pitch - run.Pitch) <= tol && f.Units.Count == run.Units.Count
                && Math.Abs(f.FirstCentre - run.Units[0].Centre[axis]) <= tol);
            if (found >= 0)
            {
                for (int u = 0; u < run.Units.Count; u++)
                {
                    families[found].Units[u].Add(run.Units[u].Index);
                }
            }
            else
            {
                families.Add((axis, run.Pitch, run.Units.Select(m => new List<int> { m.Index }).ToList(), run.Units[0].Centre[axis]));
            }
        }

        foreach (var f in families)
        {
            foreach (List<int> unit in f.Units)
            {
                unit.Sort();
            }
        }

        return families;
    }

    /// <summary>A member's shape relative to its own bounds minimum (1/32 m), with its textures and air flag.</summary>
    public static string ShapeKey(LocalMember m)
    {
        var pts = m.Vertices
            .Select(v => v - m.Min)
            .Select(v => (X: (long)Math.Round(v.X * 32), Y: (long)Math.Round(v.Y * 32), Z: (long)Math.Round(v.Z * 32)))
            .Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}"));
        string tex = string.Join(",", m.FaceTextures.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        return (m.Air ? "A" : "S") + "|" + string.Join(";", pts) + "|" + tex.ToLowerInvariant();
    }

    private static long Bucket(double v, double tol) => (long)Math.Round(v / (2 * tol));
}
