using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Ged.App;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Xunit;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Tests;

/// <summary>
/// P1 — a marquee box-select over a large object set must be O(n), not O(n²). The reported freeze
/// (the app recovered on its own after ~8 minutes, so a finite accumulation storm, not a deadlock)
/// was per-item selection: the old <c>ApplyMarquee</c> called <c>SelectObject</c>/<c>SelectBrush</c>
/// once per caught item, and each raised a <c>SelectionChanged</c> that fans out to a full
/// Outliner/Properties/LinkGraph rebuild — so N caught items ⇒ N complete panel rebuilds. The fix
/// routes the whole catch through the router's BATCH paths (<see cref="SelectionRouter.SelectObjects"/>
/// / <see cref="SelectionRouter.SelectBrushes"/>), which mutate once and raise a single event each.
/// These tests pin the batched invariant (one event, not N) and time the batched catch over ctf06's
/// full object set.
/// </summary>
public sealed class MarqueeBatchSelectionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public MarqueeBatchSelectionTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>ctf06 from the example corpus, or null when the corpus is absent (CI without it).</summary>
    private static string? Ctf06Path()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        string path = Path.Combine(dir.FullName, "research", "example_rfls", "ctf06.rfl");
        return File.Exists(path) ? path : null;
    }

    [AvaloniaFact]
    public void Batch_Object_Selection_Raises_A_Single_Event_Not_One_Per_Item()
    {
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        session.ActiveSelectKinds = SelectKinds.Objects;

        var objects = new List<LevelObject>();
        for (int i = 0; i < 500; i++)
        {
            objects.Add(doc.PlaceObject(LevelObjectKind.Light, new CoreVec3(i, 0, 0))!);
        }

        int events = 0;
        doc.SelectionChanged += () => events++;

        Assert.True(session.Selection.SelectObjects(objects, additive: true));

        Assert.Equal(500, doc.Selection.Count);
        Assert.Equal(1, events); // the whole marquee catch is ONE mutation, not 500 events
    }

    [AvaloniaFact]
    public void Batch_Brush_Selection_Raises_A_Single_Event_Not_One_Per_Item()
    {
        var session = new EditorSession();
        session.NewLevel();
        BrushEditor be = session.BrushEditor!;
        session.ActiveSelectKinds = SelectKinds.Brushes;

        var uids = new List<int>();
        for (int i = 0; i < 400; i++)
        {
            uids.Add(be.CreateBrush(new BrushCreateParams { Width = 2, Height = 2, Depth = 2 },
                new CoreVec3(i * 3, 0, 0), Ged.Core.Model.Mat3.Identity));
        }

        int events = 0;
        be.SelectionChanged += () => events++;

        Assert.True(session.Selection.SelectBrushes(uids, additive: true));

        Assert.Equal(400, be.SelectedBrushes.Count);
        Assert.Equal(1, events);
    }

    [AvaloniaFact]
    public void Per_Item_Selection_Fans_Out_One_Event_Per_Object_The_Storm_The_Batch_Avoids()
    {
        // Documents the regression the batch path prevents: selecting one at a time raises an event
        // per object, and every event drove a full panel rebuild — the O(n²) freeze.
        var session = new EditorSession();
        session.NewLevel();
        EditorDocument doc = session.Document!;
        session.ActiveSelectKinds = SelectKinds.Objects;

        var objects = new List<LevelObject>();
        for (int i = 0; i < 300; i++)
        {
            objects.Add(doc.PlaceObject(LevelObjectKind.Light, new CoreVec3(i, 0, 0))!);
        }

        int events = 0;
        doc.SelectionChanged += () => events++;

        foreach (LevelObject o in objects)
        {
            session.Selection.SelectObject(o, additive: true);
        }

        Assert.Equal(300, events); // one per item — exactly what the batch marquee collapses to one
    }

    [AvaloniaFact]
    public void Marquee_Over_Ctf06_Full_Object_Set_Is_One_Event_And_Completes_In_Low_Seconds()
    {
        string? path = Ctf06Path();
        if (path is null)
        {
            return; // corpus absent — nothing to time
        }

        var session = new EditorSession();
        session.OpenLevel(path);
        EditorDocument doc = session.Document!;
        session.ActiveSelectKinds = SelectKinds.Objects;

        var allObjects = doc.Objects.ToList();
        Assert.True(allObjects.Count > 50, $"ctf06 should carry a substantial object set (got {allObjects.Count}).");

        // A representative fan-out cost: every selection-change event iterates the CURRENT selection,
        // exactly as the real overlay/link refresh does. Under the OLD per-item path this ran once per
        // caught object with a growing selection (Σk = O(n²)); the batched path fires it exactly once.
        int events = 0;
        long checksum = 0;
        doc.SelectionChanged += () =>
        {
            events++;
            foreach (LevelObject o in doc.Selection)
            {
                checksum += o.Uid;
            }
        };

        var sw = Stopwatch.StartNew();
        Assert.True(session.Selection.SelectObjects(allObjects, additive: true));
        sw.Stop();

        _out.WriteLine($"ctf06 objects={allObjects.Count}, batched marquee select elapsed={sw.ElapsedMilliseconds} ms, events={events}");

        Assert.Equal(1, events); // ONE event for the whole box-select catch
        Assert.Equal(allObjects.Count, doc.Selection.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Batched marquee selection of {allObjects.Count} objects took {sw.ElapsedMilliseconds} ms (bound 2000 ms).");
        _ = checksum;
    }
}
