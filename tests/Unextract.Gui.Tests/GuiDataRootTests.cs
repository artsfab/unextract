using System.IO;
using Unextract.Gui.Services;

namespace Unextract.Gui.Tests;

public sealed class GuiDataRootTests
{
    [Fact]
    public void UnsetKeepsTheNormalLocations()
    {
        var result = GuiDataRoot.Resolve(null);
        Assert.Null(result.SettingsPath);
        Assert.Null(result.LogDirectory);
        Assert.Null(result.Error);
        // The defaults themselves stay under %LOCALAPPDATA%\unextract.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "unextract", "logs"), new LogLocation().Directory);
    }

    [Fact]
    public void AbsoluteExistingDirectoryMovesSettingsAndLogsUnderTheRoot()
    {
        string root = ArchiveSearchTests.Fixture("dataroot");
        var result = GuiDataRoot.Resolve(root);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(root, "settings.json"), result.SettingsPath);
        Assert.Equal(Path.Combine(root, "logs"), result.LogDirectory);
        // Resolving only decides paths: nothing is created.
        Assert.False(Path.Exists(result.SettingsPath));
        Assert.False(Directory.Exists(result.LogDirectory));
        Assert.Equal(result.LogDirectory, new LogLocation(result.LogDirectory).Directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative")]
    [InlineData(@".\relative")]
    [InlineData(@"\\?\Z:\unextract-no-such-root\")]
    [InlineData("bad\0name")]
    public void BadValueAbortsWithoutFallingBackToTheNormalLocations(string value)
    {
        var result = GuiDataRoot.Resolve(value);
        Assert.NotNull(result.Error);
        Assert.Null(result.SettingsPath);
        Assert.Null(result.LogDirectory);
    }

    [Fact]
    public void MissingAbsoluteDirectoryAndFileAreRejected()
    {
        string root = ArchiveSearchTests.Fixture("dataroot");
        Assert.NotNull(GuiDataRoot.Resolve(Path.Combine(root, "missing")).Error);
        string file = Path.Combine(root, "file.txt");
        File.WriteAllText(file, "x");
        Assert.NotNull(GuiDataRoot.Resolve(file).Error);
    }
}
