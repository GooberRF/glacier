using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_weather_region_objects (0x0AFBAE06). Read as Alpine's
/// <c>weather_region_deserialize_chunk</c> (editor_patch/weather_region.cpp:116-206): count capped at 10000,
/// lenient strings, a record the chunk ends inside is dropped with the rest. Written as
/// <c>weather_region_serialize_chunk</c> (:48-114).
/// </summary>
public sealed class AlpineWeatherRegionsSection : AlpineRecordChunkSection<AlpineWeatherRegion>
{
    public override SectionType Type => SectionType.AlpineWeatherRegions;

    protected override uint MaxRecords => 10000;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineWeatherRegionsSection(), r);

    private protected override AlpineWeatherRegion? ReadRecord(AlpineChunkReader r)
    {
        if (!TryReadHeader(r, out int uid, out Vec3 pos, out Mat3 orient))
        {
            return null;
        }

        var w = new AlpineWeatherRegion { Uid = uid, Position = pos, Orientation = orient, ScriptName = r.LenientString() };
        if (!r.TryI32(out int shape) || !r.TryI32(out int type)
            || !r.TryF32(out float width) || !r.TryF32(out float height) || !r.TryF32(out float depth)
            || !r.TryF32(out float radius) || !r.TryF32(out float density)
            || !r.TryColor( out RfColor rain) || !r.TryF32(out float rainFall) || !r.TryF32(out float windX)
            || !r.TryF32(out float windZ) || !r.TryF32(out float streak)
            || !r.TryColor( out RfColor snow) || !r.TryF32(out float snowFall) || !r.TryF32(out float swayAmp)
            || !r.TryF32(out float swaySpeed) || !r.TryF32(out float sprite))
        {
            return null;
        }

        w.Shape = shape;
        w.WeatherType = type;
        w.Width = width;
        w.Height = height;
        w.Depth = depth;
        w.Radius = radius;
        w.DensityScale = density;
        w.RainColor = rain;
        w.RainFallSpeed = rainFall;
        w.RainWindX = windX;
        w.RainWindZ = windZ;
        w.RainStreakSeconds = streak;
        w.SnowColor = snow;
        w.SnowFallSpeed = snowFall;
        w.SnowSwayAmplitude = swayAmp;
        w.SnowSwaySpeed = swaySpeed;
        w.SnowSpriteRadius = sprite;
        w.SnowBitmap = r.LenientString();
        if (!r.TryU8(out byte showRange) || !r.TryU8(out byte enabled) || !r.TryF32(out float active)
            || !r.TryF32(out float visible) || !r.TryU8(out byte block) || !r.TryF32(out float column))
        {
            return null;
        }

        w.AlwaysShowRange = showRange;
        w.InitiallyEnabled = enabled;
        w.ActiveDistance = active;
        w.VisibleDistance = visible;
        w.BlockByGeometry = block;
        w.ColumnWidth = column;
        return w;
    }

    protected override void WriteRecord(RfWriter w, AlpineWeatherRegion x)
    {
        WriteHeader(w, x.Uid, x.Position, x.Orientation);
        w.WriteVString(x.ScriptName);
        w.WriteI32(x.Shape);
        w.WriteI32(x.WeatherType);
        w.WriteF32(x.Width);
        w.WriteF32(x.Height);
        w.WriteF32(x.Depth);
        w.WriteF32(x.Radius);
        w.WriteF32(x.DensityScale);
        w.WriteColor(x.RainColor);
        w.WriteF32(x.RainFallSpeed);
        w.WriteF32(x.RainWindX);
        w.WriteF32(x.RainWindZ);
        w.WriteF32(x.RainStreakSeconds);
        w.WriteColor(x.SnowColor);
        w.WriteF32(x.SnowFallSpeed);
        w.WriteF32(x.SnowSwayAmplitude);
        w.WriteF32(x.SnowSwaySpeed);
        w.WriteF32(x.SnowSpriteRadius);
        w.WriteVString(x.SnowBitmap);
        w.WriteU8(x.AlwaysShowRange);
        w.WriteU8(x.InitiallyEnabled);
        w.WriteF32(x.ActiveDistance);
        w.WriteF32(x.VisibleDistance);
        w.WriteU8(x.BlockByGeometry);
        w.WriteF32(x.ColumnWidth);
    }
}
