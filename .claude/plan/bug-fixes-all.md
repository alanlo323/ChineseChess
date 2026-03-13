# Implementation Plan: Bug Fixes (High → Low)

## Task Type
- [x] Backend (GameService, Infrastructure)
- [x] Frontend (WPF ViewModels)
- [x] Fullstack

---

## 修復順序與技術方案

### Phase 1 — High Priority

#### Fix #22：async void OnSquareClick 加 try/catch
**檔案**: `ChineseChess.WPF/ViewModels/ChessBoardViewModel.cs:263`

**方案**: 用 try/catch 包裹整個方法體，異常時透過 `GameMessage` 或 `StatusMessage` 顯示訊息，避免 unhandled exception 導致 app 崩潰。

```csharp
private async void OnSquareClick(object? param)
{
    try
    {
        // 原有邏輯不變
        ...
    }
    catch (Exception ex)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            System.Windows.MessageBox.Show($"操作失敗：{ex.Message}", "錯誤"));
    }
}
```

---

#### Fix #7：AiVsAi fire-and-forget 例外處理
**檔案**: `ChineseChess.Application/Services/GameService.cs:424`

**方案**: 將 `_ = RunAiSearchAsync(...)` 改為附加 `.ContinueWith` 在 Faulted 狀態時記錄或發送 GameMessage。

```csharp
// 舊
_ = RunAiSearchAsync(applyBestMove: true);

// 新
RunAiSearchAsync(applyBestMove: true)
    .ContinueWith(t =>
        GameMessage?.Invoke($"AiVsAi 搜尋錯誤：{t.Exception?.InnerException?.Message}"),
        TaskContinuationOptions.OnlyOnFaulted);
```

---

### Phase 2 — Medium Priority

#### Fix #11：LoadBookmark 重設遊戲結束狀態
**檔案**: `ChineseChess.Application/Services/GameService.cs:796`

**方案**: 在 `board.ParseFen(fen)` 之後加入狀態重設。

```csharp
public void LoadBookmark(string name)
{
    var fen = bookmarkManager.GetBookmark(name);
    if (fen != null)
    {
        board.ParseFen(fen);
        isGameOver = false;
        pendingAiDrawOffer = false;
        isDrawOfferProcessed = false;
        ClearLatestHint();
        NotifyUpdate();
    }
}
```

---

#### Fix #12：Undo 重設遊戲結束狀態
**檔案**: `ChineseChess.Application/Services/GameService.cs:783`

**方案**: 在 `if (didUndo)` 區塊內加入 `isGameOver = false;`。

```csharp
if (didUndo)
{
    isGameOver = false;  // 新增：悔棋後允許繼續走棋
    NotifyUpdate();
}
```

---

#### Fix #8：isThinking 改用 Interlocked（原子 check-then-act）
**檔案**: `ChineseChess.Application/Services/GameService.cs:25, 316-317, 409`

**方案**: 將 `volatile bool isThinking` 改為 `int isThinkingFlag`（0/1），並用 `Interlocked.CompareExchange` 進行原子設定，避免競態條件。

```csharp
// 欄位改為 int (0 = false, 1 = true)
private int isThinkingFlag;

// 公開屬性
public bool IsThinking => Volatile.Read(ref isThinkingFlag) != 0;
private bool isThinking => Volatile.Read(ref isThinkingFlag) != 0;

// 進入搜尋（原子操作）
if (Interlocked.CompareExchange(ref isThinkingFlag, 1, 0) != 0) return null;

// 離開搜尋（finally）
Interlocked.Exchange(ref isThinkingFlag, 0);
```

注意：GameService 其他所有讀取 `isThinking` 的地方（約 10 處）需同步更新為讀取 property 或透過 Volatile.Read。

---

#### Fix #6：StartGameAsync busy-wait 加超時保護
**檔案**: `ChineseChess.Application/Services/GameService.cs:80`

**方案**: 加入超時計數（最多等 5 秒），超時後記錄警告並繼續。

```csharp
var waitCount = 0;
while (isThinking && waitCount < 500) // 最多等 5 秒
{
    await Task.Delay(10);
    waitCount++;
}
// 超時後仍繼續，isThinking 異常時不要卡死
```

---

#### Fix #19：OpenAICompatibleHintExplanationService 實作 IDisposable
**檔案**: `ChineseChess.Infrastructure/AI/Hint/OpenAICompatibleHintExplanationService.cs`

**方案**: 讓具體類別實作 `IDisposable`，加入 ownsHttpClient flag，在無參建構子下設定 `ownsHttpClient = true`，Dispose 時才釋放。

```csharp
public class OpenAICompatibleHintExplanationService : IHintExplanationService, IDisposable
{
    private readonly bool ownsHttpClient;

    public OpenAICompatibleHintExplanationService(HintExplanationSettings settings)
        : this(settings, new HttpClient { Timeout = GetTimeout(settings.TimeoutSeconds) }, ownsHttpClient: true)
    { }

    private OpenAICompatibleHintExplanationService(HintExplanationSettings settings, HttpClient httpClient, bool ownsHttpClient)
    {
        this.settings = settings;
        this.httpClient = httpClient;
        this.ownsHttpClient = ownsHttpClient;
    }

    public void Dispose()
    {
        if (ownsHttpClient) httpClient.Dispose();
    }
}
```

同時讓 `IHintExplanationService` 介面繼承 `IDisposable`，讓 DI 容器可正確釋放。

---

#### Fix #24-26：ViewModels 取消訂閱 GameService 事件
**檔案**:
- `ChineseChess.WPF/ViewModels/ChessBoardViewModel.cs:167`
- `ChineseChess.WPF/ViewModels/ControlPanelViewModel.cs:750`
- `ChineseChess.WPF/ViewModels/MainViewModel.cs:9`

**方案**: 所有 ViewModel 加入 `IDisposable`，在 `Dispose()` 中取消訂閱。

`ChessBoardViewModel` 新增:
```csharp
public void Dispose()
{
    gameService.BoardUpdated -= OnBoardUpdated;
    gameService.HintReady -= OnHintReady;
    gameService.SmartHintReady -= OnSmartHintReady;
}
```

`ControlPanelViewModel.Dispose()` 已存在，補充事件取消訂閱（需先在建構子中將 lambda 改為具名方法）：
- 目前 lambda 無法取消訂閱，需重構為具名私有方法：
  `gameService.GameMessage += msg => ...` → `gameService.GameMessage += OnGameMessage;`
  然後 Dispose 中 `gameService.GameMessage -= OnGameMessage;`

`MainViewModel` 新增 `IDisposable`:
```csharp
public void Dispose()
{
    gameService.HintReady -= OnHintReady;
    gameService.ThinkingProgress -= OnThinkingProgress;
}
```

---

#### Fix #23：EnumerateTTEntries 效能優化
**檔案**: `ChineseChess.WPF/ViewModels/ControlPanelViewModel.cs:642`

**方案**: 加入條目數量上限，若 TT 太大就只取樣前 N 筆。

```csharp
const int MaxDisplayEntries = 5000;
var allEntries = gameService.EnumerateTTEntries();
var entries = allEntries.Take(MaxDisplayEntries).ToList();
bool wasTruncated = entries.Count == MaxDisplayEntries;
if (wasTruncated) sb.AppendLine($"（僅顯示前 {MaxDisplayEntries:N0} 筆，實際更多）");
```

---

### Phase 3 — Low Priority

#### Fix #10：GameService 實作 IDisposable（aiPauseSignal）
**檔案**: `ChineseChess.Application/Services/GameService.cs`

**方案**: `GameService` 實作 `IDisposable`，在 `Dispose()` 中呼叫 `aiPauseSignal.Dispose()`，並取消訂閱任何持有的資源。

---

#### Fix #18：EvaluateMovesSequentialAsync noopPause 改用 using
**檔案**: `ChineseChess.Infrastructure/AI/Search/SearchEngine.cs:361`

**方案**: 加上 `using` 關鍵字，與 Parallel 版本一致。

```csharp
// 舊
var noopPause = new ManualResetEventSlim(true);

// 新
using var noopPause = new ManualResetEventSlim(true);
```

---

#### Fix #4：UnmakeNullMove 加入 Debug 斷言
**檔案**: `ChineseChess.Domain/Entities/Board.cs:214`

**方案**: 加入 `Debug.Assert` 確保最後一筆歷史確實是 NullMove。

```csharp
public void UnmakeNullMove()
{
    if (history.Count == 0) return;
    Debug.Assert(history.Peek().IsNullMove, "UnmakeNullMove called on non-null-move state");
    var state = history.Pop();
    ...
}
```

---

## 關鍵檔案清單

| 檔案 | 操作 | 說明 |
|------|------|------|
| `ChineseChess.WPF/ViewModels/ChessBoardViewModel.cs:263` | 修改 | async void try/catch + IDisposable |
| `ChineseChess.Application/Services/GameService.cs:424` | 修改 | fire-and-forget 例外 |
| `ChineseChess.Application/Services/GameService.cs:796` | 修改 | LoadBookmark 重設狀態 |
| `ChineseChess.Application/Services/GameService.cs:783` | 修改 | Undo 重設狀態 |
| `ChineseChess.Application/Services/GameService.cs:25,316,409` | 修改 | Interlocked isThinking |
| `ChineseChess.Application/Services/GameService.cs:80` | 修改 | busy-wait 超時 |
| `ChineseChess.Infrastructure/AI/Hint/OpenAICompatibleHintExplanationService.cs` | 修改 | IDisposable + ownsHttpClient |
| `ChineseChess.Application/Interfaces/IHintExplanationService.cs` | 修改 | 繼承 IDisposable |
| `ChineseChess.WPF/ViewModels/ControlPanelViewModel.cs` | 修改 | 事件取消訂閱（具名方法重構） |
| `ChineseChess.WPF/ViewModels/MainViewModel.cs` | 修改 | IDisposable + 事件取消訂閱 |
| `ChineseChess.WPF/ViewModels/ControlPanelViewModel.cs:642` | 修改 | TT 枚舉上限 |
| `ChineseChess.Infrastructure/AI/Search/SearchEngine.cs:361` | 修改 | noopPause using |
| `ChineseChess.Domain/Entities/Board.cs:214` | 修改 | UnmakeNullMove Debug.Assert |

## 風險與緩解

| 風險 | 緩解 |
|------|------|
| Fix #8（Interlocked）改動面廣 | 搜尋所有 `isThinking` 參考點一一確認後再改 |
| Fix #24-26（lambda 改具名方法）有遺漏風險 | 在 ControlPanelViewModel 中仔細清點所有匿名 lambda 訂閱 |
| Fix #11/#12 重設狀態可能影響提和流程 | 同步重設 `pendingAiDrawOffer` 和 `isDrawOfferProcessed` |
