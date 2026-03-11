using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class ControlPanelViewModel : ObservableObject
{
    private readonly IGameService gameService;
    private GameMode selectedMode = GameMode.PlayerVsAi;
    private string statusMessage = "Ready";
    private int searchDepth;
    private int searchThinkingTime;
    private int redSearchDepth;
    private int redSearchThinkingTime;
    private int blackSearchDepth;
    private int blackSearchThinkingTime;
    private bool useSharedTT;
    private bool isSmartHintEnabled;
    private int smartHintDepth;
    private string hintExplanationText = "（尚未產生提示）";
    private TTStatistics ttStats = new TTStatistics();
    private TTStatistics? blackTtStats = null;
    private const int TTExploreMaxDepth = 20;   // 固定最大深度（TT 搜尋深度通常 ≤ 10，此值足以顯示全樹）
    private string ttExplorerText = "（初始化中...）";
    private readonly System.Timers.Timer ttExplorerTimer;
    private int ttExplorerBusy;   // Interlocked 防止重疊執行（0 = 閒置，1 = 執行中）
    private int selectedTabIndex;
    private const int HintExplanationTabIndex = 3;

    public IEnumerable<GameMode> GameModes => Enum.GetValues<GameMode>();

    public GameMode SelectedMode
    {
        get => selectedMode;
        set
        {
            if (SetProperty(ref selectedMode, value))
            {
                OnPropertyChanged(nameof(IsAiVsAiMode));
                OnPropertyChanged(nameof(ShowDualTTStats));
            }
        }
    }

    public bool IsAiVsAiMode => selectedMode == GameMode.AiVsAi;

    public string StatusMessage
    {
        get => statusMessage;
        set => SetProperty(ref statusMessage, value);
    }

    public int SelectedTabIndex
    {
        get => selectedTabIndex;
        set => SetProperty(ref selectedTabIndex, value);
    }

    // ─── 全域設定（非 AiVsAi 模式使用）────────────────────────────────────

    public int SearchDepth
    {
        get => searchDepth;
        set
        {
            if (SetProperty(ref searchDepth, value))
            {
                gameService.SetDifficulty(value, searchThinkingTime * 1000);
            }
        }
    }

    public int SearchThinkingTime
    {
        get => searchThinkingTime;
        set
        {
            if (SetProperty(ref searchThinkingTime, value))
            {
                gameService.SetDifficulty(searchDepth, value * 1000);
            }
        }
    }

    // ─── AiVsAi 紅方設定 ────────────────────────────────────────────────

    public int RedSearchDepth
    {
        get => redSearchDepth;
        set
        {
            if (SetProperty(ref redSearchDepth, value))
            {
                gameService.SetRedAiDifficulty(value, redSearchThinkingTime * 1000);
            }
        }
    }

    public int RedSearchThinkingTime
    {
        get => redSearchThinkingTime;
        set
        {
            if (SetProperty(ref redSearchThinkingTime, value))
            {
                gameService.SetRedAiDifficulty(redSearchDepth, value * 1000);
            }
        }
    }

    // ─── AiVsAi 黑方設定 ────────────────────────────────────────────────

    public int BlackSearchDepth
    {
        get => blackSearchDepth;
        set
        {
            if (SetProperty(ref blackSearchDepth, value))
            {
                gameService.SetBlackAiDifficulty(value, blackSearchThinkingTime * 1000);
            }
        }
    }

    public int BlackSearchThinkingTime
    {
        get => blackSearchThinkingTime;
        set
        {
            if (SetProperty(ref blackSearchThinkingTime, value))
            {
                gameService.SetBlackAiDifficulty(blackSearchDepth, value * 1000);
            }
        }
    }

    // ─── TT 共用設定 ─────────────────────────────────────────────────────

    public bool UseSharedTT
    {
        get => useSharedTT;
        set
        {
            if (SetProperty(ref useSharedTT, value))
            {
                gameService.UseSharedTranspositionTable = value;
                OnPropertyChanged(nameof(ShowDualTTStats));
            }
        }
    }

    // 獨立TT且AI對AI模式才顯示雙欄統計
    public bool ShowDualTTStats => IsAiVsAiMode && !useSharedTT;

    // ─── 智能提示 ─────────────────────────────────────────────────────────

    public bool IsSmartHintEnabled
    {
        get => isSmartHintEnabled;
        set
        {
            if (SetProperty(ref isSmartHintEnabled, value))
            {
                gameService.IsSmartHintEnabled = value;
            }
        }
    }

    public int SmartHintDepth
    {
        get => smartHintDepth;
        set
        {
            if (SetProperty(ref smartHintDepth, value))
            {
                gameService.SmartHintDepth = value;
            }
        }
    }

    // ─── TT 統計（紅方 / 共用）────────────────────────────────────────────

    public string TtCapacity => $"{ttStats.Capacity:N0}";
    public string TtMemoryMb => $"{ttStats.MemoryMb:F1} MB";
    public string TtGeneration => ttStats.Generation.ToString();
    public string TtOccupied => $"{ttStats.OccupiedEntries:N0}";
    public string TtFillRate => $"{ttStats.FillRate:P1}";
    public string TtProbes => $"{ttStats.TotalProbes:N0}";
    public string TtHits => $"{ttStats.Hits:N0}";
    public string TtHitRate => $"{ttStats.HitRate:P1}";

    // ─── TT 統計（黑方，獨立TT模式）──────────────────────────────────────

    public string BlackTtCapacity => $"{blackTtStats?.Capacity ?? 0:N0}";
    public string BlackTtMemoryMb => $"{blackTtStats?.MemoryMb ?? 0:F1} MB";
    public string BlackTtGeneration => (blackTtStats?.Generation ?? 0).ToString();
    public string BlackTtOccupied => $"{blackTtStats?.OccupiedEntries ?? 0:N0}";
    public string BlackTtFillRate => $"{blackTtStats?.FillRate ?? 0:P1}";
    public string BlackTtProbes => $"{blackTtStats?.TotalProbes ?? 0:N0}";
    public string BlackTtHits => $"{blackTtStats?.Hits ?? 0:N0}";
    public string BlackTtHitRate => $"{blackTtStats?.HitRate ?? 0:P1}";

    // ─── 搜尋效能 ─────────────────────────────────────────────────────────

    public string SearchNodes => $"{gameService.LastSearchNodes:N0}";
    public string SearchNps => $"{gameService.LastSearchNps:N0} 節點/秒";

    // ─── TT 探索 ──────────────────────────────────────────────────────────

    public string TTExplorerText
    {
        get => ttExplorerText;
        private set => SetProperty(ref ttExplorerText, value);
    }

    public string HintExplanationText
    {
        get => hintExplanationText;
        private set
        {
            if (SetProperty(ref hintExplanationText, value))
            {
                OnPropertyChanged(nameof(CanExplainHint));
            }
        }
    }

    public bool CanExplainHint => !string.IsNullOrWhiteSpace(HintExplanationText) && !HintExplanationText.StartsWith("（尚未", StringComparison.Ordinal);

    // ─── 指令 ─────────────────────────────────────────────────────────────

    public ICommand StartGameCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand HintCommand { get; }
    public ICommand ExplainHintCommand { get; }
    public ICommand StopThinkingCommand { get; }
    public ICommand PauseThinkingCommand { get; }
    public ICommand ResumeThinkingCommand { get; }
    public ICommand RequestDrawCommand { get; }
    public ICommand ExportTranspositionTableCommand { get; }
    public ICommand ImportTranspositionTableCommand { get; }
    public ICommand ExportBlackTranspositionTableCommand { get; }
    public ICommand ImportBlackTranspositionTableCommand { get; }
    public ICommand RefreshTTStatsCommand { get; }
    public ICommand MergeTranspositionTablesCommand { get; }

    public ControlPanelViewModel(IGameService gameService, GameSettings settings)
    {
        this.gameService = gameService;

        searchDepth            = settings.SearchDepth;
        searchThinkingTime     = settings.SearchThinkingTimeSeconds;
        redSearchDepth         = settings.RedSearchDepth;
        redSearchThinkingTime  = settings.RedSearchThinkingTimeSeconds;
        blackSearchDepth       = settings.BlackSearchDepth;
        blackSearchThinkingTime = settings.BlackSearchThinkingTimeSeconds;
        useSharedTT            = settings.UseSharedTranspositionTable;
        isSmartHintEnabled     = settings.IsSmartHintEnabled;
        smartHintDepth         = settings.SmartHintDepth;

        this.gameService.IsSmartHintEnabled = isSmartHintEnabled;
        this.gameService.SmartHintDepth     = smartHintDepth;
        this.gameService.UseSharedTranspositionTable = useSharedTT;

        RefreshTTStatsCommand = new RelayCommand(_ => RefreshTTStats());
        StartGameCommand = new RelayCommand(async _ => await gameService.StartGameAsync(SelectedMode));
        UndoCommand = new RelayCommand(_ => gameService.Undo());

        RequestDrawCommand = new RelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提和請求中...";
                await gameService.RequestDrawAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"提和失敗：{ex.Message}";
            }
        });

        StopThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "停止思考中...";
            await gameService.StopGameAsync();
            StatusMessage = "AI 思考已停止";
        });
        PauseThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "正在暫停思考...";
            await gameService.PauseThinkingAsync();
        });
        ResumeThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "繼續思考中...";
            await gameService.ResumeThinkingAsync();
        });

        ExportTranspositionTableCommand = new RelayCommand(async _ =>
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "匯出 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    FileName = $"transposition-table-{DateTime.Now:yyyyMMdd_HHmmss}.cctt"
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯出 TT 表中...";
                await gameService.ExportTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "TT 表匯出完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 表匯出失敗：{ex.Message}";
            }
        });

        ImportTranspositionTableCommand = new RelayCommand(async _ =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "匯入 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯入 TT 表中...";
                await gameService.ImportTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "TT 表匯入完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 表匯入失敗：{ex.Message}";
            }
        });

        ExportBlackTranspositionTableCommand = new RelayCommand(async _ =>
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "匯出黑方 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    FileName = $"transposition-table-black-{DateTime.Now:yyyyMMdd_HHmmss}.cctt"
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯出黑方 TT 表中...";
                await gameService.ExportBlackTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "黑方 TT 表匯出完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"黑方 TT 表匯出失敗：{ex.Message}";
            }
        });

        ImportBlackTranspositionTableCommand = new RelayCommand(async _ =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "匯入黑方 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯入黑方 TT 表中...";
                await gameService.ImportBlackTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "黑方 TT 表匯入完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"黑方 TT 表匯入失敗：{ex.Message}";
            }
        });

        MergeTranspositionTablesCommand = new RelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "合併兩方 TT 中...";
                await gameService.MergeTranspositionTablesAsync();
                StatusMessage = "TT 合併完成";
                RefreshTTStats();
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 合併失敗：{ex.Message}";
            }
        });

        HintCommand = new RelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提示走法中...";
                var hint = await gameService.GetHintAsync();

                if (hint.BestMove.IsNull)
                {
                    StatusMessage = "目前沒有可用提示";
                }
                else
                {
                    var turn     = gameService.CurrentBoard.Turn;
                    var notation = MoveNotation.ToNotation(hint.BestMove, gameService.CurrentBoard);
                    StatusMessage = $"提示完成：{notation} | 分數：{FormatHintScore(hint.Score)}（{(turn == PieceColor.Red ? "紅方" : "黑方")}）";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"提示失敗：{ex.Message}";
            }
        });

        ExplainHintCommand = new RelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提示解釋中...";
                SelectedTabIndex = HintExplanationTabIndex;
                HintExplanationText = "（AI 正在解釋）";
                var explanation = await gameService.ExplainLatestHintAsync();
                HintExplanationText = explanation;
                StatusMessage = "提示解釋完成";
            }
            catch (Exception ex)
            {
                HintExplanationText = $"解釋失敗：{ex.Message}";
                StatusMessage = "提示解釋失敗";
                SelectedTabIndex = HintExplanationTabIndex;
            }
        });

        // TT 探索計時器：每秒在背景執行緒更新，透過 Dispatcher 推送至 UI
        ttExplorerTimer = new System.Timers.Timer(100);
        ttExplorerTimer.AutoReset = true;
        ttExplorerTimer.Elapsed += (_, _) => ScheduleTTExplorerRefresh();
        ttExplorerTimer.Start();

        gameService.SetDifficulty(searchDepth, searchThinkingTime * 1000);

        gameService.GameMessage += msg =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null) { StatusMessage = msg; return; }
            app.Dispatcher.Invoke(() => StatusMessage = msg);
        };

        gameService.ThinkingProgress += _ =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null) return;
            app.Dispatcher.Invoke(RefreshTTStats);
        };

        gameService.HintReady += _ =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null)
            {
                HintExplanationText = "（已取得提示，可按解釋）";
                return;
            }

            app.Dispatcher.Invoke(() => HintExplanationText = "（已取得提示，可按解釋）");
        };

        gameService.BoardUpdated += () =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null)
            {
                HintExplanationText = "（尚未產生提示）";
                return;
            }

            app.Dispatcher.Invoke(() => HintExplanationText = "（尚未產生提示）");
        };

        // AI 主動提和：顯示 MessageBox 詢問玩家
        gameService.DrawOffered += offerResult =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null) return;
            app.Dispatcher.Invoke(() =>
            {
                var answer = MessageBox.Show(
                    $"AI 提議和棋。\n{offerResult.Reason}\n\n是否接受？",
                    "AI 提和",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                gameService.RespondToDrawOffer(answer == MessageBoxResult.Yes);
            });
        };

        // 提和結果通知
        gameService.DrawOfferResolved += result =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null) return;
            string message = result.Accepted
                ? $"和棋成立！{result.Reason}"
                : $"提和遭拒。{result.Reason}";
            app.Dispatcher.Invoke(() => StatusMessage = message);
        };

        RefreshTTStats();
    }

    private void RefreshTTStats()
    {
        ttStats      = gameService.GetTTStatistics();
        blackTtStats = gameService.GetBlackTTStatistics();

        OnPropertyChanged(nameof(TtCapacity));
        OnPropertyChanged(nameof(TtMemoryMb));
        OnPropertyChanged(nameof(TtGeneration));
        OnPropertyChanged(nameof(TtOccupied));
        OnPropertyChanged(nameof(TtFillRate));
        OnPropertyChanged(nameof(TtProbes));
        OnPropertyChanged(nameof(TtHits));
        OnPropertyChanged(nameof(TtHitRate));

        OnPropertyChanged(nameof(BlackTtCapacity));
        OnPropertyChanged(nameof(BlackTtMemoryMb));
        OnPropertyChanged(nameof(BlackTtGeneration));
        OnPropertyChanged(nameof(BlackTtOccupied));
        OnPropertyChanged(nameof(BlackTtFillRate));
        OnPropertyChanged(nameof(BlackTtProbes));
        OnPropertyChanged(nameof(BlackTtHits));
        OnPropertyChanged(nameof(BlackTtHitRate));

        OnPropertyChanged(nameof(SearchNodes));
        OnPropertyChanged(nameof(SearchNps));
        OnPropertyChanged(nameof(ShowDualTTStats));
    }

    /// <summary>
    /// 計時器觸發時呼叫：若上一次更新尚未完成則跳過（防止重疊）。
    /// 在 Task.Run 背景執行緒產生文字，再透過 Dispatcher 更新 UI。
    /// </summary>
    private void ScheduleTTExplorerRefresh()
    {
        // CAS：0→1 成功才進入，否則跳過此次
        if (Interlocked.CompareExchange(ref ttExplorerBusy, 1, 0) != 0) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string text = BuildTTExplorerText();
                var app = global::System.Windows.Application.Current;
                app?.Dispatcher.Invoke(() => TTExplorerText = text);
            }
            finally
            {
                Interlocked.Exchange(ref ttExplorerBusy, 0);
            }
        });
    }

    private string BuildTTExplorerText()
    {
        var sb = new StringBuilder();

        // ── 條目分布 ────────────────────────────────────────
        sb.AppendLine("══ TT 條目分布 ══════════════════════════════");
        try
        {
            var entries = gameService.EnumerateTTEntries().ToList();
            int total = entries.Count;
            sb.AppendLine($"有效條目：{total:N0}");
            sb.AppendLine();

            if (total > 0)
            {
                // 深度分布（附簡易長條圖）
                sb.AppendLine("深度分布：");
                var byDepth = entries.GroupBy(e => e.Depth)
                                     .OrderBy(g => g.Key)
                                     .Select(g => (Depth: g.Key, Count: g.Count()))
                                     .ToList();
                int maxCount = byDepth.Max(x => x.Count);
                const int BarWidth = 20;
                foreach (var (depth, count) in byDepth)
                {
                    int bars = maxCount > 0 ? (int)Math.Round((double)count / maxCount * BarWidth) : 0;
                    string bar = new string('█', bars).PadRight(BarWidth);
                    sb.AppendLine($"  深度 {depth,2}：{count,7:N0} {bar}");
                }

                sb.AppendLine();
                sb.AppendLine("旗標分布：");
                foreach (var g in entries.GroupBy(e => e.Flag).OrderBy(g => g.Key))
                    sb.AppendLine($"  {g.Key,-12}：{g.Count(),7:N0}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（枚舉失敗：{ex.Message}）");
        }

        sb.AppendLine();

        // ── 思路樹 ───────────────────────────────────────────
        sb.AppendLine("══ 思路樹 ═══════════════════════════════════");
        try
        {
            // 取快照以確保棋盤在整個遞迴中不被修改
            var boardSnapshot = gameService.CurrentBoard.Clone();
            var root = gameService.ExploreTTTree(TTExploreMaxDepth);
            if (root == null)
            {
                sb.AppendLine("（當前局面不在 TT 中，尚未搜尋過此局面）");
            }
            else
            {
                AppendTreeNode(sb, root, "", isRoot: true, parentBoard: boardSnapshot);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（探索失敗：{ex.Message}）");
        }

        return sb.ToString();
    }

    private static void AppendTreeNode(
        System.Text.StringBuilder sb,
        TTTreeNode node,
        string indent,
        bool isRoot,
        IBoard parentBoard)
    {
        var e = node.Entry;
        string scoreStr = e.Score > 0 ? $"+{e.Score}" : e.Score.ToString();
        string flagChar = e.Flag switch
        {
            TTFlag.Exact      => "=",
            TTFlag.LowerBound => "≥",
            TTFlag.UpperBound => "≤",
            _                 => "?"
        };

        // 根節點顯示「當前局面」；子節點用 parentBoard（走法執行前的棋盤）轉換標準記譜
        string moveStr;
        IBoard boardAtNode;
        if (isRoot)
        {
            moveStr = "（當前局面）";
            boardAtNode = parentBoard;
        }
        else
        {
            moveStr = MoveNotation.ToNotation(node.MoveToHere, parentBoard);
            boardAtNode = parentBoard.Clone();
            try { boardAtNode.MakeMove(node.MoveToHere); }
            catch { /* 過期或碰撞條目，無法套用走法；子節點仍可顯示但不再遞迴 */ }
        }

        sb.AppendLine($"{indent}{moveStr}  [{flagChar} {scoreStr}, 深度:{e.Depth}]");

        foreach (var child in node.Children)
            AppendTreeNode(sb, child, indent + "  ", isRoot: false, parentBoard: boardAtNode);
    }

    private static string FormatHintScore(int score)
    {
        return score switch
        {
            > 0 => $"+{score}",
            < 0 => score.ToString(),
            _ => "0"
        };
    }
}
