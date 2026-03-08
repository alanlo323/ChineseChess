using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class ControlPanelViewModel : ObservableObject
{
    private readonly IGameService _gameService;
    private GameMode _selectedMode = GameMode.PlayerVsAi;
    private string _statusMessage = "Ready";
    private int _searchDepth = 5;
    private int _searchThinkingTime = 3;
    private int _redSearchDepth = 5;
    private int _redSearchThinkingTime = 3;
    private int _blackSearchDepth = 5;
    private int _blackSearchThinkingTime = 3;
    private bool _useSharedTT = false;
    private bool _isSmartHintEnabled = true;
    private int _smartHintDepth = 2;
    private TTStatistics _ttStats = new TTStatistics();
    private TTStatistics? _blackTtStats = null;

    public IEnumerable<GameMode> GameModes => Enum.GetValues<GameMode>();

    public GameMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(IsAiVsAiMode));
                OnPropertyChanged(nameof(ShowDualTTStats));
            }
        }
    }

    public bool IsAiVsAiMode => _selectedMode == GameMode.AiVsAi;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // ─── 全域設定（非 AiVsAi 模式使用）────────────────────────────────────

    public int SearchDepth
    {
        get => _searchDepth;
        set
        {
            if (SetProperty(ref _searchDepth, value))
            {
                _gameService.SetDifficulty(value, _searchThinkingTime * 1000);
            }
        }
    }

    public int SearchThinkingTime
    {
        get => _searchThinkingTime;
        set
        {
            if (SetProperty(ref _searchThinkingTime, value))
            {
                _gameService.SetDifficulty(_searchDepth, value * 1000);
            }
        }
    }

    // ─── AiVsAi 紅方設定 ────────────────────────────────────────────────

    public int RedSearchDepth
    {
        get => _redSearchDepth;
        set
        {
            if (SetProperty(ref _redSearchDepth, value))
            {
                _gameService.SetRedAiDifficulty(value, _redSearchThinkingTime * 1000);
            }
        }
    }

    public int RedSearchThinkingTime
    {
        get => _redSearchThinkingTime;
        set
        {
            if (SetProperty(ref _redSearchThinkingTime, value))
            {
                _gameService.SetRedAiDifficulty(_redSearchDepth, value * 1000);
            }
        }
    }

    // ─── AiVsAi 黑方設定 ────────────────────────────────────────────────

    public int BlackSearchDepth
    {
        get => _blackSearchDepth;
        set
        {
            if (SetProperty(ref _blackSearchDepth, value))
            {
                _gameService.SetBlackAiDifficulty(value, _blackSearchThinkingTime * 1000);
            }
        }
    }

    public int BlackSearchThinkingTime
    {
        get => _blackSearchThinkingTime;
        set
        {
            if (SetProperty(ref _blackSearchThinkingTime, value))
            {
                _gameService.SetBlackAiDifficulty(_blackSearchDepth, value * 1000);
            }
        }
    }

    // ─── TT 共用設定 ─────────────────────────────────────────────────────

    public bool UseSharedTT
    {
        get => _useSharedTT;
        set
        {
            if (SetProperty(ref _useSharedTT, value))
            {
                _gameService.UseSharedTranspositionTable = value;
                OnPropertyChanged(nameof(ShowDualTTStats));
            }
        }
    }

    // 獨立TT且AI對AI模式才顯示雙欄統計
    public bool ShowDualTTStats => IsAiVsAiMode && !_useSharedTT;

    // ─── 智能提示 ─────────────────────────────────────────────────────────

    public bool IsSmartHintEnabled
    {
        get => _isSmartHintEnabled;
        set
        {
            if (SetProperty(ref _isSmartHintEnabled, value))
            {
                _gameService.IsSmartHintEnabled = value;
            }
        }
    }

    public int SmartHintDepth
    {
        get => _smartHintDepth;
        set
        {
            if (SetProperty(ref _smartHintDepth, value))
            {
                _gameService.SmartHintDepth = value;
            }
        }
    }

    // ─── TT 統計（紅方 / 共用）────────────────────────────────────────────

    public string TtCapacity => $"{_ttStats.Capacity:N0}";
    public string TtMemoryMb => $"{_ttStats.MemoryMb:F1} MB";
    public string TtGeneration => _ttStats.Generation.ToString();
    public string TtOccupied => $"{_ttStats.OccupiedEntries:N0}";
    public string TtFillRate => $"{_ttStats.FillRate:P1}";
    public string TtProbes => $"{_ttStats.TotalProbes:N0}";
    public string TtHits => $"{_ttStats.Hits:N0}";
    public string TtHitRate => $"{_ttStats.HitRate:P1}";

    // ─── TT 統計（黑方，獨立TT模式）──────────────────────────────────────

    public string BlackTtCapacity => $"{_blackTtStats?.Capacity ?? 0:N0}";
    public string BlackTtMemoryMb => $"{_blackTtStats?.MemoryMb ?? 0:F1} MB";
    public string BlackTtGeneration => (_blackTtStats?.Generation ?? 0).ToString();
    public string BlackTtOccupied => $"{_blackTtStats?.OccupiedEntries ?? 0:N0}";
    public string BlackTtFillRate => $"{_blackTtStats?.FillRate ?? 0:P1}";
    public string BlackTtProbes => $"{_blackTtStats?.TotalProbes ?? 0:N0}";
    public string BlackTtHits => $"{_blackTtStats?.Hits ?? 0:N0}";
    public string BlackTtHitRate => $"{_blackTtStats?.HitRate ?? 0:P1}";

    // ─── 搜尋效能 ─────────────────────────────────────────────────────────

    public string SearchNodes => $"{_gameService.LastSearchNodes:N0}";
    public string SearchNps => $"{_gameService.LastSearchNps:N0} 節點/秒";

    // ─── 指令 ─────────────────────────────────────────────────────────────

    public ICommand StartGameCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand HintCommand { get; }
    public ICommand StopThinkingCommand { get; }
    public ICommand PauseThinkingCommand { get; }
    public ICommand ResumeThinkingCommand { get; }
    public ICommand ExportTranspositionTableCommand { get; }
    public ICommand ImportTranspositionTableCommand { get; }
    public ICommand ExportBlackTranspositionTableCommand { get; }
    public ICommand ImportBlackTranspositionTableCommand { get; }
    public ICommand RefreshTTStatsCommand { get; }
    public ICommand MergeTranspositionTablesCommand { get; }

    public ControlPanelViewModel(IGameService gameService)
    {
        _gameService = gameService;

        RefreshTTStatsCommand = new RelayCommand(_ => RefreshTTStats());
        StartGameCommand = new RelayCommand(async _ => await _gameService.StartGameAsync(SelectedMode));
        UndoCommand = new RelayCommand(_ => _gameService.Undo());

        StopThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "停止思考中...";
            await _gameService.StopGameAsync();
            StatusMessage = "AI 思考已停止";
        });
        PauseThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "正在暫停思考...";
            await _gameService.PauseThinkingAsync();
        });
        ResumeThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "繼續思考中...";
            await _gameService.ResumeThinkingAsync();
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
                await _gameService.ExportTranspositionTableAsync(dialog.FileName, asJson);
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
                await _gameService.ImportTranspositionTableAsync(dialog.FileName, asJson);
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
                await _gameService.ExportBlackTranspositionTableAsync(dialog.FileName, asJson);
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
                await _gameService.ImportBlackTranspositionTableAsync(dialog.FileName, asJson);
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
                await _gameService.MergeTranspositionTablesAsync();
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
                var hint = await _gameService.GetHintAsync();

                if (hint.BestMove.IsNull)
                {
                    StatusMessage = "目前沒有可用提示";
                }
                else
                {
                    var turn     = _gameService.CurrentBoard.Turn;
                    var notation = MoveNotation.ToNotation(hint.BestMove, _gameService.CurrentBoard);
                    StatusMessage = $"提示完成：{notation} | 分數：{FormatHintScore(hint.Score)}（{(turn == PieceColor.Red ? "紅方" : "黑方")}）";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"提示失敗：{ex.Message}";
            }
        });

        _gameService.SetDifficulty(_searchDepth, _searchThinkingTime * 1000);

        _gameService.GameMessage += msg =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null) { StatusMessage = msg; return; }
            app.Dispatcher.Invoke(() => StatusMessage = msg);
        };

        _gameService.ThinkingProgress += _ =>
        {
            var app = global::System.Windows.Application.Current;
            if (app == null) return;
            app.Dispatcher.Invoke(RefreshTTStats);
        };

        RefreshTTStats();
    }

    private void RefreshTTStats()
    {
        _ttStats      = _gameService.GetTTStatistics();
        _blackTtStats = _gameService.GetBlackTTStatistics();

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
