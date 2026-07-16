using Ged.Core.IO.Rfl.Sections;

namespace Ged.Core.IO.Rfl;

/// <summary>
/// Maps section type ids to parsers. A section whose type has no registered
/// parser stays an opaque blob (its raw bytes round-trip verbatim).
/// </summary>
public static class RflSectionRegistry
{
    /// <summary>Parses a section body into a model.</summary>
    public delegate IRflSectionContent Parser(RfReader reader, RflContext context);

    private static readonly Dictionary<uint, Parser> Parsers = new()
    {
        [(uint)SectionType.StaticGeometry] = GeometrySection.ParseStatic,
        [(uint)SectionType.GeoRegions] = GeoRegionsSection.Parse,
        [(uint)SectionType.Lights] = LightsSection.Parse,
        [(uint)SectionType.EditorOnlyLights] = LightsSection.ParseEditorOnly,
        [(uint)SectionType.CutsceneCameras] = CutsceneCamerasSection.Parse,
        [(uint)SectionType.AmbientSounds] = AmbientSoundsSection.Parse,
        [(uint)SectionType.Events] = EventsSection.Parse,
        [(uint)SectionType.MpRespawnPoints] = MpRespawnPointsSection.Parse,
        [(uint)SectionType.LevelProperties] = LevelPropertiesSection.Parse,
        [(uint)SectionType.ParticleEmitters] = ParticleEmittersSection.Parse,
        [(uint)SectionType.GasRegions] = GasRegionsSection.Parse,
        [(uint)SectionType.RoomEffects] = RoomEffectsSection.Parse,
        [(uint)SectionType.ClimbingRegions] = ClimbingRegionsSection.Parse,
        [(uint)SectionType.BoltEmitters] = BoltEmittersSection.Parse,
        [(uint)SectionType.Targets] = TargetsSection.Parse,
        [(uint)SectionType.Decals] = DecalsSection.Parse,
        [(uint)SectionType.PushRegions] = PushRegionsSection.Parse,
        [(uint)SectionType.Lightmaps] = LightmapsSection.Parse,
        [(uint)SectionType.Movers] = MoversSection.Parse,
        [(uint)SectionType.MovingGroups] = GroupsSection.ParseMoving,
        [(uint)SectionType.Cutscenes] = CutscenesSection.Parse,
        [(uint)SectionType.CutscenePathNodes] = CutscenePathNodesSection.Parse,
        [(uint)SectionType.CutscenePaths] = CutscenePathsSection.Parse,
        [(uint)SectionType.TgaFiles] = FileListSection.ParseTga,
        [(uint)SectionType.VcmFiles] = FileListSection.ParseVcm,
        [(uint)SectionType.MvfFiles] = FileListSection.ParseMvf,
        [(uint)SectionType.V3dFiles] = FileListSection.ParseV3d,
        [(uint)SectionType.VfxFiles] = FileListSection.ParseVfx,
        [(uint)SectionType.EaxEffects] = EaxEffectsSection.Parse,
        [(uint)SectionType.WaypointLists] = WaypointListsSection.Parse,
        [(uint)SectionType.NavPoints] = NavPointsSection.Parse,
        [(uint)SectionType.Entities] = EntitiesSection.Parse,
        [(uint)SectionType.Items] = ItemsSection.Parse,
        [(uint)SectionType.Clutters] = CluttersSection.Parse,
        [(uint)SectionType.Triggers] = TriggersSection.Parse,
        [(uint)SectionType.PlayerStart] = PlayerStartSection.Parse,
        [(uint)SectionType.LevelInfo] = LevelInfoSection.Parse,
        [(uint)SectionType.Brushes] = BrushesSection.Parse,
        [(uint)SectionType.Groups] = GroupsSection.Parse,
        [(uint)SectionType.AlpineLevelProperties] = AlpineLevelPropertiesSection.Parse,
        [(uint)SectionType.AlpineMeshObjects] = AlpineMeshObjectsSection.Parse,
        [(uint)SectionType.AlpineNoteObjects] = AlpineNoteObjectsSection.Parse,
        [(uint)SectionType.AlpineCoronaObjects] = AlpineCoronaObjectsSection.Parse,
        [(uint)SectionType.AlpineBagObjects] = AlpineBagObjectsSection.Parse,
        [(uint)SectionType.DashLevelProperties] = DashLevelPropertiesSection.Parse,
        [(uint)SectionType.GedPrefabInstances] = GedPrefabInstancesSection.Parse,
        [(uint)SectionType.GedObjectMetadata] = GedObjectMetadataSection.Parse,
    };

    public static bool HasParser(uint typeId) => Parsers.ContainsKey(typeId);

    /// <summary>
    /// Parses <paramref name="section"/> into a model if a parser is registered
    /// for its type. Throws if the parser does not consume the section exactly.
    /// </summary>
    public static bool TryParse(RflSection section, RflContext context, out IRflSectionContent? content)
    {
        if (!Parsers.TryGetValue(section.TypeId, out Parser? parser))
        {
            content = null;
            return false;
        }

        var reader = new RfReader(section.RawBytes);
        content = parser(reader, context);
        if (reader.Position != section.RawBytes.Length)
        {
            throw new RflFormatException(
                $"Parser for section 0x{section.TypeId:X8} consumed {reader.Position} of " +
                $"{section.RawBytes.Length} bytes (leftover {reader.Remaining}).");
        }

        return true;
    }
}
