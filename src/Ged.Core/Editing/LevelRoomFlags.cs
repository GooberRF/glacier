using System;
using System.Collections.Generic;
using Ged.Core.Editor;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The Level Properties "Level Flags" buttons, reproduced from RED.exe (Outside 0x00468020, Inside 0x00468110,
/// controls 1215 / 1217): after RED's warning ("This will flag every single room of this level as outside / inside,
/// negating the effects of all Room Effects.", strings 0x0057A03C / 0x0057A0B8) every compiled room's outside flag and
/// every room effect's Room Is Outside flag are set (Outside) or cleared (Inside). The room effects carry the choice
/// into the next build; the compiled rooms take it at once. One undo step; a level already in that state changes
/// nothing.
/// </summary>
public static class LevelRoomFlags
{
    /// <summary>RED's confirmation text for <paramref name="outside"/>.</summary>
    public static string Warning(bool outside) =>
        $"Warning:  This will flag every single room of this level as {(outside ? "outside" : "inside")}, negating the effects of all Room Effects.\n\nProceed?";

    /// <summary>Flags every room outside (true) or inside (false). Returns the number of rooms and room effects changed.</summary>
    public static int SetAllRooms(EditorDocument doc, bool outside)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Rfl.ParseAllKnownSections();
        byte value = outside ? (byte)1 : (byte)0;
        var effects = new List<(RoomEffect Effect, byte Old)>();
        var rooms = new List<(Room Room, byte Old)>();
        var hosts = new List<RflSection>();
        foreach (RflSection s in doc.Rfl.Sections)
        {
            bool touched = false;
            if (s.Content is RoomEffectsSection fx)
            {
                foreach (RoomEffect e in fx.Effects)
                {
                    if (e.RoomIsOutside != value)
                    {
                        effects.Add((e, e.RoomIsOutside));
                        touched = true;
                    }
                }
            }
            else if (s.Content is GeometrySection { Type: SectionType.StaticGeometry } geo)
            {
                foreach (Room r in geo.Geometry.Rooms)
                {
                    if (r.IsOutside != value)
                    {
                        rooms.Add((r, r.IsOutside));
                        touched = true;
                    }
                }
            }

            if (touched)
            {
                hosts.Add(s);
            }
        }

        if (effects.Count + rooms.Count == 0)
        {
            return 0;
        }

        doc.Undo.Execute(new RelayCommand(outside ? "Flag all rooms outside" : "Flag all rooms inside",
            () =>
            {
                effects.ForEach(e => e.Effect.RoomIsOutside = value);
                rooms.ForEach(r => r.Room.IsOutside = value);
                hosts.ForEach(h => h.Dirty = true);
            },
            () =>
            {
                effects.ForEach(e => e.Effect.RoomIsOutside = e.Old);
                rooms.ForEach(r => r.Room.IsOutside = r.Old);
                hosts.ForEach(h => h.Dirty = true);
            }));
        return effects.Count + rooms.Count;
    }
}
