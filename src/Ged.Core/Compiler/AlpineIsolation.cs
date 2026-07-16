using System.Collections.Generic;
using Ged.Core.Model;

namespace Ged.Core.Compiler;

/// <summary>
/// Records the compiled room UID each geoable/breakable brush isolated into, for the
/// alpine_level_properties chunk's brush-uid ↔ room-uid tables. Mirrors RED's save-time
/// <c>compute_geoable_room_uids</c> / <c>compute_breakable_room_uids</c>
/// (editor_patch/level.cpp): each such brush compiles to its own detail room (the room
/// builder isolates every brush in the isolated set), and the mapping is that room's id.
/// The set of isolated brushes is the union of geoable + breakable brush UIDs taken from
/// the alpine props — NOT a per-brush flag — so a geoable brush with infinite life still
/// maps correctly.
/// </summary>
public static class AlpineIsolation
{
    /// <summary>
    /// Resolves brush UID → compiled room UID for every brush the caller marked isolated,
    /// tracing the room builder's per-brush detail-room lookup. Brushes that did not reach
    /// a compiled room (e.g. fully carved away) are simply omitted.
    /// </summary>
    public static void RecordLinks(
        IReadOnlyCollection<int> isolatedBrushUids, RoomBuildResult rooms, CompiledLevel result)
    {
        foreach (int uid in isolatedBrushUids)
        {
            if (rooms.BrushRoom.TryGetValue(uid, out int roomIdx)
                && roomIdx >= 0 && roomIdx < rooms.Rooms.Count)
            {
                result.BrushRoomUid[uid] = rooms.Rooms[roomIdx].Id;
            }
        }
    }
}
