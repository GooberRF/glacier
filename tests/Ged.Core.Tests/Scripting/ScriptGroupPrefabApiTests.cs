using System;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO.Rfg;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Prefabs;
using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>
/// User groups and the <c>prefab</c> global: create/add/remove/rename/dissolve
/// as single undo steps, the prefab library listing, placement of a fixture prefab built in the test
/// (one undo node through the nested transaction), name resolution errors, and confinement of file
/// access to the library folder.
/// </summary>
public sealed class ScriptGroupPrefabApiTests
{
    private static EditorDocument MakeLevel()
    {
        EditorDocument doc = ScriptObjectFieldTests.NewLevel();
        var be = new BrushEditor(doc);
        for (int i = 0; i < 3; i++)
        {
            be.CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 1, Height = 1, Depth = 1 }, new Vec3(i * 2, 0.5f, 0), Mat3.Identity);
        }

        doc.PlaceObject(LevelObjectKind.Light, new Vec3(0, 3, 0));
        doc.MarkSaved();
        return doc;
    }

    private static GroupsSection? UserGroups(EditorDocument d) =>
        d.Rfl.Sections.FirstOrDefault(s => s.TypeId == (uint)SectionType.Groups)?.Content as GroupsSection;

    // ---- groups -------------------------------------------------------------------------

    [Fact]
    public void Create_Group_Is_One_Undo_Step_On_A_Level_Without_Groups()
    {
        ScriptMutationAssert.AssertMutation(MakeLevel,
            "local q = level.all_brushes()\n" +
            "local g = level.create_group{name = 'stairs1', brushes = {q:get(1), q:get(2).uid}, objects = {level.objects_of('Light')[1]}}\n" +
            "assert(g.exists and g.name == 'stairs1' and #g.brushes == 2 and #g.objects == 1)\n" +
            "assert(level.find_group('STAIRS1') ~= nil and #level.groups == 1)",
            (d, _) =>
            {
                Group g = UserGroups(d)!.Groups.Single();
                Assert.Equal("stairs1", g.Name);
                Assert.Equal(2, g.Brushes.Count);
                Assert.Single(g.Objects);
            });
    }

    [Fact]
    public void Group_Add_Remove_Rename_Dissolve()
    {
        EditorDocument Make()
        {
            EditorDocument d = MakeLevel();
            new GroupService(d).CreateGroup("g", new[] { new BrushEditor(d).Brushes[0].Uid }, Array.Empty<int>());
            d.MarkSaved();
            return d;
        }

        ScriptMutationAssert.AssertMutation(Make,
            "local g = level.groups[1]\n" +
            "local q = level.all_brushes()\n" +
            "g:add{brushes = {q:get(2), q:get(3)}, objects = {level.objects_of('Light')[1]}}\n" +
            "assert(#g.brushes == 3 and #g.objects == 1)\n" +
            "g:remove{q:get(1), level.objects_of('Light')[1].uid}\n" +
            "g:rename('renamed')\n" +
            "assert(#g.brushes == 2 and #g.objects == 0 and g.name == 'renamed')",
            (d, _) => Assert.Equal("renamed", UserGroups(d)!.Groups.Single().Name));

        ScriptMutationAssert.AssertMutation(Make, "local g = level.groups[1] g:dissolve() assert(not g.exists)",
            (d, _) => Assert.Empty(UserGroups(d)!.Groups));

        EditorDocument doc = Make();
        ScriptApiV2ContractTests.AssertApiError(doc, "level.create_group{name = 'x'}", "give at least one member", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "level.create_group{brushes = {level.objects_of('Light')[1]}}", "takes brushes", "objects");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.create_group{objects = {level.all_brushes():get(1)}}", "takes objects", "brushes");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.create_group{brushes = {1}, member = 2}", "unknown option 'member'", "Valid options");
        ScriptApiV2ContractTests.AssertApiError(doc, "level.groups[1]:rename('  ')", "must not be empty", null);
        ScriptApiV2ContractTests.AssertApiError(doc, "local g = level.groups[1] g:dissolve() g:rename('x')", "This group no longer exists.", null);
    }

    // ---- prefabs --------------------------------------------------------------------------

    /// <summary>A prefab library folder with prefabs exported from a small level (a brush + a light).</summary>
    private sealed class Library : IDisposable
    {
        public Library()
        {
            Root = Directory.CreateTempSubdirectory("ged-prefabs-").FullName;
            Write("crate_stack.gedprefab", "Crate Stack");
            Directory.CreateDirectory(Path.Combine(Root, "a"));
            Directory.CreateDirectory(Path.Combine(Root, "b"));
            Write(Path.Combine("a", "pillar.gedprefab"), "Pillar");
            Write(Path.Combine("b", "pillar.gedprefab"), "Pillar");
            File.WriteAllText(Path.Combine(Root, "broken.gedprefab"), "not a zip");
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

        private void Write(string relative, string name)
        {
            EditorDocument src = ScriptObjectFieldTests.NewLevel();
            int brush = new BrushEditor(src).CreateBrush(new BrushCreateParams { Shape = BrushShape.Box, Width = 1, Height = 1, Depth = 1 }, new Vec3(0, 0.5f, 0), Mat3.Identity);
            LevelObject light = src.PlaceObject(LevelObjectKind.Light, new Vec3(0, 2, 0))!;
            RfgFile rfg = RfgInterop.Export(src, new[] { brush }, new[] { light.Uid }, alpine: true, groupName: name);
            RfgInterop.TransformInPlace(rfg, Mat3.Identity, RfgInterop.ComputePivot(rfg).Scale(-1f));
            var manifest = new PrefabManifest { Name = name, Description = "fixture", Author = "test", BrushCount = 1, ObjectCount = 1, PivotBased = true };
            PrefabPackage.Save(Path.Combine(Root, relative), manifest, rfg, null);
        }
    }

    private static Func<EditorDocument, ScriptServices> In(Library lib) => d => new ScriptServices
    {
        Document = d,
        Confirmation = new AllowAllConfirmation(),
        PrefabsDirectory = lib.Root,
    };

    [Fact]
    public void List_Is_Sorted_And_Skips_Unreadable_Files_With_One_Warning()
    {
        using var lib = new Library();
        (ScriptRunResult r, ScriptLog log) = ScriptTestFixture.Run(MakeLevel(),
            "local t = {} for _, p in ipairs(prefab.list()) do t[#t+1] = p.name .. '@' .. p.file .. '#' .. p.brush_count .. p.object_count .. p.author end return table.concat(t, ';')",
            services: In(lib));
        Assert.True(r.Success, r.Error?.ToDisplayString());
        Assert.Equal("Crate Stack@crate_stack.gedprefab#11test;Pillar@a/pillar.gedprefab#11test;Pillar@b/pillar.gedprefab#11test", r.ReturnValue);
        string w = Assert.Single(log.Entries, e => e.Level == ScriptLogLevel.Warning).Message;
        Assert.Contains("broken.gedprefab", w, StringComparison.Ordinal);

        // A missing library is simply empty.
        (r, _) = ScriptTestFixture.Run(MakeLevel(), "return #prefab.list()", services: d => new ScriptServices
        {
            Document = d,
            PrefabsDirectory = Path.Combine(lib.Root, "no-such-folder"),
        });
        Assert.Equal("0", r.ReturnValue);
    }

    [Fact]
    public void Place_Is_One_Undo_Step_And_Tags_Every_Member()
    {
        using var lib = new Library();
        ScriptMutationAssert.AssertMutation(MakeLevel,
            "local inst = prefab.place{name = 'crate stack', pos = {10, 0, 4}, yaw = 90, tags = {kit = 'prefab'}}\n" +
            "assert(inst.name == 'Crate Stack' and #inst.uids == 2 and #inst.brushes == 1 and #inst.objects == 1)\n" +
            "assert(inst.pos.x == 10 and inst.pos.z == 4)\n" +
            "for _, b in ipairs(inst.brushes) do assert(b:tags().kit == 'prefab') end\n" +
            "for _, o in ipairs(inst.objects) do assert(o:tags().kit == 'prefab' and o.kind == 'Light') end\n" +
            "return inst.uids[1]",
            (d, res) =>
            {
                PrefabInstanceRecord rec = new PrefabInstanceService(d).Instances.Single();
                Assert.Equal("Crate Stack", rec.PrefabName);
                Assert.Equal(rec.MemberUids.OrderBy(u => u).ToArray(), res.CreatedUids);
                Assert.Equal(4, new BrushEditor(d).Brushes.Count);
            },
            services: In(lib));

        // By library file, too.
        (ScriptRunResult byFile, _) = ScriptTestFixture.Run(MakeLevel(), "return prefab.place{name = 'b/pillar.gedprefab', pos = {0, 0, 0}}.name", services: In(lib));
        Assert.True(byFile.Success, byFile.Error?.ToDisplayString());
        Assert.Equal("Pillar", byFile.ReturnValue);
    }

    [Theory]
    [InlineData("prefab.place{name = 'Crate Stak', pos = {0,0,0}}", "no prefab named 'Crate Stak'", "Crate Stack")]
    [InlineData("prefab.place{name = 'Pillar', pos = {0,0,0}}", "several prefabs are named 'Pillar': a/pillar.gedprefab, b/pillar.gedprefab", "a/pillar.gedprefab")]
    [InlineData("prefab.place{name = '../outside.gedprefab', pos = {0,0,0}}", "is a path outside the prefab library", "library folder")]
    [InlineData("prefab.place{name = 'a/../crate_stack.gedprefab', pos = {0,0,0}}", "is a path outside the prefab library", null)]
    [InlineData("prefab.place{name = 'C:/Windows/x.gedprefab', pos = {0,0,0}}", "is a path outside the prefab library", null)]
    [InlineData("prefab.place{name = 'Crate Stack'}", "'pos' is required", null)]
    [InlineData("prefab.place{name = 'Crate Stack', pos = {0,0,0}, at = 1}", "unknown option 'at'", null)]
    [InlineData("prefab.place{name = 'Crate Stack', pos = {0,0,0}, yaw = 1, rot = {yaw = 2}}", "either 'rot' or 'yaw'", null)]
    public void Place_Errors_Are_Actionable(string src, string message, string? hint)
    {
        using var lib = new Library();
        (ScriptRunResult r, _) = ScriptTestFixture.Run(MakeLevel(), src, services: In(lib));
        Assert.False(r.Success, src);
        Assert.Equal(ScriptErrorKind.Api, r.Error!.Kind);
        Assert.Contains(message, r.Error.Message, StringComparison.Ordinal);
        if (hint is not null)
        {
            Assert.Contains(hint, r.Error.Hint ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_Rooted_Path_Never_Reads_A_File_Outside_The_Library()
    {
        using var lib = new Library();
        using var other = new Library(); // a valid prefab that exists outside the configured library
        string outside = Path.Combine(other.Root, "crate_stack.gedprefab").Replace('\\', '/');
        (ScriptRunResult r, _) = ScriptTestFixture.Run(MakeLevel(), $"prefab.place{{name = '{outside}', pos = {{0,0,0}}}}", services: In(lib));
        Assert.False(r.Success);
        Assert.Contains("outside the prefab library", r.Error!.Message, StringComparison.Ordinal);
    }
}
