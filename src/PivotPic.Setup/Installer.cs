using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using PivotPic.Shared;

namespace PivotPic.Setup;

/// <summary>安装选项。</summary>
internal sealed record InstallOptions(
    string TargetDir,
    bool DesktopShortcut,
    bool AutoStart,
    bool AllUsers);

/// <summary>
/// 一次安装进度：主文案 + 可选明细（当前文件 / 计数）+ 0–1 百分比。
/// 百分比由真实工作量驱动（解压阶段按已释放字节数），不是定时器凑出来的。
/// </summary>
internal readonly record struct InstallProgress(string Message, string? Detail, double Percent);

/// <summary>
/// 安装/卸载的全部副作用集中在这里，界面只管调用与展示进度。
/// 支持当前用户安装（HKCU + LocalAppData，无需提权）与所有用户安装（Program Files + HKLM，需提权）。
/// </summary>
internal static class Installer
{
    internal const string AppName = "PivotPic";
    internal const string AppNameCn = "正画";
    internal const string AppId = "PivotPic";
    internal const string Publisher = "鹏飞广告·饺子";
    private const string RunValueName = "PivotPic";

    /// <summary>开始菜单里的卸载入口名字。</summary>
    private const string UninstallShortcutName = "卸载 " + AppName;

    /// <summary>默认装到当前用户目录，无需提权且可写。</summary>
    internal static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppName);

    /// <summary>所有用户安装的默认目录。</summary>
    internal static string DefaultAllUsersDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        AppName);

    internal static string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "1.2.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>当前进程是否已提权。</summary>
    internal static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按选项执行安装。</summary>
    internal static void Install(InstallOptions options, Action<InstallProgress> report)
    {
        report(new InstallProgress("正在准备安装目录…", options.TargetDir, 0.02));
        Directory.CreateDirectory(options.TargetDir);
        Settle(140);

        var exePath = Path.Combine(options.TargetDir, "PivotPic.exe");

        // 真实进度：解压阶段按「已释放字节 / 总字节」推进，并把当前文件名带给界面。
        ExtractPayload(exePath, report);

        // 旧版本会把安装包复制到安装目录当卸载器，升级时清掉，避免白白占上百兆。
        RemoveLegacyArtifacts(options.TargetDir);

        // 快捷方式图标直接用 exe 内嵌图标，不再单独释放 ico 文件。
        report(new InstallProgress("正在创建开始菜单快捷方式…", AppName + ".lnk", 0.90));
        Settle(160);
        var startMenu = GetStartMenuDir(options.AllUsers);
        CreateShortcut(Path.Combine(startMenu, AppName + ".lnk"), exePath, options.TargetDir, exePath);

        // 开始菜单里放一个显眼的「卸载」入口，直接指向独立的卸载程序。
        var uninstallerPath = Path.Combine(options.TargetDir, "uninstall.exe");
        CreateShortcut(Path.Combine(startMenu, UninstallShortcutName + ".lnk"), uninstallerPath, options.TargetDir, exePath);

        report(new InstallProgress("正在创建桌面快捷方式…", options.DesktopShortcut ? desktopShortcutName : "已跳过", 0.93));
        Settle(120);
        var desktopLink = Path.Combine(GetDesktopDir(options.AllUsers), AppName + ".lnk");
        if (options.DesktopShortcut)
        {
            CreateShortcut(desktopLink, exePath, options.TargetDir, exePath);
        }
        else
        {
            DeleteFileQuiet(desktopLink);
        }

        report(new InstallProgress("正在配置开机自启…", options.AutoStart ? "写入启动项" : "已关闭", 0.96));
        Settle(120);
        SetAutoStart(options.AutoStart, exePath, options.AllUsers);

        report(new InstallProgress("正在写入卸载信息…", "控制面板 · 程序和功能", 0.99));
        Settle(140);
        WriteUninstallRegistry(options, exePath);

        report(new InstallProgress("安装完成", null, 1.0));
    }

    /// <summary>桌面快捷方式文件名（用于进度明细展示）。</summary>
    private const string desktopShortcutName = AppName + ".lnk";

    /// <summary>
    /// 阶段之间的短暂停留：文件在 SSD 上释放只要几百毫秒，没有这一步进度条会一闪而过，
    /// 用户看不清到底装了什么。停留时间固定在百毫秒级，不会让安装变拖沓。
    /// </summary>
    private static void Settle(int milliseconds) => Thread.Sleep(milliseconds);

    /// <summary>卸载：把当前用户与所有用户两套位置都清理一遍，避免残留。</summary>
    internal static void Uninstall(string installDir, Action<InstallProgress> report)
    {
        try
        {
            UninstallCore(installDir, report);
        }
        catch (Exception ex)
        {
            LogFailure("Uninstall", ex);
            throw;
        }
    }

    private static void UninstallCore(string installDir, Action<InstallProgress> report)
    {
        // 加速组件的善后必须最先做：PAC 快照存在安装目录的 proxy 子目录里，
        // 一旦先删了目录就再也还原不了系统代理。
        report(new InstallProgress("正在还原网络设置…", null, 0.05));
        try
        {
            ProxyCleanup.CleanupAll(installDir);
        }
        catch (Exception ex)
        {
            LogFailure("Uninstall.ProxyCleanup", ex);
        }

        report(new InstallProgress("正在移除快捷方式…", null, 0.15));
        foreach (var allUsers in new[] { false, true })
        {
            DeleteFileQuiet(Path.Combine(GetStartMenuDir(allUsers), AppName + ".lnk"));
            DeleteFileQuiet(Path.Combine(GetStartMenuDir(allUsers), UninstallShortcutName + ".lnk"));
            DeleteFileQuiet(Path.Combine(GetDesktopDir(allUsers), AppName + ".lnk"));
        }

        report(new InstallProgress("正在清理开机自启…", null, 0.35));
        SetAutoStart(false, null, allUsers: false);
        if (IsElevated())
        {
            SetAutoStart(false, null, allUsers: true);
        }

        report(new InstallProgress("正在删除注册表项…", null, 0.5));
        DeleteRegistryTree(false);
        if (IsElevated())
        {
            DeleteRegistryTree(true);
        }

        report(new InstallProgress("正在删除程序文件…", null, 0.7));
        if (Directory.Exists(installDir))
        {
            foreach (var file in Directory.EnumerateFiles(installDir))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, "uninstall.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 卸载器自身正在运行，交给最后的 cmd 清理
                }

                DeleteFileQuiet(file);
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(installDir).Any())
                {
                    Directory.Delete(installDir, recursive: false);
                }
            }
            catch (Exception)
            {
                // 目录被占用时由下面的 cmd 兜底。
            }
        }

        report(new InstallProgress("正在收尾…", null, 1.0));
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self))
        {
            var script = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\" & rmdir /s /q \"{installDir}\"";
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", script)
                {
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath()
                });
            }
            catch (Exception)
            {
                // 清理失败不影响卸载结果。
            }
        }
    }

    /// <summary>解压阶段在总进度条里占的区间：开始 / 结束。</summary>
    private const double ExtractFrom = 0.06;
    private const double ExtractTo = 0.88;

    /// <summary>两次进度上报的最小间隔：太密集会灌爆 UI 线程，太稀疏进度条会跳。</summary>
    private const int ReportIntervalMs = 14;

    /// <summary>
    /// 把内嵌的 app.zip 解压到安装目录：主程序与依赖 DLL 都以普通文件落地，
    /// 不再打成单文件 exe，启动更快、出问题也便于排查。
    /// <para>
    /// 每释放一个文件就按「已释放字节 / 总字节」上报一次真实进度，
    /// 明细里带上当前文件名与 序号/总数，让用户看到到底在装什么。
    /// </para>
    /// </summary>
    private static void ExtractPayload(string exePath, Action<InstallProgress> report)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.app.zip")
            ?? throw new InvalidOperationException("安装包损坏：缺少程序数据。");

        var destination = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("安装路径无效。");

        Directory.CreateDirectory(destination);

        // 旧版本进程可能还占着文件，先结束再覆盖。
        if (File.Exists(exePath))
        {
            try
            {
                File.Delete(exePath);
            }
            catch (IOException)
            {
                KillRunningInstances();
                Thread.Sleep(600);
                File.Delete(exePath);
            }
        }

        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

        // 目录条目（名字以 / 结尾）先剔除，剩下的才是要落地的真实文件。
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var totalBytes = files.Sum(entry => entry.Length);
        var totalCount = files.Count;

        long doneBytes = 0;
        var doneCount = 0;
        var lastReportTick = 0L;

        report(new InstallProgress("正在释放程序文件…", $"共 {totalCount} 个文件", ExtractFrom));

        foreach (var entry in files)
        {
            var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));

            // 防御 zip 路径穿越：解压结果必须仍在安装目录内。
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var folder = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            entry.ExtractToFile(targetPath, overwrite: true);

            doneCount++;
            doneBytes += entry.Length;

            // 进度按字节算：大 DLL 和小图标占的分量不同，比单纯按文件数更接近真实耗时。
            var ratio = totalBytes > 0 ? (double)doneBytes / totalBytes : (double)doneCount / Math.Max(1, totalCount);
            var percent = ExtractFrom + (ExtractTo - ExtractFrom) * Math.Clamp(ratio, 0, 1);

            var now = Environment.TickCount64;
            var isLast = doneCount >= totalCount;

            if (!isLast && now - lastReportTick < ReportIntervalMs)
            {
                continue;
            }

            lastReportTick = now;
            report(new InstallProgress(
                "正在释放程序文件…",
                $"{entry.Name}  ·  {doneCount}/{totalCount}",
                percent));
        }

        // 收尾再报一次，保证进度条停在区间上界，不会因为节流少走一点。
        report(new InstallProgress("程序文件已释放", $"{doneCount} 个文件 · {FormatSize(doneBytes)}", ExtractTo));
    }

    /// <summary>把字节数格式化成 MB/GB，用于进度明细。</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024 / 1024:F2} GB";
        }

        return $"{bytes / 1024d / 1024:F1} MB";
    }

    /// <summary>清理旧版本遗留的冗余文件：安装包副本与独立图标。</summary>
    private static void RemoveLegacyArtifacts(string targetDir)
    {
        // uninstall.exe 现在是安装包释放的正式卸载程序，不能删；只清理历史遗留的图标文件。
        DeleteFileQuiet(Path.Combine(targetDir, "app.ico"));
    }

    /// <summary>用 WScript.Shell 创建 .lnk，免去 COM 互操作引用。</summary>
    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string iconPath,
        string? arguments = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("当前系统不支持创建快捷方式。");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDir;
            link.IconLocation = iconPath;
            link.Description = AppName;

            if (!string.IsNullOrEmpty(arguments))
            {
                link.Arguments = arguments;
            }
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void SetAutoStart(bool enabled, string? exePath, bool allUsers)
    {
        try
        {
            var root = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null)
            {
                return;
            }

            if (enabled && !string.IsNullOrEmpty(exePath))
            {
                key.SetValue(RunValueName, $"\"{exePath}\" --tray", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // 组策略限制或无权限时忽略。
        }
    }

    private static void WriteUninstallRegistry(InstallOptions options, string exePath)
    {
        try
        {
            var root = options.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId);
            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", VersionText, RegistryValueKind.String);
            key.SetValue("Publisher", Publisher, RegistryValueKind.String);
            key.SetValue("InstallLocation", options.TargetDir, RegistryValueKind.String);
            key.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
            // 指向安装目录里的独立卸载程序（由安装包释放）。
            var uninstaller = Path.Combine(options.TargetDir, "uninstall.exe");
            key.SetValue("UninstallString", $"\"{uninstaller}\"", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\"", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", EstimateSizeKb(options.TargetDir), RegistryValueKind.DWord);
        }
        catch (Exception)
        {
            // 注册表写入失败不影响主流程。
        }
    }

    private static void DeleteRegistryTree(bool allUsers)
    {
        try
        {
            var root = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
            root.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 注册表项不存在或无权限时忽略。
        }
    }

    private static int EstimateSizeKb(string directory)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                total += new FileInfo(file).Length;
            }

            return (int)Math.Min(int.MaxValue, total / 1024);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 等待宿主进程（正画的旧实例）彻底退出。
    /// <para>
    /// 在线更新时安装程序由旧版本自己拉起，旧进程此时还占着 PivotPic.exe 与几个原生 DLL。
    /// 不等它退出，解压阶段的 File.Delete / ExtractToFile 会直接抛 IOException。
    /// 超时后仍继续（调用方还有 KillRunningInstances 兜底），绝不因此让安装卡死。
    /// </para>
    /// </summary>
    /// <param name="processId">宿主的进程号；≤0 表示不需要等待。</param>
    internal static void WaitForProcessExit(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(15000))
            {
                LogFailure("WaitForHost", new TimeoutException($"等待宿主进程 {processId} 退出超时，继续安装。"));
                return;
            }

            // 进程退出后句柄回收还有一小段延迟，稍等再解压。
            Thread.Sleep(500);
        }
        catch (ArgumentException)
        {
            // 进程已经不存在，正是我们想要的状态。
        }
        catch (Exception ex)
        {
            LogFailure("WaitForHost", ex);
        }
    }

    private static void KillRunningInstances()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("PivotPic"))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // 进程已退出或无权限时忽略。
        }
    }

    private static void LogFailure(string stage, Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "pivotpic-setup.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {ex}\r\n");
        }
        catch (Exception)
        {
            // 记录失败不影响主流程。
        }
    }

    private static void DeleteFileQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 文件被占用时忽略。
        }
    }

    internal static string GetDesktopDir(bool allUsers) => Environment.GetFolderPath(
        allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory);

    internal static string GetStartMenuDir(bool allUsers)
    {
        var dir = Environment.GetFolderPath(
            allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs);

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception)
        {
            // 无权限时由调用方处理异常。
        }

        return dir;
    }

    /// <summary>已安装时返回安装目录（先查当前用户，再查所有用户）。</summary>
    internal static string? GetInstalledDir()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId);
                if (key?.GetValue("InstallLocation") is string dir && !string.IsNullOrEmpty(dir))
                {
                    return dir;
                }
            }
            catch (Exception)
            {
                // 忽略并继续查下一个根键。
            }
        }

        return null;
    }
}
