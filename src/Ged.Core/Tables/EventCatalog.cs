namespace Ged.Core.Tables;

/// <summary>A single event type from <c>events.tbl</c> (the editor's event tree source).</summary>
public sealed class EventDef
{
    public EventDef(TblRecord record)
    {
        Record = record;
        Name = record.GetString("Event Name") ?? string.Empty;
        RfeLevel1 = record.GetString("RFE Level1");
        RfeLevel2 = record.GetString("RFE Level2");
        RfeLevel3 = record.GetString("RFE Level3");

        // Prefer the explicit RFE Level1 category; fall back to the enclosing #section.
        Category = !string.IsNullOrEmpty(RfeLevel1) ? RfeLevel1! : record.Section;
    }

    public string Name { get; }

    /// <summary>The event's top-level tree category (RFE Level1, or the enclosing section).</summary>
    public string Category { get; }

    public string? RfeLevel1 { get; }

    public string? RfeLevel2 { get; }

    public string? RfeLevel3 { get; }

    public TblRecord Record { get; }

    public override string ToString() => Name;
}

/// <summary>A category node in the event tree: a name and the events directly under it.</summary>
public sealed class EventCategory
{
    public EventCategory(string name, IReadOnlyList<EventDef> events)
    {
        Name = name;
        Events = events;
    }

    public string Name { get; }

    public IReadOnlyList<EventDef> Events { get; }

    public override string ToString() => $"{Name} ({Events.Count})";
}

/// <summary>
/// Typed catalog of <c>events.tbl</c>: the flat event list plus a category tree
/// (grouped by RFE Level1), feeding the categorized event browser.
/// </summary>
public sealed class EventCatalog
{
    private readonly Dictionary<string, EventDef> _byName;

    private EventCatalog(IReadOnlyList<EventDef> events, IReadOnlyList<EventCategory> categories)
    {
        Events = events;
        Categories = categories;
        _byName = new Dictionary<string, EventDef>(StringComparer.OrdinalIgnoreCase);
        foreach (EventDef e in events)
        {
            _byName.TryAdd(e.Name, e);
        }
    }

    public IReadOnlyList<EventDef> Events { get; }

    /// <summary>Events grouped into categories, in first-seen order.</summary>
    public IReadOnlyList<EventCategory> Categories { get; }

    public static EventCatalog Load(byte[] data) => Parse(TblParser.Parse(data));

    public static EventCatalog Load(string text) => Parse(TblParser.Parse(text));

    public static EventCatalog Parse(TblDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var events = doc.Records
            .Where(r => r.Has("Event Name") && !string.IsNullOrEmpty(r.GetString("Event Name")))
            .Select(r => new EventDef(r))
            .ToList();

        var order = new List<string>();
        var grouped = new Dictionary<string, List<EventDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (EventDef e in events)
        {
            string cat = string.IsNullOrEmpty(e.Category) ? "Uncategorized" : e.Category;
            if (!grouped.TryGetValue(cat, out List<EventDef>? list))
            {
                list = new List<EventDef>();
                grouped[cat] = list;
                order.Add(cat);
            }

            list.Add(e);
        }

        var categories = order.Select(c => new EventCategory(c, grouped[c])).ToList();
        return new EventCatalog(events, categories);
    }

    public EventDef? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out EventDef? d) ? d : null;
}
