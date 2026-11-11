using IODirectory = System.IO.Directory;
using IOPath = System.IO.Path;
using PivotPic.Models;
using PivotPic.ViewModels;
using PivotPic.Services;

// 服务层行为验证：占位符展开 + 自触发抑制 + 配置往返。
// 不依赖 UI，可脱离主程序单独运行。

Console.OutputEncoding = System.Text.Encoding.UTF8;

var total = 0;
var passed = 0;

void Check(string name, string actual, string expected)
{
    total++;
    if (actual == expected)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        Console.WriteLine($"  FAIL  {name}");
        Console.WriteLine($"        期望: {expected}");
        Console.WriteLine($"        实际: {actual}");
    }
}

void Section(string title) => Console.WriteLine($"\n=== {title} ===");

// ---------------------------------------------------------------- 占位符
Section("路径占位符展开（基准 2026-11-04 09:15:32）");

var tmpl = new PathTemplateService();
var basis = new DateTime(2026, 11, 4, 9, 15, 32);

Check("{mm-dd}", tmpl.Resolve(@"D:\图库\{mm-dd}", basis), @"D:\图库\11-04");
Check("{yyyy}-{mm}-{dd}", tmpl.Resolve(@"D:\图库\{yyyy}-{mm}-{dd}", basis), @"D:\图库\2026-11-04");
Check("{yyyyMMdd}", tmpl.Resolve(@"D:\图库\{yyyyMMdd}", basis), @"D:\图库\20261104");
Check("{yyyy-MM-dd_HHmmss}", tmpl.Resolve(@"D:\图库\{yyyy-MM-dd_HHmmss}", basis), @"D:\图库\2026-11-04_091532");
Check("{m}/{d}", tmpl.Resolve(@"D:\图库\{m}/{d}", basis), @"D:\图库\11/4");
Check("{mm}", tmpl.Resolve(@"D:\图库\{mm}", basis), @"D:\图库\11");
Check("{mi}", tmpl.Resolve(@"D:\图库\{mi}", basis), @"D:\图库\15");
Check("{HH}-{mi}-{ss}", tmpl.Resolve(@"D:\图库\{HH}-{mi}-{ss}", basis), @"D:\图库\09-15-32");
Check("{yyyy}-{mm}-{dd}_{HH}{mi}{ss}", tmpl.Resolve(@"D:\图库\{yyyy}-{mm}-{dd}_{HH}{mi}{ss}", basis), @"D:\图库\2026-11-04_091532");
Check("{xyz} 原样保留", tmpl.Resolve(@"D:\图库\{xyz}", basis), @"D:\图库\{xyz}");
Check("{mm-xyz} 不部分替换", tmpl.Resolve(@"D:\图库\{mm-xyz}", basis), @"D:\图库\{mm-xyz}");
Check("混合已识别与未识别", tmpl.Resolve(@"D:\图库\{mm-dd}\{未知}", basis), @"D:\图库\11-04\{未知}");
Check("大小写不敏感 {MM}/{DD}", tmpl.Resolve(@"D:\图库\{MM}-{DD}", basis), @"D:\图库\11-04");
Check("空模板", tmpl.Resolve(string.Empty, basis), string.Empty);

Section("未识别占位符检测");
var unknown = tmpl.FindUnknownPlaceholders(@"D:\{mm-dd}\{xyz}\{yyyyMMdd}\{mm-xyz}");
Check("未识别数量", unknown.Count.ToString(), "2");
Check("未识别内容", string.Join(",", unknown), "{xyz},{mm-xyz}");

// ---------------------------------------------------------------- 通配占位符
Section("通配占位符 %...%（HasWildcard）");

Check("有效通配 %mm-dd%", tmpl.HasWildcard(@"A:/图/%mm-dd%/喷绘").ToString(), "True");
Check("花括号不算通配", tmpl.HasWildcard(@"A:/图/{mm-dd}/喷绘").ToString(), "False");
Check("环境变量不算通配 %USERPROFILE%", tmpl.HasWildcard(@"%USERPROFILE%\图").ToString(), "False");
Check("非法 token 不算通配 %xyz%", tmpl.HasWildcard(@"A:/图/%xyz%").ToString(), "False");
Check("大小写等价 %MM-DD%", tmpl.HasWildcard(@"A:/图/%MM-DD%").ToString(), "True");
Check("多段通配", tmpl.HasWildcard(@"A:/%yyyy%/%mm-dd%").ToString(), "True");

// 造一棵真实的目录树用于展开测试
var wcRoot = IOPath.Combine(IOPath.GetTempPath(), "pivotpic_wildcard_" + Guid.NewGuid().ToString("N").Substring(0, 6));
var wcToday = new DateTime(2026, 11, 4);
var todayText = wcToday.ToString("MM-dd");

foreach (var dir in new[]
{
    IOPath.Combine(wcRoot, "11-05", "喷绘"),
    IOPath.Combine(wcRoot, "11-06", "喷绘"),
    IOPath.Combine(wcRoot, "11-07", "喷绘"),
    IOPath.Combine(wcRoot, "abc", "喷绘"),
    IOPath.Combine(wcRoot, "2026", "11-05"),
    IOPath.Combine(wcRoot, "2026", "11-06"),
    IOPath.Combine(wcRoot, "notadate")
})
{
    IODirectory.CreateDirectory(dir);
}

Section("通配占位符展开（ResolveMany）");

var single = tmpl.ResolveMany(IOPath.Combine(wcRoot, "11-05", "喷绘"), wcToday);
Check("无通配返回单元素", single.Count.ToString(), "1");
Check("无通配等于 Resolve", single[0], tmpl.Resolve(IOPath.Combine(wcRoot, "11-05", "喷绘"), wcToday));

var dates = tmpl.ResolveMany(IOPath.Combine(wcRoot, "%mm-dd%", "喷绘"), wcToday);
Check("匹配 3 个日期目录（abc 被忽略）", dates.Count.ToString(), "3");
Check("第一个是 11-05", IOPath.GetFileName(IOPath.GetDirectoryName(dates[0])), "11-05");
Check("最后一个是 11-07", IOPath.GetFileName(IOPath.GetDirectoryName(dates[2])), "11-07");

var nested = tmpl.ResolveMany(IOPath.Combine(wcRoot, "%yyyy%", "%mm-dd%"), wcToday);
Check("两层通配展开", nested.Count.ToString(), "2");

var mixed = tmpl.ResolveMany(IOPath.Combine(wcRoot, "%mm-dd%", "{mm-dd}"), wcToday);
Check("混合模板：{} 展开成今天（无该目录则为 0）", mixed.Count.ToString(), "0");

IODirectory.CreateDirectory(IOPath.Combine(wcRoot, "11-06", todayText));
var mixed2 = tmpl.ResolveMany(IOPath.Combine(wcRoot, "%mm-dd%", "{mm-dd}"), wcToday);
Check("混合模板命中今天目录", mixed2.Count.ToString(), "1");

Check("目录不存在返回空", tmpl.ResolveMany(IOPath.Combine(wcRoot, "不存在%mm-dd%"), wcToday).Count.ToString(), "0");
Check("相对路径不做展开", tmpl.ResolveMany(@"%mm-dd%\喷绘", wcToday).Count.ToString(), "0");

try
{
    IODirectory.Delete(wcRoot, recursive: true);
}
catch (Exception)
{
    // 清理失败不影响测试结论。
}

// ---------------------------------------------------------------- 模型默认值
Section("配置默认值");
var cfg = new AppConfig();
Check("包含子文件夹默认开启", cfg.Folders.Count == 0 ? "true" : "n/a", "true");
var folder = new WatchedFolder();
Check("IncludeSubdirectories 默认", folder.IncludeSubdirectories.ToString(), "True");
Check("ResolutionCheckEnabled 默认", folder.ResolutionCheckEnabled.ToString(), "False");
Check("Threshold 默认", folder.Threshold.ToString(), "300");
Check("Metric 默认", folder.Metric.ToString(), "LongEdge");
Check("RotationMode 默认", cfg.RotationMode.ToString(), "LibVips");
Check("RotateDirection 默认", cfg.RotateDirection.ToString(), "Clockwise");
Check("AutoStart 默认", cfg.AutoStart.ToString(), "False");
Check("MaxPixels 默认", cfg.MaxPixels.ToString(), "300000000");

// ---------------------------------------------------------------- 自触发抑制
Section("自触发抑制 IgnoreSet");
var ignore = new IgnoreSet();
var probePath = IOPath.Combine(IOPath.GetTempPath(), "pivotpic_ignore_probe.jpg");
ignore.Remember(probePath);
Check("写入后立即命中", ignore.Consume(probePath).ToString(), "True");
Check("再次命中已被移除", ignore.Consume(probePath).ToString(), "False");
Check("未登记路径不命中", ignore.Consume(probePath + ".other").ToString(), "False");
Check("大小写不敏感", RunCaseInsensitive(ignore), "True");

static string RunCaseInsensitive(IIgnoreSet set)
{
    var p = IOPath.Combine(IOPath.GetTempPath(), "PivotPic_Case.JPG");
    set.Remember(p);
    return set.Consume(p.ToLowerInvariant()).ToString();
}


// ---------------------------------------------------------------- 路径编辑
Section("卡片路径编辑（占位符手写）");

var tmplForEdit = AppServices.PathTemplate;
if (tmplForEdit is null)
{
    AppServices.PathTemplate = tmpl;
    tmplForEdit = tmpl;
}

var folderModel = new WatchedFolder { Id = "edit-test", Path = @"D:/图库/原始" };
var card = new FolderItemViewModel(folderModel);

Check("初始草稿为空", (card.EditingPath ?? string.Empty).Length.ToString(), "0");

card.BeginEdit();
Check("BeginEdit 装载当前路径", card.EditingPath, @"D:/图库/原始");

// 手写带占位符的模板，预览应立刻展开（用当天日期断言，避免隔天失效）
var today = DateTime.Now;
card.EditingPath = @"D:/图库/{mm-dd}";
Check("预览跟随输入展开占位符", card.EditingPreviewLine,
    $@"预览：D:/图库/{today:MM}-{today:dd}");
Check("组合占位符不误报", card.HasEditingUnknown.ToString(), "False");

card.EditingPath = @"D:/图库/{xyz}";
Check("未识别占位符被标记", card.HasEditingUnknown.ToString(), "True");

// 重新装载草稿应丢弃未保存的输入
card.BeginEdit();
Check("重新装载丢弃未保存草稿", card.EditingPath, @"D:/图库/原始");
Check("丢弃草稿不改原路径", folderModel.Path, @"D:/图库/原始");

// 提交应写回模型
card.BeginEdit();
card.EditingPath = @"D:/图库/{yyyy}-{mm}-{dd}";
var committed = card.CommitEdit();
Check("CommitEdit 报告已变更", committed.ToString(), "True");
Check("CommitEdit 写回模型", folderModel.Path, @"D:/图库/{yyyy}-{mm}-{dd}");
Check("提交后草稿同步为已保存值", card.EditingPath, @"D:/图库/{yyyy}-{mm}-{dd}");
Check("提交后解析行更新",
    card.ResolvedLine.Contains($"{today:yyyy}-{today:MM}-{today:dd}").ToString(), "True");

// 未改动时提交不应报告变更
card.BeginEdit();
var again = card.CommitEdit();
Check("未改动不触发重挂", again.ToString(), "False");

// 空路径视为取消
card.BeginEdit();
card.EditingPath = "   ";
var emptyCommit = card.CommitEdit();
Check("空路径不写入", emptyCommit.ToString(), "False");
Check("空路径保留原值", folderModel.Path, @"D:/图库/{yyyy}-{mm}-{dd}");

// ---------------------------------------------------------------- 待挂载重试上限
// 开机自启时映射网络盘尚未就绪，MonitorService 会把目录登记为"待重试"并按 30 秒回探。
// ShouldKeepMountRetrying 是这条重试链的唯一闸门（纯函数），实现语义：
//     attempt > 0 && attempt <= MaxMountRetries
// 即第 1 次~第 MaxMountRetries 次继续重试，超限或非法值（0/负数）一律停止。
Section("网络盘待挂载重试上限（ShouldKeepMountRetrying）");

const int maxMountRetries = MonitorService.MaxMountRetries;

// 常量本身即 SLA：20 次 × 30 秒 ≈ 10 分钟，与日志文案"已停止自动重试"的预期一致。
Check("重试上限常量为 20 次", maxMountRetries.ToString(), "20");

var mountRetryCases = new (int Attempt, bool ShouldKeep)[]
{
    (1, true),                       // 首次安排：必须继续
    (2, true),                       // 第二次：仍在预算内
    (maxMountRetries - 1, true),     // 预算边界内最后一格
    (maxMountRetries, true),         // 预算用完的最后一次（第 20 次仍要安排）
    (maxMountRetries + 1, false),    // 超限一格：立即停止，防止无限重试
    (0, false),                      // 非法值：从未开始过，不该安排
    (-1, false),                     // 非法值：负数
    (int.MinValue, false),           // 非法值：下界
    (int.MaxValue, false),           // 非法值：上界（防止溢出误判）
};

foreach (var (attempt, shouldKeep) in mountRetryCases)
{
    Check($"attempt={attempt} → {(shouldKeep ? "继续重试" : "停止重试")}",
        MonitorService.ShouldKeepMountRetrying(attempt).ToString(),
        shouldKeep.ToString());
}

// ---------------------------------------------------------------- JPEG 兼容模式按目录
// 用户明确要求：这是「每个监控路径的独立设置」，不是一个全局开关。
// 这里验证三件事：默认开启、目录之间互不影响、设置页绑定（FolderItemViewModel）写回的是**该目录自己的**模型。
Section("JPEG 兼容模式按目录生效（ConvertProgressiveJpeg）");

var dirA = new WatchedFolder { Path = "A:////目录A" };
var dirB = new WatchedFolder { Path = "A:////目录B" };

Check("新目录默认开启", dirA.ConvertProgressiveJpeg.ToString(), "True");

dirB.ConvertProgressiveJpeg = false;
Check("目录B关闭生效", dirB.ConvertProgressiveJpeg.ToString(), "False");
Check("目录A不受影响", dirA.ConvertProgressiveJpeg.ToString(), "True");

var vm = new FolderItemViewModel(dirA);
vm.ConvertProgressiveJpeg = false;
Check("VM 关闭写回模型", dirA.ConvertProgressiveJpeg.ToString(), "False");
Check("VM 操作不影响其他目录", dirB.ConvertProgressiveJpeg.ToString(), "False");

vm.ConvertProgressiveJpeg = true;
Check("VM 重新开启写回模型", dirA.ConvertProgressiveJpeg.ToString(), "True");

// ---------------------------------------------------------------- 汇总
Console.WriteLine();
Console.WriteLine(new string('-', 52));
Console.WriteLine($"合计 {total} 项，通过 {passed} 项，失败 {total - passed} 项");
Console.WriteLine(new string('-', 52));
return total == passed ? 0 : 1;
