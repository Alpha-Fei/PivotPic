using System;
using System.IO;

namespace PivotPic.Services;

/// <summary>
/// 全应用共享的数据目录解析。
/// 优先使用程序所在目录（绿色/便携语义，配置跟着软件走）；
/// 当安装目录不可写（例如装进 Program Files 且未提权）时，自动回退到 %AppData%\PivotPic，
/// 保证任何安装位置都不会因为写配置失败而启动崩溃。
/// </summary>
internal static class AppPaths
{
    private const string LegacyFolderName = "PivotPic";

    /// <summary>配置、日志、告警历史的根目录。</summary>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>config.json 完整路径。</summary>
    public static string ConfigPath => Path.Combine(DataRoot, "config.json");

    /// <summary>日志目录。</summary>
    public static string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>告警历史文件路径。</summary>
    public static string AlertHistoryPath => Path.Combine(DataRoot, "alert-history.json");

    private static string ResolveDataRoot()
    {
        var installDir = AppContext.BaseDirectory;

        if (IsWritable(installDir))
        {
            // 注意：这里刻意不做旧配置迁移。
            // 全新安装必须是干净状态（没有监控路径），旧版本的数据由用户自行重新配置。
            return installDir;
        }

        // 安装目录只读：退回用户配置目录，功能不受影响。
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LegacyFolderName);

        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (Exception)
        {
            // 连配置目录都建不出来时，仍返回该路径，由调用方按失败处理。
        }

        return fallback;
    }

    /// <summary>在目标目录建一个临时文件来判断是否可写。</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            var probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

}
