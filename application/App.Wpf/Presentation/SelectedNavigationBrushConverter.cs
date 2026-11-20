using System.Globalization;
using System.Windows.Data;
using System.Windows;
using System.Windows.Media;

namespace DownloadManager.App.Presentation;

public sealed class SelectedNavigationBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true) return Brushes.Transparent;
        return Application.Current.TryFindResource("App.AccentSubtle") as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
