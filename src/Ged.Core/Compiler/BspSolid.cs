using System;
using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// A solid represented by a BSP tree of its boundary polygons, supporting the
/// Thibault–Naylor boolean operations (union / subtract / intersect) that RED's
/// per-brush CSG realizes. Polygons carry their full attributes; splitting
/// interpolates only position/UV, so texture continuity is preserved. The tree
/// is built and traversed iteratively (explicit work stacks) so deep trees over
/// the thousands of faces in a real level cannot overflow the call stack.
///
/// Convention: a solid's polygons face OUTWARD (normal points away from the
/// solid interior). The compiler models OPEN space as the solid, then flips
/// every surviving face once at the end so normals point into open space, which
/// is the orientation RF.exe expects.
/// </summary>
public static class BspSolid
{
    private const float Eps = CsgPlane.OnPlaneEpsilon;

    /// <summary>A ∪ B: the boundary of the union of the two solids.</summary>
    public static List<CsgFace> Union(IReadOnlyList<CsgFace> a, IReadOnlyList<CsgFace> b)
    {
        Node na = Node.Build(Clone(a));
        Node nb = Node.Build(Clone(b));
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.Insert(nb.AllPolygons());
        return na.AllPolygons();
    }

    /// <summary>A − B: the part of solid A outside solid B.</summary>
    public static List<CsgFace> Subtract(IReadOnlyList<CsgFace> a, IReadOnlyList<CsgFace> b)
    {
        Node na = Node.Build(Clone(a));
        Node nb = Node.Build(Clone(b));
        na.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.Insert(nb.AllPolygons());
        na.Invert();
        return na.AllPolygons();
    }

    /// <summary>A ∩ B: the part of solid A inside solid B.</summary>
    public static List<CsgFace> Intersect(IReadOnlyList<CsgFace> a, IReadOnlyList<CsgFace> b)
    {
        Node na = Node.Build(Clone(a));
        Node nb = Node.Build(Clone(b));
        na.Invert();
        nb.ClipTo(na);
        nb.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        na.Insert(nb.AllPolygons());
        na.Invert();
        return na.AllPolygons();
    }

    /// <summary>
    /// Classifies a point against a closed solid built from <paramref name="faces"/>:
    /// negative distance is inside (behind an outward-facing boundary). Returns
    /// +1 outside, -1 inside, 0 on the boundary (within the on-plane band).
    /// </summary>
    public static int ClassifyPoint(Node solid, Vec3 p) => solid.ClassifyPoint(p);

    private static List<CsgFace> Clone(IReadOnlyList<CsgFace> faces)
    {
        var list = new List<CsgFace>(faces.Count);
        foreach (CsgFace f in faces)
        {
            list.Add(f.With(new List<CsgVertex>(f.Vertices)));
        }

        return list;
    }

    /// <summary>Splits a polygon by a plane into coplanar/front/back buckets (csg.js semantics, 1e-4 band).</summary>
    private static void SplitPolygon(
        CsgPlane plane,
        CsgFace polygon,
        List<CsgFace> coplanarFront,
        List<CsgFace> coplanarBack,
        List<CsgFace> front,
        List<CsgFace> back)
    {
        const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

        int polygonType = 0;
        int n = polygon.Vertices.Count;
        Span<int> types = n <= 64 ? stackalloc int[n] : new int[n];
        for (int i = 0; i < n; i++)
        {
            float t = plane.Distance(polygon.Vertices[i].Position);
            int type = t < -Eps ? Back : (t > Eps ? Front : Coplanar);
            polygonType |= type;
            types[i] = type;
        }

        switch (polygonType)
        {
            case Coplanar:
                (plane.Normal.Dot(polygon.Plane.Normal) > 0 ? coplanarFront : coplanarBack).Add(polygon);
                break;
            case Front:
                front.Add(polygon);
                break;
            case Back:
                back.Add(polygon);
                break;
            default: // Spanning
                var f = new List<CsgVertex>();
                var b = new List<CsgVertex>();
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    int ti = types[i], tj = types[j];
                    CsgVertex vi = polygon.Vertices[i];
                    CsgVertex vj = polygon.Vertices[j];
                    if (ti != Back)
                    {
                        f.Add(vi);
                    }

                    if (ti != Front)
                    {
                        b.Add(vi);
                    }

                    if ((ti | tj) == Spanning)
                    {
                        float di = plane.Distance(vi.Position);
                        float dj = plane.Distance(vj.Position);
                        float t = di / (di - dj);
                        CsgVertex v = CsgVertex.Lerp(vi, vj, t);
                        f.Add(v);
                        b.Add(v);
                    }
                }

                if (f.Count >= 3)
                {
                    front.Add(polygon.With(f));
                }

                if (b.Count >= 3)
                {
                    back.Add(polygon.With(b));
                }

                break;
        }
    }

    /// <summary>One node of the BSP: a splitting plane, coplanar polygons, and front/back children.</summary>
    public sealed class Node
    {
        private CsgPlane _plane;
        private bool _hasPlane;
        private Node? _front;
        private Node? _back;
        private List<CsgFace> _polygons = new();

        public static Node Build(List<CsgFace> polygons)
        {
            var root = new Node();
            root.Insert(polygons);
            return root;
        }

        /// <summary>Adds polygons to the tree, extending it (iterative to bound stack depth).</summary>
        public void Insert(List<CsgFace> polygons)
        {
            if (polygons.Count == 0)
            {
                return;
            }

            var work = new Stack<(Node Node, List<CsgFace> Polys)>();
            work.Push((this, polygons));
            while (work.Count > 0)
            {
                (Node node, List<CsgFace> polys) = work.Pop();
                if (polys.Count == 0)
                {
                    continue;
                }

                if (!node._hasPlane)
                {
                    node._plane = polys[0].Plane;
                    node._hasPlane = true;
                }

                var front = new List<CsgFace>();
                var back = new List<CsgFace>();
                foreach (CsgFace p in polys)
                {
                    SplitPolygon(node._plane, p, node._polygons, node._polygons, front, back);
                }

                if (front.Count > 0)
                {
                    node._front ??= new Node();
                    work.Push((node._front, front));
                }

                if (back.Count > 0)
                {
                    node._back ??= new Node();
                    work.Push((node._back, back));
                }
            }
        }

        /// <summary>Removes the parts of <paramref name="polygons"/> that fall inside this solid.</summary>
        public List<CsgFace> ClipPolygons(List<CsgFace> polygons)
        {
            var result = new List<CsgFace>();
            var work = new Stack<(Node Node, List<CsgFace> Polys)>();
            work.Push((this, polygons));
            while (work.Count > 0)
            {
                (Node node, List<CsgFace> polys) = work.Pop();
                if (!node._hasPlane)
                {
                    result.AddRange(polys);
                    continue;
                }

                var front = new List<CsgFace>();
                var back = new List<CsgFace>();
                foreach (CsgFace p in polys)
                {
                    SplitPolygon(node._plane, p, front, back, front, back);
                }

                if (node._front is not null)
                {
                    work.Push((node._front, front));
                }
                else
                {
                    result.AddRange(front);
                }

                if (node._back is not null)
                {
                    work.Push((node._back, back));
                }

                // no back child => polygons behind this plane are inside => dropped
            }

            return result;
        }

        /// <summary>Clips this tree's polygons to the volume of <paramref name="other"/>.</summary>
        public void ClipTo(Node other)
        {
            var work = new Stack<Node>();
            work.Push(this);
            while (work.Count > 0)
            {
                Node node = work.Pop();
                node._polygons = other.ClipPolygons(node._polygons);
                if (node._front is not null)
                {
                    work.Push(node._front);
                }

                if (node._back is not null)
                {
                    work.Push(node._back);
                }
            }
        }

        public void Invert()
        {
            var work = new Stack<Node>();
            work.Push(this);
            while (work.Count > 0)
            {
                Node node = work.Pop();
                foreach (CsgFace p in node._polygons)
                {
                    p.Flip();
                }

                if (node._hasPlane)
                {
                    node._plane = node._plane.Flipped();
                }

                (node._front, node._back) = (node._back, node._front);
                if (node._front is not null)
                {
                    work.Push(node._front);
                }

                if (node._back is not null)
                {
                    work.Push(node._back);
                }
            }
        }

        public List<CsgFace> AllPolygons()
        {
            var result = new List<CsgFace>();
            var work = new Stack<Node>();
            work.Push(this);
            while (work.Count > 0)
            {
                Node node = work.Pop();
                result.AddRange(node._polygons);
                if (node._front is not null)
                {
                    work.Push(node._front);
                }

                if (node._back is not null)
                {
                    work.Push(node._back);
                }
            }

            return result;
        }

        /// <summary>+1 outside, -1 inside, 0 on-boundary for a point vs this solid.</summary>
        public int ClassifyPoint(Vec3 p)
        {
            Node? node = this;
            while (node is not null && node._hasPlane)
            {
                float d = node._plane.Distance(p);
                if (d > Eps)
                {
                    if (node._front is null)
                    {
                        return +1;
                    }

                    node = node._front;
                }
                else if (d < -Eps)
                {
                    if (node._back is null)
                    {
                        return -1;
                    }

                    node = node._back;
                }
                else
                {
                    // On this node's plane: descend the side that exists, prefer inside.
                    if (node._back is not null)
                    {
                        node = node._back;
                    }
                    else if (node._front is not null)
                    {
                        node = node._front;
                    }
                    else
                    {
                        return 0;
                    }
                }
            }

            return 0;
        }
    }
}
