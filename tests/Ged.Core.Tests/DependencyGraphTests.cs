using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.IO.Mesh;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.IO.Vpp;
using Ged.Core.Model;
using Ged.Core.Packaging;
using Ged.Core.Packaging.Graph;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Gates for the Dependency Graph panel: the level → category → file structure with
/// exact status/counts, nested indirect deps (mesh material textures, ATX frames)
/// as child edges, exact referencer provenance with jump UIDs, include-state
/// round-trip into <see cref="PackfileBuildPlan"/>, and refresh-after-edit.
/// </summary>
public sealed class DependencyGraphTests : IDisposable
{
    private readonly string _temp;
    private readonly string _loose;

    public DependencyGraphTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "ged_depgraph_" + Guid.NewGuid().ToString("N"));
        _loose = Path.Combine(_temp, "loose");
        Directory.CreateDirectory(_loose);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    [Fact]
    public void Graph_Has_Level_Root_Categories_And_Exact_Status_Counts()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");

            // Root.
            Assert.Equal("mylevel.rfl", g.Root.Label);
            Assert.Equal(DependencyNodeKind.Level, g.Root.NodeKind);

            // Categories present (this fixture: Textures, Meshes, Sounds, AtxChains).
            var cats = g.Categories.Select(c => c.Category).ToHashSet();
            Assert.Contains(DependencyCategory.Textures, cats);
            Assert.Contains(DependencyCategory.Meshes, cats);
            Assert.Contains(DependencyCategory.Sounds, cats);
            Assert.Contains(DependencyCategory.AtxChains, cats);

            // Every category has a tree edge from the root.
            foreach (DependencyGraphNode cat in g.Categories)
            {
                Assert.Contains(g.Edges, e => e.FromId == g.Root.Id && e.ToId == cat.Id && !e.Nested);
            }

            // Textures direct children: wall.tga (included), basewall.tga (skipped), ghost.tga (missing).
            // The mesh's material texture is NESTED, so not counted here.
            DependencyGraphNode textures = g.Categories.Single(c => c.Category == DependencyCategory.Textures);
            Assert.Equal(3, textures.Total);
            Assert.Equal(1, textures.IncludedCount);
            Assert.Equal(1, textures.SkippedCount);
            Assert.Equal(1, textures.MissingCount);

            // File statuses.
            Assert.Equal(DependencyStatus.Included, FileNode(g, "wall.tga").Status);
            Assert.Equal(DependencyStatus.BaseGameSkipped, FileNode(g, "basewall.tga").Status);
            Assert.Equal(DependencyStatus.Missing, FileNode(g, "ghost.tga").Status);
        }
    }

    [Fact]
    public void Indirect_Deps_Nest_Under_Their_Parent_As_Child_Edges()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");

            // The mesh's material texture is nested under the mesh node (child edge).
            DependencyGraphNode mesh = FileNode(g, "widget.v3m");
            var meshTexture = g.Files.FirstOrDefault(f => f.Nested && f.Dependency!.Parents.Contains("widget.v3m"));
            Assert.NotNull(meshTexture);
            Assert.Contains(g.Edges, e => e.FromId == mesh.Id && e.ToId == meshTexture!.Id && e.Nested);
            // A nested file is not a direct child of any category node.
            Assert.Null(g.CategoryOf(meshTexture!));

            // The ATX descriptor keeps its frame as a nested child.
            DependencyGraphNode atx = FileNode(g, "anim.atx");
            Assert.Equal(DependencyCategory.AtxChains, atx.Category);
            DependencyGraphNode frame = FileNode(g, "frame1.tga");
            Assert.True(frame.Nested);
            Assert.Contains(g.Edges, e => e.FromId == atx.Id && e.ToId == frame.Id && e.Nested);
        }
    }

    [Fact]
    public void File_Node_Lists_Exact_Referencers_With_Jump_Uids()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");

            // wall.tga is pulled in by brush UID 200.
            PackDependency wall = FileNode(g, "wall.tga").Dependency!;
            Assert.Contains(wall.Referers, r => r.Uid == 200);

            // amb.wav is pulled in by ambient sound UID 106.
            PackDependency amb = FileNode(g, "amb.wav").Dependency!;
            Assert.Contains(amb.Referers, r => r.Uid == 106);
        }
    }

    [Fact]
    public void Include_State_Round_Trips_Into_PackfileBuildPlan()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");
            var plan = new PackfileBuildPlan(scan, "mylevel.rfl",
                PackfileBuildPlan.DefaultOutputPath(@"C:\rf", "mylevel.rfl", multiplayer: false));

            // The node's dependency maps to exactly one plan item (the checkbox binding).
            PackDependency wall = FileNode(g, "wall.tga").Dependency!;
            PackfileBuildItem item = plan.AllItems.Single(i => ReferenceEquals(i.Dependency, wall));

            Assert.True(item.Include);
            Assert.Contains(wall, plan.Selection);

            item.Include = false; // toggle the graph checkbox off
            Assert.DoesNotContain(wall, plan.Selection);

            item.Include = true;
            Assert.Contains(wall, plan.Selection);
        }
    }

    [Fact]
    public void Refresh_After_Adding_A_Textured_Face_Shows_A_New_Node()
    {
        File.WriteAllBytes(Path.Combine(_loose, "wall.tga"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_loose, "extra.tga"), new byte[] { 1 });
        using var vfs = new AssetVfs(new IAssetSource[] { new DirectoryAssetSource(_loose) });

        var rfl = NewLevel();
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { BrushWith(200, "wall.tga") } });

        DependencyGraph before = DependencyGraphModel.Build(
            DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs)), "mylevel.rfl");
        Assert.DoesNotContain(before.Files, f => f.Label == "extra.tga");

        // Author a new textured face and re-scan (the panel's Refresh button).
        ((BrushesSection)rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().First())
            .Brushes.Add(BrushWith(201, "extra.tga"));

        DependencyGraph after = DependencyGraphModel.Build(
            DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs)), "mylevel.rfl");
        Assert.Contains(after.Files, f => f.Label == "extra.tga");
    }

    [Fact]
    public void Collapsing_A_Category_Hides_Its_File_Subtree_From_The_Visible_Set()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");

            DependencyGraphNode textures = g.Categories.Single(c => c.Category == DependencyCategory.Textures);
            var texFileIds = g.Files
                .Where(f => !f.Nested && f.Category == DependencyCategory.Textures)
                .Select(f => f.Id)
                .ToHashSet();
            Assert.NotEmpty(texFileIds);

            DependencyGraph collapsed = g.Collapse(new HashSet<DependencyCategory> { DependencyCategory.Textures });

            // The category node itself stays (still carrying its counts / badge)…
            DependencyGraphNode stillThere = collapsed.Categories.Single(c => c.Category == DependencyCategory.Textures);
            Assert.Equal(textures.Total, stillThere.Total);
            Assert.Equal(textures.MissingCount, stillThere.MissingCount);

            // …but none of its direct files remain, and no edge touches them.
            Assert.DoesNotContain(collapsed.Nodes, n => texFileIds.Contains(n.Id));
            Assert.DoesNotContain(collapsed.Edges, e => texFileIds.Contains(e.FromId) || texFileIds.Contains(e.ToId));

            // The root → Textures tree edge survives (the category is still shown).
            Assert.Contains(collapsed.Edges, e => e.FromId == g.Root.Id && e.ToId == textures.Id && !e.Nested);

            // Other categories' files are untouched.
            Assert.Contains(collapsed.Files, f => f.Label.Equals("amb.wav", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(collapsed.Files, f => f.Label.Equals("widget.v3m", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Collapsing_A_Category_Also_Hides_Files_Nested_Under_Its_Files()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");

            // anim.atx is a direct AtxChains file; frame1.tga is nested beneath it.
            Assert.False(FileNode(g, "anim.atx").Nested);
            Assert.True(FileNode(g, "frame1.tga").Nested);

            DependencyGraph collapsed = g.Collapse(new HashSet<DependencyCategory> { DependencyCategory.AtxChains });

            // Both the direct file and its nested descendant are gone from the visible set.
            Assert.DoesNotContain(collapsed.Files, f => f.Label.Equals("anim.atx", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(collapsed.Files, f => f.Label.Equals("frame1.tga", StringComparison.OrdinalIgnoreCase));

            // The AtxChains category node remains.
            Assert.Contains(collapsed.Categories, c => c.Category == DependencyCategory.AtxChains);
        }
    }

    [Fact]
    public void Collapse_With_No_Categories_Returns_The_Graph_Unchanged()
    {
        if (TestPaths.FixtureFile("mesh", "wallcomputer1.v3m") is null)
        {
            return; // retail-derived mesh fixture not present
        }

        (RflFile rfl, AssetVfs vfs) = BuildFixture();
        using (vfs)
        {
            var scan = DependencyScanner.Scan(rfl, new VfsDependencyResolver(vfs));
            DependencyGraph g = DependencyGraphModel.Build(scan, "mylevel.rfl");

            DependencyGraph same = g.Collapse(new HashSet<DependencyCategory>());
            Assert.Equal(g.Nodes.Count, same.Nodes.Count);
            Assert.Equal(g.Edges.Count, same.Edges.Count);

            // Collapsing then "expanding" (collapse an empty set) recovers every file.
            int fullFiles = g.Files.Count();
            DependencyGraph collapsed = g.Collapse(new HashSet<DependencyCategory> { DependencyCategory.Textures });
            Assert.True(collapsed.Files.Count() < fullFiles);
            Assert.Equal(fullFiles, g.Collapse(new HashSet<DependencyCategory>()).Files.Count());
        }
    }

    // ─── Fixture ─────────────────────────────────────────────────────────────

    private (RflFile Rfl, AssetVfs Vfs) BuildFixture()
    {
        // Loose (included) files.
        foreach (string f in new[] { "wall.tga", "water.tga", "amb.wav", "frame1.tga" })
        {
            File.WriteAllBytes(Path.Combine(_loose, f), new byte[] { 1, 2, 3, 4 });
        }

        File.WriteAllText(Path.Combine(_loose, "anim.atx"), "[[frame]]\nfile = \"frame1.tga\"\n");

        // A real mesh with material textures (its diffuse maps become nested deps).
        // Callers guard on this fixture being present, so it resolves here (tests/fixtures or
        // research/fixtures) or the caller has already skipped.
        string meshSrc = TestPaths.FixtureFile("mesh", "wallcomputer1.v3m")!;
        File.Copy(meshSrc, Path.Combine(_loose, "widget.v3m"));
        V3dFile mesh = V3dReader.Read(File.ReadAllBytes(meshSrc));
        foreach (string t in mesh.Submeshes.SelectMany(sm => sm.Materials.Select(m => m.DiffuseMapName))
                     .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(Path.Combine(_loose, t), new byte[] { 7 });
        }

        // Base-game VPP (skipped) file.
        string baseVpp = Path.Combine(_temp, "base.vpp");
        new VppBuilder().Add("basewall.tga", new byte[] { 9 }).Write(baseVpp);

        var rfl = NewLevel();
        var brush = BrushWith(200, "wall.tga");
        brush.Geometry.Textures.Add("basewall.tga"); // base VPP -> skipped
        brush.Geometry.Textures.Add("ghost.tga");    // nowhere -> missing
        brush.Geometry.Textures.Add("anim");         // -> anim.atx -> frame1.tga
        AddSection(rfl, SectionType.Brushes, new BrushesSection { Brushes = { brush } });

        AddSection(rfl, SectionType.AlpineMeshObjects, new AlpineMeshObjectsSection
        {
            Meshes = { new AlpineMeshObject { Uid = 105, MeshFilename = "widget.v3m" } },
        });

        AddSection(rfl, SectionType.AmbientSounds, new AmbientSoundsSection
        {
            Sounds = { new AmbientSound { Uid = 106, SoundFileName = "amb.wav" } },
        });

        var vfs = new AssetVfs(new IAssetSource[]
        {
            new DirectoryAssetSource(_loose),
            VppAssetSource.Open(baseVpp),
        });
        return (rfl, vfs);
    }

    private static DependencyGraphNode FileNode(DependencyGraph g, string name) =>
        g.Files.Single(f => f.Label.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static RflFile NewLevel()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0x12D;
        rfl.Header.LevelName = "mylevel.rfl";
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return rfl;
    }

    private static Brush BrushWith(int uid, string texture)
    {
        var b = new Brush { Uid = uid };
        b.Geometry.Textures.Add(texture);
        return b;
    }

    private static void AddSection(RflFile rfl, SectionType type, IRflSectionContent content)
    {
        var s = new RflSection((uint)type, Array.Empty<byte>()) { Content = content, Dirty = true };
        rfl.Sections.Insert(rfl.Sections.Count - 1, s);
    }
}
