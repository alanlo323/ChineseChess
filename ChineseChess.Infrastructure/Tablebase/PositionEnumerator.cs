using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using System.Text;

namespace ChineseChess.Infrastructure.Tablebase;

/// <summary>
/// 根據子力組合，窮舉所有合法的棋盤局面。
/// 使用 FEN 字串建構 Board，並過濾非法局面（飛將、非輪到方被將軍）。
/// </summary>
internal static class PositionEnumerator
{
    /// <summary>
    /// 列舉指定子力組合的所有合法局面。
    /// 每個局面含兩個版本：紅先 / 黑先。
    /// </summary>
    public static IEnumerable<Board> Enumerate(PieceConfiguration config)
    {
        var redPieces  = BuildPieceList(PieceColor.Red,   PieceType.King, config.RedExtra);
        var blackPieces = BuildPieceList(PieceColor.Black, PieceType.King, config.BlackExtra);

        foreach (var placement in GeneratePlacements(redPieces, blackPieces))
        {
            foreach (var sideToMove in new[] { PieceColor.Red, PieceColor.Black })
            {
                var board = TryBuildBoard(placement, sideToMove);
                if (board is not null)
                    yield return board;
            }
        }
    }

    // ── 私有輔助方法 ────────────────────────────────────────────────────

    private static List<(PieceColor color, PieceType type)> BuildPieceList(
        PieceColor color, PieceType king, IReadOnlyList<PieceType> extra)
    {
        var list = new List<(PieceColor, PieceType)> { (color, king) };
        foreach (var t in extra) list.Add((color, t));
        return list;
    }

    /// <summary>
    /// 遞迴生成所有棋子的格位組合（避免重複：同類型棋子使用組合而非排列）。
    /// </summary>
    private static IEnumerable<Dictionary<int, Piece>> GeneratePlacements(
        List<(PieceColor, PieceType)> redPieces,
        List<(PieceColor, PieceType)> blackPieces)
    {
        // 重要假設：同顏色同類型的棋子在 allPieces 中必須相鄰排列，
        // 才能讓 minIndexByGroup 的組合去重邏輯正確運作。
        // BuildPieceList 先放全部紅方棋子再放全部黑方棋子，此假設成立。
        // 請勿在不更新去重邏輯的情況下改變棋子排列順序。
        var allPieces = redPieces.Concat(blackPieces).ToList();
        var placement = new Dictionary<int, Piece>();
        return Place(allPieces, 0, placement, minIndexByGroup: []);
    }

    private static IEnumerable<Dictionary<int, Piece>> Place(
        List<(PieceColor color, PieceType type)> pieces,
        int pieceIdx,
        Dictionary<int, Piece> current,
        Dictionary<(PieceColor, PieceType), int> minIndexByGroup)
    {
        if (pieceIdx == pieces.Count)
        {
            yield return new Dictionary<int, Piece>(current);
            yield break;
        }

        var (color, type) = pieces[pieceIdx];
        var validSquares = ValidSquares.GetSquares(type, color);
        var key = (color, type);

        // 同類型棋子使用遞增最小索引，避免重複（A在sq1,B在sq2 ≡ A在sq2,B在sq1）
        // 找出同類棋子在 pieces 中的前一個（若有）
        int minSq = 0;
        if (minIndexByGroup.TryGetValue(key, out int prevMin))
        {
            // 同類棋子的下一個格位必須 > 前一個棋子的格位（組合去重）
            minSq = prevMin + 1;
        }

        foreach (var sq in validSquares)
        {
            if (sq < minSq) continue;               // 去重
            if (current.ContainsKey(sq)) continue;  // 格位已被佔用

            current[sq] = new Piece(color, type);
            var prevValue = minIndexByGroup.GetValueOrDefault(key, -1);
            minIndexByGroup[key] = sq;

            foreach (var result in Place(pieces, pieceIdx + 1, current, minIndexByGroup))
                yield return result;

            current.Remove(sq);
            if (prevValue == -1) minIndexByGroup.Remove(key);
            else minIndexByGroup[key] = prevValue;
        }
    }

    /// <summary>
    /// 從棋子佈局建構 Board，若局面非法則返回 null。
    /// </summary>
    private static Board? TryBuildBoard(Dictionary<int, Piece> placement, PieceColor sideToMove)
    {
        var fen = BuildFen(placement, sideToMove);

        Board board;
        try
        {
            board = new Board(fen);
        }
        catch
        {
            return null;
        }

        // 過濾非法局面：非輪到方不應被將軍
        var notToMove = sideToMove == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        if (board.IsCheck(notToMove))
            return null;

        return board;
    }

    /// <summary>從棋子佈局建構 FEN 字串。</summary>
    private static string BuildFen(Dictionary<int, Piece> placement, PieceColor sideToMove)
    {
        var rows = new string[10];
        for (int row = 0; row < 10; row++)
        {
            var sb = new StringBuilder();
            int empty = 0;
            for (int col = 0; col < 9; col++)
            {
                int idx = row * 9 + col;
                if (placement.TryGetValue(idx, out var piece))
                {
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(piece.ToChar());
                }
                else
                {
                    empty++;
                }
            }
            if (empty > 0) sb.Append(empty);
            rows[row] = sb.ToString();
        }

        var side = sideToMove == PieceColor.Red ? "w" : "b";
        return $"{string.Join('/', rows)} {side} - - 0 1";
    }
}
