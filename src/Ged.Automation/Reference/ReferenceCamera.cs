using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Ged.Automation.Reference;

/// <summary>What <see cref="ReferenceCamera.Estimate"/> read from a reference's marks.</summary>
public sealed class CameraEstimate
{
    /// <summary>The camera pitch in degrees (positive = looking up), from the horizon (or the vanishing point).</summary>
    public double? PitchDeg { get; set; }

    /// <summary>The picture's roll in degrees (positive = the horizon falls to the right); renders have no roll.</summary>
    public double? RollDeg { get; set; }

    /// <summary>The horizon's height as a fraction of the picture height (0 = top), after removing the roll.</summary>
    public double? Horizon { get; set; }

    /// <summary>How far right of the camera's heading the vanishing lines run, degrees.</summary>
    public double? VpOffsetDeg { get; set; }

    /// <summary>The camera's world heading in degrees (0 = +Z, 90 = +X), when the lines' world direction was given.</summary>
    public double? HeadingDeg { get; set; }

    /// <summary>The eye's height above the known object's base, metres (from the object mark).</summary>
    public double? EyeHeight { get; set; }

    /// <summary>The horizontal distance from the eye to the known object, metres.</summary>
    public double? ObjectDistance { get; set; }

    /// <summary>The eye height the measurements used, metres, and where it came from.</summary>
    public double? MeasureEyeHeight { get; set; }

    /// <summary>Where <see cref="MeasureEyeHeight"/> came from: object | camera | default.</summary>
    public string? MeasureEyeHeightSource { get; set; }

    /// <summary>The measured spans, in mark order.</summary>
    public List<MeasuredSpan>? Measurements { get; set; }

    /// <summary>Caveats about the estimate.</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>One measured vertical span (a wall, a doorway, a stack of crates) standing on the camera's floor.</summary>
public sealed class MeasuredSpan
{
    /// <summary>The mark [x, baseY, topY] (fractions).</summary>
    public double[] Mark { get; set; } = Array.Empty<double>();

    /// <summary>Its height in metres (null when its base is not below the horizon).</summary>
    public double? Height { get; set; }

    /// <summary>Its horizontal distance from the eye in metres.</summary>
    public double? Distance { get; set; }
}

/// <summary>
/// The camera math of reference matching: field-of-view conversion, headings and pitches, camera-frame nudges, height
/// presets, and single-view estimates (pitch from the horizon, heading from a vanishing point, eye height and
/// distances from a vertical object of known size) for a pinhole camera with square pixels. Coordinates are Glacier's:
/// left-handed, +Y up, heading 0 = +Z, 90 = +X. Picture marks are fractions of width and height from the top-left.
/// </summary>
public static class ReferenceCamera
{
    /// <summary>The default horizontal field of view (degrees): about Red Faction's default view.</summary>
    public const double DefaultHfov = 90;

    /// <summary>The default eye height above the floor (metres): a standing player.</summary>
    public const double StandingEye = 1.7;

    /// <summary>Named eye heights (metres above the floor).</summary>
    public static readonly IReadOnlyDictionary<string, double> HeightPresets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["eye"] = StandingEye,
        ["crouch"] = 1.1,
        ["jump"] = 3.0,
    };

    /// <summary>Resolves an eye-height preset name or a number of metres.</summary>
    public static double ParseHeight(string text)
    {
        if (HeightPresets.TryGetValue(text.Trim(), out double preset))
        {
            return preset;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m is >= 0.05 and <= 200)
        {
            return m;
        }

        throw new AutomationException(AutomationErrorCodes.Usage,
            $"Eye height '{text}' must be metres (0.05-200) or a preset: {string.Join(", ", HeightPresets.Select(p => $"{p.Key} ({p.Value.ToString(CultureInfo.InvariantCulture)} m)"))}.");
    }

    /// <summary>The vertical field of view (degrees) of a horizontal one at an aspect (width / height).</summary>
    public static double VerticalFov(double hfovDeg, double aspect) =>
        2 * Math.Atan(Math.Tan(hfovDeg * Math.PI / 360) / aspect) * 180 / Math.PI;

    /// <summary>The focal length in picture heights (fy = 0.5 / tan(vfov / 2)).</summary>
    public static double FocalHeights(double hfovDeg, double aspect) =>
        0.5 / Math.Tan(VerticalFov(hfovDeg, aspect) * Math.PI / 360);

    /// <summary>The heading (degrees, 0 = +Z, 90 = +X) of a direction.</summary>
    public static double Heading(Vector3 d) => Math.Atan2(d.X, d.Z) * 180 / Math.PI;

    /// <summary>The pitch (degrees, positive = up) of a direction.</summary>
    public static double Pitch(Vector3 d)
    {
        float len = d.Length();
        return len < 1e-9f ? 0 : Math.Asin(Math.Clamp(d.Y / len, -1f, 1f)) * 180 / Math.PI;
    }

    /// <summary>The unit direction of a heading and pitch (degrees).</summary>
    public static Vector3 Direction(double headingDeg, double pitchDeg)
    {
        double h = headingDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180;
        return new Vector3((float)(Math.Sin(h) * Math.Cos(p)), (float)Math.Sin(p), (float)(Math.Cos(h) * Math.Cos(p)));
    }

    /// <summary>
    /// Moves a camera in its own frame and turns it: <paramref name="move"/> = (forward, right, up) metres, forward and
    /// right along the ground (so moving never changes the height), up along +Y; <paramref name="turn"/> = (heading, pitch)
    /// degrees added (positive turns right / up; the pitch stays within ±89). The look point keeps its distance.
    /// </summary>
    public static (Vector3 Eye, Vector3 Look) Nudge(Vector3 eye, Vector3 look, Vector3 move, Vector2 turn)
    {
        Vector3 d = look - eye;
        float dist = MathF.Max(d.Length(), 1f);
        double heading = Heading(d), pitch = Pitch(d);
        double h = heading * Math.PI / 180;
        var forward = new Vector3((float)Math.Sin(h), 0, (float)Math.Cos(h));
        var right = new Vector3((float)Math.Cos(h), 0, (float)-Math.Sin(h));
        Vector3 e = eye + (forward * move.X) + (right * move.Y) + (Vector3.UnitY * move.Z);
        double newHeading = heading + turn.X, newPitch = Math.Clamp(pitch + turn.Y, -89, 89);
        return (e, e + (Direction(newHeading, newPitch) * dist));
    }

    /// <summary>A camera at <paramref name="eye"/> looking along a heading and pitch (the look point 10 m out).</summary>
    public static Vector3 LookAlong(Vector3 eye, double headingDeg, double pitchDeg) => eye + (Direction(headingDeg, pitchDeg) * 10f);

    /// <summary>
    /// The picture row (fraction from the top) where the horizon of a camera pitched <paramref name="pitchDeg"/> falls.
    /// </summary>
    public static double HorizonRow(double pitchDeg, double hfovDeg, double aspect) =>
        0.5 + (FocalHeights(hfovDeg, aspect) * Math.Tan(pitchDeg * Math.PI / 180));

    /// <summary>
    /// Reads camera cues from marks on a picture of <paramref name="aspect"/> taken with <paramref name="hfovDeg"/>:
    /// pitch and roll from the horizon (or the vanishing point when no horizon is marked), the heading offset of the
    /// vanishing lines (and the world heading when their direction is given), the eye height and distance from a
    /// vertical object of known height, and the heights of marked spans on the same floor. Pinhole camera, square pixels;
    /// objects are assumed vertical and standing on level ground.
    /// </summary>
    public static CameraEstimate Estimate(ReferenceMarks marks, double aspect, double hfovDeg, double? cameraEyeHeight)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var e = new CameraEstimate();
        double fy = FocalHeights(hfovDeg, aspect);

        // Picture fractions → centred coordinates in picture heights (u right, v up).
        (double U, double V) P(double x, double y) => ((x - 0.5) * aspect, 0.5 - y);

        double roll = 0;
        double? vh = null;
        if (marks.Horizon is { Length: 1 } level)
        {
            vh = P(0.5, level[0]).V;
        }
        else if (marks.Horizon is { Length: 4 } line)
        {
            (double u1, double v1) = P(line[0], line[1]);
            (double u2, double v2) = P(line[2], line[3]);
            if (Math.Abs(u2 - u1) < 1e-6)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "The two horizon points must lie at different x.");
            }

            roll = Math.Atan2(v2 - v1, u2 - u1);
            if (u2 < u1)
            {
                roll = Math.Atan2(v1 - v2, u1 - u2);
            }

            // The horizon's height at the picture's centre column, measured perpendicular once the roll is removed.
            vh = (v1 - (u1 * Math.Tan(roll))) * Math.Cos(roll);
            e.RollDeg = Round(-roll * 180 / Math.PI);
        }
        else if (marks.Horizon is not null)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "horizon is [y] or [x1, y1, x2, y2] (fractions of the picture).");
        }

        (double U, double V) Level(double x, double y)
        {
            (double u, double v) = P(x, y);
            double c = Math.Cos(-roll), s = Math.Sin(-roll);
            return ((u * c) - (v * s), (u * s) + (v * c));
        }

        (double U, double V)? vp = marks.Vp is { Length: 2 } vpm ? Level(vpm[0], vpm[1]) : null;
        if (marks.Vp is { } bad && bad.Length != 2)
        {
            throw new AutomationException(AutomationErrorCodes.Usage, "vp is [x, y] (fractions of the picture).");
        }

        if (vh is null && vp is { } onlyVp)
        {
            vh = onlyVp.V;
            e.Notes.Add("No horizon marked: the vanishing point's height stands in for it (true when its lines are level).");
        }

        double pitch = 0;
        if (vh is double horizonV)
        {
            pitch = Math.Atan(-horizonV / fy);
            e.PitchDeg = Round(pitch * 180 / Math.PI);
            e.Horizon = Round(0.5 - horizonV, 4);
        }
        else
        {
            e.Notes.Add("No horizon or vanishing point marked: the pitch is taken as 0 (level) for the measurements.");
        }

        // A camera-space ray (u, v, fy) turned by the pitch: its elevation tangent and its azimuth from the heading.
        (double TanElev, double Azimuth) Ray(double u, double v)
        {
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            double wy = (v * cp) + (fy * sp), wz = (-v * sp) + (fy * cp), wx = u;
            return (wy / Math.Sqrt((wx * wx) + (wz * wz)), Math.Atan2(wx, wz));
        }

        if (vp is { } v0)
        {
            double az = Ray(v0.U, v0.V).Azimuth;
            e.VpOffsetDeg = Round(az * 180 / Math.PI);
            if (marks.VpDir is { Length: 2 } dir)
            {
                if (Math.Abs(dir[0]) + Math.Abs(dir[1]) < 1e-9)
                {
                    throw new AutomationException(AutomationErrorCodes.Usage, "vpDir must be a non-zero world direction [x, z].");
                }

                double lines = Math.Atan2(dir[0], dir[1]) * 180 / Math.PI;
                e.HeadingDeg = Round(NormalizeDeg(lines - (az * 180 / Math.PI)));
            }
        }

        double? objectEye = null;
        if (marks.Object is { } obj)
        {
            if (obj.Length != 4 || obj[3] <= 0)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "object is [x, baseY, topY, heightMetres] (fractions of the picture, then metres > 0).");
            }

            (double ub, double vb) = Level(obj[0], obj[1]);
            (double ut, double vt) = Level(obj[0], obj[2]);
            double tb = Ray(ub, vb).TanElev, tt = Ray(ut, vt).TanElev;
            if (tt - tb <= 1e-6)
            {
                e.Notes.Add("The object's top is not above its base in the picture: no eye height from it.");
            }
            else
            {
                double dist = obj[3] / (tt - tb);
                objectEye = -dist * tb;
                e.EyeHeight = Round(objectEye.Value);
                e.ObjectDistance = Round(dist);
                if (objectEye < 0)
                {
                    e.Notes.Add("The object's base is above the eye: the eye height is negative relative to the floor it stands on.");
                }
            }
        }

        if (marks.Measure is { } m)
        {
            if (m.Length == 0 || m.Length % 3 != 0)
            {
                throw new AutomationException(AutomationErrorCodes.Usage, "measure is [x, baseY, topY] triples (fractions), flattened.");
            }

            double eye = objectEye ?? cameraEyeHeight ?? StandingEye;
            e.MeasureEyeHeight = Round(eye);
            e.MeasureEyeHeightSource = objectEye is not null ? "object" : cameraEyeHeight is not null ? "camera" : "default";
            e.Measurements = new List<MeasuredSpan>();
            for (int i = 0; i < m.Length; i += 3)
            {
                (double ub, double vb) = Level(m[i], m[i + 1]);
                (double ut, double vt) = Level(m[i], m[i + 2]);
                double tb = Ray(ub, vb).TanElev, tt = Ray(ut, vt).TanElev;
                var span = new MeasuredSpan { Mark = new[] { m[i], m[i + 1], m[i + 2] } };
                if (tb < -1e-6 && eye > 0)
                {
                    double dist = eye / -tb;
                    span.Distance = Round(dist);
                    span.Height = Round(dist * (tt - tb));
                }

                e.Measurements.Add(span);
            }

            if (e.Measurements.Any(s => s.Height is null))
            {
                e.Notes.Add("A span whose base is not below the horizon cannot be measured from the eye height (its floor is not the camera's).");
            }
        }

        e.Notes.Add("A single-view estimate (pinhole camera, level floor, vertical objects): good to a few percent for a real camera, rougher for painted concept art. Use it to start, then judge by the comparison.");
        return e;
    }

    /// <summary>An angle in degrees in (-180, 180].</summary>
    public static double NormalizeDeg(double deg)
    {
        double d = deg % 360;
        if (d <= -180)
        {
            d += 360;
        }
        else if (d > 180)
        {
            d -= 360;
        }

        return d;
    }

    private static double Round(double v, int digits = 2) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
}
