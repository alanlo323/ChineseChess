using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 智能提示評估效能測試。
/// 驗證 EvaluateMovesAsync 在各深度下必須在合理時間內完成所有走法的評估，
/// 特別是 check extension 無限延伸的 bug（depth >= 3 的平行路徑）。
/// </summary>
public class SmartHintEvaluationTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // 紅炮（左炮）在初始局面的索引：row7, col1 → index = 7*9+1 = 64
    // 此炮共有12個合法走法，包含跳過黑炮（row2,col1）吃黑馬（row0,col1）的吃子走法
    // 該吃子走法後的局面戰術豐富，容易觸發 check extension 指數爆炸
    private const int RedLeftCannonIndex = 64;

    // 紅炮（右炮）在初始局面的索引：row7, col7 → index = 7*9+7 = 70
    private const int RedRightCannonIndex = 70;

    [Fact]
    public async Task EvaluateMovesAsync_LeftCannonAllMoves_ShouldCompleteAtDepth4()
    {
        // Arrange
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var moves = board.GenerateLegalMoves()
            .Where(m => m.From == RedLeftCannonIndex)
            .ToList();

        Assert.Equal(12, moves.Count); // 確認12個走法

        // 5秒 timeout：若 check extension bug 存在，某些走法會卡住超過 10 秒
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        IReadOnlyList<MoveEvaluation>? evaluations = null;
        var ex = await Record.ExceptionAsync(async () =>
        {
            evaluations = await engine.EvaluateMovesAsync(board, moves, depth: 4, cts.Token);
        });

        // Assert
        Assert.Null(ex); // 不應拋出 OperationCanceledException
        Assert.False(cts.IsCancellationRequested,
            "EvaluateMovesAsync（depth=4）在 5 秒內未完成：check extension 無限延伸 bug 仍存在");
        Assert.NotNull(evaluations);
        Assert.Equal(12, evaluations!.Count); // 全部12個走法都應有評估結果
        Assert.Single(evaluations, e => e.IsBest); // 恰好一個最佳走法
    }

    [Fact]
    public async Task EvaluateMovesAsync_RightCannonAllMoves_ShouldCompleteAtDepth4()
    {
        // Arrange
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var moves = board.GenerateLegalMoves()
            .Where(m => m.From == RedRightCannonIndex)
            .ToList();

        Assert.Equal(12, moves.Count);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        IReadOnlyList<MoveEvaluation>? evaluations = null;
        var ex = await Record.ExceptionAsync(async () =>
        {
            evaluations = await engine.EvaluateMovesAsync(board, moves, depth: 4, cts.Token);
        });

        // Assert
        Assert.Null(ex);
        Assert.False(cts.IsCancellationRequested,
            "EvaluateMovesAsync（depth=4）右炮在 5 秒內未完成");
        Assert.NotNull(evaluations);
        Assert.Equal(12, evaluations!.Count);
    }

    [Fact]
    public async Task EvaluateMovesAsync_LeftCannonAllMoves_ShouldCompleteAtDepth3()
    {
        // Arrange
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var moves = board.GenerateLegalMoves()
            .Where(m => m.From == RedLeftCannonIndex)
            .ToList();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        IReadOnlyList<MoveEvaluation>? evaluations = null;
        var ex = await Record.ExceptionAsync(async () =>
        {
            evaluations = await engine.EvaluateMovesAsync(board, moves, depth: 3, cts.Token);
        });

        // Assert
        Assert.Null(ex);
        Assert.False(cts.IsCancellationRequested, "depth=3 在 5 秒內未完成");
        Assert.NotNull(evaluations);
        Assert.Equal(12, evaluations!.Count);
    }

    [Fact]
    public async Task EvaluateMovesAsync_LeftCannonAllMoves_ShouldCompleteAtDepth2()
    {
        // depth=2 走循序路徑，應該快速完成（回歸測試）
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var moves = board.GenerateLegalMoves()
            .Where(m => m.From == RedLeftCannonIndex)
            .ToList();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var evaluations = await engine.EvaluateMovesAsync(board, moves, depth: 2, cts.Token);

        Assert.False(cts.IsCancellationRequested, "depth=2 在 5 秒內未完成");
        Assert.Equal(12, evaluations.Count);
    }
}
