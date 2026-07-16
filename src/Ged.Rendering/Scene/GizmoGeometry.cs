using System;
using System.Collections.Generic;
using System.Numerics;
using Ged.Core.Editing;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.Rendering.Scene;

/// <summary>
/// Builds the transform-manipulator widget as world-space <see cref="LineSegment"/>s
/// for the active tool: axis arrows + plane quads (Move), rings (Rotate), axis boxes
/// + a centre box (Scale). Hovered / dragged handles brighten and thicken (extra
/// screen-facing offset copies); while a drag is in progress the other handles dim.
/// Shared by the App overlay and the offscreen artifact renders so both stay in sync.
/// </summary>
public static class GizmoGeometry
{
    public static List<LineSegment> Build(
        GizmoPose pose,
        GizmoTool tool,
        GizmoHandle hover,
        GizmoHandle drag,
        bool dragging,
        uint colorX,
        uint colorY,
        uint colorZ,
        Vector3 camRight,
        Vector3 camUp)
    {
        var lines = new List<LineSegment>();
        Vector3 o = Vn(pose.Pivot);
        Vector3[] ax = { Vn(pose.AxisX), Vn(pose.AxisY), Vn(pose.AxisZ) };
        uint[] baseCol = { colorX, colorY, colorZ };
        float len = pose.Length;
        float hot = len * 0.02f;
        camRight = Safe(camRight);
        camUp = Safe(camUp);

        uint ColorFor(GizmoHandle h, uint b) =>
            dragging ? (h == drag ? Brighten(b) : Dim(b, 0.3f)) : (h == hover ? Brighten(b) : b);
        bool IsHot(GizmoHandle h) => dragging ? h == drag : h == hover;

        void Seg(Vector3 a, Vector3 b, uint col, bool thick)
        {
            lines.Add(new LineSegment(a, b, col));
            if (thick)
            {
                Vector3 r = camRight * hot;
                Vector3 u = camUp * hot;
                lines.Add(new LineSegment(a + r, b + r, col));
                lines.Add(new LineSegment(a - r, b - r, col));
                lines.Add(new LineSegment(a + u, b + u, col));
                lines.Add(new LineSegment(a - u, b - u, col));
            }
        }

        switch (tool)
        {
            case GizmoTool.Move:
                for (int a = 0; a < 3; a++)
                {
                    GizmoHandle h = MoveHandle(a);
                    uint col = ColorFor(h, baseCol[a]);
                    bool thick = IsHot(h);
                    Vector3 tip = o + (ax[a] * len);
                    Seg(o, tip, col, thick);
                    Vector3 d = Safe(ax[a]);
                    Vector3 back = tip - (d * (len * 0.16f));
                    float s = len * 0.07f;
                    lines.Add(new LineSegment(tip, back + (camRight * s), col));
                    lines.Add(new LineSegment(tip, back - (camRight * s), col));
                    lines.Add(new LineSegment(tip, back + (camUp * s), col));
                    lines.Add(new LineSegment(tip, back - (camUp * s), col));
                }

                for (int n = 0; n < 3; n++)
                {
                    GizmoHandle h = PlaneHandle(n);
                    int a1 = (n + 1) % 3;
                    int a2 = (n + 2) % 3;
                    uint col = ColorFor(h, baseCol[a1]);
                    bool thick = IsHot(h);
                    float inner = len * 0.18f;
                    float outer = len * 0.45f;
                    Vector3 p00 = o + (ax[a1] * inner) + (ax[a2] * inner);
                    Vector3 p10 = o + (ax[a1] * outer) + (ax[a2] * inner);
                    Vector3 p11 = o + (ax[a1] * outer) + (ax[a2] * outer);
                    Vector3 p01 = o + (ax[a1] * inner) + (ax[a2] * outer);
                    Seg(p00, p10, col, thick);
                    Seg(p10, p11, col, thick);
                    Seg(p11, p01, col, thick);
                    Seg(p01, p00, col, thick);
                }

                break;

            case GizmoTool.Rotate:
                for (int a = 0; a < 3; a++)
                {
                    GizmoHandle h = RotateHandle(a);
                    uint col = ColorFor(h, baseCol[a]);
                    bool thick = IsHot(h);
                    Vector3 u = ax[(a + 1) % 3];
                    Vector3 v = ax[(a + 2) % 3];
                    const int seg = 40;
                    Vector3 prev = default;
                    for (int i = 0; i <= seg; i++)
                    {
                        float ang = MathF.Tau * i / seg;
                        Vector3 pt = o + (u * (MathF.Cos(ang) * len)) + (v * (MathF.Sin(ang) * len));
                        if (i > 0)
                        {
                            Seg(prev, pt, col, thick);
                        }

                        prev = pt;
                    }
                }

                break;

            case GizmoTool.Scale:
                for (int a = 0; a < 3; a++)
                {
                    GizmoHandle h = ScaleHandle(a);
                    uint col = ColorFor(h, baseCol[a]);
                    bool thick = IsHot(h);
                    Vector3 tip = o + (ax[a] * len);
                    Seg(o, tip, col, thick);
                    AddBox(lines, tip, len * 0.07f, ax, col);
                }

                AddBox(lines, o, len * 0.09f, ax, ColorFor(GizmoHandle.ScaleUniform, Palette.Rgba(220, 220, 220)));
                break;
        }

        return lines;
    }

    private static void AddBox(List<LineSegment> lines, Vector3 c, float half, Vector3[] ax, uint col)
    {
        var v = new Vector3[8];
        int idx = 0;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    v[idx++] = c + (ax[0] * (x * half)) + (ax[1] * (y * half)) + (ax[2] * (z * half));
                }
            }
        }

        int[,] e = { { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 }, { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 } };
        for (int i = 0; i < 12; i++)
        {
            lines.Add(new LineSegment(v[e[i, 0]], v[e[i, 1]], col));
        }
    }

    private static GizmoHandle MoveHandle(int a) => a == 0 ? GizmoHandle.MoveX : (a == 1 ? GizmoHandle.MoveY : GizmoHandle.MoveZ);

    private static GizmoHandle RotateHandle(int a) => a == 0 ? GizmoHandle.RotateX : (a == 1 ? GizmoHandle.RotateY : GizmoHandle.RotateZ);

    private static GizmoHandle ScaleHandle(int a) => a == 0 ? GizmoHandle.ScaleX : (a == 1 ? GizmoHandle.ScaleY : GizmoHandle.ScaleZ);

    private static GizmoHandle PlaneHandle(int normalAxis) =>
        normalAxis == 0 ? GizmoHandle.PlaneYZ : (normalAxis == 1 ? GizmoHandle.PlaneZX : GizmoHandle.PlaneXY);

    private static Vector3 Vn(CoreVec3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 Safe(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;

    private static uint Brighten(uint c)
    {
        byte r = (byte)(c & 0xFF), g = (byte)((c >> 8) & 0xFF), b = (byte)((c >> 16) & 0xFF), a = (byte)((c >> 24) & 0xFF);
        return Palette.Rgba((byte)Math.Min(255, r + 90), (byte)Math.Min(255, g + 90), (byte)Math.Min(255, b + 90), a);
    }

    private static uint Dim(uint c, float f)
    {
        byte r = (byte)(c & 0xFF), g = (byte)((c >> 8) & 0xFF), b = (byte)((c >> 16) & 0xFF), a = (byte)((c >> 24) & 0xFF);
        return Palette.Rgba((byte)(r * f), (byte)(g * f), (byte)(b * f), a);
    }
}
