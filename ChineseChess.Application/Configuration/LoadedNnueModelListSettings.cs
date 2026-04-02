namespace ChineseChess.Application.Configuration;

/// <summary>已載入 NNUE 模型列表的持久化設定（存入 loaded-nnue-models.json）。</summary>
public class LoadedNnueModelListSettings
{
    public List<LoadedNnueModelInfo> Models { get; set; } = [];
}
