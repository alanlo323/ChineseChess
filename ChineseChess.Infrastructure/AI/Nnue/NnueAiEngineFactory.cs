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
/// 根據 NnueEngineConfig 建立帶獨立 NnueNetwork 的引擎實例，
/// 每個引擎持有自己的 NnueNetwork（獨立記憶體），互不干擾。
/// </summary>
public sealed class NnueAiEngineFactory : IAiEngineFactory
{
    private readonly GameSettings gameSettings;
    private readonly IOpeningBook openingBook;
    private readonly OpeningBookSettings openingBookSettings;

    public NnueAiEngineFactory(
        GameSettings gameSettings,
        IOpeningBook openingBook,
        OpeningBookSettings openingBookSettings)
    {
        this.gameSettings        = gameSettings;
        this.openingBook         = openingBook;
        this.openingBookSettings = openingBookSettings;
    }

    /// <inheritdoc/>
    public async Task<IAiEngine> CreateWithNnueAsync(NnueEngineConfig config, CancellationToken ct = default)
    {
        // 每個引擎持有獨立的 NnueNetwork 實例（各自的模型記憶體）
        var network = new NnueNetwork();
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
