using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using ChineseChess.WPF.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 雙方棋子數量選擇器 ViewModel。
/// 提供紅方與黑方各棋子類型的數量微調，並即時計算預計局面數。
/// </summary>
public sealed class PieceCountSelectorViewModel : ObservableObject
{
    private long estimatedPositions;
    private bool isWarning;

    public PieceCountSelectorViewModel()
    {
        RedPieces   = BuildPieceList(isRed: true);
        BlackPieces = BuildPieceList(isRed: false);
        RecalcEstimate();
    }

    // ── 屬性 ────────────────────────────────────────────────────────────

    public IReadOnlyList<PieceCountItem> RedPieces   { get; }
    public IReadOnlyList<PieceCountItem> BlackPieces { get; }

    public long EstimatedPositions
    {
        get => estimatedPositions;
        private set => SetProperty(ref estimatedPositions, value);
    }

    public bool IsWarning
    {
        get => isWarning;
        private set => SetProperty(ref isWarning, value);
    }

    public string EstimatedPositionsText =>
        EstimatedPositions > 1_000_000
            ? $"⚠ 預計局面數：{EstimatedPositions:N0}（生成可能耗時）"
            : $"預計局面數：{EstimatedPositions:N0}";

    // ── 公開方法 ─────────────────────────────────────────────────────────

    /// <summary>依目前計數建立 PieceConfiguration。</summary>
    public PieceConfiguration BuildConfiguration()
    {
        var redExtra   = ExpandToList(RedPieces);
        var blackExtra = ExpandToList(BlackPieces);

        return new PieceConfiguration(
            BuildDisplayName(redExtra, blackExtra),
            redExtra,
            blackExtra);
    }

    /// <summary>從預設組合填入各方計數（預設快選用）。</summary>
    public void LoadFromPreset(PieceConfiguration config)
    {
        SetCounts(RedPieces,   config.RedExtra);
        SetCounts(BlackPieces, config.BlackExtra);
    }

    /// <summary>從棋盤提取子力，填入各方計數（「從當前局面生成」用）。</summary>
    public void LoadFromBoard(IBoard board)
    {
        ResetAll();

        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone || piece.Type == PieceType.King)
                continue;

            var list = piece.Color == PieceColor.Red ? RedPieces : BlackPieces;
            var item = list.FirstOrDefault(x => x.Type == piece.Type && x.Count < x.Max);
            if (item is not null)
                item.Count++;
        }
    }

    // ── 私有方法 ─────────────────────────────────────────────────────────

    private IReadOnlyList<PieceCountItem> BuildPieceList(bool isRed)
    {
        var pieces = new[]
        {
            (PieceType.Rook,     "車",                2),
            (PieceType.Horse,    "馬",                2),
            (PieceType.Cannon,   "砲",                2),
            (PieceType.Advisor,  isRed ? "仕" : "士", 2),
            (PieceType.Elephant, isRed ? "相" : "象", 2),
            (PieceType.Pawn,     isRed ? "兵" : "卒", 5),
        };

        return pieces
            .Select(p => new PieceCountItem(p.Item1, p.Item2, p.Item3, OnCountChanged))
            .ToList();
    }

    private void OnCountChanged()
    {
        RecalcEstimate();
        OnPropertyChanged(nameof(EstimatedPositionsText));
    }

    private void RecalcEstimate()
    {
        var config = BuildConfiguration();
        EstimatedPositions = config.EstimatedPositions;
        IsWarning = EstimatedPositions > 5_000_000;
    }

    private static List<PieceType> ExpandToList(IReadOnlyList<PieceCountItem> items)
    {
        var result = new List<PieceType>();
        foreach (var item in items)
            for (int i = 0; i < item.Count; i++)
                result.Add(item.Type);
        return result;
    }

    private static void SetCounts(IReadOnlyList<PieceCountItem> items, IReadOnlyList<PieceType> types)
    {
        // 先全部歸零
        foreach (var item in items)
            item.Count = 0;

        // 依類型累加
        foreach (var type in types)
        {
            var item = items.FirstOrDefault(x => x.Type == type && x.Count < x.Max);
            if (item is not null)
                item.Count++;
        }
    }

    private void ResetAll()
    {
        foreach (var item in RedPieces)   item.Count = 0;
        foreach (var item in BlackPieces) item.Count = 0;
    }

    private static string BuildDisplayName(List<PieceType> red, List<PieceType> black)
    {
        static string RedChar(PieceType t) => t switch
        {
            PieceType.Rook     => "車",
            PieceType.Horse    => "馬",
            PieceType.Cannon   => "砲",
            PieceType.Advisor  => "仕",
            PieceType.Elephant => "相",
            PieceType.Pawn     => "兵",
            _                  => "?",
        };

        static string BlackChar(PieceType t) => t switch
        {
            PieceType.Advisor  => "士",
            PieceType.Elephant => "象",
            PieceType.Pawn     => "卒",
            PieceType.Rook     => "車",
            PieceType.Horse    => "馬",
            PieceType.Cannon   => "砲",
            _                  => "?",
        };

        var redPart   = string.Concat(red.Select(RedChar));
        var blackPart = string.Concat(black.Select(BlackChar));

        return string.IsNullOrEmpty(blackPart)
            ? $"帥{redPart} vs 將"
            : $"帥{redPart} vs 將{blackPart}";
    }
}
