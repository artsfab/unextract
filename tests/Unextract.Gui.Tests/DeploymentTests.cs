using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;

namespace Unextract.Gui.Tests;

// Tests that create WPF windows run serially in one collection. When xUnit ran the classes that create MainWindow in
// parallel, this test failed intermittently; with those classes in the "Wpf" collection the failure no longer
// reproduced (Gui.Tests then passed 15 consecutive runs). It was seen only under parallel test execution, not in the
// product. The mechanism inside WPF was not identified.
[Collection("Wpf")]
public sealed class DeploymentTests
{
    internal static string GuiOutputDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && directory.Name != "bin") directory = directory.Parent;
            Assert.NotNull(directory);
            var suffix = Path.GetRelativePath(directory.FullName, AppContext.BaseDirectory);
            var root = directory.Parent?.Parent?.Parent?.FullName;
            Assert.NotNull(root);
            return Path.Combine(root, "src", "Unextract.Gui", "bin", suffix);
        }
    }

    [Fact]
    public async Task BuildBundlesRunnableCliWithoutGuiAssemblyDependencies()
    {
        string output = GuiOutputDirectory;
        Assert.True(File.Exists(Path.Combine(output, "unextract-gui.exe")), output);
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "unextract-gui.deps.json")));
        foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
        {
            Assert.DoesNotContain("Unextract.Core/", library.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Unextract.Windows/", library.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("unextract/", library.Name, StringComparison.Ordinal);
        }
        Assert.False(File.Exists(Path.Combine(output, "unextract.dll")));
        Assert.False(File.Exists(Path.Combine(output, "Unextract.Core.dll")));
        Assert.False(File.Exists(Path.Combine(output, "Unextract.Windows.dll")));
        string cli = new CliLocation(output).ExecutablePath;
        Assert.True(File.Exists(cli), cli);
        Assert.True(File.Exists(Path.Combine(output, "cli", "unextract.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(output, "cli", "Unextract.Core.dll")));
        Assert.True(File.Exists(Path.Combine(output, "cli", "Unextract.Windows.dll")));

        // No arguments: input error only. No user archive or target is touched.
        using var process = Process.Start(new ProcessStartInfo(cli)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        });
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("analyze", await stdout + await stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCliDoesNotUseAnExeBesideGuiAndIsExplainedInWpfView()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "gui-deployment-" + Guid.NewGuid().ToString("N"), "日本語 空白");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "unextract.exe"), "not the bundled CLI");
        var availability = new CliLocation(fixture).Check();
        Assert.False(availability.IsAvailable);
        Assert.Equal(Path.Combine(fixture, "cli", "unextract.exe"), availability.Path);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var model = new MainViewModel(availability);
                var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Opacity = 0 };
                window.Show();
                window.UpdateLayout();
                var text = Assert.IsType<TextBlock>(window.FindName("CliStatusText"));
                text.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                Assert.Contains("同梱CLIが見つかりません", text.Text, StringComparison.Ordinal);
                Assert.Contains("解析・削除を開始できません", text.Text, StringComparison.Ordinal);
                window.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF initialization did not finish.");
        Assert.Null(failure);
    }
}
