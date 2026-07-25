using System;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// Resolves the UID a link should actually store when its target is a mover. RF.exe identifies a
/// mover's runtime object by its <b>start keyframe's UID</b> — the moving-group instantiation
/// <c>FUN_00469250</c> (RF.exe @0x00469250) sets the mover object's uid slot
/// (<c>*(mover+0x20)</c>) to <c>keyframe[0].uid</c>, so a trigger/event that links to a mover must
/// reference that start-keyframe UID, not a member brush. This is confirmed in RED-authored
/// levels: every dmabrupt trigger that drives a mover links to the mover's start-keyframe UID
/// (e.g. trigger 54→49, 93→10320, 264→266, 10182→10180), never to a mover brush UID.
/// <para>
/// Glacier projects a mover's member brush as a whole-object <see cref="LevelObjectKind.Mover"/>
/// whose UID is the <b>brush</b> UID, so a raw click-to-link on the mover body would store the
/// brush UID — a link RF cannot resolve to the mover, i.e. "trigger fires, nothing moves". This
/// helper redirects such a target to the owning moving group's start-keyframe UID so a
/// Glacier-authored trigger→mover link matches a RED-authored one in shape.
/// </para>
/// </summary>
public static class MoverLinkResolver
{
    /// <summary>
    /// If <paramref name="targetUid"/> is a mover brush that belongs to a moving group, returns that
    /// group's start-keyframe UID; otherwise returns <paramref name="targetUid"/> unchanged (a link
    /// straight to a keyframe, or to any non-mover object, is already correct).
    /// </summary>
    public static int ResolveTarget(EditorDocument doc, int targetUid)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (Group g in DocumentLinks.MovingGroups(doc))
        {
            if (g.IsMoving == 0 || g.MovingData is not { } data || data.Keyframes.Count == 0)
            {
                continue;
            }

            // Only a *brush* member is a mover activation target; member objects (a trigger/clutter
            // that merely rides the platform) keep their own identity.
            if (g.Brushes.Contains(targetUid))
            {
                int start = Math.Clamp(data.StartingKeyframe, 0, data.Keyframes.Count - 1);
                return data.Keyframes[start].Uid;
            }
        }

        return targetUid;
    }
}
