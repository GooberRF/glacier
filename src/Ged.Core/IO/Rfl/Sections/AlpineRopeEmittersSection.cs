using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// alpine_rope_emitter_objects (0x0AFBAE0A). Read as Alpine's <c>rope_emitter_deserialize_chunk</c>
/// (editor_patch/rope_emitter.cpp:285-466): count capped at 10000 (common/include/common/rope_curve.h:83), strict
/// strings, an optional decoration block whose 1–6 mesh slots each carry the glare / light blocks their masked
/// flags declare (rope_curve.h:428-430, rope_emitter.cpp:390-417). A record the chunk ends inside — or whose
/// decoration mesh count is outside 1..6, which leaves its length unknowable — ends the load. Written as
/// <c>rope_emitter_serialize_chunk</c> (:173-279).
/// </summary>
public sealed class AlpineRopeEmittersSection : AlpineRecordChunkSection<AlpineRopeEmitter>
{
    private const int MaxDecorationMeshes = 6;
    private const byte SlotFlagMask = 0x3;

    public override SectionType Type => SectionType.AlpineRopeEmitters;

    protected override uint MaxRecords => 10000;

    public static IRflSectionContent Parse(RfReader r, RflContext ctx) => Load(new AlpineRopeEmittersSection(), r);

    private protected override AlpineRopeEmitter? ReadRecord(AlpineChunkReader r)
    {
        if (!TryReadHeader(r, out int uid, out Vec3 pos, out Mat3 orient)
            || !r.TryString(out string script) || !r.TryI32(out int target)
            || !r.TryF32(out float dangle) || !r.TryF32(out float slack) || !r.TryF32(out float weight)
            || !r.TryF32(out float thickness) || !r.TryI32(out int segments) || !r.TryF32(out float uvTiles)
            || !r.TryU32(out uint color) || !r.TryString(out string bitmap)
            || !r.TryF32(out float swayAmp) || !r.TryF32(out float swaySpeed) || !r.TryU32(out uint flags)
            || !r.TryU8(out byte on) || !r.TryU8(out byte hasDeco))
        {
            return null;
        }

        var rope = new AlpineRopeEmitter
        {
            Uid = uid,
            Position = pos,
            Orientation = orient,
            ScriptName = script,
            TargetUid = target,
            DangleLength = dangle,
            Slack = slack,
            Weight = weight,
            Thickness = thickness,
            Segments = segments,
            UvTilesPerMeter = uvTiles,
            PackedColor = color,
            Bitmap = bitmap,
            SwayAmplitude = swayAmp,
            SwaySpeed = swaySpeed,
            Flags = flags,
            InitiallyOn = on,
        };

        if (hasDeco > 1)
        {
            r.Lossy = true; // a block flag other than 0/1 does not survive a rewrite: keep the chunk raw
        }

        if (hasDeco != 0)
        {
            if (ReadDecorations(r) is not { } deco)
            {
                return null;
            }

            rope.Decorations = deco;
        }

        if (!r.TryF32(out float down) || !r.TryF32(out float up) || !r.TryU8(out byte direction))
        {
            return null;
        }

        rope.ZipDownSpeed = down;
        rope.ZipUpSpeed = up;
        rope.ZipDirection = direction;
        return rope;
    }

    private static AlpineRopeDecorations? ReadDecorations(AlpineChunkReader r)
    {
        if (!r.TryU8(out byte enabled) || !r.TryU8(out byte meshCount) || meshCount is < 1 or > MaxDecorationMeshes)
        {
            return null;
        }

        var deco = new AlpineRopeDecorations { Enabled = enabled };
        for (int m = 0; m < meshCount; m++)
        {
            if (!r.TryString(out string mesh) || !r.TryVec3(out Vec3 offset) || !r.TryF32(out float pitch)
                || !r.TryF32(out float yaw) || !r.TryF32(out float roll) || !r.TryU8(out byte fx))
            {
                return null;
            }

            var slot = new AlpineRopeDecorationMesh { Mesh = mesh, Offset = offset, Pitch = pitch, Yaw = yaw, Roll = roll, FxFlags = fx };
            byte masked = (byte)(fx & SlotFlagMask);
            if ((masked & AlpineRopeDecorationMesh.FlagGlare) != 0)
            {
                if (!r.TryString(out string glare) || !r.TryColor(out RfColor glareColor) || !r.TryF32(out float cone)
                    || !r.TryF32(out float intensity) || !r.TryF32(out float radiusDistance) || !r.TryF32(out float radiusScale)
                    || !r.TryF32(out float diminish) || !r.TryString(out string volumetric))
                {
                    return null;
                }

                slot.GlareBitmap = glare;
                slot.GlareColor = glareColor;
                slot.GlareConeAngle = cone;
                slot.GlareIntensity = intensity;
                slot.GlareRadiusDistance = radiusDistance;
                slot.GlareRadiusScale = radiusScale;
                slot.GlareDiminishDistance = diminish;
                slot.VolumetricBitmap = volumetric;
                if (volumetric.Length != 0)
                {
                    if (!r.TryF32(out float vh) || !r.TryF32(out float vl))
                    {
                        return null;
                    }

                    slot.VolumetricHeight = vh;
                    slot.VolumetricLength = vl;
                }
            }

            if ((masked & AlpineRopeDecorationMesh.FlagLight) != 0)
            {
                if (!r.TryU8(out byte lr) || !r.TryU8(out byte lg) || !r.TryU8(out byte lb)
                    || !r.TryF32(out float radius) || !r.TryF32(out float lightIntensity))
                {
                    return null;
                }

                slot.LightR = lr;
                slot.LightG = lg;
                slot.LightB = lb;
                slot.LightRadius = radius;
                slot.LightIntensity = lightIntensity;
            }

            deco.Meshes.Add(slot);
        }

        if (!r.TryU8(out byte spacingMode) || !r.TryI32(out int count) || !r.TryF32(out float spacing)
            || !r.TryU8(out byte random) || !r.TryU8(out byte orientMode))
        {
            return null;
        }

        deco.SpacingMode = spacingMode;
        deco.Count = count;
        deco.Spacing = spacing;
        deco.RandomOrder = random;
        deco.OrientMode = orientMode;
        return deco;
    }

    protected override void WriteRecord(RfWriter w, AlpineRopeEmitter x)
    {
        WriteHeader(w, x.Uid, x.Position, x.Orientation);
        w.WriteVString(x.ScriptName);
        w.WriteI32(x.TargetUid);
        w.WriteF32(x.DangleLength);
        w.WriteF32(x.Slack);
        w.WriteF32(x.Weight);
        w.WriteF32(x.Thickness);
        w.WriteI32(x.Segments);
        w.WriteF32(x.UvTilesPerMeter);
        w.WriteU32(x.PackedColor);
        w.WriteVString(x.Bitmap);
        w.WriteF32(x.SwayAmplitude);
        w.WriteF32(x.SwaySpeed);
        w.WriteU32(x.Flags);
        w.WriteU8(x.InitiallyOn);

        // Alpine gates the block on the mesh list (rope_emitter.cpp:214-220): no meshes, one zero byte.
        AlpineRopeDecorations? deco = x.Decorations is { Meshes.Count: > 0 } d ? d : null;
        w.WriteU8(deco is null ? (byte)0 : (byte)1);
        if (deco is not null)
        {
            w.WriteU8(deco.Enabled);
            w.WriteU8((byte)Math.Min(deco.Meshes.Count, MaxDecorationMeshes));
            foreach (AlpineRopeDecorationMesh slot in deco.Meshes.Take(MaxDecorationMeshes))
            {
                w.WriteVString(slot.Mesh);
                w.WriteVec3(slot.Offset);
                w.WriteF32(slot.Pitch);
                w.WriteF32(slot.Yaw);
                w.WriteF32(slot.Roll);
                w.WriteU8(slot.FxFlags);
                if (slot.HasGlare)
                {
                    w.WriteVString(slot.GlareBitmap);
                    w.WriteColor(slot.GlareColor);
                    w.WriteF32(slot.GlareConeAngle);
                    w.WriteF32(slot.GlareIntensity);
                    w.WriteF32(slot.GlareRadiusDistance);
                    w.WriteF32(slot.GlareRadiusScale);
                    w.WriteF32(slot.GlareDiminishDistance);
                    w.WriteVString(slot.VolumetricBitmap);
                    if (slot.VolumetricBitmap.Length != 0)
                    {
                        w.WriteF32(slot.VolumetricHeight);
                        w.WriteF32(slot.VolumetricLength);
                    }
                }

                if (slot.HasLight)
                {
                    w.WriteU8(slot.LightR);
                    w.WriteU8(slot.LightG);
                    w.WriteU8(slot.LightB);
                    w.WriteF32(slot.LightRadius);
                    w.WriteF32(slot.LightIntensity);
                }
            }

            w.WriteU8(deco.SpacingMode);
            w.WriteI32(deco.Count);
            w.WriteF32(deco.Spacing);
            w.WriteU8(deco.RandomOrder);
            w.WriteU8(deco.OrientMode);
        }

        w.WriteF32(x.ZipDownSpeed);
        w.WriteF32(x.ZipUpSpeed);
        w.WriteU8(x.ZipDirection);
    }
}
