namespace Ged.Core.Editor;

/// <summary>
/// Thrown by every save of a level Glacier must not write: one saved by a newer Alpine Faction than Glacier
/// knows (RFL version above 306), which opens read-only (ALPINE-COMPAT-SPEC §2, owner decision Q4). The message
/// is user-facing.
/// </summary>
public sealed class LevelSaveBlockedException : InvalidOperationException
{
    public LevelSaveBlockedException(string message)
        : base(message)
    {
    }

    public LevelSaveBlockedException()
    {
    }

    public LevelSaveBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
