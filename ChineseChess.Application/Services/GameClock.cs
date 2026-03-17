using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Enums;
using System;

namespace ChineseChess.Application.Services;

/// <summary>
/// 棋鐘實作：管理紅黑雙方剩餘時間的狀態機。
/// 支援假時鐘注入（Func&lt;DateTime&gt; getNow）以利測試。
/// 狀態：Stopped → Running → Paused → Running → Stopped
/// </summary>
public sealed class GameClock : IGameClock
{
    private readonly TimeSpan timePerPlayer;
    private readonly Func<DateTime> getNow;

    // 雙方剩餘時間（不含當前正在計時的部分）
    private TimeSpan redRemaining;
    private TimeSpan blackRemaining;

    // 目前是否在計時中
    private bool isRunning;

    // 目前計時中的玩家
    private PieceColor? activePlayer;

    // 本輪計時開始的時間點
    private DateTime turnStartedAt;

    // 是否暫停中
    private bool isPaused;

    // 暫停開始的時間點
    private DateTime pauseStartedAt;

    // 超時事件是否已觸發（防止重複觸發）
    private bool timeoutFired;

    public event EventHandler<PieceColor>? OnTimeout;

    public GameClock(TimeSpan timePerPlayer, Func<DateTime>? getNow = null)
    {
        this.timePerPlayer = timePerPlayer;
        this.getNow = getNow ?? (() => DateTime.UtcNow);
        redRemaining = timePerPlayer;
        blackRemaining = timePerPlayer;
    }

    public TimeSpan RedRemaining
    {
        get
        {
            if (isRunning && !isPaused && activePlayer == PieceColor.Red)
            {
                var elapsed = getNow() - turnStartedAt;
                var remaining = redRemaining - elapsed;
                return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }
            return redRemaining < TimeSpan.Zero ? TimeSpan.Zero : redRemaining;
        }
    }

    public TimeSpan BlackRemaining
    {
        get
        {
            if (isRunning && !isPaused && activePlayer == PieceColor.Black)
            {
                var elapsed = getNow() - turnStartedAt;
                var remaining = blackRemaining - elapsed;
                return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }
            return blackRemaining < TimeSpan.Zero ? TimeSpan.Zero : blackRemaining;
        }
    }

    public bool IsRunning => isRunning;

    public PieceColor? ActivePlayer => activePlayer;

    /// <summary>
    /// 開始計時，firstPlayer 方先行。
    /// </summary>
    public void Start(PieceColor firstPlayer)
    {
        if (firstPlayer == PieceColor.None) return;

        redRemaining = timePerPlayer;
        blackRemaining = timePerPlayer;
        isRunning = true;
        isPaused = false;
        activePlayer = firstPlayer;
        turnStartedAt = getNow();
        timeoutFired = false;
    }

    /// <summary>
    /// 停止當前方計時，切換至另一方。
    /// </summary>
    public void SwitchTurn()
    {
        if (!isRunning || activePlayer == null) return;

        // 記錄當前方消耗的時間
        CommitCurrentTurnTime();

        // 切換到另一方
        activePlayer = activePlayer == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        turnStartedAt = getNow();
    }

    /// <summary>
    /// 凍結倒計時（記錄暫停時刻）。
    /// </summary>
    public void Pause()
    {
        if (!isRunning || isPaused) return;

        // 先提交目前消耗的時間
        CommitCurrentTurnTime();
        isPaused = true;
        pauseStartedAt = getNow();
    }

    /// <summary>
    /// 從暫停點繼續（補上暫停的時間）。
    /// </summary>
    public void Resume()
    {
        if (!isRunning || !isPaused) return;

        isPaused = false;
        // 重新設定本輪開始時間點為「現在」（暫停期間不計）
        turnStartedAt = getNow();
    }

    /// <summary>
    /// 清除所有計時狀態。
    /// </summary>
    public void Stop()
    {
        if (!isRunning) return;

        // 提交已計時的時間
        if (!isPaused)
        {
            CommitCurrentTurnTime();
        }

        isRunning = false;
        isPaused = false;
        activePlayer = null;
    }

    /// <summary>
    /// 手動觸發計時器檢查（測試用）。
    /// 正式環境由 DispatcherTimer 每秒呼叫；測試中由假時鐘推進後手動呼叫。
    /// </summary>
    public void Tick()
    {
        if (!isRunning || isPaused || timeoutFired) return;

        var remaining = activePlayer == PieceColor.Red ? RedRemaining : BlackRemaining;
        if (remaining <= TimeSpan.Zero && activePlayer.HasValue)
        {
            timeoutFired = true;
            isRunning = false;
            OnTimeout?.Invoke(this, activePlayer.Value);
        }
    }

    /// <summary>
    /// 將當前輪次的已計時時間提交到剩餘時間欄位中。
    /// 呼叫後 turnStartedAt 失效，呼叫端需重設。
    /// </summary>
    private void CommitCurrentTurnTime()
    {
        if (activePlayer == null || isPaused) return;

        var elapsed = getNow() - turnStartedAt;
        if (activePlayer == PieceColor.Red)
        {
            redRemaining -= elapsed;
        }
        else
        {
            blackRemaining -= elapsed;
        }
    }
}
