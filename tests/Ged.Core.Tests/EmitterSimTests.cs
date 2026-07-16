using System.Collections.Generic;
using System.Linq;
using Ged.Core.Effects;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the live-preview emitter sims: a deterministic seeded emitter
/// yields the expected particle counts and closed-form positions at a query time,
/// on/off gating works, and the bolt polyline pins its endpoints and re-jitters.
/// </summary>
public sealed class EmitterSimTests
{
    /// <summary>An always-on, no-jitter emitter firing straight up +Z at 2 m/s, 0.1s spacing, 1s life.</summary>
    private static ParticleEmitter SteadyEmitter() => new()
    {
        Header = new ObjectHeader { Uid = 42, Position = new Vec3(0, 0, 0), Rotation = Mat3.Identity },
        Shape = 1,
        PlaneWidth = 0f,
        PlaneDepth = 0f,
        SpawnDelay = 0.1f,
        SpawnRandomize = 0f,
        Velocity = 2f,
        VelocityRandomize = 0f,
        Acceleration = 0f,
        GravityMultiplier = 0f,
        Decay = 1.0f,
        DecayRandomize = 0f,
        ParticleRadius = 0.25f,
        ParticleRadiusRandomize = 0f,
        GrowthRate = 0f,
        RandomDirection = 0f,
        InitiallyOn = 1,
        TimeOn = 0f,
        TimeOff = 0f,
        ParticleColor = new RfColor(255, 255, 255, 255),
        FadeToColor = new RfColor(255, 0, 0, 0),
    };

    [Fact]
    public void Particle_Count_Is_Deterministic_At_Time()
    {
        var e = SteadyEmitter();

        // Spawns at 0, .1, .2, .3, .4, .5 are all within life 1.0 at t=0.55 -> 6 alive.
        IReadOnlyList<SimParticle> at055 = ParticleSimulator.Simulate(e, 0.55f, NoGravity());
        Assert.Equal(6, at055.Count);

        // At t=1.55 the 0.0..0.5 spawns have expired (age>1); spawns .6..1.5 alive = 10.
        IReadOnlyList<SimParticle> at155 = ParticleSimulator.Simulate(e, 1.55f, NoGravity());
        Assert.Equal(10, at155.Count);
    }

    [Fact]
    public void Particle_Positions_Match_Closed_Form()
    {
        var e = SteadyEmitter();
        IReadOnlyList<SimParticle> particles = ParticleSimulator.Simulate(e, 0.55f, NoGravity());

        // Highest particle = oldest (spawned at t=0, age=0.55): z = 2 * 0.55 = 1.1.
        SimParticle oldest = particles.OrderByDescending(p => p.Position.Z).First();
        Assert.Equal(1.1f, oldest.Position.Z, 3);
        Assert.Equal(0f, oldest.Position.X, 4);
        Assert.Equal(0f, oldest.Position.Y, 4);

        // Youngest particle spawned at t=0.5, age=0.05: z = 2 * 0.05 = 0.1.
        SimParticle youngest = particles.OrderBy(p => p.Position.Z).First();
        Assert.Equal(0.1f, youngest.Position.Z, 3);
    }

    [Fact]
    public void Simulation_Is_Reproducible()
    {
        var e = SteadyEmitter();
        var a = ParticleSimulator.Simulate(e, 0.73f, NoGravity());
        var b = ParticleSimulator.Simulate(e, 0.73f, NoGravity());
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Position.Z, b[i].Position.Z, 5);
        }
    }

    [Fact]
    public void Gravity_And_Growth_Apply()
    {
        var e = SteadyEmitter();
        e.GravityMultiplier = 1f;
        e.GrowthRate = 1f; // +1 m radius per second
        var opts = new ParticleSimOptions { Gravity = 10f };

        SimParticle oldest = ParticleSimulator.Simulate(e, 0.55f, opts).OrderByDescending(p => p.Position.Z).First();
        // Gravity pulls Y down: y = 0.5 * (-10) * 0.55^2 = -1.5125.
        Assert.Equal(-1.5125f, oldest.Position.Y, 3);
        // Radius grew: 0.25 + 1 * 0.55 = 0.8.
        Assert.Equal(0.8f, oldest.Radius, 3);
    }

    [Fact]
    public void Color_Fades_Toward_Dest()
    {
        var e = SteadyEmitter();
        SimParticle oldest = ParticleSimulator.Simulate(e, 0.55f, NoGravity()).OrderByDescending(p => p.Position.Z).First();
        // life fraction 0.55: G fades 255->0, so ~= 255*(1-0.55) ~= 114; A fades 255->0.
        Assert.InRange(oldest.Color.G, 110, 118);
        Assert.InRange(oldest.Color.A, 110, 118);
        Assert.Equal(255, oldest.Color.R); // R stays 255 (both endpoints 255)
    }

    [Fact]
    public void OnOff_Cycle_Gates_Spawning()
    {
        var e = SteadyEmitter();
        e.TimeOn = 0.3f;
        e.TimeOff = 0.3f; // on [0,.3), off [.3,.6), on [.6,.9)...

        Assert.True(ParticleSimulator.IsEmittingAt(e, 0.1f));
        Assert.False(ParticleSimulator.IsEmittingAt(e, 0.4f));
        Assert.True(ParticleSimulator.IsEmittingAt(e, 0.7f));

        // An initially-off emitter with no cycle never emits.
        var off = SteadyEmitter();
        off.InitiallyOn = 0;
        Assert.Empty(ParticleSimulator.Simulate(off, 1.0f, NoGravity()));
    }

    [Fact]
    public void Budget_Caps_Live_Particle_Count()
    {
        var e = SteadyEmitter();
        e.SpawnDelay = 0.001f; // would spawn thousands
        e.Decay = 10f;
        var opts = new ParticleSimOptions { MaxParticles = 500, Gravity = 0f };
        Assert.True(ParticleSimulator.Simulate(e, 5f, opts).Count <= 500);
    }

    // ─── Bolt ────────────────────────────────────────────────────────────────

    [Fact]
    public void Bolt_Polyline_Pins_Endpoints_And_Segments()
    {
        var bolt = new BoltEmitter
        {
            Header = new ObjectHeader { Uid = 7, Position = new Vec3(0, 0, 0) },
            NumSegments = 8,
            Jitter = 0.5f,
            Thickness = 0.1f,
            InitiallyOn = 1,
        };
        var src = new Vec3(0, 0, 0);
        var trg = new Vec3(10, 0, 0);

        IReadOnlyList<Vec3> line = BoltSimulator.Polyline(bolt, src, trg, 0f);
        Assert.Equal(9, line.Count); // segments + 1
        Assert.True(line[0].ApproxEquals(src));
        Assert.True(line[^1].ApproxEquals(trg));

        // Interior points are displaced off the straight axis by the jitter.
        bool anyOffAxis = line.Skip(1).Take(7).Any(p => System.MathF.Abs(p.Y) > 1e-3f || System.MathF.Abs(p.Z) > 1e-3f);
        Assert.True(anyOffAxis);
    }

    [Fact]
    public void Bolt_Rejitters_Over_Time_But_Is_Stable_Within_A_Frame()
    {
        var bolt = new BoltEmitter
        {
            Header = new ObjectHeader { Uid = 7 },
            NumSegments = 8,
            Jitter = 0.5f,
            InitiallyOn = 1,
        };
        var src = new Vec3(0, 0, 0);
        var trg = new Vec3(10, 0, 0);

        var f0 = BoltSimulator.Polyline(bolt, src, trg, 0f);
        var f0b = BoltSimulator.Polyline(bolt, src, trg, 0.001f); // same flicker bucket
        var f1 = BoltSimulator.Polyline(bolt, src, trg, 1.0f); // different bucket

        Assert.Equal(f0[4].Y, f0b[4].Y, 5); // stable within the bucket
        Assert.NotEqual(f0[4].Y, f1[4].Y, 5); // re-jittered across buckets
    }

    private static ParticleSimOptions NoGravity() => new() { Gravity = 0f };
}
