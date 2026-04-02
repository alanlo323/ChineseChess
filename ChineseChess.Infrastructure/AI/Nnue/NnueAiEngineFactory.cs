using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Book;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Evaluator;
using ChineseChess.Infrastructure.AI.Nnue.Network;
using ChineseChess.Infrastructure.AI.Search;

namespace ChineseChess.Infrastructure.AI.Nnue;

/// <summary>
/// IAiEngineFactory 實作。
/// 根據 NnueEngineConfig 建立帶獨立 NnueNetwork 的引擎實例。
/// 若 config.ModelId 有值且 Registry 有快取權重，則共享 NnueWeights 節省記憶體；
/// 否則從 ModelFilePath 直接載入（向後相容）。
/// </summary>
public sealed class NnueAiEngineFactory : IAiEngineFactory
{
    private readonly GameSettings gameSettings;
    private readonly IOpeningBook openingBook;
    private readonly OpeningBookSettings openingBookSettings;
    // 可選依賴：Registry 不存在時退化為直接載入檔案
    private readonly LoadedNnueModelRegistry? nnueModelRegistry;

    public NnueAiEngineFactory(
        GameSettings gameSettings,
        IOpeningBook openingBook,
        OpeningBookSettings openingBookSettings,
        LoadedNnueModelRegistry? nnueModelRegistry = null)
    {
        this.gameSettings        = gameSettings;
        this.openingBook         = openingBook;
        this.openingBookSettings = openingBookSettings;
        this.nnueModelRegistry   = nnueModelRegistry;
    }

    /// <inheritdoc/>
    public async Task<IAiEngine> CreateWithNnueAsync(NnueEngineConfig config, CancellationToken ct = default)
    {
        var network = new NnueNetwork();

        // 優先使用 Registry 的共享權重（相同 .nnue 檔只佔一份 ~17MB 記憶體）
        if (!string.IsNullOrEmpty(config.ModelId) && nnueModelRegistry != null)
        {
            var weights  = nnueModelRegistry.GetWeights(config.ModelId);
            var modelInfoData = nnueModelRegistry.GetModelInfo(config.ModelId);
            if (weights != null && modelInfoData != null)
            {
                network.LoadFromWeights(weights, new NnueModelInfo
                {
                    FilePath      = modelInfoData.FilePath,
                    Description   = modelInfoData.Description,
                    FileSizeBytes = modelInfoData.FileSizeBytes,
                    LoadedAt      = modelInfoData.LoadedAt,
                });
                var evaluator2   = new CompositeEvaluator(network);
                var searchEngine2 = new SearchEngine(gameSettings, evaluator2);
                return new OpeningBookEngineDecorator(searchEngine2, openingBook, openingBookSettings);
            }
        }

        // 退化路徑：weights 尚未快取或未提供 ModelId，直接從檔案載入（可能產生額外記憶體）
        if (!string.IsNullOrEmpty(config.ModelId))
            System.Diagnostics.Trace.TraceWarning(
                $"NnueAiEngineFactory: ModelId={config.ModelId} 的 weights 未就緒，退化到讀檔路徑");
        await network.LoadFromFileAsync(config.ModelFilePath, ct).ConfigureAwait(false);

        var evaluator   = new CompositeEvaluator(network);
        var searchEngine = new SearchEngine(gameSettings, evaluator);
        return new OpeningBookEngineDecorator(searchEngine, openingBook, openingBookSettings);
    }

    /// <inheritdoc/>
    public IAiEngine CreateWithHandcrafted()
    {
        var evaluator    = new HandcraftedEvaluator();
        var searchEngine = new SearchEngine(gameSettings, evaluator);
        return new OpeningBookEngineDecorator(searchEngine, openingBook, openingBookSettings);
    }
}
