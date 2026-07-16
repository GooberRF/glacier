using Xunit;

namespace Ged.Rendering.Tests;

/// <summary>
/// Serializes every test class that creates a Direct3D 11 <c>GraphicsDevice</c>.
/// xUnit parallelizes distinct test classes across threads by default; a D3D11
/// device carries a non-thread-safe immediate context and shares driver/DXGI
/// state, so two fixtures creating, rendering with, and disposing devices at the
/// same time can corrupt native memory (observed as an intermittent
/// <see cref="System.AccessViolationException"/> that crashes the test host).
/// The crash surfaced almost exclusively under solution-level <c>dotnet test</c>,
/// where the parallel Ged.Core.Tests process (a CPU-saturating multithreaded
/// lightmapper recompiling the corpus) widened the timing window.
///
/// Placing all GPU-touching classes in one collection with
/// <c>DisableParallelization</c> guarantees at most one live device at a time
/// without weakening a single assertion. Pure-CPU rendering tests (camera math,
/// pick-id encoding, scene/overlay builders, brush emitters) stay parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuTestCollection
{
    public const string Name = "GPU";
}
