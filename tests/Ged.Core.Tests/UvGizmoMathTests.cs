using System.Collections.Generic;
using Ged.Core.Editing;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// The UV Unwrap 2D gizmo's transform math (held-M/R/S manipulator): grid-snapped move,
/// rotate-step snap, and per-axis / uniform scale. Pure over UV-space points, so the snap
/// rules are pinned here independently of the window that drives them.
/// </summary>
public sealed class UvGizmoMathTests
{
    // ---- Move (free + axis-constrained + grid-snapped) ------------------------

    [Fact]
    public void MoveDelta_Free_Returns_Raw_Pointer_Delta()
    {
        Uv c = new(0.3f, 0.4f);
        (float du, float dv) = UvGizmoMath.MoveDelta(
            c, start: new(0.5f, 0.5f), current: new(0.72f, 0.31f),
            UvGizmoMath.Axis.Both, gridSnap: false, step: 0.0625f);

        Assert.Equal(0.22f, du, 4);
        Assert.Equal(-0.19f, dv, 4);
    }

    [Fact]
    public void MoveDelta_AxisU_Zeroes_The_V_Component()
    {
        (float du, float dv) = UvGizmoMath.MoveDelta(
            new(0f, 0f), new(0f, 0f), new(0.4f, 0.9f),
            UvGizmoMath.Axis.U, gridSnap: false, step: 0.0625f);

        Assert.Equal(0.4f, du, 4);
        Assert.Equal(0f, dv, 4);
    }

    [Fact]
    public void MoveDelta_AxisV_Zeroes_The_U_Component()
    {
        (float du, float dv) = UvGizmoMath.MoveDelta(
            new(0f, 0f), new(0f, 0f), new(0.4f, 0.9f),
            UvGizmoMath.Axis.V, gridSnap: false, step: 0.0625f);

        Assert.Equal(0f, du, 4);
        Assert.Equal(0.9f, dv, 4);
    }

    [Fact]
    public void MoveDelta_GridSnap_Lands_The_Centroid_On_The_Grid()
    {
        // Centroid at 0.10; a drag of +0.20 would put it at 0.30. With a 0.0625 (1/16) grid
        // the nearest multiple to 0.30 is 0.3125, so the delta is chosen to land there.
        Uv c = new(0.10f, 0.10f);
        (float du, float dv) = UvGizmoMath.MoveDelta(
            c, start: new(0f, 0f), current: new(0.20f, 0.20f),
            UvGizmoMath.Axis.Both, gridSnap: true, step: 0.0625f);

        Assert.Equal(0.3125f, c.U + du, 4);
        Assert.Equal(0.3125f, c.V + dv, 4);
    }

    [Fact]
    public void MoveDelta_GridSnap_Snaps_The_Pivot_Not_Each_Vertex()
    {
        // Two points offset from a centroid at (0.10, 0.10). A grid-snapped free drag keeps their
        // relative offset (island shape) intact while landing the *centroid* on the grid lattice.
        var uvs = new List<Uv> { new(0.05f, 0.08f), new(0.15f, 0.12f) };
        var sel = new[] { 0, 1 };
        Uv c = UnwrapOps.Centroid(uvs, sel); // (0.10, 0.10)
        (float du, float dv) = UvGizmoMath.MoveDelta(
            c, new(0f, 0f), new(0.20f, 0.20f), UvGizmoMath.Axis.Both, gridSnap: true, step: 0.0625f);
        UnwrapOps.Move(uvs, sel, du, dv);

        Uv moved = UnwrapOps.Centroid(uvs, sel);
        Assert.Equal(0.3125f, moved.U, 4); // pivot on the grid (nearest 1/16 to 0.30)
        Assert.Equal(0.3125f, moved.V, 4);
        // Island shape preserved — each vertex shifted by the same delta, not snapped one by one.
        Assert.Equal(0.10f, uvs[1].U - uvs[0].U, 4);
        Assert.Equal(0.04f, uvs[1].V - uvs[0].V, 4);
    }

    [Fact]
    public void MoveDelta_AxisU_GridSnap_Leaves_V_Exactly_Untouched()
    {
        // The constrained U handle snaps only U; V keeps its raw (zero) delta even under snap.
        Uv c = new(0.10f, 0.13f);
        (float du, float dv) = UvGizmoMath.MoveDelta(
            c, new(0f, 0f), new(0.20f, 0f), UvGizmoMath.Axis.U, gridSnap: true, step: 0.0625f);
        Assert.Equal(0.3125f, c.U + du, 4);
        Assert.Equal(0f, dv, 4);
    }

    // ---- Rotate (angle measurement + step snap) -------------------------------

    [Fact]
    public void AngleDegrees_Quarter_Turn_Is_Ninety()
    {
        // From the +U ray to the +V ray about the origin is +90° in UnwrapOps' convention.
        float deg = UvGizmoMath.AngleDegrees(new(0f, 0f), from: new(1f, 0f), to: new(0f, 1f));
        Assert.Equal(90f, deg, 3);
    }

    [Fact]
    public void AngleDegrees_Matches_UnwrapOps_Rotate_Direction()
    {
        // Two points symmetric about (0.5, 0.5) so the selection centroid IS the gizmo centroid;
        // rotating by AngleDegrees must carry the grabbed point onto the pointer's ray.
        var uvs = new List<Uv> { new(0.8f, 0.5f), new(0.2f, 0.5f) };
        var sel = new[] { 0, 1 };
        Uv c = UnwrapOps.Centroid(uvs, sel); // (0.5, 0.5)
        float deg = UvGizmoMath.AngleDegrees(c, from: new(0.8f, 0.5f), to: new(0.5f, 0.8f));

        UnwrapOps.Rotate(uvs, sel, deg);
        Assert.Equal(0.5f, uvs[0].U, 4); // grabbed point lands on the +V ray
        Assert.Equal(0.8f, uvs[0].V, 4);
    }

    [Theory]
    [InlineData(37f, 15f, 30f)]
    [InlineData(38f, 15f, 45f)]
    [InlineData(-7f, 15f, 0f)]
    [InlineData(-8f, 15f, -15f)]
    [InlineData(44f, 45f, 45f)]
    [InlineData(23f, 45f, 45f)]
    [InlineData(22f, 45f, 0f)]
    [InlineData(22f, 90f, 0f)]
    public void SnapAngle_Rounds_To_Nearest_Step(float degrees, float step, float expected)
    {
        Assert.Equal(expected, UvGizmoMath.SnapAngle(degrees, step), 3);
    }

    // ---- Scale (per-axis + uniform) -------------------------------------------

    [Fact]
    public void AxisScale_Is_The_Ratio_Of_Lever_Arms()
    {
        // Start 2 units right of the centroid; drag to 4 units right => 2x on that axis.
        Assert.Equal(2f, UvGizmoMath.AxisScale(centroid: 0f, start: 2f, current: 4f), 4);
        // Half the distance => 0.5x.
        Assert.Equal(0.5f, UvGizmoMath.AxisScale(0f, 2f, 1f), 4);
    }

    [Fact]
    public void AxisScale_Guards_A_Zero_Lever_Arm()
    {
        Assert.Equal(1f, UvGizmoMath.AxisScale(centroid: 0.5f, start: 0.5f, current: 0.9f), 4);
    }

    [Fact]
    public void AxisScale_Through_UnwrapOps_Scales_One_Axis_Only()
    {
        var uvs = new List<Uv> { new(0f, 0f), new(2f, 0f), new(2f, 1f), new(0f, 1f) };
        var sel = new[] { 0, 1, 2, 3 };
        Uv c = UnwrapOps.Centroid(uvs, sel); // (1.0, 0.5)
        float su = UvGizmoMath.AxisScale(c.U, start: 2f, current: 3f); // (2-1):(3-1) => 2x
        UnwrapOps.Scale(uvs, sel, su, 1f);

        Assert.Equal(4f, uvs[1].U - uvs[0].U, 4); // width doubled
        Assert.Equal(1f, uvs[2].V - uvs[1].V, 4); // height unchanged
    }

    [Fact]
    public void UniformScale_Is_The_Radial_Distance_Ratio()
    {
        Uv c = new(0.5f, 0.5f);
        Uv start = new(0.8f, 0.5f);  // radius 0.3
        Uv current = new(1.1f, 0.5f); // radius 0.6 => 2x
        Assert.Equal(2f, UvGizmoMath.UniformScale(c, start, current), 4);
    }

    [Fact]
    public void UniformScale_Guards_A_Zero_Start_Radius()
    {
        Uv c = new(0.5f, 0.5f);
        Assert.Equal(1f, UvGizmoMath.UniformScale(c, start: c, current: new(0.9f, 0.9f)), 4);
    }

    [Fact]
    public void SnapToStep_Rounds_To_Nearest_Multiple()
    {
        Assert.Equal(0.3125f, UvGizmoMath.SnapToStep(0.30f, 0.0625f), 5);
        Assert.Equal(0f, UvGizmoMath.SnapToStep(0.02f, 0.0625f), 5);
        Assert.Equal(1.25f, UvGizmoMath.SnapToStep(1.24f, 0.25f), 5);
    }
}
