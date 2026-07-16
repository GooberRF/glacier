using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Pre-build brush validation, mirroring RED's red-status warnings: steam-jet
/// count, breakable-glass rules (4-sided detail face with a gls/sgl texture),
/// portal brushes must be air (or face-solid), and non-manifold brushes. Emits
/// warnings into the <see cref="BuildReport"/>; never blocks the build.
/// </summary>
public static class BrushValidator
{
    private const int MaxSteamJets = 3;

    public static void Validate(IReadOnlyList<Brush> brushes, BuildReport report)
    {
        int steamJets = 0;
        foreach (Brush b in brushes)
        {
            var flags = (BrushFlags)b.Flags;

            if ((flags & BrushFlags.EmitsSteam) != 0)
            {
                steamJets++;
            }

            if ((flags & BrushFlags.Portal) != 0 && (flags & BrushFlags.Air) == 0)
            {
                report.Add(BuildSeverity.Warning,
                    $"Brush {b.Uid}: portal brush should be air (or a face-solid).", BrushCenter(b), b.Uid);
            }

            CheckBreakableGlass(b, flags, report);

            if (!IsManifold(b))
            {
                report.Add(BuildSeverity.Warning,
                    $"Brush {b.Uid}: non-manifold (open edges) — geometry may leak.", BrushCenter(b), b.Uid);
            }
        }

        if (steamJets > MaxSteamJets)
        {
            report.Add(BuildSeverity.Warning,
                $"{steamJets} steam-emitter brushes (>{MaxSteamJets}); RF may not animate all jets.");
        }
    }

    private static void CheckBreakableGlass(Brush b, BrushFlags flags, BuildReport report)
    {
        // Breakable glass = a finite-life detail brush textured gls*/sgl*.
        if ((flags & BrushFlags.Detail) == 0 || b.Life < 0)
        {
            return;
        }

        bool glass = false;
        foreach (string t in b.Geometry.Textures)
        {
            string n = t.ToLowerInvariant();
            if (n.StartsWith("gls") || n.StartsWith("sgl"))
            {
                glass = true;
                break;
            }
        }

        if (!glass)
        {
            return;
        }

        // The glass pane's main faces should be simple quads.
        foreach (Face f in b.Geometry.Faces)
        {
            if (f.Vertices.Count > 4)
            {
                report.Add(BuildSeverity.Warning,
                    $"Brush {b.Uid}: breakable glass should use 4-sided faces.", BrushCenter(b), b.Uid);
                return;
            }
        }
    }

    private static bool IsManifold(Brush b)
    {
        var count = new Dictionary<(int, int), int>();
        foreach (Face f in b.Geometry.Faces)
        {
            int n = f.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                int u = f.Vertices[i].Index;
                int v = f.Vertices[(i + 1) % n].Index;
                if (u == v)
                {
                    continue;
                }

                var key = u < v ? (u, v) : (v, u);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        }

        foreach (int c in count.Values)
        {
            if (c != 2)
            {
                return false;
            }
        }

        return count.Count > 0;
    }

    private static Vec3 BrushCenter(Brush b)
    {
        Aabb bb = Ged.Core.Editing.GeometryUtil.LocalBounds(b.Geometry);
        Vec3 localCenter = new((bb.P1.X + bb.P2.X) * 0.5f, (bb.P1.Y + bb.P2.Y) * 0.5f, (bb.P1.Z + bb.P2.Z) * 0.5f);
        return b.Position.Add(b.Rotation.Transform(localCenter));
    }
}
