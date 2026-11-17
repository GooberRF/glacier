using System.Linq;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Editing;

/// <summary>
/// Validates a proposed link from one object to another against the RED
/// originator rule (Triggers/Events/Clutter/Nav Points, plus Alpine's
/// navpoint→event), duplicate rejection, self-link rejection, and — for event
/// originators — the schema's expected link-target kinds.
/// </summary>
public static class LinkRules
{
    public const string OriginatorMessage =
        "Links can only be created from Triggers, Events, Clutter, Nav Points, and Waypoint Helpers.";

    public static LinkResult Validate(LevelObject origin, LevelObject target)
    {
        if (LinkModel.LinksOf(origin) is not { } links)
        {
            return LinkResult.Reject(OriginatorMessage);
        }

        if (ReferenceEquals(origin, target) || origin.Uid == target.Uid)
        {
            return LinkResult.Reject("An object cannot be linked to itself.");
        }

        if (links.Contains(target.Uid))
        {
            return LinkResult.Reject($"Already linked to UID {target.Uid}.");
        }

        // A waypoint helper links only to other waypoint helpers (its routes; editor_patch/waypoint_helper.cpp:384-388).
        if (origin.Model is AlpineWaypointHelper && target.Model is not AlpineWaypointHelper)
        {
            return LinkResult.Reject("A Waypoint Helper links only to other Waypoint Helpers.");
        }

        // Alpine saves at most 64 links per helper and drops the rest (waypoint_helper.cpp:270-292).
        if (origin.Model is AlpineWaypointHelper && links.Count >= AlpineWaypointHelperLimits.MaxLinks)
        {
            return LinkResult.Reject($"A Waypoint Helper keeps at most {AlpineWaypointHelperLimits.MaxLinks} links.");
        }

        // Event originators constrain their target kind via the catalog.
        if (origin.Model is RflEvent ev && EventSchemaCatalog.Find(ev.ClassName) is { } schema
            && !TargetKindAllowed(schema, target.Kind))
        {
            return LinkResult.Reject(
                $"{ev.ClassName} does not link to a {target.Kind}.");
        }

        return LinkResult.Allow();
    }

    /// <summary>Whether an event of <paramref name="schema"/> may link to a <paramref name="targetKind"/>.</summary>
    public static bool TargetKindAllowed(EventSchema schema, LevelObjectKind targetKind)
    {
        if (schema.LinkTargets.Count == 0)
        {
            return true;
        }

        // A link to a mover is stored as its start keyframe (the mover's UID in game; Lua link_to(mover) and
        // RED both write it that way), so a keyframe is a mover target.
        if (targetKind == LevelObjectKind.Keyframe && schema.LinkTargets.Contains(EventLinkTarget.Mover))
        {
            return true;
        }

        EventLinkTarget mapped = LinkModel.ToTarget(targetKind);
        foreach (EventLinkTarget t in schema.LinkTargets)
        {
            if (t is EventLinkTarget.Any or EventLinkTarget.Room)
            {
                return true;
            }

            if (t == mapped)
            {
                return true;
            }

            if (t == EventLinkTarget.Object && LinkModel.IsPhysicalObject(targetKind))
            {
                return true;
            }
        }

        return false;
    }
}
