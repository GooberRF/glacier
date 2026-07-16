using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Room-effect billboards in the built scene: each room_effects marker emits the RoomFX
/// ("waves") billboard, pickable by its own UID as a <see cref="PickKind.Object"/> — the
/// same handle the pick -> FindByUid -> select path resolves to.
/// </summary>
public sealed class RoomEffectRenderTests
{
    private static RflFile LevelWithRoomEffects()
    {
        var effects = new List<RoomEffect>
        {
            new()
            {
                EffectType = RoomEffectsSection.EffectSkyRoom,
                Header = new ObjectHeader { Uid = 7001, Position = new Vec3(0, 0, 0) },
            },
            new()
            {
                EffectType = RoomEffectsSection.EffectLiquidRoom,
                LiquidProperties = new RoomEffectLiquidProperties { Waveform = 1, LiquidType = 1 },
                Header = new ObjectHeader { Uid = 7002, Position = new Vec3(5, 1, 2) },
            },
        };

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Header.LevelName = "roomfx_render.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.RoomEffects, Array.Empty<byte>())
        {
            Content = new RoomEffectsSection { Effects = effects },
            Dirty = true,
        });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    [Fact]
    public void Room_Effects_Emit_Pickable_RoomFx_Billboards()
    {
        RenderScene scene = SceneBuilder.Build(LevelWithRoomEffects(), new SceneBuildOptions());

        var billboards = scene.Billboards.Where(b => b.Kind == BillboardKind.RoomEffect).ToList();
        Assert.Equal(2, billboards.Count);
        Assert.All(billboards, b => Assert.Equal((int)EditorIcon.RoomEffect, b.Icon));
        Assert.All(billboards, b => Assert.Equal(PickKind.Object, b.PickId.Kind));

        // Each room effect is pickable by its own UID.
        Assert.Contains(billboards, b => b.PickId.Index == 7001);
        Assert.Contains(billboards, b => b.PickId.Index == 7002);
    }
}
