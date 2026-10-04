using System.Windows;
using Unextract.Gui.Services;
using Unextract.Gui.ViewModels;
using Unextract.Gui.Views;

namespace Unextract.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var data = GuiDataRoot.Resolve(Environment.GetEnvironmentVariable(GuiDataRoot.VariableName));
        if (data.Error is not null)
        {
            MessageBox.Show(data.Error, "unextract GUI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var viewModel = new MainViewModel(new CliLocation(AppContext.BaseDirectory).Check(),
            settings: data.SettingsPath is null ? null : new SearchSettings(data.SettingsPath),
            logs: data.LogDirectory is null ? null : new LogLocation(data.LogDirectory));
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();
    }
}
