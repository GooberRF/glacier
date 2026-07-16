using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

public class EventCatalogTests
{
    /// <summary>The 58 Alpine event class names (events.tbl AF_* sections / event.h ids 100-157).</summary>
    private static readonly string[] AlpineNames =
    {
        "Set_Variable", "Clone_Entity", "Set_Player_World_Collide", "Switch_Random", "Difficulty_Gate", "HUD_Message",
        "Play_Video", "Set_Level_Hardness", "Sequence", "Clear_Queued", "Remove_Link", "Route_Node", "Add_Link",
        "Valid_Gate", "Goal_Math", "Goal_Gate", "Scope_Gate", "Inside_Gate", "Anchor_Marker", "Force_Unhide",
        "Set_Difficulty", "Set_Fog_Far_Clip", "AF_When_Dead", "Gametype_Gate", "When_Picked_Up", "Set_Skybox",
        "Set_Life", "Set_Debris", "Set_Fog_Color", "Set_Entity_Flag", "AF_Teleport_Player", "Set_Item_Drop", "AF_Heal",
        "Anchor_Marker_Orient", "Light_State", "World_HUD_Sprite", "Set_Light_Color", "Capture_Point_Handler",
        "Respawn_Point_State", "Modify_Respawn_Point", "When_Captured", "Set_Capture_Point_Owner", "Owner_Gate",
        "Set_Gameplay_Rule", "When_Round_Ends", "Mesh_Animate", "Mesh_Set_Texture", "Mesh_Set_Collision",
        "AF_Fullscreen_Image", "AF_Fullscreen_Color", "Unhide_Glare", "Gas_Region_State", "Modify_Gas_Region",
        "Resize_Gas_Region", "ATX_Set_Frame", "ATX_Play", "ATX_Pause", "ATX_Set_Frame_Time",
    };

    private static readonly string[] OrientationClasses =
    {
        "Teleport", "Alarm", "Teleport_Player", "Play_Vclip", "AF_Teleport_Player", "Clone_Entity", "Anchor_Marker_Orient",
    };

    private static readonly string[] NonForwarding =
    {
        "Remove_Object", "Invert", "Switch", "Set_AI_Mode", "Delay", "Particle_State", "UnHide",
    };

    [Fact]
    public void Catalog_Has_All_148_Classes_With_Unique_Game_Ids()
    {
        Assert.Equal(148, EventSchemaCatalog.All.Count);
        Assert.Equal(148, EventSchemaCatalog.All.Select(e => e.GameId).Distinct().Count());

        // Stock ids 0-89 (90) and Alpine ids 100-157 (58) each present exactly once.
        Assert.Equal(90, EventSchemaCatalog.All.Count(e => e.GameId is >= 0 and <= 89));
        Assert.Equal(58, EventSchemaCatalog.All.Count(e => e.GameId is >= 100 and <= 157));
        foreach (int id in Enumerable.Range(0, 90))
        {
            Assert.NotNull(EventSchemaCatalog.FindById(id));
        }

        foreach (int id in Enumerable.Range(100, 58))
        {
            Assert.NotNull(EventSchemaCatalog.FindById(id));
        }
    }

    [Fact]
    public void Every_Stock_events_tbl_Leaf_Has_A_Schema()
    {
        if (TestPaths.Tables is null)
        {
            return;
        }

        EventCatalog tree = EventCatalog.Load(File.ReadAllBytes(Path.Combine(TestPaths.Tables, "events.tbl")));
        foreach (EventDef leaf in tree.Events.Where(e => e.Name != "Trigger Event"))
        {
            Assert.True(EventSchemaCatalog.Find(leaf.Name) is not null,
                $"events.tbl leaf '{leaf.Name}' has no EventSchema.");
        }
    }

    [Fact]
    public void Every_Alpine_Event_Has_An_Alpine_Schema()
    {
        Assert.Equal(58, AlpineNames.Length);
        foreach (string name in AlpineNames)
        {
            EventSchema? s = EventSchemaCatalog.Find(name);
            Assert.True(s is not null, $"Alpine event '{name}' missing from catalog.");
            Assert.True(s!.IsAlpine, $"'{name}' should be flagged Alpine.");
            Assert.Equal(300, s.MinVersion);
        }
    }

    [Fact]
    public void Placeable_Set_Excludes_The_Three_Auto_Classes()
    {
        Assert.False(EventSchemaCatalog.Find("Play_Custom_Animation")!.Placeable);
        Assert.False(EventSchemaCatalog.Find("Follow_Player")!.Placeable);
        Assert.False(EventSchemaCatalog.Find("Set_Light_State")!.Placeable);
        Assert.Equal(145, EventSchemaCatalog.Placeable.Count());
    }

    [Fact]
    public void Exactly_The_Seven_Directional_Classes_Carry_Orientation()
    {
        var actual = EventSchemaCatalog.All.Where(e => e.HasOrientation).Select(e => e.ClassName).OrderBy(n => n);
        Assert.Equal(OrientationClasses.OrderBy(n => n), actual);
    }

    [Fact]
    public void Non_Forwarding_Stock_Events_Are_Flagged()
    {
        foreach (string name in NonForwarding)
        {
            Assert.False(EventSchemaCatalog.Find(name)!.ForwardsSignal, $"{name} must not forward.");
        }

        // A representative forwarding event still forwards.
        Assert.True(EventSchemaCatalog.Find("Cyclic_Timer")!.ForwardsSignal);
    }

    [Fact]
    public void Browser_Tree_Has_Stock_And_Alpine_Categories()
    {
        var cats = EventSchemaCatalog.Placeable.Select(e => e.Category).Distinct().ToList();
        foreach (string c in new[] { "AI_Actions", "Level", "Modifiers", "Catalysts", "Special",
            "AF_General", "AF_Flow", "AF_Utility", "AF_Catalysts", "AF_Gameplay" })
        {
            Assert.Contains(c, cats);
        }
    }

    [Theory]
    [InlineData("Teleport", 0xB4)]
    [InlineData("Alarm", 0xB4)]
    [InlineData("Teleport_Player", 0xB4)]
    [InlineData("Play_Vclip", 0xB4)]
    [InlineData("AF_Teleport_Player", 0x12C)]
    [InlineData("Clone_Entity", 0x12C)]
    [InlineData("Anchor_Marker_Orient", 0x12D)]
    public void Directional_Classes_Round_Trip_Their_Orientation(string className, int version)
    {
        EventSchema schema = EventSchemaCatalog.Find(className)!;
        RflEvent ev = EventFactory.Create(schema, 1000, new Vec3(1, 2, 3), version);

        // A distinctive, non-identity orientation.
        var rot = new Mat3(new Vec3(0, 0, 1), new Vec3(0, 1, 0), new Vec3(-1, 0, 0));
        ev.Rotation = rot;

        RflEvent back = RoundTrip(ev, version);
        Assert.NotNull(back.Rotation);
        Assert.Equal(rot, back.Rotation!.Value);
    }

    [Fact]
    public void Non_Directional_Event_Persists_No_Orientation()
    {
        EventSchema schema = EventSchemaCatalog.Find("Play_Sound")!;
        RflEvent ev = EventFactory.Create(schema, 1, Vec3.Zero, 0xB4);
        Assert.Null(RoundTrip(ev, 0xB4).Rotation);
    }

    [Fact]
    public void Field_Access_Encodes_The_Slot_Traps()
    {
        // Message: int-from-float trap — the "extra id" integer is stored in float1.
        EventSchema message = EventSchemaCatalog.Find("Message")!;
        var ev = new RflEvent { ClassName = "Message" };
        EventFieldAccess.Set(message.Field(EventSlot.Float1)!, ev, 42);
        Assert.Equal(42f, ev.Float1);
        Assert.Equal(42, EventFieldAccess.Get(message.Field(EventSlot.Float1)!, ev));

        // Goto: int1==1 → bool serialized as 1/0.
        EventSchema goto_ = EventSchemaCatalog.Find("Goto")!;
        EventFieldSpec faceWhenDone = goto_.Field(EventSlot.Int1)!;
        EventFieldAccess.Set(faceWhenDone, ev, true);
        Assert.Equal(1, ev.Int1);
        Assert.Equal(true, EventFieldAccess.Get(faceWhenDone, ev));
        EventFieldAccess.Set(faceWhenDone, ev, false);
        Assert.Equal(0, ev.Int1);

        // Skybox_State: single-char str flag.
        EventSchema skybox = EventSchemaCatalog.Find("Skybox_State")!;
        EventFieldSpec onFlag = skybox.Field(EventSlot.Str1)!;
        EventFieldAccess.Set(onFlag, ev, true);
        Assert.Equal("1", ev.Str1);
        Assert.Equal(true, EventFieldAccess.Get(onFlag, ev));
        EventFieldAccess.Set(onFlag, ev, false);
        Assert.Equal(string.Empty, ev.Str1);

        // Modify_Rotating_Mover: keyword dropdown saved as text.
        EventSchema mrm = EventSchemaCatalog.Find("Modify_Rotating_Mover")!;
        EventFieldSpec mode = mrm.Field(EventSlot.Str1)!;
        EventFieldAccess.Set(mode, ev, "Increase");
        Assert.Equal("Increase", ev.Str1);
    }

    private static RflEvent RoundTrip(RflEvent ev, int version)
    {
        var section = new EventsSection();
        section.Events.Add(ev);
        var ctx = new RflContext(version);
        var w = new RfWriter();
        section.Write(w, ctx);
        var parsed = (EventsSection)EventsSection.Parse(new RfReader(w.ToArray()), ctx);
        return parsed.Events[0];
    }
}
