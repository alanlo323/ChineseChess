using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 外部引擎 adapter 的 Application 層介面。
/// 讓 ViewModel（WPF 層）依賴抽象而非 Infrastructure 具體型別，
/// 符合 Clean Architecture 依賴方向（UI → Application，不依賴 Infrastructure）。
/// </summary>
public interface IExternalEngineAdapter : IAiEngine
{
    /// <summary>握手後解析到的引擎名稱（如 "Pikafish 2026-01-02"）。</summary>
    string EngineName { get; }

    /// <summary>引擎是否為 Pikafish（大小寫不敏感）。</summary>
    bool IsPikafish { get; }

    /// <summary>啟動引擎 process 並完成握手協議。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>向引擎發送帶值選項（如 MultiPV、Skill Level）。</summary>
    Task SendOptionAsync(string name, string value);

    /// <summary>向引擎發送無值選項（如 Clear Hash）。</summary>
    Task SendButtonOptionAsync(string name);
}
