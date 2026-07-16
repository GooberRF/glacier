using Ged.Core.Model;

namespace Ged.Core.IO.Rfl.Sections;

/// <summary>items (0x40000).</summary>
public sealed class ItemsSection : IRflSectionContent
{
    public SectionType Type => SectionType.Items;

    public List<Item> Items { get; set; } = new();

    public static IRflSectionContent Parse(RfReader r, RflContext ctx)
    {
        var section = new ItemsSection();
        int count = r.ReadI32();
        for (int i = 0; i < count; i++)
        {
            section.Items.Add(new Item
            {
                Header = ObjectHeader.Read(r),
                Count = r.ReadI32(),
                RespawnTime = r.ReadI32(),
                TeamId = r.ReadI32(),
            });
        }

        return section;
    }

    public void Write(RfWriter w, RflContext ctx)
    {
        w.WriteI32(Items.Count);
        foreach (Item item in Items)
        {
            item.Header.Write(w);
            w.WriteI32(item.Count);
            w.WriteI32(item.RespawnTime);
            w.WriteI32(item.TeamId);
        }
    }
}
