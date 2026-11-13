using System;
using Ged.Core.Model;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>The single rotation representation: convention, decomposition, round-trip, rotation-like input.</summary>
public sealed class ScriptRotationTests
{
    private static double AngleDiff(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180)
        {
            d -= 360;
        }

        if (d < -180)
        {
            d += 360;
        }

        return Math.Abs(d);
    }

    [Fact]
    public void Convention_Yaw_Pitch_Roll()
    {
        // yaw 0 faces +Z; positive yaw turns toward +X.
        Assert.True(ScriptRotation.FromEuler(0, 0, 0).ApproxEquals(Mat3.Identity));
        Assert.True(ScriptRotation.FromEuler(90, 0, 0).Forward.ApproxEquals(new Vec3(1, 0, 0)));

        // pitch positive tilts the facing direction up.
        Vec3 f = ScriptRotation.FromEuler(0, 30, 0).Forward;
        Assert.Equal(0.5f, f.Y, 4);
        Assert.True(f.Z > 0.8f);

        // roll positive tilts the right vector up.
        Vec3 r = ScriptRotation.FromEuler(0, 0, 30).Right;
        Assert.Equal(0.5f, r.Y, 4);
        Assert.True(r.X > 0.8f);
    }

    [Fact]
    public void Euler_Round_Trips_Over_A_Grid()
    {
        for (int yaw = -180; yaw <= 180; yaw += 30)
        {
            for (int pitch = -85; pitch <= 85; pitch += 17)
            {
                for (int roll = -180; roll <= 180; roll += 45)
                {
                    Mat3 m = ScriptRotation.FromEuler(yaw, pitch, roll);
                    (double y, double p, double r) = ScriptRotation.ToEuler(m);
                    Assert.True(AngleDiff(y, yaw) < 1e-3, $"yaw {yaw},{pitch},{roll} -> {y}");
                    Assert.True(AngleDiff(p, pitch) < 1e-3, $"pitch {yaw},{pitch},{roll} -> {p}");
                    Assert.True(AngleDiff(r, roll) < 1e-3, $"roll {yaw},{pitch},{roll} -> {r}");
                    Assert.True(ScriptRotation.FromEuler(y, p, r).ApproxEquals(m, 1e-4f));
                }
            }
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void Gimbal_Lock_Folds_Roll_Into_Yaw(double pitch)
    {
        Mat3 m = ScriptRotation.FromEuler(30, pitch, 20);
        (double y, double p, double r) = ScriptRotation.ToEuler(m);
        Assert.Equal(0, r);
        Assert.True(AngleDiff(p, pitch) < 0.05); // asin is ill-conditioned at ±90° in float
        Assert.True(ScriptRotation.FromEuler(y, p, r).ApproxEquals(m, 1e-3f), $"folded {y},{p},{r}");
    }

    [Fact]
    public void Rotation_Like_Forms_Are_Accepted()
    {
        Mat3 euler = ScriptRotation.FromArgs("t", "rot", Table(("yaw", 90.0)));
        Assert.True(euler.Forward.ApproxEquals(new Vec3(1, 0, 0)));

        Mat3 basis = ScriptRotation.FromArgs("t", "rot", Table(("forward", new Vec3(1, 0, 0)), ("up", new Vec3(0, 1, 0))));
        Assert.True(basis.ApproxEquals(euler, 1e-4f));

        // Non-orthogonal forward/up is orthonormalised (Right = Up × Forward, Up = Forward × Right).
        Mat3 skew = ScriptRotation.FromArgs("t", "rot", Table(("forward", new Vec3(0, 0, 2)), ("up", new Vec3(0, 1, 1))));
        Assert.True(skew.ApproxEquals(Mat3.Identity, 1e-4f));

        var rec = new ScriptRotation(euler);
        Assert.True(ScriptRotation.FromArgs("t", "rot", rec).ApproxEquals(euler));
        Assert.Equal(90, rec.Yaw);
    }

    [Fact]
    public void Rotation_Like_Errors_Are_Actionable()
    {
        ScriptApiException unknown = Assert.Throws<ScriptApiException>(() => ScriptRotation.FromArgs("t", "rot", Table(("yawn", 1.0))));
        Assert.Contains("'yaw'", unknown.Hint, StringComparison.Ordinal);
        Assert.Throws<ScriptApiException>(() => ScriptRotation.FromArgs("t", "rot", Table(("forward", new Vec3(0, 1, 0)), ("up", new Vec3(0, 2, 0)))));
        Assert.Throws<ScriptApiException>(() => ScriptRotation.FromArgs("t", "rot", Table(("forward", new Vec3(0, 0, 1)))));
        Assert.Throws<ScriptApiException>(() => ScriptRotation.FromArgs("t", "rot", 45.0));
    }

    [Fact]
    public void Ged_Rot_Constructs_A_Record()
    {
        (ScriptRunResult r, _) = ScriptTestFixture.Run(ScriptTestFixture.NewDoc(entities: 0),
            "local q = ged.rot(90, 10) return q.yaw .. ',' .. q.pitch .. ',' .. q.roll .. ',' .. string.format('%.3f', q.forward.x)");
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("90,10,0,0.985", r.ReturnValue);
    }

    private static ScriptArgs Table(params (string Key, object? Value)[] entries)
    {
        var d = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string k, object? v) in entries)
        {
            d[k] = v;
        }

        return new ScriptArgs(d);
    }
}
