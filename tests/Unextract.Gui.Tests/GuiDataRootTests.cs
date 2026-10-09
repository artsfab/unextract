using System.IO;
using Unextract.Gui.Services;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.Gui.Tests;

// The data root switch for the UI E2E only (environment variable UNEXTRACT_GUI_TEST_DATA_ROOT, read only by the composition root):
// unset keeps %LOCALAPPDATA%\unextract; an existing absolute directory gives <root>\settings.json and <root>\logs; an empty,
// relative, missing or file path is an error (start-up aborts) without falling back to the normal location. Not a user feature.
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
        string root = ArchiveSearchTests.Fixture();
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
        string root = ArchiveSearchTests.Fixture();
        Assert.NotNull(GuiDataRoot.Resolve(Path.Combine(root, "missing")).Error);
        string file = Path.Combine(root, "file.txt");
        File.WriteAllText(file, "x");
        Assert.NotNull(GuiDataRoot.Resolve(file).Error);
    }
}
