using Silk.NET.OpenGL;

namespace Ged.Rendering.Rhi.Gl;

/// <summary>
/// Version / extension probes shared by the cross-platform offscreen contexts
/// (<see cref="EglOffscreenContext"/>, <see cref="GlxOffscreenContext"/>). The
/// Windows <see cref="WglOffscreenContext"/> keeps its own equivalents so L2's
/// tested path is untouched.
/// </summary>
internal static class GlVersion
{
    /// <summary>The GL major*10+minor version reported by the current context (e.g. 33 for 3.3).</summary>
    public static int QueryTens(GL gl)
    {
        int major = gl.GetInteger(GLEnum.MajorVersion);
        int minor = gl.GetInteger(GLEnum.MinorVersion);
        if (major == 0)
        {
            string v = gl.GetStringS(StringName.Version) ?? string.Empty;
            string[] parts = v.Split('.', ' ');
            if (parts.Length >= 2 && int.TryParse(parts[0], out major))
            {
                int.TryParse(parts[1], out minor);
            }
        }

        return (major * 10) + minor;
    }

    /// <summary>True when the named GL extension is present on the current core-profile context.</summary>
    public static bool HasExtension(GL gl, string name)
    {
        int count = gl.GetInteger(GLEnum.NumExtensions);
        for (uint i = 0; i < count; i++)
        {
            if (gl.GetStringS(StringName.Extensions, i) == name)
            {
                return true;
            }
        }

        return false;
    }
}
