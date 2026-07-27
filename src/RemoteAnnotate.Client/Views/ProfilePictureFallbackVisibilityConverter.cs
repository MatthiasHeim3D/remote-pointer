using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RemoteAnnotate.Client.Views;

public sealed class ProfilePictureFallbackVisibilityConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        var hasConfiguredPicture = value switch
        {
            string path => !string.IsNullOrWhiteSpace(path),
            byte[] bytes => bytes.Length > 0,
            _ => false,
        };
        return hasConfiguredPicture ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
