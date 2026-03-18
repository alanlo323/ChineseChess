using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Enums;
using System.Collections.Generic;

namespace ChineseChess.Application.Services;

/// <summary>
/// WXF 重複局面裁決器（純函式，無狀態）。
///
/// 輸入：wxfHistory，首筆為種子條目（Classification=Cancel），其後為實際著法紀錄。
/// 輸出：RepetitionVerdict（None / Draw / RedWins / BlackWins）。
///
/// 裁決規則：
///   1. 同一 ZobristKey 出現 ≥ 3 次 → 觸發重複偵測
///   2. 重複區間內有 Cancel → None（重複鏈被打斷）
///   3. 分析一個完整循環內各方最高違規等級：
///      - 雙方均 Idle → Draw
///      - 紅方更重 → BlackWins（紅方犯規）
///      - 黑方更重 → RedWins（黑方犯規）
///      - 同等級 → Draw
/// </summary>
public static class WxfRepetitionJudge
{
    /// <summary>
    /// 分析 wxfHistory 並回傳 WXF 裁決結果。
    /// history[0] 應為種子條目（初始局面，Classification=Cancel）。
    /// </summary>
    public static RepetitionVerdict Judge(IReadOnlyList<MoveRecord> history)
    {
        int n = history.Count;
        if (n < 5) return RepetitionVerdict.None; // 種子 + 至少 4 步才可能有一個完整循環

        ulong currentKey = history[n - 1].ZobristKey;

        // 向前掃描，找第 2 和第 3 個相同 ZobristKey 的位置
        // （ZobristKey 已含行棋方資訊，相等即代表同局面同行棋方）
        int firstMatch  = -1; // 第 2 次出現（倒數第 2）
        int secondMatch = -1; // 第 3 次出現（倒數第 3，最早）

        for (int i = n - 2; i >= 0; i--)
        {
            if (history[i].ZobristKey != currentKey) continue;

            if (firstMatch == -1)
            {
                firstMatch = i;
            }
            else
            {
                secondMatch = i;
                break;
            }
        }

        // 未找到 3 次重複
        if (secondMatch == -1) return RepetitionVerdict.None;

        // 確認完整重複區間（secondMatch+1 ~ n-1）中沒有 Cancel（吃子或兵前進）
        // Cancel 代表不可逆著法，重複鏈被打斷
        for (int i = secondMatch + 1; i < n; i++)
        {
            if (history[i].Classification == MoveClassification.Cancel)
                return RepetitionVerdict.None;
        }

        // 分析一個完整循環（firstMatch+1 ~ n-1）內各方的最高違規等級
        var redWorst   = MoveClassification.Idle;
        var blackWorst = MoveClassification.Idle;

        for (int i = firstMatch + 1; i < n; i++)
        {
            var cls = history[i].Classification;
            if (cls <= MoveClassification.Idle) continue; // Idle 或 Cancel 不升級違規等級

            if (history[i].Turn == PieceColor.Red)
            {
                if (cls > redWorst) redWorst = cls;
            }
            else
            {
                if (cls > blackWorst) blackWorst = cls;
            }
        }

        // 裁決
        if (redWorst == MoveClassification.Idle && blackWorst == MoveClassification.Idle)
            return RepetitionVerdict.Draw;

        if (redWorst > blackWorst) return RepetitionVerdict.BlackWins;
        if (blackWorst > redWorst) return RepetitionVerdict.RedWins;

        return RepetitionVerdict.Draw; // 同等級 → 和局
    }
}
