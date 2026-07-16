namespace Ged.Core.Model;

/// <summary>A bolt (lightning) emitter (RFL <c>bolt_emitter</c>).</summary>
public sealed class BoltEmitter
{
    public ObjectHeader Header { get; set; } = new();

    public int TargetUid { get; set; }

    public float SrcCtrlDist { get; set; }

    public float TrgCtrlDist { get; set; }

    public float Thickness { get; set; }

    public float Jitter { get; set; }

    public int NumSegments { get; set; }

    public float SpawnDelay { get; set; }

    public float SpawnDelayRandomize { get; set; }

    public float Decay { get; set; }

    public float DecayRandomize { get; set; }

    public RfColor Color { get; set; }

    public string Texture { get; set; } = string.Empty;

    /// <summary>32-bit bolt_emitter_flags bitfield.</summary>
    public uint Flags { get; set; }

    public byte InitiallyOn { get; set; }
}
