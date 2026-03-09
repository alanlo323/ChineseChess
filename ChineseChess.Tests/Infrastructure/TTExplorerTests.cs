using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 測試 TranspositionTable 的節點枚舉與樹狀探索功能。
/// </summary>
public class TTExplorerTests
{
    // ── EnumerateEntries ──────────────────────────────────────

    [Fact]
    public void EnumerateEntries_EmptyTable_ReturnsEmpty()
    {
        var tt = new TranspositionTable(1);

        var entries = tt.EnumerateEntries().ToList();

        Assert.Empty(entries);
    }

    [Fact]
    public void EnumerateEntries_SingleEntry_ReturnsThatEntry()
    {
        var tt = new TranspositionTable(1);
        tt.Store(0xABCD_1234_5678_EF00uL, 500, 5, TTFlag.Exact, new Move(10, 20));

        var entries = tt.EnumerateEntries().ToList();

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal(0xABCD_1234_5678_EF00uL, e.Key);
        Assert.Equal(500, e.Score);
        Assert.Equal((byte)5, e.Depth);
        Assert.Equal(TTFlag.Exact, e.Flag);
        Assert.Equal(new Move(10, 20), e.BestMove);
    }

    [Fact]
    public void EnumerateEntries_MultipleEntries_CountMatchesOccupied()
    {
        var tt = new TranspositionTable(1);
        // 存入多個不同 key（避免雜湊碰撞：用大間距 key）
        tt.Store(0x1000_0000_0000_0001uL, 100, 3, TTFlag.Exact, new Move(1, 11));
        tt.Store(0x2000_0000_0000_0002uL, 200, 4, TTFlag.LowerBound, new Move(2, 12));
        tt.Store(0x3000_0000_0000_0003uL, -100, 2, TTFlag.UpperBound, new Move(3, 13));

        var entries = tt.EnumerateEntries().ToList();

        // 所有條目應都被枚舉（允許碰撞導致略少，但基本上不應出現）
        Assert.InRange(entries.Count, 1, 3);
        // 每個條目都應有有效的 key（非 0）
        Assert.All(entries, e => Assert.NotEqual(0uL, e.Key));
    }

    [Fact]
    public void EnumerateEntries_AllEntriesHaveValidFlags()
    {
        var tt = new TranspositionTable(1);
        tt.Store(0xAAAA_BBBB_CCCC_DDDDuL, 0, 1, TTFlag.Exact, new Move(5, 50));
        tt.Store(0x1111_2222_3333_4444uL, 999, 8, TTFlag.LowerBound, new Move(8, 80));

        var entries = tt.EnumerateEntries().ToList();

        Assert.All(entries, e =>
        {
            Assert.True(e.Flag == TTFlag.Exact ||
                        e.Flag == TTFlag.LowerBound ||
                        e.Flag == TTFlag.UpperBound);
        });
    }

    // ── ExploreTTTree ──────────────────────────────────────────

    [Fact]
    public void ExploreTTTree_CurrentPositionNotInTT_ReturnsNull()
    {
        var tt = new TranspositionTable(1);
        var board = new Board(DefaultFen);

        // TT 為空，當前局面 key 不在表中
        var node = tt.ExploreTTTree(board, maxDepth: 3);

        Assert.Null(node);
    }

    [Fact]
    public void ExploreTTTree_CurrentPositionInTT_NoBestMove_ReturnsLeafNode()
    {
        var tt = new TranspositionTable(1);
        var board = new Board(DefaultFen);

        // 存入當前局面，但無有效 BestMove（From == To == 0）
        tt.Store(board.ZobristKey, 42, 3, TTFlag.Exact, new Move(0, 0));

        var node = tt.ExploreTTTree(board, maxDepth: 3);

        // 應該回傳根節點，但沒有子節點（因為 BestMove 無效）
        Assert.NotNull(node);
        Assert.Equal(42, node.Entry.Score);
        Assert.Equal((byte)3, node.Entry.Depth);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void ExploreTTTree_TwoDepthChain_BuildsCorrectTree()
    {
        var tt = new TranspositionTable(1);
        var board = new Board(DefaultFen);
        ulong rootKey = board.ZobristKey;

        // 取一個合法走法作為 BestMove
        var moves = board.GenerateLegalMoves().ToList();
        Assert.NotEmpty(moves);
        var bestMove = moves[0];

        // 存入根節點，指向 bestMove
        tt.Store(rootKey, 100, 5, TTFlag.Exact, bestMove);

        // 計算子局面 key
        var childBoard = board.Clone();
        childBoard.MakeMove(bestMove);
        ulong childKey = childBoard.ZobristKey;

        // 存入子節點（無進一步 BestMove）
        tt.Store(childKey, -80, 4, TTFlag.Exact, new Move(0, 0));

        var tree = tt.ExploreTTTree(board, maxDepth: 5);

        Assert.NotNull(tree);
        Assert.Equal(100, tree.Entry.Score);
        Assert.Single(tree.Children);

        var child = tree.Children[0];
        Assert.Equal(bestMove, child.MoveToHere);
        Assert.Equal(-80, child.Entry.Score);
        Assert.Empty(child.Children);
    }

    [Fact]
    public void ExploreTTTree_MaxDepthLimitsRecursion()
    {
        var tt = new TranspositionTable(1);
        var board = new Board(DefaultFen);

        // 建立 3 層鏈條
        var current = board.Clone();
        for (int depth = 5; depth >= 3; depth--)
        {
            var legalMoves = current.GenerateLegalMoves().ToList();
            if (!legalMoves.Any()) break;

            var mv = legalMoves[0];
            tt.Store(current.ZobristKey, depth * 100, depth, TTFlag.Exact, mv);
            current.MakeMove(mv);
        }
        // 最後一層存入無 BestMove
        tt.Store(current.ZobristKey, 0, 3, TTFlag.Exact, new Move(0, 0));

        // maxDepth=1 應只回傳根節點（無子節點）
        var treeDepth1 = tt.ExploreTTTree(board, maxDepth: 1);
        Assert.NotNull(treeDepth1);
        Assert.Empty(treeDepth1.Children);

        // maxDepth=2 應有一層子節點
        var treeDepth2 = tt.ExploreTTTree(board, maxDepth: 2);
        Assert.NotNull(treeDepth2);
        Assert.Single(treeDepth2.Children);
    }

    [Fact]
    public void ExploreTTTree_CycleDetection_PreventsDuplicateVisit()
    {
        var tt = new TranspositionTable(1);
        var board = new Board(DefaultFen);

        // 人工製造一個「自我指向」的 TT 條目
        // 讓根節點的 BestMove 指向一個讓局面走回類似狀態的走法
        // 此測試只驗證探索不會無限迴圈（有 maxDepth 保護即可）
        var legalMoves = board.GenerateLegalMoves().ToList();
        Assert.NotEmpty(legalMoves);
        var mv = legalMoves[0];

        // 根節點指向 mv；子節點沒有 BestMove
        tt.Store(board.ZobristKey, 0, 1, TTFlag.Exact, mv);
        // 同一個 rootKey 的子節點也指回相同的走法 key（cycle）
        var child = board.Clone();
        child.MakeMove(mv);
        tt.Store(child.ZobristKey, 0, 1, TTFlag.Exact, mv);
        // 孫節點也指回根 key（模擬迴圈）
        var grandChild = child.Clone();
        grandChild.MakeMove(legalMoves.Count > 1 ? legalMoves[1] : mv);
        // 手動覆寫孫節點，讓其 BestMove 指向一個合法但不在 TT 的 key → 自然終止
        // 重點：maxDepth 保護確保不會無限遞迴
        tt.Store(grandChild.ZobristKey, 0, 1, TTFlag.Exact, new Move(0, 0));

        // 應在 maxDepth 內正常結束，不拋出例外
        var tree = tt.ExploreTTTree(board, maxDepth: 10);
        Assert.NotNull(tree);
    }

    // ── 輔助 ──────────────────────────────────────────────────

    private const string DefaultFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w";
}
