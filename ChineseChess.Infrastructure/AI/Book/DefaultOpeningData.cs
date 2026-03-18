using ChineseChess.Domain.Entities;
using System.Collections.Generic;

namespace ChineseChess.Infrastructure.AI.Book;

/// <summary>
/// 內建象棋開局庫資料。
/// 以「開局線」（Opening Line）驅動建構：定義走法序列後，自動推導所有中間局面。
/// 多條共享前綴的線條會自動合併候選走法並累加權重。
///
/// 收錄局面（~100 個），覆蓋 5 大開局流派前 4–6 手：
///   A. 中炮局（炮二平五：64→67）
///   B. 馬局（馬二進三：82→65 / 馬八進七：88→69）
///   C. 飛相局（相七進五：87→67 / 相三進五：83→67）
///   D. 仙人指路（兵七進一：60→51）
///   E. 左炮局（炮八平五：70→67）
/// </summary>
public static class DefaultOpeningData
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 棋盤座標快速參考（row*9+col，0-89）────────────────────────────
    // Row 9（紅底）: R=81 N=82 B=83 A=84 K=85 A=86 B=87 N=88 R=89
    // Row 7（紅炮）: C=64(c1), C=70(c7)
    // Row 6（紅兵）: 54(c0) 56(c2) 58(c4) 60(c6) 62(c8)
    // Row 3（黑卒）: 27(c0) 29(c2) 31(c4) 33(c6) 35(c8)
    // Row 2（黑炮）: c=19(c1), c=25(c7)
    // Row 0（黑頂）: r=0 n=1 b=2 a=3 k=4 a=5 b=6 n=7 r=8
    // Row 8 全空，象眼不受阻（飛相局合法）

    /// <summary>一條開局線：整體權重 + 從初始局面起的完整走法序列。</summary>
    private readonly record struct OpeningLine(int Weight, Move[] Moves);

    private static OpeningLine Line(int weight, params Move[] moves) => new(weight, moves);
    private static Move M(int from, int to) => new Move(from, to);

    public static OpeningBook Build()
    {
        var book = new OpeningBook();
        AddLines(book, GetCentralCannonLines());
        AddLines(book, GetHorseOpeningLines());
        AddLines(book, GetElephantOpeningLines());
        AddLines(book, GetPawnOpeningLines());
        AddLines(book, GetLeftCannonLines());
        return book;
    }

    /// <summary>
    /// 遍歷開局線，對每個前綴局面加入下一步作為候選走法。
    /// 多條線共享前綴時，相同走法的權重自動累加。
    /// </summary>
    private static void AddLines(OpeningBook book, IEnumerable<OpeningLine> lines)
    {
        foreach (var line in lines)
        {
            var board = new Board(InitialFen);
            foreach (var move in line.Moves)
            {
                book.AddMove(board.ZobristKey, move, line.Weight);
                board.MakeMove(move);
            }
        }
    }

    // ─── A. 中炮局（炮二平五：64→67） ────────────────────────────────────
    private static IEnumerable<OpeningLine> GetCentralCannonLines() =>
    [
        // A1 中炮對屏風馬（車一平二主線）
        Line(25, M(64,67), M(7,24), M(82,65), M(8,7), M(81,82), M(25,26)),
        // A2 中炮對屏風馬（兩頭蛇：紅雙馬同開）
        Line(20, M(64,67), M(7,24), M(88,69), M(1,20), M(56,47)),
        // A3 中炮對屏風馬（炮八平九子變）
        Line(15, M(64,67), M(7,24), M(82,65), M(8,7), M(81,82), M(33,42), M(70,71)),
        // A4 中炮對中炮（順手炮）
        Line(20, M(64,67), M(25,22), M(82,65), M(7,24), M(81,82), M(1,20)),
        // A5 中炮對單提馬（黑車9進1）
        Line(15, M(64,67), M(1,20), M(82,65), M(0,9), M(81,82), M(25,26)),
        // A6 中炮對反宮馬（黑象3進5）
        Line(15, M(64,67), M(1,20), M(82,65), M(2,22), M(56,47), M(7,24)),
        // A7 中炮對卒7進1
        Line(10, M(64,67), M(33,42), M(82,65), M(7,24), M(88,69), M(1,20)),
    ];

    // ─── B. 馬局（馬二進三：82→65 / 馬八進七：88→69） ───────────────────
    private static IEnumerable<OpeningLine> GetHorseOpeningLines() =>
    [
        // B1 右馬對屏風馬（兵三進一）
        Line(20, M(82,65), M(7,24), M(88,69), M(1,20), M(56,47), M(29,38)),
        // B2 右馬對中炮（炮八平五反擊）
        Line(15, M(82,65), M(25,22), M(70,67), M(7,24), M(88,69)),
        // B3 左馬對屏風馬
        Line(15, M(88,69), M(7,24), M(82,65), M(1,20), M(56,47), M(29,38)),
        // B4 左馬對單提馬（兵三進一）
        Line(10, M(88,69), M(1,20), M(56,47), M(7,24), M(82,65)),
    ];

    // ─── C. 飛相局（相七進五：87→67 / 相三進五：83→67） ─────────────────
    // 注意：row8 在初始局面全空，象眼不被封，兩個飛相走法均合法。
    private static IEnumerable<OpeningLine> GetElephantOpeningLines() =>
    [
        // C1 相七進五對飛象（象眼 row8,c5=77 空）
        Line(15, M(87,67), M(6,22), M(82,65), M(7,24), M(88,69), M(1,20)),
        // C2 相七進五對中炮
        Line(10, M(87,67), M(25,22), M(82,65), M(7,24), M(88,69), M(1,20)),
        // C3 相三進五對飛象（象眼 row8,c3=75 空）
        Line(10, M(83,67), M(6,22), M(88,69), M(7,24), M(82,65), M(1,20)),
    ];

    // ─── D. 仙人指路（兵七進一：60→51） ──────────────────────────────────
    private static IEnumerable<OpeningLine> GetPawnOpeningLines() =>
    [
        // D1 仙人指路對卒7進1
        Line(15, M(60,51), M(33,42), M(88,69), M(7,24), M(56,47), M(29,38)),
        // D2 仙人指路對卒3進1
        Line(10, M(60,51), M(29,38), M(88,69), M(1,20), M(64,67), M(7,24)),
        // D3 仙人指路對中炮
        Line(10, M(60,51), M(33,42), M(64,67), M(25,22), M(88,69), M(7,24)),
    ];

    // ─── E. 左炮局（炮八平五：70→67） ────────────────────────────────────
    private static IEnumerable<OpeningLine> GetLeftCannonLines() =>
    [
        // E1 左炮對屏風馬
        Line(15, M(70,67), M(7,24), M(88,69), M(8,7), M(89,88)),
        // E2 左炮對雙馬
        Line(10, M(70,67), M(1,20), M(88,69), M(7,24), M(82,65)),
    ];
}
