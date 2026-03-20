using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ChineseChess.Domain.Entities;

public class Board : IBoard
{
    public const int BoardSize = 90;
    public const int Width = 9;
    public const int Height = 10;

    private readonly Piece[] pieces = new Piece[BoardSize];
    private PieceColor turn;
    private ulong zobristKey;

    // Undo 用的歷史紀錄
    private readonly Stack<GameState> history = new Stack<GameState>();

    // 和棋判定用：Zobrist Key 歷史（每次 MakeMove 後紀錄）
    private readonly List<ulong> zobristHistory = new List<ulong>();

    // WXF 長將偵測：記錄每步走完後對方是否被將軍（與 zobristHistory 並排）
    private readonly List<bool> wasCheckAfterMove = new();

    // 無吃子半回合計數器（達 120 觸發和棋，皮卡魚規則）
    private int halfMoveClock;

    // 重要子計數器：車/馬/炮/兵（卒）的總數；歸零時觸發棋子不足和棋（皮卡魚規則）
    private int majorPieceCount;

    private struct GameState
    {
        public Move Move;
        public Piece CapturedPiece;
        public bool IsNullMove;
        public int PreviousHalfMoveClock;
    }

    public PieceColor Turn => turn;
    public ulong ZobristKey => zobristKey;

    /// <summary>無吃子半回合計數。每走一步無吃子 +1，吃子後歸零。</summary>
    public int HalfMoveClock => halfMoveClock;

    /// <summary>本局已走的總步數（含紅黑雙方）。</summary>
    public int MoveCount => history.Count;

    public Board()
    {
        // 初始化空棋盤
        for (int i = 0; i < BoardSize; i++) pieces[i] = Piece.None;
        turn = PieceColor.Red;
        zobristKey = 0;
        // 記錄初始局面（空棋盤）的 Key，供和棋判定使用
        zobristHistory.Add(zobristKey);

        // Turn 的初始 Zobrist（若要納入 Turn）
        // zobristKey ^= ZobristHash.SideToMoveKey; // 若紅方先手且要對黑方 XOR，反之亦然。
        // 假設 Turn 在黑方時將 SideToMoveKey 包進 Key？
        // 通常在換手時會做：Key ^= SideToMoveKey。
        // 開局若為紅方，通常不 XOR；若為黑方，才 XOR。
    }

    public Board(string fen) : this()
    {
        ParseFen(fen);
    }

    public Piece GetPiece(int index)
    {
        if (index < 0 || index >= BoardSize) return Piece.None;
        return pieces[index];
    }
    
    // --- 狀態變更 ---

    public void MakeMove(Move move)
    {
        if (move.From < 0 || move.From >= BoardSize || move.To < 0 || move.To >= BoardSize)
        {
            throw new ArgumentOutOfRangeException("Move index is outside the board.");
        }

        if (move.From == move.To)
        {
            throw new InvalidOperationException("Move source and destination must be different.");
        }

        var piece = pieces[move.From];
        var target = pieces[move.To];
        if (piece.IsNone)
        {
            throw new InvalidOperationException("Cannot move an empty square.");
        }

        if (target.Color == piece.Color)
        {
            throw new InvalidOperationException("Cannot capture own piece.");
        }

        // 更新 Zobrist：移除 From 的棋子
        zobristKey ^= ZobristHash.GetPieceKey(move.From, piece.Color, piece.Type);
        
        // 更新 Zobrist：移除 To 位置的目標棋子（吃子）
        if (!target.IsNone)
        {
            zobristKey ^= ZobristHash.GetPieceKey(move.To, target.Color, target.Type);
        }

        // 更新棋盤
        pieces[move.To] = piece;
        pieces[move.From] = Piece.None;

        // 更新 Zobrist：新增 Piece 到 To
        zobristKey ^= ZobristHash.GetPieceKey(move.To, piece.Color, piece.Type);

        // 交換行棋方
        turn = turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        zobristKey ^= ZobristHash.SideToMoveKey;

        // 維護無吃子計數
        bool isCapture = !target.IsNone;
        int previousHalfMoveClock = halfMoveClock;
        halfMoveClock = isCapture ? 0 : halfMoveClock + 1;

        // 維護重要子計數
        if (isCapture)
        {
            var t = target.Type;
            if (t == PieceType.Horse || t == PieceType.Rook ||
                t == PieceType.Cannon || t == PieceType.Pawn)
                majorPieceCount--;
        }

        // 推入歷史紀錄
        history.Push(new GameState
        {
            Move = move,
            CapturedPiece = target,
            PreviousHalfMoveClock = previousHalfMoveClock,
        });

        // 記錄走完後的 Zobrist Key 到歷史列表（和棋判定用）
        zobristHistory.Add(zobristKey);

        // wasCheckAfterMove 不在此熱路徑中填寫（IsCheck 為 O(90+) 開銷）。
        // 需要長將偵測時，由呼叫端在真實走棋後呼叫 RecordCheckAfterMove()。
        wasCheckAfterMove.Add(false);
    }

    /// <summary>
    /// 更新最後一步的長將記錄（覆寫 MakeMove 放入的預設值 false）。
    /// 由呼叫端在真實走棋後呼叫，以避免在搜尋熱路徑中執行 IsCheck。
    /// </summary>
    public void RecordCheckAfterMove()
    {
        if (wasCheckAfterMove.Count == 0) return;
        wasCheckAfterMove[wasCheckAfterMove.Count - 1] = IsCheck(turn);
    }

    public void UnmakeMove(Move move)
    {
        if (history.Count == 0) throw new InvalidOperationException("No history to undo.");

        var state = history.Pop();
        if (state.Move != move)
        {
            history.Push(state);
            throw new InvalidOperationException("Unmake move does not match history.");
        }

        // 移除最後一筆 Zobrist 歷史（和棋判定用）
        if (!state.IsNullMove && zobristHistory.Count > 0)
        {
            zobristHistory.RemoveAt(zobristHistory.Count - 1);
        }

        // 同步還原 wasCheckAfterMove（與 zobristHistory 並排對齊）
        if (!state.IsNullMove && wasCheckAfterMove.Count > 0)
        {
            wasCheckAfterMove.RemoveAt(wasCheckAfterMove.Count - 1);
        }

        // 還原無吃子計數
        halfMoveClock = state.PreviousHalfMoveClock;

        // 回復行棋方
        turn = turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        zobristKey ^= ZobristHash.SideToMoveKey;

        // 空著不涉及棋子移動，僅還原行棋方與 Zobrist 即可
        if (state.IsNullMove) return;

        // 還原棋子
        var movedPiece = pieces[state.Move.To];
        var capturedPiece = state.CapturedPiece;

        pieces[state.Move.From] = movedPiece;
        pieces[state.Move.To] = capturedPiece;

        // 還原重要子計數
        if (!capturedPiece.IsNone)
        {
            var t = capturedPiece.Type;
            if (t == PieceType.Horse || t == PieceType.Rook ||
                t == PieceType.Cannon || t == PieceType.Pawn)
                majorPieceCount++;
        }

        // 還原 Zobrist（Re-XOR 可逆）
        zobristKey ^= ZobristHash.GetPieceKey(state.Move.To, movedPiece.Color, movedPiece.Type);
        zobristKey ^= ZobristHash.GetPieceKey(state.Move.From, movedPiece.Color, movedPiece.Type);
        if (!capturedPiece.IsNone)
        {
            zobristKey ^= ZobristHash.GetPieceKey(state.Move.To, capturedPiece.Color, capturedPiece.Type);
        }
    }

    public void UndoMove()
    {
        // 直接呼叫 UnmakeMove，若歷史為空則由 UnmakeMove 拋出 InvalidOperationException，
        // 行為與 UnmakeMove 一致（呼叫端應先用 TryGetLastMove 確認可悔棋）
        UnmakeMove(history.Peek().Move);
    }

    public bool TryGetLastMove(out Move move)
    {
        foreach (var state in history)
        {
            if (!state.Move.IsNull)
            {
                move = state.Move;
                return true;
            }
        }

        move = Move.Null;
        return false;
    }

    /// <remarks>
    /// 空著不記錄 zobristHistory 和 wasCheckAfterMove，
    /// 因此 UnmakeNullMove 時亦不需還原這兩個列表。
    /// IsDrawByRepetition 和 IsLikelyPerpetualCheck 應在含空著的路徑中避免呼叫，
    /// 因為這兩個方法依賴 zobristHistory / wasCheckAfterMove 與實際步數對齊。
    /// </remarks>
    public void MakeNullMove()
    {
        zobristKey ^= ZobristHash.SideToMoveKey;
        turn = turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red;

        history.Push(new GameState
        {
            Move = Move.Null,
            CapturedPiece = Piece.None,
            IsNullMove = true
        });
    }

    public void UnmakeNullMove()
    {
        if (history.Count == 0) throw new InvalidOperationException("No history to undo.");
        // 確保彈出的狀態確實是 NullMove，防止呼叫順序錯誤導致棋盤狀態損壞
        Debug.Assert(history.Peek().IsNullMove, "UnmakeNullMove 被呼叫但最後一筆歷史不是 NullMove");
        history.Pop();

        zobristKey ^= ZobristHash.SideToMoveKey;
        turn = turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
    }

        // --- FEN ---
    
    public void ParseFen(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen))
        {
            throw new ArgumentException("Invalid FEN string.");
        }

        // 簡易 FEN 解析器
        // 範例：rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1
        var parts = fen.Split(' ');
        if (parts.Length < 2)
        {
            throw new ArgumentException("Invalid FEN format.");
        }

        var rows = parts[0].Split('/');

        if (rows.Length != 10)
        {
            throw new ArgumentException("Invalid FEN rows.");
        }

        // 備份現有狀態：解析失敗時還原，確保棋盤不處於不一致狀態
        var backupPieces = (Piece[])pieces.Clone();
        var backupTurn = turn;
        var backupZobristKey = zobristKey;
        var backupHalfMoveClock = halfMoveClock;
        var backupMajorPieceCount = majorPieceCount;
        var backupZobristHistory = new List<ulong>(zobristHistory);
        var backupWasCheckAfterMove = new List<bool>(wasCheckAfterMove);

        // 清空棋盤
        for (int i = 0; i < BoardSize; i++) pieces[i] = Piece.None;
        zobristKey = 0;
        history.Clear();
        zobristHistory.Clear();
        wasCheckAfterMove.Clear();
        halfMoveClock = 0;

        try
        {

        for (int r = 0; r < 10; r++)
        {
            int c = 0;
            foreach (char ch in rows[r])
            {
                if (char.IsDigit(ch))
                {
                    int empty = ch - '0';
                    if (empty <= 0 || c + empty > 9)
                    {
                        throw new ArgumentException($"Invalid FEN row length at row {r}.");
                    }
                    c += empty;
                }
                else
                {
                    if (c >= 9)
                    {
                        throw new ArgumentException($"Invalid FEN row length at row {r}.");
                    }

                    int index = r * 9 + c;
                    Piece p = CharToPiece(ch);
                    pieces[index] = p;
                    zobristKey ^= ZobristHash.GetPieceKey(index, p.Color, p.Type);
                    c++;
                }
            }

            if (c != 9)
            {
                throw new ArgumentException($"Invalid FEN row length at row {r}.");
            }
        }

        turn = parts[1] switch
        {
            "w" or "r" => PieceColor.Red,
            "b" or "k" => PieceColor.Black,
            _ => throw new ArgumentException("Invalid FEN side to move.")
        }; // 'w' 為標準表示，'r' 可能也會被使用
        if (turn == PieceColor.Black) zobristKey ^= ZobristHash.SideToMoveKey;

        // 記錄解析後局面的 Key 為起始點（和棋判定用）
        zobristHistory.Add(zobristKey);

        // 解析 halfMoveClock（第 5 個欄位，索引 4）
        if (parts.Length >= 5 && int.TryParse(parts[4], out int parsedHalfMove) && parsedHalfMove >= 0)
        {
            halfMoveClock = parsedHalfMove;
        }

        // 重新計算重要子計數器
        majorPieceCount = 0;
        for (int i = 0; i < BoardSize; i++)
        {
            var t = pieces[i].Type;
            if (t == PieceType.Horse || t == PieceType.Rook ||
                t == PieceType.Cannon || t == PieceType.Pawn)
                majorPieceCount++;
        }

        } // end try
        catch
        {
            // 解析失敗：還原備份狀態，確保棋盤不處於不一致狀態
            Array.Copy(backupPieces, pieces, pieces.Length);
            turn = backupTurn;
            zobristKey = backupZobristKey;
            halfMoveClock = backupHalfMoveClock;
            majorPieceCount = backupMajorPieceCount;
            zobristHistory.Clear();
            zobristHistory.AddRange(backupZobristHistory);
            wasCheckAfterMove.Clear();
            wasCheckAfterMove.AddRange(backupWasCheckAfterMove);
            throw;
        }
    }

    public string ToFen()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 10; r++)
        {
            int empty = 0;
            for (int c = 0; c < 9; c++)
            {
                var p = pieces[r * 9 + c];
                if (p.IsNone)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0)
                    {
                        sb.Append(empty);
                        empty = 0;
                    }
                    sb.Append(p.ToChar());
                }
            }
            if (empty > 0) sb.Append(empty);
            if (r < 9) sb.Append('/');
        }
        
        sb.Append(turn == PieceColor.Red ? " w" : " b");
        sb.Append($" - - {halfMoveClock} 1");
        return sb.ToString();
    }

    private Piece CharToPiece(char c)
    {
        var color = char.IsUpper(c) ? PieceColor.Red : PieceColor.Black;
        var lower = char.ToLower(c);
        var type = lower switch
        {
            'k' => PieceType.King,
            'a' => PieceType.Advisor,
            'b' => PieceType.Elephant, // FEN 預設用 'b' 表示象（象）
            'e' => PieceType.Elephant, // 也支援 'e'
            'n' => PieceType.Horse,    // 'n' 表示馬（horse）
            'h' => PieceType.Horse,    // 也支援 'h'
            'r' => PieceType.Rook,
            'c' => PieceType.Cannon,
            'p' => PieceType.Pawn,
            _ => throw new ArgumentException($"Invalid piece character '{c}' in FEN.")
        };
        return new Piece(color, type);
    }

        // --- 預留的著法產生（占位） ---

    public IEnumerable<Move> GenerateLegalMoves()
    {
        var legalMoves = new List<Move>();
        var pseudoMoves = GeneratePseudoLegalMoves().ToList();
        int kingIndex = GetKingIndex(turn);

        foreach (var move in pseudoMoves)
        {
            if (IsLegalMove(move, kingIndex, turn))
                legalMoves.Add(move);
        }

        return legalMoves;
    }

    public IEnumerable<Move> GenerateCaptureMoves()
    {
        int kingIndex = GetKingIndex(turn);
        var result = new List<Move>();

        foreach (var move in GeneratePseudoLegalMoves())
        {
            if (pieces[move.To].IsNone) continue;          // 只取吃子著法
            if (IsLegalMove(move, kingIndex, turn))
                result.Add(move);
        }

        return result;
    }

    public IEnumerable<Move> GenerateQuietMoves()
    {
        int kingIndex = GetKingIndex(turn);
        var result = new List<Move>();

        foreach (var move in GeneratePseudoLegalMoves())
        {
            if (!pieces[move.To].IsNone) continue;         // 只取安靜著法
            if (IsLegalMove(move, kingIndex, turn))
                result.Add(move);
        }

        return result;
    }

    /// <summary>
    /// 判斷一個偽合法著法是否為真正合法著法（不讓己方將帥被將軍）。
    /// 快速路徑：若走法的 from 與 to 都不在將帥的同行/列上，
    /// 則走法不可能透過車/炮/飛將或馬腳暴露將帥，直接回傳 true。
    /// 將帥本身的移動必須走慢速路徑（完整驗證）。
    /// </summary>
    private bool IsLegalMove(Move move, int kingIndex, PieceColor movingColor)
    {
        var movingPiece = pieces[move.From];

        if (kingIndex >= 0 && movingPiece.Type != PieceType.King)
        {
            int kingRow = kingIndex / Width;
            int kingCol = kingIndex % Width;
            int fromRow = move.From / Width;
            int fromCol = move.From % Width;
            int toRow   = move.To / Width;
            int toCol   = move.To % Width;

            bool couldExposeKing = fromRow == kingRow || fromCol == kingCol
                                || toRow   == kingRow || toCol   == kingCol;

            if (!couldExposeKing) return true;
        }

        // 慢速路徑：完整驗證
        MakeMove(move);
        bool legal = !IsCheck(movingColor);
        UnmakeMove(move);
        return legal;
    }

    public IEnumerable<Move> GeneratePseudoLegalMoves()
    {
        var moves = new List<Move>();

        bool IsInsideBoard(int row, int col) => row >= 0 && row < Height && col >= 0 && col < Width;
        bool IsOwnPiece(int index) => pieces[index].Color == turn;

        void TryAddMove(int from, int to)
        {
            if (to < 0 || to >= BoardSize) return;
            if (IsOwnPiece(to)) return;
            moves.Add(new Move((byte)from, (byte)to));
        }

        bool InSamePalace(int row, int col, PieceColor color)
        {
            if (col < 3 || col > 5) return false;
            return color == PieceColor.Red ? row >= 7 && row <= 9 : row >= 0 && row <= 2;
        }

        bool IsCrossedRiver(Piece piece, int row)
        {
            return piece.Color == PieceColor.Red ? row <= 4 : row >= 5;
        }

        for (int from = 0; from < BoardSize; from++)
        {
            var piece = pieces[from];
            if (piece.Color != turn) continue;
            if (piece.IsNone) continue;

            int fromRow = from / Width;
            int fromCol = from % Width;

            switch (piece.Type)
            {
                case PieceType.King:
                    {
                        int[,] kingDirs = { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
                        for (int i = 0; i < 4; i++)
                        {
                            int toRow = fromRow + kingDirs[i, 0];
                            int toCol = fromCol + kingDirs[i, 1];
                            if (!IsInsideBoard(toRow, toCol)) continue;
                            if (!InSamePalace(toRow, toCol, piece.Color)) continue;
                            TryAddMove(from, toRow * Width + toCol);
                        }
                        break;
                    }

                case PieceType.Advisor:
                    {
                        int[,] advisorDirs = { { -1, -1 }, { -1, 1 }, { 1, -1 }, { 1, 1 } };
                        for (int i = 0; i < 4; i++)
                        {
                            int toRow = fromRow + advisorDirs[i, 0];
                            int toCol = fromCol + advisorDirs[i, 1];
                            if (!IsInsideBoard(toRow, toCol)) continue;
                            if (!InSamePalace(toRow, toCol, piece.Color)) continue;
                            TryAddMove(from, toRow * Width + toCol);
                        }
                        break;
                    }

                case PieceType.Elephant:
                    {
                        int[,] elephantDirs = { { -2, -2 }, { -2, 2 }, { 2, -2 }, { 2, 2 } };
                        for (int i = 0; i < 4; i++)
                        {
                            int toRow = fromRow + elephantDirs[i, 0];
                            int toCol = fromCol + elephantDirs[i, 1];
                            if (!IsInsideBoard(toRow, toCol)) continue;
                            if (IsCrossedRiver(piece, toRow)) continue;
                            int blockRow = fromRow + elephantDirs[i, 0] / 2;
                            int blockCol = fromCol + elephantDirs[i, 1] / 2;
                            int blockIndex = blockRow * Width + blockCol;
                            if (pieces[blockIndex].IsNone)
                            {
                                TryAddMove(from, toRow * Width + toCol);
                            }
                        }
                        break;
                    }

                case PieceType.Horse:
                    {
                        int[][] horseDirs =
                        {
                            new int[] { 2, 1, 1, 0 }, new int[] { 2, -1, 1, 0 }, new int[] { -2, 1, -1, 0 }, new int[] { -2, -1, -1, 0 },
                            new int[] { 1, 2, 0, 1 }, new int[] { 1, -2, 0, -1 }, new int[] { -1, 2, 0, 1 }, new int[] { -1, -2, 0, -1 }
                        };

                        for (int i = 0; i < horseDirs.Length; i++)
                        {
                            int toRow = fromRow + horseDirs[i][0];
                            int toCol = fromCol + horseDirs[i][1];
                            if (!IsInsideBoard(toRow, toCol)) continue;

                            int blockRow = fromRow + horseDirs[i][2];
                            int blockCol = fromCol + horseDirs[i][3];
                            int blockIndex = blockRow * Width + blockCol;

                            if (pieces[blockIndex].IsNone)
                            {
                                TryAddMove(from, toRow * Width + toCol);
                            }
                        }
                        break;
                    }

                case PieceType.Rook:
                case PieceType.Cannon:
                    {
                        bool isCannon = piece.Type == PieceType.Cannon;
                        int[,] slideDirs = { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };

                        for (int dir = 0; dir < 4; dir++)
                        {
                            int dRow = slideDirs[dir, 0];
                            int dCol = slideDirs[dir, 1];

                            bool screenHit = false;
                            int toRow = fromRow + dRow;
                            int toCol = fromCol + dCol;
                            while (IsInsideBoard(toRow, toCol))
                            {
                                int to = toRow * Width + toCol;
                                var target = pieces[to];

                                if (target.IsNone)
                                {
                                    if (!isCannon || !screenHit)
                                    {
                                        TryAddMove(from, to);
                                    }
                                }
                                else
                                {
                                    if (!isCannon)
                                    {
                                        TryAddMove(from, to);
                                        break;
                                    }

                                    if (!screenHit)
                                    {
                                        screenHit = true;
                                    }
                                    else
                                    {
                                        if (target.Color != piece.Color)
                                        {
                                            TryAddMove(from, to);
                                        }
                                        break;
                                    }
                                }

                                toRow += dRow;
                                toCol += dCol;
                            }
                        }

                        break;
                    }

                case PieceType.Pawn:
                    {
                        int forward = piece.Color == PieceColor.Red ? -1 : 1;

                        int forwardRow = fromRow + forward;
                        int forwardCol = fromCol;
                        if (IsInsideBoard(forwardRow, forwardCol))
                        {
                            TryAddMove(from, forwardRow * Width + forwardCol);
                        }

                        if (IsCrossedRiver(piece, fromRow))
                        {
                            int[] sideCols = { fromCol - 1, fromCol + 1 };
                            foreach (var sideCol in sideCols)
                            {
                                if (IsInsideBoard(fromRow, sideCol))
                                {
                                    TryAddMove(from, fromRow * Width + sideCol);
                                }
                            }
                        }
                        break;
                    }

                default:
                    break;
            }
        }

        return moves;
    }
    
    public bool IsCheck(PieceColor color)
    {
        int kingIndex = GetKingIndex(color);
        if (kingIndex < 0) return false;

        var attacker = color == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        return IsSquareAttacked(kingIndex, attacker);
    }

    public bool IsCheckmate(PieceColor color)
    {
        if (!IsCheck(color)) return false;
        // GenerateLegalMoves() 只產生當前 turn 的著法；若 color != turn，結果會是對手的著法
        Debug.Assert(color == turn, "IsCheckmate 必須在目標方輪次時呼叫");
        return !GenerateLegalMoves().Any();
    }

    private int GetKingIndex(PieceColor color)
    {
        for (int i = 0; i < BoardSize; i++)
        {
            if (pieces[i].Color == color && pieces[i].Type == PieceType.King)
            {
                return i;
            }
        }
        return -1;
    }

    private bool IsSquareAttacked(int targetIndex, PieceColor byColor)
    {
        if (targetIndex < 0 || targetIndex >= BoardSize) return false;
        if (pieces[targetIndex].Type == PieceType.None) return false;

        int targetRow = targetIndex / Width;
        int targetCol = targetIndex % Width;

        // 將軍面對面規則：同一個列上若無阻擋，雙方將互相將對方視為被將。
        int kingIndex = GetKingIndex(byColor);
        if (kingIndex >= 0 && pieces[targetIndex].Type == PieceType.King)
        {
            int kingRow = kingIndex / Width;
            int kingCol = kingIndex % Width;
            if (kingCol == targetCol)
            {
                int rowStep = targetRow > kingRow ? 1 : -1;
                bool blocked = false;
                for (int row = kingRow + rowStep; row != targetRow; row += rowStep)
                {
                    if (!pieces[row * Width + kingCol].IsNone)
                    {
                        blocked = true;
                        break;
                    }
                }
                if (!blocked) return true;
            }
        }

        for (int from = 0; from < BoardSize; from++)
        {
            var piece = pieces[from];
            if (piece.Color != byColor) continue;

            if (AttacksSquare(from, piece, targetIndex, targetRow, targetCol))
            {
                return true;
            }
        }

        return false;
    }

    private bool AttacksSquare(int attackerIndex, Piece attacker, int targetIndex, int targetRow, int targetCol)
    {
        if (attacker.IsNone) return false;
        if (attackerIndex == targetIndex) return false;

        int attackerRow = attackerIndex / Width;
        int attackerCol = attackerIndex % Width;
        int dr = targetRow - attackerRow;
        int dc = targetCol - attackerCol;

        bool IsInsideBoard(int row, int col) => row >= 0 && row < Height && col >= 0 && col < Width;
        bool InSamePalace(int row, int col, PieceColor color)
        {
            if (col < 3 || col > 5) return false;
            return color == PieceColor.Red ? row >= 7 && row <= 9 : row >= 0 && row <= 2;
        }
        bool IsCrossedRiver(Piece piece, int row)
        {
            return piece.Color == PieceColor.Red ? row <= 4 : row >= 5;
        }

        switch (attacker.Type)
        {
            case PieceType.King:
                if (Math.Abs(dr) + Math.Abs(dc) != 1) return false;
                return InSamePalace(targetRow, targetCol, attacker.Color);

            case PieceType.Advisor:
                if (Math.Abs(dr) != 1 || Math.Abs(dc) != 1) return false;
                return InSamePalace(targetRow, targetCol, attacker.Color);

            case PieceType.Elephant:
                if (Math.Abs(dr) != 2 || Math.Abs(dc) != 2) return false;
                if (IsCrossedRiver(attacker, targetRow)) return false;
                int elephantBlockRow = attackerRow + dr / 2;
                int elephantBlockCol = attackerCol + dc / 2;
                int elephantBlockIndex = elephantBlockRow * Width + elephantBlockCol;
                return pieces[elephantBlockIndex].IsNone;

            case PieceType.Horse:
                int[][] horseDirs =
                {
                    new int[] { 2, 1, 1, 0 }, new int[] { 2, -1, 1, 0 }, new int[] { -2, 1, -1, 0 }, new int[] { -2, -1, -1, 0 },
                    new int[] { 1, 2, 0, 1 }, new int[] { 1, -2, 0, -1 }, new int[] { -1, 2, 0, 1 }, new int[] { -1, -2, 0, -1 }
                };

                for (int i = 0; i < horseDirs.Length; i++)
                {
                    int expectedDr = horseDirs[i][0];
                    int expectedDc = horseDirs[i][1];
                    if (dr != expectedDr || dc != expectedDc) continue;

                    int blockRow = attackerRow + horseDirs[i][2];
                    int blockCol = attackerCol + horseDirs[i][3];
                    int blockIndex = blockRow * Width + blockCol;
                    return IsInsideBoard(blockRow, blockCol) && pieces[blockIndex].IsNone;
                }
                return false;

            case PieceType.Rook:
            case PieceType.Cannon:
                if (dr != 0 && dc != 0) return false;
                if (dr == 0 && dc == 0) return false;

                int dirRow = Math.Sign(dr);
                int dirCol = Math.Sign(dc);
                int row = attackerRow + dirRow;
                int col = attackerCol + dirCol;
                int blockers = 0;

                while (IsInsideBoard(row, col))
                {
                    int current = row * Width + col;
                    if (current == targetIndex)
                    {
                        if (attacker.Type == PieceType.Rook)
                        {
                            return blockers == 0;
                        }

                        if (attacker.Type == PieceType.Cannon)
                        {
                            return blockers == 1;
                        }

                        return false;
                    }

                    if (!pieces[current].IsNone)
                    {
                        blockers++;

                        if (attacker.Type == PieceType.Rook || blockers >= 2)
                        {
                            return false;
                        }
                    }

                    row += dirRow;
                    col += dirCol;
                }
                return false;

            case PieceType.Pawn:
                int forward = attacker.Color == PieceColor.Red ? -1 : 1;
                if (dr == forward && dc == 0) return true;
                if (!IsCrossedRiver(attacker, attackerRow)) return false;
                return dr == 0 && Math.Abs(dc) == 1;

            default:
                return false;
        }
    }

    public IBoard Clone()
    {
        var b = new Board();
        Array.Copy(pieces, b.pieces, BoardSize);
        b.turn = turn;
        b.zobristKey = zobristKey;
        b.halfMoveClock = halfMoveClock;
        b.majorPieceCount = majorPieceCount;
        // 複製 Zobrist 歷史以便 Clone 後繼續做和棋判定
        b.zobristHistory.AddRange(zobristHistory);
        // 複製將軍歷史以便 Clone 後繼續做 WXF 長將偵測
        b.wasCheckAfterMove.AddRange(wasCheckAfterMove);
        // 搜尋時不複製 Undo 堆疊（僅 Clone 棋盤狀態）
        return b;
    }

    // --- 和棋判定 ---

    /// <summary>
    /// 判定是否達到重覆局面和棋條件。
    /// 預設閾值為 3（同一局面出現三次，含當前局面）。
    /// zobristHistory 的最後一筆就是當前局面 Key（由 MakeMove 和 ParseFen 維護）。
    /// </summary>
    public bool IsDrawByRepetition(int threshold = 3)
    {
        int count = zobristHistory.Count;
        if (count == 0) return false;

        // zobristHistory 最後一筆是當前局面
        ulong currentKey = zobristHistory[count - 1];
        int occurrences = 1; // 當前局面本身算一次

        // 每隔 2 步才是同一行棋方的局面，從倒數第 3 筆（隔 2）開始往前找
        for (int i = count - 3; i >= 0; i -= 2)
        {
            if (zobristHistory[i] == currentKey)
            {
                occurrences++;
                if (occurrences >= threshold) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判定是否達到無吃子步數和棋條件（一百二十步，皮卡魚規則）。
    /// halfMoveClock 計算半步（每方走一步 +1），閾值 120 = 60 全回合。
    /// </summary>
    public bool IsDrawByNoCapture(int limit = 120)
    {
        return halfMoveClock >= limit;
    }

    /// <summary>
    /// 判定棋盤上是否已無車、馬、炮、兵（卒），雙方只剩將帥士象，構成棋子不足和棋（皮卡魚規則）。
    /// </summary>
    public bool IsDrawByInsufficientMaterial() => majorPieceCount == 0;

    /// <summary>
    /// 判定是否達到任一和棋條件（三次重覆局面、一百二十步無吃子或棋子不足）。
    /// </summary>
    public bool IsDraw()
    {
        return IsDrawByRepetition() || IsDrawByNoCapture() || IsDrawByInsufficientMaterial();
    }

    /// <summary>
    /// 輕量長將模式偵測：檢查最近同一方的最後 3 步是否都是將軍且無吃子。
    /// 用於搜尋引擎快速評分，不做完整 WXF 裁決。
    /// 條件：wasCheckAfterMove 最後 6 筆中的奇數索引全為 true，且 halfMoveClock ≥ 6。
    /// </summary>
    public bool IsLikelyPerpetualCheck()
    {
        int count = wasCheckAfterMove.Count;
        if (count < 6) return false;

        // 同一方的最後 3 步（隔 2 步 = 同一方），從最新往前掃
        for (int i = count - 1; i >= count - 6; i -= 2)
        {
            if (i < 0) return false;
            if (!wasCheckAfterMove[i]) return false;
        }

        // 確認此段無吃子（halfMoveClock 代表最近無吃子步數）
        return halfMoveClock >= 6;
    }

    /// <summary>
    /// 檢查最近 n 個半步的 Zobrist 雜湊序列中，是否有任何局面重複出現。
    ///
    /// 用途：在引擎尚未實作完整 WXF 長將/長捉裁決規則前，
    /// 作為 ProbCut 等前向剪枝的「重複風險」守衛。
    /// 若最近局面已在循環，代入機率剪枝可能污染評估邊界（因循環可能是勝/負而非和），
    /// 此時應禁用 ProbCut，回到完整搜尋。
    ///
    /// 注意：這只是啟發式近似，不等同於 WXF 正式裁決。
    /// </summary>
    public bool IsAnyRepetitionInLastN(int n)
    {
        int count = zobristHistory.Count;
        if (count < 2) return false;

        int start = Math.Max(0, count - n);
        var seen = new HashSet<ulong>(n);
        for (int i = start; i < count; i++)
        {
            if (!seen.Add(zobristHistory[i]))
                return true;
        }
        return false;
    }
}
