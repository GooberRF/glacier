namespace Ged.Core.IO.Rfl.Sections;

/// <summary>
/// A typed Alpine chunk that may be kept raw: when its bytes did not parse exactly (a truncated record, a count
/// over Alpine's cap, or fields a newer Alpine appended) it is written back verbatim and its records must not be
/// edited (ALPINE-COMPAT-SPEC §10 rule 3). Editing paths check this before they touch the chunk.
/// </summary>
public interface IRetainedChunk
{
    /// <summary>True when the chunk is written back verbatim and its records are read-only.</summary>
    bool IsReadOnly { get; }

    /// <summary>Why the chunk is read-only, or null.</summary>
    string? ReadOnlyReason { get; }
}
