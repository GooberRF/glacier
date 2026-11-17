using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Ged.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Ged.App.Tests;

/// <summary>Headless Avalonia app for the App-layer control tests.</summary>
public sealed class TestApp : Application
{
    public TestApp() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    /// <summary>
    /// Set to 1 to render with Skia instead of the headless drawing stub, so screenshot evidence
    /// (<see cref="P2ScreenshotEvidence"/>) can capture real frames. The normal test run leaves it unset.
    /// </summary>
    public const string RenderVariable = "GED_APP_TESTS_RENDER";

    public static AppBuilder BuildAvaloniaApp() =>
        System.Environment.GetEnvironmentVariable(RenderVariable) == "1"
            ? AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            : AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
