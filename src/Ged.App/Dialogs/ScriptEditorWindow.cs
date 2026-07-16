using System;
using System.IO;
using System.Threading;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using Ged.App.Services;
using Ged.Core;
using Ged.Core.Scripting;

namespace Ged.App.Dialogs;

/// <summary>
/// The Script Editor (plan §6.2): a peer window of <c>UvUnwrapWindow</c> using AvaloniaEdit for the
/// code surface with Lua syntax highlighting, a Run / Dry-Run / Run-Selection toolbar, a
/// capabilities banner, and inline error reporting into the status line + Log output. Unknown
/// (unsaved) scripts default to Dry-Run (§6.7). Output routes to the shared Script Log.
/// </summary>
internal sealed class ScriptEditorWindow : Window
{
    private readonly ScriptingService _scripting;
    private readonly TextEditor _editor;
    private readonly TextBlock _status;
    private readonly CheckBox _allowDestructive;
    private string? _path;

    public ScriptEditorWindow(ScriptingService scripting, string? path = null, string? initialSource = null)
    {
        _scripting = scripting;
        _path = path;

        Title = "Script Editor — Glacier";
        Width = 860;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Scope AvaloniaEdit's Fluent theme to this window so we don't touch the shared App styles.
        Styles.Add(new StyleInclude(new Uri("avares://Glacier/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });

        _editor = new TextEditor
        {
            FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo, monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            Text = initialSource ?? DefaultTemplate,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 2;
        InstallLuaHighlighting(_editor);
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.KeyDown += OnEditorKeyDown;

        _status = new TextBlock
        {
            Margin = new Thickness(8, 4),
            TextWrapping = TextWrapping.Wrap,
            Text = "Ready.",
        };

        _allowDestructive = new CheckBox { Content = "Allow destructive ops", IsChecked = false, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };

        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4a, 0x9e, 0xd8)),
            Padding = new Thickness(10, 6),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This script can modify the level (one undo step). It cannot read your files or access the network. "
                     + "Dry-Run previews changes without applying them.",
            },
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 6),
            Children =
            {
                Btn("▶ Run", () => Run(dryRun: false)),
                Btn("👁 Dry-Run", () => Run(dryRun: true)),
                Btn("Run Selection", RunSelection),
                new Border { Width = 1, Background = Brushes.Gray, Margin = new Thickness(4, 2) },
                Btn("New", NewScript),
                Btn("Open…", OpenAsync),
                Btn("Save", () => SaveAsync(false)),
                Btn("Save As…", () => SaveAsync(true)),
                _allowDestructive,
            },
        };

        var top = new StackPanel { Children = { banner, toolbar } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_status, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_editor);
        Content = root;

        UpdateTitle();
    }

    private Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button Btn(string text, Func<System.Threading.Tasks.Task> onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4) };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private void Run(bool dryRun) => Execute(_editor.Text ?? string.Empty, dryRun);

    private void RunSelection()
    {
        string sel = _editor.SelectedText;
        if (string.IsNullOrWhiteSpace(sel))
        {
            _status.Text = "Nothing selected.";
            return;
        }

        Execute(sel, dryRun: false);
    }

    private void Execute(string source, bool dryRun)
    {
        string name = _path is { Length: > 0 } p ? Path.GetFileName(p) : "editor";
        ScriptRunResult result = _scripting.Run(source, name, dryRun, _allowDestructive.IsChecked == true, CancellationToken.None);

        if (result.Success)
        {
            string mode = result.WasDryRun ? "Dry-run" : (result.Committed ? "Applied (1 undo step)" : "Ran");
            string rv = result.ReturnValue is { Length: > 0 } r ? $" → {r}" : string.Empty;
            _status.Foreground = Brushes.Gray;
            _status.Text = $"{mode}{rv}.";
        }
        else if (result.Error is { } err)
        {
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0x5a, 0x5a));
            _status.Text = err.ToDisplayString();
            if (err.Line > 0)
            {
                ScrollToLine(err.Line);
            }
        }
    }

    private void ScrollToLine(int line)
    {
        try
        {
            line = Math.Clamp(line, 1, Math.Max(1, _editor.Document.LineCount));
            var docLine = _editor.Document.GetLineByNumber(line);
            _editor.CaretOffset = docLine.Offset;
            _editor.ScrollToLine(line);
            _editor.Focus();
        }
        catch (Exception)
        {
            // best-effort navigation
        }
    }

    private void NewScript()
    {
        _editor.Text = DefaultTemplate;
        _path = null;
        UpdateTitle();
        _status.Text = "New script.";
    }

    private async System.Threading.Tasks.Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open Lua Script",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Lua script") { Patterns = new[] { "*.lua" } } },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } local)
        {
            _editor.Text = await File.ReadAllTextAsync(local);
            _path = local;
            UpdateTitle();
            _status.Text = $"Opened {Path.GetFileName(local)}.";
        }
    }

    private async System.Threading.Tasks.Task SaveAsync(bool saveAs)
    {
        string? target = _path;
        if (saveAs || string.IsNullOrEmpty(target))
        {
            string startDir = AppPaths.ScriptsDirectory;
            Directory.CreateDirectory(startDir);
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save Lua Script",
                DefaultExtension = "lua",
                SuggestedFileName = "script.lua",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(startDir)),
                FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Lua script") { Patterns = new[] { "*.lua" } } },
            });
            target = file?.TryGetLocalPath();
        }

        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        await File.WriteAllTextAsync(target, _editor.Text ?? string.Empty);
        _path = target;
        UpdateTitle();
        _status.Text = $"Saved {Path.GetFileName(target)}. Reload the Scripts library to see it in the palette.";
    }

    private void UpdateTitle() =>
        Title = _path is { Length: > 0 } p ? $"Script Editor — {Path.GetFileName(p)}" : "Script Editor — untitled";

    private static void InstallLuaHighlighting(TextEditor editor)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(LuaXshd));
            editor.SyntaxHighlighting = AvaloniaEdit.Highlighting.Xshd.HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception)
        {
            // Highlighting is a nicety; the editor still works plain if the grammar fails to load.
        }
    }

    // ---- Static API completion (plan §6.3) ------------------------------------

    private static readonly IReadOnlyList<string> Globals = ScriptApiReference.GlobalNames();
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, IReadOnlyList<string>> MembersByReceiver =
        ScriptApiReference.MemberNamesByReceiver();

    private CompletionWindow? _completion;

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".")
        {
            ShowMemberCompletion();
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!ShowMemberCompletion())
            {
                ShowCompletion(Globals, replaceCurrentWord: true);
            }

            e.Handled = true;
        }
    }

    private bool ShowMemberCompletion()
    {
        int caret = _editor.CaretOffset;
        // Skip the just-typed '.' if present.
        int dot = caret;
        if (dot > 0 && _editor.Document.GetCharAt(dot - 1) == '.')
        {
            dot--;
        }

        string receiver = WordEndingAt(dot);
        if (receiver.Length > 0 && MembersByReceiver.TryGetValue(receiver, out IReadOnlyList<string>? members))
        {
            ShowCompletion(members, replaceCurrentWord: false);
            return true;
        }

        return false;
    }

    private void ShowCompletion(IReadOnlyList<string> items, bool replaceCurrentWord)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            var window = new CompletionWindow(_editor.TextArea);
            if (replaceCurrentWord)
            {
                string word = WordEndingAt(_editor.CaretOffset);
                window.StartOffset = _editor.CaretOffset - word.Length;
            }

            foreach (string item in items)
            {
                window.CompletionList.CompletionData.Add(new ApiCompletion(item));
            }

            window.Closed += (_, _) => _completion = null;
            _completion = window;
            window.Show();
        }
        catch (Exception)
        {
            // Completion is a nicety; never let it break editing.
        }
    }

    private string WordEndingAt(int offset)
    {
        int start = offset;
        while (start > 0)
        {
            char c = _editor.Document.GetCharAt(start - 1);
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                start--;
            }
            else
            {
                break;
            }
        }

        return _editor.Document.GetText(start, offset - start);
    }

    private sealed class ApiCompletion : ICompletionData
    {
        public ApiCompletion(string text) => Text = text;

        public Avalonia.Media.IImage? Image => null;

        public string Text { get; }

        public object Content => Text;

        public object Description => "Glacier scripting API";

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }

    private const string DefaultTemplate =
        "--@name  My Script\n--@id    my-script\n--@category Scripts\n--@desc  What this script does\n--@api   1\n\n"
        + "-- The whole run is one undo step. Dry-Run to preview.\n"
        + "log.info(\"objects: \" .. level.count .. \", brushes: \" .. level.brush_count)\n";

    // AvaloniaEdit native highlighting (no TextMateSharp dependency needed for Lua).
    private const string LuaXshd = """
<SyntaxDefinition name="Lua" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#6A9955" />
  <Color name="String" foreground="#CE9178" />
  <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
  <Color name="Api" foreground="#4EC9B0" />
  <Color name="Number" foreground="#B5CEA8" />
  <RuleSet ignoreCase="false">
    <Span color="Comment" multiline="true" begin="--\[\[" end="\]\]" />
    <Span color="Comment" begin="--" />
    <Span color="String" begin="&quot;" end="&quot;">
      <RuleSet><Span begin="\\" end="." /></RuleSet>
    </Span>
    <Span color="String" begin="'" end="'">
      <RuleSet><Span begin="\\" end="." /></RuleSet>
    </Span>
    <Keywords color="Keyword">
      <Word>and</Word><Word>break</Word><Word>do</Word><Word>else</Word><Word>elseif</Word>
      <Word>end</Word><Word>false</Word><Word>for</Word><Word>function</Word><Word>if</Word>
      <Word>in</Word><Word>local</Word><Word>nil</Word><Word>not</Word><Word>or</Word>
      <Word>repeat</Word><Word>return</Word><Word>then</Word><Word>true</Word><Word>until</Word>
      <Word>while</Word>
    </Keywords>
    <Keywords color="Api">
      <Word>ged</Word><Word>level</Word><Word>selection</Word><Word>assets</Word><Word>ops</Word>
      <Word>lint</Word><Word>log</Word><Word>rng</Word><Word>print</Word>
    </Keywords>
    <Rule color="Number">\b0[xX][0-9a-fA-F]+|\b\d+(\.\d+)?([eE][+-]?\d+)?</Rule>
  </RuleSet>
</SyntaxDefinition>
""";
}
