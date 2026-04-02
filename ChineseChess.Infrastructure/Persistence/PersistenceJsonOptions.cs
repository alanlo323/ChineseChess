using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChineseChess.Infrastructure.Persistence;

/// <summary>
/// 所有 JSON 持久化 service 共用的序列化選項。
/// WriteIndented：方便人工閱讀，enum 以字串儲存（向後相容）。
/// </summary>
internal static class PersistenceJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
