using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using System.Collections.Generic;
using System.Linq;

namespace ChineseChess.Infrastructure.AI.Book;

/// <summary>
/// 象棋開局庫。以 Zobrist hash 為 key，儲存各局面的候選走法與權重。
/// 可選擇加權隨機選手或永遠選最高權重走法。
/// </summary>
public sealed class OpeningBook : IOpeningBook
{
    private readonly Dictionary<ulong, OpeningBookEntry> entries = new();
    private readonly bool useRandomSelection;

    public OpeningBook(bool useRandomSelection = true)
    {
        this.useRandomSelection = useRandomSelection;
    }

    public bool IsLoaded => entries.Count > 0;
    public int EntryCount => entries.Count;

    /// <summary>
    /// 查詢開局庫。找到後依設定的選手策略回傳走法。
    /// </summary>
    public bool TryProbe(ulong zobristKey, out Move move)
    {
        if (!entries.TryGetValue(zobristKey, out var entry) || entry.Moves.Count == 0)
        {
            move = Move.Null;
            return false;
        }

        move = useRandomSelection ? SelectWeightedRandom(entry) : SelectHighestWeight(entry);
        return true;
    }

    public bool ContainsPosition(ulong zobristKey) => entries.ContainsKey(zobristKey);

    /// <summary>
    /// 設定（或覆寫）指定 Zobrist key 的候選走法。
    /// 權重 ≤ 0 的走法自動過濾。若過濾後無合法走法，不加入此局面。
    /// </summary>
    public void SetEntry(ulong zobristKey, IEnumerable<(Move Move, int Weight)> moves)
    {
        var bookMoves = moves
            .Where(m => m.Weight > 0)
            .Select(m => new OpeningBookMove(m.Move, m.Weight))
            .ToList();

        if (bookMoves.Count == 0)
            return;

        entries[zobristKey] = new OpeningBookEntry
        {
            ZobristKey = zobristKey,
            Moves = bookMoves
        };
    }

    /// <summary>
    /// 增量新增走法。若同局面同走法已存在，累加權重；不同走法則附加。
    /// 與 <see cref="SetEntry"/> 的差異在於此方法不覆寫現有記錄。
    /// </summary>
    public void AddMove(ulong zobristKey, Move move, int weight)
    {
        if (weight <= 0) return;

        if (!entries.TryGetValue(zobristKey, out var existing))
        {
            entries[zobristKey] = new OpeningBookEntry
            {
                ZobristKey = zobristKey,
                Moves = [new OpeningBookMove(move, weight)]
            };
            return;
        }

        var list = existing.Moves.ToList();
        int idx = list.FindIndex(m => m.Move == move);
        if (idx >= 0)
            list[idx] = new OpeningBookMove(move, list[idx].Weight + weight);
        else
            list.Add(new OpeningBookMove(move, weight));

        entries[zobristKey] = new OpeningBookEntry { ZobristKey = zobristKey, Moves = list };
    }

    public void Clear() => entries.Clear();

    /// <summary>回傳不可變的條目視圖（供序列化使用）。</summary>
    internal IReadOnlyDictionary<ulong, OpeningBookEntry> GetEntries() => entries;

    private static Move SelectWeightedRandom(OpeningBookEntry entry)
    {
        int total = entry.TotalWeight;
        if (total <= 0) return entry.Moves[0].Move;

        int roll = Random.Shared.Next(total);
        int cumulative = 0;
        foreach (var m in entry.Moves)
        {
            cumulative += m.Weight;
            if (roll < cumulative) return m.Move;
        }
        return entry.Moves[^1].Move;
    }

    private static Move SelectHighestWeight(OpeningBookEntry entry)
    {
        var best = entry.Moves[0];
        foreach (var m in entry.Moves)
        {
            if (m.Weight > best.Weight) best = m;
        }
        return best.Move;
    }
}
