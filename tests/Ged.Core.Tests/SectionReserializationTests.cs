using System.Collections.Generic;
using System.IO;
using System.Text;
using Ged.Core.IO;
using Ged.Core.IO.Rfl;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Test B: for every section of every corpus file that has a parser, parse the
/// section and force a re-serialization from the model. The produced bytes must
/// equal the original section payload exactly, proving lossless understanding.
/// </summary>
public sealed class SectionReserializationTests
{
    [Theory]
    [MemberData(nameof(Corpus.RflFileNames), MemberType = typeof(Corpus))]
    public void Every_Parsed_Section_Reserializes_Exactly(string? fileName)
    {
        if (fileName is null)
        {
            return; // corpus unavailable
        }

        string path = Path.Combine(Corpus.Directory!, fileName);
        RflFile file = RflFile.Load(path);
        RflContext ctx = file.Context;

        var failures = new List<string>();
        int parsedCount = 0;

        for (int i = 0; i < file.Sections.Count; i++)
        {
            RflSection section = file.Sections[i];
            if (!RflSectionRegistry.HasParser(section.TypeId))
            {
                continue;
            }

            IRflSectionContent content;
            try
            {
                RflSectionRegistry.TryParse(section, ctx, out IRflSectionContent? parsed);
                content = parsed!;
            }
            catch (Exception ex)
            {
                failures.Add($"section[{i}] 0x{section.TypeId:X8}: parse threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            parsedCount++;

            var w = new RfWriter(section.RawBytes.Length);
            content.Write(w, ctx);
            byte[] produced = w.ToArray();

            if (!produced.AsSpan().SequenceEqual(section.RawBytes))
            {
                failures.Add(Describe(i, section, produced));
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{fileName}: {failures.Count} section(s) failed to reserialize (of {parsedCount} parsed):\n"
            + string.Join("\n", failures));
    }

    private static string Describe(int index, RflSection section, byte[] produced)
    {
        var sb = new StringBuilder();
        sb.Append($"section[{index}] 0x{section.TypeId:X8} ({section.Type}): ");
        sb.Append($"raw={section.RawBytes.Length} produced={produced.Length}");
        int diff = FirstDifference(section.RawBytes, produced);
        sb.Append($" firstDiff@{diff}");
        return sb.ToString();
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int n = System.Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }
}
