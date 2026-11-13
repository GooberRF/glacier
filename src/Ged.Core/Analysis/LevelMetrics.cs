using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Ged.Core.Editing;
using Ged.Core.Editing.Organic;
using Ged.Core.Editor;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;
using Ged.Core.Model;
using Ged.Core.Rooms;
using Ged.Core.Tables;

namespace Ged.Core.Analysis;

/// <summary>
/// Level fingerprinting (CORPUS-NORMS-SPEC §3): pure, no I/O. Metrics come from the compiled geometry stored in
/// the file — RED's own output for stock-built levels — never from a rebuild; a level with brushes but no
/// compiled geometry is fingerprinted object-only. Lightmap sections are never decoded. Each metric group is
/// computed in isolation: a failure in one group nulls that group (status <c>ok_partial</c>) instead of losing the
/// level. Cancellation (the per-level timeout) is cooperative and propagates as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public static class LevelMetrics
{
    /// <summary>The smallest byte count that can hold an RFL header (magic through the level-name length).</summary>
    public const int MinHeaderBytes = 30;

    /// <summary>RFL versions the fingerprint accepts: RF1 180–200 and Alpine 300–306 (others: header-only identity).</summary>
    public static bool IsSupportedVersion(int version) => (version >= 180 && version <= 200) || (version >= 300 && version <= 306);

    /// <summary>Lower-case hex SHA-256.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>True when <paramref name="bytes"/> starts with the RFL magic and can hold a header.</summary>
    public static bool HasRflMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= MinHeaderBytes && BitConverter.ToUInt32(bytes[..4]) == RflHeader.Magic;

    /// <summary>The header version (bytes 4–7), or null when the bytes are not an RFL.</summary>
    public static int? HeaderVersion(ReadOnlySpan<byte> bytes) => HasRflMagic(bytes) ? BitConverter.ToInt32(bytes.Slice(4, 4)) : null;

    /// <summary>
    /// Fingerprints raw level bytes: unsupported versions get a header-only identity (status
    /// <c>unsupported_version</c>); a file that is not an RFL or does not parse throws
    /// <see cref="RflFormatException"/> (or the parser's exception) for the caller to record as <c>parse_error</c>.
    /// </summary>
    public static LevelFingerprint Analyze(byte[] bytes, MetricOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new MetricOptions();
        if (!HasRflMagic(bytes))
        {
            throw new RflFormatException("Not an RFL file (bad magic or shorter than a header).");
        }

        string sha = Sha256Hex(bytes);
        int version = BitConverter.ToInt32(bytes, 4);
        if (!IsSupportedVersion(version))
        {
            return HeaderOnly(bytes, sha, options, FingerprintSchema.Status.UnsupportedVersion, $"RFL version {version} is not supported (180-200, 300-306).");
        }

        RflFile rfl = RflFile.Load(bytes);
        return Compute(rfl, options, ct, sha, bytes.LongLength);
    }

    /// <summary>A fingerprint with only the header identity (unsupported versions, or levels that failed to parse).</summary>
    public static LevelFingerprint HeaderOnly(byte[] bytes, string sha256, MetricOptions? options, string status, string message)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new MetricOptions();
        var identity = new JsonObject
        {
            ["reliability"] = "high",
            ["sha256"] = sha256,
            ["size"] = bytes.LongLength,
            ["rflVersion"] = HeaderVersion(bytes),
            ["timestamp"] = bytes.Length >= 12 && HasRflMagic(bytes) ? BitConverter.ToUInt32(bytes, 8) : null,
            ["fileName"] = options.FileName,
            ["origin"] = options.Origin,
        };
        try
        {
            if (HasRflMagic(bytes))
            {
                var r = new RfReader(bytes) { Position = 28 };
                identity["headerName"] = LevelIdentity.FromWindows1252(r.ReadVString());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A header too damaged to name: identity stays without it.
        }

        return new LevelFingerprint(new JsonObject
        {
            ["schema"] = FingerprintSchema.Schema,
            ["extractorVersion"] = FingerprintSchema.ExtractorVersion,
            ["status"] = status,
            ["message"] = message,
            ["identity"] = identity,
            ["skipped"] = new JsonObject(),
        });
    }

    /// <summary>
    /// True when the level carries Glacier provenance tags from an automated run (any <c>ai.*</c> key in a
    /// <c>0x6ED00002</c> metadata chunk) — such levels are excluded from the corpus norms. Parses only that chunk.
    /// </summary>
    public static bool HasAiTags(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        RflContext ctx = rfl.Context;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId != (uint)SectionType.GedObjectMetadata)
            {
                continue;
            }

            IRflSectionContent? content = s.Content;
            if (content is null)
            {
                try
                {
                    RflSectionRegistry.TryParse(s, ctx, out content);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    continue; // a damaged metadata chunk carries no readable tags
                }
            }

            if (content is not GedObjectMetadataSection meta)
            {
                continue;
            }

            foreach (GedObjectMetadataRecord rec in meta.Entries)
            {
                foreach (GedObjectMetadataBlock b in rec.Blocks)
                {
                    if (b.MetadataType == (uint)GedMetadataType.Tags && ObjectTags.TryDecode(b.Payload, out Dictionary<string, string> tags)
                        && tags.Keys.Any(k => k.StartsWith("ai.", StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Computes the fingerprint of a loaded level. The level is read, never modified: sections are parsed through a
    /// private view that leaves the lightmaps out. <paramref name="sha256"/> / <paramref name="size"/> describe the
    /// source bytes when known (computed from a serialization otherwise).
    /// </summary>
    public static LevelFingerprint Compute(RflFile rfl, MetricOptions? options, CancellationToken ct, string? sha256 = null, long? size = null)
    {
        ArgumentNullException.ThrowIfNull(rfl);
        options ??= new MetricOptions();
        ct.ThrowIfCancellationRequested();
        if (sha256 is null || size is null)
        {
            byte[] raw = rfl.Save(updateTimestamp: false);
            sha256 ??= Sha256Hex(raw);
            size ??= raw.LongLength;
        }

        // A private view without the lightmap sections (never decoded; they can be very large).
        var view = new RflFile { Header = rfl.Header, TrailingBytes = rfl.TrailingBytes };
        int lightmapPages = 0;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Lightmaps)
            {
                lightmapPages += s.RawBytes.Length >= 4 ? Math.Max(0, BitConverter.ToInt32(s.RawBytes, 0)) : 0;
                continue;
            }

            view.Sections.Add(s);
        }

        var doc = new EditorDocument(view, path: null);
        var ctx = new Context(view, doc, options, ct, lightmapPages);
        return ctx.Build(sha256, size.Value);
    }

    /// <summary>Everything one computation needs, plus the group builders.</summary>
    private sealed class Context
    {
        private readonly RflFile _rfl;
        private readonly EditorDocument _doc;
        private readonly MetricOptions _o;
        private readonly CancellationToken _ct;
        private readonly int _lightmapPages;
        private readonly JsonObject _skipped = new();
        private readonly Geometry? _geo;
        private readonly LevelInfoSection? _info;
        private readonly LevelPropertiesSection? _props;
        private readonly List<Brush> _brushes;
        private readonly List<Light> _lights;
        private readonly List<MpRespawnPoint> _spawns;
        private readonly List<LevelObject> _items;
        private SpaceSampler? _sampler;
        private double? _floorArea;
        private bool _partial;

        public Context(RflFile rfl, EditorDocument doc, MetricOptions o, CancellationToken ct, int lightmapPages)
        {
            _rfl = rfl;
            _doc = doc;
            _o = o;
            _ct = ct;
            _lightmapPages = lightmapPages;
            _geo = rfl.Sections.Select(s => s.Content).OfType<GeometrySection>().FirstOrDefault()?.Geometry;
            _info = rfl.Sections.Select(s => s.Content).OfType<LevelInfoSection>().FirstOrDefault();
            _props = rfl.Sections.Select(s => s.Content).OfType<LevelPropertiesSection>().FirstOrDefault();
            _brushes = rfl.Sections.Select(s => s.Content).OfType<BrushesSection>().SelectMany(b => b.Brushes).ToList();
            _lights = rfl.Sections.Select(s => s.Content).OfType<LightsSection>().Where(l => l.Type == SectionType.Lights).SelectMany(l => l.Lights).ToList();
            _spawns = rfl.Sections.Select(s => s.Content).OfType<MpRespawnPointsSection>().SelectMany(s => s.Points).ToList();
            _items = doc.Objects.Where(o => o.Kind == LevelObjectKind.Item).ToList();
        }

        private bool HasGeometry => _geo is { Faces.Count: > 0 };

        public LevelFingerprint Build(string sha, long size)
        {
            var root = new JsonObject
            {
                ["schema"] = FingerprintSchema.Schema,
                ["extractorVersion"] = FingerprintSchema.ExtractorVersion,
                ["status"] = FingerprintSchema.Status.Ok,
            };

            root["identity"] = Identity(sha, size);
            Group(root, "counts", Counts);
            PrepareSampler();
            Group(root, "space", Space);
            Group(root, "vertical", Vertical);
            Group(root, "corridor", Corridor);
            Group(root, "opening", Openings);
            Group(root, "portal", Portals);
            Group(root, "stair", Stairs);
            Group(root, "room", Rooms);
            Group(root, "topology", Topology);
            Group(root, "textures", Textures);
            Group(root, "texel", Texel);
            Group(root, "detail", Detail);
            Group(root, "brush", BrushGroup);
            Group(root, "lights", Lights);
            Group(root, "spawns", Spawns);
            Group(root, "items", Items);
            Group(root, "ctf", Ctf);
            Group(root, "events", Events);
            LevelClass((JsonObject)root["identity"]!);
            root["skipped"] = _skipped;
            root["status"] = _partial ? FingerprintSchema.Status.OkPartial : FingerprintSchema.Status.Ok;
            return new LevelFingerprint(root);
        }

        /// <summary>Runs one group builder in isolation: null + reason on a skip or a failure.</summary>
        private void Group(JsonObject root, string name, Func<(JsonObject? Value, string? Reason)> build)
        {
            _ct.ThrowIfCancellationRequested();
            (JsonObject? value, string? reason) result;
            try
            {
                result = build();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = (null, $"error: {ex.GetType().Name}: {ex.Message}");
            }

            root[name] = result.value;
            if (result.value is null)
            {
                string reason = result.reason ?? "unavailable";
                _skipped[name] = reason;
                if (!FingerprintSchema.NotApplicableReasons.Contains(reason))
                {
                    _partial = true;
                }
            }
        }

        private void PrepareSampler()
        {
            if (!HasGeometry)
            {
                return;
            }

            try
            {
                _sampler = SpaceSampler.Build(_geo!, _ct, _o.SamplerProbe);
                _sampler.Measure(_o.LatticePitch, _o.MaxFloorSamples);
                double g2 = _sampler.Pitch * _sampler.Pitch;
                _floorArea = _sampler.Samples.Count(s => s.Valid) * g2;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _sampler = null;
                _skipped["sampler"] = $"error: {ex.GetType().Name}: {ex.Message}";
                _partial = true;
            }
        }

        private (JsonObject?, string?) NoGeometry() => (null, HasGeometry ? "error: sampler unavailable" : "no_geometry");

        // ---- identity / counts -------------------------------------------------------------------

        private JsonObject Identity(string sha, long size)
        {
            bool multiplayer = _info?.MultiplayerLevel != 0 && _info is not null;
            bool flags = _items.Any(i => LevelIdentity.IsCtfFlagClass(i.ClassName));
            bool teamSpawns = _spawns.Any(s => s.RedTeam != 0 && s.BlueTeam == 0) && _spawns.Any(s => s.BlueTeam != 0 && s.RedTeam == 0);
            GametypeVerdict gt = LevelIdentity.ClassifyGametype(multiplayer, _o.FileName ?? _rfl.Header.LevelName, flags, teamSpawns);
            string rawAuthor = LevelIdentity.FromWindows1252(_info?.Author ?? string.Empty);
            return new JsonObject
            {
                ["reliability"] = "high",
                ["sha256"] = sha,
                ["size"] = size,
                ["rflVersion"] = _rfl.Header.Version,
                ["timestamp"] = _rfl.Header.Timestamp,
                ["name"] = LevelIdentity.FromWindows1252(_info?.LevelName ?? string.Empty),
                ["headerName"] = LevelIdentity.FromWindows1252(_rfl.Header.LevelName ?? string.Empty),
                ["fileName"] = _o.FileName,
                ["author"] = new JsonObject
                {
                    ["raw"] = rawAuthor,
                    ["canonical"] = LevelIdentity.CanonicalAuthor(_info?.Author, _o.AuthorAliases),
                },
                ["multiplayer"] = multiplayer,
                ["gametype"] = new JsonObject
                {
                    ["value"] = gt.Value,
                    ["source"] = gt.Source,
                    ["conflict"] = gt.Conflict,
                },
                ["teamSpawns"] = teamSpawns,
                ["origin"] = _o.Origin,
                ["editor"] = EditorOf(_rfl),
                ["alpine"] = _rfl.Context.IsAlpine,
                ["playerClass"] = _spawns.Count > 0 ? Spatial.DesignGrader.PlayerClass(null, _spawns.Count) : null,
            };
        }

        /// <summary>
        /// <c>identity.class</c> (E12): payload when the gametype or a Payload_Handler says so; otherwise, with
        /// <see cref="MetricOptions.LevelClass"/>, the design grader's own class detector on the compiled geometry
        /// (open-air share of the floor and the share at fall-off edges); null when not measured.
        /// </summary>
        private void LevelClass(JsonObject identity)
        {
            string gametype = identity["gametype"]?["value"]?.GetValue<string>() ?? string.Empty;
            bool payload = gametype == "pay" || _doc.Objects.Any(o => o.Kind == LevelObjectKind.Event && o.ClassName == "Payload_Handler");
            if (payload)
            {
                identity["class"] = new JsonObject { ["value"] = "payload", ["source"] = "gametype" };
                return;
            }

            if (!_o.LevelClass)
            {
                identity["class"] = null;
                return;
            }

            if (!HasGeometry)
            {
                identity["class"] = null;
                _skipped["class"] = "no_geometry";
                return;
            }

            try
            {
                Spatial.SpatialLevel level = Spatial.SpatialLevel.FromDocument(_doc, _geo, Spatial.GeometryFreshness.StoredFile, _o.FileName);
                Spatial.SpatialAnalysis a = Spatial.SpatialAnalysis.Run(level, new Spatial.SpatialOptions
                {
                    MovementLinks = false, MaxSamples = _o.LevelClassMaxSamples, Parallelism = 1,
                }, _ct);
                (double open, double falloff) = a.ClassFeatureValues();
                var metrics = new Dictionary<string, double?>(StringComparer.Ordinal)
                {
                    ["open_air_fraction"] = open,
                    ["falloff_edge_fraction"] = falloff,
                };
                identity["class"] = new JsonObject
                {
                    ["value"] = Spatial.DesignGrader.LevelClass(a, metrics, gametype),
                    ["source"] = "detector",
                    ["open_air_fraction"] = Math.Round(open, 3),
                    ["falloff_edge_fraction"] = Math.Round(falloff, 3),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                identity["class"] = null;
                _skipped["class"] = ex is Spatial.SpatialException se ? se.Code : $"error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string EditorOf(RflFile rfl)
        {
            bool glacier = rfl.Sections.Any(s => (s.TypeId & 0xFFF00000u) == 0x6ED00000u);
            bool redplus = rfl.Sections.Any(s => (s.TypeId & 0xFFFF0000u) == 0x5ED00000u);
            return glacier ? "glacier" : redplus ? "redplus" : "red";
        }

        private (JsonObject?, string?) Counts()
        {
            var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (LevelObject o in _doc.Objects)
            {
                string k = Camel(o.Kind.ToString());
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
            }

            JsonObject Histogram(LevelObjectKind kind)
            {
                var h = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (LevelObject o in _doc.Objects.Where(o => o.Kind == kind))
                {
                    string c = o.ClassName ?? string.Empty;
                    h[c] = h.GetValueOrDefault(c) + 1;
                }

                return ToJson(h);
            }

            Geometry? g = _geo;
            var counts = new JsonObject
            {
                ["reliability"] = "high",
                ["objects"] = ToJson(kinds),
                ["classes"] = new JsonObject
                {
                    ["entity"] = Histogram(LevelObjectKind.Entity),
                    ["item"] = Histogram(LevelObjectKind.Item),
                    ["clutter"] = Histogram(LevelObjectKind.Clutter),
                    ["event"] = Histogram(LevelObjectKind.Event),
                },
                ["geometry"] = new JsonObject
                {
                    ["faces"] = g?.Faces.Count ?? 0,
                    ["vertices"] = g?.Vertices.Count ?? 0,
                    ["rooms"] = g?.Rooms.Count ?? 0,
                    ["mainRooms"] = g?.Rooms.Count(r => r.IsSubroom == 0) ?? 0,
                    ["subrooms"] = g?.Rooms.Count(r => r.IsSubroom != 0) ?? 0,
                    ["portals"] = g?.Portals.Count ?? 0,
                    ["surfaces"] = g?.Surfaces.Count ?? 0,
                    ["lightmapPages"] = _lightmapPages,
                },
                ["brushes"] = _brushes.Count,
                ["levelProperties"] = _props is null ? null : new JsonObject
                {
                    ["geomodTexture"] = _props.GeomodTexture,
                    ["hardness"] = _props.Hardness,
                },
                ["alpineProperties"] = _rfl.Sections.Any(s => s.TypeId == (uint)SectionType.AlpineLevelProperties),
                ["dashProperties"] = _rfl.Sections.Any(s => s.TypeId == (uint)SectionType.DashLevelProperties),
                ["glacierMetadata"] = _rfl.Sections.Any(s => s.TypeId == (uint)SectionType.GedObjectMetadata),
            };
            return (counts, null);
        }

        // ---- scale -------------------------------------------------------------------------------

        private (JsonObject?, string?) Space()
        {
            if (_sampler is null)
            {
                return NoGeometry();
            }

            List<SpaceSampler.Sample> valid = _sampler.Samples.Where(s => s.Valid).ToList();
            int sky = valid.Count(s => s.OpenSky);
            return (new JsonObject
            {
                ["reliability"] = "high",
                ["pitch"] = R(_sampler.Pitch),
                ["samples"] = _sampler.Samples.Count,
                ["valid_samples"] = valid.Count,
                ["floor_area"] = R(_floorArea ?? 0),
                ["headroom"] = Percentiles.Distribution(valid.Where(s => !s.OpenSky).Select(s => s.Headroom)),
                ["open_sky_fraction"] = valid.Count > 0 ? R((double)sky / valid.Count) : null,
                ["clear_width"] = Percentiles.Distribution(valid.Select(s => s.Width)),
            }, null);
        }

        private (JsonObject?, string?) Vertical()
        {
            if (_sampler is null)
            {
                return NoGeometry();
            }

            double[] ys = _sampler.Samples.Where(s => s.Valid).Select(s => s.P.Y).OrderBy(y => y).ToArray();
            if (ys.Length == 0)
            {
                return (new JsonObject { ["reliability"] = "high", ["span"] = null, ["layers"] = 0 }, null);
            }

            double span = Percentiles.Type7(ys, 0.99) - Percentiles.Type7(ys, 0.01);

            // Layers: 1 m bins holding at least 5% of the floor; adjacent qualifying bins merge.
            var bins = ys.GroupBy(y => (long)Math.Floor(y)).ToDictionary(g => g.Key, g => g.Count());
            var qualifying = bins.Where(kv => kv.Value >= 0.05 * ys.Length).Select(kv => kv.Key).OrderBy(k => k).ToList();
            int layers = 0;
            for (int i = 0; i < qualifying.Count; i++)
            {
                if (i == 0 || qualifying[i] != qualifying[i - 1] + 1)
                {
                    layers++;
                }
            }

            return (new JsonObject { ["reliability"] = "high", ["span"] = R(span), ["layers"] = layers, ["layers_reliability"] = "medium" }, null);
        }

        private (JsonObject?, string?) Corridor()
        {
            if (_sampler is null)
            {
                return NoGeometry();
            }

            List<SpaceSampler.Sample> valid = _sampler.Samples.Where(s => s.Valid).ToList();
            List<SpaceSampler.Sample> corr = valid.Where(s => s.Corridor).ToList();
            return (new JsonObject
            {
                ["reliability"] = "medium",
                ["width"] = Percentiles.Distribution(corr.Select(s => s.Width)),
                ["height"] = Percentiles.Distribution(corr.Where(s => !s.OpenSky).Select(s => s.Headroom)),
                ["fraction"] = valid.Count > 0 ? R((double)corr.Count / valid.Count) : null,
            }, null);
        }

        private (JsonObject?, string?) Openings()
        {
            if (_sampler is null)
            {
                return NoGeometry();
            }

            List<SpaceSampler.Opening> found = _sampler.FindOpenings();

            // Cross-method agreement (CORPUS-NORMS-SPEC §4-2): openings whose centre lies within 1 m (horizontally)
            // of a vertical portal's centre, with the opening's floor inside the portal's height range.
            var diffs = new List<double>();
            List<PortalShape> portals = PortalShapes().Where(p => p.Vertical).ToList();
            foreach (SpaceSampler.Opening op in found)
            {
                PortalShape? best = null;
                double bestD = double.MaxValue;
                foreach (PortalShape p in portals)
                {
                    double dx = p.Centre.X - op.Center.X, dz = p.Centre.Z - op.Center.Z;
                    double d = Math.Sqrt((dx * dx) + (dz * dz));
                    if (d <= 1.0 && op.Center.Y >= p.MinY - 0.6 && op.Center.Y <= p.MaxY && d < bestD)
                    {
                        best = p;
                        bestD = d;
                    }
                }

                if (best is not null)
                {
                    diffs.Add(Math.Abs(op.Width - best.Width));
                }
            }

            return (new JsonObject
            {
                ["reliability"] = "medium",
                ["count"] = found.Count,
                ["width"] = Percentiles.Distribution(found.Select(o => o.Width)),
                ["height"] = Percentiles.Distribution(found.Select(o => o.Height)),
                ["depth"] = Percentiles.Distribution(found.Select(o => o.Depth)),
                ["portal_agreement"] = new JsonObject
                {
                    ["pairs"] = diffs.Count,
                    ["abs_width_diff"] = Percentiles.Distribution(diffs),
                },
                ["notes"] = new JsonArray(
                    "Blind spots: openings wider than 6 m; doorways whose mover door is excluded from the static geometry read as open; openings at a level change (stair doorways).",
                    "Single-sample openings are dropped at the base pitch; walls thinner than one lattice step may hide a doorway."),
            }, null);
        }

        /// <summary>One portal: vertical (with width/height/centre/vertical extent) or horizontal.</summary>
        private sealed record PortalShape(bool Vertical, double Width, double Height, Vec3d Centre, double MinY, double MaxY);

        private List<PortalShape>? _portalShapes;
        private int _portalGroups;

        /// <summary>
        /// Portal shapes: faces grouped by portal index (<c>portal_index_plus_2</c> − 2), texture-sentinel portal faces
        /// without an index grouped by plane — both markers count (CLAUDE.md). Vertical when the mean normal (faces
        /// aligned to the first) is within 20 degrees of horizontal: width along normal × Y, height along Y.
        /// </summary>
        private List<PortalShape> PortalShapes()
        {
            if (_portalShapes is not null)
            {
                return _portalShapes;
            }

            Geometry g = _geo!;
            var groups = new SortedDictionary<string, List<Face>>(StringComparer.Ordinal);
            foreach (Face f in g.Faces)
            {
                if (!f.IsPortalFace)
                {
                    continue;
                }

                string key;
                if (f.PortalIndexPlus2 >= 2)
                {
                    key = "p" + (f.PortalIndexPlus2 - 2).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    Vec3 n = f.Plane.Normal;
                    float sgn = (n.X != 0 ? n.X : n.Y != 0 ? n.Y : n.Z) < 0 ? -1 : 1;
                    key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"f{Math.Round(n.X * sgn, 2):0.00},{Math.Round(n.Y * sgn, 2):0.00},{Math.Round(n.Z * sgn, 2):0.00},{Math.Round(f.Plane.Offset * sgn * 4) / 4:0.00}");
                }

                if (!groups.TryGetValue(key, out List<Face>? list))
                {
                    list = new List<Face>();
                    groups[key] = list;
                }

                list.Add(f);
            }

            _portalGroups = groups.Count;
            _portalShapes = new List<PortalShape>();
            double sin20 = Math.Sin(20 * Math.PI / 180);
            foreach (List<Face> faces in groups.Values)
            {
                Vec3d first = default;
                Vec3d sum = new(0, 0, 0);
                var pts = new List<Vec3d>();
                bool any = false;
                foreach (Face f in faces)
                {
                    if (SpaceSampler.Polygon(g, f) is not { } pg)
                    {
                        continue;
                    }

                    if (!any)
                    {
                        first = pg.Normal;
                        any = true;
                    }

                    sum = sum.Add(pg.Normal.Dot(first) < 0 ? pg.Normal.Scale(-1) : pg.Normal);
                    pts.AddRange(pg.Poly);
                }

                if (!any || sum.Length < 1e-9)
                {
                    continue;
                }

                Vec3d n = sum.Scale(1 / sum.Length);
                Vec3d centre = new(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
                double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                if (Math.Abs(n.Y) > sin20)
                {
                    _portalShapes.Add(new PortalShape(false, 0, 0, centre, minY, maxY));
                    continue;
                }

                Vec3d across = n.Cross(new Vec3d(0, 1, 0));
                across = across.Length > 1e-9 ? across.Scale(1 / across.Length) : new Vec3d(1, 0, 0);
                double width = pts.Max(p => p.Dot(across)) - pts.Min(p => p.Dot(across));
                _portalShapes.Add(new PortalShape(true, width, maxY - minY, centre, minY, maxY));
            }

            return _portalShapes;
        }

        private (JsonObject?, string?) Portals()
        {
            if (!HasGeometry)
            {
                return (null, "no_geometry");
            }

            List<PortalShape> shapes = PortalShapes();
            List<PortalShape> vertical = shapes.Where(s => s.Vertical).ToList();
            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = _portalGroups,
                ["vertical_count"] = vertical.Count,
                ["horizontal_count"] = shapes.Count(s => !s.Vertical),
                ["width"] = Percentiles.Distribution(vertical.Select(s => s.Width)),
                ["height"] = Percentiles.Distribution(vertical.Select(s => s.Height)),
            }, null);
        }

        private (JsonObject?, string?) Stairs()
        {
            if (_sampler is null)
            {
                return NoGeometry();
            }

            List<StairDetector.Stair> stairs = StairDetector.Detect(_sampler.Faces, _ct);
            return (new JsonObject
            {
                ["reliability"] = "medium",
                ["count"] = stairs.Count,
                ["riser"] = Percentiles.Distribution(stairs.Select(s => s.Riser)),
                ["tread"] = Percentiles.Distribution(stairs.Where(s => s.Tread.HasValue).Select(s => s.Tread!.Value)),
                ["steps"] = Percentiles.Distribution(stairs.Select(s => (double)s.Steps)),
                ["notes"] = new JsonArray(
                    "Treads under an invisible collision ramp still count; spiral stairs (direction test) and terraces with treads deeper than 2 m are not counted."),
            }, null);
        }

        private (JsonObject?, string?) Rooms()
        {
            if (!HasGeometry)
            {
                return (null, "no_geometry");
            }

            List<Room> rooms = _geo!.Rooms.Where(r => r.IsSubroom == 0 && r.IsSkyroom == 0).ToList();
            if (rooms.Count < 4)
            {
                return (null, "fewer_than_4_rooms");
            }

            var w = new List<double>();
            var d = new List<double>();
            var h = new List<double>();
            var v = new List<double>();
            foreach (Room r in rooms)
            {
                double sx = Math.Abs(r.Aabb.P2.X - r.Aabb.P1.X), sy = Math.Abs(r.Aabb.P2.Y - r.Aabb.P1.Y), sz = Math.Abs(r.Aabb.P2.Z - r.Aabb.P1.Z);
                w.Add(Math.Max(sx, sz));
                d.Add(Math.Min(sx, sz));
                h.Add(sy);
                v.Add(sx * sy * sz);
            }

            return (new JsonObject
            {
                ["reliability"] = "low",
                ["count"] = rooms.Count,
                ["width"] = Percentiles.Distribution(w),
                ["depth"] = Percentiles.Distribution(d),
                ["height"] = Percentiles.Distribution(h),
                ["volume"] = Percentiles.Distribution(v),
                ["notes"] = new JsonArray("Room bounding boxes overstate non-box rooms, and an unportalled level is one room: for retrieval, not for defaults."),
            }, null);
        }

        private (JsonObject?, string?) Topology()
        {
            if (!HasGeometry)
            {
                return (null, "no_geometry");
            }

            Geometry g = _geo!;
            RoomGraph graph = RoomGraph.Build(g);
            var nodes = Enumerable.Range(0, g.Rooms.Count).Where(i => g.Rooms[i].IsSubroom == 0 && g.Rooms[i].IsSkyroom == 0).ToList();
            var isNode = new HashSet<int>(nodes);
            var adj = nodes.ToDictionary(i => i, _ => new List<int>());
            int edges = 0;
            foreach ((int a, int b, int _) in graph.Edges)
            {
                if (isNode.Contains(a) && isNode.Contains(b))
                {
                    adj[a].Add(b);
                    adj[b].Add(a);
                    edges++;
                }
            }

            // Components.
            var comp = new Dictionary<int, int>();
            var sizes = new List<int>();
            foreach (int start in nodes)
            {
                if (comp.ContainsKey(start))
                {
                    continue;
                }

                int id = sizes.Count;
                int size = 0;
                var q = new Queue<int>();
                q.Enqueue(start);
                comp[start] = id;
                while (q.Count > 0)
                {
                    int x = q.Dequeue();
                    size++;
                    foreach (int y in adj[x])
                    {
                        if (comp.TryAdd(y, id))
                        {
                            q.Enqueue(y);
                        }
                    }
                }

                sizes.Add(size);
            }

            // Diameter of the largest component (BFS from every node; capped for very large graphs).
            int? diameter = null;
            if (sizes.Count > 0)
            {
                int largest = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]).ThenBy(i => i).First();
                List<int> members = nodes.Where(n => comp[n] == largest).ToList();
                if (members.Count <= 3000)
                {
                    int best = 0;
                    foreach (int s in members)
                    {
                        _ct.ThrowIfCancellationRequested();
                        var dist = new Dictionary<int, int> { [s] = 0 };
                        var q = new Queue<int>();
                        q.Enqueue(s);
                        while (q.Count > 0)
                        {
                            int x = q.Dequeue();
                            foreach (int y in adj[x])
                            {
                                if (dist.TryAdd(y, dist[x] + 1))
                                {
                                    best = Math.Max(best, dist[y]);
                                    q.Enqueue(y);
                                }
                            }
                        }
                    }

                    diameter = best;
                }
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["rooms"] = nodes.Count,
                ["portals"] = edges,
                ["components"] = sizes.Count,
                ["degree"] = Percentiles.Distribution(nodes.Select(n => (double)adj[n].Count)),
                ["dead_ends"] = nodes.Count(n => adj[n].Count == 1),
                ["loops"] = edges - nodes.Count + sizes.Count,
                ["diameter"] = diameter,
                ["portalised"] = nodes.Count >= 4,
                ["notes"] = new JsonArray("Describes the portal graph, not the playable layout."),
            }, null);
        }

        // ---- materials -----------------------------------------------------------------------------

        private List<(Face Face, Vec3d[] Poly, Vec3d N, double Area, string Texture)>? _material;

        /// <summary>World faces for the material metrics: textured, not portal, not sky-room, not invisible.</summary>
        private List<(Face Face, Vec3d[] Poly, Vec3d N, double Area, string Texture)> MaterialFaces()
        {
            if (_material is not null)
            {
                return _material;
            }

            _material = new();
            Geometry g = _geo!;
            foreach (Face f in g.Faces)
            {
                if (f.IsPortalFace || f.Texture < 0 || f.Texture >= g.Textures.Count || SpaceSampler.IsInvisibleTexture(g, f)
                    || (f.RoomIndex >= 0 && f.RoomIndex < g.Rooms.Count && g.Rooms[f.RoomIndex].IsSkyroom != 0))
                {
                    continue;
                }

                if (SpaceSampler.Polygon(g, f) is { } pg)
                {
                    _material.Add((f, pg.Poly, pg.Normal, pg.Area, g.Textures[f.Texture]));
                }
            }

            return _material;
        }

        private (JsonObject?, string?) Textures()
        {
            if (!HasGeometry)
            {
                return (null, "no_geometry");
            }

            var faces = MaterialFaces();
            double total = faces.Sum(f => f.Area);
            if (total <= 0)
            {
                return (null, "no_geometry");
            }

            static string Key(string t) => t.ToLowerInvariant();
            JsonArray Top(IEnumerable<(string Tex, double Area)> items, double denom, int n) => new(
                items.GroupBy(i => Key(i.Tex))
                    .Select(g => (Name: g.Key, Area: g.Sum(x => x.Area)))
                    .OrderByDescending(x => x.Area).ThenBy(x => x.Name, StringComparer.Ordinal)
                    .Take(n)
                    .Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["area_fraction"] = R(x.Area / denom) })
                    .ToArray());

            var roles = new JsonObject();
            foreach ((string role, Func<Vec3d, bool> test) in new (string, Func<Vec3d, bool>)[]
            {
                ("floor", n => n.Y >= 0.7), ("wall", n => n.Y > -0.7 && n.Y < 0.7), ("ceiling", n => n.Y <= -0.7),
            })
            {
                var sel = faces.Where(f => test(f.N)).ToList();
                double area = sel.Sum(f => f.Area);
                roles[role] = new JsonObject
                {
                    ["area_fraction"] = R(area / total),
                    ["top"] = area > 0 ? Top(sel.Select(f => (f.Texture, f.Area)), area, 8) : new JsonArray(),
                };
            }

            var family = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var f in faces)
            {
                string fam = LevelIdentity.TextureFamily(f.Texture);
                family[fam] = family.GetValueOrDefault(fam) + f.Area;
            }

            var familyJson = new JsonObject();
            foreach ((string k, double a) in family)
            {
                familyJson[k] = R(a / total);
            }

            double? stock = null;
            if (_o.IsStockTexture is { } isStock)
            {
                var memo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                double sa = 0;
                foreach (var f in faces)
                {
                    if (!memo.TryGetValue(f.Texture, out bool s))
                    {
                        s = isStock(f.Texture);
                        memo[f.Texture] = s;
                    }

                    if (s)
                    {
                        sa += f.Area;
                    }
                }

                stock = sa / total;
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = faces.Select(f => Key(f.Texture)).Distinct(StringComparer.Ordinal).Count(),
                ["palette"] = Top(faces.Select(f => (f.Texture, f.Area)), total, 24),
                ["roles"] = roles,
                ["family"] = familyJson,
                ["stock_fraction"] = stock is double sv ? R(sv) : null,
            }, null);
        }

        private (JsonObject?, string?) Texel()
        {
            if (!HasGeometry)
            {
                return (null, "no_geometry");
            }

            Geometry g = _geo!;
            var items = new List<(double, double)>();
            foreach (var f in MaterialFaces())
            {
                List<FaceVertex> vs = f.Face.Vertices;
                for (int i = 0; i < vs.Count; i++)
                {
                    FaceVertex a = vs[i], b = vs[(i + 1) % vs.Count];
                    double du = b.TextureCoords.U - a.TextureCoords.U, dv = b.TextureCoords.V - a.TextureCoords.V;
                    double uv = Math.Sqrt((du * du) + (dv * dv));
                    if (uv < 1e-6)
                    {
                        continue;
                    }

                    double world = f.Poly[(i + 1) % vs.Count].Sub(f.Poly[i]).Length;
                    if (world > 1e-6)
                    {
                        items.Add((world / uv, f.Area));
                    }

                    break;
                }
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["tile_metres"] = Percentiles.WeightedDistribution(items),
            }, null);
        }

        private (JsonObject?, string?) Detail()
        {
            if (!HasGeometry)
            {
                return (null, "no_geometry");
            }

            var faces = MaterialFaces();
            double total = faces.Sum(f => f.Area);
            double detail = faces.Where(f => (f.Face.Flags & (ushort)FaceFlags.IsDetail) != 0).Sum(f => f.Area);
            return (new JsonObject
            {
                ["reliability"] = "high",
                ["area_fraction"] = total > 0 ? R(detail / total) : null,
            }, null);
        }

        private (JsonObject?, string?) BrushGroup()
        {
            if (_brushes.Count == 0)
            {
                return (null, "no_brushes");
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = _brushes.Count,
                ["faces"] = Percentiles.Distribution(_brushes.Select(b => (double)b.Geometry.Faces.Count)),
                ["air_fraction"] = R((double)_brushes.Count(b => (b.Flags & (uint)BrushFlags.Air) != 0) / _brushes.Count),
                ["detail_fraction"] = R((double)_brushes.Count(b => (b.Flags & (uint)BrushFlags.Detail) != 0) / _brushes.Count),
                ["portal_count"] = _brushes.Count(b => (b.Flags & (uint)BrushFlags.Portal) != 0),
            }, null);
        }

        // ---- lighting -----------------------------------------------------------------------------

        private (JsonObject?, string?) Lights()
        {
            int n = _lights.Count;
            var hue = new List<double>();
            var sat = new List<double>();
            var val = new List<double>();
            int warm = 0, cool = 0, neutral = 0, omni = 0, spot = 0, tube = 0;
            var heights = new List<double>();
            foreach (Light l in _lights)
            {
                (double h, double s, double v) = Hsv(l.Color);
                hue.Add(h);
                sat.Add(s);
                val.Add(v);
                if (s < 0.12)
                {
                    neutral++;
                }
                else if (h >= 0 && h <= 70)
                {
                    warm++;
                }
                else if (h >= 170 && h <= 260)
                {
                    cool++;
                }

                switch ((l.Flags & 0x30u) >> 4)
                {
                    case 2: spot++; break;
                    case 3: tube++; break;
                    default: omni++; break;
                }

                if (_sampler?.HeightAboveFloor(new Vec3d(l.Position.X, l.Position.Y, l.Position.Z)) is double ht)
                {
                    heights.Add(ht);
                }
            }

            double? Frac(int k) => n > 0 ? R((double)k / n) : null;
            RfColor amb = _props?.AmbientColor ?? default;
            bool fog = _props is not null && _props.FogFarPlane > 0 && _props.FogFarPlane > _props.FogNearPlane;
            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = n,
                ["per_100m2"] = _floorArea is double fa && fa > 0 ? R(n * 100.0 / fa) : null,
                ["range"] = Percentiles.Distribution(_lights.Select(l => (double)l.Range)),
                ["intensity"] = Percentiles.Distribution(_lights.Select(l => (double)l.OnIntensity)),
                ["type"] = new JsonObject { ["omni"] = Frac(omni), ["spot"] = Frac(spot), ["tube"] = Frac(tube) },
                ["dynamic_fraction"] = Frac(_lights.Count(l => (l.Flags & 0x1u) != 0)),
                ["hue"] = Percentiles.Distribution(hue),
                ["saturation"] = Percentiles.Distribution(sat),
                ["value"] = Percentiles.Distribution(val),
                ["warm_fraction"] = Frac(warm),
                ["cool_fraction"] = Frac(cool),
                ["neutral_fraction"] = Frac(neutral),
                ["height_above_floor"] = Percentiles.Distribution(heights),
                ["ambient"] = _props is null ? null : new JsonArray(amb.R, amb.G, amb.B),
                ["lightmaps_present"] = _lightmapPages > 0,
                ["fog"] = _props is null ? null : new JsonObject
                {
                    ["present"] = fog,
                    ["near"] = R(_props.FogNearPlane),
                    ["far"] = R(_props.FogFarPlane),
                    ["color"] = new JsonArray(_props.FogColor.R, _props.FogColor.G, _props.FogColor.B),
                },
            }, null);
        }

        private static (double H, double S, double V) Hsv(RfColor c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            double h = 0;
            if (d > 1e-12)
            {
                h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
                if (h < 0)
                {
                    h += 360;
                }
            }

            return (h, max > 0 ? d / max : 0, max);
        }

        // ---- gameplay layout ----------------------------------------------------------------------

        private static double? NearestDistance(Vec3d p, IReadOnlyList<Vec3d> others, bool skipSelf)
        {
            double best = double.MaxValue;
            foreach (Vec3d q in others)
            {
                double d = q.Sub(p).Length;
                if (skipSelf && d < 1e-9)
                {
                    continue;
                }

                best = Math.Min(best, d);
            }

            return best < double.MaxValue ? best : null;
        }

        private static List<double> NearestNeighbour(IReadOnlyList<Vec3d> pts)
        {
            var result = new List<double>();
            for (int i = 0; i < pts.Count; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < pts.Count; j++)
                {
                    if (i != j)
                    {
                        best = Math.Min(best, pts[j].Sub(pts[i]).Length);
                    }
                }

                if (best < double.MaxValue)
                {
                    result.Add(best);
                }
            }

            return result;
        }

        private static Vec3d V(Vec3 v) => new(v.X, v.Y, v.Z);

        private (JsonObject?, string?) Spawns()
        {
            List<Vec3d> pts = _spawns.Select(s => V(s.Position)).ToList();
            var widths = new List<double>();
            if (_sampler is not null)
            {
                foreach (Vec3d p in pts)
                {
                    int si = _sampler.NearestValid(p, 2.0, 1.0, 3.0);
                    if (si >= 0)
                    {
                        widths.Add(_sampler.Samples[si].Width);
                    }
                }
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = _spawns.Count,
                ["red"] = _spawns.Count(s => s.RedTeam != 0),
                ["blue"] = _spawns.Count(s => s.BlueTeam != 0),
                ["bot"] = _spawns.Count(s => s.Bot != 0),
                ["nn_distance"] = Percentiles.Distribution(NearestNeighbour(pts)),
                ["per_100m2"] = _floorArea is double fa && fa > 0 ? R(_spawns.Count * 100.0 / fa) : null,
                ["clear_width"] = Percentiles.Distribution(widths),
                ["clear_width_reliability"] = "medium",
            }, null);
        }

        private (JsonObject?, string?) Items()
        {
            List<Vec3d> pts = _items.Select(i => V(i.Position)).ToList();
            List<Vec3d> spawnPts = _spawns.Select(s => V(s.Position)).ToList();
            var byClass = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (LevelObject i in _items)
            {
                byClass[i.ClassName ?? string.Empty] = byClass.GetValueOrDefault(i.ClassName ?? string.Empty) + 1;
            }

            JsonObject? category = null;
            if (_o.Items is { } catalog && _items.Count > 0)
            {
                var cats = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (LevelObject i in _items)
                {
                    string c = ItemCategory(catalog, i.ClassName);
                    cats[c] = cats.GetValueOrDefault(c) + 1;
                }

                category = new JsonObject();
                foreach ((string k, int v) in cats)
                {
                    category[k] = R((double)v / _items.Count);
                }
            }

            var heights = new List<double>();
            var context = new SortedDictionary<string, int>(StringComparer.Ordinal);
            if (_sampler is not null)
            {
                foreach (Vec3d p in pts)
                {
                    if (_sampler.HeightAboveFloor(p) is double h)
                    {
                        heights.Add(h);
                    }

                    int si = _sampler.NearestValid(p, 2.0, 1.0, 3.0);
                    if (si >= 0)
                    {
                        SpaceSampler.Sample s = _sampler.Samples[si];
                        string c = s.Width >= 8 ? "open" : s.Corridor ? "corridor" : s.Width < 5 && s.Length < 2.5 * s.Width ? "nook" : "other";
                        context[c] = context.GetValueOrDefault(c) + 1;
                    }
                }
            }

            int ctxTotal = context.Values.Sum();
            JsonObject? ctxJson = null;
            if (ctxTotal > 0)
            {
                ctxJson = new JsonObject();
                foreach ((string k, int v) in context)
                {
                    ctxJson[k] = R((double)v / ctxTotal);
                }
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = _items.Count,
                ["by_class"] = ToJson(byClass),
                ["category"] = category,
                ["nn_distance"] = Percentiles.Distribution(NearestNeighbour(pts)),
                ["to_nearest_spawn"] = Percentiles.Distribution(pts.Select(p => NearestDistance(p, spawnPts, false)).Where(d => d.HasValue).Select(d => d!.Value)),
                ["height_above_floor"] = Percentiles.Distribution(heights),
                ["context"] = ctxJson,
                ["notes"] = new JsonArray("height_above_floor is medium reliability; context is medium-low (nearest floor sample within 2 m)."),
            }, null);
        }

        /// <summary>weapon / ammo / health / armor / powerup / other from the item table entry.</summary>
        internal static string ItemCategory(ItemCatalog catalog, string? className)
        {
            ItemDef? d = className is null ? null : catalog.Find(className);
            string cls = (className ?? string.Empty).ToLowerInvariant();
            if (d is not null && !string.IsNullOrWhiteSpace(d.GivesWeapon))
            {
                return "weapon";
            }

            if (d is not null && !string.IsNullOrWhiteSpace(d.AmmoFor))
            {
                return "ammo";
            }

            if (cls.Contains("health", StringComparison.Ordinal) || cls.Contains("medic", StringComparison.Ordinal) || cls.Contains("first aid", StringComparison.Ordinal))
            {
                return "health";
            }

            if (cls.Contains("armor", StringComparison.Ordinal) || cls.Contains("suit", StringComparison.Ordinal))
            {
                return "armor";
            }

            if (cls.Contains("amp", StringComparison.Ordinal) || cls.Contains("invuln", StringComparison.Ordinal) || cls.Contains("powerup", StringComparison.Ordinal))
            {
                return "powerup";
            }

            return d is null ? "unknown" : "other";
        }

        private (JsonObject?, string?) Ctf()
        {
            LevelObject? red = _items.Where(i => LevelIdentity.IsCtfFlagClass(i.ClassName) && i.ClassName.Contains("red", StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Uid).FirstOrDefault();
            LevelObject? blue = _items.Where(i => LevelIdentity.IsCtfFlagClass(i.ClassName) && i.ClassName.Contains("blue", StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Uid).FirstOrDefault();
            if (red is null || blue is null)
            {
                return (null, "not_ctf");
            }

            return (new JsonObject
            {
                ["reliability"] = "high",
                ["flag_distance"] = R(V(red.Position).Sub(V(blue.Position)).Length),
            }, null);
        }

        // ---- event idioms -------------------------------------------------------------------------

        private (JsonObject?, string?) Events()
        {
            var labels = new Dictionary<int, string>();
            foreach (LevelObject o in _doc.Objects)
            {
                labels.TryAdd(o.Uid, Label(o));
            }

            var edges = DocumentLinks.OriginatorEdges(_doc).Select(e => (e.From, e.To))
                .Concat(DocumentLinks.StructuralMoverEdges(_doc).Select(e => (e.From, e.To)))
                .Distinct()
                .ToList();
            int links = DocumentLinks.OriginatorEdges(_doc).Count();
            string L(int uid) => labels.TryGetValue(uid, out string? s) ? s : "Missing";
            var outs = new Dictionary<int, List<int>>();
            foreach ((int from, int to) in edges)
            {
                if (!outs.TryGetValue(from, out List<int>? l))
                {
                    l = new List<int>();
                    outs[from] = l;
                }

                l.Add(to);
            }

            foreach (List<int> l in outs.Values)
            {
                l.Sort();
            }

            var motifs = new Dictionary<string, int>(StringComparer.Ordinal);
            long paths = 0;
            bool truncated = false;
            void Count(string m)
            {
                motifs[m] = motifs.GetValueOrDefault(m) + 1;
                if ((++paths & 1023) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                }
            }

            List<int> Out(int uid) => outs.TryGetValue(uid, out List<int>? l) ? l : new List<int>();
            foreach ((int a, int b) in edges.OrderBy(e => e.From).ThenBy(e => e.To))
            {
                if (paths >= _o.MaxMotifPaths)
                {
                    truncated = true;
                    break;
                }

                string ab = L(a) + ">" + L(b);
                Count(ab);
                foreach (int c in Out(b))
                {
                    if (c == a || c == b)
                    {
                        continue;
                    }

                    string abc = ab + ">" + L(c);
                    Count(abc);
                    foreach (int d in Out(c))
                    {
                        if (d == a || d == b || d == c)
                        {
                            continue;
                        }

                        Count(abc + ">" + L(d));
                    }
                }
            }

            var eventClasses = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (LevelObject o in _doc.Objects.Where(o => o.Kind == LevelObjectKind.Event))
            {
                eventClasses[o.ClassName ?? string.Empty] = eventClasses.GetValueOrDefault(o.ClassName ?? string.Empty) + 1;
            }

            var top = motifs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(100)
                .Select(kv => (JsonNode)new JsonObject { ["motif"] = kv.Key, ["count"] = kv.Value }).ToArray();
            return (new JsonObject
            {
                ["reliability"] = "high",
                ["count"] = _doc.Objects.Count(o => o.Kind == LevelObjectKind.Event),
                ["triggers"] = _doc.Objects.Count(o => o.Kind == LevelObjectKind.Trigger),
                ["links"] = links,
                ["class_histogram"] = ToJson(eventClasses),
                ["motif_count"] = motifs.Count,
                ["motifs"] = new JsonArray(top),
                ["motifs_truncated"] = truncated,
            }, null);
        }

        /// <summary>A motif node label: <c>Event:&lt;Class&gt;</c> for events, else the object kind (no script names or UIDs).</summary>
        private static string Label(LevelObject o) => o.Kind switch
        {
            LevelObjectKind.Event => "Event:" + (o.ClassName ?? string.Empty),
            _ => o.Kind.ToString(),
        };

        // ---- helpers -----------------------------------------------------------------------------

        private static double R(double v) => Percentiles.Round(v);

        private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

        private static JsonObject ToJson(SortedDictionary<string, int> map)
        {
            var o = new JsonObject();
            foreach ((string k, int v) in map)
            {
                o[k] = v;
            }

            return o;
        }
    }
}
