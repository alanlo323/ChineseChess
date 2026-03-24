using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Nnue.Features;

/// <summary>
/// 中線鏡像（Mid-Mirror）64-bit 位置編碼。
/// 當棋局左右對稱時，需要對特徵做水平鏡像以確保 NNUE 特徵的一致性。
///
/// 編碼格式（63-0 bit）：
///   bit 63     = Middle King（王在 E 列時為 1）
///   bits 60-62 = B_ADVISOR 計數
///   bits 57-59 = B_BISHOP 計數
///   bits 53-56 = B_PAWN 計數（4 bits）
///   bits 50-52 = B_KNIGHT 計數
///   bits 47-49 = B_CANNON 計數
///   bits 44-46 = B_ROOK 計數
///   bit  43    = Guarding bit
///   bits 36-42 = B_ADVISOR square
///   bits 29-35 = B_BISHOP square
///   bits 21-28 = B_PAWN square（8 bits）
///   bits 14-20 = B_KNIGHT square
///   bits  7-13 = B_CANNON square
///   bits  0-6  = B_ROOK square
///
/// 完全對應 Pikafish half_ka_v2_hm.h MidMirrorEncoding。
/// </summary>
public static class MidMirrorEncoder
{
    private const ulong BalanceEncoding = 0xa4a92a74e989d3a7UL;

    // shifts[pieceType][0] = count bit shift, [1] = square bit shift
    // PieceType 0=None/King(skipped), 1=King, 2=Advisor, 3=Elephant, 4=Horse, 5=Rook, 6=Cannon, 7=Pawn
    // 對應 Pikafish PieceType: ROOK=1→shifts[1]={44,0}, ADVISOR=2→{60,36}, CANNON=3→{47,7},
    //   PAWN=4→{53,21}, KNIGHT=5→{50,14}, BISHOP=6→{57,29}
    // C# PieceType: Rook=5, Advisor=2, Cannon=6, Pawn=7, Horse=4, Elephant=3, King=1
    private static readonly (int CountShift, int SquareShift)[] Shifts = new (int, int)[8]
    {
        (0, 0),   // 0=None
        (0, 0),   // 1=King（特殊處理）
        (60, 36), // 2=Advisor
        (57, 29), // 3=Elephant → BISHOP
        (50, 14), // 4=Horse → KNIGHT
        (44, 0),  // 5=Rook
        (47, 7),  // 6=Cannon
        (53, 21), // 7=Pawn（4 bits，需確認不溢出）
    };

    /// <summary>
    /// 計算指定顏色所有棋子的中線鏡像編碼總和。
    /// </summary>
    public static ulong ComputeEncoding(IBoard board, PieceColor color)
    {
        ulong encoding = 0;
        for (int csIdx = 0; csIdx < 90; csIdx++)
        {
            var piece = board.GetPiece(csIdx);
            if (piece.IsNone || piece.Color != color) continue;

            encoding += ComputePieceEncoding(piece.Type, csIdx, color);
        }
        return encoding;
    }

    /// <summary>
    /// 判斷指定視角是否需要中線鏡像。
    /// 需同時計算雙方編碼，因此建議一次呼叫取得雙方編碼後再判斷。
    /// </summary>
    public static bool RequiresMidMirror(ulong ownEncoding, ulong enemyEncoding)
    {
        // 雙方王均不在 E 列（bit 63 同時為 1）
        if (((1UL << 63) & ownEncoding & enemyEncoding) == 0)
            return false;

        // 己方編碼 < BalanceEncoding（左翼偏重），或平衡但敵方更「左」
        return ownEncoding < BalanceEncoding
            || (ownEncoding == BalanceEncoding && enemyEncoding < BalanceEncoding);
    }

    /// <summary>
    /// 完整計算雙方編碼並判斷是否需要中線鏡像。
    /// </summary>
    public static bool RequiresMidMirror(IBoard board, PieceColor perspective)
    {
        ulong ownEnc = ComputeEncoding(board, perspective);
        ulong enemyEnc = ComputeEncoding(board, FlipColor(perspective));
        return RequiresMidMirror(ownEnc, enemyEnc);
    }

    /// <summary>
    /// 單一棋子的中線編碼。
    /// 與 Pikafish MidMirrorEncoding[piece][sq] 完全相容。
    /// </summary>
    public static ulong ComputePieceEncoding(PieceType type, int csIdx, PieceColor color)
    {
        if (type == PieceType.None) return 0;

        int row = csIdx / 9, col = csIdx % 9;

        // Pikafish 座標：rank = 9 - row, file = col
        int rank = 9 - row;
        int file = col;  // 0=A, 4=E, 8=I

        // 王在非 E 列：貢獻 bit 63
        if (type == PieceType.King)
            return file != 4 ? (1UL << 63) : 0;

        // E 列棋子：貢獻為 0
        if (file == 4) return 0;

        var (countShift, squareShift) = Shifts[(int)type];

        // 正規化：BLACK 做垂直翻轉，右翼做水平映射
        int r_ = color == PieceColor.Red ? rank : 9 - rank;     // WHITE=RED 不翻，BLACK 翻
        int f_ = file < 4 ? file : 8 - file;                    // 右翼 → 映射到左翼

        // 計算左翼編碼：計數 +1, 位置 = (3 - f_) * 10 + r_
        //   3 = FILE_D（Pikafish）
        ulong enc = (1UL << countShift)
                  | ((ulong)((3 - f_) * 10 + r_) << squareShift);

        // 右翼取負（補數）
        if (file > 4) enc = (ulong)(-(long)enc);

        return enc;
    }

    private static PieceColor FlipColor(PieceColor color) =>
        color == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
}
