using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Assets;
using Ged.Core.Editor;
using Ged.Core.Scripting;
using Ged.Core.Tests.Scripting;
using Xunit;

namespace Ged.Core.Tests.Organic;

/// <summary>
/// Texture provenance (ORGANIC-GEOMETRY-SPEC §2.6) over a synthetic VFS — a loose folder, a mod packfile,
/// alpinefaction.vpp and a fake retail <c>maps1.vpp</c>, mounted in that priority order: classification,
/// overrides through the supercede chain, the table form of <c>catalog.textures</c> (filters, sorting,
/// limit), <c>catalog.texture_info</c>, <c>assets.texture_sources</c>, and the no-install behaviour.
/// </summary>
public sealed class TextureProvenanceTests
{
    private sealed class FakeSource : IAssetSource
    {
        private readonly HashSet<string> _files;

        public FakeSource(string description, AssetSourceKind kind, params string[] files)
        {
            Description = description;
            Kind = kind;
            _files = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        }

        public string Description { get; }

        public AssetSourceKind Kind { get; }

        public string? Category => null;

        public bool Contains(string name) => name is not null && _files.Contains(name);

        public byte[]? Read(string name) => Contains(name) ? new byte[] { 1, 2, 3 } : null; // not a decodable image

        public long? GetSize(string name) => Contains(name) ? 3 : null;

        public IEnumerable<string> EnumerateFiles() => _files;

        public void Rescan()
        {
        }
    }

    private static AssetVfs Vfs() => new(new IAssetSource[]
    {
        new FakeSource(System.IO.Path.Combine("C:", "games", "rf", "user_maps", "textures"), AssetSourceKind.LooseDirectory, "rock02.dds", "mytex.tga"),
        new FakeSource("coolmod.vpp", AssetSourceKind.Packfile, "modwall.tga", "floor01.tga"),
        new FakeSource("alpinefaction.vpp", AssetSourceKind.Packfile, "af_glow.tga"),
        new FakeSource("maps1.vpp", AssetSourceKind.Packfile, "rock02.tga", "floor01.tga", "wall01.tga"),
    });

    [Theory]
    [InlineData("audio.vpp", true)]
    [InlineData("MAPS1.VPP", true)]
    [InlineData("maps_en.vpp", true)]
    [InlineData("levelsm.vpp", true)]
    [InlineData("tables.vpp", true)]
    [InlineData("alpinefaction.vpp", false)]
    [InlineData("levelspf.vpp", false)]
    [InlineData("meshes - Copy.vpp", false)]
    [InlineData("coolmod.vpp", false)]
    public void Stock_Packfiles_Are_Identified_By_Name(string name, bool stock) =>
        Assert.Equal(stock, StockContent.IsStockPackfile(name));

    [Fact]
    public void Describe_Classifies_Origin_Stock_And_Overrides()
    {
        AssetVfs vfs = Vfs();
        TextureProvenance wall = StockContent.Describe(vfs, "wall01.tga")!;
        Assert.Equal((true, AssetOrigin.Stock, "maps1.vpp", false), (wall.Stock, wall.Origin, wall.Source, wall.Overridden));

        TextureProvenance rock = StockContent.Describe(vfs, "rock02.tga")!; // .dds supersedes the retail .tga
        Assert.Equal(("rock02.dds", true, AssetOrigin.Loose, "textures", true), (rock.ResolvedName, rock.Stock, rock.Origin, rock.Source, rock.Overridden));

        TextureProvenance floor = StockContent.Describe(vfs, "floor01.tga")!; // the mod pack wins the mount order
        Assert.Equal((true, AssetOrigin.Packfile, "coolmod.vpp", true), (floor.Stock, floor.Origin, floor.Source, floor.Overridden));

        TextureProvenance glow = StockContent.Describe(vfs, "af_glow.tga")!;
        Assert.Equal((false, AssetOrigin.Alpine, false), (glow.Stock, glow.Origin, glow.Overridden));

        TextureProvenance mine = StockContent.Describe(vfs, "mytex.tga")!;
        Assert.Equal((false, AssetOrigin.Loose), (mine.Stock, mine.Origin));
        Assert.Null(StockContent.Describe(vfs, "nothere.tga"));
    }

    private static (ScriptRunResult Result, ScriptLog Log) Run(string script, AssetVfs? vfs)
    {
        EditorDocument doc = LevelTemplates.CreateNew(KitTestHarness.FixedDate);
        return ScriptTestFixture.Run(doc, script, services: d => ScriptTestFixture.Services(d) with { Assets = vfs });
    }

    [Fact]
    public void Catalog_Texture_Info_And_Table_Form_Filter_And_Sort()
    {
        (ScriptRunResult r, _) = Run(
            "local t = catalog.texture_info('rock02.tga')\n" +
            "assert(t.exists and t.stock and t.overridden and t.origin == 'loose' and t.source == 'textures' and t.width == 0)\n" +
            "assert(catalog.texture_info('nothere.tga') == nil)\n" +
            "local all = catalog.textures{}\n" +
            "local names = {} for i, x in ipairs(all) do names[i] = x.name end\n" +
            "assert(table.concat(names, ',') == 'af_glow.tga,floor01.tga,modwall.tga,mytex.tga,rock02.dds,rock02.tga,wall01.tga', table.concat(names, ','))\n" +
            "local stock = catalog.textures{stock = true}\n" +
            "local s = {} for i, x in ipairs(stock) do s[i] = x.name end\n" +
            "assert(table.concat(s, ',') == 'floor01.tga,rock02.dds,rock02.tga,wall01.tga', table.concat(s, ','))\n" +
            "assert(#catalog.textures{stock = false, origin = 'alpine'} == 1)\n" +
            "assert(#catalog.textures{origin = 'stock'} == 1)\n" +
            "assert(#catalog.textures{filter = 'rock', limit = 1} == 1)\n" +
            "assert(#catalog.textures{filter = '*01.tga'} == 2)\n" +
            "assert(type(catalog.textures('rock')[1]) == 'string')\n" +
            "assert(#catalog.textures() == 7 and type(catalog.textures()[1]) == 'string')", Vfs());
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }

    [Fact]
    public void Table_Form_Rejects_Bad_Options()
    {
        (ScriptRunResult r, _) = Run("catalog.textures{origin = 'stok'}", Vfs());
        Assert.False(r.Success);
        Assert.Equal("catalog.textures: unknown origin 'stok'.", r.Error!.Message);
        Assert.Equal("Did you mean: stock?", r.Error.Hint);

        (ScriptRunResult r2, _) = Run("catalog.textures{limt = 3}", Vfs());
        Assert.False(r2.Success);
        Assert.Equal("catalog.textures: unknown option 'limt'.", r2.Error!.Message);
    }

    [Fact]
    public void Texture_Sources_Lists_What_The_Level_Uses()
    {
        (ScriptRunResult r, _) = Run(
            "level.brush{shape = 'box', pos = {0, 2, 0}, size = {4, 4, 4}, textures = {floor = 'floor01.tga', wall = 'mytex.tga', ceiling = 'gone.tga'}}\n" +
            "local by = {} for _, t in ipairs(assets.texture_sources()) do by[t.name] = t end\n" +
            "assert(by['floor01.tga'].stock and by['floor01.tga'].origin == 'packfile')\n" +
            "assert(not by['mytex.tga'].stock and by['mytex.tga'].origin == 'loose')\n" +
            "assert(by['gone.tga'].exists == false and by['gone.tga'].origin == nil)", Vfs());
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }

    [Fact]
    public void Without_An_Install_Lookups_Are_Empty()
    {
        (ScriptRunResult r, _) = Run(
            "assert(catalog.texture_info('rock02.tga') == nil)\n" +
            "assert(#catalog.textures{stock = true} == 0)\n" +
            "level.brush{shape = 'box', size = {4, 4, 4}, texture = 'rock02.tga'}\n" +
            "local s = assets.texture_sources()\nassert(#s == 1 and s[1].name == 'rock02.tga' and not s[1].exists and not s[1].stock)", null);
        Assert.True(r.Success, r.Error?.ToDisplayString());
    }
}
