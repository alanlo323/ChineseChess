using ChineseChess.Application.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

/// <summary>棋局記錄序列化、匯出、匯入服務介面。</summary>
public interface IGameRecordService
{
    /// <summary>將 GameRecord 序列化為縮排 JSON 字串。</summary>
    string Serialize(GameRecord record);

    /// <summary>從 JSON 字串反序列化 GameRecord。</summary>
    /// <exception cref="System.Text.Json.JsonException">格式不符時拋出。</exception>
    GameRecord Deserialize(string json);

    /// <summary>非同步將 GameRecord 以 JSON 寫入指定路徑（.ccgame）。</summary>
    Task ExportAsync(GameRecord record, string filePath, CancellationToken ct = default);

    /// <summary>非同步從指定路徑（.ccgame）讀取並還原 GameRecord。</summary>
    Task<GameRecord> ImportAsync(string filePath, CancellationToken ct = default);
}
