using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using PivotPic.Models;
using WinForms = System.Windows.Forms;

namespace PivotPic.Services;

/// <summary>
/// 托盘驻留。图标由矢量几何在运行时渲染（见 <see cref="Interop.TrayIconFactory"/>），
/// 菜单为自绘深色扁平样式，不出现任何位图资源。
/// </summary>
public sealed class TrayService : ITrayService
{
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _menu;
    private WinForms.ToolStripMenuItem? _toggleItem;
    private WinForms.ToolStripMenuItem? _updateItem;
    private TrayState _state = TrayState.Idle;
    private int _alertCount;
    private bool _disposed;

    /// <summary>
    /// 点击托盘里的「发现新版本」时要执行的动作，由主程序在启动时注入。
    /// 用静态回调而不是直接引用主窗口，托盘服务就不会反向依赖 UI 层。
    /// </summary>
    public static Action? UpdateEntryInvoked { get; set; }

    /// <inheritdoc />
    public void Initialize()
    {
        BuildMenu();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = BuildTooltip(),
            Icon = Interop.TrayIconFactory.Create(_state),
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += (_, _) => InvokeShell(null);
        _notifyIcon.MouseClick += OnMouseClick;
    }

    /// <inheritdoc />
    public void ShowUpdateBadge(string? versionText)
    {
        if (_disposed || _updateItem is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(versionText))
        {
            _updateItem.Visible = false;
            return;
        }

        _updateItem.Text = $"有更新 {versionText}";
        _updateItem.Visible = true;
    }

    private void OnMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            // 单击只提示状态，双击才开窗，避免误触打断当前工作。
            _notifyIcon?.ShowBalloonTip(1200, "正画 PivotPic", BuildTooltip(), WinForms.ToolTipIcon.None);
        }
    }

    /// <inheritdoc />
    public void UpdateState(TrayState state, int alertCount)
    {
        if (_disposed)
        {
            return;
        }

        _state = state;
        _alertCount = alertCount;

        if (_notifyIcon is null)
        {
            return;
        }

        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = Interop.TrayIconFactory.Create(state);
        _notifyIcon.Text = BuildTooltip();
        old?.Dispose();

        if (_toggleItem is not null)
        {
            RefreshToggleItemText();
        }
    }

    /// <summary>
    /// 按当前配置重算「暂停监控 / 恢复监控」文案。
    /// 现状所有改动 <c>MonitoringEnabled</c> 的路径都会调用 <see cref="UpdateState"/>，功能上本已正确。
    /// 在菜单弹出时再算一次属于防御性加固：正确性不再依赖「每个新增调用方都记得通知托盘」
    /// 这个隐式契约，避免将来新增入口漏调导致文案停在旧状态（不报错、难察觉）。
    /// </summary>
    private void RefreshToggleItemText()
    {
        if (_toggleItem is null)
        {
            return;
        }

        _toggleItem.Text = IsMonitoring() ? "暂停监控" : "恢复监控";
    }

    /// <inheritdoc />
    public void ShowBalloon(string title, string message)
    {
        _notifyIcon?.ShowBalloonTip(3000, title, message, WinForms.ToolTipIcon.None);
    }

    private static bool IsMonitoring()
    {
        return AppServices.Config?.Current.MonitoringEnabled ?? false;
    }

    private string BuildTooltip()
    {
        var stateText = _state switch
        {
            TrayState.Monitoring => "监控中",
            TrayState.Alert => "有待处理告警",
            _ => "已暂停"
        };

        var baseText = $"正画 PivotPic · {stateText}";
        var withCount = _alertCount > 0 ? $"{baseText}（{_alertCount} 条告警）" : baseText;

        // NotifyIcon.Text 上限 127 字符，超出会抛异常。
        return withCount.Length > 120 ? withCount[..120] : withCount;
    }

    private void BuildMenu()
    {
        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new WinForms.Padding(6, 6, 6, 6),
            Font = new Font("Microsoft YaHei UI", 9f),
            BackColor = Color.FromArgb(0x1C, 0x1F, 0x26),
            ForeColor = Color.FromArgb(0xF2, 0xF3, 0xF5)
        };

        var open = CreateItem("打开主界面", () => InvokeShell(null));
        _toggleItem = CreateItem(IsMonitoring() ? "暂停监控" : "恢复监控", ToggleMonitoring);

        // 有更新时才显示；文案由 ShowUpdateBadge 动态写入。
        _updateItem = CreateItem("有更新", OpenUpdateEntry);
        _updateItem.Visible = false;

        var settings = CreateItem("设置", () => InvokeShell("settings"));
        var about = CreateItem("关于", () => InvokeShell("about"));
        var exit = CreateItem("退出", ExitApp);

        _menu.Items.Add(open);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(settings);
        _menu.Items.Add(about);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exit);

        _menu.Opening += (_, _) =>
        {
            // 弹出前重算文案：当前所有改 MonitoringEnabled 的入口都会通知托盘，功能上已正确；
            // 这里再算一遍是把「正确性」从调用方约定收回到托盘自己，将来新增开关入口漏调也不会显示旧文案。
            RefreshToggleItemText();
            ApplyRoundedRegion();
        };
    }

    private static WinForms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            ForeColor = Color.FromArgb(0xF2, 0xF3, 0xF5),
            Padding = new WinForms.Padding(8, 4, 8, 4)
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>把菜单裁成圆角矩形，避免 Windows 原生方角破坏几何调性。</summary>
    private void ApplyRoundedRegion()
    {
        if (_menu is null || _menu.Width <= 0)
        {
            return;
        }

        var hRegion = CreateRoundRectRgn(0, 0, _menu.Width + 1, _menu.Height + 1, 12, 12);
        if (hRegion == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _menu.Region = Region.FromHrgn(hRegion);
        }
        finally
        {
            DeleteObject(hRegion);
        }
    }

    private void ToggleMonitoring()
    {
        var config = AppServices.Config;
        if (config is null)
        {
            return;
        }

        var next = !config.Current.MonitoringEnabled;
        config.Current.MonitoringEnabled = next;
        config.SaveDebounced();

        if (next)
        {
            AppServices.Monitor?.StartAll();
            UpdateState(TrayState.Monitoring, _alertCount);
        }
        else
        {
            AppServices.Monitor?.StopAll();
            UpdateState(TrayState.Idle, _alertCount);
        }

        AppServices.Log?.Info("Tray", next ? "从托盘恢复监控" : "从托盘暂停监控");
        (Application.Current as App)?.RefreshShellState();
    }

    /// <summary>点击托盘里的「有更新」：把主窗口唤到前台并打开更新详情。</summary>
    private static void OpenUpdateEntry()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateEntryInvoked?.Invoke();
            }
            catch (Exception)
            {
                // 更新入口打不开不能连累托盘可用性。
            }
        });
    }

    private static void InvokeShell(string? page)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(() =>
        {
            app.ShowShell();
            if (page is not null)
            {
                App.Shell?.NavigateTo(page);
            }
        });
    }

    private static void ExitApp()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            App.Shell?.RequestExit();
        });
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            var icon = _notifyIcon.Icon;
            _notifyIcon.Dispose();
            _notifyIcon = null;
            icon?.Dispose();
        }

        _menu?.Dispose();
        _menu = null;
    }

    /// <summary>深色扁平菜单渲染器，去掉所有系统渐变与分隔线阴影。</summary>
    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            // 圆角由 Region 裁切负责，这里不画边框。
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);

            using var path = RoundedRect(rect, 8);
            using var brush = new SolidBrush(Color.FromArgb(0x2A, 0x55, 0x46, 0xE8));
            g.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            using var pen = new Pen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            var y = e.Item.Height / 2;
            g.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var d = radius * 2;

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>深色配色表，覆盖系统默认的蓝白渐变。</summary>
    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x1C, 0x1F, 0x26);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.FromArgb(0x2A, 0x55, 0x46, 0xE8);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x2A, 0x55, 0x46, 0xE8);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x2A, 0x55, 0x46, 0xE8);
        public override Color SeparatorDark => Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        public override Color SeparatorLight => Color.Transparent;
    }
}
