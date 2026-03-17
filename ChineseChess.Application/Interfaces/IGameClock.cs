using ChineseChess.Domain.Enums;
using System;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 棋鐘介面：管理雙方剩餘時間，支援暫停/恢復、超時事件。
/// </summary>
public interface IGameClock
{
    TimeSpan RedRemaining { get; }
    TimeSpan BlackRemaining { get; }
    bool IsRunning { get; }
    PieceColor? ActivePlayer { get; }

    /// <summary>某方時間耗盡時觸發，參數為超時方。</summary>
    event EventHandler<PieceColor> OnTimeout;

    /// <summary>開始計時，firstPlayer 方先行。</summary>
    void Start(PieceColor firstPlayer);

    /// <summary>停止當前方計時，切換至另一方。</summary>
    void SwitchTurn();

    /// <summary>凍結倒計時（記錄暫停時刻）。</summary>
    void Pause();

    /// <summary>從暫停點繼續（補上暫停的時間）。</summary>
    void Resume();

    /// <summary>清除所有計時狀態。</summary>
    void Stop();

    /// <summary>
    /// 手動觸發計時器檢查（測試用）。
    /// 檢查剩餘時間是否耗盡，若是則觸發 OnTimeout。
    /// </summary>
    void Tick();
}
