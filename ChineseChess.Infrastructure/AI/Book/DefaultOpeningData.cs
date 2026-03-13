using ChineseChess.Domain.Entities;
using System.Collections.Generic;

namespace ChineseChess.Infrastructure.AI.Book;

/// <summary>
/// 內建象棋開局庫資料。
/// 以 Board.MakeMove 動態計算 Zobrist key，保證與運行時棋盤完全一致。
///
/// 收錄局面：
///   1. 標準初始局面（紅方第一手）
///   2. 炮二平五後黑方回應
///   3. 馬二進三後黑方回應
///   4. 馬八進七後黑方回應
/// </summary>
public static class DefaultOpeningData
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 棋盤座標（標準初始局面）────────────────────────────────────────
    // Row 7（紅方炮）：col1=64, col7=70
    // Row 9（紅方底線）：R=81 N=82 B=83 A=84 K=85 A=86 B=87 N=88 R=89
    // Row 6（紅方兵）：col0=54 col2=56 col4=58 col6=60 col8=62
    // Row 0（黑方頂線）：r=0 n=1 b=2 a=3 k=4 a=5 b=6 n=7 r=8
    // Row 2（黑方炮）：col1=19, col7=25
    // Row 3（黑方卒）：col0=27 col2=29 col4=31 col6=33 col8=35

    public static OpeningBook Build()
    {
        var positions = new Dictionary<ulong, List<(Move Move, int Weight)>>();

        // ─── 1. 標準初始局面：紅方第一手 ─────────────────────────────────
        var root = new Board(InitialFen);
        var rootKey = root.ZobristKey;

        positions[rootKey] =
        [
            (new Move(64, 67), 30),  // 炮二平五（中炮）
            (new Move(82, 65), 20),  // 馬二進三
            (new Move(88, 69), 20),  // 馬八進七
            (new Move(70, 67), 10),  // 炮八平五
            (new Move(83, 67), 5),   // 相三進五（飛相局）
            (new Move(87, 67), 5),   // 相七進五（飛相局）
            (new Move(56, 47), 5),   // 兵三進一
            (new Move(60, 51), 5),   // 兵七進一（仙人指路）
        ];

        // ─── 2. 炮二平五後黑方回應 ────────────────────────────────────────
        AddBlackResponses(positions, InitialFen, new Move(64, 67),
        [
            (new Move(7, 24), 30),    // 馬8進7（屏風馬）
            (new Move(25, 22), 25),   // 炮8平5（中炮對攻）
            (new Move(1, 20), 20),    // 馬2進3
            (new Move(0, 9), 10),     // 車9進1（Black rook col0,row0 → col0,row1）
            (new Move(33, 42), 15),   // 卒7進1
        ]);

        // ─── 3. 馬二進三後黑方回應 ────────────────────────────────────────
        AddBlackResponses(positions, InitialFen, new Move(82, 65),
        [
            (new Move(7, 24), 30),    // 馬8進7
            (new Move(1, 20), 25),    // 馬2進3
            (new Move(25, 22), 20),   // 炮8平5
            (new Move(33, 42), 15),   // 卒7進1
            (new Move(27, 36), 10),   // 卒9進1
        ]);

        // ─── 4. 馬八進七後黑方回應 ────────────────────────────────────────
        AddBlackResponses(positions, InitialFen, new Move(88, 69),
        [
            (new Move(7, 24), 30),    // 馬8進7
            (new Move(1, 20), 25),    // 馬2進3
            (new Move(25, 22), 20),   // 炮8平5
            (new Move(33, 42), 15),   // 卒7進1
        ]);

        // ─── 5. 飛相局後黑方回應 ─────────────────────────────────────────
        AddBlackResponses(positions, InitialFen, new Move(83, 67),
        [
            (new Move(7, 24), 35),    // 馬8進7
            (new Move(1, 20), 30),    // 馬2進3
            (new Move(25, 22), 20),   // 炮8平5
            (new Move(2, 22), 15),    // 象7進5（Black elephant col2,row0 → col4,row2）
        ]);

        // ─── 建立開局庫 ───────────────────────────────────────────────────
        var book = new OpeningBook();
        foreach (var (key, moves) in positions)
        {
            book.SetEntry(key, moves);
        }
        return book;
    }

    /// <summary>
    /// 在 <paramref name="baseFen"/> 局面上執行 <paramref name="firstMove"/>，
    /// 然後將 <paramref name="responses"/> 作為黑方（或對手）回應加入 <paramref name="positions"/>。
    /// </summary>
    private static void AddBlackResponses(
        Dictionary<ulong, List<(Move Move, int Weight)>> positions,
        string baseFen,
        Move firstMove,
        List<(Move Move, int Weight)> responses)
    {
        var board = new Board(baseFen);
        board.MakeMove(firstMove);
        positions[board.ZobristKey] = responses;
    }
}
