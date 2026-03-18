using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.IO;
using System.Threading;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// ProbCut 回歸資料收集測試。
///
/// 驗證 DataCollectionMode 啟用/關閉時的行為差異：
/// - 關閉時：零開銷，不收集樣本
/// - 啟用時：記錄 (ShallowScore, DeepScore, BetaUsed, ...) 樣本
/// - 關鍵正確性：開/關狀態下最佳著法必須相同
///
/// CSV 格式：ShallowScore,DeepScore,BetaUsed,Depth,Ply,DepthPair,Phase,CaptureClass
/// </summary>
public class ProbCutDataCollectionTests
{
    // 標準開局（有足夠深度讓 ProbCut 觸發）
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // 有吃子機會的局面（ProbCut 需要吃子著法才能觸發）
    private const string TacticalFen = "r1bakabnr/4n4/1c5c1/p1p1R1p1p/9/9/P1P1P1P1P/1C5C1/9/1NBAKABNR w - - 0 1";

    // ─── 測試 1：關閉時樣本數為 0 ─────────────────────────────────────────

    [Fact]
    public void DataCollectionMode_Off_ZeroSamples()
    {
        var board = new Board(TacticalFen);
        var collector = new ProbCutDataCollector();
        var worker = CreateWorker(board, collector);

        worker.DataCollectionMode = false;
        worker.Search(7);

        Assert.Equal(0, collector.SampleCount);
    }

    // ─── 測試 2：開啟後有樣本收集 ─────────────────────────────────────────

    [Fact]
    public void DataCollectionMode_On_SamplesCollectedAfterSearch()
    {
        // 需要有吃子機會的局面，且深度夠深（depth=7 >= ProbCutMinDepth=5）
        var board = new Board(TacticalFen);
        var collector = new ProbCutDataCollector();
        var worker = CreateWorker(board, collector);

        worker.DataCollectionMode = true;
        worker.Search(7);

        Assert.True(collector.SampleCount > 0,
            $"DataCollectionMode=true 應收集樣本，但 SampleCount={collector.SampleCount}");
    }

    // ─── 測試 3：關鍵正確性：開/關最佳著法相同 ───────────────────────────

    [Fact]
    public void DataCollectionMode_On_SameBestMoveAsOff()
    {
        var board1 = new Board(TacticalFen);
        var board2 = new Board(TacticalFen);

        var collectorOn = new ProbCutDataCollector();
        var collectorOff = new ProbCutDataCollector();

        var workerOn = CreateWorker(board1, collectorOn);
        var workerOff = CreateWorker(board2, collectorOff);

        workerOn.DataCollectionMode = true;
        workerOff.DataCollectionMode = false;

        var resultOn = workerOn.Search(5);
        var resultOff = workerOff.Search(5);

        Assert.Equal(resultOff.BestMove, resultOn.BestMove);
        Assert.NotEqual(default, resultOn.BestMove);
    }

    // ─── 測試 4：開啟後搜尋不拋例外 ──────────────────────────────────────

    [Fact]
    public void DataCollectionMode_On_SearchCompletes()
    {
        var board = new Board(InitialFen);
        var collector = new ProbCutDataCollector();
        var worker = CreateWorker(board, collector);

        worker.DataCollectionMode = true;

        // 不應拋出任何例外
        var result = worker.Search(6);
        Assert.NotEqual(default, result.BestMove);
    }

    // ─── 測試 5：ExportCsv 含標頭 ────────────────────────────────────────

    [Fact]
    public void ExportCsv_ContainsHeader()
    {
        var board = new Board(TacticalFen);
        var collector = new ProbCutDataCollector();
        var worker = CreateWorker(board, collector);

        worker.DataCollectionMode = true;
        worker.Search(5);

        using var sw = new StringWriter();
        collector.ExportCsv(sw);
        var csv = sw.ToString();

        // CSV 第一行應含欄位名稱
        Assert.StartsWith("ShallowScore,DeepScore,BetaUsed,Depth,Ply,DepthPair,Phase,CaptureClass", csv);
    }

    // ─── 測試 6：CSV 樣本欄位正確 ────────────────────────────────────────

    [Fact]
    public void ExportCsv_RowMatchesSampleFields()
    {
        var board = new Board(TacticalFen);
        var collector = new ProbCutDataCollector();
        var worker = CreateWorker(board, collector);

        worker.DataCollectionMode = true;
        worker.Search(6);

        // 確保有樣本才繼續
        if (collector.SampleCount == 0) return;

        using var sw = new StringWriter();
        collector.ExportCsv(sw);
        var lines = sw.ToString().Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        // 至少有標頭行 + 一個樣本
        Assert.True(lines.Length >= 2, "CSV 應至少有標頭和一行樣本");

        // 每行應有 8 個欄位（以逗號分隔）
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            Assert.Equal(8, parts.Length);
        }
    }

    // ─── 測試 7：NullCollector 是空操作 ──────────────────────────────────

    [Fact]
    public void NullCollector_RecordSample_IsNoOp()
    {
        var nullCollector = NullProbCutDataCollector.Instance;

        // 不應拋出任何例外
        nullCollector.RecordSample(new ProbCutSample(
            ShallowScore: 100, DeepScore: 200, BetaUsed: 300,
            Depth: 6, Ply: 2,
            DepthPair: ProbCutDepthPair.D6_1,
            Phase: ProbCutPhase.Midgame,
            CaptureClass: ProbCutCaptureClass.RookCapture));

        Assert.Equal(0, nullCollector.SampleCount);

        using var sw = new StringWriter();
        nullCollector.ExportCsv(sw);
        Assert.Equal(string.Empty, sw.ToString());
    }

    // ─── 測試 8：ProbCutSample record struct 相等語義 ─────────────────────

    [Fact]
    public void ProbCutSample_RecordEquality()
    {
        var s1 = new ProbCutSample(
            ShallowScore: -45, DeepScore: 120, BetaUsed: 380,
            Depth: 6, Ply: 3,
            DepthPair: ProbCutDepthPair.D6_1,
            Phase: ProbCutPhase.Midgame,
            CaptureClass: ProbCutCaptureClass.RookCapture);

        var s2 = new ProbCutSample(
            ShallowScore: -45, DeepScore: 120, BetaUsed: 380,
            Depth: 6, Ply: 3,
            DepthPair: ProbCutDepthPair.D6_1,
            Phase: ProbCutPhase.Midgame,
            CaptureClass: ProbCutCaptureClass.RookCapture);

        var s3 = new ProbCutSample(
            ShallowScore: 0, DeepScore: 0, BetaUsed: 0,
            Depth: 0, Ply: 0,
            DepthPair: ProbCutDepthPair.D5_0,
            Phase: ProbCutPhase.Opening,
            CaptureClass: ProbCutCaptureClass.PawnCapture);

        Assert.Equal(s1, s2);
        Assert.NotEqual(s1, s3);
    }

    // ─── Helper ──────────────────────────────────────────────────────────

    private static SearchWorker CreateWorker(IBoard board, IProbCutDataCollector collector)
    {
        var worker = new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            new TranspositionTable(sizeMb: 4),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true),
            collector);
        worker.ProbCutEnabled = true;
        return worker;
    }
}
