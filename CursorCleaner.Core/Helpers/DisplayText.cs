using CursorCleaner.Models;

namespace CursorCleaner.Helpers;

public static class DisplayText
{
    public static string Category(DataCategory category) => category switch
    {
        DataCategory.SQLite => "SQLite 数据库",
        DataCategory.Workspace => "工作区",
        DataCategory.AgentTranscript => "代理记录",
        DataCategory.ChatSession => "历史会话",
        DataCategory.Other => "其他",
        _ => category.ToString()
    };

    public static string FormatSessionSource(SessionSource source) => source switch
    {
        SessionSource.File => "文件",
        SessionSource.Database => "数据库",
        SessionSource.Both => "文件+库",
        _ => source.ToString()
    };

    public static string Theme(CleanerTheme theme) => theme switch
    {
        CleanerTheme.System => "跟随系统",
        CleanerTheme.Light => "浅色",
        CleanerTheme.Dark => "深色",
        _ => theme.ToString()
    };

    public static string LocalTime(DateTime utc) =>
        utc.ToUniversalTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string RecycleBinSettingLabel => OperatingSystem.IsMacOS()
        ? "使用废纸篓"
        : "使用 Windows 回收站";

    public static string RecycleBinModeLabel => OperatingSystem.IsMacOS() ? "废纸篓" : "回收站";

    public static string ScanRoamingLabel => OperatingSystem.IsMacOS()
        ? "Application Support 数据"
        : "Roaming 数据";

    public static string ScanLocalLabel => OperatingSystem.IsMacOS()
        ? "Caches 数据"
        : "Local 数据";

    public static string ScanUserProfileLabel => "用户目录 .cursor";

    public static string Recommendation(DataCategory category) => category switch
    {
        DataCategory.ChatSession or DataCategory.AgentTranscript => "可按会话删除",
        DataCategory.Workspace => "可按工作区或保留期清理",
        DataCategory.SQLite => "仅高级工具可维护，不作为普通文件删除",
        DataCategory.Other => "不纳入普通清理",
        _ => string.Empty
    };

    public static string WorkspaceRecommendation(bool projectMissing) =>
        projectMissing ? "项目路径已不存在，可考虑清理" : "建议保留";
}
