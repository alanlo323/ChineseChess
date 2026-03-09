using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class TranspositionTablePersistenceTests
{
    [Fact]
    public void TranspositionTable_ShouldRoundTripBinary()
    {
        var original = new TranspositionTable(1);
        original.Store(0x1234_5678_9ABC_DEF0uL, 1532, 6, TTFlag.Exact, new ChineseChess.Domain.Entities.Move(12, 34));
        original.NewGeneration();
        original.NewGeneration();

        using var ms = new MemoryStream();
        original.ExportToBinary(ms);
        ms.Position = 0;

        var restored = new TranspositionTable(1);
        restored.ImportFromBinary(ms);

        var ok = restored.Probe(0x1234_5678_9ABC_DEF0uL, out var entry);
        Assert.True(ok);
        Assert.Equal(TTFlag.Exact, entry.Flag);
        Assert.Equal(1532, entry.Score);
        Assert.Equal((byte)6, entry.Depth);
        Assert.Equal((byte)2, original.Generation);
        Assert.Equal((byte)2, restored.Generation);
        Assert.Equal(new ChineseChess.Domain.Entities.Move(12, 34), entry.BestMove);
    }

    [Fact]
    public void TranspositionTable_ShouldRoundTripJson()
    {
        var original = new TranspositionTable(1);
        original.Store(0x1111_2222_3333_4444uL, -500, 5, TTFlag.LowerBound, new ChineseChess.Domain.Entities.Move(45, 67));
        original.NewGeneration();

        using var ms = new MemoryStream();
        original.ExportToJson(ms);
        ms.Position = 0;

        var restored = new TranspositionTable(1);
        restored.ImportFromJson(ms);

        var ok = restored.Probe(0x1111_2222_3333_4444uL, out var entry);
        Assert.True(ok);
        Assert.Equal(TTFlag.LowerBound, entry.Flag);
        Assert.Equal(-500, entry.Score);
        Assert.Equal((byte)5, entry.Depth);
        Assert.Equal((byte)1, original.Generation);
        Assert.Equal((byte)1, restored.Generation);
        Assert.Equal(new ChineseChess.Domain.Entities.Move(45, 67), entry.BestMove);
    }

    [Fact]
    public void TranspositionTable_InvalidBinaryShouldThrow()
    {
        var table = new TranspositionTable(1);
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("invalid-cctt"));

        Assert.Throws<InvalidDataException>(() => table.ImportFromBinary(ms));
    }

    [Fact]
    public void TranspositionTable_InvalidJsonShouldThrow()
    {
        var table = new TranspositionTable(1);
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{ \"size\": 1, \"generation\": 1"));

        Assert.Throws<InvalidDataException>(() => table.ImportFromJson(ms));
    }
}
