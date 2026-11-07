using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PivotPic.Setup;

public partial class MainWindow : Window
{
    private readonly bool _uninstallMode;

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();

        _uninstallMode = uninstallMode;
        BrandVersion.Text = $"版本 {Installer.VersionText}";
        LoadBrandLogo();

        if (uninstallMode)
        {
            ShowUninstall();
        }
        else
        {
            // 固定按「所有用户」安装（Program Files + HKLM），不再让用户选。
            PathBox.Text = Installer.GetInstalledDir() ?? Installer.DefaultAllUsersDir;
            ChkAutoStart.IsChecked = true;
            ChkDesktop.IsChecked = true;
        }
    }

    /// <summary>从嵌入资源加载品牌图，保证安装程序与软件本体用的是同一个 logo。</summary>
    private void LoadBrandLogo()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.logo.png");
            if (stream is null)
            {
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            BrandLogo.Source = image;
        }
        catch (Exception)
        {
            // 资源异常时留空即可，不影响安装。
        }
    }

    private void ShowUninstall()
    {
        PageInstall.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Collapsed;
        PageDone.Visibility = Visibility.Collapsed;
        PageUninstall.Visibility = Visibility.Visible;

        var dir = Installer.GetInstalledDir();
        UninstallPathText.Text = string.IsNullOrEmpty(dir)
            ? "未检测到已安装的目录，仍可继续清理快捷方式与注册表项。"
            : $"安装目录：{dir}";
    }

    /// <summary>整个窗口可拖动；点在输入框/按钮/复选框上时不触发，避免抢走交互。</summary>
    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractive(DependencyObject source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.TextBox
                or System.Windows.Controls.Primitives.ToggleButton
                or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择安装位置",
            InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : Installer.DefaultAllUsersDir
        };

        if (dialog.ShowDialog(this) == true)
        {
            var selected = dialog.FolderName;
            PathBox.Text = string.Equals(
                Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar)),
                Installer.AppName,
                StringComparison.OrdinalIgnoreCase)
                ? selected
                : Path.Combine(selected, Installer.AppName);
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var target = (PathBox.Text ?? string.Empty).Trim();

        if (target.Length == 0)
        {
            ShowError("请填写安装位置。");
            return;
        }

        try
        {
            target = Path.GetFullPath(target);
        }
        catch (Exception)
        {
            ShowError("安装位置不是合法路径。");
            return;
        }

        // 固定按所有用户安装：写 HKLM 与 Program Files，需要管理员权限。
        const bool allUsers = true;

        ErrorText.Visibility = Visibility.Collapsed;
        BtnInstall.IsEnabled = false;
        BtnClose.IsEnabled = false;

        PageInstall.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;

        var options = new InstallOptions(
            target,
            ChkDesktop.IsChecked == true,
            ChkAutoStart.IsChecked == true,
            allUsers);

        // 所有用户安装需要提权：用管理员身份重启自身执行静默安装。
        // 正常发布版带 requireAdministrator 清单，走不到这里；万一被降权运行，至少给出明确提示。
        if (allUsers && !Installer.IsElevated())
        {
            StatusText.Text = "正在请求管理员权限…";
            DetailText.Text = "请在系统弹窗中允许后继续";
            if (RunElevatedInstall(options))
            {
                ShowDone(target);
            }
            else
            {
                BackToOptions();
                ShowError("安装已取消，或管理员权限被拒绝。");
            }

            return;
        }

        try
        {
            await Task.Run(() => Installer.Install(options, Report));
            ShowDone(target);
        }
        catch (Exception ex)
        {
            BackToOptions();
            ShowError("安装失败：" + ex.Message);
        }
    }

    private bool RunElevatedInstall(InstallOptions options)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
            {
                return false;
            }

            var args = $"--silent-install --dir \"{options.TargetDir}\" "
                       + $"--desktop {(options.DesktopShortcut ? 1 : 0)} "
                       + $"--autostart {(options.AutoStart ? 1 : 0)} --allusers 1";

            var process = Process.Start(new ProcessStartInfo(self, args)
            {
                Verb = "runas",
                UseShellExecute = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            // 用户点了「否」会抛异常，按取消处理。
            return false;
        }
    }

    private void BackToOptions()
    {
        PageProgress.Visibility = Visibility.Collapsed;
        PageInstall.Visibility = Visibility.Visible;
        BtnInstall.IsEnabled = true;
        BtnClose.IsEnabled = true;
    }

    private void ShowDone(string target)
    {
        PageProgress.Visibility = Visibility.Collapsed;
        PageDone.Visibility = Visibility.Visible;
        DoneText.Text = $"已安装到：{target}\n可从桌面或开始菜单启动 PivotPic。";
        ChkRunNow.IsChecked = true;
    }

    private void Report(InstallProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = progress.Message;
            DetailText.Text = progress.Detail ?? string.Empty;
            PercentText.Text = $"{(int)Math.Round(progress.Percent * 100)}%";

            var track = BarFill.Parent as FrameworkElement;
            var width = track?.ActualWidth ?? 0;
            BarFill.Width = Math.Max(0, width * Math.Clamp(progress.Percent, 0, 1));
        });
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (ChkRunNow.IsChecked == true)
        {
            var exe = Path.Combine((PathBox.Text ?? string.Empty).Trim(), "PivotPic.exe");
            if (File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exe)
                    });
                }
                catch (Exception)
                {
                    // 启动失败不阻塞关闭安装程序。
                }
            }
        }

        Close();
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        var dir = Installer.GetInstalledDir() ?? Installer.DefaultAllUsersDir;

        BtnUninstall.IsEnabled = false;
        PageUninstall.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;
        StatusText.Text = "正在卸载…";
        DetailText.Text = string.Empty;

        try
        {
            await Task.Run(() => Installer.Uninstall(dir, Report));
        }
        catch (Exception)
        {
            // 卸载尽力而为，最后统一关闭。
        }

        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
