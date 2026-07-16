using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Effects;

/// <summary>One live particle at a query time: world position, radius, colour.</summary>
public readonly record struct SimParticle(Vec3 Position, float Radius, RfColor Color, float LifeFraction);

/// <summary>Tunables for the CPU particle sim (budget + world constants).</summary>
public sealed class ParticleSimOptions
{
    /// <summary>Hard cap on live particles per emitter (time-slicing budget).</summary>
    public int MaxParticles { get; set; } = 2000;

    /// <summary>World gravity magnitude (m/s²), applied down −Y and scaled by the emitter's gravity multiplier.</summary>
    public float Gravity { get; set; } = 9.8f;
}

/// <summary>
/// A table-accurate CPU particle simulation driven entirely by a
/// <see cref="ParticleEmitter"/>'s authored fields. The stream is deterministic:
/// particle <c>i</c> is a pure function of the emitter's UID seed and <c>i</c>
/// (non-cumulative spawn jitter), so the alive set at any query time can be
/// evaluated in a bounded window without integrating from t=0 — cheap enough to
/// re-evaluate every frame and reproducible enough to unit-test.
/// </summary>
public static class ParticleSimulator
{
    private const int SaltInterval = 1;
    private const int SaltLife = 2;
    private const int SaltPosX = 3;
    private const int SaltPosY = 4;
    private const int SaltPosZ = 5;
    private const int SaltDirX = 6;
    private const int SaltDirY = 7;
    private const int SaltDirZ = 8;
    private const int SaltSpeed = 9;
    private const int SaltRadius = 10;

    /// <summary>Particles alive at <paramref name="time"/> seconds (world space).</summary>
    public static IReadOnlyList<SimParticle> Simulate(ParticleEmitter emitter, float time, ParticleSimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        options ??= new ParticleSimOptions();
        var result = new List<SimParticle>();
        if (time < 0f)
        {
            return result;
        }

        int seed = emitter.Header.Uid != 0 ? emitter.Header.Uid : 0x5eed;
        float interval = MathF.Max(emitter.SpawnDelay, 0.01f);
        float jitterBound = MathF.Max(emitter.SpawnRandomize, 0f);
        float maxLife = MathF.Max(emitter.Decay + MathF.Max(emitter.DecayRandomize, 0f), 0.05f);

        Vec3 origin = emitter.Header.Position;
        Mat3 rot = emitter.Header.Rotation;
        Vec3 forward = rot.Forward.Normalized();
        if (forward.LengthSquared() < 1e-6f)
        {
            forward = new Vec3(0f, 0f, 1f);
        }

        // Bounded spawn-index window: only particles spawned in (t-maxLife, t] can be alive.
        int iHi = (int)MathF.Ceiling((time + jitterBound) / interval) + 1;
        int iLo = (int)MathF.Floor((time - maxLife - jitterBound) / interval) - 1;
        if (iLo < 0)
        {
            iLo = 0;
        }

        if (iHi - iLo > options.MaxParticles)
        {
            iLo = Math.Max(iLo, iHi - options.MaxParticles);
        }

        for (int i = iLo; i <= iHi && result.Count < options.MaxParticles; i++)
        {
            float spawnTime = (i * interval) + (SimRandom.Signed(seed, i, SaltInterval) * jitterBound);
            if (spawnTime < 0f || spawnTime > time)
            {
                continue;
            }

            if (!IsEmittingAt(emitter, spawnTime))
            {
                continue;
            }

            float life = MathF.Max(emitter.Decay + (SimRandom.Signed(seed, i, SaltLife) * MathF.Max(emitter.DecayRandomize, 0f)), 0.05f);
            float age = time - spawnTime;
            if (age >= life)
            {
                continue;
            }

            // Spawn offset within the emitter's shape (local space, then rotated to world).
            Vec3 local = SpawnOffset(emitter, seed, i);
            Vec3 spawnPos = origin.Add(rot.Transform(local));

            // Velocity: forward perturbed toward a random direction by RandomDirection.
            Vec3 rnd = new Vec3(
                SimRandom.Signed(seed, i, SaltDirX),
                SimRandom.Signed(seed, i, SaltDirY),
                SimRandom.Signed(seed, i, SaltDirZ)).Normalized();
            float spread = Math.Clamp(emitter.RandomDirection, 0f, 1f);
            Vec3 dir = forward.Add(rnd.Scale(spread)).Normalized();
            float speed = emitter.Velocity + (SimRandom.Signed(seed, i, SaltSpeed) * MathF.Max(emitter.VelocityRandomize, 0f));
            Vec3 v0 = dir.Scale(speed);

            // Acceleration along travel + gravity.
            Vec3 accel = dir.Scale(emitter.Acceleration)
                .Add(new Vec3(0f, -options.Gravity * emitter.GravityMultiplier, 0f));
            Vec3 pos = spawnPos.Add(v0.Scale(age)).Add(accel.Scale(0.5f * age * age));

            float radius = emitter.ParticleRadius
                + (SimRandom.Signed(seed, i, SaltRadius) * MathF.Max(emitter.ParticleRadiusRandomize, 0f))
                + (emitter.GrowthRate * age);
            radius = MathF.Max(radius, 0f);

            float lf = age / life;
            RfColor color = LerpColor(emitter.ParticleColor, emitter.FadeToColor, lf);
            result.Add(new SimParticle(pos, radius, color, lf));
        }

        return result;
    }

    /// <summary>Whether the emitter is spawning at <paramref name="t"/> given its on/off cycle.</summary>
    public static bool IsEmittingAt(ParticleEmitter e, float t)
    {
        bool initiallyOn = e.InitiallyOn != 0;
        float on = MathF.Max(e.TimeOn, 0f);
        float off = MathF.Max(e.TimeOff, 0f);
        if (on <= 0f || off <= 0f)
        {
            return initiallyOn; // no alternating cycle: constant state
        }

        float period = on + off;
        float phase = t - (MathF.Floor(t / period) * period);
        return initiallyOn ? phase < on : phase >= off;
    }

    private static Vec3 SpawnOffset(ParticleEmitter e, int seed, int i)
    {
        if (e.Shape == 2)
        {
            // Sphere: a random point within SphereRadius.
            var v = new Vec3(
                SimRandom.Signed(seed, i, SaltPosX),
                SimRandom.Signed(seed, i, SaltPosY),
                SimRandom.Signed(seed, i, SaltPosZ));
            float r = MathF.Cbrt(MathF.Abs(SimRandom.Value(seed, i, SaltRadius)));
            return v.Normalized().Scale(e.SphereRadius * r);
        }

        // Plane (default): a random point on the emitter's local XY rectangle.
        float x = SimRandom.Signed(seed, i, SaltPosX) * 0.5f * e.PlaneWidth;
        float y = SimRandom.Signed(seed, i, SaltPosY) * 0.5f * e.PlaneDepth;
        return new Vec3(x, y, 0f);
    }

    private static RfColor LerpColor(RfColor a, RfColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        static byte L(byte x, byte y, float f) => (byte)MathF.Round(x + ((y - x) * f));
        return new RfColor(L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t), L(a.A, b.A, t));
    }
}
