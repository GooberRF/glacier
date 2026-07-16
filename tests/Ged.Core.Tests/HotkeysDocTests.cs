using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ged.Core.Input;
using Xunit;

namespace Ged.Core.Tests;

/// <summary>
/// Generates <c>docs/internal/HOTKEYS.md</c> from the CommandCatalog presets so the published
/// binding table can never drift from the code. Runs as a test: it writes the file
/// (when the repo tree is present) and asserts the table is well-formed.
/// </summary>
public sealed class HotkeysDocTests
{
    [Fact]
    public void Generate_Hotkeys_Doc_From_Command_Presets()
    {
        IReadOnlyList<CommandSpec> all = CommandCatalog.All;
        Assert.NotEmpty(all);

        var sb = new StringBuilder();
        sb.AppendLine("# Glacier — Keyboard Shortcuts");
        sb.AppendLine();
        sb.AppendLine("Auto-generated from the CommandRegistry presets (`CommandCatalog`) by");
        sb.AppendLine("`HotkeysDocTests` — do not edit by hand; edit the catalog and re-run the tests.");
        sb.AppendLine();
        sb.AppendLine("Two presets ship: **RED Classic** (reproduces the stock RED hotkeys plus the");
        sb.AppendLine("Alpine additions) and **Modern**. Every binding is rebindable in Settings ▸ Input,");
        sb.AppendLine("and every command is reachable from the command palette (Ctrl+Shift+P). A blank");
        sb.AppendLine("cell means the command has no default key in that preset (still bindable).");
        sb.AppendLine();
        sb.AppendLine("## Mouse-driven transforms & snapping");
        sb.AppendLine();
        sb.AppendLine("- **M + arrows** move / **R + arrows** rotate / **S** scale the selection by the");
        sb.AppendLine("  grid / rotation / scale increment (RED-parity, always increment-based).");
        sb.AppendLine("- **M + LMB drag** moves in the view plane; **N + LMB drag** moves along the");
        sb.AppendLine("  dominant world axis. The transform gizmos (Tools ▸ Transform Gizmo) drag");
        sb.AppendLine("  move / rotate / scale handles at the selection pivot.");
        sb.AppendLine("- The **magnet snap** toggle (viewport toolbar / *Toggle Snap*) snaps all mouse");
        sb.AppendLine("  drags: move to absolute world-grid multiples, rotate to the rotation step,");
        sb.AppendLine("  scale to the scale step. **Hold Alt during a drag to temporarily invert the");
        sb.AppendLine("  snap state** (snap↔free).");
        sb.AppendLine("- **Linux:** many X11 window managers claim plain **Alt+drag** for window moves.");
        sb.AppendLine("  Hold **Ctrl+Alt** during the drag instead (it reaches the viewport with Alt down");
        sb.AppendLine("  and inverts the snap), or rebind the WM's move modifier to Super.");
        sb.AppendLine();

        int rows = 0;
        foreach (IGrouping<string, CommandSpec> group in all.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| Command | RED Classic | Modern |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (CommandSpec c in group.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                string name = c.Name + (c.Implemented ? string.Empty : " *(later)*");
                sb.AppendLine($"| {name} | {Cell(c.RedGesture)} | {Cell(c.ModernGesture)} |");
                rows++;
            }

            sb.AppendLine();
        }

        Assert.Equal(all.Count, rows); // every command is documented

        // Write the doc into the repo tree when available (skips gracefully in a bare
        // test sandbox); assert on the produced text either way.
        string text = sb.ToString();
        if (TestPaths.RepoRoot is { } root)
        {
            string docsDir = Path.Combine(root, "docs", "internal");
            Directory.CreateDirectory(docsDir);
            File.WriteAllText(Path.Combine(docsDir, "HOTKEYS.md"), text);
        }

        Assert.Contains("RED Classic", text);
        Assert.Contains("| Undo | `Ctrl+Z` | `Ctrl+Z` |", text);
        Assert.Contains("Hold Alt during a drag", text);
    }

    private static string Cell(string? gesture) =>
        string.IsNullOrEmpty(gesture) ? " " : $"`{gesture}`";
}
