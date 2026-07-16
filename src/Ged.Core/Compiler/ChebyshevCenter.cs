using System;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Reusable scratch for <see cref="ChebyshevCenter"/> so the per-leaf LP allocates nothing on the hot path.
/// Seidel's recursion descends dimensions 4→1 in a single active path (a violated constraint recurses fully
/// before the parent continues), so ONE row/objective/box/solution buffer per dimension level is reused across
/// every recursion and every leaf. Grows monotonically when a deeper leaf needs more constraint slots.
/// </summary>
internal sealed class ChebyshevWorkspace
{
    internal int Cap;
    internal double[][][] Rows = null!; // [dim][row][coeff…, rhs]  (row length = dim + 1)
    internal double[][] Obj = null!;    // [dim] objective (length dim)
    internal double[][] Lo = null!;     // [dim] box lower (length dim)
    internal double[][] Hi = null!;     // [dim] box upper (length dim)
    internal double[][] Sol = null!;    // [dim] solution  (length dim)

    public ChebyshevWorkspace(int initialConstraints = 64) => Grow(initialConstraints);

    /// <summary>Ensures the buffers hold at least <paramref name="count"/> rows (+ box rows added per level).</summary>
    internal void Ensure(int count)
    {
        if (count + 8 > Cap)
        {
            Grow(count);
        }
    }

    private void Grow(int count)
    {
        Cap = count + 8;
        Rows = new double[5][][];
        Obj = new double[5][];
        Lo = new double[5][];
        Hi = new double[5][];
        Sol = new double[5][];
        for (int d = 1; d <= 4; d++)
        {
            var rows = new double[Cap][];
            for (int i = 0; i < Cap; i++)
            {
                rows[i] = new double[d + 1];
            }

            Rows[d] = rows;
            Obj[d] = new double[d];
            Lo[d] = new double[d];
            Hi[d] = new double[d];
            Sol[d] = new double[d];
        }
    }
}

/// <summary>
/// Deepest-interior point of a convex leaf cell by its <b>Chebyshev centre</b> (compiler-parity-notes.md,
/// "contents-carrying leaf classification" — blocker 1). The leaf-extraction path must classify every convex
/// BSP leaf open/solid ONCE at a guaranteed-interior point; the original construction enumerated the cell's
/// corner vertices (O(n³) triple solves per leaf) which dominated solve time on deep non-convex terrain
/// leaves (dm04 maxCons 46 → ~13× solve). The Chebyshev centre is the exact deepest interior point and is
/// found by a tiny linear program:
/// <code>
///   maximise r  s.t.  nᵢ·x + dᵢ + r ≤ 0  for every leaf half-space i  (unit normals ⇒ r is the Euclidean margin)
/// </code>
/// Four variables (x, y, z, r); ≤ ~50 constraints; solved by <b>Seidel's randomised incremental LP</b>
/// (expected O(constraints) for fixed dimension, deterministically seeded for reproducibility). A world-bound
/// box on every variable makes the program bounded, so a leaf open to the void is still resolved. The returned
/// radius is the margin: r ≥ ε ⇒ the centre is strictly interior (its open/solid verdict is the true leaf
/// contents); r &lt; ε ⇒ a sub-resolution cell (RED's 1e-4 build collapses it too), and the caller falls back
/// to the exact vertex enumeration (kept as the verification oracle).
/// </summary>
internal static class ChebyshevCenter
{
    /// <summary>Objective/feasibility band. Coeffs are O(1) (unit normals + a 1 on r); coords are O(10²) m, so
    /// double precision noise is ~1e-11 — 1e-7 is safely above it and far below the 1e-4 geometry band.</summary>
    private const double Eps = 1e-7;

    /// <summary>Smallest usable pivot magnitude (every row carries a unit r-coefficient, so a full-rank row
    /// always has a pivot ≥ this; a smaller max means a near-degenerate/all-zero row).</summary>
    private const double PivotTiny = 1e-9;

    /// <summary>Convenience overload (tests / one-off calls): allocates a workspace per call.</summary>
    public static bool Solve(CsgPlane[] planes, int count, Vec3 wc, float half, out Vec3 center, out float radius) =>
        Solve(planes, count, wc, half, new ChebyshevWorkspace(count), out center, out radius);

    /// <summary>
    /// Chebyshev centre of the cell {p : plane.Distance(p) ≤ 0 for every constraint} ∩ the world-bound box
    /// [wc − (half+1), wc + (half+1)]³. Constraint planes are the leaf's oriented bounding half-spaces (unit
    /// normals). Returns the centre and its margin <paramref name="radius"/> (the Chebyshev radius); radius ≤ 0
    /// ⇒ the LP found no interior (an empty/degenerate cell). Deterministic: constraints are shuffled with a
    /// fixed-seed generator, so the same cell always yields the same centre. Uses <paramref name="ws"/> as
    /// reusable scratch — no allocation on the hot path.
    /// </summary>
    public static bool Solve(
        CsgPlane[] planes, int count, Vec3 wc, float half, ChebyshevWorkspace ws, out Vec3 center, out float radius)
    {
        ws.Ensure(count);

        // Variables v = (x, y, z, r). Each constraint nᵢ·x + dᵢ + r ≤ 0 ⇒ row [nx, ny, nz, 1 | −dᵢ].
        double[][] rows = ws.Rows[4];
        for (int i = 0; i < count; i++)
        {
            Vec3 n = planes[i].Normal;
            double[] r = rows[i];
            r[0] = n.X;
            r[1] = n.Y;
            r[2] = n.Z;
            r[3] = 1.0;
            r[4] = -planes[i].Offset;
        }

        // A fixed-seed shuffle gives Seidel its expected-linear behaviour while staying fully reproducible.
        Shuffle(rows, count);

        double box = half + 1.0;
        double[] lo = ws.Lo[4];
        double[] hi = ws.Hi[4];
        double[] c = ws.Obj[4];
        lo[0] = wc.X - box;
        lo[1] = wc.Y - box;
        lo[2] = wc.Z - box;
        lo[3] = 0.0;
        hi[0] = wc.X + box;
        hi[1] = wc.Y + box;
        hi[2] = wc.Z + box;
        hi[3] = box;
        c[0] = 0.0;
        c[1] = 0.0;
        c[2] = 0.0;
        c[3] = 1.0;

        if (!SolveRec(4, count, ws))
        {
            center = wc;
            radius = 0f;
            return false;
        }

        double[] v = ws.Sol[4];
        center = new Vec3((float)v[0], (float)v[1], (float)v[2]);
        radius = (float)v[3];
        return radius > 0f;
    }

    /// <summary>Seidel's incremental LP maximising Obj[d]·x over the first <paramref name="rowCount"/> rows ∩ box,
    /// in <paramref name="d"/> variables (all buffers live in <paramref name="ws"/>). Returns false when the region
    /// is empty. On a violated constraint the optimum moves onto its hyperplane, and the earlier constraints + box
    /// are projected into a (d−1)-variable sub-LP by eliminating the pivot variable.</summary>
    private static bool SolveRec(int d, int rowCount, ChebyshevWorkspace ws)
    {
        double[][] rows = ws.Rows[d];
        double[] c = ws.Obj[d];
        double[] lo = ws.Lo[d];
        double[] hi = ws.Hi[d];
        double[] x = ws.Sol[d];

        // Start at the box corner that maximises the objective (a definite vertex; c[k] ≤ 0 ⇒ lo).
        for (int k = 0; k < d; k++)
        {
            x[k] = c[k] > 0 ? hi[k] : lo[k];
        }

        for (int h = 0; h < rowCount; h++)
        {
            double[] row = rows[h];
            double lhs = 0;
            for (int k = 0; k < d; k++)
            {
                lhs += row[k] * x[k];
            }

            if (lhs <= row[d] + Eps)
            {
                continue; // current vertex still satisfies this constraint
            }

            // Violated: the optimum now lies on row·x = row[d]. Pick the largest-magnitude pivot coefficient.
            int p = -1;
            double best = PivotTiny;
            for (int k = 0; k < d; k++)
            {
                double m = Math.Abs(row[k]);
                if (m > best)
                {
                    best = m;
                    p = k;
                }
            }

            if (p < 0)
            {
                // All coeffs ≈ 0: the constraint is 0 ≤ row[d]. Infeasible only if row[d] < 0.
                if (row[d] < -Eps)
                {
                    return false;
                }

                continue;
            }

            double ap = row[p];
            double bh = row[d];

            if (d == 1)
            {
                double xv = bh / ap;
                if (xv < lo[0] - Eps || xv > hi[0] + Eps)
                {
                    return false;
                }

                x[0] = xv;
                continue; // remaining rows are tested against the fixed point on the next iterations
            }

            // Reduce to d−1 variables (drop index p): substitute x[p] = (bh − Σ_{k≠p} row[k]·x[k]) / ap.
            double[][] rr = ws.Rows[d - 1];
            int rc = 0;
            for (int g = 0; g < h; g++)
            {
                double[] rg = rows[g];
                double factor = rg[p] / ap;
                double[] nr = rr[rc++];
                int idx = 0;
                for (int k = 0; k < d; k++)
                {
                    if (k == p)
                    {
                        continue;
                    }

                    nr[idx++] = rg[k] - (factor * row[k]);
                }

                nr[d - 1] = rg[d] - (factor * bh);
            }

            // The eliminated variable's box bounds become two constraints in the reduced space.
            FillBoxRow(rr[rc++], row, p, ap, bh, d, hi[p], upper: true);
            FillBoxRow(rr[rc++], row, p, ap, bh, d, lo[p], upper: false);

            double[] rObj = ws.Obj[d - 1];
            double[] rLo = ws.Lo[d - 1];
            double[] rHi = ws.Hi[d - 1];
            double cfac = c[p] / ap;
            int ci = 0;
            for (int k = 0; k < d; k++)
            {
                if (k == p)
                {
                    continue;
                }

                rObj[ci] = c[k] - (cfac * row[k]);
                rLo[ci] = lo[k];
                rHi[ci] = hi[k];
                ci++;
            }

            if (!SolveRec(d - 1, rc, ws))
            {
                return false;
            }

            double[] xr = ws.Sol[d - 1];
            int ri = 0;
            double sum = 0;
            for (int k = 0; k < d; k++)
            {
                if (k == p)
                {
                    continue;
                }

                x[k] = xr[ri];
                sum += row[k] * xr[ri];
                ri++;
            }

            x[p] = (bh - sum) / ap;
        }

        return true;
    }

    /// <summary>Writes the reduced-space constraint for a box bound on the eliminated variable x[p] into
    /// <paramref name="nr"/> (length d): x[p] ≤ bound when <paramref name="upper"/>, else x[p] ≥ bound, given
    /// x[p] = (bh − Σ_{k≠p} row[k]·x[k]) / ap.</summary>
    private static void FillBoxRow(double[] nr, double[] row, int p, double ap, double bh, int d, double bound, bool upper)
    {
        bool keep = upper ? ap < 0 : ap > 0; // whether the reduced coeffs keep +row[k] or negate it
        int idx = 0;
        for (int k = 0; k < d; k++)
        {
            if (k == p)
            {
                continue;
            }

            nr[idx++] = keep ? row[k] : -row[k];
        }

        nr[d - 1] = upper
            ? (ap > 0 ? (bound * ap) - bh : bh - (bound * ap))
            : (ap > 0 ? bh - (bound * ap) : (bound * ap) - bh);
    }

    /// <summary>Deterministic Fisher–Yates shuffle (fixed-seed xorshift) over the first <paramref name="count"/>
    /// row references. Seidel is correct for any order; the shuffle only buys expected-linear time, and a
    /// constant seed keeps every compile reproducible.</summary>
    private static void Shuffle(double[][] rows, int count)
    {
        ulong s = 0x9E3779B97F4A7C15UL ^ (ulong)count;
        for (int i = count - 1; i > 0; i--)
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            int j = (int)(s % (ulong)(i + 1));
            (rows[i], rows[j]) = (rows[j], rows[i]);
        }
    }
}
