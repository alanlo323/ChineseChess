namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 讀取 .plain 格式訓練資料並以 IAsyncEnumerable 串流回傳。
///
/// .plain 格式（每樣本一行，欄位以 | 分隔）：
///   fen | score | result
///   例：rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1 | 0 | 0.5
///
/// score 以 centipawns 為單位（先手視角），result 以 0.0/0.5/1.0 表示黑勝/和/紅勝。
/// </summary>
public sealed class TrainingDataLoader
{
    private readonly string filePath;

    public TrainingDataLoader(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"訓練資料檔不存在：{filePath}");
        this.filePath = filePath;
    }

    /// <summary>以非同步串流方式逐行讀取訓練樣本。跳過空行與以 # 開頭的注釋行。</summary>
    public async IAsyncEnumerable<TrainingPosition> LoadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var line in File.ReadLinesAsync(filePath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            var pos = TryParseLine(line);
            if (pos is not null) yield return pos;
        }
    }

    /// <summary>讀取前 maxCount 筆資料到記憶體（用於小資料集的 shuffle）。</summary>
    public async Task<List<TrainingPosition>> LoadAllAsync(
        int maxCount = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        var result = new List<TrainingPosition>();
        await foreach (var pos in LoadAsync(cancellationToken))
        {
            result.Add(pos);
            if (result.Count >= maxCount) break;
        }
        return result;
    }

    // ── 私有解析 ─────────────────────────────────────────────────────────

    private static TrainingPosition? TryParseLine(string line)
    {
        // 格式：fen | score | result
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return null;

        string fen = parts[0];
        if (string.IsNullOrEmpty(fen)) return null;

        if (!int.TryParse(parts[1], out int score)) return null;
        if (!float.TryParse(parts[2],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float result))
            return null;

        return new TrainingPosition
        {
            Fen    = fen,
            Score  = score,
            Result = result,
        };
    }
}
