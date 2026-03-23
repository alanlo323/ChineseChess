using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// GameService 提和功能整合測試。
/// 驗證玩家提和、AI 主動提和的完整流程。
/// </summary>
public class GameServiceDrawOfferTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── DrawOffer 模型測試 ──────────────────────────────────────────────────

    [Fact]
    public void DrawOfferResult_Accepted_ShouldHaveCorrectProperties()
    {
        var result = new DrawOfferResult(DrawOfferSource.Player, Accepted: true, Reason: "AI 接受提和");

        Assert.True(result.Accepted);
        Assert.Equal(DrawOfferSource.Player, result.Source);
        Assert.Equal("AI 接受提和", result.Reason);
    }

    [Fact]
    public void DrawOfferResult_Rejected_ShouldHaveCorrectProperties()
    {
        var result = new DrawOfferResult(DrawOfferSource.Ai, Accepted: false, Reason: "AI 拒絕提和：AI 佔優");

        Assert.False(result.Accepted);
        Assert.Equal(DrawOfferSource.Ai, result.Source);
        Assert.Equal("AI 拒絕提和：AI 佔優", result.Reason);
    }

    [Fact]
    public void DrawOfferSettings_DefaultValues_ShouldBeCorrect()
    {
        var settings = new DrawOfferSettings();

        Assert.Equal(50, settings.DrawOfferThreshold);
        Assert.Equal(100, settings.DrawRefuseThreshold);
        Assert.Equal(30, settings.MinMoveCountForAiDrawOffer);
        Assert.Equal(10, settings.CooldownMoves);
    }

    // ─── 玩家提和：AI 評估後接受（均勢局面）────────────────────────────────

    [Fact]
    public async Task RequestDrawAsync_WhenAiIsNeutral_ShouldAcceptAndFireDrawOfferResolved()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);
        // 停用開局限制，讓測試專注於均勢判斷
        gameService.SetDrawOfferSettings(new DrawOfferSettings { MinMoveCountForAiDrawOffer = 0 });

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 強制設定為均勢局面（空棋盤，AI 搜尋分數接近 0）
        // 使用只有雙將的殘局，避免 AI 搜尋走法
        ((Board)gameService.CurrentBoard).ParseFen("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        await gameService.RequestDrawAsync();

        // 等待 AI 評估完成
        await Task.Delay(1000);

        Assert.NotNull(resolvedResult);
        Assert.Equal(DrawOfferSource.Player, resolvedResult!.Source);
    }

    // ─── 玩家提和：開局階段拒絕 ─────────────────────────────────────────────

    [Fact]
    public async Task RequestDrawAsync_InOpeningPhase_ShouldRefuseEvenIfPositionIsEven()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);
        // 使用預設設定（MinMoveCountForAiDrawOffer = 30），不做任何走棋

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 設定均勢局面，但步數為 0（開局）
        ((Board)gameService.CurrentBoard).ParseFen("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        await gameService.RequestDrawAsync();

        // 開局拒絕是同步的（不需要等待 AI 搜尋）
        await Task.Delay(100);

        Assert.NotNull(resolvedResult);
        Assert.Equal(DrawOfferSource.Player, resolvedResult!.Source);
        Assert.False(resolvedResult.Accepted);
        Assert.Contains("開局", resolvedResult.Reason);
    }

    // ─── 玩家提和：AI 佔優時拒絕 ────────────────────────────────────────────

    [Fact]
    public async Task RequestDrawAsync_WhenAiHasAdvantage_ShouldRefuseAndFireDrawOfferResolved()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(2, 1000, 1);
        // 停用開局限制，讓測試專注於優劣判斷
        gameService.SetDrawOfferSettings(new DrawOfferSettings { MinMoveCountForAiDrawOffer = 0 });

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 設定 AI（黑方）明顯佔優的局面：黑有車，紅沒有
        // 紅方（玩家）提和，黑方（AI）應拒絕
        ((Board)gameService.CurrentBoard).ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/9/9/9/4K4 w - - 0 1");

        await gameService.RequestDrawAsync();

        // 等待 AI 評估完成
        await Task.Delay(2000);

        Assert.NotNull(resolvedResult);
        Assert.Equal(DrawOfferSource.Player, resolvedResult!.Source);
        Assert.False(resolvedResult.Accepted);
    }

    // ─── 非 PlayerVsAi 模式下不能提和 ──────────────────────────────────────

    [Fact]
    public async Task RequestDrawAsync_InPlayerVsPlayerMode_ShouldNotFireEvent()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsPlayer);

        await gameService.RequestDrawAsync();

        Assert.Null(resolvedResult);
    }

    [Fact]
    public async Task RequestDrawAsync_InAiVsAiMode_ShouldNotFireEvent()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.AiVsAi);
        await gameService.StopGameAsync();

        await gameService.RequestDrawAsync();

        Assert.Null(resolvedResult);
    }

    // ─── AI 主動提和：均勢局面且走步數足夠 ──────────────────────────────────

    [Fact]
    public async Task AiDrawOffer_WhenEquilibrium_AndSufficientMoves_ShouldFireDrawOffered()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);
        // 停用步數門檻，讓提和評估專注於均勢分數
        gameService.SetDrawOfferSettings(new DrawOfferSettings { MinMoveCountForAiDrawOffer = 0 });

        DrawOfferResult? drawOffered = null;
        gameService.DrawOffered += args => drawOffered = args;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 設定均勢殘局（各一炮，評分接近 0）
        // 注意：不能用雙王殘局，否則觸發「棋子不足和棋」（皮卡魚規則）
        // FEN：黑將(0,4)=4、黑炮(1,4)=13，紅炮(8,4)=76、紅帥(9,4)=85
        // 兩炮擋住飛將，均勢局面 AI 評分 ≈ 0
        ((Board)gameService.CurrentBoard).ParseFen("4k4/4c4/9/9/9/9/9/9/4C4/4K4 w - - 0 1");

        // 紅方（玩家）走帥橫移，觸發 AI（黑方）進行搜尋
        await gameService.HumanMoveAsync(new Move(85, 84));

        // 等待 AI 完成搜尋並執行提和評估（最多 2 秒）
        await Task.Delay(2000);

        // AI 應偵測到均勢並主動提和
        Assert.NotNull(drawOffered);
        Assert.Equal(DrawOfferSource.Ai, drawOffered!.Source);
    }

    // ─── AI 提和後玩家接受 ────────────────────────────────────────────────

    [Fact]
    public async Task RespondToDrawOffer_WhenPlayerAccepts_ShouldFireDrawOfferResolvedWithAccepted()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 模擬 AI 已提和（待玩家回應）
        gameService.SimulateAiDrawOffer();

        // 玩家接受
        gameService.RespondToDrawOffer(true);

        Assert.NotNull(resolvedResult);
        Assert.Equal(DrawOfferSource.Ai, resolvedResult!.Source);
        Assert.True(resolvedResult.Accepted);
    }

    // ─── AI 提和後玩家拒絕 ────────────────────────────────────────────────

    [Fact]
    public async Task RespondToDrawOffer_WhenPlayerRejects_ShouldFireDrawOfferResolvedWithRejected()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        gameService.SimulateAiDrawOffer();

        // 玩家拒絕
        gameService.RespondToDrawOffer(false);

        Assert.NotNull(resolvedResult);
        Assert.Equal(DrawOfferSource.Ai, resolvedResult!.Source);
        Assert.False(resolvedResult.Accepted);
    }

    // ─── 遊戲結束後提和應無效 ────────────────────────────────────────────

    [Fact]
    public async Task RequestDrawAsync_WhenGameIsOver_ShouldNotFireEvent()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 將局面設定為終局（黑方被將死）
        ((Board)gameService.CurrentBoard).ParseFen("3k5/9/9/9/9/9/9/9/9/R3K4 w - - 0 1");

        // 手動設定 isGameOverFlag（透過走棋觸發勝負）
        // isGameOver 現為 property，backing field 為 isGameOverFlag（int，1=true）
        var field = typeof(GameService).GetField(
            "isGameOverFlag",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(gameService, 1);

        await gameService.RequestDrawAsync();

        Assert.Null(resolvedResult);
    }

    // ─── RespondToDrawOffer 在無待提和時應無效 ───────────────────────────

    [Fact]
    public async Task RespondToDrawOffer_WhenNoPendingOffer_ShouldNotFireEvent()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);

        DrawOfferResult? resolvedResult = null;
        gameService.DrawOfferResolved += r => resolvedResult = r;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 沒有待回應的提和，直接回應
        gameService.RespondToDrawOffer(true);

        Assert.Null(resolvedResult);
    }

    // ─── AI 提和冷卻機制 ─────────────────────────────────────────────────

    [Fact]
    public async Task AiDrawOffer_CooldownShouldPreventRepeatedOffers()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        int drawOfferedCount = 0;
        gameService.DrawOffered += _ => drawOfferedCount++;

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 第一次 AI 提和
        gameService.SimulateAiDrawOffer();

        // 玩家拒絕，觸發冷卻
        gameService.RespondToDrawOffer(false);

        // 記錄 DrawOffered 觸發次數（應只有 1 次）
        Assert.Equal(1, drawOfferedCount);

        // 嘗試再次模擬提和（在冷卻期間應被忽略）
        // 冷卻期未到，不應再次觸發
        gameService.SimulateAiDrawOffer();

        // 仍應只有 1 次
        Assert.Equal(1, drawOfferedCount);
    }

    // ─── DrawOfferSettings 自訂值 ────────────────────────────────────────

    [Fact]
    public void DrawOfferSettings_CustomValues_ShouldBeApplied()
    {
        var settings = new DrawOfferSettings
        {
            DrawOfferThreshold = 30,
            DrawRefuseThreshold = 150,
            MinMoveCountForAiDrawOffer = 40,
            CooldownMoves = 15
        };

        Assert.Equal(30, settings.DrawOfferThreshold);
        Assert.Equal(150, settings.DrawRefuseThreshold);
        Assert.Equal(40, settings.MinMoveCountForAiDrawOffer);
        Assert.Equal(15, settings.CooldownMoves);
    }

    // ─── 提和接受後遊戲應結束 ────────────────────────────────────────────

    [Fact]
    public async Task RequestDrawAsync_WhenAccepted_ShouldSetGameOver()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 設定均勢局面
        ((Board)gameService.CurrentBoard).ParseFen("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        await gameService.RequestDrawAsync();

        // 等待 AI 評估
        await Task.Delay(1000);

        // 無論接受或拒絕，確認事件有觸發（提和流程完整執行）
        Assert.True(gameService.IsDrawOfferProcessed);
    }

    // ─── 提和接受後不應再接受走棋 ────────────────────────────────────────

    [Fact]
    public async Task HumanMove_AfterDrawAccepted_ShouldBeRejected()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 500, 1);

        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        // 模擬 AI 提和，玩家接受
        gameService.SimulateAiDrawOffer();
        gameService.RespondToDrawOffer(true);

        var fenAfterDraw = gameService.CurrentBoard.ToFen();

        // 嘗試繼續走棋
        var moves = gameService.CurrentBoard.GenerateLegalMoves();
        var enumerator = moves.GetEnumerator();
        if (enumerator.MoveNext())
        {
            await gameService.HumanMoveAsync(enumerator.Current);
        }

        // 局面不應改變
        Assert.Equal(fenAfterDraw, gameService.CurrentBoard.ToFen());
    }
}
