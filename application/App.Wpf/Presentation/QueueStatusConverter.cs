using System.Globalization;
using System.Windows.Data;

namespace DownloadManager.App.Presentation;

public sealed class QueueStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is string name && values[1] is IReadOnlyDictionary<string, string> statuses &&
        statuses.TryGetValue(name, out var status) ? status : "Idle";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
