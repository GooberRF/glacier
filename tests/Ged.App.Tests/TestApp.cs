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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
