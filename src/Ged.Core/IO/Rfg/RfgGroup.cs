using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.IO.Rfg;

/// <summary>
/// One group inside an .rfg file. Unlike the RFL <c>group</c> (which references
/// members by UID), an RFG group embeds the actual brushes and object sections
/// inline, reusing the RFL section body layouts. Its nav points have no
/// connection lists.
/// </summary>
public sealed class RfgGroup
{
    /// <summary>Chunk id for the Alpine per-brush metadata block.</summary>
    private const uint AlpineBrushInfoChunkId = (uint)SectionType.AlpineBrushInfo;

    public string Name { get; set; } = string.Empty;

    public byte IsMoving { get; set; }

    public MovingGroupData? MovingData { get; set; }

    public BrushesSection Brushes { get; set; } = new();

    public GeoRegionsSection GeoRegions { get; set; } = new();

    public LightsSection Lights { get; set; } = new(SectionType.Lights);

    public CutsceneCamerasSection CutsceneCameras { get; set; } = new();

    public CutscenePathNodesSection CutscenePathNodes { get; set; } = new();

    public AmbientSoundsSection AmbientSounds { get; set; } = new();

    public EventsSection Events { get; set; } = new();

    public MpRespawnPointsSection MpRespawnPoints { get; set; } = new();

    public List<NavPoint> NavPoints { get; set; } = new();

    public EntitiesSection Entities { get; set; } = new();

    public ItemsSection Items { get; set; } = new();

    public CluttersSection Clutters { get; set; } = new();

    public TriggersSection Triggers { get; set; } = new();

    public ParticleEmittersSection ParticleEmitters { get; set; } = new();

    public GasRegionsSection GasRegions { get; set; } = new();

    public DecalsSection Decals { get; set; } = new();

    public ClimbingRegionsSection ClimbingRegions { get; set; } = new();

    public RoomEffectsSection RoomEffects { get; set; } = new();

    public EaxEffectsSection EaxEffects { get; set; } = new();

    public BoltEmittersSection BoltEmitters { get; set; } = new();

    public TargetsSection Targets { get; set; } = new();

    public PushRegionsSection PushRegions { get; set; } = new();

    /// <summary>
    /// Alpine (version &gt;= 300) per-brush geoable/breakable metadata. Empty for
    /// stock groups. See docs/research/format-quirks.md for the caveat that the
    /// exact on-disk placement is unverified (no .rfg sample in the corpus).
    /// </summary>
    public List<AlpineBrushInfo> AlpineBrushInfos { get; set; } = new();

    public static RfgGroup Read(RfReader r, RflContext ctx)
    {
        var g = new RfgGroup
        {
            Name = r.ReadVString(),
            IsMoving = r.ReadU8(),
        };

        if (g.IsMoving != 0)
        {
            g.MovingData = GroupsSection.ReadMovingData(r);
        }

        g.Brushes = (BrushesSection)BrushesSection.Parse(r, ctx);
        g.GeoRegions = (GeoRegionsSection)GeoRegionsSection.Parse(r, ctx);
        g.Lights = (LightsSection)LightsSection.Parse(r, ctx);
        g.CutsceneCameras = (CutsceneCamerasSection)CutsceneCamerasSection.Parse(r, ctx);
        g.CutscenePathNodes = (CutscenePathNodesSection)CutscenePathNodesSection.Parse(r, ctx);
        g.AmbientSounds = (AmbientSoundsSection)AmbientSoundsSection.Parse(r, ctx);
        g.Events = (EventsSection)EventsSection.Parse(r, ctx);
        g.MpRespawnPoints = (MpRespawnPointsSection)MpRespawnPointsSection.Parse(r, ctx);

        int numNavPoints = r.ReadI32();
        for (int i = 0; i < numNavPoints; i++)
        {
            g.NavPoints.Add(NavPoint.Read(r));
        }

        g.Entities = (EntitiesSection)EntitiesSection.Parse(r, ctx);
        g.Items = (ItemsSection)ItemsSection.Parse(r, ctx);
        g.Clutters = (CluttersSection)CluttersSection.Parse(r, ctx);
        g.Triggers = (TriggersSection)TriggersSection.Parse(r, ctx);
        g.ParticleEmitters = (ParticleEmittersSection)ParticleEmittersSection.Parse(r, ctx);
        g.GasRegions = (GasRegionsSection)GasRegionsSection.Parse(r, ctx);
        g.Decals = (DecalsSection)DecalsSection.Parse(r, ctx);
        g.ClimbingRegions = (ClimbingRegionsSection)ClimbingRegionsSection.Parse(r, ctx);
        g.RoomEffects = (RoomEffectsSection)RoomEffectsSection.Parse(r, ctx);
        g.EaxEffects = (EaxEffectsSection)EaxEffectsSection.Parse(r, ctx);
        g.BoltEmitters = (BoltEmittersSection)BoltEmittersSection.Parse(r, ctx);
        g.Targets = (TargetsSection)TargetsSection.Parse(r, ctx);
        g.PushRegions = (PushRegionsSection)PushRegionsSection.Parse(r, ctx);

        if (ctx.Version >= 0x12C)
        {
            uint chunkId = r.ReadU32();
            if (chunkId != AlpineBrushInfoChunkId)
            {
                throw new RflFormatException(
                    $"Expected Alpine brush-info chunk 0x{AlpineBrushInfoChunkId:X8} in .rfg group, got 0x{chunkId:X8}.");
            }

            int count = (int)r.ReadU32();
            for (int i = 0; i < count; i++)
            {
                g.AlpineBrushInfos.Add(new AlpineBrushInfo
                {
                    BrushIndex = r.ReadU32(),
                    Flags = r.ReadU8(),
                    Material = r.ReadU8(),
                });
            }
        }

        return g;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteVString(Name);
        w.WriteU8(IsMoving);

        if (IsMoving != 0)
        {
            GroupsSection.WriteMovingData(w, MovingData!);
        }

        Brushes.Write(w, ctx);
        GeoRegions.Write(w, ctx);
        Lights.Write(w, ctx);
        CutsceneCameras.Write(w, ctx);
        CutscenePathNodes.Write(w, ctx);
        AmbientSounds.Write(w, ctx);
        Events.Write(w, ctx);
        MpRespawnPoints.Write(w, ctx);

        w.WriteI32(NavPoints.Count);
        foreach (NavPoint np in NavPoints)
        {
            np.Write(w);
        }

        Entities.Write(w, ctx);
        Items.Write(w, ctx);
        Clutters.Write(w, ctx);
        Triggers.Write(w, ctx);
        ParticleEmitters.Write(w, ctx);
        GasRegions.Write(w, ctx);
        Decals.Write(w, ctx);
        ClimbingRegions.Write(w, ctx);
        RoomEffects.Write(w, ctx);
        EaxEffects.Write(w, ctx);
        BoltEmitters.Write(w, ctx);
        Targets.Write(w, ctx);
        PushRegions.Write(w, ctx);

        if (ctx.Version >= 0x12C)
        {
            w.WriteU32(AlpineBrushInfoChunkId);
            w.WriteU32((uint)AlpineBrushInfos.Count);
            foreach (AlpineBrushInfo info in AlpineBrushInfos)
            {
                w.WriteU32(info.BrushIndex);
                w.WriteU8(info.Flags);
                w.WriteU8(info.Material);
            }
        }
    }
}
