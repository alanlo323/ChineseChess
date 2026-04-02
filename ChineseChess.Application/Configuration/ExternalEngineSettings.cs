using ChineseChess.Application.Enums;

namespace ChineseChess.Application.Configuration;

/// <summary>
/// AI 玩家的使用者設定，儲存至 engine-user-settings.json。
/// 涵蓋引擎類型、搜索參數、外部引擎路徑/協議、Pikafish 設定、評估器類型、NNUE 模型路徑。
/// 舊版 JSON 缺少新欄位時使用預設值（向後相容）。
/// </summary>
public class ExternalEngineSettings
{
    // ─── 紅方外部引擎 ─────────────────────────────────────────────────────
    public bool UseRedExternalEngine { get; set; } = false;
    public string RedEnginePath { get; set; } = string.Empty;
    public EngineProtocol RedProtocol { get; set; } = EngineProtocol.Ucci;

    // ─── 黑方外部引擎 ─────────────────────────────────────────────────────
    public bool UseBlackExternalEngine { get; set; } = false;
    public string BlackEnginePath { get; set; } = string.Empty;
    public EngineProtocol BlackProtocol { get; set; } = EngineProtocol.Ucci;

    // ─── 伺服器 ───────────────────────────────────────────────────────────
    public int ServerPort { get; set; } = 23333;

    /// <summary>紅方 Pikafish 專屬設定（非 Pikafish 引擎時忽略）。</summary>
    public PikafishSettings RedPikafish { get; set; } = new();

    /// <summary>黑方 Pikafish 專屬設定（非 Pikafish 引擎時忽略）。</summary>
    public PikafishSettings BlackPikafish { get; set; } = new();

    // ─── 紅方 AI 設定（新增：引擎類型、搜索、評估器）─────────────────────
    public AiEngineType RedAiEngineType { get; set; } = AiEngineType.Internal;
    public int RedSearchDepth { get; set; } = 6;
    public int RedSearchTimeSeconds { get; set; } = 3;
    public InternalEvaluatorType RedEvaluatorType { get; set; } = InternalEvaluatorType.Handcrafted;
    public string RedNnueModelPath { get; set; } = string.Empty;

    // ─── 黑方 AI 設定（新增：引擎類型、搜索、評估器）─────────────────────
    public AiEngineType BlackAiEngineType { get; set; } = AiEngineType.Internal;
    public int BlackSearchDepth { get; set; } = 6;
    public int BlackSearchTimeSeconds { get; set; } = 3;
    public InternalEvaluatorType BlackEvaluatorType { get; set; } = InternalEvaluatorType.Handcrafted;
    public string BlackNnueModelPath { get; set; } = string.Empty;

    // ─── 已載入引擎 ID（新版：從引擎登錄選取）────────────────────────────
    // null = 尚未選取或使用舊版路徑方式（向後相容）
    public string? RedEngineId { get; set; } = null;
    public string? BlackEngineId { get; set; } = null;
}
