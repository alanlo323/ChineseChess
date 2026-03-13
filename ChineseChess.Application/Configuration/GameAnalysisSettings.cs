namespace ChineseChess.Application.Configuration;

public class GameAnalysisSettings
{
    public bool IsEnabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string ApiKey { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } =
        "你是專業中國象棋分析師。請用繁體中文針對以下局面提供即時分析：" +
        "1) 當前局勢評估（哪方佔優、為什麼）" +
        "2) 關鍵棋子位置與作用" +
        "3) 下一步可能的策略方向" +
        "請以簡潔明了的方式呈現，約 150-300 字，不要使用 Markdown 格式。";
    public double Temperature { get; set; } = 0;
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 15;
    public bool EnableReasoning { get; set; } = false;
    public string ReasoningEffort { get; set; } = "low";
    public string Disclaimer { get; set; } = "以下分析由 AI 產生，僅供參考，不代表最終結論。";
}
