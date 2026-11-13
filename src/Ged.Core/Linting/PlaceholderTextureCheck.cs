using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>
/// The <see cref="LintCategory.PlaceholderTexture"/> check: brush faces the player can see that still carry the
/// placeholder texture every new brush starts with (<see cref="BrushCreateParams.DefaultTexture"/>, RED's stock
/// default) or no usable texture at all (an empty name or a texture slot that does not exist).
/// <para>
/// <b>What counts as seen.</b> Portal faces and portal brushes (an opening, never drawn), faces flagged Show Sky (they
/// draw the sky) and faces flagged invisible are skipped. A static brush face only counts when the level's compiled
/// geometry kept part of it: a compiled face with a placeholder texture (or none) lies on the brush face's plane and
/// overlaps it. So solid faces buried against other solid, air faces swallowed by overlapping air, and faces outside
/// the level are not reported. Movers are not part of the static build and are always drawn, so their faces all count.
/// Which faces a build hides cannot be known without one: a level with no compiled geometry is not checked (a note
/// says so when it has placeholder faces). Geometry older than the brushes can only miss brushes added since the build
/// (a face retextured since then no longer matches). A texture name that does not resolve in the mounted install is
/// reported by the missing-asset check instead.
/// </para>
/// One warning per brush, or per kit piece for brushes a kit function made (the <c>kit</c> / <c>kit.name</c> tags):
/// the brushes and face count, the first face's centre as the location, the first brush as the UID, and a fix hint
/// (the kit function's texture options for kit pieces). Capped by <see cref="LintOptions.MaxPlaceholderFindings"/>.
/// </summary>
internal static class PlaceholderTextureCheck
{
    /// <summary>
    /// Texture names that mean "not textured yet": the stock default new brushes get, and the pre-fix default name
    /// "Rck_Default01.tga" (never shipped in stock RF, so it renders as the missing-texture fallback).
    /// </summary>
    internal static readonly IReadOnlyList<string> PlaceholderNames = new[] { BrushCreateParams.DefaultTexture, "Rck_Default01.tga" };

    private const float PlaneTolerance = 0.02f;
    private const float NormalTolerance = 0.999f;
    private const float BoundsTolerance = 0.05f;

    private const ushort NeverDrawnFlags = (ushort)(FaceFlags.ShowSky | FaceFlags.IsInvisible);

    private enum Problem
    {
        None,
        Placeholder,
        Untextured,
    }

    /// <summary>One brush face with a placeholder (or no) texture, in world space.</summary>
    private sealed record Candidate(Brush Brush, bool Mover, Problem Problem, string Texture, Vec3 Normal, float Offset, Vec3 Min, Vec3 Max, Vec3 Centre);

    /// <summary>One compiled face with a placeholder (or no) texture.</summary>
    private readonly record struct Compiled(Problem Problem, Vec3 Normal, Vec3 Min, Vec3 Max);

    public static void Run(RflFile rfl, LintOptions options, List<LintFinding> findings, List<string> notes)
    {
        var brushes = new List<(Brush Brush, bool Mover)>();
        var seen = new HashSet<int>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is MoversSection ms)
            {
                foreach (Brush b in ms.Movers)
                {
                    seen.Add(b.Uid);
                    brushes.Add((b, true));
                }
            }
        }

        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is BrushesSection bs)
            {
                // Movers are stored twice (brush section + mover section): the mover copy is the one that is drawn.
                brushes.AddRange(bs.Brushes.Where(b => !seen.Contains(b.Uid)).Select(b => (b, false)));
            }
        }

        List<Candidate> candidates = Candidates(brushes);
        if (candidates.Count == 0)
        {
            return;
        }

        Geometry? compiled = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        if (compiled is not { Faces.Count: > 0 })
        {
            notes.Add($"The level is not built: {candidates.Select(c => c.Brush.Uid).Distinct().Count()} brush(es) have faces with the " +
                "placeholder texture or no texture, but which of them the player will see is only known after a build. " +
                "Build the level to check them.");
            return;
        }

        List<Compiled> problemFaces = CompiledProblemFaces(compiled);
        List<Candidate> visible = candidates.Where(c => c.Mover || problemFaces.Any(f => Overlaps(c, f))).ToList();

        if (visible.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<int, Dictionary<string, string>> tags = Tags(rfl);
        (string Fn, string Name)? KitOf(int uid) =>
            tags.TryGetValue(uid, out Dictionary<string, string>? t) && t.TryGetValue("kit", out string? fn) && fn.Length > 0
                ? (fn, t.TryGetValue("kit.name", out string? n) ? n : string.Empty)
                : null;

        // One finding per kit piece (kit function + name) or per untagged brush, in brush order.
        var groups = visible
            .GroupBy(c => KitOf(c.Brush.Uid) is { } k ? (k.Fn, k.Name, Uid: 0) : (Fn: string.Empty, Name: string.Empty, c.Brush.Uid))
            .ToList();
        int shown = Math.Min(groups.Count, Math.Max(0, options.MaxPlaceholderFindings));
        for (int i = 0; i < shown; i++)
        {
            List<Candidate> faces = groups[i].ToList();
            Candidate first = faces[0];
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.PlaceholderTexture,
                Message(faces, KitOf(first.Brush.Uid)), first.Brush.Uid, Location: first.Centre));
        }

        if (groups.Count > shown)
        {
            List<Candidate> rest = groups.Skip(shown).SelectMany(g => g).ToList();
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.PlaceholderTexture,
                $"{rest.Select(c => c.Brush.Uid).Distinct().Count()} more brush(es) show {rest.Count} face(s) with the placeholder " +
                "texture or no texture."));
        }
    }

    private static List<Candidate> Candidates(List<(Brush Brush, bool Mover)> brushes)
    {
        var list = new List<Candidate>();
        foreach ((Brush b, bool mover) in brushes)
        {
            if ((b.Flags & (uint)BrushFlags.Portal) != 0)
            {
                continue; // a portal brush marks an opening; it is never drawn
            }

            Geometry g = b.Geometry;
            foreach (Face f in g.Faces)
            {
                if (f.IsPortalFace || (f.Flags & NeverDrawnFlags) != 0 || f.Vertices.Count < 3)
                {
                    continue;
                }

                (Problem problem, string name) = Classify(g.Textures, f.Texture);
                if (problem == Problem.None)
                {
                    continue;
                }

                var pts = new List<Vec3>(f.Vertices.Count);
                foreach (FaceVertex fv in f.Vertices)
                {
                    if (fv.Index >= 0 && fv.Index < g.Vertices.Count)
                    {
                        pts.Add(BrushTransform.WorldVertex(b, g.Vertices[fv.Index]));
                    }
                }

                if (pts.Count < 3 || Normal(pts) is not Vec3 n)
                {
                    continue;
                }

                (Vec3 min, Vec3 max, Vec3 centre) = Bounds(pts);
                list.Add(new Candidate(b, mover, problem, name, n, n.Dot(pts[0]), min, max, centre));
            }
        }

        return list;
    }

    private static List<Compiled> CompiledProblemFaces(Geometry g)
    {
        var list = new List<Compiled>();
        foreach (Face f in g.Faces)
        {
            // The build writes a face whose brush face has no texture name with texture -1 (the portal sentinel) but
            // no portal index; a real portal face keeps its portal index or comes from a portal brush.
            Problem problem = f.Texture < 0 && f.PortalIndexPlus2 < 2 ? Problem.Untextured
                : f.IsPortalFace ? Problem.None
                : Classify(g.Textures, f.Texture).Problem;
            if (problem == Problem.None || (f.Flags & NeverDrawnFlags) != 0)
            {
                continue;
            }

            var pts = new List<Vec3>(f.Vertices.Count);
            foreach (FaceVertex fv in f.Vertices)
            {
                if (fv.Index >= 0 && fv.Index < g.Vertices.Count)
                {
                    pts.Add(g.Vertices[fv.Index]);
                }
            }

            if (pts.Count >= 3 && Normal(pts) is Vec3 n)
            {
                (Vec3 min, Vec3 max, _) = Bounds(pts);
                list.Add(new Compiled(problem, n, min, max));
            }
        }

        return list;
    }

    /// <summary>
    /// True when the compiled face lies on the candidate's plane (either facing: an air brush's visible side faces
    /// into the air it carves) and their bounds overlap — the build kept part of the brush face.
    /// </summary>
    private static bool Overlaps(Candidate c, Compiled f)
    {
        // A texture-less compiled face only stands for a texture-less brush face (never for a placeholder one, so a
        // portal-sentinel face on a doorway plane cannot make a placeholder wall there look visible).
        if (f.Problem == Problem.Untextured && c.Problem != Problem.Untextured)
        {
            return false;
        }

        if (MathF.Abs(c.Normal.Dot(f.Normal)) < NormalTolerance)
        {
            return false;
        }

        Vec3 mid = f.Min.Add(f.Max).Scale(0.5f);
        if (MathF.Abs(c.Normal.Dot(mid) - c.Offset) > PlaneTolerance)
        {
            return false;
        }

        return f.Min.X <= c.Max.X + BoundsTolerance && f.Max.X >= c.Min.X - BoundsTolerance
            && f.Min.Y <= c.Max.Y + BoundsTolerance && f.Max.Y >= c.Min.Y - BoundsTolerance
            && f.Min.Z <= c.Max.Z + BoundsTolerance && f.Max.Z >= c.Min.Z - BoundsTolerance;
    }

    private static (Problem Problem, string Name) Classify(List<string> textures, int index)
    {
        if (index < 0 || index >= textures.Count || string.IsNullOrWhiteSpace(textures[index]))
        {
            return (Problem.Untextured, string.Empty);
        }

        string name = textures[index];
        return PlaceholderNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
            ? (Problem.Placeholder, name)
            : (Problem.None, name);
    }

    private static string Message(List<Candidate> faces, (string Fn, string Name)? kit)
    {
        int placeholder = faces.Count(f => f.Problem == Problem.Placeholder);
        int untextured = faces.Count - placeholder;
        var what = new List<string>();
        if (placeholder > 0)
        {
            string names = string.Join(" / ", faces.Where(f => f.Problem == Problem.Placeholder).Select(f => f.Texture)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            what.Add($"{placeholder} visible face(s) with the placeholder texture {names} (the default every new brush starts with)");
        }

        if (untextured > 0)
        {
            what.Add($"{untextured} visible face(s) with no texture");
        }

        List<Brush> brushes = faces.Select(f => f.Brush).DistinctBy(b => b.Uid).ToList();
        string made = kit is { } k ? $"made by kit.{k.Fn}{(k.Name.Length > 0 ? $" \"{k.Name}\"" : string.Empty)}" : string.Empty;
        string who;
        if (brushes.Count == 1)
        {
            who = $"{((brushes[0].Flags & (uint)BrushFlags.Air) != 0 ? "Air brush" : "Brush")} {brushes[0].Uid}{(made.Length > 0 ? $" ({made})" : string.Empty)}";
        }
        else
        {
            string list = string.Join(", ", brushes.Take(5).Select(b => b.Uid.ToString(CultureInfo.InvariantCulture)));
            who = $"{brushes.Count} brushes ({list}{(brushes.Count > 5 ? ", ..." : string.Empty)}){(made.Length > 0 ? " " + made : string.Empty)}";
        }

        string verb = brushes.Count == 1 ? "has" : "have";
        string fix = kit is not null
            ? " Pass texture = ... or textures = {floor = ..., wall = ..., ceiling = ...} to that kit call, or set kit.defaults.texture / kit.defaults.textures first."
            : " Give them a real texture (the Texture panel, or brush:set_texture / brush:set_textures / face:set_texture in Lua; catalog.textures lists the names).";
        return $"{who} {verb} {string.Join(" and ", what)}.{fix}";
    }

    private static IReadOnlyDictionary<int, Dictionary<string, string>> Tags(RflFile rfl)
    {
        var map = new Dictionary<int, Dictionary<string, string>>();
        foreach (RflSection s in rfl.Sections)
        {
            if (s.Content is not GedObjectMetadataSection meta)
            {
                continue;
            }

            foreach (GedObjectMetadataRecord rec in meta.Entries)
            {
                GedObjectMetadataBlock? block = rec.Blocks.FirstOrDefault(b => b.MetadataType == (uint)GedMetadataType.Tags);
                if (block is not null && !map.ContainsKey(rec.Uid))
                {
                    map[rec.Uid] = ObjectTags.Decode(block.Payload);
                }
            }
        }

        return map;
    }

    /// <summary>The polygon's unit normal (Newell's method), or null when it is degenerate.</summary>
    private static Vec3? Normal(List<Vec3> pts)
    {
        float x = 0, y = 0, z = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            Vec3 a = pts[i], c = pts[(i + 1) % pts.Count];
            x += (a.Y - c.Y) * (a.Z + c.Z);
            y += (a.Z - c.Z) * (a.X + c.X);
            z += (a.X - c.X) * (a.Y + c.Y);
        }

        var n = new Vec3(x, y, z);
        float len = n.Length();
        return len < 1e-8f ? null : n.Scale(1f / len);
    }

    private static (Vec3 Min, Vec3 Max, Vec3 Centre) Bounds(List<Vec3> pts)
    {
        Vec3 min = pts[0], max = pts[0], sum = Vec3.Zero;
        foreach (Vec3 p in pts)
        {
            min = new Vec3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
            max = new Vec3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
            sum = sum.Add(p);
        }

        return (min, max, sum.Scale(1f / pts.Count));
    }
}
