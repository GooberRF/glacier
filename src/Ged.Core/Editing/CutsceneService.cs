using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Undo-safe editing of cutscene camera paths: the <c>cutscene_paths</c> (0x6000)
/// section stores named, ordered lists of <c>cutscene_path_nodes</c> (0x5000) UIDs.
/// Create/rename/delete paths, place path nodes (an object header with a pose), and
/// append / remove / reorder a path's nodes. Cameras and cutscene records are
/// edited through the object inspector.
/// </summary>
public sealed class CutsceneService
{
    private readonly EditorDocument _doc;

    public CutsceneService(EditorDocument doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    public IReadOnlyList<CutscenePath> Paths => PathsContent()?.Paths ?? (IReadOnlyList<CutscenePath>)Array.Empty<CutscenePath>();

    public IReadOnlyList<ObjectHeader> Nodes => NodesContent()?.Nodes ?? (IReadOnlyList<ObjectHeader>)Array.Empty<ObjectHeader>();

    public ObjectHeader? FindNode(int uid) => Nodes.FirstOrDefault(n => n.Uid == uid);

    /// <summary>Creates a named, empty cutscene path.</summary>
    public CutscenePath CreatePath(string name)
    {
        var path = new CutscenePath { Name = string.IsNullOrWhiteSpace(name) ? "Path" : name };
        (CutscenePathsSection content, RflSection host) = EnsurePaths();
        _doc.Undo.Execute(new RelayCommand($"Create path \"{path.Name}\"",
            () => { EnsurePresent(host); content.Paths.Add(path); host.Dirty = true; },
            () => { content.Paths.Remove(path); host.Dirty = true; }));
        return path;
    }

    public void RenamePath(CutscenePath path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        string old = path.Name;
        string next = string.IsNullOrWhiteSpace(name) ? old : name;
        if (next == old)
        {
            return;
        }

        RflSection host = PathsHost();
        _doc.Undo.Execute(new RelayCommand("Rename path",
            () => { path.Name = next; host.Dirty = true; },
            () => { path.Name = old; host.Dirty = true; }));
    }

    public void DeletePath(CutscenePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        CutscenePathsSection? content = PathsContent();
        if (content is null)
        {
            return;
        }

        int index = content.Paths.IndexOf(path);
        if (index < 0)
        {
            return;
        }

        RflSection host = PathsHost();
        _doc.Undo.Execute(new RelayCommand("Delete path",
            () => { content.Paths.Remove(path); host.Dirty = true; },
            () => { content.Paths.Insert(Math.Clamp(index, 0, content.Paths.Count), path); host.Dirty = true; }));
    }

    /// <summary>Places a new cutscene path node (an object header with a pose).</summary>
    public ObjectHeader AddNode(Vec3 position, Mat3 rotation)
    {
        var node = new ObjectHeader
        {
            Uid = _doc.AllocateUid(),
            ClassName = "Cutscene_Path_Node",
            Position = position,
            Rotation = rotation,
        };
        (CutscenePathNodesSection content, RflSection host) = EnsureNodes();
        _doc.Undo.Execute(new RelayCommand("Add path node",
            () => { EnsurePresent(host); content.Nodes.Add(node); host.Dirty = true; _doc.RefreshObjects(); },
            () => { content.Nodes.Remove(node); host.Dirty = true; _doc.RefreshObjects(); }));
        return node;
    }

    /// <summary>Appends a node UID to a path's ordered node list.</summary>
    public void AppendNode(CutscenePath path, int nodeUid)
    {
        ArgumentNullException.ThrowIfNull(path);
        RflSection host = PathsHost();
        _doc.Undo.Execute(new RelayCommand("Add node to path",
            () => { path.PathNodes.Add(nodeUid); host.Dirty = true; },
            () => { path.PathNodes.Remove(nodeUid); host.Dirty = true; }));
    }

    public void RemoveNodeAt(CutscenePath path, int index)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (index < 0 || index >= path.PathNodes.Count)
        {
            return;
        }

        int uid = path.PathNodes[index];
        RflSection host = PathsHost();
        _doc.Undo.Execute(new RelayCommand("Remove node from path",
            () => { path.PathNodes.RemoveAt(index); host.Dirty = true; },
            () => { path.PathNodes.Insert(Math.Clamp(index, 0, path.PathNodes.Count), uid); host.Dirty = true; }));
    }

    /// <summary>Moves a node within a path's order (drag-reorder in the inspector).</summary>
    public void ReorderNode(CutscenePath path, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (from < 0 || from >= path.PathNodes.Count || to < 0 || to >= path.PathNodes.Count || from == to)
        {
            return;
        }

        RflSection host = PathsHost();
        _doc.Undo.Execute(new RelayCommand("Reorder path node",
            () => { Move(path.PathNodes, from, to); host.Dirty = true; },
            () => { Move(path.PathNodes, to, from); host.Dirty = true; }));
    }

    private static void Move(List<int> list, int from, int to)
    {
        int v = list[from];
        list.RemoveAt(from);
        list.Insert(to, v);
    }

    // ---- section plumbing -----------------------------------------------------

    private CutscenePathsSection? PathsContent() => FindContent<CutscenePathsSection>();

    private CutscenePathNodesSection? NodesContent() => FindContent<CutscenePathNodesSection>();

    private RflSection PathsHost()
    {
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.CutscenePaths)
            {
                return s;
            }
        }

        return EnsurePaths().Host;
    }

    private (CutscenePathsSection Content, RflSection Host) EnsurePaths()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.CutscenePaths, () => new CutscenePathsSection());
        return ((CutscenePathsSection)host.Content!, host);
    }

    private (CutscenePathNodesSection Content, RflSection Host) EnsureNodes()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.CutscenePathNodes, () => new CutscenePathNodesSection());
        return ((CutscenePathNodesSection)host.Content!, host);
    }

    private void EnsurePresent(RflSection host)
    {
        if (!_doc.Rfl.Sections.Contains(host))
        {
            int endIndex = _doc.Rfl.Sections.FindIndex(s => s.IsEnd);
            if (endIndex >= 0)
            {
                _doc.Rfl.Sections.Insert(endIndex, host);
            }
            else
            {
                _doc.Rfl.Sections.Add(host);
            }
        }
    }

    private T? FindContent<T>()
        where T : class, IRflSectionContent
    {
        _doc.Rfl.ParseAllKnownSections();
        foreach (RflSection s in _doc.Rfl.Sections)
        {
            if (s.Content is T t)
            {
                return t;
            }
        }

        return null;
    }
}
