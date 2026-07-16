using Ged.Core.Scripting;
using Xunit;

namespace Ged.Core.Tests.Scripting;

/// <summary>Facade-only tests (no engine) for script header metadata parsing (plan §6.5).</summary>
public sealed class ScriptMetadataTests
{
    [Fact]
    public void Parses_Command_Header()
    {
        const string src = @"--@name  Array Selected
--@id    array-selected
--@category Scripts
--@key   Ctrl+Shift+A
--@desc  Arrays the current selection
--@api   1

selection.all()";
        ScriptMetadata m = ScriptMetadata.Parse(src);

        Assert.True(m.IsCommand);
        Assert.Equal("Array Selected", m.Name);
        Assert.Equal("array-selected", m.Id);
        Assert.Equal("Scripts", m.Category);
        Assert.Equal("Ctrl+Shift+A", m.Key);
        Assert.Equal("Arrays the current selection", m.Description);
        Assert.Equal(1, m.ApiVersion);
        Assert.False(m.AllowDestructive);
    }

    [Fact]
    public void Allow_Destructive_Flag_Is_Read()
    {
        ScriptMetadata m = ScriptMetadata.Parse("--@allow-destructive\nops.save()");
        Assert.True(m.AllowDestructive);
    }

    [Fact]
    public void Header_Stops_At_First_Code_Line()
    {
        // A directive after real code is ignored (header must be at the top).
        ScriptMetadata m = ScriptMetadata.Parse("local x = 1\n--@name Nope");
        Assert.Null(m.Name);
        Assert.False(m.IsCommand);
    }

    [Fact]
    public void Id_Is_Slugified()
    {
        ScriptMetadata m = ScriptMetadata.Parse("--@name Test\n--@id  My Fancy Script!!");
        Assert.Equal("my-fancy-script", m.Id);
    }

    [Fact]
    public void No_Header_Yields_Defaults()
    {
        ScriptMetadata m = ScriptMetadata.Parse("return 1 + 1");
        Assert.False(m.IsCommand);
        Assert.Equal("Scripts", m.Category);
        Assert.Null(m.ApiVersion);
    }
}
