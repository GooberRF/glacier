using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Spatial;

/// <summary>Disjoint sets with path halving; the smaller index becomes the root (deterministic).</summary>
internal sealed class UnionFind
{
    private readonly int[] _p;

    public UnionFind(int n)
    {
        _p = new int[n];
        for (int i = 0; i < n; i++)
        {
            _p[i] = i;
        }
    }

    public int Find(int x)
    {
        while (_p[x] != x)
        {
            _p[x] = _p[_p[x]];
            x = _p[x];
        }

        return x;
    }

    public void Union(int a, int b)
    {
        a = Find(a);
        b = Find(b);
        if (a != b)
        {
            _p[Math.Max(a, b)] = Math.Min(a, b);
        }
    }
}

/// <summary>A small undirected graph with the route and cut algorithms the layout measurements use.</summary>
internal sealed class UGraph
{
    public readonly List<HashSet<int>> Adj = new();

    public int N => Adj.Count;

    public int AddNode()
    {
        Adj.Add(new HashSet<int>());
        return Adj.Count - 1;
    }

    public void AddEdge(int a, int b)
    {
        if (a == b)
        {
            return;
        }

        Adj[a].Add(b);
        Adj[b].Add(a);
    }

    public int EdgeCount => Adj.Sum(a => a.Count) / 2;

    /// <summary>The number of connected components.</summary>
    public int Components()
    {
        var seen = new bool[Adj.Count];
        int c = 0;
        var st = new Stack<int>();
        for (int i = 0; i < Adj.Count; i++)
        {
            if (seen[i])
            {
                continue;
            }

            c++;
            st.Push(i);
            seen[i] = true;
            while (st.Count > 0)
            {
                int u = st.Pop();
                foreach (int v in Adj[u])
                {
                    if (!seen[v])
                    {
                        seen[v] = true;
                        st.Push(v);
                    }
                }
            }
        }

        return c;
    }

    /// <summary>
    /// The number of node-disjoint paths between the source set and the sink set (unit-capacity max flow; source and
    /// sink nodes are uncapacitated), at most <paramref name="cap"/>.
    /// </summary>
    public int DisjointCount(IReadOnlyCollection<int> sources, IReadOnlyCollection<int> sinks, int cap) =>
        DisjointPaths(sources, sinks, cap, null).Count;

    /// <summary>
    /// Node-disjoint paths between the sets (as <see cref="DisjointCount"/>), each as the node sequence from a source
    /// to a sink; <paramref name="banned"/> nodes are removed first.
    /// </summary>
    public List<List<int>> DisjointPaths(IReadOnlyCollection<int> sources, IReadOnlyCollection<int> sinks, int cap, ISet<int>? banned)
    {
        var src = new HashSet<int>(sources);
        var dst = new HashSet<int>(sinks);
        src.ExceptWith(dst);
        if (banned is not null)
        {
            src.ExceptWith(banned);
            dst.ExceptWith(banned);
        }

        var paths = new List<List<int>>();
        if (src.Count == 0 || dst.Count == 0)
        {
            return paths;
        }

        int n = Adj.Count;
        int s = 2 * n, t = (2 * n) + 1;
        var capacity = new Dictionary<(int, int), int>();
        var adj = new List<int>[(2 * n) + 2];
        for (int i = 0; i < adj.Length; i++)
        {
            adj[i] = new List<int>();
        }

        void Add(int a, int b, int c)
        {
            if (!capacity.ContainsKey((a, b)))
            {
                adj[a].Add(b);
                adj[b].Add(a);
                capacity[(a, b)] = 0;
                capacity.TryAdd((b, a), 0);
            }

            capacity[(a, b)] += c;
        }

        const int Big = 1000;
        for (int v = 0; v < n; v++)
        {
            if (banned is not null && banned.Contains(v))
            {
                continue;
            }

            Add(2 * v, (2 * v) + 1, src.Contains(v) || dst.Contains(v) ? Big : 1);
            foreach (int w in Adj[v])
            {
                if (banned is null || !banned.Contains(w))
                {
                    Add((2 * v) + 1, 2 * w, 1);
                }
            }
        }

        foreach (int x in src)
        {
            Add(s, 2 * x, Big);
        }

        foreach (int x in dst)
        {
            Add((2 * x) + 1, t, Big);
        }

        int flow = 0;
        var prev = new int[(2 * n) + 2];
        var q = new Queue<int>();
        while (flow < cap)
        {
            Array.Fill(prev, -1);
            prev[s] = s;
            q.Clear();
            q.Enqueue(s);
            while (q.Count > 0 && prev[t] < 0)
            {
                int u = q.Dequeue();
                foreach (int v in adj[u])
                {
                    if (prev[v] < 0 && capacity[(u, v)] > 0)
                    {
                        prev[v] = u;
                        q.Enqueue(v);
                    }
                }
            }

            if (prev[t] < 0)
            {
                break;
            }

            for (int v = t; v != s; v = prev[v])
            {
                capacity[(prev[v], v)] -= 1;
                capacity[(v, prev[v])] += 1;
            }

            flow++;
        }

        // Decompose the flow into node paths: follow saturated graph edges (out(v) → in(w) with reverse capacity > 0).
        var used = new HashSet<(int, int)>();
        foreach (int start in src.OrderBy(x => x))
        {
            while (paths.Count < flow)
            {
                var path = new List<int> { start };
                int cur = start;
                var visited = new HashSet<int> { start };
                bool reached = dst.Contains(cur);
                while (!reached)
                {
                    int next = -1;
                    foreach (int w in Adj[cur].OrderBy(x => x))
                    {
                        if (visited.Contains(w) || used.Contains((cur, w)))
                        {
                            continue;
                        }

                        // A unit of flow went out(cur) → in(w) when its reverse edge holds capacity beyond the original 0.
                        if (capacity.TryGetValue((2 * w, (2 * cur) + 1), out int back) && back > 0)
                        {
                            next = w;
                            break;
                        }
                    }

                    if (next < 0)
                    {
                        break;
                    }

                    used.Add((cur, next));
                    capacity[(2 * next, (2 * cur) + 1)] -= 1;
                    visited.Add(next);
                    path.Add(next);
                    cur = next;
                    reached = dst.Contains(cur);
                }

                if (!reached)
                {
                    break;
                }

                paths.Add(path);
            }
        }

        // A decomposition that could not trace every unit (cycles in the residual) still reports the full count.
        while (paths.Count < flow)
        {
            paths.Add(new List<int>());
        }

        return paths;
    }

    /// <summary>Greedy distinct routes: the shortest path, then its interior nodes are removed, repeated. Returns the route lengths.</summary>
    public List<double> GreedyRoutes(IReadOnlyCollection<int> sources, IReadOnlyCollection<int> sinks, Func<int, int, double> w, int cap) =>
        GreedyRoutePaths(sources, sinks, w, cap).Select(r => r.Length).ToList();

    /// <summary>As <see cref="GreedyRoutes"/>, with each route's node sequence.</summary>
    public List<(double Length, List<int> Nodes)> GreedyRoutePaths(IReadOnlyCollection<int> sources, IReadOnlyCollection<int> sinks, Func<int, int, double> w, int cap)
    {
        var res = new List<(double, List<int>)>();
        var banned = new HashSet<int>();
        var dst = new HashSet<int>(sinks);
        for (int r = 0; r < cap; r++)
        {
            int n = Adj.Count;
            var d = new double[n];
            var prev = new int[n];
            Array.Fill(d, double.PositiveInfinity);
            Array.Fill(prev, -1);
            var pq = new PriorityQueue<int, double>();
            foreach (int s in sources)
            {
                if (dst.Contains(s))
                {
                    continue;
                }

                d[s] = 0;
                pq.Enqueue(s, 0);
            }

            int hit = -1;
            while (pq.TryDequeue(out int u, out double du))
            {
                if (du > d[u])
                {
                    continue;
                }

                if (dst.Contains(u))
                {
                    hit = u;
                    break;
                }

                foreach (int v in Adj[u])
                {
                    if (banned.Contains(v))
                    {
                        continue;
                    }

                    double nd = du + w(u, v);
                    if (nd < d[v])
                    {
                        d[v] = nd;
                        prev[v] = u;
                        pq.Enqueue(v, nd);
                    }
                }
            }

            if (hit < 0)
            {
                break;
            }

            var nodes = new List<int>();
            for (int v = hit; v >= 0; v = prev[v])
            {
                nodes.Add(v);
            }

            nodes.Reverse();
            res.Add((d[hit], nodes));
            int cnt = 0;
            for (int v = prev[hit]; v >= 0 && prev[v] >= 0; v = prev[v])
            {
                banned.Add(v);
                cnt++;
            }

            if (cnt == 0)
            {
                break; // adjacent source/sink: one route only
            }
        }

        return res;
    }

    /// <summary>Articulation points (iterative Tarjan; the set does not depend on visiting order).</summary>
    public HashSet<int> Articulation()
    {
        int n = Adj.Count, timer = 0;
        var tin = new int[n];
        var low = new int[n];
        var vis = new bool[n];
        var res = new HashSet<int>();
        var adjList = Adj.Select(a => a.ToArray()).ToArray();
        var stack = new Stack<(int V, int Parent, int Next, int Children)>();
        for (int r = 0; r < n; r++)
        {
            if (vis[r])
            {
                continue;
            }

            vis[r] = true;
            tin[r] = low[r] = timer++;
            stack.Push((r, -1, 0, 0));
            while (stack.Count > 0)
            {
                (int v, int p, int next, int children) = stack.Pop();
                int[] nb = adjList[v];
                if (next < nb.Length)
                {
                    int to = nb[next];
                    stack.Push((v, p, next + 1, children));
                    if (to == p)
                    {
                        continue;
                    }

                    if (vis[to])
                    {
                        low[v] = Math.Min(low[v], tin[to]);
                    }
                    else
                    {
                        vis[to] = true;
                        tin[to] = low[to] = timer++;
                        stack.Push((to, v, 0, 0));
                    }

                    continue;
                }

                // v is finished: report to its parent.
                if (p >= 0)
                {
                    (int pv, int pp, int pnext, int pchildren) = stack.Pop();
                    low[pv] = Math.Min(low[pv], low[v]);
                    if (low[v] >= tin[pv] && pp != -1)
                    {
                        res.Add(pv);
                    }

                    stack.Push((pv, pp, pnext, pchildren + 1));
                }
                else if (children > 1)
                {
                    res.Add(v);
                }
            }
        }

        return res;
    }

    /// <summary>The connected pieces left when <paramref name="x"/> is removed, reached from its neighbours.</summary>
    public List<List<int>> ComponentsWithout(int x)
    {
        var seen = new bool[N];
        seen[x] = true;
        var res = new List<List<int>>();
        var st = new Stack<int>();
        foreach (int s in Adj[x])
        {
            if (seen[s])
            {
                continue;
            }

            var comp = new List<int>();
            st.Push(s);
            seen[s] = true;
            while (st.Count > 0)
            {
                int u = st.Pop();
                comp.Add(u);
                foreach (int v in Adj[u])
                {
                    if (!seen[v])
                    {
                        seen[v] = true;
                        st.Push(v);
                    }
                }
            }

            res.Add(comp);
        }

        return res;
    }
}
