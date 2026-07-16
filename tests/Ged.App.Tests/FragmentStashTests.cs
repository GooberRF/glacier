using System;
using System.Collections.Generic;
using System.IO;
using Ged.App;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// Item 8 regression: the per-document build-overlay stash (clipped-face survival map +
/// compiled fragment index) is keyed by brush UID and holds world-space geometry from
/// the level that was BUILT. It must be dropped whenever the document changes —
/// previously it survived Open/New, so a freshly opened level rendered the previous
/// level's fragments as phantom faces (and hid the wrong faces) wherever brush UIDs
/// collided across documents, which is effectively always.
/// </summary>
public sealed class FragmentStashTests
{
    private static void StashOverlays(EditorSession session)
    {
        session.BrushFaceSurvival = new Dictionary<int, bool[]> { [42] = new[] { false, true } };
        session.BrushFragments = BrushFragmentIndex.Build(
            new Geometry(), new Dictionary<int, int>(), new Dictionary<int, bool[]>());
        Assert.NotNull(session.BrushFaceSurvival);
        Assert.NotNull(session.BrushFragments);
    }

    [Fact]
    public void New_Level_Drops_The_Previous_Documents_Fragment_Stash()
    {
        var session = new EditorSession();
        session.NewLevel();
        StashOverlays(session); // simulate a build of the first document

        session.NewLevel(); // document switch

        Assert.Null(session.BrushFaceSurvival);
        Assert.Null(session.BrushFragments);
    }

    [Fact]
    public void Open_Level_Drops_The_Previous_Documents_Fragment_Stash()
    {
        var session = new EditorSession();
        session.NewLevel();
        string temp = Path.Combine(Path.GetTempPath(), $"ged_stash_{Guid.NewGuid():N}.rfl");
        session.Document!.Save(temp);
        try
        {
            StashOverlays(session);

            session.OpenLevel(temp); // document switch via open

            Assert.Null(session.BrushFaceSurvival);
            Assert.Null(session.BrushFragments);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
