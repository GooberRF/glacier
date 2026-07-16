using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Ged.App.Camera;
using Ged.Core.Input;
using Ged.Core.Playtest;

namespace Ged.App.Dialogs;

/// <summary>
/// The first-launch setup dialog: locate (and validate) the RF install, pick a
/// keymap preset (RED Classic / Modern), a camera scheme, and a theme, then write
/// the choices to <see cref="AppSettings"/> and the <see cref="Keymap"/>. Shown
/// once (guarded by <see cref="AppSettings.FirstRunComplete"/>).
/// </summary>
internal sealed class FirstRunWizard : Window
{
    private readonly AppSettings _settings;
    private readonly Keymap _keymap;
    private readonly FirstRunConfig _config;
    private readonly TextBox _installBox = new() { Watermark = $"…{Path.DirectorySeparatorChar}Red Faction", MinWidth = 320 };
    private readonly TextBlock _installStatus = new() { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _exeBox = new() { Watermark = $"…{Path.DirectorySeparatorChar}AlpineFactionLauncher.exe", MinWidth = 320 };
    private readonly TextBlock _exeStatus = new() { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _preset = new();
    private readonly ComboBox _scheme = new();
    private readonly ComboBox _theme = new();
    private bool _suppressExeChanged;

    public FirstRunWizard(AppSettings settings, Keymap keymap)
    {
        _settings = settings;
        _keymap = keymap;
        _config = new FirstRunConfig(
            settings.RfInstallDir ?? GuessInstall(),
            string.IsNullOrWhiteSpace(settings.GameExePath) ? null : settings.GameExePath,
            // Auto-detect the launcher from the af:// protocol registration (item 6): prefills
            // the exe box even when the install dir alone doesn't locate it.
            Services.WindowsAfProtocolReader.Instance);

        Title = "Welcome to Glacier";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _preset.ItemsSource = CommandCatalog.PresetNames;
        _preset.SelectedItem = _keymap.PresetName;
        _scheme.ItemsSource = Enum.GetValues<CameraSchemeKind>().Select(CameraSchemes.DisplayName).ToList();
        _scheme.SelectedIndex = Math.Clamp(_settings.CameraScheme, 0, 3);
        _theme.ItemsSource = new[] { "Dark", "Light" };
        _theme.SelectedIndex = _settings.DarkTheme ? 0 : 1;

        _installBox.Text = _config.RfInstallDir ?? string.Empty;
        _exeBox.Text = _config.GameExePath ?? string.Empty;
        _installBox.TextChanged += (_, _) =>
        {
            _config.SetInstallDir(_installBox.Text);
            // A blank/guessed exe follows the install dir; a manual pick is left untouched.
            _suppressExeChanged = true;
            _exeBox.Text = _config.GameExePath ?? string.Empty;
            _suppressExeChanged = false;
            Validate();
            ValidateExe();
        };
        _exeBox.TextChanged += (_, _) =>
        {
            if (!_suppressExeChanged)
            {
                _config.SetGameExe(_exeBox.Text);
            }

            ValidateExe();
        };

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await BrowseAsync();

        var exeBrowse = new Button { Content = "Browse…" };
        exeBrowse.Click += async (_, _) => await BrowseExeAsync();

        var finish = new Button { Content = "Finish", IsDefault = true, MinWidth = 90 };
        finish.Click += (_, _) => Apply();

        var skip = new Button { Content = "Skip", MinWidth = 90 };
        skip.Click += (_, _) => { _settings.FirstRunComplete = true; Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Let's get set up.", FontWeight = FontWeight.SemiBold, FontSize = 16 },
                Label("Red Faction install folder"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { _installBox, browse },
                },
                _installStatus,
                Label(OperatingSystem.IsWindows()
                    ? "Alpine Faction launcher (AlpineFactionLauncher.exe — needed to play-test)"
                    : "Alpine Faction launcher (the Windows AlpineFactionLauncher.exe — runs via Wine; see the launch template in Settings)"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { _exeBox, exeBrowse },
                },
                _exeStatus,
                Label("Keymap preset"),
                _preset,
                Label("Camera scheme"),
                _scheme,
                Label("Theme"),
                _theme,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { finish, skip },
                },
            },
        };

        Validate();
        ValidateExe();
    }

    // Wizard labels WRAP (item 5): the window is a fixed 480 px and cannot resize, so a long
    // label — e.g. the Alpine-launcher caption — must wrap instead of clipping at the edge.
    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Opacity = 0.8,
        Margin = new Avalonia.Thickness(0, 6, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private static string GuessInstall()
    {
        foreach (string c in InstallGuesses())
        {
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
            {
                return c;
            }
        }

        return string.Empty;
    }

    /// <summary>Common RF-install locations to probe, per platform (Steam/Wine on Linux, drive paths on Windows).</summary>
    private static IEnumerable<string> InstallGuesses()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files (x86)\Steam\steamapps\common\Red Faction";
            yield break;
        }

        // Linux: Steam (native/Proton) and typical Wine-prefix drive_c locations. POSIX paths,
        // case-sensitive — these match the directory names Steam/GOG actually create.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "Red Faction");
        yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Red Faction");
        yield return Path.Combine(home, ".wine", "drive_c", "Program Files (x86)", "Steam", "steamapps", "common", "Red Faction");
        yield return Path.Combine(home, ".wine", "drive_c", "RedFaction");
    }

    private void Validate()
    {
        string dir = _installBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir))
        {
            _installStatus.Text = "Optional — set later in Settings. Textures/meshes won't resolve until set.";
            _installStatus.Foreground = new SolidColorBrush(Colors.Gray);
            return;
        }

        // Shared classifier (item 7): "✓ found N VPPs (+ alpinefaction.vpp)" / guidance.
        Ged.Core.Assets.RfInstallScan scan = Ged.Core.Assets.RfInstall.Scan(dir);
        _installStatus.Text = scan.StatusText();
        _installStatus.Foreground = new SolidColorBrush(scan.Valid ? Colors.MediumSeaGreen : Colors.IndianRed);
    }

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate your Red Faction install",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is string p)
        {
            _installBox.Text = p;
        }
    }

    private void ValidateExe()
    {
        string exe = _exeBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exe))
        {
            _exeStatus.Text = "Set it here or later in Settings. Play-test buttons stay disabled until set.";
            _exeStatus.Foreground = new SolidColorBrush(Colors.Gray);
        }
        else if (!File.Exists(exe))
        {
            _exeStatus.Text = "✗ File not found — check the path.";
            _exeStatus.Foreground = new SolidColorBrush(Colors.IndianRed);
        }
        else if (GameLauncher.DetectKind(exe) == GameKind.AlpineLauncher)
        {
            _exeStatus.Text = "✓ Alpine Faction launcher.";
            _exeStatus.Foreground = new SolidColorBrush(Colors.MediumSeaGreen);
        }
        else
        {
            _exeStatus.Text = "⚠ Not AlpineFactionLauncher.exe — play-testing runs through the Alpine Faction launcher.";
            _exeStatus.Foreground = new SolidColorBrush(Colors.Orange);
        }
    }

    private async Task BrowseExeAsync()
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate AlpineFactionLauncher.exe",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } } },
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is string p)
        {
            _config.SetGameExe(p);
            _suppressExeChanged = true;
            _exeBox.Text = p;
            _suppressExeChanged = false;
            ValidateExe();
        }
    }

    private void Apply()
    {
        if (_config.RfInstallDir is { } dir && Directory.Exists(dir))
        {
            _settings.RfInstallDir = dir;
        }

        // Persist the guessed/picked game exe when it resolves to a real file; an unset or
        // invalid exe is left blank so play-test resolution re-guesses (or prompts).
        if (_config.GameExePath is { } exe && File.Exists(exe))
        {
            _settings.GameExePath = exe;
        }

        if (_preset.SelectedItem is string preset)
        {
            _keymap.ApplyPreset(preset);
        }

        _settings.CameraScheme = Math.Clamp(_scheme.SelectedIndex, 0, 3);
        _settings.DarkTheme = _theme.SelectedIndex == 0;
        _settings.FirstRunComplete = true;
        Close();
    }
}
