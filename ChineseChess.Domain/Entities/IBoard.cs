using System.Collections.Generic;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Domain.Entities;

public interface IBoard
{
    Piece GetPiece(int index);
    PieceColor Turn { get; }
    ulong ZobristKey { get; }
    int HalfMoveClock { get; }

    /// <summary>本局已走的總步數（含紅黑雙方）。</summary>
    int MoveCount { get; }

    // 走法產生
    IEnumerable<Move> GenerateLegalMoves();

    /// <summary>只產生吃子著法（目標格有對方棋子），已過濾合法性。</summary>
    IEnumerable<Move> GenerateCaptureMoves();

    /// <summary>只產生安靜著法（目標格為空），已過濾合法性。</summary>
    IEnumerable<Move> GenerateQuietMoves();

    // 狀態變更
    void MakeMove(Move move);
    void UnmakeMove(Move move);
    void MakeNullMove();
    void UnmakeNullMove();
    bool TryGetLastMove(out Move move);

    // 狀態查詢
    bool IsCheck(PieceColor color);
    bool IsCheckmate(PieceColor color);

    // 和棋判定
    bool IsDrawByRepetition(int threshold = 3);
    bool IsDrawByNoCapture(int limit = 120);
    bool IsDrawByInsufficientMaterial();
    bool IsDraw();

    /// <summary>
    /// 檢查最近 n 個半步的 Zobrist 雜湊序列中，是否有任何局面重複出現。
    /// 用於 ProbCut 等前向剪枝的重複風險守衛。
    /// </summary>
    bool IsAnyRepetitionInLastN(int n);

    /// <summary>
    /// 輕量長將模式偵測：同一方最近 3 步都是將軍且無吃子。
    /// 用於搜尋引擎的長將懲罰評分（非完整 WXF 裁決）。
    /// </summary>
    bool IsLikelyPerpetualCheck();

    /// <summary>
    /// 覆寫 MakeMove 放入的預設值，記錄當前對手是否被將軍。
    /// 應在真實走棋（非搜尋路徑）後呼叫，以支援長將偵測。
    /// </summary>
    void RecordCheckAfterMove();

    // 擺棋模式：直接設定棋子與行棋方（不記錄走法歷史）
    void SetPiece(int index, Piece piece);
    void SetTurn(PieceColor color);
    void ClearAll();

    // 工具方法
    string ToFen();
    IBoard Clone();
}
