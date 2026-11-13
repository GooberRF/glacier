using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Linting;

/// <summary>
/// Decals against long compiled faces. The game and the editor clip each decal to every compiled face its box touches
/// (RF.exe <c>0x004D6240</c>, RED.exe <c>0x00493570</c>) in two fixed 25-vertex stack buffers, copying the face's
/// whole vertex list in and letting each of the decal's six planes add a vertex; a face with more than
/// <see cref="MaxFaceVertexRecords"/> vertex records overruns them and the level crashes while it loads
/// (research/alpine-decal-clip-overflow.md). Compiled faces gain those records from t-junction sealing (every
/// neighbouring vertex on a face's edge joins its outline), so large floors, walls and ceilings bordered by many
/// pieces carry them.
/// <para>
/// A face counts as touched when its bounding box overlaps the decal box's and its plane passes through the box — a
/// superset of the faces the game's own early-outs (bounding box, face and distance tests) let through to the copy.
/// Portal faces are never drawn and are left out.
/// </para>
/// </summary>
public static class DecalClipCheck
{
    /// <summary>The most vertex records a face a decal touches may have: 19 + 6 clip-added vertices fill the 25-vertex buffer.</summary>
    public const int MaxFaceVertexRecords = 19;

    private const float Slack = 0.01f;

    /// <summary>The compiled faces of one geometry with their bounds, for repeated decal queries.</summary>
    public sealed class FaceIndex
    {
        private readonly Geometry _geo;
        private readonly List<(int Face, Vec3 Min, Vec3 Max)> _faces = new();

        public FaceIndex(Geometry geometry)
        {
            _geo = geometry ?? throw new ArgumentNullException(nameof(geometry));
            for (int i = 0; i < geometry.Faces.Count; i++)
            {
                Face f = geometry.Faces[i];
                if (f.IsPortalFace || f.Vertices.Count < 3)
                {
                    continue;
                }

                var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                foreach (FaceVertex fv in f.Vertices)
                {
                    if (fv.Index >= 0 && fv.Index < geometry.Vertices.Count)
                    {
                        Vec3 p = geometry.Vertices[fv.Index];
                        min = Vec3Math.Min(min, p);
                        max = Vec3Math.Max(max, p);
                    }
                }

                if (min.X <= max.X)
                {
                    _faces.Add((i, min, max));
                }
            }
        }

        /// <summary>
        /// The touched face with the most vertex records for a decal box (centre, orientation, full extents along right /
        /// up / forward): its index and record count, or (-1, 0) when the box touches no face.
        /// </summary>
        public (int Face, int Records) WorstTouched(Vec3 centre, Mat3 rotation, Vec3 extents)
        {
            Vec3 r = Unit(rotation.Right), u = Unit(rotation.Up), f = Unit(rotation.Forward);
            float hx = Math.Abs(extents.X) / 2, hy = Math.Abs(extents.Y) / 2, hz = Math.Abs(extents.Z) / 2;

            // The box's world bounds.
            float ex = (Math.Abs(r.X) * hx) + (Math.Abs(u.X) * hy) + (Math.Abs(f.X) * hz) + Slack;
            float ey = (Math.Abs(r.Y) * hx) + (Math.Abs(u.Y) * hy) + (Math.Abs(f.Y) * hz) + Slack;
            float ez = (Math.Abs(r.Z) * hx) + (Math.Abs(u.Z) * hy) + (Math.Abs(f.Z) * hz) + Slack;
            int best = -1, records = 0;
            foreach ((int fi, Vec3 mn, Vec3 mx) in _faces)
            {
                if (mx.X < centre.X - ex || mn.X > centre.X + ex || mx.Y < centre.Y - ey || mn.Y > centre.Y + ey ||
                    mx.Z < centre.Z - ez || mn.Z > centre.Z + ez)
                {
                    continue;
                }

                Face face = _geo.Faces[fi];
                Vec3 n = face.Plane.Normal;
                float reach = (Math.Abs(n.Dot(r)) * hx) + (Math.Abs(n.Dot(u)) * hy) + (Math.Abs(n.Dot(f)) * hz) + Slack;

                // The plane through the face's first vertex (robust to either sign convention of the stored distance).
                Vec3 p0 = _geo.Vertices[face.Vertices[0].Index];
                if (Math.Abs(n.Dot(centre.Sub(p0))) > reach)
                {
                    continue;
                }

                if (face.Vertices.Count > records)
                {
                    best = fi;
                    records = face.Vertices.Count;
                }
            }

            return (best, records);
        }

        private static Vec3 Unit(Vec3 v)
        {
            float l = v.Length();
            return l > 1e-6f ? v.Scale(1f / l) : v;
        }
    }

    /// <summary>The linter pass: one Error per decal whose box touches a compiled face with too many vertex records.</summary>
    internal static void Run(RflFile rfl, List<LintFinding> findings, List<string> notes)
    {
        List<Decal> decals = rfl.Sections.Select(s => s.Content).OfType<DecalsSection>().SelectMany(s => s.Decals).ToList();
        if (decals.Count == 0)
        {
            return;
        }

        Geometry? compiled = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
        if (compiled is not { Faces.Count: > 0 })
        {
            notes.Add($"The level is not built: its {decals.Count} decal(s) were not checked against the compiled faces they touch " +
                $"(a face with more than {MaxFaceVertexRecords} vertex records under a decal crashes the game and the editor). " +
                "Build the level to check them.");
            return;
        }

        var index = new FaceIndex(compiled);
        foreach (Decal d in decals)
        {
            (int face, int records) = index.WorstTouched(d.Header.Position, d.Header.Rotation, d.Extents);
            if (records <= MaxFaceVertexRecords)
            {
                continue;
            }

            string name = d.Header.ScriptName is { Length: > 0 } sn ? $"'{sn}'" : $"with texture {d.Texture}";
            findings.Add(new LintFinding(LintSeverity.Error, LintCategory.DecalOnComplexFace,
                $"Decal {name} touches compiled face {face.ToString(CultureInfo.InvariantCulture)} with {records} vertex records " +
                $"(more than {MaxFaceVertexRecords}). The game and the editor crash when the level loads: they clip a decal in " +
                "fixed 25-vertex buffers. Move or shrink the decal so its box clears that face, or delete it.",
                d.Header.Uid, Location: d.Header.Position));
        }
    }
}
