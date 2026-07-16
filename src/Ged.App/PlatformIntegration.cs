using System;
using Ged.App.Services;

namespace Ged.App;

/// <summary>
/// One-shot, platform-specific service wiring run at startup (CP3 / Linux bring-up).
/// The cross-platform singletons default to their Windows implementation
/// (<see cref="AudioPreview.Current"/> → winmm; <see cref="ViewportHost.Current"/> →
/// Win32); this assigns the non-Windows backends by OS detection so the rest of the
/// app is platform-agnostic.
/// </summary>
internal static class PlatformIntegration
{
    /// <summary>Assigns the OS-appropriate audio (and any future platform) backends. Idempotent.</summary>
    public static void Configure()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            // Ambient-sound preview via the system audio player (paplay/aplay) — no native payload.
            AudioPreview.Current = new LinuxAudioPreview();
        }

        // ViewportHost.Current is assigned by L3's Avalonia OpenGlControlBase host
        // (the cross-platform render surface); no Win32-specific override is needed here.
    }
}
