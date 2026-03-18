using System.Collections.Concurrent;
using System.IO;

namespace ChineseChess.Infrastructure.AI.Search;

/// <summary>
/// ProbCut 回歸資料收集器的生產實作。
/// 使用 ConcurrentBag 支援 Lazy SMP 多 worker 並發寫入（無鎖）。
///
/// CSV Schema（供 Python 回歸分析使用）：
///   ShallowScore,DeepScore,BetaUsed,Depth,Ply,DepthPair,Phase,CaptureClass
///
/// 使用範例：
///   var collector = new ProbCutDataCollector();
///   worker.DataCollectionMode = true;
///   worker.Search(7);
///   collector.ExportCsv(File.CreateText("probcut_data.csv"));
/// </summary>
public sealed class ProbCutDataCollector : IProbCutDataCollector
{
    private readonly ConcurrentBag<ProbCutSample> samples = new();

    public int SampleCount => samples.Count;

    public void RecordSample(ProbCutSample sample) => samples.Add(sample);

    public void ExportCsv(TextWriter writer)
    {
        writer.WriteLine("ShallowScore,DeepScore,BetaUsed,Depth,Ply,DepthPair,Phase,CaptureClass");

        foreach (var s in samples)
        {
            writer.WriteLine(
                $"{s.ShallowScore},{s.DeepScore},{s.BetaUsed},{s.Depth},{s.Ply}," +
                $"{s.DepthPair},{s.Phase},{s.CaptureClass}");
        }
    }
}

/// <summary>
/// ProbCut 資料收集器的空實作（Null Object Pattern）。
/// Production 預設使用此實作——所有方法皆空操作，無任何效能影響。
/// </summary>
public sealed class NullProbCutDataCollector : IProbCutDataCollector
{
    public static readonly NullProbCutDataCollector Instance = new();

    private NullProbCutDataCollector() { }

    public int SampleCount => 0;

    public void RecordSample(ProbCutSample sample) { }

    public void ExportCsv(TextWriter writer) { }
}
