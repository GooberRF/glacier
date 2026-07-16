using System;
using System.Collections.Generic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The single source of truth for every directed link edge in a document: the persisted
/// originator links (triggers / events / clutter / nav points) plus the structural
/// moving-group edges (member mover → start keyframe, keyframe sequence). The Link Graph
/// panel and the viewport link overlay both enumerate through here so they show exactly
/// the same set of links.
/// </summary>
public static class DocumentLinks
{
    /// <summary>All directed (from-UID → to-UID) link edges in the document.</summary>
    public static IEnumerable<(int From, int To)> AllEdges(EditorDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        foreach (LevelObject o in doc.Objects)
        {
            if (LinkModel.LinksOf(o) is { } links)
            {
                foreach (int t in links)
                {
                    yield return (o.Uid, t);
                }
            }
        }

        foreach ((int from, int to) in MovingGroupLinks.Edges(MovingGroups(doc)))
        {
            yield return (from, to);
        }
    }

    /// <summary>Every group across the groups + moving-groups sections of the document.</summary>
    public static IEnumerable<Group> MovingGroups(EditorDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (RflSection s in doc.Rfl.Sections)
        {
            if (s.Content is GroupsSection gs)
            {
                foreach (Group g in gs.Groups)
                {
                    yield return g;
                }
            }
        }
    }
}
