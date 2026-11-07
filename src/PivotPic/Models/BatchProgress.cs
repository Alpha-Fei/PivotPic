namespace PivotPic.Models;

/// <summary>
/// 一批处理任务的进度快照。
/// <para>
/// 口径：<see cref="Total"/> 是这一轮需要处理的横版图片数（竖版/正方形已在入队前过滤，
/// 不计入分子分母），<see cref="Processed"/> 是已处理完的，<see cref="Rotated"/> 是真正被旋转的。
/// 进度条按 Processed/Total 走，反映的是"这批需要处理的图片处理到哪了"。
/// </para>
/// </summary>
public sealed class BatchProgress
{
    /// <summary>本轮已处理完的图片数。</summary>
    public int Processed { get; init; }

    /// <summary>本轮需要处理的横版图片总数（竖版/正方形不计入）。</summary>
    public int Total { get; init; }

    /// <summary>本轮真正被旋转的文件数。</summary>
    public int Rotated { get; init; }

    /// <summary>当前正在处理（或刚处理完）的文件名。</summary>
    public string? CurrentFile { get; init; }

    /// <summary>本批是否已结束。</summary>
    public bool Completed { get; init; }

    /// <summary>完成比例，0–1。</summary>
    public double Ratio => Total <= 0 ? 0 : Math.Clamp((double)Processed / Total, 0, 1);
}
