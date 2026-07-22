using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Editing;
using Ged.Core.Editor;
using Ged.Core.Model;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// RED-parity defaults for a newly created Light: initial state On, enabled, shadow
/// casting, and shape Sphere (light_type Point / omnidirectional). Encoded in
/// light_flags as 0x21C = is_enabled(0x8) | shadow_casting(0x4) | type Point(0x10) |
/// initial_state On(0x200) — RED's most common authored value. The prior default 0x1
/// was the "dynamic" bit — which no stock light sets and which left the light disabled
/// with an invalid type.
/// </summary>
public sealed class LightCreateDefaultTests
{
    private static Light NewLight() =>
        (Light)ObjectFactory.Build(LevelObjectKind.Light, 1, Vec3.Zero).Model;

    [Fact]
    public void New_Light_Defaults_Enabled_On_ShadowCasting_And_Sphere()
    {
        Light l = NewLight();

        Assert.Equal(0x21Cu, l.Flags);
        Assert.True((l.Flags & 0x8u) != 0, "is_enabled bit must be set");
        Assert.True((l.Flags & 0x4u) != 0, "shadow_casting bit must be set");
        Assert.Equal(2u, (l.Flags >> 8) & 0xFu); // initial_state == On
        Assert.Equal(1u, (l.Flags >> 4) & 0x3u); // light_type == Point (omnidirectional / Sphere)
        Assert.Equal(0u, l.Flags & 0x1u);         // NOT dynamic
    }

    [Fact]
    public void New_Light_Flags_Survive_Save_Reload_And_Existing_Lights_Unchanged()
    {
        string? dm01 = Corpus.Available ? Path.Combine(Corpus.Directory!, "dm01.rfl") : null;
        if (dm01 is null || !File.Exists(dm01))
        {
            return;
        }

        var doc = EditorDocument.OpenBytes(File.ReadAllBytes(dm01), dm01);

        // Snapshot every pre-existing light's flags — the new default must not touch them.
        Dictionary<int, uint> before = doc.Objects
            .Where(o => o.Kind == LevelObjectKind.Light)
            .ToDictionary(o => o.Uid, o => ((Light)o.Model).Flags);

        LevelObject? placed = doc.PlaceObject(LevelObjectKind.Light, new Vec3(5, 5, 5));
        int uid = placed!.Uid;

        var reloaded = EditorDocument.OpenBytes(doc.SaveToBytes());

        var newLight = (Light)reloaded.FindByUid(uid)!.Model;
        Assert.Equal(0x21Cu, newLight.Flags);

        foreach (var (existingUid, flags) in before)
        {
            var back = (Light)reloaded.FindByUid(existingUid)!.Model;
            Assert.Equal(flags, back.Flags);
        }
    }
}
