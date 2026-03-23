using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Enums;
using System;

namespace ChineseChess.Application.Services;

/// <summary>
/// 棋鐘實作：管理紅黑雙方剩餘時間的狀態機。
/// 支援假時鐘注入（Func&lt;DateTime&gt; getNow）以利測試。
/// 狀態：Stopped → Running → Paused → Running → Stopped
/// 所有公開成員均以 lockObj 保護，可安全跨執行緒（UI / Timer / 讀取）存取。
/// </summary>
public sealed class GameClock : IGameClock
{
    private readonly TimeSpan timePerPlayer;
    private readonly Func<DateTime> getNow;
    private readonly object lockObj = new();

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
            lock (lockObj)
            {
                return GetRemainingInternal(PieceColor.Red);
            }
        }
    }

    public TimeSpan BlackRemaining
    {
        get
        {
            lock (lockObj)
            {
                return GetRemainingInternal(PieceColor.Black);
            }
        }
    }

    // 不加鎖的內部計算，供 lock 區塊內部呼叫；呼叫端必須持有 lockObj
    private TimeSpan GetRemainingInternal(PieceColor color)
    {
        var stored = color == PieceColor.Red ? redRemaining : blackRemaining;
        if (isRunning && !isPaused && activePlayer == color)
        {
            stored -= getNow() - turnStartedAt;
        }
        return stored < TimeSpan.Zero ? TimeSpan.Zero : stored;
    }

    public bool IsRunning
    {
        get { lock (lockObj) { return isRunning; } }
    }

    public PieceColor? ActivePlayer
    {
        get { lock (lockObj) { return activePlayer; } }
    }

    /// <summary>
    /// 開始計時，firstPlayer 方先行。
    /// </summary>
    public void Start(PieceColor firstPlayer)
    {
        if (firstPlayer == PieceColor.None) return;

        lock (lockObj)
        {
            redRemaining = timePerPlayer;
            blackRemaining = timePerPlayer;
            isRunning = true;
            isPaused = false;
            activePlayer = firstPlayer;
            turnStartedAt = getNow();
            timeoutFired = false;
        }
    }

    // 走棋後剩餘時間的最低保障（避免因走棋延遲而立即超時）
    private static readonly TimeSpan MinRemainingAfterMove = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 停止當前方計時，切換至另一方。
    /// 若走棋後剩餘時間不足 <see cref="MinRemainingAfterMove"/>，則補回至該門檻。
    /// </summary>
    public void SwitchTurn()
    {
        lock (lockObj)
        {
            if (!isRunning || activePlayer == null) return;

            // 記錄當前方消耗的時間
            CommitCurrentTurnTimeInternal();

            // 走棋後剩餘不足 5 秒時，補回至 5 秒
            if (activePlayer == PieceColor.Red)
            {
                if (redRemaining < MinRemainingAfterMove)
                    redRemaining = MinRemainingAfterMove;
            }
            else
            {
                if (blackRemaining < MinRemainingAfterMove)
                    blackRemaining = MinRemainingAfterMove;
            }

            // 切換到另一方
            activePlayer = activePlayer == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
            turnStartedAt = getNow();
        }
    }

    /// <summary>
    /// 凍結倒計時（記錄暫停時刻）。
    /// </summary>
    public void Pause()
    {
        lock (lockObj)
        {
            if (!isRunning || isPaused) return;

            // 先提交目前消耗的時間
            CommitCurrentTurnTimeInternal();
            isPaused = true;
            pauseStartedAt = getNow();
        }
    }

    /// <summary>
    /// 從暫停點繼續（補上暫停的時間）。
    /// </summary>
    public void Resume()
    {
        lock (lockObj)
        {
            if (!isRunning || !isPaused) return;

            isPaused = false;
            // 重新設定本輪開始時間點為「現在」（暫停期間不計）
            turnStartedAt = getNow();
        }
    }

    /// <summary>
    /// 清除所有計時狀態。
    /// </summary>
    public void Stop()
    {
        lock (lockObj)
        {
            if (!isRunning) return;

            // 提交已計時的時間
            if (!isPaused)
            {
                CommitCurrentTurnTimeInternal();
            }

            isRunning = false;
            isPaused = false;
            activePlayer = null;
        }
    }

    /// <summary>
    /// 手動觸發計時器檢查（測試用）。
    /// 正式環境由 DispatcherTimer 每秒呼叫；測試中由假時鐘推進後手動呼叫。
    /// 在 lock 內確認超時狀態，lock 外觸發事件（避免 lock 內觸發外部事件造成死鎖）。
    /// </summary>
    public void Tick()
    {
        PieceColor? timedOutPlayer = null;

        lock (lockObj)
        {
            if (!isRunning || isPaused || timeoutFired) return;

            var remaining = GetRemainingInternal(activePlayer!.Value);

            if (remaining <= TimeSpan.Zero && activePlayer.HasValue)
            {
                timeoutFired = true;
                isRunning = false;
                timedOutPlayer = activePlayer.Value;
            }
        }

        // lock 外觸發事件，防止在 lock 持有期間呼叫外部訂閱者造成死鎖
        if (timedOutPlayer.HasValue)
            OnTimeout?.Invoke(this, timedOutPlayer.Value);
    }

    /// <summary>
    /// 將當前輪次的已計時時間提交到剩餘時間欄位中。
    /// 呼叫端必須持有 lockObj。呼叫後 turnStartedAt 失效，呼叫端需重設。
    /// </summary>
    private void CommitCurrentTurnTimeInternal()
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
