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
                TaskStatus.Queued => new SolidColorBrush(Color.FromRgb(112, 112, 136)),
                TaskStatus.Parsing => new SolidColorBrush(Color.FromRgb(0, 240, 255)),
                TaskStatus.Downloading => new SolidColorBrush(Color.FromRgb(0, 240, 255)),
                TaskStatus.Paused => new SolidColorBrush(Color.FromRgb(255, 208, 0)),
                TaskStatus.Merging => new SolidColorBrush(Color.FromRgb(180, 0, 255)),
                TaskStatus.Completed => new SolidColorBrush(Color.FromRgb(0, 255, 106)),
                TaskStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 0, 64)),
                TaskStatus.Cancelled => new SolidColorBrush(Color.FromRgb(112, 112, 136)),
                _ => new SolidColorBrush(Color.FromRgb(112, 112, 136))
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
                SegmentStatus.Pending => new SolidColorBrush(Color.FromRgb(26, 26, 46)),
                SegmentStatus.Downloading => new SolidColorBrush(Color.FromRgb(0, 240, 255)),
                SegmentStatus.Completed => new SolidColorBrush(Color.FromRgb(0, 255, 106)),
                SegmentStatus.Failed => new SolidColorBrush(Color.FromRgb(255, 0, 64)),
                SegmentStatus.Retrying => new SolidColorBrush(Color.FromRgb(255, 208, 0)),
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
