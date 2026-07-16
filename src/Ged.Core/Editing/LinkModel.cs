using System.Collections.Generic;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Core.Tables;

namespace Ged.Core.Editing;

/// <summary>Outcome of a link validity check: a flag and a user-facing message.</summary>
public readonly record struct LinkResult(bool Ok, string Message)
{
    public static LinkResult Allow() => new(true, string.Empty);

    public static LinkResult Reject(string message) => new(false, message);

    public static implicit operator bool(LinkResult r) => r.Ok;
}

/// <summary>
/// Access to the link list of the four originator object kinds (Triggers,
/// Events, Clutter, Nav Points) and the kind ↔ event-link-target mapping used by
/// the validator. Only these kinds carry a persisted <c>Links</c> array.
/// </summary>
public static class LinkModel
{
    /// <summary>The originator's persisted UID link list, or null when the kind can't originate links.</summary>
    public static List<int>? LinksOf(LevelObject o) => o.Model switch
    {
        Trigger t => t.Links,
        RflEvent e => e.Links,
        Clutter c => c.Links,
        NavPoint n => n.Links,
        _ => null,
    };

    /// <summary>RED rule: links originate only from Triggers, Events, Clutter, and Nav Points.</summary>
    public static bool CanOriginate(LevelObject o) => LinksOf(o) is not null;

    /// <summary>Maps a level-object kind onto the coarse event-link-target enum for validation.</summary>
    public static EventLinkTarget ToTarget(LevelObjectKind kind) => kind switch
    {
        LevelObjectKind.Entity => EventLinkTarget.Entity,
        LevelObjectKind.Item => EventLinkTarget.Item,
        LevelObjectKind.Clutter => EventLinkTarget.Clutter,
        LevelObjectKind.Light => EventLinkTarget.Light,
        LevelObjectKind.Trigger => EventLinkTarget.Trigger,
        LevelObjectKind.Event => EventLinkTarget.Event,
        LevelObjectKind.Mover => EventLinkTarget.Mover,
        LevelObjectKind.NavPoint => EventLinkTarget.NavPoint,
        LevelObjectKind.ParticleEmitter => EventLinkTarget.ParticleEmitter,
        LevelObjectKind.BoltEmitter => EventLinkTarget.BoltEmitter,
        LevelObjectKind.PushRegion => EventLinkTarget.PushRegion,
        LevelObjectKind.AmbientSound => EventLinkTarget.AmbientSound,
        LevelObjectKind.MpRespawnPoint => EventLinkTarget.RespawnPoint,
        _ => EventLinkTarget.Object,
    };

    /// <summary>True for kinds that count as a generic game "object" link target.</summary>
    public static bool IsPhysicalObject(LevelObjectKind kind) => kind switch
    {
        LevelObjectKind.Entity or LevelObjectKind.Item or LevelObjectKind.Clutter or LevelObjectKind.Mover
            or LevelObjectKind.Light or LevelObjectKind.MeshObject => true,
        _ => false,
    };
}
