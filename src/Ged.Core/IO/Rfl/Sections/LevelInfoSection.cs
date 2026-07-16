using System;
using System.Globalization;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>level_info (0x1000000): level metadata plus four editor view configs.</summary>
public sealed class LevelInfoSection : IRflSectionContent
{
    private const int FreeLook = 0;

    /// <summary>RED's date format, e.g. "Friday, August 24, 2001 16:48:01" (verified on stock levels).</summary>
    public const string DateFormat = "dddd, MMMM d, yyyy HH:mm:ss";

    public SectionType Type => SectionType.LevelInfo;

    /// <summary>rfl.ksy <c>unknown</c> (typically 1, unused by the engine). Preserved exactly.</summary>
    public int Unknown { get; set; }

    public string LevelName { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Date { get; set; } = string.Empty;

    public byte HasMovers { get; set; }

    public byte MultiplayerLevel { get; set; }

    /// <summary>Always exactly four entries.</summary>
    public List<EditorViewConfig> ViewConfigs { get; set; } = new();

    /// <summary>
    /// The level_info a fresh level gets (File &gt; New): empty name/author, today's date in
    /// RED's format, no movers, single-player, and RED's four editor view configs in the order
    /// it writes them (Top, Front, Free Look, Left — verified on stock levels), each with an
    /// identity orientation and the ortho views pushed back along their view axis.
    /// </summary>
    public static LevelInfoSection CreateDefault(DateTime now) => new()
    {
        Unknown = 1,
        LevelName = string.Empty,
        Author = string.Empty,
        Date = now.ToString(DateFormat, CultureInfo.InvariantCulture),
        HasMovers = 0,
        MultiplayerLevel = 0,
        ViewConfigs =
        {
            new EditorViewConfig { ViewType = 1, Position2d = new[] { 30f, 0f, 25000f, 0f }, Rotation = Mat3.Identity }, // Top
            new EditorViewConfig { ViewType = 3, Position2d = new[] { 30f, 0f, 0f, -25000f }, Rotation = Mat3.Identity }, // Front
            new EditorViewConfig { ViewType = FreeLook, Position3d = new Vec3(0f, 0f, 0f), Rotation = Mat3.Identity }, // Free Look
            new EditorViewConfig { ViewType = 5, Position2d = new[] { 30f, 25000f, 0f, 0f }, Rotation = Mat3.Identity }, // Left
        },
    };

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new LevelInfoSection
        {
            Unknown = r.ReadI32(),
            LevelName = r.ReadVString(),
            Author = r.ReadVString(),
            Date = r.ReadVString(),
            HasMovers = r.ReadU8(),
            MultiplayerLevel = r.ReadU8(),
        };

        for (int i = 0; i < 4; i++)
        {
            var cfg = new EditorViewConfig { ViewType = r.ReadI32() };
            if (cfg.ViewType == FreeLook)
            {
                cfg.Position3d = r.ReadVec3();
            }
            else
            {
                cfg.Position2d = new[] { r.ReadF32(), r.ReadF32(), r.ReadF32(), r.ReadF32() };
            }

            cfg.Rotation = r.ReadMat3();
            section.ViewConfigs.Add(cfg);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Unknown);
        w.WriteVString(LevelName);
        w.WriteVString(Author);
        w.WriteVString(Date);
        w.WriteU8(HasMovers);
        w.WriteU8(MultiplayerLevel);

        foreach (EditorViewConfig cfg in ViewConfigs)
        {
            w.WriteI32(cfg.ViewType);
            if (cfg.ViewType == FreeLook)
            {
                w.WriteVec3(cfg.Position3d ?? default);
            }
            else
            {
                float[] p = cfg.Position2d ?? new float[4];
                for (int i = 0; i < 4; i++)
                {
                    w.WriteF32(p[i]);
                }
            }

            w.WriteMat3(cfg.Rotation);
        }
    }
}
