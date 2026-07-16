using System;
using System.Linq;
using System.Numerics;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// ITEM 2 — directional-event facing arrows. Verifies the class set matches Alpine
/// (event.cpp:1249-1263) and that <see cref="SceneBuilder"/> emits an in-viewport arrow
/// from an oriented event's position, gated by the build toggle.
/// </summary>
public sealed class EventArrowSceneTests
{
    [Theory]
    [InlineData("Teleport", true)]
    [InlineData("Play_Vclip", true)]
    [InlineData("Teleport_Player", true)]
    [InlineData("AF_Teleport_Player", true)]
    [InlineData("Clone_Entity", true)]
    [InlineData("Anchor_Marker_Orient", true)]
    [InlineData("Alarm", false)] // persists orientation but is NOT arrowed (matches Alpine)
    [InlineData("Message", false)]
    [InlineData("Explode", false)]
    public void HasFacingArrow_Matches_The_Alpine_Class_Set(string className, bool expected)
    {
        Assert.Equal(expected, RflEvent.HasFacingArrow(className));
    }

    [Fact]
    public void Build_Emits_A_Facing_Arrow_From_An_Oriented_Event()
    {
        RflFile file = LevelWithEvents();
        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());

        // The Teleport at (5,0,0) is arrowed: some line starts at its position.
        Assert.Contains(scene.Lines, l => Approx(l.A, new Vector3(5, 0, 0)));

        // The non-oriented Message at (0,5,0) is not arrowed: no line starts there.
        Assert.DoesNotContain(scene.Lines, l => Approx(l.A, new Vector3(0, 5, 0)));
    }

    [Fact]
    public void EventFacingArrows_Toggle_Off_Suppresses_The_Arrow()
    {
        RflFile file = LevelWithEvents();
        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions { EventFacingArrows = false });
        Assert.DoesNotContain(scene.Lines, l => Approx(l.A, new Vector3(5, 0, 0)));
    }

    [Fact]
    public void Build_Uses_Identity_Forward_When_An_Oriented_Event_Has_No_Stored_Rotation()
    {
        // An oriented class loaded from an older file may lack a rotation matrix; the arrow
        // still draws along identity-forward (+Z), matching Alpine's default matrix behaviour.
        var events = new EventsSection();
        events.Events.Add(new RflEvent { Uid = 1, ClassName = "Teleport", Position = new Vec3(2, 3, 4), Rotation = null });
        RflFile file = Wrap(events);

        RenderScene scene = SceneBuilder.Build(file, new SceneBuildOptions());
        Assert.Contains(scene.Lines, l => Approx(l.A, new Vector3(2, 3, 4)) && l.B.Z > 4f); // shaft runs +Z
    }

    private static RflFile LevelWithEvents()
    {
        var events = new EventsSection();
        events.Events.Add(new RflEvent
        {
            Uid = 1, ClassName = "Teleport", Position = new Vec3(5, 0, 0), Rotation = Mat3.Identity,
        });
        events.Events.Add(new RflEvent
        {
            Uid = 2, ClassName = "Message", Position = new Vec3(0, 5, 0), Rotation = null,
        });
        return Wrap(events);
    }

    private static RflFile Wrap(EventsSection events)
    {
        var rfl = new RflFile();
        rfl.Sections.Add(new RflSection((uint)SectionType.Events, Array.Empty<byte>()) { Content = events, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static bool Approx(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
}
