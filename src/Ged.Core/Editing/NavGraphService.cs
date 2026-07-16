using System;
using System.Collections.Generic;
using System.Linq;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Undo-safe operations over the AI navigation graph. Three stock commands ride on
/// this service: cycling the connection between two nav points (stock <c>J</c>),
/// auto-connecting nav points into a path graph (stock "Calculate Nav Paths"), and
/// managing named waypoint lists (stock "Waypoint List").
///
/// Nav-point connectivity is the per-nav-point <see cref="NavPoint.Links"/> UID list
/// — the same list the scene builder draws as "path node connections" and the linter
/// walks — so an edit here shows up immediately in the viewport. Waypoint lists are
/// the separate <c>waypoint_lists</c> section of nav-point array indices.
/// </summary>
public sealed class NavGraphService
{
    private readonly EditorDocument _doc;

    public NavGraphService(EditorDocument doc) => _doc = doc ?? throw new ArgumentNullException(nameof(doc));

    /// <summary>Directed connection state between an ordered nav-point pair (A, B).</summary>
    public enum ConnectionState
    {
        None,
        Forward,
        Backward,
        Both,
    }

    // ---- Pure connection-state logic (unit-tested) ----------------------------

    /// <summary>The current connection state between an ordered nav-point pair.</summary>
    public static ConnectionState StateOf(NavPoint a, NavPoint b)
    {
        bool ab = a.Links.Contains(b.Uid);
        bool ba = b.Links.Contains(a.Uid);
        if (ab && ba)
        {
            return ConnectionState.Both;
        }

        if (ab)
        {
            return ConnectionState.Forward;
        }

        return ba ? ConnectionState.Backward : ConnectionState.None;
    }

    /// <summary>Stock J cycle order: None → Forward → Backward → Both → None.</summary>
    public static ConnectionState NextState(ConnectionState s) => s switch
    {
        ConnectionState.None => ConnectionState.Forward,
        ConnectionState.Forward => ConnectionState.Backward,
        ConnectionState.Backward => ConnectionState.Both,
        _ => ConnectionState.None,
    };

    /// <summary>The A and B link lists that realise <paramref name="state"/> (existing links preserved).</summary>
    public static (List<int> A, List<int> B) ApplyState(NavPoint a, NavPoint b, ConnectionState state)
    {
        List<int> na = a.Links.Where(u => u != b.Uid).ToList();
        List<int> nb = b.Links.Where(u => u != a.Uid).ToList();
        if (state is ConnectionState.Forward or ConnectionState.Both)
        {
            na.Add(b.Uid);
        }

        if (state is ConnectionState.Backward or ConnectionState.Both)
        {
            nb.Add(a.Uid);
        }

        return (na, nb);
    }

    /// <summary>
    /// Pure: the mutual proximity links to add so every same-nav-type pair within
    /// <paramref name="maxDistance"/> is connected both ways (existing links skipped).
    /// </summary>
    public static List<(int From, int To)> ComputeProximityLinks(IReadOnlyList<NavPoint> points, float maxDistance)
    {
        var result = new List<(int, int)>();
        float maxSq = maxDistance * maxDistance;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                NavPoint p = points[i];
                NavPoint q = points[j];
                if (p.Uid == q.Uid || p.NavType != q.NavType || DistSq(p.Position, q.Position) > maxSq)
                {
                    continue;
                }

                if (!p.Links.Contains(q.Uid))
                {
                    result.Add((p.Uid, q.Uid));
                }

                if (!q.Links.Contains(p.Uid))
                {
                    result.Add((q.Uid, p.Uid));
                }
            }
        }

        return result;
    }

    // ---- Nav connections (undo-safe) ------------------------------------------

    /// <summary>Stock J: cycles the connection between two nav points; returns the new state.</summary>
    public ConnectionState CycleConnection(LevelObject a, LevelObject b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Model is not NavPoint na || b.Model is not NavPoint nb)
        {
            throw new ArgumentException("CycleConnection requires two nav points.");
        }

        ConnectionState next = NextState(StateOf(na, nb));
        (List<int> newA, List<int> newB) = ApplyState(na, nb, next);
        CommitLinks($"Cycle nav connection ({next})", new()
        {
            (na.Links, a.Section, newA),
            (nb.Links, b.Section, newB),
        });
        return next;
    }

    /// <summary>
    /// Stock "Calculate Nav Paths": mutually connects same-type nav points within
    /// <paramref name="maxDistance"/> world units, preserving existing connections.
    /// Returns the number of directed connections added.
    /// </summary>
    public int CalculatePaths(float maxDistance)
    {
        var navObjs = _doc.Objects.Where(o => o.Kind == LevelObjectKind.NavPoint).ToList();
        var points = navObjs.Select(o => (NavPoint)o.Model).ToList();
        List<(int From, int To)> links = ComputeProximityLinks(points, maxDistance);
        if (links.Count == 0)
        {
            return 0;
        }

        Dictionary<int, List<int>> byFrom = links
            .GroupBy(l => l.From)
            .ToDictionary(g => g.Key, g => g.Select(x => x.To).ToList());

        var edits = new List<(List<int>, RflSection, List<int>)>();
        foreach (LevelObject o in navObjs)
        {
            if (!byFrom.TryGetValue(o.Uid, out List<int>? adds))
            {
                continue;
            }

            var np = (NavPoint)o.Model;
            edits.Add((np.Links, o.Section, np.Links.Concat(adds).ToList()));
        }

        CommitLinks($"Calculate nav paths (+{links.Count})", edits);
        return links.Count;
    }

    // ---- Waypoint lists (undo-safe) -------------------------------------------

    /// <summary>The waypoint_lists section (created empty when absent) and its host section.</summary>
    public (WaypointListsSection Section, RflSection Host) WaypointSection()
    {
        RflSection host = _doc.Rfl.GetOrCreateSection(SectionType.WaypointLists, () => new WaypointListsSection());
        return ((WaypointListsSection)host.Content!, host);
    }

    /// <summary>The current waypoint lists (a live view of the section).</summary>
    public IReadOnlyList<WaypointList> WaypointLists => WaypointSection().Section.Lists;

    public void AddWaypointList(string name) =>
        MutateWaypoints("Add waypoint list", lists => lists.Add(new WaypointList { Name = name }));

    public void RenameWaypointList(int index, string name) =>
        MutateWaypoints("Rename waypoint list", lists => { if (In(index, lists.Count)) { lists[index].Name = name; } });

    public void RemoveWaypointList(int index) =>
        MutateWaypoints("Delete waypoint list", lists => { if (In(index, lists.Count)) { lists.RemoveAt(index); } });

    public void AddWaypoints(int listIndex, IEnumerable<int> navIndices) =>
        MutateWaypoints("Add waypoints", lists => { if (In(listIndex, lists.Count)) { lists[listIndex].WaypointIndices.AddRange(navIndices); } });

    public void RemoveWaypointAt(int listIndex, int memberIndex) =>
        MutateWaypoints("Remove waypoint", lists =>
        {
            if (In(listIndex, lists.Count) && In(memberIndex, lists[listIndex].WaypointIndices.Count))
            {
                lists[listIndex].WaypointIndices.RemoveAt(memberIndex);
            }
        });

    // ---- Internals ------------------------------------------------------------

    private void MutateWaypoints(string description, Action<List<WaypointList>> mutate)
    {
        (WaypointListsSection sec, RflSection host) = WaypointSection();
        List<WaypointList> old = CloneLists(sec.Lists);
        List<WaypointList> next = CloneLists(sec.Lists);
        mutate(next);
        _doc.Undo.Execute(new RelayCommand(description,
            () => { sec.Lists = CloneLists(next); host.Dirty = true; },
            () => { sec.Lists = CloneLists(old); host.Dirty = true; }));
    }

    private void CommitLinks(string description, List<(List<int> List, RflSection Section, List<int> New)> edits)
    {
        var snap = edits
            .Select(e => (e.List, e.Section, Old: e.List.ToList(), e.New))
            .ToList();

        _doc.Undo.Execute(new RelayCommand(description,
            () =>
            {
                foreach (var s in snap)
                {
                    s.List.Clear();
                    s.List.AddRange(s.New);
                    s.Section.Dirty = true;
                }

                _doc.NotifyLinksChanged();
            },
            () =>
            {
                foreach (var s in snap)
                {
                    s.List.Clear();
                    s.List.AddRange(s.Old);
                    s.Section.Dirty = true;
                }

                _doc.NotifyLinksChanged();
            }));
    }

    private static List<WaypointList> CloneLists(List<WaypointList> src) =>
        src.Select(l => new WaypointList { Name = l.Name, WaypointIndices = new List<int>(l.WaypointIndices) }).ToList();

    private static bool In(int i, int count) => i >= 0 && i < count;

    private static float DistSq(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
