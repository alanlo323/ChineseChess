using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Constants;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

/// <summary>
/// Elo 評估對弈服務。
/// 編排引擎 A 與引擎 B 的 N 局自動對弈，計算 Elo 差距。
/// 純 Application 層邏輯：不依賴 Infrastructure 具體實作，透過 IAiEngine 介面操作引擎。
/// </summary>
public class EloMatchService
{
    /// <summary>
    /// 執行完整 N 局對弈評估。
    /// 引擎實例由呼叫端（ViewModel）負責建立與釋放，此方法不管理引擎生命週期。
    /// </summary>
    /// <param name="engineA">引擎 A（被評估方）。</param>
    /// <param name="engineB">引擎 B（參照方）。</param>
    /// <param name="settings">對弈設定。</param>
    /// <param name="ct">取消 Token。</param>
    /// <param name="progress">進度回報介面，每步和每局結束時觸發。</param>
    /// <returns>所有已完成局的結果列表。</returns>
    public async Task<IReadOnlyList<SingleGameResult>> RunMatchAsync(
        IAiEngine engineA,
        IAiEngine engineB,
        EloMatchSettings settings,
        CancellationToken ct,
        IProgress<EloMatchProgress>? progress = null,
        ManualResetEventSlim? pauseSignal = null)
    {
        var results = new List<SingleGameResult>(settings.TotalGames);

        for (int gameNumber = 1; gameNumber <= settings.TotalGames; gameNumber++)
        {
            ct.ThrowIfCancellationRequested();

            // 奇數局 A 執紅，偶數局 A 執黑
            bool engineAPlaysRed = (gameNumber % 2 == 1);
            var redEngine = engineAPlaysRed ? engineA : engineB;
            var blackEngine = engineAPlaysRed ? engineB : engineA;

            var result = await PlaySingleGameAsync(
                redEngine, blackEngine,
                gameNumber, engineAPlaysRed,
                settings, ct, progress, results, pauseSignal);

            results.Add(result);

            // 每局結束後回報統計
            var stats = CalculateStatistics(results);
            progress?.Report(new EloMatchProgress
            {
                CurrentGameNumber = gameNumber,
                TotalGames = settings.TotalGames,
                EngineAPlaysRed = engineAPlaysRed,
                CurrentMoveCount = result.TotalMoves,
                CurrentFen = result.FinalFen,
                LastGameResult = result,
                RunningStats = stats
            });
        }

        return results;
    }

    /// <summary>執行單局對弈，回傳結果。</summary>
    private async Task<SingleGameResult> PlaySingleGameAsync(
        IAiEngine redEngine,
        IAiEngine blackEngine,
        int gameNumber,
        bool engineAPlaysRed,
        EloMatchSettings settings,
        CancellationToken ct,
        IProgress<EloMatchProgress>? progress,
        IReadOnlyList<SingleGameResult> resultsSoFar,
        ManualResetEventSlim? pauseSignal)
    {
        var board = new Board();
        board.ParseFen(GameConstants.InitialPositionFen);

        int moveCount = 0;
        int resignConsecutiveCount = 0;
        PieceColor? resignLosingColor = null;

        // 預建兩份 SearchSettings（紅/黑），避免每步重建
        var redSettings = new SearchSettings
        {
            Depth = engineAPlaysRed ? settings.EngineADepth : settings.EngineBDepth,
            TimeLimitMs = engineAPlaysRed ? settings.EngineATimeLimitMs : settings.EngineBTimeLimitMs,
            AllowOpeningBook = true,
            ThreadCount = Environment.ProcessorCount,
            PauseSignal = pauseSignal
        };
        var blackSettings = new SearchSettings
        {
            Depth = engineAPlaysRed ? settings.EngineBDepth : settings.EngineADepth,
            TimeLimitMs = engineAPlaysRed ? settings.EngineBTimeLimitMs : settings.EngineATimeLimitMs,
            AllowOpeningBook = true,
            ThreadCount = Environment.ProcessorCount,
            PauseSignal = pauseSignal
        };

        // 進度節流：每 10 步回報一次（減少 UI 執行緒壓力）
        const int progressReportInterval = 10;

        // 預先計算一次統計（整局共用，不在每步重算）
        var runningStats = CalculateStatistics(resultsSoFar);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 終止條件偵測（上一步走完後的局面）
            if (moveCount > 0)
            {
                var termination = CheckTermination(board, moveCount, settings, resignConsecutiveCount, resignLosingColor);
                if (termination.HasValue)
                {
                    string fen = board.ToFen();
                    var (reason, winner) = termination.Value;
                    return winner.HasValue
                        ? BuildResult(gameNumber, engineAPlaysRed, winner.Value, reason, moveCount, fen)
                        : BuildDraw(gameNumber, engineAPlaysRed, reason, moveCount, fen);
                }
            }

            if (moveCount >= settings.MaxMovesPerGame)
                return BuildDraw(gameNumber, engineAPlaysRed, TerminationReason.MaxMoves, moveCount, board.ToFen());

            // 搜尋
            var currentEngine = board.Turn == PieceColor.Red ? redEngine : blackEngine;
            var currentColor = board.Turn;
            var searchSettings = currentColor == PieceColor.Red ? redSettings : blackSettings;

            var searchResult = await currentEngine.SearchAsync(board, searchSettings, ct);

            if (searchResult.BestMove == default)
                return BuildDraw(gameNumber, engineAPlaysRed, TerminationReason.MaxMoves, moveCount, board.ToFen());

            // 認輸偵測
            if (searchResult.Score <= -settings.ResignThresholdCp)
            {
                if (resignLosingColor == currentColor)
                    resignConsecutiveCount++;
                else
                {
                    resignLosingColor = currentColor;
                    resignConsecutiveCount = 1;
                }
            }
            else
            {
                resignConsecutiveCount = 0;
                resignLosingColor = null;
            }

            board.MakeMove(searchResult.BestMove);
            moveCount++;

            // 節流進度回報
            if (progress != null && moveCount % progressReportInterval == 0)
            {
                progress.Report(new EloMatchProgress
                {
                    CurrentGameNumber = gameNumber,
                    TotalGames = settings.TotalGames,
                    EngineAPlaysRed = engineAPlaysRed,
                    CurrentMoveCount = moveCount,
                    CurrentFen = board.ToFen(),
                    LastGameResult = null,
                    RunningStats = runningStats
                });
            }
        }
    }

    /// <summary>
    /// 偵測對局終止條件。回傳 null 表示繼續；否則回傳 (原因, 勝方)，勝方為 null 表示和棋。
    /// </summary>
    private static (TerminationReason reason, PieceColor? winner)? CheckTermination(
        Board board, int moveCount, EloMatchSettings settings,
        int resignConsecutiveCount, PieceColor? resignLosingColor)
    {
        var loser = board.Turn;
        var winner = loser == PieceColor.Red ? PieceColor.Black : PieceColor.Red;

        if (board.IsCheckmate(loser))
            return (TerminationReason.Checkmate, winner);

        if (board.IsStalemate(loser))
            return (TerminationReason.Stalemate, winner);

        if (board.IsDrawByRepetition())
            return (TerminationReason.DrawByRepetition, null);

        if (board.IsDrawByNoCapture(settings.NoCaptureDrawLimit))
            return (TerminationReason.DrawByNoCapture, null);

        if (board.IsDrawByInsufficientMaterial())
            return (TerminationReason.DrawByInsufficientMaterial, null);

        if (resignLosingColor.HasValue && resignConsecutiveCount >= settings.ResignConsecutiveMoves)
        {
            var resignWinner = resignLosingColor.Value == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
            return (TerminationReason.Resignation, resignWinner);
        }

        return null;
    }

    // ── 輔助建構 ──

    private static SingleGameResult BuildResult(
        int gameNumber, bool engineAPlaysRed,
        PieceColor winner, TerminationReason reason,
        int totalMoves, string fen)
    {
        GameOutcome outcome;
        if (winner == PieceColor.Red)
            outcome = engineAPlaysRed ? GameOutcome.EngineAWin : GameOutcome.EngineBWin;
        else
            outcome = engineAPlaysRed ? GameOutcome.EngineBWin : GameOutcome.EngineAWin;

        return new SingleGameResult
        {
            GameNumber = gameNumber,
            EngineAPlaysRed = engineAPlaysRed,
            Outcome = outcome,
            Reason = reason,
            TotalMoves = totalMoves,
            FinalFen = fen
        };
    }

    private static SingleGameResult BuildDraw(
        int gameNumber, bool engineAPlaysRed,
        TerminationReason reason, int totalMoves, string fen)
        => new()
        {
            GameNumber = gameNumber,
            EngineAPlaysRed = engineAPlaysRed,
            Outcome = GameOutcome.Draw,
            Reason = reason,
            TotalMoves = totalMoves,
            FinalFen = fen
        };

    // ── Elo 計算 ──

    /// <summary>
    /// 從結果列表計算 Elo 統計數據。
    /// </summary>
    public static EloMatchStatistics CalculateStatistics(IReadOnlyList<SingleGameResult> results)
    {
        if (results.Count == 0)
            return new EloMatchStatistics();

        int wins = 0, losses = 0, draws = 0;
        long totalMoves = 0;

        foreach (var r in results)
        {
            totalMoves += r.TotalMoves;
            switch (r.Outcome)
            {
                case GameOutcome.EngineAWin: wins++; break;
                case GameOutcome.EngineBWin: losses++; break;
                case GameOutcome.Draw: draws++; break;
            }
        }

        int n = results.Count;
        double score = (wins + draws * 0.5) / n;
        double eloDiff = CalculateEloDifference(score);
        var (ciLow, ciHigh) = n > 1
            ? CalculateConfidenceInterval(wins, draws, losses, score, n)
            : (eloDiff, eloDiff);

        return new EloMatchStatistics
        {
            GamesPlayed = n,
            EngineAWins = wins,
            EngineBWins = losses,
            Draws = draws,
            EngineAWinRate = (double)wins / n,
            EngineAScore = score,
            EloDifference = eloDiff,
            EloConfidenceLow = ciLow,
            EloConfidenceHigh = ciHigh,
            AverageGameLength = (double)totalMoves / n,
            IsLowSampleWarning = n < 30
        };
    }

    /// <summary>
    /// 從得分率計算 Elo 差距。
    /// 公式：eloDiff = -400 × log₁₀(1/score - 1)
    /// Score = 0.5 → Elo = 0；Score > 0.5 → Elo > 0（A 較強）。
    /// </summary>
    private static double CalculateEloDifference(double score)
    {
        // 鉗位避免 log(0) 或除以零
        score = Math.Clamp(score, 0.001, 0.999);
        double elo = -400.0 * Math.Log10(1.0 / score - 1.0);
        // 鉗位至合理範圍
        return Math.Clamp(elo, -999.0, 999.0);
    }

    /// <summary>
    /// 計算 Elo 差距的 95% 信賴區間（Wald 三項式近似法）。
    /// Var(score) ≈ [w×(1-s)² + d×(0.5-s)² + l×(0-s)²] / (n-1)
    /// SE = √(Var / n)
    /// </summary>
    private static (double low, double high) CalculateConfidenceInterval(
        int wins, int draws, int losses, double score, int n)
    {
        // 使用原始計數（非比例），正確的 Wald 三項式方差
        double variance = (wins  * Math.Pow(1.0 - score, 2)
                         + draws * Math.Pow(0.5 - score, 2)
                         + losses * Math.Pow(0.0 - score, 2))
                         / (n - 1);

        double se = Math.Sqrt(variance / n);

        double scoreLow = score - 1.96 * se;
        double scoreHigh = score + 1.96 * se;

        return (CalculateEloDifference(scoreLow), CalculateEloDifference(scoreHigh));
    }
}
