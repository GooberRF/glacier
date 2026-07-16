namespace Ged.Core.Model;

/// <summary>Maps a parent room to the rooms it contains (RFL <c>subroom_list</c>).</summary>
public sealed class SubroomList
{
    public int RoomIndex { get; set; }

    public List<int> SubroomIndices { get; set; } = new();
}
