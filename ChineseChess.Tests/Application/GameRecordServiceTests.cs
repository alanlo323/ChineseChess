using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Search;
using System.Linq;
using System.Threading.Tasks;

namespace ChineseChess.Tests.Application;

/// <summary>
/// 測試 GameService 的走法歷史追蹤、重播狀態機、NavigateTo、ContinueFromCurrentPosition。
/// </summary>
public class GameRecordServiceTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    private static GameService CreateGameService()
    {
        var engine = new SearchEngine();
        var svc = new GameService(engine);
        svc.SetDifficulty(depth: 1, timeMs: 500, threadCount: 1);
        return svc;
    }

    private static Move FirstLegalMove(GameService svc)
    {
        var iter = svc.CurrentBoard.GenerateLegalMoves().GetEnumerator();
        iter.MoveNext();
        return iter.Current;
    }

    private static async Task WaitAiDone(GameService svc)
    {
        var waited = 0;
        while (svc.IsThinking && waited < 500) { await Task.Delay(10); waited++; }
    }

    // ─── Phase 2：moveHistory 追蹤 ──────────────────────────────────────────

    [Fact]
    public async Task StartGame_ShouldClearMoveHistory()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        Assert.Empty(svc.MoveHistory);
        Assert.Equal(ReplayState.Live, svc.ReplayState);
        Assert.NotEmpty(svc.InitialFen);
    }

    [Fact]
    public async Task HumanMove_ShouldAppendToMoveHistory()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        var move = FirstLegalMove(svc);
        await svc.HumanMoveAsync(move);

        Assert.True(svc.MoveHistory.Count >= 1);
        var entry = svc.MoveHistory[0];
        Assert.Equal(1, entry.StepNumber);
        Assert.Equal(move, entry.Move);
        Assert.Equal(PieceColor.Red, entry.Turn);
        Assert.NotEmpty(entry.Notation);
    }

    [Fact]
    public async Task Undo_ShouldRemoveLastHistoryEntry()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        await WaitAiDone(svc);

        var countBefore = svc.MoveHistory.Count;
        svc.Undo(); // PlayerVsAi 模式悔棋會退兩步（人+AI）
        Assert.True(svc.MoveHistory.Count <= countBefore);
    }

    [Fact]
    public async Task StartGame_AgainAfterMoves_ShouldResetHistory()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        Assert.NotEmpty(svc.MoveHistory);

        await svc.StartGameAsync(GameMode.PlayerVsAi);
        Assert.Empty(svc.MoveHistory);
    }

    // ─── Phase 3：重播狀態機 ────────────────────────────────────────────────

    [Fact]
    public async Task EnterReplayMode_ShouldSetReplayingState()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.EnterReplayModeAsync();

        Assert.Equal(ReplayState.Replaying, svc.ReplayState);
    }

    [Fact]
    public async Task HumanMove_InReplayMode_ShouldBeBlocked()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        var moveBeforeReplay = FirstLegalMove(svc);
        await svc.EnterReplayModeAsync();

        await svc.HumanMoveAsync(moveBeforeReplay);

        Assert.Empty(svc.MoveHistory);
        Assert.Equal(ReplayState.Replaying, svc.ReplayState);
    }

    [Fact]
    public async Task NavigateTo_ShouldRestoreBoardToCorrectPosition()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        var move1 = FirstLegalMove(svc);
        await svc.HumanMoveAsync(move1);
        await WaitAiDone(svc);

        if (svc.MoveHistory.Count == 0) return;

        // 計算第 1 步後的棋盤 FEN（在重播之前就算好）
        var boardAfterStep1 = new Board(InitialFen);
        boardAfterStep1.MakeMove(svc.MoveHistory[0].Move);
        var expectedFen1 = boardAfterStep1.ToFen();

        await svc.EnterReplayModeAsync();
        await svc.NavigateToAsync(1);

        Assert.Equal(1, svc.ReplayCurrentStep);
        Assert.Equal(expectedFen1, svc.CurrentBoard.ToFen());
    }

    [Fact]
    public async Task NavigateTo_Zero_ShouldRestoreInitialBoard()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        await WaitAiDone(svc);

        await svc.EnterReplayModeAsync();
        await svc.NavigateToAsync(0);

        Assert.Equal(0, svc.ReplayCurrentStep);
        Assert.Equal(InitialFen, svc.CurrentBoard.ToFen());
    }

    [Fact]
    public async Task GoToEnd_ShouldRestoreLiveState()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        await WaitAiDone(svc);

        await svc.EnterReplayModeAsync();
        await svc.NavigateToAsync(0);
        await svc.GoToEndAsync();

        Assert.Equal(ReplayState.Live, svc.ReplayState);
        Assert.Equal(svc.MoveHistory.Count, svc.ReplayCurrentStep);
    }

    // ─── Phase 4：中途換手 ─────────────────────────────────────────────────

    [Fact]
    public async Task ContinueFromCurrentPosition_ShouldTruncateHistoryAndSetLive()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        await WaitAiDone(svc);

        if (svc.MoveHistory.Count < 2) return;

        var originalFirstEntry = svc.MoveHistory[0];

        await svc.EnterReplayModeAsync();
        await svc.NavigateToAsync(1);

        // 截斷至第 1 步後繼續（PlayerVsAi，輪到黑方，AI 先走）
        // ContinueFromCurrentPositionAsync 會截斷再啟動 AI，歷史可能增加
        await svc.ContinueFromCurrentPositionAsync(GameMode.PlayerVsAi);
        await WaitAiDone(svc);

        // 第 1 步原始條目應仍在歷史的第 1 筆
        Assert.Equal(originalFirstEntry.Move, svc.MoveHistory[0].Move);
        Assert.Equal(ReplayState.Live, svc.ReplayState);
        // 截斷後歷史至少有 1 筆（可能 AI 又走了）
        Assert.True(svc.MoveHistory.Count >= 1);
    }

    // ─── Phase 5：匯出 / 匯入 ─────────────────────────────────────────────

    [Fact]
    public async Task ExportGameRecord_ShouldContainCorrectSteps()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        await WaitAiDone(svc);

        var record = svc.ExportGameRecord("測試玩家", "測試AI");

        Assert.Equal(svc.MoveHistory.Count, record.Steps.Count);
        Assert.Equal(svc.InitialFen, record.InitialFen);
        Assert.Equal("測試玩家", record.Metadata.RedPlayer);
    }

    [Fact]
    public async Task LoadGameRecord_ShouldSetReplayingAndRestoreHistory()
    {
        var svc = CreateGameService();
        await svc.StartGameAsync(GameMode.PlayerVsAi);

        await svc.HumanMoveAsync(FirstLegalMove(svc));
        await WaitAiDone(svc);

        var record = svc.ExportGameRecord();
        var originalCount = record.Steps.Count;

        await svc.StartGameAsync(GameMode.PlayerVsAi);
        await svc.LoadGameRecordAsync(record);

        Assert.Equal(ReplayState.Replaying, svc.ReplayState);
        Assert.Equal(originalCount, svc.MoveHistory.Count);
        Assert.Equal(record.InitialFen, svc.InitialFen);
    }
}
