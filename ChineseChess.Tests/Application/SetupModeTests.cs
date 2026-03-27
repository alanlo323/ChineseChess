using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Search;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// GameService 擺棋模式整合測試。
/// 涵蓋：進入/退出擺棋模式、放置/移除棋子、清空/重置棋盤、
/// 設定行棋方、確認局面（合法/非法）以及擺棋期間禁止走棋等守衛行為。
/// </summary>
public class SetupModeTests
{
    private static GameService CreateService() => new GameService(new SearchEngine());

    private static async Task<GameService> CreateAndStartService(GameMode mode = GameMode.PlayerVsPlayer)
    {
        var service = CreateService();
        await service.StartGameAsync(mode);
        return service;
    }

    // 最小合法局面：紅帥(85)、黑將(4)、紅兵阻擋(40)，三子直線不對臉
    private static void SetupMinimalLegalBoard(GameService service)
    {
        service.SetupClearBoard();
        service.SetupPlacePiece(85, new Piece(PieceColor.Red, PieceType.King));
        service.SetupPlacePiece(4, new Piece(PieceColor.Black, PieceType.King));
        service.SetupPlacePiece(40, new Piece(PieceColor.Red, PieceType.Pawn));
    }

    // ─── 進入擺棋模式 ────────────────────────────────────────────────────

    [Fact]
    public async Task EnterSetupMode_ShouldSetIsInSetupModeTrue()
    {
        var service = await CreateAndStartService();

        await service.EnterSetupModeAsync();

        Assert.True(service.IsInSetupMode);
    }

    [Fact]
    public async Task EnterSetupMode_ShouldFireSetupModeChangedEvent()
    {
        var service = await CreateAndStartService();
        bool eventFired = false;
        service.SetupModeChanged += () => eventFired = true;

        await service.EnterSetupModeAsync();

        Assert.True(eventFired);
    }

    [Fact]
    public async Task EnterSetupMode_WhenAlreadyInSetupMode_ShouldNotFireEventAgain()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        int eventCount = 0;
        service.SetupModeChanged += () => eventCount++;
        await service.EnterSetupModeAsync(); // 重複進入

        Assert.Equal(0, eventCount);
    }

    // ─── 放置棋子 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupPlacePiece_ShouldUpdateBoard()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        var piece = new Piece(PieceColor.Red, PieceType.Cannon);
        service.SetupPlacePiece(30, piece);

        Assert.Equal(piece, service.CurrentBoard.GetPiece(30));
    }

    [Fact]
    public async Task SetupPlacePiece_ShouldFireBoardUpdated()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        bool fired = false;
        service.BoardUpdated += () => fired = true;

        service.SetupPlacePiece(30, new Piece(PieceColor.Red, PieceType.Cannon));

        Assert.True(fired);
    }

    [Fact]
    public async Task SetupPlacePiece_WhenNotInSetupMode_ShouldIgnore()
    {
        var service = await CreateAndStartService();
        // 未進入擺棋模式

        service.SetupPlacePiece(30, new Piece(PieceColor.Red, PieceType.Cannon));

        Assert.Equal(Piece.None, service.CurrentBoard.GetPiece(30));
    }

    // ─── 移除棋子 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupRemovePiece_ShouldClearSquare()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        service.SetupPlacePiece(30, new Piece(PieceColor.Red, PieceType.Cannon));

        service.SetupRemovePiece(30);

        Assert.Equal(Piece.None, service.CurrentBoard.GetPiece(30));
    }

    [Fact]
    public async Task SetupRemovePiece_WhenNotInSetupMode_ShouldIgnore()
    {
        var service = await CreateAndStartService();
        // 初始局面 index=0 是黑方車（不在擺棋模式）

        service.SetupRemovePiece(0);

        // 初始局面的黑方車應仍在
        Assert.NotEqual(Piece.None, service.CurrentBoard.GetPiece(0));
    }

    // ─── 清空棋盤 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupClearBoard_ShouldRemoveAllPieces()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        service.SetupClearBoard();

        for (int i = 0; i < 90; i++)
        {
            Assert.Equal(Piece.None, service.CurrentBoard.GetPiece(i));
        }
    }

    [Fact]
    public async Task SetupClearBoard_WhenNotInSetupMode_ShouldIgnore()
    {
        var service = await CreateAndStartService();

        service.SetupClearBoard();

        // 初始局面的棋子應仍在
        Assert.NotEqual(Piece.None, service.CurrentBoard.GetPiece(0));
    }

    // ─── 重置棋盤 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupResetBoard_ShouldRestoreInitialPosition()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        service.SetupClearBoard(); // 先清空

        service.SetupResetBoard();

        // 驗證初始局面（黑方車在 index=0）
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Rook), service.CurrentBoard.GetPiece(0));
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Rook), service.CurrentBoard.GetPiece(89));
    }

    // ─── 設定行棋方 ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetupSetTurn_ShouldUpdateBoardTurn()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        service.SetupSetTurn(PieceColor.Black);

        Assert.Equal(PieceColor.Black, service.CurrentBoard.Turn);
    }

    [Fact]
    public async Task SetupSetTurn_WhenNotInSetupMode_ShouldIgnore()
    {
        var service = await CreateAndStartService();

        service.SetupSetTurn(PieceColor.Black);

        Assert.Equal(PieceColor.Red, service.CurrentBoard.Turn); // 維持 Red
    }

    // ─── 確認局面（合法） ────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmSetup_WithLegalBoard_ShouldSucceedAndExitSetupMode()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        SetupMinimalLegalBoard(service);

        var result = await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        Assert.True(result.IsValid);
        Assert.False(service.IsInSetupMode);
    }

    [Fact]
    public async Task ConfirmSetup_WithLegalBoard_ShouldFireSetupModeChangedEvent()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        SetupMinimalLegalBoard(service);

        int eventCount = 0;
        service.SetupModeChanged += () => eventCount++;

        await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        Assert.Equal(1, eventCount);
    }

    // ─── 確認局面（非法） ────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmSetup_WithIllegalBoard_ShouldReturnInvalidAndStayInSetupMode()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        service.SetupClearBoard();
        // 沒有放任何將帥 → 非法

        var result = await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        Assert.False(result.IsValid);
        Assert.True(service.IsInSetupMode); // 仍在擺棋模式
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ConfirmSetup_WithFlyingKings_ShouldReturnInvalid()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        service.SetupClearBoard();
        // 將帥對面無阻擋
        service.SetupPlacePiece(85, new Piece(PieceColor.Red, PieceType.King));
        service.SetupPlacePiece(4, new Piece(PieceColor.Black, PieceType.King));

        var result = await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ConfirmSetup_WhenNotInSetupMode_ShouldReturnInvalid()
    {
        var service = await CreateAndStartService();
        // 未進入擺棋模式

        var result = await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        Assert.False(result.IsValid);
    }

    // ─── 擺棋期間禁止走棋 ────────────────────────────────────────────────

    [Fact]
    public async Task HumanMove_WhileInSetupMode_ShouldBeIgnored()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        // 嘗試走法（任意合法走法）
        var move = new Move(81, 72); // 隨意走法
        await service.HumanMoveAsync(move);

        // 棋盤不應有任何正式走棋歷史
        Assert.Empty(service.MoveHistory);
    }

    [Fact]
    public async Task Undo_WhileInSetupMode_ShouldBeIgnored()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();

        // 不應拋出例外
        var exception = Record.Exception(() => service.Undo());
        Assert.Null(exception);
    }

    // ─── 確認後的遊戲繼續 ────────────────────────────────────────────────

    [Fact]
    public async Task AfterConfirmSetup_BoardShouldReflectSetupPosition()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        SetupMinimalLegalBoard(service);
        service.SetupSetTurn(PieceColor.Black);

        await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        // 確認棋盤保留擺棋局面（位置與 SetupMinimalLegalBoard 一致）
        Assert.Equal(new Piece(PieceColor.Red, PieceType.King), service.CurrentBoard.GetPiece(85));
        Assert.Equal(new Piece(PieceColor.Black, PieceType.King), service.CurrentBoard.GetPiece(4));
        Assert.Equal(PieceColor.Black, service.CurrentBoard.Turn);
    }

    // ─── IsInSetupMode 預設值 ────────────────────────────────────────────

    [Fact]
    public void IsInSetupMode_InitialState_ShouldBeFalse()
    {
        var service = CreateService();
        Assert.False(service.IsInSetupMode);
    }

    [Fact]
    public async Task IsInSetupMode_AfterStartGame_ShouldBeFalse()
    {
        var service = await CreateAndStartService();
        Assert.False(service.IsInSetupMode);
    }

    // ─── 確認後 AI 觸發行為 ──────────────────────────────────────────────

    /// <summary>
    /// PlayerVsAi 模式，玩家=紅，局面設為黑方先手（AI 輪次）→ 確認後 AI 應走出第一步。
    /// </summary>
    [Fact]
    public async Task ConfirmSetup_PlayerVsAi_AiTurn_ShouldTriggerAiAndHaveMoveInHistory()
    {
        var service = CreateService();
        service.PlayerColor = PieceColor.Red;   // 玩家是紅，AI 是黑
        // 用 PlayerVsPlayer 啟動以跳過 StartGameAsync 的 AI 先手觸發；
        // 真正要測的是 ConfirmSetupAsync 的觸發邏輯。
        await service.StartGameAsync(GameMode.PlayerVsPlayer);
        await service.EnterSetupModeAsync();
        SetupMinimalLegalBoard(service);
        service.SetupSetTurn(PieceColor.Black); // 輪到 AI（黑）走棋

        await service.ConfirmSetupAsync(GameMode.PlayerVsAi);

        // AI 應已走出第一步
        Assert.NotEmpty(service.MoveHistory);
    }

    /// <summary>
    /// PlayerVsAi 模式，玩家=紅，局面設為紅方先手（玩家輪次）→ 確認後應等待玩家，AI 不應先走。
    /// </summary>
    [Fact]
    public async Task ConfirmSetup_PlayerVsAi_PlayerTurn_ShouldNotTriggerAi()
    {
        var service = CreateService();
        service.PlayerColor = PieceColor.Red;   // 玩家是紅
        await service.StartGameAsync(GameMode.PlayerVsPlayer);
        await service.EnterSetupModeAsync();
        SetupMinimalLegalBoard(service);
        service.SetupSetTurn(PieceColor.Red);   // 輪到玩家（紅）走棋

        await service.ConfirmSetupAsync(GameMode.PlayerVsAi);

        // 玩家尚未走棋，歷史應為空
        Assert.Empty(service.MoveHistory);
    }

    /// <summary>
    /// PlayerVsPlayer 模式 → 確認後不觸發 AI，歷史為空。
    /// </summary>
    [Fact]
    public async Task ConfirmSetup_PlayerVsPlayer_ShouldNotTriggerAi()
    {
        var service = await CreateAndStartService();
        await service.EnterSetupModeAsync();
        SetupMinimalLegalBoard(service);

        await service.ConfirmSetupAsync(GameMode.PlayerVsPlayer);

        Assert.Empty(service.MoveHistory);
    }
}
