using System.Windows;
using Microsoft.Win32;

namespace Unextract.Gui.Services;

internal sealed class WindowsFolderPicker : IFolderPicker
{
    public string? SelectFolder(Window owner, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ZIPを検索するディレクトリを選択",
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}
