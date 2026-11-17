using System;
using System.Collections.Generic;

namespace Ged.Core.Model;

/// <summary>Weather region ranges (Alpine RED's dialog, editor_patch/weather_region.cpp:284-292).</summary>
public static class AlpineWeatherRegionLimits
{
    /// <summary>Box width / height / depth and sphere radius: RED's float spinner range 0..999.</summary>
    public const float MaxDimension = 999f;

    public const float MinDensityScale = 0f;

    public const float MaxDensityScale = 2f;

    public const float MinColumnWidth = 0.25f;

    public const float MaxColumnWidth = 8f;
}

/// <summary>Vehicle factory ranges and class rules.</summary>
public static class AlpineVehicleFactoryLimits
{
    /// <summary>Longest respawn delay in seconds (editor_patch/vehicle_factory.cpp:68).</summary>
    public const float MaxRespawnDelaySeconds = 3600f;

    /// <summary>Largest turret yaw limit in degrees; larger stored values read as 0 (common/include/common/vehicle_orient.h:7).</summary>
    public const byte MaxTurretYawLimitDegrees = 179;

    /// <summary>A new factory's class (editor_patch/mfc_types.h:814).</summary>
    public const string DefaultClass = "Jeep01";

    /// <summary>The stock vehicle and turret classes Alpine RED offers when entity.tbl cannot be read (vehicle_factory.cpp:32-39).</summary>
    public static IReadOnlyList<string> StockClasses { get; } = new[]
    {
        "Jeep01", "APC", "Fighter01", "sub", "Driller01", "Stationary Turret_Plain",
    };

    /// <summary>entity.tbl classes a factory cannot spawn (common/include/common/vehicle_meshes.h:10-25).</summary>
    public static IReadOnlyList<string> UnsupportedClasses { get; } = new[] { "masako_fighter", "Stationary Turret", "Shuttle" };

    /// <summary>True for a class Alpine refuses for factories (case-insensitive, vehicle_meshes.h:16-25).</summary>
    public static bool IsUnsupported(string className)
    {
        foreach (string u in UnsupportedClasses)
        {
            if (string.Equals(u, className, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Rope emitter ranges (common/include/common/rope_curve.h:83-116, 369-459).</summary>
public static class AlpineRopeLimits
{
    public const int MinSegments = 2;

    public const int MaxSegments = 64;

    public const float MinThickness = 0.001f;

    public const float MaxThickness = 10f;

    public const float MaxSlack = 1000f;

    public const float MinDangle = 0.01f;

    public const float MaxDangle = 1000f;

    public const float MinWeight = 0.01f;

    public const float MaxWeight = 100f;

    public const float MaxUvTiles = 1000f;

    public const float MaxSwayAmplitude = 100f;

    public const float MaxSwaySpeed = 100f;

    public const float MinZipSpeed = 4f;

    public const float MaxZipSpeed = 40f;

    public const uint FlagDynamic = 0x1;

    public const uint FlagGlow = 0x2;

    public const uint FlagSway = 0x4;

    public const uint FlagZipline = 0x8;

    public const int MaxDecorationMeshes = 6;

    public const int MinDecorationCount = 1;

    public const int MaxDecorationCount = 64;

    public const float MinDecorationSpacing = 0.05f;

    public const float MaxDecorationSpacing = 100f;
}

/// <summary>Directional light ranges (common/include/common/alpine_dir_light.h:17-28).</summary>
public static class AlpineDirectionalLightLimits
{
    public const float MaxIntensity = 10f;

    public const float MaxExtent = 10000f;

    public const float MaxFeather = 10000f;

    public const float MaxSpread = 45f;
}

/// <summary>Waypoint helper ranges (common/include/common/alpine_waypoint_helper.h:17-22).</summary>
public static class AlpineWaypointHelperLimits
{
    /// <summary>Links Alpine keeps per helper; extra links are dropped on save (waypoint_helper.cpp:270-292).</summary>
    public const int MaxLinks = 64;

    public const float MinRadius = 0.5f;

    public const float MaxRadius = 64f;
}

/// <summary>Liquid emission ranges (editor_patch/liquid_emission.h:35-36).</summary>
public static class AlpineLiquidEmissionLimits
{
    public const float MaxIntensity = 100f;

    public const float MaxDistance = 1000f;
}
