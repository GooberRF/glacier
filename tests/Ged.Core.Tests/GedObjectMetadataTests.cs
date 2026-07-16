using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.Lighting;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Item 4 — the GED-only <c>ged_object_metadata</c> chunk (0x6ED00002): section round-trip
/// (known + UNKNOWN block types preserved opaquely), byte-identity when no metadata exists or
/// when metadata is set then cleared, and the undo-safe LightCookie wiring through
/// <see cref="GedObjectMetadataService"/>.
/// </summary>
public sealed class GedObjectMetadataTests
{
    private static EditorDocument EmptyDoc()
    {
        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));
        return new EditorDocument(rfl);
    }

    private static byte[] Vstring(string s)
    {
        var w = new RfWriter(s.Length + 2);
        w.WriteVString(s);
        return w.ToArray();
    }

    // ---- Section round-trip (incl. unknown-block preservation) ---------------

    [Fact]
    public void Section_Round_Trips_Known_And_Unknown_Block_Types()
    {
        var section = new GedObjectMetadataSection();
        section.Entries.Add(new GedObjectMetadataRecord
        {
            Uid = 42,
            Blocks =
            {
                new GedObjectMetadataBlock(GedMetadataType.LightCookie, Vstring("cookies/grid.tga")),

                // A block type GED does not know — must survive verbatim (forward compat).
                new GedObjectMetadataBlock { MetadataType = 0xDEAD, Payload = new byte[] { 1, 2, 3, 4, 5 } },
            },
        });
        section.Entries.Add(new GedObjectMetadataRecord { Uid = 7, Blocks = { new GedObjectMetadataBlock(GedMetadataType.LightCookie, Vstring("beam.png")) } });

        var rfl = new RflFile();
        rfl.Header.Version = 0xC8;
        rfl.Sections.Add(new RflSection((uint)SectionType.GedObjectMetadata, Array.Empty<byte>()) { Content = section, Dirty = true });
        rfl.Sections.Add(new RflSection((uint)SectionType.End, Array.Empty<byte>()));

        byte[] bytes = rfl.Save();
        RflFile reloaded = RflFile.Load(bytes);
        reloaded.ParseAllKnownSections();

        GedObjectMetadataSection back = reloaded.Sections
            .Select(s => s.Content).OfType<GedObjectMetadataSection>().Single();
        Assert.Equal(2, back.Entries.Count);

        GedObjectMetadataRecord e42 = back.Entries.Single(e => e.Uid == 42);
        Assert.Equal(2, e42.Blocks.Count);
        Assert.Equal("cookies/grid.tga", new RfReader(e42.Blocks[0].Payload).ReadVString());

        GedObjectMetadataBlock unknown = e42.Blocks.Single(b => b.MetadataType == 0xDEAD);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, unknown.Payload); // opaque preservation
    }

    [Fact]
    public void Reserialize_Is_Byte_Identical_Including_Unknown_Blocks()
    {
        var section = new GedObjectMetadataSection();
        section.Entries.Add(new GedObjectMetadataRecord
        {
            Uid = 5,
            Blocks = { new GedObjectMetadataBlock { MetadataType = 0x9001, Payload = new byte[] { 9, 8, 7 } } },
        });
        var w1 = new RfWriter(64);
        section.Write(w1, new RflContext(0));
        byte[] raw = w1.ToArray();

        // parse(raw) then write → identical bytes.
        var reparsed = (GedObjectMetadataSection)GedObjectMetadataSection.Parse(new RfReader(raw), new RflContext(0));
        var w2 = new RfWriter(64);
        reparsed.Write(w2, new RflContext(0));
        Assert.Equal(raw, w2.ToArray());
    }

    // ---- Byte identity when absent / cleared ---------------------------------

    [Fact]
    public void No_Chunk_Written_When_There_Is_No_Metadata()
    {
        EditorDocument doc = EmptyDoc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);

        var svc = new GedObjectMetadataService(doc);
        Assert.Null(svc.Cookie(1)); // reading must not create the chunk

        byte[] after = doc.SaveToBytes(updateTimestamp: false);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Set_Then_Clear_Cookie_Restores_Byte_Identity()
    {
        EditorDocument doc = EmptyDoc();
        byte[] before = doc.SaveToBytes(updateTimestamp: false);
        var svc = new GedObjectMetadataService(doc);

        svc.SetCookie(9, "cookies/spot.tga");
        Assert.Equal("cookies/spot.tga", svc.Cookie(9));
        Assert.NotEqual(before, doc.SaveToBytes(updateTimestamp: false)); // chunk now present

        svc.SetCookie(9, null); // clear it — the now-empty chunk must be dropped
        Assert.Null(svc.Cookie(9));
        Assert.Equal(before, doc.SaveToBytes(updateTimestamp: false)); // byte-identical again
    }

    // ---- Undo + read-back ----------------------------------------------------

    [Fact]
    public void SetCookie_Is_Undoable()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new GedObjectMetadataService(doc);

        svc.SetCookie(3, "cookies/window.tga");
        Assert.Equal("cookies/window.tga", svc.Cookie(3));

        doc.Undo.Undo();
        Assert.Null(svc.Cookie(3));

        doc.Undo.Redo();
        Assert.Equal("cookies/window.tga", svc.Cookie(3));
    }

    // ---- Inspector wiring: the cookie a user sets reaches the baker resolver -------

    [Fact]
    public void Inspector_Cookie_Set_And_Clear_Drive_The_Baker_Resolver()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new GedObjectMetadataService(doc); // what MainWindow.Get/SetLightCookie delegate to

        // (Light inspector reads it) — none yet.
        Assert.Null(svc.Cookie(7));

        // (inspector Browse… → SetLightCookie) — the cookie is stored...
        svc.SetCookie(7, "spot.tga");
        Assert.Equal("spot.tga", svc.Cookie(7));

        // ...and the baker builds its per-light resolver from the same metadata.
        Func<int, LightCookie?>? resolver = LightCookies.BuildResolver(
            svc.AllCookies(),
            _ => (2, 1, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }),
            onMissing: null);
        Assert.NotNull(resolver);
        Assert.NotNull(resolver!(7)); // light 7 is baked with a cookie
        Assert.Null(resolver!(8));    // an unrelated light is not

        // (inspector Clear → SetLightCookie(null)) — the cookie is gone, so the bake has no cookies.
        svc.SetCookie(7, null);
        Assert.Null(svc.Cookie(7));
        Assert.Null(LightCookies.BuildResolver(svc.AllCookies(), _ => null, null));
    }

    [Fact]
    public void AllCookies_Returns_Every_Light_Cookie_Mapping()
    {
        EditorDocument doc = EmptyDoc();
        var svc = new GedObjectMetadataService(doc);
        svc.SetCookie(3, "a.tga");
        svc.SetCookie(8, "b.png");

        IReadOnlyDictionary<int, string> map = svc.AllCookies();
        Assert.Equal(2, map.Count);
        Assert.Equal("a.tga", map[3]);
        Assert.Equal("b.png", map[8]);
    }
}
