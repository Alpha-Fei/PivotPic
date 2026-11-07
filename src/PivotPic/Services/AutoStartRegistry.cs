using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace PivotPic.Services;

/// <summary>
/// 开机自启的读写。
/// <para>
/// 安装程序勾选「为所有用户安装」时会把自启写到 HKLM，当前用户安装则写 HKCU。
/// 因此读取必须两个位置都看，写入要跟随已有条目所在的 hive，避免出现
/// 「安装时开了、设置里却显示关闭」的不一致。
/// </para>
/// </summary>
internal static class AutoStartRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PivotPic";
    private const string ElevateSwitch = "--autostart-machine=";

    /// <summary>自启项所在的注册表范围。</summary>
    public enum Scope
    {
        None,
        User,
        Machine
    }

    /// <summary>当前进程是否已提权。</summary>
    public static bool IsElevated
    {
        get
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
    }

    /// <summary>探测自启项实际写在哪：先看当前用户，再看所有用户。</summary>
    public static Scope Probe()
    {
        if (HasValue(Registry.CurrentUser))
        {
            return Scope.User;
        }

        return HasValue(Registry.LocalMachine) ? Scope.Machine : Scope.None;
    }

    /// <summary>是否已开启（任一范围存在即视为开启）。</summary>
    public static bool IsEnabled => Probe() != Scope.None;

    /// <summary>
    /// 开启或关闭自启。
    /// 关闭位于 HKLM 的条目且当前未提权时，会自动以管理员身份重启自身完成删除。
    /// </summary>
    /// <returns>操作是否真正生效。</returns>
    public static bool Set(bool enabled)
    {
        if (enabled)
        {
            // 机器范围已有条目就直接沿用，并清掉可能残留的用户级重复项。
            if (Probe() == Scope.Machine)
            {
                DeleteValue(Registry.CurrentUser);
                return true;
            }

            WriteValue(Registry.CurrentUser);
            return true;
        }

        DeleteValue(Registry.CurrentUser);

        if (Probe() != Scope.Machine)
        {
            return true;
        }

        if (IsElevated)
        {
            DeleteValue(Registry.LocalMachine);
            return Probe() != Scope.Machine;
        }

        return RunElevated(false);
    }

    /// <summary>供提权后的子进程调用：只处理 HKLM 条目。</summary>
    public static bool SetMachineAutoStart(bool enabled)
    {
        if (enabled)
        {
            WriteValue(Registry.LocalMachine);
            return true;
        }

        DeleteValue(Registry.LocalMachine);
        return !HasValue(Registry.LocalMachine);
    }

    /// <summary>解析 --autostart-machine=on|off 参数。</summary>
    public static bool TryParseElevateSwitch(string argument, out bool enable)
    {
        enable = false;

        if (!argument.StartsWith(ElevateSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        enable = argument.EndsWith("on", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool HasValue(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteValue(RegistryKey root)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("无法确定当前程序路径。");
        }

        using var key = root.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{executable}\" --tray", RegistryValueKind.String);
    }

    private static void DeleteValue(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // 无权限或键不存在时忽略，由调用方通过 Probe 复核结果。
        }
    }

    /// <summary>以管理员身份重启自身，只做注册表操作。</summary>
    private static bool RunElevated(bool enable)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
            {
                return false;
            }

            var process = Process.Start(new ProcessStartInfo(self, ElevateSwitch + (enable ? "on" : "off"))
            {
                Verb = "runas",
                UseShellExecute = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15000);
            return Probe() != Scope.Machine;
        }
        catch (Exception)
        {
            // 用户取消 UAC 时按失败处理。
            return false;
        }
    }
}
