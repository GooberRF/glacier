using System.Numerics;
using Ged.Core.Model;
using Silk.NET.Maths;

namespace Ged.Rendering;

/// <summary>Which kind of projection a <see cref="Camera"/> uses.</summary>
public enum CameraProjection
{
    Perspective,
    Orthographic,
}

/// <summary>The fixed orthographic viewing directions (RED's ortho panes).</summary>
public enum OrthoView
{
    Top,
    Bottom,
    Front,
    Back,
    Left,
    Right,
}

/// <summary>
/// A viewport camera. Perspective mode is a yaw/pitch free-look fly camera;
/// orthographic mode looks down a fixed world axis with a pan center and zoom.
/// All view/projection math uses Silk.NET.Maths and follows the row-vector,
/// [0,1]-depth (Direct3D) convention, so <see cref="ViewProjection"/> can be
/// transposed and uploaded to an HLSL constant buffer directly.
/// </summary>
public sealed class Camera
{
    private static readonly Vector3D<float> WorldUp = new(0f, 1f, 0f);

    public CameraProjection Projection { get; set; } = CameraProjection.Perspective;

    /// <summary>Eye position (perspective) or pan center (orthographic).</summary>
    public Vector3 Position { get; set; } = new(0f, 2f, -5f);

    /// <summary>Heading, radians. 0 looks toward +Z.</summary>
    public float Yaw { get; set; }

    /// <summary>Pitch, radians, clamped to just under +/- 90 degrees.</summary>
    public float Pitch { get; set; }

    public float FieldOfView { get; set; } = 70f * (MathF.PI / 180f);

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 6000f;

    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Half-height of the orthographic view volume in world units.</summary>
    public float OrthoZoom { get; set; } = 20f;

    /// <summary>Camera bank (roll) about the forward axis, radians (perspective only).</summary>
    public float Roll { get; set; }

    public OrthoView Ortho { get; set; } = OrthoView.Top;

    /// <summary>Unit forward (view) direction in world space.</summary>
    public Vector3 Forward => ForwardVec().ToNumerics();

    /// <summary>Unit right direction in the camera's horizontal plane.</summary>
    public Vector3 Right => RightVec().ToNumerics();

    /// <summary>Unit up direction of the camera plane (used to build billboards).</summary>
    public Vector3 Up => Vector3D.Cross(ForwardVec(), RightVec()).ToNumerics();

    public Matrix4X4<float> View => BuildView();

    public Matrix4X4<float> ProjectionMatrix => BuildProjection();

    /// <summary>World-to-clip transform (row-vector: clip = world * ViewProjection).</summary>
    public Matrix4X4<float> ViewProjection => Matrix4X4.Multiply(View, BuildProjection());

    /// <summary>Sets pitch/yaw so the camera looks from <paramref name="from"/> toward <paramref name="to"/>.</summary>
    public void LookAt(Vector3 from, Vector3 to)
    {
        Position = from;
        Vector3 d = to - from;
        if (d.LengthSquared() < 1e-6f)
        {
            return;
        }

        d = Vector3.Normalize(d);
        Pitch = MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
        Yaw = MathF.Atan2(d.X, d.Z);
    }

    /// <summary>
    /// Moves along the camera basis (right, up, forward) by the given amounts. The up
    /// component is world +Y for a perspective fly camera (stock RED lift), but the
    /// view-plane up for an orthographic pane — world Y is the invisible depth axis of
    /// the Top/Bottom views, so lifting there must scroll the plane instead.
    /// </summary>
    public void MoveLocal(float right, float up, float forward)
    {
        Vector3 p = Position;
        p += Right * right;
        p += Projection == CameraProjection.Orthographic ? Up * up : new Vector3(0f, up, 0f);
        p += Forward * forward;
        Position = p;
    }

    /// <summary>Pans in the view plane: right along <see cref="Right"/>, up along <see cref="Up"/>.</summary>
    public void Pan(float right, float up) => Position += (Right * right) + (Up * up);

    /// <summary>
    /// Scales the orthographic zoom (view-volume half-height); a factor below 1 zooms
    /// in. Clamped to the same range the wheel uses.
    /// </summary>
    public void ZoomOrtho(float factor) =>
        OrthoZoom = Math.Clamp(OrthoZoom * factor, 0.5f, 20000f);

    /// <summary>
    /// Cursor-centered orthographic zoom: scales <see cref="OrthoZoom"/> and pans so the
    /// world point under the given pixel stays under it. No-op for perspective cameras.
    /// </summary>
    public void ZoomOrthoAt(float px, float py, float width, float height, float factor)
    {
        if (Projection != CameraProjection.Orthographic)
        {
            return;
        }

        (Vector3 anchor, _) = PixelRay(px, py, width, height);
        float before = OrthoZoom;
        ZoomOrtho(factor);
        float applied = OrthoZoom / before; // the factor after clamping
        Position = anchor + ((Position - anchor) * applied);
    }

    /// <summary>Applies mouse-look deltas (radians), clamping pitch.</summary>
    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        const float limit = (MathF.PI / 2f) - 0.01f;
        Pitch = Math.Clamp(Pitch + deltaPitch, -limit, limit);
    }

    /// <summary>Snaps a perspective camera to look down the nearest world axis (stock C).</summary>
    public void OrientToNearestAxis()
    {
        float half = MathF.PI / 2f;
        Yaw = MathF.Round(Yaw / half) * half;
        Pitch = MathF.Round(Pitch / half) * half;
        float limit = half - 0.01f;
        Pitch = Math.Clamp(Pitch, -limit, limit);
        Roll = 0f;
    }

    /// <summary>Frames the given world bounds: positions a perspective camera to see all of it.</summary>
    public void Frame(Aabb bounds)
    {
        var min = new Vector3(bounds.P1.X, bounds.P1.Y, bounds.P1.Z);
        var max = new Vector3(bounds.P2.X, bounds.P2.Y, bounds.P2.Z);
        Vector3 center = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 1f);

        if (Projection == CameraProjection.Orthographic)
        {
            Position = center;
            OrthoZoom = radius * 1.1f;
            return;
        }

        float dist = radius / MathF.Tan(FieldOfView * 0.5f);
        LookAt(center + new Vector3(dist * 0.6f, dist * 0.5f, dist * 0.6f), center);
    }

    /// <summary>
    /// The view-projection as a System.Numerics matrix (row-major, row-vector).
    /// Uploaded to the cbuffer verbatim: HLSL reads a cbuffer float4x4
    /// column-major, so the row-major bytes are interpreted as the transpose,
    /// which is exactly what <c>mul(matrix, columnVector)</c> requires. Do NOT
    /// pre-transpose here.
    /// </summary>
    public Matrix4x4 ViewProjectionMatrix => ViewProjection.ToNumerics();

    /// <summary>
    /// A world-space ray through a viewport pixel (top-left origin). Perspective rays
    /// start at the eye; orthographic rays start on the view plane and run along the
    /// forward axis. This is the unproject the transform gizmo and marquee need.
    /// </summary>
    public (Vector3 Origin, Vector3 Direction) PixelRay(float px, float py, float width, float height)
    {
        float w = MathF.Max(width, 1f);
        float h = MathF.Max(height, 1f);
        float nx = ((px / w) * 2f) - 1f;
        float ny = ((py / h) * 2f) - 1f;

        if (Projection == CameraProjection.Orthographic)
        {
            float halfH = OrthoZoom;
            float halfW = halfH * MathF.Max(AspectRatio, 0.01f);
            Vector3 origin = Position + (Right * (nx * halfW)) - (Up * (ny * halfH));
            return (origin, Vector3.Normalize(Forward));
        }

        float tanY = MathF.Tan(FieldOfView * 0.5f);
        float tanX = tanY * MathF.Max(AspectRatio, 0.01f);
        Vector3 dir = Vector3.Normalize(Forward + (Right * (nx * tanX)) - (Up * (ny * tanY)));
        return (Position, dir);
    }

    /// <summary>
    /// Projects a world point to a viewport pixel (top-left origin). Returns false when
    /// the point is behind the camera (perspective). Used by marquee box-select.
    /// </summary>
    public bool WorldToScreen(Vector3 world, float width, float height, out Vector2 screen)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewProjectionMatrix);
        if (clip.W <= 1e-5f)
        {
            screen = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        screen = new Vector2(
            ((ndcX * 0.5f) + 0.5f) * MathF.Max(width, 1f),
            (1f - ((ndcY * 0.5f) + 0.5f)) * MathF.Max(height, 1f));
        return true;
    }

    /// <summary>
    /// World units per screen pixel at <paramref name="focus"/> — the factor that makes
    /// a world size project to a constant pixel size (screen-constant gizmo sizing).
    /// </summary>
    public float WorldPerPixel(Vector3 focus, float height)
    {
        float h = MathF.Max(height, 1f);
        return Projection == CameraProjection.Orthographic
            ? OrthoZoom * 2f / h
            : 2f * MathF.Max(2f, Vector3.Distance(Position, focus)) * MathF.Tan(FieldOfView * 0.5f) / h;
    }

    private Vector3D<float> ForwardVec()
    {
        if (Projection == CameraProjection.Orthographic)
        {
            return OrthoForward();
        }

        float cp = MathF.Cos(Pitch);
        return Vector3D.Normalize(new Vector3D<float>(cp * MathF.Sin(Yaw), MathF.Sin(Pitch), cp * MathF.Cos(Yaw)));
    }

    private Vector3D<float> RightVec()
    {
        Vector3D<float> f = ForwardVec();
        Vector3D<float> up = MathF.Abs(Vector3D.Dot(f, WorldUp)) > 0.99f ? new Vector3D<float>(0f, 0f, 1f) : WorldUp;
        return Vector3D.Normalize(Vector3D.Cross(up, f));
    }

    private Vector3D<float> OrthoForward() => Ortho switch
    {
        OrthoView.Top => new Vector3D<float>(0f, -1f, 0f),
        OrthoView.Bottom => new Vector3D<float>(0f, 1f, 0f),
        OrthoView.Front => new Vector3D<float>(0f, 0f, 1f),
        OrthoView.Back => new Vector3D<float>(0f, 0f, -1f),
        OrthoView.Left => new Vector3D<float>(1f, 0f, 0f),
        _ => new Vector3D<float>(-1f, 0f, 0f),
    };

    /// <summary>
    /// Left-handed, row-vector view matrix (world * View = view space). RF is a
    /// left-handed, +Z-forward world; a right-handed look-at would mirror X and
    /// flip the level. Built explicitly so the orientation matches the game.
    /// </summary>
    private Matrix4X4<float> BuildView()
    {
        Vector3D<float> f = ForwardVec();
        Vector3D<float> pos = Position.ToSilk();
        Vector3D<float> up = Projection == CameraProjection.Orthographic && Ortho is OrthoView.Top or OrthoView.Bottom
            ? new Vector3D<float>(0f, 0f, 1f)
            : WorldUp;
        Vector3D<float> eye = Projection == CameraProjection.Orthographic ? pos - (f * 3000f) : pos;

        Vector3D<float> r = Vector3D.Normalize(Vector3D.Cross(up, f));
        Vector3D<float> u = Vector3D.Cross(f, r);
        if (Projection == CameraProjection.Perspective && Roll != 0f)
        {
            float cr = MathF.Cos(Roll), sr = MathF.Sin(Roll);
            Vector3D<float> rr = (r * cr) + (u * sr);
            Vector3D<float> uu = (u * cr) - (r * sr);
            r = rr;
            u = uu;
        }

        float tx = -Vector3D.Dot(r, eye);
        float ty = -Vector3D.Dot(u, eye);
        float tz = -Vector3D.Dot(f, eye);

        return new Matrix4X4<float>(
            new Vector4D<float>(r.X, u.X, f.X, 0f),
            new Vector4D<float>(r.Y, u.Y, f.Y, 0f),
            new Vector4D<float>(r.Z, u.Z, f.Z, 0f),
            new Vector4D<float>(tx, ty, tz, 1f));
    }

    /// <summary>Left-handed, [0,1]-depth (Direct3D) projection, row-vector convention.</summary>
    private Matrix4X4<float> BuildProjection()
    {
        if (Projection == CameraProjection.Orthographic)
        {
            float height = MathF.Max(OrthoZoom, 0.01f) * 2f;
            float width = height * MathF.Max(AspectRatio, 0.01f);
            const float zn = 0.1f;
            const float zf = 12000f;
            float range = 1f / (zf - zn);
            return new Matrix4X4<float>(
                new Vector4D<float>(2f / width, 0f, 0f, 0f),
                new Vector4D<float>(0f, 2f / height, 0f, 0f),
                new Vector4D<float>(0f, 0f, range, 0f),
                new Vector4D<float>(0f, 0f, -zn * range, 1f));
        }

        float yScale = 1f / MathF.Tan(FieldOfView * 0.5f);
        float xScale = yScale / MathF.Max(AspectRatio, 0.01f);
        float q = FarPlane / (FarPlane - NearPlane);
        return new Matrix4X4<float>(
            new Vector4D<float>(xScale, 0f, 0f, 0f),
            new Vector4D<float>(0f, yScale, 0f, 0f),
            new Vector4D<float>(0f, 0f, q, 1f),
            new Vector4D<float>(0f, 0f, -NearPlane * q, 0f));
    }
}
