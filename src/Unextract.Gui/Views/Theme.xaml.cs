using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Unextract.Gui.Views;

// Loaded by each window itself (also without an Application instance, as in the view tests).
public partial class Theme : ResourceDictionary
{
    public Theme() => InitializeComponent();
}

// Visible for true, a non-empty string or any other non-null value; Invert flips it.
public sealed class VisibleWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length != 0,
            int i => i != 0,
            _ => true,
        };
        return visible != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// The viewport height of a scrolling pane minus the vertical margin of its content: 10 + 10 (details), 14 (dialog top).
public sealed class MarginlessHeight : IValueConverter
{
    public static readonly MarginlessHeight Instance = new() { Margin = 20 };
    public static readonly MarginlessHeight Top = new() { Margin = 14 };

    public double Margin { get; init; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double height && height > Margin ? height - Margin : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Visible when the list is shown (first value true) but has no items (second value 0).
public sealed class EmptyListNote : IMultiValueConverter
{
    public static readonly EmptyListNote Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [true, 0] ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Archive rows and Target rows of the work list; Archive and Target details.

public sealed class ByItemTypeSelector : DataTemplateSelector
{
    public DataTemplate? Archive { get; set; }
    public DataTemplate? Target { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        ViewModels.ArchiveViewModel => Archive,
        ViewModels.TargetViewModel => Target,
        _ => null,
    };
}
