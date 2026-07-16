using System.Numerics;
using Ged.Rendering.Graphics;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;

namespace Ged.Rendering.Tests;

/// <summary>
/// Shared helpers for the rendering tests: repo/corpus/artifact paths, a
/// best-effort GPU device factory (tests that need a device skip gracefully when
/// none can be created), and small synthetic scene builders.
/// </summary>
internal static class RenderTestSupport
{
    public static string? RepoRoot { get; } = LocateRepoRoot();

    public static string? Corpus { get; } = Sub("research", "example_rfls");

    /// <summary>Committed test fixtures directory (<c>tests/fixtures</c>), or null.</summary>
    public static string? Fixtures { get; } = Sub("tests", "fixtures");

    /// <summary>Read-only research fixtures (<c>research/fixtures</c>) holding retail-derived
    /// binaries kept out of the public repo (gitignored), or null.</summary>
    public static string? ResearchFixtures { get; } = Sub("research", "fixtures");

    public static string ArtifactsDir { get; } = InitArtifacts();

    /// <summary>Root of a real RF install (read-only), located like the Core tests do.</summary>
    public static string? RfInstall { get; } = LocateRfInstall();

    /// <summary>
    /// Experiment hook: the geometry-recompiling parity gates now build with the TRUE shipping default compile
    /// options — RED's authentic SHARED BSP (the owner-approved flip; CompileOptions.SharedBsp defaults true).
    /// Setting GED_FORCE_PERBRUSH=1 forces them onto the LEGACY per-brush accumulator for A/B comparison; the
    /// caller turns off BOTH the shared-BSP branch (SharedBsp=false, dispatched first) and the incremental branch
    /// (IncrementalAccumulator=false) so the build falls through to the per-brush path. The plain suite covers the
    /// real default (shared BSP); the env var is the explicit legacy override.
    /// </summary>
    public static bool ForcePerBrush { get; } =
        System.Environment.GetEnvironmentVariable("GED_FORCE_PERBRUSH") == "1";

    /// <summary>Creates a shared device, or returns null (with a reason) if D3D11 is unavailable.</summary>
    public static GraphicsDevice? TryCreateDevice(out string reason) =>
        TryCreateDevice(GraphicsBackend.Direct3D11, out reason);

    /// <summary>
    /// Creates a device on the requested backend, or returns null (with a reason) when
    /// that backend is unavailable — so the backend-parity tests skip gracefully on a
    /// box without D3D11 hardware or without an OpenGL 3.3 core context, exactly like
    /// the existing render tests skip without a GPU.
    /// </summary>
    public static GraphicsDevice? TryCreateDevice(GraphicsBackend backend, out string reason)
    {
        try
        {
            var gd = new GraphicsDevice(backend);
            reason = gd.IsWarp ? "software" : "hardware";
            return gd;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    public static string? CorpusFile(string name)
    {
        if (Corpus is null)
        {
            return null;
        }

        string p = Path.Combine(Corpus, name);
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Resolves a fixture file, preferring the committed <c>tests/fixtures</c> tree (synthetic
    /// assets) and falling back to <c>research/fixtures</c> (retail-derived binaries kept out of
    /// the public repo). Returns null when it is in neither root, so a test needing a
    /// retail-derived fixture can skip gracefully on a checkout that lacks it.
    /// </summary>
    public static string? FixtureFile(params string[] parts)
    {
        foreach (string? root in new[] { Fixtures, ResearchFixtures })
        {
            if (root is null)
            {
                continue;
            }

            string p = Path.Combine(new[] { root }.Concat(parts).ToArray());
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Existing fixture sub-directories (e.g. "tex" or "mesh") across both fixture roots —
    /// committed <c>tests/fixtures</c> first, then <c>research/fixtures</c> — for mounting an
    /// <c>AssetVfs</c>. Only directories that exist are returned.
    /// </summary>
    public static IEnumerable<string> FixtureDirs(string sub)
    {
        foreach (string? root in new[] { Fixtures, ResearchFixtures })
        {
            if (root is null)
            {
                continue;
            }

            string dir = Path.Combine(root, sub);
            if (Directory.Exists(dir))
            {
                yield return dir;
            }
        }
    }

    /// <summary>True if fewer than a threshold fraction of pixels equal the dominant color (image is non-trivial).</summary>
    public static bool IsNonTrivial(byte[] rgba, out int distinctColors)
    {
        var counts = new Dictionary<uint, int>();
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            uint c = (uint)(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16) | (rgba[i + 3] << 24));
            counts.TryGetValue(c, out int n);
            counts[c] = n + 1;
        }

        distinctColors = counts.Count;
        if (counts.Count <= 1)
        {
            return false;
        }

        int total = rgba.Length / 4;
        int dominant = counts.Values.Max();
        return dominant < (int)(total * 0.985);
    }

    /// <summary>A single opaque quad facing -Z at z=5, filling the view of a camera at the origin.</summary>
    public static RenderScene QuadScene()
    {
        var scene = new RenderScene();
        var batch = new GeometryBatch(string.Empty, -1, RenderPass.Opaque);
        Vector3 n = new(0f, 0f, -1f);
        uint col = Palette.Rgba(200, 120, 80);
        uint pick = new PickId(PickKind.Face, 0).Encode();

        void V(float x, float y, float u, float v) => batch.Vertices.Add(new WorldVertex
        {
            Position = new Vector3(x, y, 5f),
            Normal = n,
            TexCoord = new Vector2(u, v),
            LightmapCoord = Vector2.Zero,
            Color = col,
            PickId = pick,
        });

        V(-3f, -3f, 0f, 1f);
        V(3f, -3f, 1f, 1f);
        V(3f, 3f, 1f, 0f);
        V(-3f, 3f, 0f, 0f);
        // Winding-normal -Z so the front face points at the origin camera (survives
        // back-face culling): the quad genuinely faces the viewer, matching its -Z normal.
        batch.Indices.AddRange(new uint[] { 0, 2, 1, 0, 3, 2 });
        scene.Batches.Add(batch);
        return scene;
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glacier.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? Sub(params string[] parts)
    {
        if (RepoRoot is null)
        {
            return null;
        }

        string path = Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());
        return Directory.Exists(path) ? path : null;
    }

    private static string InitArtifacts()
    {
        string dir = RepoRoot is not null
            ? Path.Combine(RepoRoot, "tests", "artifacts")
            : Path.Combine(Path.GetTempPath(), "ged-render-artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? LocateRfInstall()
    {
        foreach (string c in RfDirCandidates())
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.vpp").Any())
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Candidate RF-install directories in priority order: the <c>GED_RF_DIR</c> environment
    /// variable first, then each non-comment line of the developer-local, gitignored
    /// <c>research/rf-dirs.txt</c> (one path per line, '#' comments allowed). No machine-specific
    /// paths are baked into source, so a public checkout resolves nothing and RF-dependent tests
    /// skip gracefully.
    /// </summary>
    private static IEnumerable<string> RfDirCandidates()
    {
        string? env = Environment.GetEnvironmentVariable("GED_RF_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        foreach (string line in ReadRfDirsFile())
        {
            yield return line;
        }
    }

    private static IReadOnlyList<string> ReadRfDirsFile()
    {
        var result = new List<string>();
        if (RepoRoot is null)
        {
            return result;
        }

        string path = Path.Combine(RepoRoot, "research", "rf-dirs.txt");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                result.Add(trimmed);
            }
        }
        catch
        {
            // Unreadable rf-dirs.txt -> no candidates; RF-dependent tests skip gracefully.
        }

        return result;
    }
}
