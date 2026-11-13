using System;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;

namespace Ged.Core.Editing;

/// <summary>
/// The <c>alpine_level_properties</c> section an undoable edit writes into, created lazily INSIDE the
/// edit's command: <see cref="Attach"/> (called from the command's apply) inserts a freshly created
/// section and raises its chunk version to 6 (the version Alpine RED writes, which carries every
/// field Glacier edits; the raise writes the full layout); <see cref="Detach"/> (called from the
/// command's undo) restores the version and removes a
/// section this edit created. So undoing the first Alpine edit on a stock level leaves no empty
/// section behind and the saved bytes match the pre-edit file exactly. Reading never creates it.
/// </summary>
internal sealed class AlpinePropertiesEdit
{
    private const uint RequiredVersion = AlpineLevelPropertiesSection.CurrentVersion;

    private readonly EditorDocument _doc;
    private readonly uint _oldVersion;

    private AlpinePropertiesEdit(EditorDocument doc, RflSection host, AlpineLevelPropertiesSection content, bool created)
    {
        _doc = doc;
        Host = host;
        Content = content;
        Created = created;
        _oldVersion = content.Version;
    }

    /// <summary>The section content the edit reads and writes.</summary>
    public AlpineLevelPropertiesSection Content { get; }

    /// <summary>The section host (detached until <see cref="Attach"/> when <see cref="Created"/>).</summary>
    public RflSection Host { get; }

    /// <summary>True when the level had no Alpine section and this edit creates it.</summary>
    public bool Created { get; }

    /// <summary>The level's existing Alpine section content, or null (never creates it).</summary>
    public static AlpineLevelPropertiesSection? Find(EditorDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return FindHost(doc)?.Content as AlpineLevelPropertiesSection;
    }

    /// <summary>Prepares an edit: the existing section, or a new detached one (version 6).</summary>
    public static AlpinePropertiesEdit Begin(EditorDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (FindHost(doc) is { Content: AlpineLevelPropertiesSection existing } host)
        {
            return new AlpinePropertiesEdit(doc, host, existing, created: false);
        }

        var content = new AlpineLevelPropertiesSection { Version = RequiredVersion };
        var section = new RflSection((uint)SectionType.AlpineLevelProperties, Array.Empty<byte>()) { Content = content, Dirty = true };
        return new AlpinePropertiesEdit(doc, section, content, created: true);
    }

    /// <summary>Command apply: insert a created section, raise the version to 6, mark it dirty.</summary>
    public void Attach()
    {
        if (Created && !_doc.Rfl.Sections.Contains(Host))
        {
            _doc.Rfl.InsertSection(Host);
        }

        if (Content.Version < RequiredVersion)
        {
            Content.Version = RequiredVersion;
        }

        Host.Dirty = true;
    }

    /// <summary>Command undo: restore the version, remove a section this edit created.</summary>
    public void Detach()
    {
        Content.Version = _oldVersion;
        if (Created)
        {
            _doc.Rfl.Sections.Remove(Host);
        }

        Host.Dirty = true;
    }

    private static RflSection? FindHost(EditorDocument doc)
    {
        doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.Content is AlpineLevelPropertiesSection)
            {
                return s;
            }
        }

        return null;
    }
}
