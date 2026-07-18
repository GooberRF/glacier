using System.IO;
using Ged.App.Services;
using Xunit;

namespace Ged.App.Tests;

/// <summary>
/// The Help ▸ Help Topics (F1) command resolves the offline HTML reference. In the dev tree
/// there is no <c>help.html</c> beside the test binary, so the resolver must fall back to
/// <c>docs/help.html</c> — the file the packaging step stages beside the shipped exe.
/// </summary>
public sealed class HelpReferenceTests
{
    [Fact]
    public void ResolvePath_Finds_An_Existing_Help_File()
    {
        string? path = HelpReference.ResolvePath();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"resolved help path should exist: {path}");
        Assert.EndsWith("help.html", path);
    }

    [Fact]
    public void Discord_Invite_Points_At_The_Community_Server()
    {
        Assert.Equal("https://discord.gg/factionfiles", HelpReference.DiscordUrl);
    }

    [Fact]
    public void Issues_Url_Points_At_The_Glacier_Issue_Tracker()
    {
        Assert.Equal("https://github.com/GooberRF/glacier/issues", HelpReference.IssuesUrl);
    }
}
