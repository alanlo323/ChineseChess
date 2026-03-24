using ChineseChess.Infrastructure.AI.Nnue.Network;
using ZstdSharp;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 NnueFileFormat.LoadWeights 的 ZSTD 自動偵測與解壓縮行為：
///   1. 未壓縮檔案：版本不符時拋出正確錯誤
///   2. ZSTD 壓縮檔案：版本不符時拋出相同的版本錯誤（而非 ZSTD 錯誤）
///   3. ZSTD 壓縮檔案：版本正確時通過版本檢查，進入後續解析
///   4. ZSTD 魔術碼但內容損壞：拋出 ZSTD 解壓縮錯誤
///   5. 未壓縮與 ZSTD 壓縮相同的錯誤版本資料：兩者產生相同型別與訊息的錯誤
/// </summary>
public class NnueFileFormatTests
{
    private const uint WrongVersion = 0xDEADBEEFu;

    // ── 輔助：建立最小的正確版本 payload（版本對，其餘全零）────────────
    // 夠大讓版本讀取成功，但後續 LEB128 魔術字串必定失敗

    private static byte[] BuildMinimalCorrectVersionPayload()
    {
        var data = new byte[100];
        BitConverter.GetBytes(NnueFileFormat.FileVersion).CopyTo(data, 0);
        return data;
    }

    // ── 輔助：ZSTD 壓縮 ─────────────────────────────────────────────────

    private static byte[] ZstdCompress(byte[] input)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(input).ToArray();
    }

    // ── 輔助：寫臨時檔並在測試結束後刪除 ──────────────────────────────

    private static string WriteTempFile(byte[] data)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, data);
        return path;
    }

    // ── 測試案例 ─────────────────────────────────────────────────────────

    [Fact]
    public void LoadWeights_UncompressedWrongVersion_ThrowsVersionMismatch()
    {
        var data = new byte[4];
        BitConverter.GetBytes(WrongVersion).CopyTo(data, 0);
        string path = WriteTempFile(data);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => NnueFileFormat.LoadWeights(path));
            Assert.Contains("版本不符", ex.Message);
            Assert.Contains($"0x{WrongVersion:X}", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadWeights_ZstdCompressedWrongVersion_ThrowsVersionMismatch()
    {
        var inner = new byte[4];
        BitConverter.GetBytes(WrongVersion).CopyTo(inner, 0);
        string path = WriteTempFile(ZstdCompress(inner));
        try
        {
            // ZSTD 解壓成功後，應看到版本不符錯誤（而非 ZSTD 錯誤）
            var ex = Assert.Throws<InvalidDataException>(
                () => NnueFileFormat.LoadWeights(path));
            Assert.Contains("版本不符", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadWeights_ZstdCompressedCorrectVersion_PassesVersionCheck()
    {
        // 版本正確，後續資料無效 → 應在版本之後才失敗（LEB128 魔術字串）
        string path = WriteTempFile(ZstdCompress(BuildMinimalCorrectVersionPayload()));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => NnueFileFormat.LoadWeights(path));
            Assert.DoesNotContain("版本不符", ex.Message);
            Assert.DoesNotContain("ZSTD", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadWeights_InvalidZstdPayload_ThrowsDecompressionError()
    {
        // ZSTD 魔術碼開頭但後續為無效內容
        byte[] data = [0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x02, 0x03, 0x04];
        string path = WriteTempFile(data);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => NnueFileFormat.LoadWeights(path));
            Assert.Contains("ZSTD", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadWeights_ZstdAndUncompressed_SameVersionErrorForSameData()
    {
        // 相同的錯誤版本，兩種封裝格式應產生相同類型的例外與相似訊息
        var wrongVersion = new byte[4];
        BitConverter.GetBytes(WrongVersion).CopyTo(wrongVersion, 0);

        string pathPlain = WriteTempFile(wrongVersion);
        string pathZstd  = WriteTempFile(ZstdCompress(wrongVersion));
        try
        {
            var exPlain = Assert.Throws<InvalidDataException>(
                () => NnueFileFormat.LoadWeights(pathPlain));
            var exZstd = Assert.Throws<InvalidDataException>(
                () => NnueFileFormat.LoadWeights(pathZstd));

            Assert.Contains("版本不符", exPlain.Message);
            Assert.Contains("版本不符", exZstd.Message);
        }
        finally
        {
            File.Delete(pathPlain);
            File.Delete(pathZstd);
        }
    }
}
