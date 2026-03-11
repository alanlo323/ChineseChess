namespace ChineseChess.Application.Configuration;

public class HintExplanationSettings
{
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string ApiKey { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = "你是熟悉中國象棋的專家，請使用繁體中文，僅根據提供的棋局資料解釋建議走法。";
    public double Temperature { get; set; } = 0.25;
    public int MaxTokens { get; set; } = 1200;
    public int TimeoutSeconds { get; set; } = 20;
}

