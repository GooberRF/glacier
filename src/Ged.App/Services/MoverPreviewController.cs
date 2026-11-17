using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Threading;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Ged.Rendering.Scene;
using CoreVec3 = Ged.Core.Model.Vec3;

namespace Ged.App.Services;

/// <summary>
/// The viewport mover preview (Alpine RED's Preview, editor_patch/mover_path.cpp:1044-1273, 1867-1941): plays one
/// moving group with the game's timing and path (<see cref="MoverPlayback"/>) and draws its members at the simulated
/// pose as a ghost over the level. Play/pause, stop (back to the rest pose), a scrub bar and a speed choice. It never
/// writes the document, so stopping or closing leaves the level exactly as it was and nothing to undo.
/// </summary>
internal sealed class MoverPreviewController
{
    /// <summary>The speeds the preview offers (Alpine RED: 0.25x, 0.5x, 1x, 2x).</summary>
    public static readonly IReadOnlyList<(float Speed, string Label)> Speeds = new[] { (0.25f, "0.25x"), (0.5f, "0.5x"), (1f, "1x"), (2f, "2x") };

    private readonly Func<EditorDocument?> _document;
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private TimeSpan _lastTick;

    public MoverPreviewController(Func<EditorDocument?> document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>Raised whenever the pose, play state or status changes (redraw the ghost, update the transport).</summary>
    public event Action? Changed;

    /// <summary>The mover being previewed, or null.</summary>
    public Group? Group { get; private set; }

    public MoverPlayback? Playback { get; private set; }

    public bool Active => Playback is not null;

    public bool Playing => Playback?.Playing == true;

    public float Speed { get; set; } = 1f;

    /// <summary>When false, <see cref="PlayPause"/> runs no timer; tests drive <see cref="Tick"/> themselves.</summary>
    public bool UseTimer { get; set; } = true;

    public string Status => Playback?.Status ?? string.Empty;

    /// <summary>
    /// Opens the preview on <paramref name="group"/> at rest; returns null when it can play, or why it cannot
    /// (Alpine RED's messages, mover_path.cpp:1878-1889).
    /// </summary>
    public string? Start(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        EditorDocument? doc = _document();
        if (doc is null || MoverPlaybackPath.From(doc.Rfl, group) is not { } path)
        {
            return "Select a keyframe or a member of a moving group to preview it.";
        }

        var playback = new MoverPlayback(path);
        if (!playback.CanPlay)
        {
            return "This moving group does not have enough keyframes to preview.";
        }

        StopTimer();
        Group = group;
        Playback = playback;
        Changed?.Invoke();
        return null;
    }

    /// <summary>Re-reads the mover after an edit, keeping the run where it is when it still fits.</summary>
    public void Refresh()
    {
        if (Group is not { } group || Playback is not { } old || _document() is not { } doc)
        {
            return;
        }

        if (group.MovingData is null || MoverPlaybackPath.From(doc.Rfl, group) is not { } path)
        {
            Close();
            return;
        }

        var playback = new MoverPlayback(path);
        if (!playback.CanPlay)
        {
            Close();
            return;
        }

        if (old.State.From < path.Count && old.State.To < path.Count)
        {
            playback.Scrub(old.RunFraction);
            if (old.Playing)
            {
                playback.PlayPause();
            }
        }

        Playback = playback;
        Changed?.Invoke();
    }

    public void PlayPause()
    {
        if (Playback is not { } p)
        {
            return;
        }

        p.PlayPause();
        if (p.Playing)
        {
            StartTimer();
        }
        else
        {
            StopTimer();
        }

        Changed?.Invoke();
    }

    /// <summary>Stops and returns the members to their rest pose.</summary>
    public void Stop()
    {
        StopTimer();
        Playback?.Stop();
        Changed?.Invoke();
    }

    public void Scrub(float fraction)
    {
        if (Playback is not { } p)
        {
            return;
        }

        StopTimer();
        p.Scrub(fraction);
        Changed?.Invoke();
    }

    /// <summary>Ends the preview; the ghost disappears.</summary>
    public void Close()
    {
        StopTimer();
        bool was = Active;
        Playback = null;
        Group = null;
        if (was)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Advances the run by <paramref name="seconds"/> of real time at the chosen speed.</summary>
    public void Tick(float seconds)
    {
        if (Playback is not { Playing: true } p)
        {
            StopTimer();
            return;
        }

        p.Tick(MathF.Min(seconds, 0.1f) * Speed);
        if (!p.Playing)
        {
            StopTimer();
        }

        Changed?.Invoke();
    }

    /// <summary>The members drawn at the current pose: brush edges and a marker per member object.</summary>
    public List<LineSegment> GhostLines(MoverService movers)
    {
        var lines = new List<LineSegment>();
        if (Playback is not { } p || Group is not { } group || _document() is not { } doc)
        {
            return lines;
        }

        MoverPose pose = p.Pose();
        uint color = Palette.Rgba(255, 255, 0);
        lines.AddRange(OverlayBuilder.PosedMoverGhost(movers.CurrentMemberShapes(group), pose, color));

        foreach (int uid in group.Objects)
        {
            if (doc.FindByUid(uid) is { } o)
            {
                (CoreVec3 pos, _) = pose.Place(o.Position, Mat3.Identity);
                AddMarker(lines, new Vector3(pos.X, pos.Y, pos.Z), 0.5f, color);
            }
        }

        return lines;
    }

    /// <summary>The timing ticks of the previewed mover (equal time steps along its forward run).</summary>
    public List<LineSegment> TickLines()
    {
        var lines = new List<LineSegment>();
        if (Playback is not { } p)
        {
            return lines;
        }

        uint color = Palette.Rgba(120, 220, 255);
        foreach (Vector3 t in p.TimingTicks())
        {
            AddMarker(lines, t, 0.08f, color);
        }

        return lines;
    }

    private static void AddMarker(List<LineSegment> lines, Vector3 p, float r, uint color)
    {
        lines.Add(new LineSegment(p - new Vector3(r, 0, 0), p + new Vector3(r, 0, 0), color));
        lines.Add(new LineSegment(p - new Vector3(0, r, 0), p + new Vector3(0, r, 0), color));
        lines.Add(new LineSegment(p - new Vector3(0, 0, r), p + new Vector3(0, 0, r), color));
    }

    private void StartTimer()
    {
        if (!UseTimer)
        {
            return;
        }

        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => OnTimer());
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private void OnTimer()
    {
        TimeSpan now = _clock.Elapsed;
        float dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        Tick(dt);
    }
}
