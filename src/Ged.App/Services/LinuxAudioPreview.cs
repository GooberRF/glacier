using System;
using System.Diagnostics;
using System.IO;

namespace Ged.App.Services;

/// <summary>
/// The Linux <see cref="IAudioPreview"/>: plays a WAV preview by spawning the
/// system audio player — <c>paplay</c> (PulseAudio / PipeWire's pulse shim, present
/// on every mainstream desktop) with an <c>aplay</c> (ALSA) fallback. Chosen over a
/// native audio binding to keep the zero-native-payload story: no extra package,
/// nothing to bundle, and it uses whatever sound server the user already runs.
/// <para>
/// The in-memory WAV is written to a temp file (the players take a filename), the
/// player is spawned detached with its output suppressed, and the temp file is
/// removed when playback ends (or when the next preview / <see cref="Stop"/> runs).
/// <see cref="Play"/> returns false if neither player could be started.
/// </para>
/// </summary>
public sealed class LinuxAudioPreview : IAudioPreview
{
    // paplay first (PulseAudio/PipeWire), then aplay (ALSA) — both accept a WAV path.
    private static readonly string[] Players = { "paplay", "aplay" };

    private readonly object _gate = new();
    private Process? _process;
    private string? _tempFile;

    public bool Play(byte[] wavData)
    {
        ArgumentNullException.ThrowIfNull(wavData);
        Stop();

        string temp;
        try
        {
            temp = Path.Combine(Path.GetTempPath(), $"ged-preview-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(temp, wavData);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (string player in Players)
        {
            try
            {
                var psi = new ProcessStartInfo(player)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(temp);

                Process? proc = Process.Start(psi);
                if (proc is null)
                {
                    continue;
                }

                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => OnExited(proc, temp);

                lock (_gate)
                {
                    _process = proc;
                    _tempFile = temp;
                }

                return true;
            }
            catch (Exception)
            {
                // Player not installed / failed to launch — try the next one.
            }
        }

        TryDelete(temp);
        return false;
    }

    public void Stop()
    {
        Process? proc;
        string? temp;
        lock (_gate)
        {
            proc = _process;
            temp = _tempFile;
            _process = null;
            _tempFile = null;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch (Exception)
            {
                // Already gone — non-fatal.
            }
            finally
            {
                proc.Dispose();
            }
        }

        TryDelete(temp);
    }

    private void OnExited(Process proc, string temp)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_process, proc))
            {
                _process = null;
                _tempFile = null;
            }
        }

        TryDelete(temp);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }
}
