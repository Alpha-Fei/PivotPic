using System;
using System.Windows;
using System.Windows.Controls;
using PivotPic.Services;

namespace PivotPic.Views;

/// <summary>关于页：品牌展示 + 版本号与更新入口。</summary>
public partial class AboutPage : UserControl
{
    /// <summary>构造页面。</summary>
    public AboutPage()
    {
        InitializeComponent();

        RefreshUpdateBadge();

        // 后台检查完成的时间不确定，订阅状态变化即可，不必自己轮询。
        if (AppServices.UpdateCoordinator is not null)
        {
            AppServices.UpdateCoordinator.StateChanged += OnUpdateStateChanged;
        }
    }

    /// <summary>版本号统一从 AppInfo 取，构建时注入程序集。</summary>
    public string VersionText => AppInfo.VersionText;

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshUpdateBadge));
            return;
        }

        RefreshUpdateBadge();
    }

    /// <summary>只有确有新版本时才露出徽标，避免版本号旁边常年挂个无意义的小标签。</summary>
    private void RefreshUpdateBadge()
    {
        var hasUpdate = AppServices.UpdateCoordinator?.HasAvailableUpdate == true;
        BtnUpdateBadge.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUpdateBadgeClick(object sender, RoutedEventArgs e)
    {
        App.Shell?.OpenUpdateEntry();
    }

    /// <summary>打开赞助链接。</summary>
    private void OnSponsorClick(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", "https://afdian.com/a/muzic");
        }
        catch
        {
            // 忽略浏览器打开失败
        }
    }
}
