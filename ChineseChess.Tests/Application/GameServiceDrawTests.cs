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
/// GameService 和棋判定整合測試。
/// 驗證三次重覆局面與六十步無吃子觸發正確的 GameMessage。
///
/// LoopFen 局面說明：
///   "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1"
///   黑方：將 e10(4)、仕 f10(5)，紅方：車 a1(81)、帥 d1(84)
///   合法循環走法：
///     紅車 a1(81) ↔ a2(72)
///     黑仕 f10(5) ↔ e9(13)
/// </summary>
public class GameServiceDrawTests
{
    private const string LoopFen = "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1";

    // 合法循環走法（已驗證）
    private static readonly Move RedRookDown      = new Move(81, 72); // 紅車 a1→a2
    private static readonly Move RedRookUp        = new Move(72, 81); // 紅車 a2→a1
    private static readonly Move BlackAdvisorDown = new Move(5, 13);  // 黑仕 f10→e9
    private static readonly Move BlackAdvisorUp   = new Move(13, 5);  // 黑仕 e9→f10

    /// <summary>對 GameService 執行一個完整循環（4 步，局面回到起點）。</summary>
    private static async Task DoOneCycleAsync(GameService gameService)
    {
        await gameService.HumanMoveAsync(RedRookDown);
        await gameService.HumanMoveAsync(BlackAdvisorDown);
        await gameService.HumanMoveAsync(RedRookUp);
        await gameService.HumanMoveAsync(BlackAdvisorUp);
    }

    /// <summary>
    /// 透過反射設定 Board 的私有 _halfMoveClock 欄位（整合測試輔助方法）。
    /// </summary>
    private static void SetHalfMoveClock(Board board, int value)
    {
        var field = typeof(Board).GetField(
            "halfMoveClock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(board, value);
    }

    // ─── 三次重覆局面（IsDrawByRepetition）整合測試 ───────────────────────

    [Fact]
    public async Task HumanMove_DrawByRepetition_ShouldTriggerDrawMessage()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        // 兩個完整循環後局面出現第三次 → 和棋
        await DoOneCycleAsync(gameService);
        await DoOneCycleAsync(gameService);

        Assert.Contains(messages, msg => msg.Contains("重覆") || msg.Contains("和棋") || msg.Contains("Draw"));
    }

    [Fact]
    public async Task HumanMove_DrawByRepetition_MessageShouldContainCorrectReason()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        await DoOneCycleAsync(gameService);
        await DoOneCycleAsync(gameService);

        // 和棋訊息必須包含「三次重覆局面」
        Assert.Contains(messages, msg => msg.Contains("三次重覆局面"));
    }

    [Fact]
    public async Task HumanMove_DrawByRepetition_ShouldNotTriggerAfterOneCycle()
    {
        // 只走一個循環（局面第二次出現），不應觸發和棋
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        await DoOneCycleAsync(gameService);

        Assert.DoesNotContain(messages, msg => msg.Contains("重覆") || msg.Contains("六十步"));
    }

    // ─── 六十步無吃子（IsDrawByNoCapture）整合測試 ───────────────────────

    [Fact]
    public async Task HumanMove_DrawByNoCapture_ShouldTriggerDrawMessage()
    {
        // 使用反射將 HalfMoveClock 設為 59，再走一步觸發無吃子和棋
        // 此局面：紅車 a1(81)，黑將 e10(4)，黑仕 f10(5)，紅帥 e1(85)
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        // 設定無吃子計數為 59（模擬已走了 59 步無吃子）
        SetHalfMoveClock((Board)gameService.CurrentBoard, 59);
        Assert.Equal(59, gameService.CurrentBoard.HalfMoveClock);

        // 走第 60 步（無吃子）觸發和棋
        await gameService.HumanMoveAsync(RedRookDown); // 紅車 a1→a2，無吃子

        Assert.Contains(messages, msg => msg.Contains("和棋") || msg.Contains("Draw") || msg.Contains("六十步"));
    }

    [Fact]
    public async Task HumanMove_DrawByNoCapture_MessageShouldContainCorrectReason()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        // 設定無吃子計數為 59，再走一步觸發
        SetHalfMoveClock((Board)gameService.CurrentBoard, 59);
        await gameService.HumanMoveAsync(RedRookDown);

        // 訊息應包含「六十步無吃子」
        Assert.Contains(messages, msg => msg.Contains("六十步無吃子"));
    }

    [Fact]
    public async Task HumanMove_DrawByNoCapture_At59Moves_ShouldNotTrigger()
    {
        // 無吃子計數未達 60，不應觸發和棋
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        // 設定無吃子計數為 58，走一步後只有 59 步
        SetHalfMoveClock((Board)gameService.CurrentBoard, 58);
        await gameService.HumanMoveAsync(RedRookDown); // 59 步，黑方回合

        Assert.DoesNotContain(messages, msg => msg.Contains("六十步"));
        Assert.Equal(59, gameService.CurrentBoard.HalfMoveClock);
    }

    // ─── 和棋後拒絕繼續走棋 ──────────────────────────────────────────────

    [Fact]
    public async Task HumanMove_AfterDrawDetected_ShouldNotAcceptFurtherMoves()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        // 觸發和棋（三次重覆）
        await DoOneCycleAsync(gameService);
        await DoOneCycleAsync(gameService);

        var fenAfterDraw = gameService.CurrentBoard.ToFen();
        messages.Clear();

        // 再嘗試走棋，局面不應改變（遊戲已結束）
        await gameService.HumanMoveAsync(RedRookDown);

        Assert.Equal(fenAfterDraw, gameService.CurrentBoard.ToFen());
    }

    // ─── 重新開始遊戲重置和棋狀態 ────────────────────────────────────────

    [Fact]
    public async Task StartGame_AfterDraw_ResetsGameState()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        // 第一局：觸發和棋
        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        await DoOneCycleAsync(gameService);
        await DoOneCycleAsync(gameService);

        Assert.Contains(messages, msg => msg.Contains("重覆") || msg.Contains("和棋") || msg.Contains("Draw"));
        messages.Clear();

        // 重新開始，不應立即觸發和棋
        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        // 走一步後確認未觸發和棋
        await gameService.HumanMoveAsync(RedRookDown);

        Assert.DoesNotContain(messages, msg => msg.Contains("重覆") || msg.Contains("六十步"));
    }

    // ─── HalfMoveClock 在黑方走棋後正確更新 ──────────────────────────────

    [Fact]
    public async Task HumanMove_HalfMoveClock_IncrementsAfterEachNonCaptureMove()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        Assert.Equal(0, gameService.CurrentBoard.HalfMoveClock);

        await gameService.HumanMoveAsync(RedRookDown);   // +1（紅走）
        Assert.Equal(1, gameService.CurrentBoard.HalfMoveClock);

        await gameService.HumanMoveAsync(BlackAdvisorDown); // +1（黑走）
        Assert.Equal(2, gameService.CurrentBoard.HalfMoveClock);
    }

    // ─── 和棋優先於勝負判定 ───────────────────────────────────────────────

    [Fact]
    public async Task DrawByRepetition_ShouldTriggerBeforeCheckmateCheck()
    {
        // 驗證和棋判定在勝負判定之前執行（CheckGameOver 的優先順序）
        // 此測試確認即使局面複雜，只要出現三次重覆，就優先判定為和棋
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        gameService.GameMessage += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);
        ((Board)gameService.CurrentBoard).ParseFen(LoopFen);

        await DoOneCycleAsync(gameService);
        await DoOneCycleAsync(gameService);

        // 應觸發和棋訊息（而非將死或困斃）
        Assert.Contains(messages, msg => msg.Contains("和棋") || msg.Contains("重覆"));
        Assert.DoesNotContain(messages, msg => msg.Contains("將死") || msg.Contains("困斃"));
    }
}
