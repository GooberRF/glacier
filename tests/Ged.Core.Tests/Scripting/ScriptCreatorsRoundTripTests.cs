using System;
using System.IO;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.Model;
using Ged.Core.Prefabs;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// One script that uses every creating function of the API — brushes, every placement form, the
/// Player Start, movers and keyframes, groups, a prefab, tags, level properties and the design kit —
/// then save → reopen: the reopened level re-saves byte-identically and a second script finds
/// everything again by name and tag.
/// </summary>
public sealed class ScriptCreatorsRoundTripTests
{
    private const string Author =
        "local kit = ged.import('kit')\n" +
        "level.props:set_many{level_name = 'All Creators', multiplayer = true}\n" +
        "local hall = kit.room{name = 'hall', pos = {0, 0, 0}, size = {16, 6, 16}}\n" +
        "local box = level.brush{shape = 'cylinder', pos = {-4, 1, -4}, size = {2, 2, 2}, sides = 8, tags = {part = 'drum'}}\n" +
        "local slab = level.place_box(4, 0.25, 4, 2, 0.5, 2)\n" +
        "local door = level.brush{pos = {0, 1.5, 6}, size = {3, 3, 0.25}, tags = {part = 'door'}}\n" +
        "local m = level.create_mover{name = 'door1', brushes = {door}, props = {is_door = true}}\n" +
        "m:add_keyframe{offset = {0, 3, 0}, props = {pause_time = 2}}\n" +
        "local light = level.place{kind = 'light', pos = {0, 4, 0}, name = 'lamp', props = {size_range = 12}, tags = {part = 'lamp'}}\n" +
        "local trig = level.place{kind = 'trigger', pos = {0, 1, 4}, name = 'door1_trigger', links = {m}}\n" +
        "local ev = level.place{kind = 'event', class = 'Explode', pos = {2, 1, 2}, name = 'boom'}\n" +
        "local spawn = level.place{kind = 'respawn', pos = {-6, 1, 6}, yaw = 90, name = 'spawn_a'}\n" +
        "level.place_event('Explode', 3, 1, 3)\n" +
        "level.place('Light', 5, 4, 5)\n" +
        "level.player_start{pos = {0, 1, -6}, yaw = 0}\n" +
        "level.create_group{name = 'props_group', brushes = {box, slab}}\n" +
        "prefab.place{name = 'Crate', pos = {6, 0, -6}, yaw = 90, tags = {part = 'crate'}}\n" +
        "kit.light_room{room = hall, name = 'hall_extra', spacing = 16}\n";

    private const string Check =
        "assert(level.props:get('level_name') == 'All Creators')\n" +
        "local m = level.find_mover('door1') assert(m and #m.keyframes == 2 and m:get('is_door'))\n" +
        "assert(level.objects_named('lamp')[1]:get('size_range') == 12)\n" +
        "assert(level.objects_named('door1_trigger')[1]:links()[1] == m.start_keyframe.uid)\n" +
        "assert(level.objects_named('boom')[1].class_name == 'Explode')\n" +
        "assert(math.abs(level.objects_named('spawn_a')[1].rot.yaw - 90) < 1e-3)\n" +
        "assert(level.player_start().pos.z == -6)\n" +
        "assert(#level.find_group('props_group').brushes == 2)\n" +
        "assert(level.brushes_tagged('part', 'drum').count == 1 and level.brushes_tagged('part', 'crate').count == 1)\n" +
        "assert(level.objects_tagged('part', 'crate').count == 1 and level.objects_tagged('part', 'lamp').count == 1)\n" +
        "assert(level.brushes_tagged('kit.name', 'hall').count == 1 and level.objects_tagged('kit.name', 'hall_extra').count == 1)\n" +
        "assert(#level.objects_of('Event') == 2)\n" +
        "return level.brush_count .. ':' .. level.count";

    private sealed class Library : IDisposable
    {
        public Library()
        {
            Root = Directory.CreateTempSubdirectory("ged-creators-").FullName;
            EditorDocument src = ScriptObjectFieldTests.NewLevel();
            int brush = new BrushEditor(src).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 1, Height = 1, Depth = 1 }, new Vec3(0, 0.5f, 0), Mat3.Identity);
            LevelObject light = src.PlaceObject(LevelObjectKind.Light, new Vec3(0, 2, 0))!;
            RfgFile rfg = RfgInterop.Export(src, new[] { brush }, new[] { light.Uid }, alpine: true, groupName: "Crate");
            RfgInterop.TransformInPlace(rfg, Mat3.Identity, RfgInterop.ComputePivot(rfg).Scale(-1f));
            PrefabPackage.Save(Path.Combine(Root, "crate.gedprefab"),
                new PrefabManifest { Name = "Crate", Description = "fixture", Author = "test", BrushCount = 1, ObjectCount = 1, PivotBased = true }, rfg, null);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void Every_Creator_Survives_Save_And_Reopen()
    {
        using var lib = new Library();
        Func<EditorDocument, ScriptServices> services = d => new ScriptServices
        {
            Document = d,
            Confirmation = new AllowAllConfirmation(),
            PrefabsDirectory = lib.Root,
        };

        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        (ScriptRunResult authored, _) = ScriptTestFixture.Run(doc, Author, services: services);
        Assert.True(authored.Success, authored.Error?.ToDisplayString());
        (ScriptRunResult before, _) = ScriptTestFixture.Run(doc, Check, services: services);
        Assert.True(before.Success, before.Error?.ToDisplayString());

        byte[] saved = doc.SaveToBytes(updateTimestamp: false);
        EditorDocument reopened = EditorDocument.OpenBytes(saved, "creators.rfl");
        Assert.Equal(saved, reopened.SaveToBytes(updateTimestamp: false));
        (ScriptRunResult after, _) = ScriptTestFixture.Run(reopened, Check, services: services);
        Assert.True(after.Success, after.Error?.ToDisplayString());
        Assert.Equal(before.ReturnValue, after.ReturnValue);
    }
}
