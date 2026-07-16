namespace Ged.Core.Model;

/// <summary>An ambient sound emitter (RFL <c>ambient_sound</c>).</summary>
public sealed class AmbientSound
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public byte HiddenInEditor { get; set; }

    public string SoundFileName { get; set; } = string.Empty;

    public float MinDistance { get; set; }

    public float VolumeScale { get; set; }

    public float Rolloff { get; set; }

    public int StartDelayMs { get; set; }
}
