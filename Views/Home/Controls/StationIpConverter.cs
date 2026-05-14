using System.Globalization;
using System.Windows.Data;

namespace AutomaticOnlineHostComputer.Views.Home.Controls;

public sealed class StationIpConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyDictionary<string, string> map)
            return "未配置IP";

        var code = parameter?.ToString();
        if (string.IsNullOrWhiteSpace(code))
            return "未配置IP";

        return map.TryGetValue(code, out var ip) && !string.IsNullOrWhiteSpace(ip)
            ? ip
            : "未配置IP";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}