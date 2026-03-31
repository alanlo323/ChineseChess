# Chinese Chess — 中文象棋 AI 桌面應用

> 一個以 **Clean Architecture** 設計、功能完整的中文象棋（象棋）WPF 桌面應用。
> 內建高效能 AI 引擎，支援 NNUE 神經網路評估、多執行緒並行搜尋、外部引擎連接，以及 LLM 驅動的走法解析。

**平台**：Windows（WPF） &nbsp;·&nbsp; **語言**：C# &nbsp;·&nbsp; **框架**：.NET 10 &nbsp;·&nbsp; **架構**：Clean Architecture

---

## 目錄

- [功能概覽](#功能概覽)
- [快速開始](#快速開始)
- [遊戲功能詳述](#遊戲功能詳述)
  - [遊戲模式](#遊戲模式)
  - [擺棋模式](#擺棋模式)
  - [棋局歷史與重播](#棋局歷史與重播)
  - [書籤與棋局記錄](#書籤與棋局記錄)
  - [棋鐘計時](#棋鐘計時)
  - [Elo 評估](#elo-評估)
  - [其他操控](#其他操控)
- [AI 引擎架構詳述](#ai-引擎架構詳述)
  - [搜尋框架](#搜尋框架)
  - [並行搜尋：Lazy SMP](#並行搜尋lazy-smp)
  - [轉置表](#轉置表transposition-table)
  - [走法排序](#走法排序)
  - [剪枝與縮減技術](#剪枝與縮減技術)
  - [評估函式](#評估函式)
  - [NNUE 神經網路](#nnue-神經網路)
  - [開局庫](#開局庫opening-book)
  - [殘局表](#殘局表endgame-tablebase)
  - [外部引擎支援](#外部引擎支援ucciuci)
  - [AI 提示與走法解析](#ai-提示與走法解析)
  - [難度設定](#難度設定)
- [架構說明](#架構說明)
- [設定檔](#設定檔)
- [開發指南](#開發指南)

---

## 功能概覽

| 類別 | 功能 |
|---|---|
| 遊戲模式 | 人對人、人對 AI、AI 對 AI |
| 棋盤操作 | 擺棋模式（BoardSetup）、FEN 匯入/匯出 |
| 棋局管理 | 重播（Replay）、書籤（Bookmarks）、棋局記錄（`.ccgame`）|
| AI 引擎 | Negamax + Alpha-Beta + IID + Lazy SMP 並行 |
| 評估函式 | 手工評估（Handcrafted）+ NNUE 神經網路（HalfKAv2_hm）|
| 外部引擎 | UCI / UCCI 協議，支援 Pikafish 等外部程式 |
| AI 解析 | LLM 走法解釋（OpenAI 相容 API）+ 局面分析 |
| 殘局庫 | 倒退分析精確求解（ETB，`.etb` 格式）|
| 裁判規則 | WXF 長將 / 長捉裁決 |
| 計時 | 可選棋鐘模式（每方固定時間）|
| Elo 評估 | 自動 N 局對弈估算兩引擎 Elo 差距（含 95% CI），即時同步棋盤 / 棋譜 / TT 探索 |

---

## 快速開始

### 環境需求

- [.NET 10 SDK](https://dotnet.microsoft.com/) 或以上
- Windows 10 / 11（WPF 僅限 Windows）
- （選用）外部引擎可執行檔，例如 [Pikafish](https://github.com/official-pikafish/Pikafish)

### 建置與執行

```bash
# 複製儲存庫
git clone https://github.com/your-username/ChineseChess.git
cd ChineseChess

# 建置整個解決方案
dotnet build

# 啟動應用程式
dotnet run --project ChineseChess.WPF
```

或直接在 Visual Studio / Rider 中開啟 `ChineseChess.sln`，設定啟動專案為 `ChineseChess.WPF` 後執行。

### LLM 走法解析設定（選用）

在 `ChineseChess.WPF/` 目錄建立 `appsettings.local.json`，填入 API 資訊：

```json
{
  "HintExplanation": {
    "Endpoint": "https://your-llm-endpoint/v1",
    "ApiKey": "your-api-key"
  },
  "GameAnalysis": {
    "IsEnabled": true,
    "Endpoint": "https://your-llm-endpoint/v1",
    "ApiKey": "your-api-key"
  }
}
```

此檔案已在 `.gitignore` 中排除，不會提交至版本控制。

---

## 遊戲功能詳述

### 遊戲模式

應用程式支援三種遊戲模式，可在控制面板（Control Panel）隨時切換：

| 模式 | 說明 |
|---|---|
| **人對人**（Player vs Player） | 兩位玩家輪流在同一台電腦操作 |
| **人對 AI**（Player vs AI） | 玩家選擇執紅或執黑，對戰內建 AI 引擎 |
| **AI 對 AI**（AI vs AI） | 雙方均由 AI 自動對弈，可觀察棋局發展 |

### 擺棋模式

**BoardSetup** 模式讓你從任意局面開始對弈：

- 從棋子選擇盤點選棋子類型與顏色
- **左鍵**在棋盤上放置選定棋子；**右鍵**移除棋子
- 設定行棋方（紅先 / 黑先）
- 支援清空棋盤或重置為標準初始局面
- 確認局面後選擇對局模式開始遊戲

### 棋局歷史與重播

棋譜側邊欄（Move History Panel）記錄整局所有著法：

- 走法以 WXF 記譜格式顯示（如「炮二平五」）
- **進入重播模式**後可自由瀏覽任意步數：
  - 逐步前進 / 後退
  - 直接跳至開局 / 末局
  - 點選棋譜任意步數直接跳轉
- **從當前步繼續**（Continue from Here）：從重播中的任意局面以指定模式接續對弈
- **匯出棋局**（Export `.ccgame`）：儲存完整棋局記錄（JSON 格式）
- **匯入棋局**（Import `.ccgame`）：載入已儲存的棋局並進入重播

### 書籤與棋局記錄

- **書籤**（Bookmarks）：隨時儲存當前局面，可一鍵恢復至任意書籤局面，方便研究變化分支
- **棋局記錄**（`.ccgame`）：儲存完整的局面 FEN 歷程與元資料，可跨次工作階段載入

### 棋鐘計時

可選啟用棋鐘模式（Timed Game）：

- 設定每位玩家的固定時間（分鐘）
- 棋鐘在界面上即時顯示雙方剩餘時間
- 任一方超時即判負
- 啟用棋鐘時，固定時間限制控制項自動隱藏（由棋鐘統一管理時間）

### Elo 評估

**Elo Match** 功能讓你量化兩個引擎的實力差距：自動執行 N 局對弈，從勝 / 負 / 和統計推算 Elo 差距（含 95% 信賴區間）。

#### 引擎選擇

每場對弈可分別為引擎 A、引擎 B 選擇以下類型：

| 類型 | 說明 |
|---|---|
| **手工評估**（Handcrafted） | 使用純手工調教的評估函式 |
| **當前內建**（Current Builtin） | 使用目前載入的引擎（含 NNUE 若已啟用）|
| **外部引擎**（External） | 使用已在「外部引擎」分頁設定的 UCI/UCCI 引擎（如 Pikafish）|

#### 對弈設定

- 總局數（建議 ≥ 30 局以取得可靠 CI）
- 每引擎的搜尋深度與每步時間限制
- 每局最大步數（超過判和）
- 認輸門檻（centipawn）與連續步數

顏色自動交替：奇數局引擎 A 執紅，偶數局引擎 A 執黑，確保結果無色偏。

#### 即時 UI 同步

評估進行中，所有 UI 面板與正常對局完全同步：

| 面板 | 更新內容 |
|---|---|
| **主棋盤** | 每步後即時顯示當前局面（含最後落子高亮）|
| **棋譜側欄** | 以 WXF 格式記錄每步著法，進度條顯示步數 |
| **TT 探索** | 顯示引擎 A 的轉置表分布與探索樹 |
| **AI 分析文字** | 即時輸出引擎搜尋的深度 / 分數 / 節點數等思考進度 |

#### Elo 統計計算

```
Score = (引擎A勝局 + 和局 × 0.5) / 總局數

Elo 差距 = −400 × log₁₀(1 / Score − 1)
  └─ Score = 0.5 → Elo = 0（實力相當）

95% 信賴區間（Wald 三項式近似）：
  Var(Score) = (w×(1-s)² + d×(0.5-s)² + l×(0-s)²) / (n-1)
  CI = Score ± 1.96 × √(Var/n)  →  轉換為 Elo 上下界
```

樣本數 < 30 時顯示「⚠ 樣本不足，CI 僅供參考」警告。

#### 操控

- **開始 / 暫停 / 停止**：全程可隨時暫停或中止評估
- **匯出 CSV**：將逐局結果（局號、執色、勝負、步數、終止原因）匯出為 CSV 檔案

### 其他操控

- **悔棋**（Undo）：撤回最後一步
- **提和**（Draw Offer）：AI 在局面接近平衡時自動提和，玩家可接受或拒絕；設有冷卻機制避免頻繁重複提和
- **WXF 重複局面裁決**：自動偵測長將、長捉並依 WXF 規則裁決
- **MultiPV 提示**：同時顯示多個候選走法（預設 3 個），協助研究替代方案

---

## AI 引擎架構詳述

AI 引擎完整實裝於 `ChineseChess.Infrastructure` 層，以 `IAiEngine` 介面對上層抽象。

### 搜尋框架

核心搜尋以 **Negamax** 架構配合：

- **Alpha-Beta 剪枝**：消除不需搜尋的分支
- **迭代加深（Iterative Deepening, IID）**：從深度 1 逐步加深到目標深度，兼顧時間控制與走法排序品質
- **Aspiration Window**：以前一層搜尋分數為中心設定窄視窗，提升剪枝效率；窗口失敗時自動擴展並重試

### 並行搜尋：Lazy SMP

`SearchWorker` 實作 **Lazy SMP** 架構：

- 多個 Worker 執行緒各持有獨立的棋盤複本、Killer 表與歷史表
- **轉置表（TT）在所有 Worker 間共用**，確保搜尋資訊互相補充
- 支援最多 128 個搜尋執行緒（`ThreadCount` 可設定）
- 每個 Worker 的 NNUE 評估器各自持有獨立增量累加器

### 轉置表（Transposition Table）

- **Zobrist 雜湊**：以 64-bit 雜湊值唯一標識局面
- 自動碰撞率偵測與動態擴容（每次搜尋開始前自動調整）
- 支援**持久化**：
  - 匯出：`.cctt`（Binary 或 JSON 格式）
  - 匯入：從檔案恢復 TT，延續跨次工作階段的搜尋知識
  - 紅黑方可共用同一 TT 或各自獨立（`UseSharedTranspositionTable`）
  - 支援將紅方 TT 複製至黑方初始化（`CopyRedTtToBlackAtStart`）
  - 支援合併雙方 TT（`MergeTranspositionTables`）
- **TT Explorer**：UI 面板可即時瀏覽 TT 內容（最大深度 20 層）

### 走法排序

每個節點的走法按以下優先序排列，以最大化 Beta 截斷效率：

1. **PV 走法**（來自 TT 的最佳走法）
2. **吃子走法**，以 MVV-LVA（Most Valuable Victim – Least Valuable Attacker）排序
3. **Killer 走法**（同深度下曾產生 Beta 截斷的非吃子走法，儲存 2 個）
4. **Countermove Table**：對手上一步的最佳反制走法
5. **History Heuristic**：基於搜尋歷史的走法評分
6. **Continuation History**：基於「對手走至某格之後」的著法評分，更細緻地捕捉棋面脈絡

### 剪枝與縮減技術

| 技術 | 說明 |
|---|---|
| **空著剪枝**（Null-Move Pruning） | 安全局面下跳過一步，以淺層搜尋預估 Beta 截斷可能性 |
| **靜止搜尋**（Quiescence Search） | 在葉節點延伸搜尋所有吃子，穩定局面估值；Delta Pruning 去除無效吃子 |
| **Futility Pruning** | 淺層（深度 ≤ 2）非關鍵節點估值明顯低於 Alpha 時跳過 |
| **LMR**（Late Move Reduction） | 後序著法以預計算對數表縮減搜尋深度；歷史分數可調整縮減量 |
| **IIR**（Internal Iterative Reduction） | TT 無走法參考且深度 ≥ 4 時縮減搜尋深度 1，節省時間 |
| **Singular Extension** | 深度 ≥ 6 時若 TT 最佳走法顯著優於其他走法，延伸該走法搜尋深度 |
| **ProbCut** | 深度 ≥ 5 時以淺層搜尋預篩高價值吃子，快速截斷可能的 Beta 截斷局面 |
| **Check Extension** | 將軍走法延伸 1 層深度，確保將殺計算不被截斷 |

### 評估函式

#### 手工評估（HandcraftedEvaluator）

```
總分 = 材料分 + 位置分（PST，依棋局相位插值）+ 特殊項懲罰/獎勵
```

**材料分（基準值）：**

| 棋子 | 分值 |
|---|---|
| 車（Rook） | 600 |
| 炮（Cannon） | 285 |
| 馬（Horse） | 270 |
| 士/象（Advisor/Elephant） | 120 |
| 兵/卒（Pawn） | 30 + PST 加成 |

**棋局相位插值**：以殘存強子數計算相位（256 = 開局，0 = 殘局），開局完整套用 PST，殘局 PST 減半（材料更重要）。

**特殊評估項：**
- 馬腳封堵懲罰（-10 分/被封腳位）
- 炮直接瞄準將/帥加分（+40）；炮打其他棋子加分（+10）
- 敵車直接壓制將/帥列懲罰（-60 無阻隔；-20 一子阻隔）

#### NNUE 神經網路（NnueEvaluator）

採用 **HalfKAv2_hm** 特徵架構，完整相容 Pikafish `.nnue` 格式：

```
特徵層（FT）  : HalfKAv2 稀疏特徵 → int16[2×1024] 增量累加器
FC0           : SparseInput Affine：uint8[1024] → int32[16]（SqrCReLU + CReLU）
FC1           : Affine：uint8[30] → int32[32]（CReLU）
FC2           : Affine：uint8[32] → int32[1]
輸出          : (PSQT 分 + 位置分) / OutputScale
```

- **增量累加器**：走棋時只更新變動特徵，大幅減少計算量
- **Lazy 評估**：估值差異在邊際（LazyMargin = 200）內時跳過完整推論
- **進攻 Bucket**：依殘存車/馬炮組合分為 4 個 Bucket，提升不同棋子組合的評估精度

### NNUE 神經網路

#### 訓練器

內建完整的 NNUE 訓練流程（`NnueTrainer`）：

- **資料來源**（可選）：
  - `VsHandcrafted`：NNUE 模型對手工引擎自動生成對局
  - `SelfPlay`：NNUE 模型自我對弈生成對局
  - `FromFile`：載入靜態 `.plain` 訓練資料檔
- **訓練演算法**：Adam 優化器，支援動態 Learning Rate
- **生命週期控制**：非同步啟動 / 暫停 / 恢復 / 停止
- **模型儲存**：損失改善時自動回調並觸發匯出

#### 內建模型

`NNUE/` 目錄含多個預訓練模型：
- 標準象棋模型（Elo ~914）
- Pikafish 相容模型
- 自訓練模型（`trained.nnue`）

### 開局庫（Opening Book）

- 格式：自訂 `.bin` 二進位格式（`openingbook.bin`）
- **隨機選擇**（`UseRandomSelection`）：在多個等效開局走法中隨機挑選，增加對局多樣性
- 最大開局深度（`MaxPly`）：預設 20 步，超過後切換至正常搜尋
- 以 `OpeningBookEngineDecorator` 裝飾器模式包裝 `SearchEngine`，不侵入核心搜尋邏輯

### 殘局表（Endgame Tablebase）

- 採用**倒退分析（Retrograde Analysis）**精確求解指定棋子組合的所有殘局局面
- 以 Zobrist 雜湊索引所有局面，查詢時間複雜度 O(1)
- 統計：勝局數 / 負局數 / 和局數 / 總局面數
- 支援自訂棋子組合生成對應殘局表
- 格式：`.etb` 二進位序列化（`ETB/` 目錄）

### 外部引擎支援（UCCI/UCI）

透過 `ExternalEngineAdapter` 連接支援 UCI 或 UCCI 協議的外部引擎：

- 自動握手，解析引擎名稱（如 `"Pikafish 2026-01-02"`）
- 特別支援 Pikafish：自動偵測並套用 Pikafish 特有的提和規則
- 獨立管理引擎 process 生命週期（啟動 → 握手 → 搜尋 → 退出）
- 支援暫停/取消搜尋（`stop` 指令）

### AI 提示與走法解析

#### 走法提示

- **SmartHint**：在 AI 思考的同時以較淺深度（`SmartHintDepth`，預設 2）快速產生提示
- **MultiPV 提示**：同時搜尋多條 PV 線，UI 以列表顯示候選走法

#### LLM 走法解釋（Hint Explanation）

整合 OpenAI 相容 API，以 LLM 解釋 AI 建議走法（繁體中文）：

- **輸入**：FEN 局面 + 行棋方 + 建議走法 + 評分 + 搜尋資訊 + 延伸 PV 思路
- **輸出**：Step-by-step 格式，說明走法理由、戰術意圖、對手回應與風險（120～260 字）
- 設定項：Endpoint、Model、ApiKey、Temperature、MaxTokens、TimeoutSeconds

#### LLM 局面分析（Game Analysis）

AI 走子後可自動觸發局面解說：

- 以旁觀解說員角度解讀當前棋面優劣、棋子配置與戰局走向
- 輸出：約 150～280 字自然段落（繁體中文）
- 可透過 `GameAnalysis.IsEnabled` 開關控制（預設關閉）

### 難度設定

| 等級 | 搜尋深度 | 思考時間 |
|---|---|---|
| 初學（Beginner） | 2 | 3 秒 |
| 休閒（Casual） | 4 | 8 秒 |
| 進階（Advanced） | 6 | 15 秒 |
| 專家（Expert） | 9 | 25 秒 |

深度與思考時間亦可手動調整，紅黑雙方支援獨立設定（`RedSearchDepth` / `BlackSearchDepth`）。

---

## 架構說明

本專案嚴格遵循 **Clean Architecture** 原則，依賴方向僅向內（內層不依賴外層）：

```
┌─────────────────────────────────────────────┐
│              ChineseChess.WPF               │
│   MVVM / Canvas 棋盤渲染 / DI 容器組裝      │
└──────────────────┬──────────────────────────┘
                   │ 依賴
    ┌──────────────┼──────────────────────────┐
    │              │                          │
    ▼              ▼                          ▼
┌──────────┐  ┌──────────────────────────────────────┐
│Application│  │           Infrastructure             │
│GameService│  │  AI: Search / Evaluators / NNUE /    │
│Bookmarks  │  │      Book / ETB / Protocol / LLM     │
│IGameService│ │  (依賴 Domain + Application)         │
└─────┬─────┘  └──────────────────────────────────────┘
      │ 依賴
      ▼
┌─────────────────┐
│     Domain      │
│  Board / Piece  │
│  Move / Valid.  │
│  (零外部依賴)   │
└─────────────────┘
```

### 各層職責

| 層 | 專案 | 職責 | 外部依賴 |
|---|---|---|---|
| Domain | `ChineseChess.Domain` | 9×10 棋盤、棋子（`readonly struct`）、走法（`readonly struct`）、合法走法生成、FEN 解析、Zobrist 雜湊、局面驗證 | 無 |
| Application | `ChineseChess.Application` | 遊戲狀態機（`GameService`）、書籤、棋鐘、WXF 裁決、所有介面定義 | 僅 Domain |
| Infrastructure | `ChineseChess.Infrastructure` | AI 引擎完整實裝、NNUE 推論與訓練、開局庫、殘局表、外部引擎介面卡、LLM 服務 | Domain + Application |
| Presentation | `ChineseChess.WPF` | WPF MVVM、Canvas 棋盤渲染、控制面板、DI 容器組裝、設定檔讀取 | 所有層 |

### 關鍵設計模式

- **介面驅動設計**：`IAiEngine`、`IGameService`、`IEvaluator`、`IBoard` 等，所有層均依賴抽象
- **裝飾器模式**：`OpeningBookEngineDecorator` 在不修改 `SearchEngine` 的前提下附加開局庫查詢
- **Factory 模式**：`IAiEngineFactory` / `NnueAiEngineFactory` 分離引擎建立與使用
- **事件通訊**：`BoardUpdated`、`GameMessage`、`HintReady`、`ThinkingProgress`、`DrawOffered` 等事件跨層廣播
- **async/await + CancellationToken**：所有 AI 搜尋操作完全非同步，支援暫停/恢復機制
- **Constructor Injection**：使用 `Microsoft.Extensions.DependencyInjection` 管理所有依賴

---

## 設定檔

`ChineseChess.WPF/appsettings.json` 管理所有執行期設定：

```json
{
  "OpeningBook": {
    "IsEnabled": true,
    "BookFilePath": "openingbook.bin",
    "UseRandomSelection": true,
    "MaxPly": 20
  },
  "GameSettings": {
    "SearchDepth": 6,
    "SearchThinkingTimeSeconds": 3,
    "RedSearchDepth": 6,
    "RedSearchThinkingTimeSeconds": 3,
    "BlackSearchDepth": 6,
    "BlackSearchThinkingTimeSeconds": 3,
    "UseSharedTranspositionTable": true,
    "CopyRedTtToBlackAtStart": true,
    "TranspositionTableSizeMb": 128,
    "IsSmartHintEnabled": true,
    "SmartHintDepth": 2
  },
  "HintExplanation": {
    "Endpoint": "https://your-llm-endpoint/v1",
    "Model": "your-model-name",
    "ApiKey": "（填入 appsettings.local.json）",
    "Temperature": 0,
    "MaxTokens": 8192,
    "TimeoutSeconds": 20
  },
  "GameAnalysis": {
    "IsEnabled": false,
    "Endpoint": "https://your-llm-endpoint/v1",
    "Model": "your-model-name",
    "ApiKey": "（填入 appsettings.local.json）",
    "Temperature": 0,
    "MaxTokens": 4096,
    "TimeoutSeconds": 15
  }
}
```

私密資料（API Key）請放置於 `appsettings.local.json`（已在 `.gitignore` 中排除）：

```json
{
  "HintExplanation": { "ApiKey": "your-api-key-here" },
  "GameAnalysis":    { "ApiKey": "your-api-key-here" }
}
```

---

## 開發指南

### 建置與執行

```bash
# 建置整個解決方案
dotnet build

# 執行應用程式
dotnet run --project ChineseChess.WPF

# 發行（Release build，Windows x64 單檔）
dotnet publish ChineseChess.WPF -c Release -r win-x64 --self-contained
```

### 測試

```bash
# 執行所有測試
dotnet test

# 詳細輸出
dotnet test ChineseChess.Tests --logger "console;verbosity=detailed"

# 指定測試篩選
dotnet test --filter "FullyQualifiedName~SearchTests"
```

主要測試範圍（`ChineseChess.Tests`）：

| 類別 | 涵蓋內容 |
|---|---|
| Domain | 棋盤走法生成、合法性驗證、FEN 解析、Zobrist 雜湊、WXF 記譜 |
| AI 搜尋 | Alpha-Beta 正確性、Aspiration Window、IIR、Singular Extension、ProbCut、LMR |
| AI 評估 | 馬腳懲罰、炮威脅、車壓制、棋局相位插值、機動性、兵型 |
| NNUE | 特徵計算、推論一致性、增量累加器正確性 |
| 開局庫 | 查詢裝飾器、`.bin` 序列化 |
| 殘局表 | 倒退分析結果、`.etb` 序列化 |
| Application | GameService 狀態機、書籤、棋鐘、提和、WXF 裁決、擺棋模式、MultiPV |
| 外部引擎 | UCCI 協議解析、Adapter 生命週期 |

### 程式碼慣例

- **命名空間**：File-scoped（`namespace ChineseChess.Domain.Entities;`）
- **Nullable**：所有專案啟用 `<Nullable>enable</Nullable>`
- **值型別**：不可變資料使用 `readonly struct`（`Piece`、`Move`）
- **欄位命名**：私有欄位不使用底線前綴（`count` 而非 `_count`）
- **非同步**：AI 相關操作一律使用 `async/await + CancellationToken`
- **語言**：原始碼注釋使用繁體中文（zh-TW）；UI 字串使用繁體中文
- **Commit 格式**：遵循 Conventional Commits（`feat:`、`fix:`、`refactor:`、`test:`、`docs:`）
