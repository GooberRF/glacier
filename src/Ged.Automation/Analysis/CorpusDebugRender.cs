using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ged.Automation.Rendering;
using Ged.Core.Analysis;
using Ged.Core.Editing.Organic;
using Ged.Core.IO.Tex;

namespace Ged.Automation.Analysis;

/// <summary>
/// The eyeball set's plan image (CORPUS-NORMS-SPEC §4-3), drawn on the CPU: floor samples as cells (brighter =
/// higher; invalid samples dark red), corridor samples tinted blue, floor-face outlines in grey, detected openings
/// as orange boxes across their travel axis with the width label, stairs as green arrows (bottom → top) with the
/// riser/tread label. Plan orientation as <c>glacier-cli render --plan</c>: +X right, +Z up.
/// </summary>
internal static class CorpusDebugRender
{
    private const int MaxSide = 1600;
    private const int Margin = 24;

    public static byte[] RenderPng(AnalysisDebugView view, string title)
    {
        ArgumentNullException.ThrowIfNull(view);
        var pts = view.Samples.Select(s => (s.X, s.Z)).Concat(view.FloorEdges.SelectMany(e => new[] { (e.A.X, e.A.Z), (e.B.X, e.B.Z) })).ToList();
        if (pts.Count == 0)
        {
            pts.Add((0, 0));
        }

        double minX = pts.Min(p => p.Item1), maxX = pts.Max(p => p.Item1), minZ = pts.Min(p => p.Item2), maxZ = pts.Max(p => p.Item2);
        double extent = Math.Max(1, Math.Max(maxX - minX, maxZ - minZ));
        double scale = Math.Min(8, (MaxSide - (2 * Margin)) / extent);
        int w = (int)Math.Ceiling(((maxX - minX) * scale) + (2 * Margin));
        int h = (int)Math.Ceiling(((maxZ - minZ) * scale) + (2 * Margin)) + 16;
        var pixels = new byte[w * h * 4];
        var canvas = new RgbaCanvas(w, h, pixels);
        canvas.FillRect(0, 0, w, h, new Rgba(18, 18, 22));
        Vector2 P(double x, double z) => new((float)(Margin + ((x - minX) * scale)), (float)(h - Margin - ((z - minZ) * scale)));

        double minY = view.Samples.Count > 0 ? view.Samples.Min(s => s.Y) : 0;
        double maxY = view.Samples.Count > 0 ? view.Samples.Max(s => s.Y) : 1;
        int cell = Math.Max(1, (int)Math.Round(view.Pitch * scale));
        foreach (DebugSample s in view.Samples.OrderBy(s => s.Y))
        {
            Vector2 c = P(s.X, s.Z);
            Rgba col;
            if (!s.Valid)
            {
                col = new Rgba(90, 30, 30);
            }
            else
            {
                int shade = 80 + (int)(120 * ((s.Y - minY) / Math.Max(1e-6, maxY - minY)));
                col = s.Corridor ? new Rgba((byte)(shade / 3), (byte)(shade / 2), (byte)Math.Min(255, shade + 60)) : new Rgba((byte)shade, (byte)shade, (byte)shade);
            }

            canvas.FillRect((int)(c.X - (cell / 2f)), (int)(c.Y - (cell / 2f)), cell, cell, col);
        }

        foreach ((Vec3d a, Vec3d b) in view.FloorEdges)
        {
            canvas.Line(P(a.X, a.Z), P(b.X, b.Z), new Rgba(150, 150, 160, 110));
        }

        var orange = new Rgba(255, 150, 30);
        foreach (DebugOpening o in view.Openings)
        {
            Vec3d across = new(-o.Axis.Z, 0, o.Axis.X);
            double hw = o.Width / 2, hd = 0.5;
            Vec3d[] corners =
            {
                o.Center.Add(across.Scale(hw)).Add(o.Axis.Scale(hd)), o.Center.Add(across.Scale(-hw)).Add(o.Axis.Scale(hd)),
                o.Center.Add(across.Scale(-hw)).Add(o.Axis.Scale(-hd)), o.Center.Add(across.Scale(hw)).Add(o.Axis.Scale(-hd)),
            };
            for (int i = 0; i < 4; i++)
            {
                canvas.Line(P(corners[i].X, corners[i].Z), P(corners[(i + 1) % 4].X, corners[(i + 1) % 4].Z), orange, 2f);
            }

            Vector2 c = P(o.Center.X, o.Center.Z);
            canvas.HaloText((int)c.X + 4, (int)c.Y - 4, string.Create(CultureInfo.InvariantCulture, $"{o.Width:0.0}"), orange);
        }

        var green = new Rgba(80, 230, 90);
        foreach (DebugStair s in view.Stairs)
        {
            Vector2 a = P(s.Bottom.X, s.Bottom.Z), b = P(s.Top.X, s.Top.Z);
            canvas.Line(a, b, green, 2f);
            Vector2 d = b - a;
            if (d.Length() > 1)
            {
                Vector2 u = Vector2.Normalize(d), n = new(-u.Y, u.X);
                canvas.Line(b, b - (u * 8) + (n * 5), green, 2f);
                canvas.Line(b, b - (u * 8) - (n * 5), green, 2f);
            }

            string label = string.Create(CultureInfo.InvariantCulture, $"{s.Riser:0.00}/{(s.Tread is double t ? t.ToString("0.00", CultureInfo.InvariantCulture) : "-")}");
            canvas.HaloText((int)b.X + 4, (int)b.Y + 2, label, green);
        }

        canvas.HaloText(4, 4, title, Rgba.White);
        canvas.HaloText(4, h - 12, string.Create(CultureInfo.InvariantCulture,
            $"pitch {view.Pitch:0.##} m  openings {view.Openings.Count}  stairs {view.Stairs.Count}  blue = corridor  orange = opening (width m)  green = stair (riser/tread)"), new Rgba(200, 200, 200));
        return PngWriter.Encode(w, h, pixels);
    }
}
