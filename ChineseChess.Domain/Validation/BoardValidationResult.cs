using System.Collections.Generic;

namespace ChineseChess.Domain.Validation;

/// <summary>局面合法性驗證結果。</summary>
public sealed class BoardValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; }

    public BoardValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }

    public static BoardValidationResult Valid() => new BoardValidationResult(System.Array.Empty<string>());
}
