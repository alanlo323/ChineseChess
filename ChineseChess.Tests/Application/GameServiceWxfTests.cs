using ChineseChess.Application.Enums;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// GameService 中 WXF 重複局面裁決整合測試。
///
/// 測試 wxfHistory 的管理（新增/清除/Undo）以及 WXF 裁決的觸發。
///
/// LoopFen 局面說明（延續 GameServiceDrawTests 的設計）：
///   "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1"
///   合法循環走法（均為 Idle 分類）：
///     紅車 a1(81) ↔ a2(72)
///     黑仕 f10(5) ↔ e9(13)
/// </summary>
public class GameServiceWxfTests
{
    private const string LoopFen = "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1";

    private static readonly Move RedRookDown      = new Move(81, 72);
    private static readonly Move RedRookUp        = new Move(72, 81);
    private static readonly Move BlackAdvisorDown = new Move(5, 13);
    private static readonly Move BlackAdvisorUp   = new Move(13, 5);

    private static async Task DoOneCycleAsync(GameService gs)
    {
        await gs.HumanMoveAsync(RedRookDown);
        await gs.HumanMoveAsync(BlackAdvisorDown);
        await gs.HumanMoveAsync(RedRookUp);
        await gs.HumanMoveAsync(BlackAdvisorUp);
    }

    // ─── Reflection 輔助 ──────────────────────────────────────────────────

    private static readonly FieldInfo WxfHistoryField =
        typeof(GameService).GetField("wxfHistory",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new System.InvalidOperationException("wxfHistory field not found");

    private static readonly MethodInfo ResetWxfHistoryMethod =
        typeof(GameService).GetMethod("ResetWxfHistory",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new System.InvalidOperationException("ResetWxfHistory method not found");

    private static int GetWxfHistoryCount(GameService gs)
        => (WxfHistoryField.GetValue(gs) as System.Collections.IList)?.Count ?? 0;

    /// <summary>切換 FEN 並重置 wxfHistory 使種子對應新局面。</summary>
    private static void LoadFenAndResetHistory(GameService gs, string fen)
    {
        ((Board)gs.CurrentBoard).ParseFen(fen);
        // 呼叫私有方法 ResetWxfHistory，讓種子 ZobristKey 對應新局面
        ResetWxfHistoryMethod.Invoke(gs, null);
    }

    // ─── wxfHistory 管理測試 ─────────────────────────────────────────────

    [Fact]
    public async Task WxfHistory_HasSeedAfterStartGame()
    {
        var gs = new GameService(new SearchEngine());
        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        Assert.Equal(1, GetWxfHistoryCount(gs));
    }

    [Fact]
    public async Task WxfHistory_GrowsAfterHumanMoves()
    {
        var gs = new GameService(new SearchEngine());
        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        LoadFenAndResetHistory(gs, LoopFen);

        await gs.HumanMoveAsync(RedRookDown);
        Assert.Equal(2, GetWxfHistoryCount(gs)); // 種子 + 1 步

        await gs.HumanMoveAsync(BlackAdvisorDown);
        Assert.Equal(3, GetWxfHistoryCount(gs)); // 種子 + 2 步
    }

    [Fact]
    public async Task WxfHistory_IsClearedOnStartGame()
    {
        var gs = new GameService(new SearchEngine());
        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        LoadFenAndResetHistory(gs, LoopFen);

        await gs.HumanMoveAsync(RedRookDown);
        await gs.HumanMoveAsync(BlackAdvisorDown);
        Assert.Equal(3, GetWxfHistoryCount(gs));

        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        Assert.Equal(1, GetWxfHistoryCount(gs)); // 重開後只剩種子
    }

    [Fact]
    public async Task WxfHistory_IsClearedOnLoadBookmark()
    {
        var gs = new GameService(new SearchEngine());
        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        LoadFenAndResetHistory(gs, LoopFen);

        await gs.HumanMoveAsync(RedRookDown);
        await gs.HumanMoveAsync(BlackAdvisorDown);
        var countAfterMoves = GetWxfHistoryCount(gs);
        Assert.Equal(3, countAfterMoves);

        gs.AddBookmark("testSave");
        gs.LoadBookmark("testSave");
        Assert.Equal(1, GetWxfHistoryCount(gs)); // 載入書籤後只剩種子
    }

    [Fact]
    public async Task WxfHistory_IsReducedOnUndo_PlayerVsPlayer()
    {
        var gs = new GameService(new SearchEngine());
        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        LoadFenAndResetHistory(gs, LoopFen);

        await gs.HumanMoveAsync(RedRookDown);
        await gs.HumanMoveAsync(BlackAdvisorDown);
        Assert.Equal(3, GetWxfHistoryCount(gs)); // 種子 + 2

        gs.Undo(); // 悔 1 步
        Assert.Equal(2, GetWxfHistoryCount(gs));

        gs.Undo(); // 悔 1 步
        Assert.Equal(1, GetWxfHistoryCount(gs)); // 只剩種子
    }

    [Fact]
    public async Task WxfHistory_SeedNotRemovedOnUndoToStart()
    {
        var gs = new GameService(new SearchEngine());
        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        LoadFenAndResetHistory(gs, LoopFen);

        await gs.HumanMoveAsync(RedRookDown);
        gs.Undo(); // 悔完所有步
        Assert.Equal(1, GetWxfHistoryCount(gs)); // 種子仍在

        // 再次 Undo 不應再減少
        gs.Undo();
        Assert.Equal(1, GetWxfHistoryCount(gs));
    }

    // ─── WXF 裁決觸發測試（整合測試）────────────────────────────────────

    [Fact]
    public async Task HumanMove_DrawByIdleRepetition_TriggersWxfDraw()
    {
        var gs = new GameService(new SearchEngine());
        var messages = new List<string>();
        gs.GameMessage += msg => messages.Add(msg);

        await gs.StartGameAsync(GameMode.PlayerVsPlayer);
        LoadFenAndResetHistory(gs, LoopFen);

        // 兩個完整循環 → 局面第三次出現（全 Idle）→ WXF Draw
        await DoOneCycleAsync(gs);
        await DoOneCycleAsync(gs);

        Assert.Contains(messages, msg => msg.Contains("和棋") || msg.Contains("重複") || msg.Contains("Draw"));
    }
}
