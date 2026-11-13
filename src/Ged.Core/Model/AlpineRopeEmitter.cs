namespace Ged.Core.Model;

/// <summary>
/// An Alpine rope emitter (0x0AFBAE0A; layout editor_patch/rope_emitter.cpp:173-279 write, 285-466 read): a rope
/// or zipline hanging from this object to <see cref="TargetUid"/>, with optional decoration meshes. Values are kept
/// as stored.
/// </summary>
public sealed class AlpineRopeEmitter
{
    public int Uid { get; set; }

    public Vec3 Position { get; set; }

    public Mat3 Orientation { get; set; } = Mat3.Identity;

    public string ScriptName { get; set; } = string.Empty;

    public int TargetUid { get; set; } = -1;

    public float DangleLength { get; set; }

    public float Slack { get; set; }

    public float Weight { get; set; }

    public float Thickness { get; set; }

    public int Segments { get; set; }

    public float UvTilesPerMeter { get; set; }

    /// <summary>Stored packed red in the high byte (R&lt;&lt;24 | G&lt;&lt;16 | B&lt;&lt;8 | A).</summary>
    public uint PackedColor { get; set; } = 0xFFFFFFFFu;

    public string Bitmap { get; set; } = string.Empty;

    public float SwayAmplitude { get; set; }

    public float SwaySpeed { get; set; }

    /// <summary>Rope flags; 0x8 = zipline (common/include/common/rope_curve.h:101).</summary>
    public uint Flags { get; set; }

    public byte InitiallyOn { get; set; } = 1;

    /// <summary>The decoration block, or null when the record stores none (a single zero byte on disk).</summary>
    public AlpineRopeDecorations? Decorations { get; set; }

    public float ZipDownSpeed { get; set; }

    public float ZipUpSpeed { get; set; }

    public byte ZipDirection { get; set; }

    /// <summary>True when <see cref="Flags"/> carries the zipline bit.</summary>
    public bool IsZipline => (Flags & 0x8u) != 0;
}

/// <summary>A rope emitter's decoration block (present whenever the rope has at least one decoration mesh).</summary>
public sealed class AlpineRopeDecorations
{
    /// <summary>The "Decorations" checkbox; the mesh list is kept even when it is off.</summary>
    public byte Enabled { get; set; }

    /// <summary>1 to 6 decoration meshes.</summary>
    public List<AlpineRopeDecorationMesh> Meshes { get; set; } = new();

    public byte SpacingMode { get; set; }

    public int Count { get; set; }

    public float Spacing { get; set; }

    public byte RandomOrder { get; set; }

    public byte OrientMode { get; set; }
}

/// <summary>
/// One decoration mesh slot of a rope: the mesh, its offsets, and the glare / light effect blocks its
/// <see cref="FxFlags"/> declare (glare 0x1, light 0x2 — rope_curve.h:428-430; other bits are ignored on read).
/// </summary>
public sealed class AlpineRopeDecorationMesh
{
    public const byte FlagGlare = 0x1;
    public const byte FlagLight = 0x2;

    public string Mesh { get; set; } = string.Empty;

    public Vec3 Offset { get; set; }

    public float Pitch { get; set; }

    public float Yaw { get; set; }

    public float Roll { get; set; }

    /// <summary>The slot flags byte as stored.</summary>
    public byte FxFlags { get; set; }

    public bool HasGlare => (FxFlags & FlagGlare) != 0;

    public bool HasLight => (FxFlags & FlagLight) != 0;

    public string GlareBitmap { get; set; } = string.Empty;

    public RfColor GlareColor { get; set; }

    public float GlareConeAngle { get; set; }

    public float GlareIntensity { get; set; }

    public float GlareRadiusDistance { get; set; }

    public float GlareRadiusScale { get; set; }

    public float GlareDiminishDistance { get; set; }

    public string VolumetricBitmap { get; set; } = string.Empty;

    /// <summary>Stored only when <see cref="VolumetricBitmap"/> is not empty.</summary>
    public float VolumetricHeight { get; set; }

    /// <summary>Stored only when <see cref="VolumetricBitmap"/> is not empty.</summary>
    public float VolumetricLength { get; set; }

    public byte LightR { get; set; }

    public byte LightG { get; set; }

    public byte LightB { get; set; }

    public float LightRadius { get; set; }

    public float LightIntensity { get; set; }
}
