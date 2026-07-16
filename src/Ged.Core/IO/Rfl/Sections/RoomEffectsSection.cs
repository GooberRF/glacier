using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>room_effects (0xC00): sky/liquid/ambient-light room markers.</summary>
public sealed class RoomEffectsSection : IRflSectionContent
{
    public const int EffectSkyRoom = 1;
    public const int EffectLiquidRoom = 2;
    public const int EffectAmbientLight = 3;
    public const int EffectNone = 4;

    public SectionType Type => SectionType.RoomEffects;

    public List<RoomEffect> Effects { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new RoomEffectsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            var e = new RoomEffect { EffectType = r.ReadI32() };

            if (e.EffectType == EffectAmbientLight)
            {
                e.AmbientLightColor = r.ReadColor();
            }
            else if (e.EffectType == EffectLiquidRoom)
            {
                e.LiquidProperties = ReadLiquid(r);
            }

            e.RoomIsCold = r.ReadU8();
            e.RoomIsOutside = r.ReadU8();
            e.RoomIsAirLock = r.ReadU8();
            e.Header = ObjectHeader.Read(r);
            section.Effects.Add(e);
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Effects.Count);
        foreach (RoomEffect e in Effects)
        {
            w.WriteI32(e.EffectType);

            if (e.EffectType == EffectAmbientLight)
            {
                w.WriteColor(e.AmbientLightColor!.Value);
            }
            else if (e.EffectType == EffectLiquidRoom)
            {
                WriteLiquid(w, e.LiquidProperties!);
            }

            w.WriteU8(e.RoomIsCold);
            w.WriteU8(e.RoomIsOutside);
            w.WriteU8(e.RoomIsAirLock);
            e.Header.Write(w);
        }
    }

    private static RoomEffectLiquidProperties ReadLiquid(RfReader r) => new()
    {
        Waveform = r.ReadI32(),
        Depth = r.ReadF32(),
        SurfaceTexture = r.ReadVString(),
        LiquidColor = r.ReadColor(),
        Visibility = r.ReadF32(),
        LiquidType = r.ReadI32(),
        ContainsPlankton = r.ReadU8(),
        TexturePixelsPerMeterU = r.ReadI32(),
        TexturePixelsPerMeterV = r.ReadI32(),
        TextureAngleDegrees = r.ReadF32(),
        TextureScrollRate = r.ReadUv(),
    };

    private static void WriteLiquid(RfWriter w, RoomEffectLiquidProperties lp)
    {
        w.WriteI32(lp.Waveform);
        w.WriteF32(lp.Depth);
        w.WriteVString(lp.SurfaceTexture);
        w.WriteColor(lp.LiquidColor);
        w.WriteF32(lp.Visibility);
        w.WriteI32(lp.LiquidType);
        w.WriteU8(lp.ContainsPlankton);
        w.WriteI32(lp.TexturePixelsPerMeterU);
        w.WriteI32(lp.TexturePixelsPerMeterV);
        w.WriteF32(lp.TextureAngleDegrees);
        w.WriteUv(lp.TextureScrollRate);
    }
}
