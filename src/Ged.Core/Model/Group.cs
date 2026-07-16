namespace Ged.Core.Model;

/// <summary>
/// An editor group or moving group (RFL <c>group</c>). Used by both the groups
/// (0x3000000) and moving_groups (0x3000) sections and by .rfg files.
/// </summary>
public sealed class Group
{
    public string Name { get; set; } = string.Empty;

    /// <summary>rfl.ksy <c>unknown</c> byte (typically 0). Preserved exactly.</summary>
    public byte Unknown { get; set; }

    public byte IsMoving { get; set; }

    /// <summary>Present iff <see cref="IsMoving"/> != 0.</summary>
    public MovingGroupData? MovingData { get; set; }

    /// <summary>Non-brush member UIDs.</summary>
    public List<int> Objects { get; set; } = new();

    /// <summary>Brush member UIDs.</summary>
    public List<int> Brushes { get; set; } = new();
}
