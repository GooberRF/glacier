namespace Ged.Core.Model;

/// <summary>A particle emitter (RFL <c>particle_emitter</c>).</summary>
public sealed class ParticleEmitter
{
    public ObjectHeader Header { get; set; } = new();

    /// <summary>1 = plane, 2 = sphere.</summary>
    public int Shape { get; set; }

    public float SphereRadius { get; set; }

    public float PlaneWidth { get; set; }

    public float PlaneDepth { get; set; }

    public string Texture { get; set; } = string.Empty;

    public float SpawnDelay { get; set; }

    public float SpawnRandomize { get; set; }

    public float Velocity { get; set; }

    public float VelocityRandomize { get; set; }

    public float Acceleration { get; set; }

    public float Decay { get; set; }

    public float DecayRandomize { get; set; }

    public float ParticleRadius { get; set; }

    public float ParticleRadiusRandomize { get; set; }

    public float GrowthRate { get; set; }

    public float GravityMultiplier { get; set; }

    public float RandomDirection { get; set; }

    public RfColor ParticleColor { get; set; }

    public RfColor FadeToColor { get; set; }

    public uint EmitterFlags { get; set; }

    public ushort ParticleFlags { get; set; }

    /// <summary>Packed nibbles: high = stickiness, low = bounciness. Stored raw for exactness.</summary>
    public byte StickinessBounciness { get; set; }

    /// <summary>Packed nibbles: high = push effect, low = swirliness. Stored raw for exactness.</summary>
    public byte PushSwirliness { get; set; }

    /// <summary>Stickiness nibble (0-15) of <see cref="StickinessBounciness"/> — an inspector accessor, not serialized.</summary>
    public int Stickiness
    {
        get => (StickinessBounciness >> 4) & 0xF;
        set => StickinessBounciness = (byte)((StickinessBounciness & 0x0F) | ((value & 0xF) << 4));
    }

    /// <summary>Bounciness nibble (0-15) of <see cref="StickinessBounciness"/>.</summary>
    public int Bounciness
    {
        get => StickinessBounciness & 0xF;
        set => StickinessBounciness = (byte)((StickinessBounciness & 0xF0) | (value & 0xF));
    }

    /// <summary>Push nibble (0-15) of <see cref="PushSwirliness"/>.</summary>
    public int Push
    {
        get => (PushSwirliness >> 4) & 0xF;
        set => PushSwirliness = (byte)((PushSwirliness & 0x0F) | ((value & 0xF) << 4));
    }

    /// <summary>Swirliness nibble (0-15) of <see cref="PushSwirliness"/>.</summary>
    public int Swirliness
    {
        get => PushSwirliness & 0xF;
        set => PushSwirliness = (byte)((PushSwirliness & 0xF0) | (value & 0xF));
    }

    public byte InitiallyOn { get; set; }

    public float TimeOn { get; set; }

    public float TimeOnRandomize { get; set; }

    public float TimeOff { get; set; }

    public float TimeOffRandomize { get; set; }

    public float ActiveDistance { get; set; }
}
