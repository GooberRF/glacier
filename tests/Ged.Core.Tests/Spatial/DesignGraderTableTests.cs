using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Spatial;
using Xunit;

namespace Ged.Core.Tests.Spatial;

/// <summary>
/// The grader's threshold table (design-grader.json): every check is documented with a fix hint, reads only known
/// metrics, names only known location kinds, and the rule language evaluates with the exemplar study's semantics.
/// </summary>
public sealed class DesignGraderTableTests
{
    [Fact]
    public void Every_Check_Is_Documented_Reads_Known_Metrics_And_Known_Locations()
    {
        var known = new HashSet<string>(DesignMetrics.Ids, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? c in DesignGrader.Table["checks"]!.AsArray())
        {
            string id = c!["id"]!.GetValue<string>();
            Assert.True(ids.Add(id), $"duplicate check {id}");
            Assert.False(string.IsNullOrWhiteSpace(c["doc"]?.GetValue<string>()), id);
            Assert.False(string.IsNullOrWhiteSpace(c["fix"]?.GetValue<string>()), id);
            foreach (string key in new[] { "rule", "open_air", "payload" })
            {
                if (c[key] is JsonObject rule)
                {
                    foreach (string m in Metrics(rule))
                    {
                        Assert.True(known.Contains(m), $"{id}.{key} reads unknown metric '{m}'");
                    }
                }
            }

            foreach (JsonNode? kind in c["locate"]?.AsArray() ?? new JsonArray())
            {
                Assert.Contains(kind!.GetValue<string>(), SpatialAnalysis.LocationKinds);
            }

            if (c["explain"]?.GetValue<string>() is { } explain)
            {
                Assert.Contains(explain, SpatialAnalysis.DetailKinds);
            }

            if (c["explain_metric"]?.GetValue<string>() is { } em)
            {
                Assert.True(known.Contains(em), $"{id}.explain_metric reads unknown metric '{em}'");
            }
        }

        Assert.Equal(new[] { "G24", "G25", "G26" }, DesignGrader.Table["checks"]!.AsArray().Where(c => c!["hard"]?.GetValue<bool>() == true).Select(c => c!["id"]!.GetValue<string>()));
        Assert.True(ids.Count >= 35);
    }

    [Fact]
    public void Rules_Follow_The_Study_Semantics()
    {
        var m = new Dictionary<string, double?> { ["a"] = 5, ["b"] = null };
        JsonNode Parse(string s) => JsonNode.Parse(s)!;
        Assert.True(DesignGrader.Evaluate(Parse("""{"metric":"a","min":1,"max":5}"""), m));
        Assert.Null(DesignGrader.Evaluate(Parse("""{"metric":"b","min":1}"""), m)); // missing = not measurable
        Assert.False(DesignGrader.Evaluate(Parse("""{"all":[{"metric":"a","max":4},{"metric":"b","min":1}]}"""), m));
        Assert.Null(DesignGrader.Evaluate(Parse("""{"all":[{"metric":"b","min":1}]}"""), m));
        Assert.True(DesignGrader.Evaluate(Parse("""{"any":[{"metric":"a","max":4},{"metric":"a","min":5}]}"""), m));
        Assert.True(DesignGrader.Evaluate(Parse("""{"metric":"b","lt":1,"default":0}"""), m)); // the study's `or 0`
        Assert.Null(DesignGrader.Evaluate(Parse("""{"na_if":{"metric":"a","ge":5},"then":{"metric":"a","max":1}}"""), m));
        Assert.True(DesignGrader.Evaluate(Parse("""{"fewer_than":2,"of":[{"metric":"a","ge":5},{"metric":"b","ge":1,"default":0}]}"""), m));
    }

    private static IEnumerable<string> Metrics(JsonNode n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["metric"]?.GetValue<string>() is { } m)
                {
                    yield return m;
                }

                if (o["na_if_missing"]?.GetValue<string>() is { } nm)
                {
                    yield return nm;
                }

                foreach (KeyValuePair<string, JsonNode?> kv in o)
                {
                    if (kv.Value is JsonObject or JsonArray)
                    {
                        foreach (string x in Metrics(kv.Value))
                        {
                            yield return x;
                        }
                    }
                }

                break;
            case JsonArray a:
                foreach (JsonNode? x in a)
                {
                    foreach (string y in Metrics(x!))
                    {
                        yield return y;
                    }
                }

                break;
        }
    }
}
