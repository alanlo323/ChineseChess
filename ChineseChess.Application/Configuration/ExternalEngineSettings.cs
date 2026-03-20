using ChineseChess.Application.Enums;

namespace ChineseChess.Application.Configuration;

/// <summary>
/// 外部引擎使用者設定，儲存至 engine-user-settings.json。
/// </summary>
public class ExternalEngineSettings
{
    public bool UseRedExternalEngine { get; set; } = false;
    public string RedEnginePath { get; set; } = string.Empty;
    public EngineProtocol RedProtocol { get; set; } = EngineProtocol.Ucci;

    public bool UseBlackExternalEngine { get; set; } = false;
    public string BlackEnginePath { get; set; } = string.Empty;
    public EngineProtocol BlackProtocol { get; set; } = EngineProtocol.Ucci;

    public int ServerPort { get; set; } = 23333;

    /// <summary>紅方 Pikafish 專屬設定（非 Pikafish 引擎時忽略）。</summary>
    public PikafishSettings RedPikafish { get; set; } = new();

    /// <summary>黑方 Pikafish 專屬設定（非 Pikafish 引擎時忽略）。</summary>
    public PikafishSettings BlackPikafish { get; set; } = new();
}
