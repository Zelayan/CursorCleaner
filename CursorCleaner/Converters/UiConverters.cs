using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CursorCleaner.Helpers;
using CursorCleaner.Models;
using CursorCleaner.ViewModels;

namespace CursorCleaner.Converters;

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? ByteSizeFormatter.Format(bytes) : "0.0 B";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int page && int.TryParse(parameter?.ToString(), out var expected) && page == expected ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class WorkspaceRecommendationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "项目路径已不存在，检查后可清理" : "项目路径有效，建议保留";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CategoryRecommendationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DataCategory.SQLite => "数据库文件，仅通过高级工具维护",
        DataCategory.Workspace or DataCategory.ChatSession or DataCategory.AgentTranscript => "可按策略生成预览",
        _ => "检查路径后决定"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DataCategoryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DataCategory.SQLite => "SQLite 数据库",
        DataCategory.Workspace => "工作区",
        DataCategory.AgentTranscript => "代理记录",
        DataCategory.ChatSession => "历史会话",
        DataCategory.Other => "其他",
        _ => value?.ToString() ?? string.Empty
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ThemeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CleanerTheme.System => "跟随系统",
        CleanerTheme.Light => "浅色",
        CleanerTheme.Dark => "深色",
        _ => value?.ToString() ?? string.Empty
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime utc ? utc.ToUniversalTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture) : string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SessionRoleBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is string role && (role.Equals("user", StringComparison.OrdinalIgnoreCase) || role.Equals("human", StringComparison.OrdinalIgnoreCase))
            ? "AccentBrush"
            : "MutedTextBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? SystemColors.WindowTextBrush;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

