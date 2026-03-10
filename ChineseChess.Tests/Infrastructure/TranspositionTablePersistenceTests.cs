using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
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
        original.Store(0x1234_5678_9ABC_DEF0uL, 1532, 6, TTFlag.Exact, new Move(12, 34));
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
        Assert.Equal(new Move(12, 34), entry.BestMove);
    }

    [Fact]
    public void TranspositionTable_CompressedBinary_ShouldBeSmallerThanRaw()
    {
        // 1MB TT = 65536 slots；未壓縮原始大小約為 header(17B) + 65536*16B ≈ 1MB
        var tt = new TranspositionTable(1);
        tt.Store(0xAAAA_BBBB_CCCC_DDDDuL, 500, 6, TTFlag.Exact, new Move(10, 20));
        tt.Store(0x1111_2222_3333_4444uL, -300, 4, TTFlag.LowerBound, new Move(30, 40));

        using var ms = new MemoryStream();
        tt.ExportToBinary(ms);

        long compressedSize = ms.Length;
        long rawPayloadSize = 17 + (long)tt.GetStatistics().Capacity * 16;

        // 期望壓縮後至少縮小 90%（大部分槽位為零，GZip 效率極高）
        Assert.True(compressedSize < rawPayloadSize / 10,
            $"壓縮後 {compressedSize} bytes 應遠小於未壓縮的 {rawPayloadSize} bytes");
    }

    [Fact]
    public void TranspositionTable_LegacyV1Binary_ShouldStillImport()
    {
        // 手工建構符合 v1（無壓縮）格式的二進制，驗證向後相容匯入
        const ulong ttSize = 65536UL; // 1MB TT 固定大小
        const byte generation = 2;

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new byte[] { 0x43, 0x43, 0x54, 0x54 }); // "CCTT" magic
            writer.Write(1u);         // version = 1（舊格式，無壓縮）
            writer.Write(ttSize);     // size
            writer.Write(generation); // generation

            // 寫入全零 keys 與 data 陣列（空 TT）
            for (ulong i = 0; i < ttSize; i++) writer.Write(0uL);
            for (ulong i = 0; i < ttSize; i++) writer.Write(0uL);
        }
        ms.Position = 0;

        var table = new TranspositionTable(1);
        table.ImportFromBinary(ms); // 應能順利讀取舊版 v1 格式

        Assert.Equal(generation, table.Generation);
        Assert.Equal(0L, table.GetStatistics().OccupiedEntries);
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
