# 實作計劃：AI 引擎多面向改進

## 任務類型
- [x] Backend（AI 引擎 / 搜尋邏輯）

## 背景分析

### 已確認的現狀

| 問題 | 位置 | 現狀 |
|------|------|------|
| 無吃子和局未納入搜尋終止 | `SearchWorker.Negamax()` L194 | 只有 `IsDrawByRepetition(threshold:2)`，缺少 `IsDrawByNoCapture()` |
| PV 輸出 | `SearchEngine.SearchAsync()` L251 | 主搜尋已有 `BuildPrincipalVariation`，但 helper worker 不輸出 PV |
| `GenerateLegalMoves` + `IsCheck` 效能 | `Board.GenerateLegalMoves()` / `IsSquareAttacked()` | O(legal_moves × board_scan)，每層大量重複掃描 |
| 並行策略 | `EvaluateMovesParallelAsync()` L364 | 直接用 `Environment.ProcessorCount`，每個走法建立獨立 Task |
| 統計資訊不足 | `SearchResult` / `SearchProgress` | 有節點數和 NPS，缺少 TT 命中率、剪枝比例 |

---

## 實作步驟

### Phase A：高優先快速修復（1-2天）

#### A1. 納入無吃子和局終止條件（`SearchWorker.cs`）

**目的**：避免在必和局面中浪費節點繼續搜尋。

**修改位置**：`SearchWorker.Negamax()` 的和局早返部分（L193-L197）

**現有程式碼**：
```csharp
// 2. 重覆局面偵測（搜尋中用 2 次閾值視為和棋，回傳 0）
if (ply > 0 && board.IsDrawByRepetition(threshold: 2))
{
    return 0;
}
```

**修改後**：
```csharp
// 2. 和局早返（重覆局面 OR 無吃子超限）
if (ply > 0 && (board.IsDrawByRepetition(threshold: 2) || board.IsDrawByNoCapture()))
{
    return 0;
}
```

**影響範圍**：`SearchWorker.cs` 一行，無其他依賴變更。

**注意事項**：
- `ply > 0` 條件保留，根節點不做此截止（讓 root 節點正確回傳最佳著法）
- `IsDrawByNoCapture()` 預設 limit=60，與 `Board.HalfMoveClock` 一致

---

#### A2. 補全 TT 統計輸出（`TranspositionTable.cs` + `SearchResult` / `SearchProgress`）

**目的**：讓 TT hit / fail-high / fail-low 比例可觀察，找出搜尋效率瓶頸。

**步驟**：

1. 在 `TranspositionTable` 加入 `long ttHits`、`long ttMisses`、`long storeCalls` 計數（`Interlocked.Increment`）
2. 在 `TTStatistics` 中新增 `HitRate`（hits / (hits + misses)）
3. 在 `SearchProgress` 新增 `TtHitRate` 欄位
4. 在 `SearchEngine.ReportProgress` 中從 `tt.GetStatistics().HitRate` 填充此欄位
5. 在 `GameService.FormatThinkingProgress` 中加入 TT 命中率顯示

**修改檔案**：
- `ChineseChess.Infrastructure/AI/Search/TranspositionTable.cs`
- `ChineseChess.Application/Interfaces/IAiEngine.cs`（`SearchProgress` 定義若在此）
- `ChineseChess.Application/Services/GameService.cs`

---

### Phase B：中優先效能提升（3-5天）

#### B1. 優化 `GenerateLegalMoves` 的合法性檢查

**目的**：減少 `O(legal_moves × board_scan)` 的 IsCheck 呼叫次數。

**技術方向**：增量式快速過濾，分兩類走法處理：

**分類1 — 「安全走法」（不需完整 IsCheck）**：
- 不是沿著攻擊線移動的棋子（非滑行子，不影響王的直線安全）
- 不是被絕對釘住的棋子（只有車、炮、兵的直線可以釘住）

**分類2 — 「可疑走法」（仍需完整 IsCheck）**：
- 王自己的移動
- 任何吃子走法（可能解除阻擋）
- 被直線子（車/炮）沿同一方向走的棋子

**實作計劃**：

1. 在 `Board` 加入 `GetKingIndex(PieceColor)` 的 cache（走法後更新）或利用現有掃描
2. 加入 `IsAbsolutelyPinned(int fromIndex, int kingIndex)` 判斷棋子是否被絕對釘住
3. 在 `GenerateLegalMoves` 中：
   - 若移動棋子不是王、且未被絕對釘住、且是非王方向 → 跳過 IsCheck 直接加入合法
   - 否則執行完整 MakeMove/IsCheck/UnmakeMove

**虛擬碼**：
```csharp
public IEnumerable<Move> GenerateLegalMoves()
{
    int kingIndex = GetKingIndex(turn);
    var legalMoves = new List<Move>();

    foreach (var move in GeneratePseudoLegalMoves())
    {
        // 王的移動：必須完整驗證
        if (pieces[move.From].Type == PieceType.King)
        {
            if (IsMoveKingSafe(move)) legalMoves.Add(move);
            continue;
        }

        // 快速路徑：若此棋子未被釘住，且不影響王的安全，直接加入
        if (!IsAbsolutelyPinned(move.From, kingIndex) && !MightExposeKing(move, kingIndex))
        {
            legalMoves.Add(move);
            continue;
        }

        // 慢速路徑：完整驗證
        MakeMove(move);
        if (!IsCheck(turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red)) // 注意翻轉
            legalMoves.Add(move);
        UnmakeMove(move);
    }
    return legalMoves;
}
```

**修改檔案**：`Board.cs`（僅限 `GenerateLegalMoves` 及輔助方法）

**測試要點**：
- 所有現有 Board 測試應繼續通過（行為等價）
- 加入邊界測試：王被將軍時的合法著法不能遺漏

---

#### B2. 優化並行提示評估（`SearchEngine.cs`）

**目的**：減少 Task 建立開銷，以 `SearchSettings.ThreadCount` 統一控制並行度。

**現有問題**：
```csharp
int threadCount = Math.Min(total, Environment.ProcessorCount);
var tasks = moves.Select(move => Task.Run(() => { ... })).ToArray();
```
每個走法建立一個 Task，走法多時產生大量短任務。

**修改方向**：改為基於固定執行緒數的分批排程（類 work-stealing）：

```csharp
private async Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesParallelAsync(
    IBoard board, IReadOnlyList<Move> moves, int depth,
    int threadCount, // 新增參數，由 SearchSettings.ThreadCount 傳入
    CancellationToken ct, IProgress<string>? progress)
{
    // 將 moves 分成 threadCount 個批次
    int batchSize = (moves.Count + threadCount - 1) / threadCount;
    var batches = moves.Chunk(batchSize).ToArray();

    var results = new ConcurrentBag<(Move Move, int Score)>();
    var tasks = batches.Select(batch => Task.Factory.StartNew(() =>
    {
        var noopPause = new ManualResetEventSlim(true);
        foreach (var move in batch)
        {
            if (ct.IsCancellationRequested) break;
            var clonedBoard = board.Clone();
            clonedBoard.MakeMove(move);
            var worker = new SearchWorker(clonedBoard, evaluator, tt, ct, ct, noopPause);
            int score = -worker.SearchSingleDepth(depth - 1);
            results.Add((move, score));
        }
    }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

    await Task.WhenAll(tasks);
    // ... 排序並回傳
}
```

**修改檔案**：`SearchEngine.cs`（`EvaluateMovesParallelAsync`）

**helper worker 彈性深度**：
- 在 `SearchEngine.SearchAsync` 中，目前 helper depth = `settings.Depth + 1 + (i % 2)`
- 建議：加入時間保護，若 `settings.TimeLimitMs < 500`，helper depth 不超過 `settings.Depth`

---

### Phase C：長期改善（未定時間）

#### C1. 評估函數精進

這是棋力最核心的提升路徑，建議分多個獨立 PR 漸進推進：

**C1.1 馬腳阻擋懲罰**
- 馬每個移動方向有一個「腳位」
- 若腳位被佔，該方向被封死 → 在評估中計算每匹馬的可用方向數

**C1.2 炮的威脅性評估**
- 炮需要一個「炮台」（中間有一顆棋子）才能攻擊
- 評估：（炮攻擊範圍內高價值目標數）× 係數

**C1.3 王的安全性**
- 計算王附近的守衛資源（士象完整性）
- 敵方攻擊子靠近王的距離懲罰

**修改檔案**：`HandcraftedEvaluator.cs`（每個子功能獨立 PR）

---

## 關鍵檔案總覽

| 檔案 | 操作 | 對應 Phase |
|------|------|-----------|
| `ChineseChess.Infrastructure/AI/Search/SearchWorker.cs:L194` | 修改：加入 `IsDrawByNoCapture()` 條件 | A1 |
| `ChineseChess.Infrastructure/AI/Search/TranspositionTable.cs` | 修改：加入命中率計數器 | A2 |
| `ChineseChess.Application/Configuration/SearchProgress.cs`（待確認路徑） | 修改：加入 `TtHitRate` 欄位 | A2 |
| `ChineseChess.Application/Services/GameService.cs:L963` | 修改：`FormatThinkingProgress` 加入 TT 命中率顯示 | A2 |
| `ChineseChess.Domain/Entities/Board.cs:L358-L375` | 修改：`GenerateLegalMoves` 加入快速路徑 | B1 |
| `ChineseChess.Infrastructure/AI/Search/SearchEngine.cs:L360-L413` | 修改：`EvaluateMovesParallelAsync` 改為批次排程 | B2 |
| `ChineseChess.Infrastructure/AI/Evaluators/HandcraftedEvaluator.cs` | 修改：加入戰術評估函數 | C1 |

---

## 風險與緩解措施

| 風險 | 緩解 |
|------|------|
| B1 的快速路徑誤判，導致漏掉非法著法（將軍情況） | 新增 Board 的完整合法著法測試（所有象棋問題局面），Phase B1 需通過 1000+ 局面驗證 |
| A2 的統計計數造成 TT thread-safe 問題 | 使用 `Interlocked.Increment` 而非普通 `++` |
| B2 並行重構造成 hint 結果不穩定 | 在 Phase B2 之前/之後分別 benchmark，確保評分一致性 |
| helper worker 彈性深度可能降低棋力 | 新增 `MinHelperDepth` 設定（不低於 `settings.Depth`）|

---

## 建議的新增測試

| 測試場景 | 對應 Phase | 優先級 |
|---------|-----------|--------|
| 搜尋中 `IsDrawByNoCapture` 早停（HalfMoveClock >= 60 時，Negamax 應回傳 0） | A1 | 高 |
| TT 統計計數準確性（已知命中次數與 `HitRate` 一致） | A2 | 中 |
| `GenerateLegalMoves` 等價性（原始與優化版本對同一局面產生相同著法集合） | B1 | 高 |
| `EvaluateMovesParallelAsync` 與 `EvaluateMovesSequentialAsync` 結果一致性 | B2 | 中 |
| 將軍局面下快速路徑不遺漏合法著法 | B1 | 高 |

---

## 實作順序建議

```
A1（1-2小時）→ A2（半天）→ B2（1天）→ B1（2-3天，需充分測試）→ C1（多個 PR）
```

A1 最小改動且效益明確，建議優先實作。

---

## 備註

- **PV 狀態**：`SearchEngine.SearchAsync()` 已在 L251 建立 PV 並填入 `result.PvLine`，`GameService.StoreLatestHint()` 也已正確儲存 `PvLine`。PV 在主搜尋中已實作，不需額外修改。
- **並行 hint**：`EvaluateMovesAsync` 回傳的 `MoveEvaluation` 目前不包含 PV，若需要每個走法的子 PV 需額外工程。

---

*計劃生成日期：2026-03-11*
