using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

public class LinkValidationTests
{
    private static EditorDocument NewDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xB4;
        rfl.Header.LevelName = "linktest";
        return new EditorDocument(rfl);
    }

    private static LevelObject Place(EditorDocument doc, LevelObjectKind kind) =>
        doc.PlaceObject(kind, Vec3.Zero)!;

    private static LevelObject PlaceEvent(EditorDocument doc, string className) =>
        doc.PlaceEvent(EventSchemaCatalog.Find(className)!, Vec3.Zero)!;

    [Fact]
    public void Trigger_And_Clutter_And_NavPoint_And_Event_Can_Originate()
    {
        var doc = NewDoc();
        Assert.True(LinkModel.CanOriginate(Place(doc, LevelObjectKind.Trigger)));
        Assert.True(LinkModel.CanOriginate(Place(doc, LevelObjectKind.Clutter)));
        Assert.True(LinkModel.CanOriginate(Place(doc, LevelObjectKind.NavPoint)));
        Assert.True(LinkModel.CanOriginate(PlaceEvent(doc, "Delay")));
    }

    [Fact]
    public void Entities_And_Items_Cannot_Originate_Links()
    {
        var doc = NewDoc();
        var entity = Place(doc, LevelObjectKind.Entity);
        var item = Place(doc, LevelObjectKind.Item);
        var trigger = Place(doc, LevelObjectKind.Trigger);

        Assert.False(LinkRules.Validate(entity, trigger).Ok);
        Assert.Equal(LinkRules.OriginatorMessage, LinkRules.Validate(entity, trigger).Message);
        Assert.False(LinkRules.Validate(item, trigger).Ok);
    }

    [Fact]
    public void Trigger_To_Event_Is_Allowed_And_NavPoint_To_Event_Is_Allowed()
    {
        var doc = NewDoc();
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var navpoint = Place(doc, LevelObjectKind.NavPoint);
        var ev = PlaceEvent(doc, "Delay");

        Assert.True(LinkRules.Validate(trigger, ev).Ok);
        Assert.True(LinkRules.Validate(navpoint, ev).Ok);
    }

    [Fact]
    public void Self_Link_And_Duplicate_Link_Are_Rejected()
    {
        var doc = NewDoc();
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");
        var links = new LinkService(doc);

        Assert.False(LinkRules.Validate(trigger, trigger).Ok);

        Assert.True(links.LinkOneToMany(trigger, new[] { ev }).Ok);
        // Second identical link is a duplicate.
        Assert.False(LinkRules.Validate(trigger, ev).Ok);
        Assert.False(links.LinkOneToMany(trigger, new[] { ev }).Ok);
    }

    [Fact]
    public void Event_Target_Kind_Is_Validated_Against_The_Catalog()
    {
        var doc = NewDoc();
        var playSound = PlaceEvent(doc, "Play_Sound"); // targets Object
        var clutter = Place(doc, LevelObjectKind.Clutter);
        var otherEvent = PlaceEvent(doc, "Delay");

        Assert.True(LinkRules.Validate(playSound, clutter).Ok);   // clutter is a physical object
        Assert.False(LinkRules.Validate(playSound, otherEvent).Ok); // an event is not an "object" target

        // Particle_State only links to particle emitters.
        var particleState = PlaceEvent(doc, "Particle_State");
        var emitter = Place(doc, LevelObjectKind.ParticleEmitter);
        Assert.True(LinkRules.Validate(particleState, emitter).Ok);
        Assert.False(LinkRules.Validate(particleState, clutter).Ok);
    }

    [Fact]
    public void Link_One_To_Many_Adds_Links_And_Is_Undoable()
    {
        var doc = NewDoc();
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var e1 = PlaceEvent(doc, "Delay");
        var e2 = PlaceEvent(doc, "Invert");
        var links = new LinkService(doc);

        Assert.True(links.LinkOneToMany(trigger, new[] { e1, e2 }).Ok);
        var triggerModel = (Trigger)trigger.Model;
        Assert.Equal(new[] { e1.Uid, e2.Uid }, triggerModel.Links);

        doc.Undo.Undo();
        Assert.Empty(triggerModel.Links);
    }

    [Fact]
    public void Break_All_Links_Removes_Incoming_And_Outgoing()
    {
        var doc = NewDoc();
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var ev = PlaceEvent(doc, "Delay");
        var downstream = PlaceEvent(doc, "Invert");
        var links = new LinkService(doc);

        links.LinkOneToMany(trigger, new[] { ev });        // trigger -> event
        links.LinkOneToMany(ev, new[] { downstream });     // event -> downstream event

        // Breaking on the event clears its outgoing links and the trigger's link to it.
        Assert.True(links.BreakAllLinks(new[] { ev }));
        Assert.Empty(((RflEvent)ev.Model).Links);
        Assert.DoesNotContain(ev.Uid, ((Trigger)trigger.Model).Links);
    }

    [Fact]
    public void Back_Link_Links_From_Targets_To_Primary()
    {
        var doc = NewDoc();
        var primary = PlaceEvent(doc, "Delay");
        var trigger = Place(doc, LevelObjectKind.Trigger);
        var links = new LinkService(doc);

        Assert.True(links.BackLink(primary, new[] { trigger }).Ok);
        Assert.Contains(primary.Uid, ((Trigger)trigger.Model).Links);
    }
}
