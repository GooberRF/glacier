using System.IO;
using System.Text;
using Ged.Core.Editing;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Tables;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Byte-exact spot-checks that the catalog's slot mapping lands values in the
/// right generic-record slots. Expected bytes are built independently with a
/// raw <see cref="BinaryWriter"/>, so these fail if a semantic field is ever
/// routed to the wrong slot (the disassembly-confirmed traps for Message,
/// Music_Start, Skybox_State, and the two confirmed events Play_Sound/Swap_Textures).
/// </summary>
public class EventSerializationTests
{
    private const int V = 0xB4; // events carry a trailing color at >= 0xB0
    private static readonly Vec3 Pos = new(1f, 2f, 3f);
    private static readonly RfColor Col = new(11, 22, 33, 255);

    [Fact]
    public void Play_Sound_Maps_File_AltFile_MinDist_Duration_Loop_Speaker()
    {
        RflEvent ev = Blank("Play_Sound", 500);
        EventSchema s = EventSchemaCatalog.Find("Play_Sound")!;
        Set(ev, s, EventSlot.Str1, "fire.wav");
        Set(ev, s, EventSlot.Str2, "alt.wav");
        Set(ev, s, EventSlot.Float1, 10f);
        Set(ev, s, EventSlot.Float2, 2f);
        Set(ev, s, EventSlot.Bool1, true);
        Set(ev, s, EventSlot.Int1, 42);

        byte[] expected = Record(500, "Play_Sound", 1, 0, 42, 0, 10f, 2f, "fire.wav", "alt.wav");
        Assert.Equal(expected, Serialize(ev));
    }

    [Fact]
    public void Swap_Textures_Maps_Two_Indices_And_Two_Files()
    {
        RflEvent ev = Blank("Swap_Textures", 501);
        EventSchema s = EventSchemaCatalog.Find("Swap_Textures")!;
        Set(ev, s, EventSlot.Int1, 3);
        Set(ev, s, EventSlot.Int2, 7);
        Set(ev, s, EventSlot.Str1, "a.tga");
        Set(ev, s, EventSlot.Str2, "b.tga");

        byte[] expected = Record(501, "Swap_Textures", 0, 0, 3, 7, 0f, 0f, "a.tga", "b.tga");
        Assert.Equal(expected, Serialize(ev));
    }

    [Fact]
    public void Message_Puts_Extra_Id_Integer_Into_Float1_And_Index_Into_Int1()
    {
        RflEvent ev = Blank("Message", 502);
        EventSchema s = EventSchemaCatalog.Find("Message")!;
        Set(ev, s, EventSlot.Int1, 5);       // message-table index
        Set(ev, s, EventSlot.Float1, 10);    // IntAsFloat — stored as 10.0f
        Set(ev, s, EventSlot.Bool1, true);
        Set(ev, s, EventSlot.Int2, 3);

        // str1/str2 stay empty — Message ignores them.
        byte[] expected = Record(502, "Message", 1, 0, 5, 3, 10f, 0f, string.Empty, string.Empty);
        Assert.Equal(expected, Serialize(ev));
    }

    [Fact]
    public void Music_Start_Uses_Bool2_Not_Bool1()
    {
        RflEvent ev = Blank("Music_Start", 503);
        EventSchema s = EventSchemaCatalog.Find("Music_Start")!;
        Set(ev, s, EventSlot.Str1, "track.wav");
        Set(ev, s, EventSlot.Bool2, true);

        byte[] expected = Record(503, "Music_Start", 0, 1, 0, 0, 0f, 0f, "track.wav", string.Empty);
        Assert.Equal(expected, Serialize(ev));
    }

    [Fact]
    public void Skybox_State_Writes_Single_Char_Str1_Flag()
    {
        RflEvent ev = Blank("Skybox_State", 504);
        EventSchema s = EventSchemaCatalog.Find("Skybox_State")!;
        Set(ev, s, EventSlot.Str1, true);   // FlagChar
        Set(ev, s, EventSlot.Float1, 5f);

        byte[] expected = Record(504, "Skybox_State", 0, 0, 0, 0, 5f, 0f, "1", string.Empty);
        Assert.Equal(expected, Serialize(ev));
    }

    private static RflEvent Blank(string cls, int uid) => new()
    {
        Uid = uid, ClassName = cls, Position = Pos, ScriptName = string.Empty, Color = Col,
    };

    private static void Set(RflEvent ev, EventSchema s, EventSlot slot, object v) =>
        EventFieldAccess.Set(s.Field(slot)!, ev, v);

    private static byte[] Serialize(RflEvent ev)
    {
        var section = new EventsSection();
        section.Events.Add(ev);
        var w = new RfWriter();
        section.Write(w, new RflContext(V));
        return w.ToArray();
    }

    /// <summary>Independently hand-builds the single-event section bytes for version 0xB4.</summary>
    private static byte[] Record(
        int uid, string cls, byte b1, byte b2, int i1, int i2, float f1, float f2, string s1, string s2)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(1);                    // count
        w.Write(uid);
        VStr(w, cls);
        w.Write(Pos.X); w.Write(Pos.Y); w.Write(Pos.Z);
        VStr(w, string.Empty);         // script name
        w.Write((byte)0);              // hidden
        w.Write(0f);                   // delay
        w.Write(b1); w.Write(b2);
        w.Write(i1); w.Write(i2);
        w.Write(f1); w.Write(f2);
        VStr(w, s1); VStr(w, s2);
        w.Write(0);                    // links count
        w.Write(Col.R); w.Write(Col.G); w.Write(Col.B); w.Write(Col.A);
        return ms.ToArray();
    }

    private static void VStr(BinaryWriter w, string s)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(s);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }
}
