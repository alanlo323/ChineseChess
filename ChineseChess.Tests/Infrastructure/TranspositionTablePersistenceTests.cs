using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.IO;
using System.IO.Compression;
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
    public void TranspositionTable_ExportedBinary_ShouldHaveVersion4InHeader()
    {
        // v4：Full key + Columnar + Brotli（QP 相容格式）
        var tt = new TranspositionTable(1);
        using var ms = new MemoryStream();
        tt.ExportToBinary(ms);
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        reader.ReadBytes(4);          // 跳過 magic "CCTT"
        uint version = reader.ReadUInt32();

        Assert.Equal(4u, version);    // 期望 v4（QP 相容）
    }

    [Fact]
    public void TranspositionTable_V4_ShouldRoundTripAllFieldsAndEdgeCases()
    {
        // 測試負分、最大深度、所有 TTFlag 值、特殊棋步
        var original = new TranspositionTable(1);
        original.Store(0x1111_1111_1111_1111uL, -30000, 20, TTFlag.UpperBound, new Move(0, 89));
        original.Store(0x2222_2222_2222_2222uL,  30000,  1, TTFlag.LowerBound, new Move(89, 0));
        original.Store(0x3333_3333_3333_3333uL,      0,  8, TTFlag.Exact,      new Move(44, 55));
        original.NewGeneration();
        original.NewGeneration();
        original.NewGeneration();

        using var ms = new MemoryStream();
        original.ExportToBinary(ms);
        ms.Position = 0;

        var restored = new TranspositionTable(1);
        restored.ImportFromBinary(ms);

        Assert.Equal((byte)3, restored.Generation);

        Assert.True(restored.Probe(0x1111_1111_1111_1111uL, out var e1));
        Assert.Equal(-30000, e1.Score);
        Assert.Equal((byte)20, e1.Depth);
        Assert.Equal(TTFlag.UpperBound, e1.Flag);
        Assert.Equal(new Move(0, 89), e1.BestMove);

        Assert.True(restored.Probe(0x2222_2222_2222_2222uL, out var e2));
        Assert.Equal(30000, e2.Score);
        Assert.Equal(TTFlag.LowerBound, e2.Flag);
        Assert.Equal(new Move(89, 0), e2.BestMove);

        Assert.True(restored.Probe(0x3333_3333_3333_3333uL, out var e3));
        Assert.Equal(0, e3.Score);
        Assert.Equal(TTFlag.Exact, e3.Flag);
        Assert.Equal(new Move(44, 55), e3.BestMove);
    }

    [Fact]
    public void TranspositionTable_V2GZip_ShouldStillImport()
    {
        // 手工建構 v2（GZip）格式，驗證匯入仍向後相容
        const ulong ttSize = 65536UL;
        const byte generation = 5;

        using var ms = new MemoryStream();
        using (var hw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            hw.Write(new byte[] { 0x43, 0x43, 0x54, 0x54 }); // "CCTT"
            hw.Write(2u);          // version = 2（GZip）
            hw.Write(ttSize);      // size
            hw.Write(generation);  // generation
        }
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var gw = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true))
        {
            for (ulong i = 0; i < ttSize; i++) gw.Write(0uL); // keys（全零）
            for (ulong i = 0; i < ttSize; i++) gw.Write(0uL); // data（全零）
        }
        ms.Position = 0;

        var table = new TranspositionTable(1);
        table.ImportFromBinary(ms);   // 應能順利讀取 v2 格式

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
