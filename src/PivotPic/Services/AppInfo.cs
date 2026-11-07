using System;
using System.Reflection;

namespace PivotPic.Services;

/// <summary>程序级元信息。版本号由构建脚本注入程序集，界面统一从这里取，避免多处写死。</summary>
internal static class AppInfo
{
    /// <summary>形如 v1.2.5。</summary>
    public static string VersionText { get; } = BuildVersionText();

    /// <summary>形如 1.2.5，用于标题栏或安装包命名。</summary>
    public static string Version { get; } = BuildVersion();

    private static Version? ReadVersion() => Assembly.GetExecutingAssembly().GetName().Version;

    private static string BuildVersionText()
    {
        var version = ReadVersion();
        return version is null ? "v1.2.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string BuildVersion()
    {
        var version = ReadVersion();
        return version is null ? "1.2.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
