using System.Globalization;

namespace CursorCleaner.Helpers;

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, int decimalPlaces = 1)
    {
        if (decimalPlaces < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces));
        }

        double value = Math.Abs((double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (bytes < 0)
        {
            value = -value;
        }

        return $"{value.ToString("F" + decimalPlaces, CultureInfo.CurrentCulture)} {Units[unit]}";
    }
}
