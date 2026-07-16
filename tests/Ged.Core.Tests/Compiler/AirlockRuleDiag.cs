using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ged.Core.Compiler;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Ged.Core.Tests.Compiler;

/// <summary>
/// ITEM 3 diagnostic — pins RED's <c>is_airlock</c> (+0x43) rule from ground truth. RED sets is_airlock
/// on 17 dmabrupt rooms (mostly detail subrooms) through a NON-room-effect mechanism the ledger left
/// unpinned. This dumps, from RED's own parsed geometry, exactly which rooms carry each of the
/// cold/outside/airlock flags and — decisively — whether every airlock SUBROOM is a subroom-child of an
/// airlock MAIN room (the inheritance hypothesis). Pure diagnostic, no asserts.
/// </summary>
[Trait("Category", "DeepGate")] // heavy corpus compile/bake; deep publish tier (docs/internal/TESTING-PROTOCOL.md)
public sealed class AirlockRuleDiag
{
    private const string Level = "dmabruptdecayrc2a27.rfl";
    private readonly ITestOutputHelper _out;

    public AirlockRuleDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Report_Airlock_Ground_Truth()
    {
        if (!Corpus.Available)
        {
            return;
        }

        string path = Path.Combine(Corpus.Directory!, Level);
        if (!File.Exists(path))
        {
            return;
        }

        RflFile rfl = RflFile.Load(path);
        rfl.ParseAllKnownSections();
        Geometry red = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().First().Geometry;

        // GED's own airlock output (from a full recompile) — the honest before number.
        CompiledLevel gedBuild = GeometryBuildService.Build(
            rfl, new CompileOptions { Alpine = true, BuildSurfaces = false });
        int gedAirlock = gedBuild.Geometry.Rooms.Count(r => r.IsAirlock != 0);
        _out.WriteLine($"GED recompile airlock rooms = {gedAirlock}");

        // Dump every room effect and its cold/outside/airlock flags — the ONLY RED build path that sets
        // is_airlock (GeoBuild_Driver 0x43a26a-0x43a282: effect+0x9c/0x9d/0x9e -> room+0x41/0x42/0x43).
        RoomEffectsSection? fx = rfl.Sections.Select(s => s.Content).OfType<RoomEffectsSection>().FirstOrDefault();
        if (fx is not null)
        {
            _out.WriteLine($"room effects: {fx.Effects.Count}");
            foreach (RoomEffect e in fx.Effects)
            {
                _out.WriteLine($"  effect uid={e.Header.Uid} type={e.EffectType} pos={e.Header.Position} " +
                    $"cold={e.RoomIsCold} outside={e.RoomIsOutside} airlock={e.RoomIsAirLock}");
            }
        }
        else
        {
            _out.WriteLine("room effects: NONE parsed");
        }

        // Parent(s) of each subroom, from RED's dense subroom-list array.
        var parentsOf = new Dictionary<int, List<int>>();
        foreach (SubroomList sl in red.SubroomLists)
        {
            foreach (int sub in sl.SubroomIndices)
            {
                if (!parentsOf.TryGetValue(sub, out List<int>? l))
                {
                    parentsOf[sub] = l = new List<int>();
                }

                l.Add(sl.RoomIndex);
            }
        }

        int airlock = 0, cold = 0, outside = 0;
        int airlockMain = 0, airlockSub = 0;
        var airlockMains = new HashSet<int>();
        var airlockSubs = new List<int>();
        for (int i = 0; i < red.Rooms.Count; i++)
        {
            Room r = red.Rooms[i];
            if (r.IsAirlock != 0)
            {
                airlock++;
                if (r.IsSubroom != 0)
                {
                    airlockSub++;
                    airlockSubs.Add(i);
                }
                else
                {
                    airlockMain++;
                    airlockMains.Add(i);
                }
            }

            if (r.IsCold != 0)
            {
                cold++;
            }

            if (r.IsOutside != 0)
            {
                outside++;
            }
        }

        _out.WriteLine($"RED {Level}: rooms={red.Rooms.Count} airlock={airlock} (main={airlockMain} sub={airlockSub}) cold={cold} outside={outside}");
        _out.WriteLine($"airlock MAIN rooms: {string.Join(",", airlockMains)}");

        // Hypothesis A: every airlock subroom has an airlock main-room parent.
        int subsWithAirlockParent = 0, subsWithoutAirlockParent = 0;
        foreach (int sub in airlockSubs)
        {
            List<int> ps = parentsOf.GetValueOrDefault(sub, new List<int>());
            bool hasAirlockParent = ps.Any(p => airlockMains.Contains(p));
            if (hasAirlockParent)
            {
                subsWithAirlockParent++;
            }
            else
            {
                subsWithoutAirlockParent++;
                _out.WriteLine($"  airlock sub#{sub} parents=[{string.Join(",", ps)}] NONE airlock" +
                    $" (cold={red.Rooms[sub].IsCold} outside={red.Rooms[sub].IsOutside} life={red.Rooms[sub].Life})");
            }
        }

        _out.WriteLine($"HYP-A inheritance: airlock subs with airlock parent = {subsWithAirlockParent}/{airlockSubs.Count}" +
            $" (without={subsWithoutAirlockParent})");

        // Converse: of the airlock main room's subroom children, how many are airlock?
        foreach (int m in airlockMains)
        {
            var children = red.SubroomLists.FirstOrDefault(sl => sl.RoomIndex == m)?.SubroomIndices ?? new List<int>();
            int childAirlock = children.Count(c => red.Rooms[c].IsAirlock != 0);
            _out.WriteLine($"  airlock MAIN #{m}: {children.Count} subroom children, {childAirlock} of them airlock" +
                $" (cold={red.Rooms[m].IsCold} outside={red.Rooms[m].IsOutside})");
        }

        // Correlate airlock with cold/outside on the same rooms (are they a flag triple?).
        int alCold = 0, alOutside = 0;
        for (int i = 0; i < red.Rooms.Count; i++)
        {
            if (red.Rooms[i].IsAirlock != 0)
            {
                if (red.Rooms[i].IsCold != 0)
                {
                    alCold++;
                }

                if (red.Rooms[i].IsOutside != 0)
                {
                    alOutside++;
                }
            }
        }

        _out.WriteLine($"of {airlock} airlock rooms: also cold={alCold}, also outside={alOutside}");

        // Spatial analysis: bounding box of all 17 airlock rooms + is each airlock room inside the airlock
        // MAIN room's AABB? Tests the "authored airlock volume" hypothesis.
        var allAirlock = new List<int>(airlockSubs);
        allAirlock.AddRange(airlockMains);
        Aabb hull = red.Rooms[allAirlock[0]].Aabb;
        foreach (int i in allAirlock)
        {
            Aabb a = red.Rooms[i].Aabb;
            hull = new Aabb(
                new Vec3(MathF.Min(hull.P1.X, a.P1.X), MathF.Min(hull.P1.Y, a.P1.Y), MathF.Min(hull.P1.Z, a.P1.Z)),
                new Vec3(MathF.Max(hull.P2.X, a.P2.X), MathF.Max(hull.P2.Y, a.P2.Y), MathF.Max(hull.P2.Z, a.P2.Z)));
        }

        _out.WriteLine($"airlock cluster hull: {hull.P1} .. {hull.P2}  size=({hull.P2.X - hull.P1.X:F1},{hull.P2.Y - hull.P1.Y:F1},{hull.P2.Z - hull.P1.Z:F1})");
        foreach (int m in airlockMains)
        {
            Aabb ma = red.Rooms[m].Aabb;
            _out.WriteLine($"airlock MAIN #{m} AABB: {ma.P1} .. {ma.P2}");
            int inside = 0;
            foreach (int sub in airlockSubs)
            {
                if (CenterInside(red.Rooms[sub].Aabb, ma))
                {
                    inside++;
                }
            }

            _out.WriteLine($"  airlock subs whose CENTER is inside MAIN #{m} AABB: {inside}/{airlockSubs.Count}");
        }

        // Which main room's AABB contains each airlock cluster room (regardless of PVS parent)?
        var mainRooms = new List<int>();
        for (int i = 0; i < red.Rooms.Count; i++)
        {
            if (red.Rooms[i].IsSubroom == 0)
            {
                mainRooms.Add(i);
            }
        }

        // For the airlock main room, is it distinguished by being small (a chamber)?
        foreach (int m in airlockMains)
        {
            Aabb ma = red.Rooms[m].Aabb;
            float vol = (ma.P2.X - ma.P1.X) * (ma.P2.Y - ma.P1.Y) * (ma.P2.Z - ma.P1.Z);
            _out.WriteLine($"airlock MAIN #{m} volume={vol:F1} life={red.Rooms[m].Life} skyroom={red.Rooms[m].IsSkyroom}");
        }
    }

    private static bool CenterInside(Aabb inner, Aabb outer)
    {
        var c = new Vec3((inner.P1.X + inner.P2.X) * 0.5f, (inner.P1.Y + inner.P2.Y) * 0.5f, (inner.P1.Z + inner.P2.Z) * 0.5f);
        return c.X >= outer.P1.X && c.X <= outer.P2.X &&
               c.Y >= outer.P1.Y && c.Y <= outer.P2.Y &&
               c.Z >= outer.P1.Z && c.Z <= outer.P2.Z;
    }
}
