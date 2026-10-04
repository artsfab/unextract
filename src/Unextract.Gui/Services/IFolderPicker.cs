using System.Windows;

namespace Unextract.Gui.Services;

internal interface IFolderPicker
{
    string? SelectFolder(Window owner, string? initialDirectory);
}
