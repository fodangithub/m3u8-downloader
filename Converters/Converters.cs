using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using M3U8Downloader.Models;

namespace M3U8Downloader.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TaskStatus status)
        {
            return status switch
            {
                TaskStatus.Queued => new SolidColorBrush(Color.FromRgb(158, 158, 158)),
                TaskStatus.Parsing => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                TaskStatus.Downloading => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                TaskStatus.Paused => new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                TaskStatus.Merging => new SolidColorBrush(Color.FromRgb(156, 39, 176)),
                TaskStatus.Completed => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                TaskStatus.Failed => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                TaskStatus.Cancelled => new SolidColorBrush(Color.FromRgb(158, 158, 158)),
                _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TaskStatus status)
        {
            return status switch
            {
                TaskStatus.Queued => "Queued",
                TaskStatus.Parsing => "Parsing",
                TaskStatus.Downloading => "Downloading",
                TaskStatus.Paused => "Paused",
                TaskStatus.Merging => "Merging",
                TaskStatus.Completed => "Completed",
                TaskStatus.Failed => "Failed",
                TaskStatus.Cancelled => "Cancelled",
                _ => "Unknown"
            };
        }
        return "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SegmentStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SegmentStatus status)
        {
            return status switch
            {
                SegmentStatus.Pending => new SolidColorBrush(Color.FromRgb(224, 224, 224)),
                SegmentStatus.Downloading => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                SegmentStatus.Completed => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                SegmentStatus.Failed => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                SegmentStatus.Retrying => new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        var inverse = parameter?.ToString() == "inverse";
        if (inverse) boolValue = !boolValue;
        return boolValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        return boolValue ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
