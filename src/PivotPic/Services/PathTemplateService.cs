using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PivotPic.Services;

/// <summary>
/// 路径时间占位符展开器。
/// <para>
/// 约定：花括号内允许由 token 与分隔符自由组合，例如 {mm-dd}、{yyyyMMdd}、{yyyy-MM-dd_HHmmss}。
/// 花括号内的字母片段按「长优先」贪婪匹配 token 表（yyyy &gt; yy &gt; mm &gt; dd &gt; hh &gt; mi &gt; ss &gt; m &gt; d &gt; h），
/// 非字母字符（- _ . 空格 / \ : 等）原样保留为分隔符。
/// </para>
/// <para>
/// 只有在整对花括号被完整解析时才展开；只要残留任何无法识别的字母片段，整个 {xxx} 原样保留。
/// </para>
/// <para>
/// <b>mm 语义（写死，不接受推断扩展）</b>：同一花括号内出现过 hh/HH 之后再出现的 mm 视为「分钟」；
/// 未出现小时字段时 mm 恒为「月」。跨花括号不传递该状态。紧凑时间写法请用 {HH}{mi}{ss}。
/// </para>
/// </summary>
public sealed class PathTemplateService : IPathTemplateService
{
    /// <summary>
    /// token 表，顺序即优先级：长 token 在短 token 之前，保证 yyyy 不会被 yy 截断、
    /// mi 不会被 m 截断、mm 不会被 m 截断。
    /// </summary>
    private static readonly string[] TokenTable =
    {
        "yyyy", "yy", "mm", "dd", "hh", "mi", "ss", "m", "d", "h"
    };

    /// <summary>单次通配展开允许枚举的最大目录数，防止 <c>%yyyy%\\%mm-dd%</c> 之类把磁盘扫穿。</summary>
    private const int MaxWildcardMatches = 500;

    /// <summary>任意一对百分号，捕获内部串（不允许嵌套）。</summary>
    private static readonly Regex EnvironmentTokenRegex = new(
        @"%([^%]+)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>任意一对百分号，捕获内部串（不允许嵌套）。</summary>
    private static readonly Regex WildcardRegex = new(
        @"%([^%]*)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>任意一对花括号，捕获内部串（不允许嵌套花括号）。</summary>
    private static readonly Regex BraceRegex = new(
        @"\{([^{}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 只展开**系统里真实存在**的环境变量。
    /// <para>
    /// 不能用 <see cref="Environment.ExpandEnvironmentVariables"/>：它遇到 <c>%mm-dd%</c>
    /// 这种「长得像环境变量」的日期通配会当成未知变量处理掉，通配就永远匹配不到。
    /// </para>
    /// </summary>
    private static string ExpandKnownEnvironmentVariables(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('%'))
        {
            return text;
        }

        return EnvironmentTokenRegex.Replace(text, match =>
        {
            var value = Environment.GetEnvironmentVariable(match.Groups[1].Value);
            return value ?? match.Value;
        });
    }

    /// <summary>展开模板：先展开环境变量，再按「花括号内分词」规则替换时间占位符。</summary>
    /// <param name="template">路径模板，例如 "%USERPROFILE%\Pictures\{mm-dd}"。</param>
    /// <param name="now">基准时间，为 null 时取 <see cref="DateTime.Now"/>。</param>
    public string Resolve(string template, DateTime? now = null)
    {
        // 空模板不参与展开，直接返回空串，避免调用方拿到奇怪的相对路径。
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        // 顺序很重要：环境变量先展开，其展开结果里的占位符仍会被后续替换处理。
        var expanded = ExpandKnownEnvironmentVariables(template);
        var moment = now ?? DateTime.Now;

        return BraceRegex.Replace(expanded, match =>
        {
            var inner = match.Groups[1].Value;

            // 解析失败（含残留字母片段或空内容）时整对花括号原样保留，绝不部分替换。
            if (!TryParseInner(inner, out var pieces) || !ContainsToken(pieces))
            {
                return match.Value;
            }

            return Render(pieces, moment);
        });
    }

    /// <summary>列出模板中所有未识别的占位符原始写法（含花括号），去重且保持出现顺序。</summary>
    public IReadOnlyList<string> FindUnknownPlaceholders(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return Array.Empty<string>();
        }

        // 与 Resolve 保持一致：先展开环境变量，避免 %USERPROFILE% 之类的写法被误判。
        var expanded = ExpandKnownEnvironmentVariables(template);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in BraceRegex.Matches(expanded))
        {
            var raw = match.Value;
            var inner = match.Groups[1].Value;

            // 按与 Resolve 完全相同的分词规则试解析：解析成功（且含至少一个 token）就算已识别。
            if (TryParseInner(inner, out var pieces) && ContainsToken(pieces))
            {
                continue;
            }

            // 去重时忽略大小写，但对外输出保留用户原始写法。
            if (seen.Add(raw))
            {
                result.Add(raw);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public bool HasWildcard(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return false;
        }

        // 先展开环境变量：%USERPROFILE% 这类在展开后就不存在了，自然不会误判成通配。
        var expanded = ExpandKnownEnvironmentVariables(template);

        foreach (Match match in WildcardRegex.Matches(expanded))
        {
            if (TryBuildInnerPattern(match.Groups[1].Value, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ResolveMany(string template, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return Array.Empty<string>();
        }

        var expanded = ExpandKnownEnvironmentVariables(template);
        var moment = now ?? DateTime.Now;

        // 无通配：与 Resolve 完全等价（单元素），保持旧行为不变。
        if (!HasWildcard(expanded))
        {
            return new[] { Resolve(expanded, moment) };
        }

        // 用户可能粘正斜杠路径（A:/测试/%MM-DD%），统一成正斜杠前的反斜杠再按层级拆。
        var normalized = expanded.Replace('/', '\\');

        var segments = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

        // 根：盘符（C:）或 UNC（\\server\share）。
        var startIndex = 0;
        string current;

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (segments.Length < 2)
            {
                return Array.Empty<string>();
            }

            current = @"\\" + segments[0] + @"\" + segments[1];
            startIndex = 2;
        }
        else if (segments.Length > 0 && segments[0].EndsWith(":", StringComparison.Ordinal))
        {
            current = segments[0] + @"\";
            startIndex = 1;
        }
        else
        {
            // 相对路径不做通配展开：无法可靠定位层级。
            return Array.Empty<string>();
        }

        var results = new List<string> { current };

        for (var i = startIndex; i < segments.Length; i++)
        {
            var segment = segments[i];
            var next = new List<string>();

            if (TryBuildWildcardPattern(segment, out var pattern))
            {
                var regex = new Regex("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                foreach (var prefix in results)
                {
                    foreach (var directory in SafeEnumerateDirectories(prefix))
                    {
                        if (!regex.IsMatch(Path.GetFileName(directory.TrimEnd('\\'))))
                        {
                            continue;
                        }

                        next.Add(directory);

                        if (next.Count >= MaxWildcardMatches)
                        {
                            break;
                        }
                    }

                    if (next.Count >= MaxWildcardMatches)
                    {
                        break;
                    }
                }

                if (next.Count >= MaxWildcardMatches)
                {
                    AppServices.Log?.Warn("扫描",
                        $"通配展开超过 {MaxWildcardMatches} 个目录，已截断：{expanded}");
                }
            }
            else
            {
                // 普通段：{...} 仍按原有规则展开成今天的日期。
                var literal = Resolve(segment, moment);

                foreach (var prefix in results)
                {
                    var combined = Path.Combine(prefix, literal);
                    if (Directory.Exists(combined))
                    {
                        next.Add(combined);
                    }
                }
            }

            if (next.Count == 0)
            {
                return Array.Empty<string>();
            }

            results = next;
        }

        return results;
    }

    /// <summary>
    /// 把一个路径段转成正则：其中的 <c>%...%</c> 换成「按 token 的数字模式」，其余字符按字面量转义。
    /// 返回 false 表示这段里没有**有效**的 <c>%...%</c>（例如 %USERPROFILE%）。
    /// </summary>
    private static bool TryBuildWildcardPattern(string segment, out string pattern)
    {
        pattern = string.Empty;

        var matches = WildcardRegex.Matches(segment);
        if (matches.Count == 0)
        {
            return false;
        }

        var builder = new StringBuilder(segment.Length * 2);
        var cursor = 0;
        var valid = false;

        foreach (Match match in matches)
        {
            if (!TryBuildInnerPattern(match.Groups[1].Value, out var inner))
            {
                // 不是日期通配（比如环境变量残留），按字面量处理。
                continue;
            }

            builder.Append(Regex.Escape(segment.Substring(cursor, match.Index - cursor)));
            builder.Append(inner);

            cursor = match.Index + match.Length;
            valid = true;
        }

        if (!valid)
        {
            return false;
        }

        builder.Append(Regex.Escape(segment.Substring(cursor)));
        pattern = builder.ToString();
        return true;
    }

    /// <summary>把 <c>%...%</c> 的**内部串**转成正则（不含 % 本身）。</summary>
    private static bool TryBuildInnerPattern(string inner, out string pattern)
    {
        pattern = string.Empty;

        if (!TryParseInner(inner, out var pieces) || !ContainsToken(pieces))
        {
            return false;
        }

        var builder = new StringBuilder(inner.Length * 2);

        foreach (var piece in pieces)
        {
            builder.Append(piece.Token is null ? Regex.Escape(piece.Literal) : TokenPattern(piece.Token));
        }

        pattern = builder.ToString();
        return true;
    }

    /// <summary>token → 正则片段。两位 token 严格两位，单字母 token 允许 1–2 位（11-6 与 11-06 都能匹配）。</summary>
    private static string TokenPattern(string token) => token.ToLowerInvariant() switch
    {
        "yyyy" => @"\d{4}",
        "yy" => @"\d{2}",
        "mm" => @"\d{2}",
        "dd" => @"\d{2}",
        "hh" => @"\d{2}",
        "mi" => @"\d{2}",
        "ss" => @"\d{2}",
        "m" => @"\d{1,2}",
        "d" => @"\d{1,2}",
        "h" => @"\d{1,2}",
        _ => @"\d+"
    };

    /// <summary>枚举子目录，吞掉权限/网络错误，避免一个坏目录打断整次展开。</summary>
    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>把花括号内部串切成「token 片段 / 字面量片段」，任一字母片段解析失败即整体失败。</summary>
    private static bool TryParseInner(string inner, out List<Piece> pieces)
    {
        pieces = new List<Piece>(inner.Length);
        var index = 0;

        while (index < inner.Length)
        {
            if (!IsAsciiLetter(inner[index]))
            {
                // 收集连续的非字母字符作为一段字面量（分隔符），原样拼回结果。
                var start = index;
                while (index < inner.Length && !IsAsciiLetter(inner[index]))
                {
                    index++;
                }

                pieces.Add(new Piece(token: null, literal: inner.Substring(start, index - start)));
                continue;
            }

            // 收集连续的字母，形成一段「待分词」的字母片段。
            var runStart = index;
            while (index < inner.Length && IsAsciiLetter(inner[index]))
            {
                index++;
            }

            var run = inner.Substring(runStart, index - runStart);
            if (!TryTokenizeRun(run, pieces))
            {
                // 该字母片段无法被 token 表完整消费，整对花括号判为未识别。
                pieces.Clear();
                return false;
            }
        }

        return true;
    }

    /// <summary>把一段纯字母按 token 表长优先分词，必须完全消费，否则返回 false。</summary>
    private static bool TryTokenizeRun(string run, List<Piece> pieces)
    {
        var cursor = 0;

        while (cursor < run.Length)
        {
            string? matched = null;

            foreach (var token in TokenTable)
            {
                // 长度不够时直接跳过，避免越过 run 边界。
                if (cursor + token.Length > run.Length)
                {
                    continue;
                }

                if (string.Compare(run, cursor, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    matched = token;
                    break;
                }
            }

            if (matched is null)
            {
                return false;
            }

            pieces.Add(new Piece(token: matched, literal: string.Empty));
            cursor += matched.Length;
        }

        return true;
    }

    /// <summary>渲染已解析的片段；{mm} 是否按分钟解释取决于同一对花括号内此前是否出现过 {hh}。</summary>
    private static string Render(List<Piece> pieces, DateTime moment)
    {
        var builder = new StringBuilder(pieces.Count * 4);

        // 时间上下文标记：只有出现过 hh（小时）token 后，mm 才改按分钟解释。
        var hourSeen = false;

        foreach (var piece in pieces)
        {
            if (piece.Token is null)
            {
                builder.Append(piece.Literal);
                continue;
            }

            switch (piece.Token.ToLowerInvariant())
            {
                case "yyyy":
                    builder.Append(moment.ToString("yyyy", CultureInfo.InvariantCulture));
                    break;
                case "yy":
                    builder.Append(moment.ToString("yy", CultureInfo.InvariantCulture));
                    break;
                case "mm":
                    // 既定约定：默认 {mm} 是「月」；已出现小时 token 的紧凑时间写法里按分钟处理。
                    builder.Append(moment.ToString(hourSeen ? "mm" : "MM", CultureInfo.InvariantCulture));
                    break;
                case "dd":
                    builder.Append(moment.ToString("dd", CultureInfo.InvariantCulture));
                    break;
                case "hh":
                    // 24 小时制，避免上午/下午两段时间落到同一个目录。
                    builder.Append(moment.ToString("HH", CultureInfo.InvariantCulture));
                    hourSeen = true;
                    break;
                case "mi":
                    builder.Append(moment.ToString("mm", CultureInfo.InvariantCulture));
                    break;
                case "ss":
                    builder.Append(moment.ToString("ss", CultureInfo.InvariantCulture));
                    break;
                case "m":
                    builder.Append(moment.Month.ToString(CultureInfo.InvariantCulture));
                    break;
                case "d":
                    builder.Append(moment.Day.ToString(CultureInfo.InvariantCulture));
                    break;
                case "h":
                    builder.Append(moment.Hour.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    // token 表已封闭，理论上不可达；万一走到这里也保留原样。
                    builder.Append('{').Append(piece.Token).Append('}');
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 智能识别路径中的日期片段并替换为时间占位符，未识别到就原样返回。
    /// <para>例如 <c>C:\图库\11-06\喷绘</c> → <c>C:\图库\{mm}-{dd}\喷绘</c>。</para>
    /// <para>「添加目录」与「目录设置」两个弹窗共用这一份实现，避免规则各写一遍走偏。</para>
    /// </summary>
    public static string ConvertDatesToPlaceholders(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        var result = path;

        // 优先匹配长格式，避免被短格式误伤
        result = Regex.Replace(
            result, @"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)",
            m => IsValidMonthDay(m.Groups[2].Value, m.Groups[3].Value) ? "{yyyy}-{mm}-{dd}" : m.Value);

        result = Regex.Replace(
            result, @"(?<!\d)(\d{4})/(\d{2})/(\d{2})(?!\d)",
            m => IsValidMonthDay(m.Groups[2].Value, m.Groups[3].Value) ? "{yyyy}/{mm}/{dd}" : m.Value);

        result = Regex.Replace(
            result, @"(?<!\d)(\d{2})-(\d{2})(?!\d)",
            m => IsValidMonthDay(m.Groups[1].Value, m.Groups[2].Value) ? "{mm}-{dd}" : m.Value);

        result = Regex.Replace(
            result, @"(?<!\d)(\d{2})\.(\d{2})(?!\d)",
            m => IsValidMonthDay(m.Groups[1].Value, m.Groups[2].Value) ? "{mm}.{dd}" : m.Value);

        result = Regex.Replace(
            result, @"(?<!\d)(\d{4})(\d{2})(\d{2})(?!\d)",
            m => IsValidMonthDay(m.Groups[2].Value, m.Groups[3].Value) ? "{yyyymmdd}" : m.Value);

        return result;
    }

    /// <summary>粗略校验月/日合法（01-12 / 01-31），避免把 99-99 之类误判为日期。</summary>
    private static bool IsValidMonthDay(string month, string day)
    {
        return int.TryParse(month, out var m) && m is >= 1 and <= 12
            && int.TryParse(day, out var d) && d is >= 1 and <= 31;
    }

    /// <summary>判断片段列表里是否至少含一个 token（纯分隔符或空内容不构成有效占位符）。</summary>
    private static bool ContainsToken(List<Piece> pieces)
    {
        foreach (var piece in pieces)
        {
            if (piece.Token is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>只把 ASCII 字母当作待分词字符；中文等其它字符一律走字面量分支，从而自然落入「未识别」。</summary>
    private static bool IsAsciiLetter(char value)
    {
        return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
    }

    /// <summary>一个已解析片段：要么是 token（Token 非空），要么是字面量（Literal 非空）。</summary>
    private readonly struct Piece
    {
        public Piece(string? token, string literal)
        {
            Token = token;
            Literal = literal;
        }

        /// <summary>归一化前的小写 token 名，字面量片段为 null。</summary>
        public string? Token { get; }

        /// <summary>字面量内容，token 片段为空串。</summary>
        public string Literal { get; }
    }
}
