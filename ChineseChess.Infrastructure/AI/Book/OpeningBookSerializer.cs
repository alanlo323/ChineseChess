using ChineseChess.Domain.Entities;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChineseChess.Infrastructure.AI.Book;

/// <summary>
/// 開局庫序列化工具。支援 binary（緊湊）與 JSON（除錯用）兩種格式。
///
/// Binary 格式：
///   Header：magic "CCOB" (4 bytes) + version uint16 (2 bytes) + entryCount uint32 (4 bytes)
///   每個 entry：zobristKey ulong (8 bytes) + moveCount uint16 (2 bytes)
///   每個 move：from byte (1) + to byte (1) + weight int32 (4) = 6 bytes
/// </summary>
public static class OpeningBookSerializer
{
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("CCOB");
    private const ushort Version = 1;

    // ─── Binary ──────────────────────────────────────────────────────────

    public static void SaveToBinary(OpeningBook book, Stream output)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write(MagicBytes);        // 4 bytes magic
        writer.Write(Version);          // 2 bytes version
        writer.Write((uint)book.EntryCount);  // 4 bytes entry count

        foreach (var kv in book.GetEntries())
        {
            var entry = kv.Value;
            writer.Write(entry.ZobristKey);             // 8 bytes
            writer.Write((ushort)entry.Moves.Count);    // 2 bytes
            foreach (var m in entry.Moves)
            {
                writer.Write(m.Move.From);  // 1 byte
                writer.Write(m.Move.To);    // 1 byte
                writer.Write(m.Weight);     // 4 bytes
            }
        }
    }

    // 安全上界：防止損壞/惡意檔案造成記憶體耗盡
    private const uint MaxEntryCount = 1_000_000;
    private const ushort MaxMovesPerEntry = 256;

    public static OpeningBook LoadFromBinary(Stream input, bool useRandomSelection = true)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        // 驗證 magic
        var magic = reader.ReadBytes(4);
        if (magic.Length < 4 || magic[0] != MagicBytes[0] || magic[1] != MagicBytes[1]
            || magic[2] != MagicBytes[2] || magic[3] != MagicBytes[3])
        {
            throw new InvalidDataException("無效的開局庫檔案格式（magic bytes 不符）。");
        }

        var version = reader.ReadUInt16();
        if (version > Version)
        {
            throw new InvalidDataException($"開局庫版本 {version} 不支援（最高支援 {Version}）。");
        }

        var entryCount = reader.ReadUInt32();
        if (entryCount > MaxEntryCount)
        {
            throw new InvalidDataException($"開局庫局面數 {entryCount} 超過上限 {MaxEntryCount}，可能是損壞的檔案。");
        }

        var book = new OpeningBook(useRandomSelection);

        for (uint i = 0; i < entryCount; i++)
        {
            var key = reader.ReadUInt64();
            var moveCount = reader.ReadUInt16();
            if (moveCount > MaxMovesPerEntry)
            {
                throw new InvalidDataException($"局面 {i} 的走法數 {moveCount} 超過上限 {MaxMovesPerEntry}，可能是損壞的檔案。");
            }

            var moves = new (Move Move, int Weight)[moveCount];
            for (int j = 0; j < moveCount; j++)
            {
                var from = reader.ReadByte();
                var to = reader.ReadByte();
                var weight = reader.ReadInt32();
                moves[j] = (new Move(from, to), weight);
            }
            book.SetEntry(key, moves);
        }

        return book;
    }

    // ─── JSON ─────────────────────────────────────────────────────────────

    public static void SaveToJson(OpeningBook book, Stream output)
    {
        var dto = new BookDto();
        foreach (var kv in book.GetEntries())
        {
            var entry = kv.Value;
            var moves = new MoveDto[entry.Moves.Count];
            for (int i = 0; i < entry.Moves.Count; i++)
            {
                var m = entry.Moves[i];
                moves[i] = new MoveDto(m.Move.From, m.Move.To, m.Weight);
            }
            dto.Entries.Add(new EntryDto(entry.ZobristKey, moves));
        }
        JsonSerializer.Serialize(output, dto, JsonOptions);
    }

    public static OpeningBook LoadFromJson(Stream input, bool useRandomSelection = true)
    {
        var dto = JsonSerializer.Deserialize<BookDto>(input, JsonOptions)
            ?? throw new InvalidDataException("JSON 格式無效。");

        var book = new OpeningBook(useRandomSelection);
        foreach (var entry in dto.Entries)
        {
            var moves = new (Move Move, int Weight)[entry.Moves.Length];
            for (int i = 0; i < entry.Moves.Length; i++)
            {
                var m = entry.Moves[i];
                moves[i] = (new Move(m.From, m.To), m.Weight);
            }
            book.SetEntry(entry.Key, moves);
        }
        return book;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─── DTO ──────────────────────────────────────────────────────────────

    private sealed class BookDto
    {
        public List<EntryDto> Entries { get; set; } = [];
    }

    private sealed record EntryDto(ulong Key, MoveDto[] Moves);

    private sealed record MoveDto(byte From, byte To, int Weight);
}
