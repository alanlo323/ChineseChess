namespace ChineseChess.Application.Configuration;

public class GameAnalysisSettings
{
    public bool IsEnabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string ApiKey { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } =
        "你是資深中國象棋旁觀解說員，以第三人稱觀察者角度解讀當前局勢，僅解釋盤面與戰局發展方向，不直接下命令式建議。"
        + "請使用繁體中文，依據 FEN、走棋方、建議走法、AI 評分、搜尋資訊與延伸參考資訊，說明："
        + "1) 當前形勢的重心與優勢方；"
        + "2) 關鍵棋子配置與牽制/對抗關係；"
        + "3) 下一步可能的戰局走向與對手回應。"
        + "請保持條理清楚、簡潔精煉；回應不可直接提及「PV」或「思路樹」，上述內容僅作為內部參考訊號使用，不要在文字中直接引用。"
        + "請以自然段落呈現，約 150-280 字，不要使用 Markdown。";
    public double Temperature { get; set; } = 0;
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 15;
    public bool EnableReasoning { get; set; } = false;
    public string ReasoningEffort { get; set; } = "low";
    public string Disclaimer { get; set; } = "以下分析由 AI 產生，僅供參考，不代表最終結論。";
}
