using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Book;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class OpeningBookTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 基本功能 ────────────────────────────────────────────────────────

    [Fact]
    public void TryProbe_EmptyBook_ReturnsFalse()
    {
        var book = new OpeningBook();
        var result = book.TryProbe(12345UL, out var move);

        Assert.False(result);
        Assert.True(move.IsNull);
    }

    [Fact]
    public void TryProbe_SingleEntry_ReturnsCorrectMove()
    {
        var book = new OpeningBook(useRandomSelection: false);
        var expected = new Move(64, 67);
        book.SetEntry(100UL, [(expected, 10)]);

        var result = book.TryProbe(100UL, out var move);

        Assert.True(result);
        Assert.Equal(expected, move);
    }

    [Fact]
    public void TryProbe_MultipleEntries_ReturnsOneFromCandidates()
    {
        var book = new OpeningBook(useRandomSelection: true);
        var moves = new[]
        {
            new Move(64, 67),
            new Move(82, 65),
            new Move(88, 69),
        };
        book.SetEntry(999UL, moves.Select(m => (m, 10)).ToList());

        // 多次呼叫，回傳值都應在候選清單內
        for (int i = 0; i < 50; i++)
        {
            book.TryProbe(999UL, out var selected);
            Assert.Contains(selected, moves);
        }
    }

    [Fact]
    public void TryProbe_ZeroWeightMoves_NotReturned()
    {
        // 權重 0 的走法不應被加入
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(200UL, [(new Move(64, 67), 0)]);

        // SetEntry 應過濾掉權重 0，導致空 entry，TryProbe 回傳 false
        var result = book.TryProbe(200UL, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryProbe_NonRandom_AlwaysReturnsHighestWeight()
    {
        var book = new OpeningBook(useRandomSelection: false);
        var highWeight = new Move(82, 65);
        var lowWeight = new Move(64, 67);
        book.SetEntry(300UL, [(lowWeight, 5), (highWeight, 100)]);

        for (int i = 0; i < 20; i++)
        {
            book.TryProbe(300UL, out var move);
            Assert.Equal(highWeight, move);
        }
    }

    [Fact]
    public void ContainsPosition_ExistingKey_ReturnsTrue()
    {
        var book = new OpeningBook();
        book.SetEntry(42UL, [(new Move(64, 67), 1)]);

        Assert.True(book.ContainsPosition(42UL));
    }

    [Fact]
    public void ContainsPosition_MissingKey_ReturnsFalse()
    {
        var book = new OpeningBook();
        Assert.False(book.ContainsPosition(999UL));
    }

    [Fact]
    public void IsLoaded_EmptyBook_ReturnsFalse()
    {
        var book = new OpeningBook();
        Assert.False(book.IsLoaded);
    }

    [Fact]
    public void IsLoaded_AfterAddingEntry_ReturnsTrue()
    {
        var book = new OpeningBook();
        book.SetEntry(1UL, [(new Move(1, 2), 5)]);
        Assert.True(book.IsLoaded);
    }

    [Fact]
    public void EntryCount_ReflectsActualCount()
    {
        var book = new OpeningBook();
        Assert.Equal(0, book.EntryCount);

        book.SetEntry(1UL, [(new Move(1, 2), 5)]);
        Assert.Equal(1, book.EntryCount);

        book.SetEntry(2UL, [(new Move(3, 4), 5)]);
        Assert.Equal(2, book.EntryCount);
    }

    [Fact]
    public void SetEntry_OverwritesSameKey()
    {
        var book = new OpeningBook(useRandomSelection: false);
        var original = new Move(64, 67);
        var updated = new Move(82, 65);

        book.SetEntry(1UL, [(original, 10)]);
        book.SetEntry(1UL, [(updated, 10)]);  // 覆寫

        book.TryProbe(1UL, out var move);
        Assert.Equal(updated, move);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var book = new OpeningBook();
        book.SetEntry(1UL, [(new Move(1, 2), 5)]);
        book.SetEntry(2UL, [(new Move(3, 4), 5)]);

        book.Clear();

        Assert.Equal(0, book.EntryCount);
        Assert.False(book.IsLoaded);
    }

    // ─── Binary 序列化 ────────────────────────────────────────────────────

    [Fact]
    public void BinarySerializer_RoundTrip_PreservesAllEntries()
    {
        var original = new OpeningBook(useRandomSelection: false);
        original.SetEntry(100UL, [(new Move(64, 67), 30), (new Move(82, 65), 20)]);
        original.SetEntry(200UL, [(new Move(7, 24), 25)]);

        using var ms = new MemoryStream();
        OpeningBookSerializer.SaveToBinary(original, ms);
        ms.Position = 0;
        var loaded = OpeningBookSerializer.LoadFromBinary(ms, useRandomSelection: false);

        Assert.Equal(original.EntryCount, loaded.EntryCount);
        Assert.True(loaded.ContainsPosition(100UL));
        Assert.True(loaded.ContainsPosition(200UL));

        // 驗證權重還原正確：非隨機模式下應選最高權重（Move(64,67) weight=30 > Move(82,65) weight=20）
        loaded.TryProbe(100UL, out var move);
        Assert.Equal(new Move(64, 67), move);
    }

    [Fact]
    public void BinarySerializer_RoundTrip_PreservesWeightRatio()
    {
        // 驗證加權隨機分布：100:0 → 永遠選第一個
        var original = new OpeningBook(useRandomSelection: true);
        original.SetEntry(777UL, [(new Move(64, 67), 100), (new Move(82, 65), 0)]);

        using var ms = new MemoryStream();
        OpeningBookSerializer.SaveToBinary(original, ms);
        ms.Position = 0;
        // SetEntry 會過濾 weight=0，所以只剩一個走法
        var loaded = OpeningBookSerializer.LoadFromBinary(ms, useRandomSelection: true);

        for (int i = 0; i < 20; i++)
        {
            loaded.TryProbe(777UL, out var m);
            Assert.Equal(new Move(64, 67), m);
        }
    }

    [Fact]
    public void BinarySerializer_InvalidMagic_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("XXXX\x00\x01\x00\x00\x00\x00"));
        Assert.Throws<InvalidDataException>(() => OpeningBookSerializer.LoadFromBinary(ms));
    }

    [Fact]
    public void BinarySerializer_EmptyBook_RoundTrip()
    {
        var original = new OpeningBook();
        using var ms = new MemoryStream();
        OpeningBookSerializer.SaveToBinary(original, ms);
        ms.Position = 0;
        var loaded = OpeningBookSerializer.LoadFromBinary(ms);
        Assert.Equal(0, loaded.EntryCount);
    }

    // ─── JSON 序列化 ──────────────────────────────────────────────────────

    [Fact]
    public void JsonSerializer_RoundTrip_PreservesAllEntries()
    {
        var original = new OpeningBook();
        original.SetEntry(555UL, [(new Move(64, 67), 30), (new Move(70, 67), 10)]);

        using var ms = new MemoryStream();
        OpeningBookSerializer.SaveToJson(original, ms);
        ms.Position = 0;
        var loaded = OpeningBookSerializer.LoadFromJson(ms);

        Assert.Equal(original.EntryCount, loaded.EntryCount);
        Assert.True(loaded.ContainsPosition(555UL));
    }

    // ─── DefaultOpeningData ───────────────────────────────────────────────

    [Fact]
    public void DefaultOpeningData_InitialPosition_HasMoves()
    {
        var book = DefaultOpeningData.Build();

        var board = new Board(InitialFen);
        var result = book.TryProbe(board.ZobristKey, out var move);

        Assert.True(result);
        Assert.False(move.IsNull);
    }

    [Fact]
    public void DefaultOpeningData_InitialMoves_AreAllLegal()
    {
        var book = DefaultOpeningData.Build();
        var board = new Board(InitialFen);
        var legalMoves = board.GenerateLegalMoves().ToHashSet();

        // 多次 probe 確保所有可能被選中的走法都是合法的
        var probedMoves = new HashSet<Move>();
        for (int i = 0; i < 200; i++)
        {
            book.TryProbe(board.ZobristKey, out var m);
            probedMoves.Add(m);
        }

        foreach (var m in probedMoves)
        {
            Assert.Contains(m, legalMoves);
        }
    }

    [Fact]
    public void DefaultOpeningData_HasAtLeastFivePositions()
    {
        var book = DefaultOpeningData.Build();
        Assert.True(book.EntryCount >= 5, $"開局庫應含至少 5 個局面，實際：{book.EntryCount}");
    }

    [Fact]
    public void DefaultOpeningData_BlackResponsePositions_ExistAfterRedFirstMove()
    {
        var book = DefaultOpeningData.Build();

        // 紅方先走炮二平五後，黑方局面應存在
        var board = new Board(InitialFen);
        board.MakeMove(new Move(64, 67)); // 炮二平五
        var blackHasResponse = book.ContainsPosition(board.ZobristKey);

        Assert.True(blackHasResponse, "炮二平五後應有黑方回應記錄");
    }

    [Theory]
    [InlineData(64, 67)]   // 炮二平五
    [InlineData(82, 65)]   // 馬二進三
    [InlineData(88, 69)]   // 馬八進七
    [InlineData(83, 67)]   // 相三進五
    public void DefaultOpeningData_BlackResponses_AreAllLegal(int redFrom, int redTo)
    {
        var book = DefaultOpeningData.Build();

        var board = new Board(InitialFen);
        board.MakeMove(new Move(redFrom, redTo));

        if (!book.ContainsPosition(board.ZobristKey)) return; // 若無對應局面，跳過

        var legalMoves = board.GenerateLegalMoves().ToHashSet();
        var probedMoves = new HashSet<Move>();
        for (int i = 0; i < 200; i++)
        {
            if (book.TryProbe(board.ZobristKey, out var m))
                probedMoves.Add(m);
        }

        foreach (var m in probedMoves)
        {
            Assert.Contains(m, legalMoves);
        }
    }

    [Fact]
    public void BinarySerializer_ExcessiveEntryCount_ThrowsInvalidDataException()
    {
        // 偽造 entryCount 超過上限的惡意檔案
        using var ms = new MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("CCOB")); // magic
        writer.Write((ushort)1);          // version
        writer.Write(uint.MaxValue);      // entryCount = 4,294,967,295（超過上限）
        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => OpeningBookSerializer.LoadFromBinary(ms));
    }
}
