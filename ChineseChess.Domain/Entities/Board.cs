using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using System;
using System.Collections.Generic;
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

    // 無吃子半回合計數器（達 60 觸發和棋）
    private int halfMoveClock;

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

        // 推入歷史紀錄
        history.Push(new GameState
        {
            Move = move,
            CapturedPiece = target,
            PreviousHalfMoveClock = previousHalfMoveClock,
        });

        // 記錄走完後的 Zobrist Key 到歷史列表（和棋判定用）
        zobristHistory.Add(zobristKey);
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
        if (history.Count > 0)
        {
            UnmakeMove(history.Peek().Move);
        }
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

        // 清空棋盤
        for (int i = 0; i < BoardSize; i++) pieces[i] = Piece.None;
        zobristKey = 0;
        history.Clear();
        zobristHistory.Clear();
        halfMoveClock = 0;

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

        // TODO：解析著法時計數（move clocks / counters）
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
        sb.Append(" - - 0 1"); // 暫存計數器（原本以便用於後續欄位）
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

        foreach (var move in pseudoMoves)
        {
            var movingColor = turn;
            MakeMove(move);
            if (!IsCheck(movingColor))
            {
                legalMoves.Add(move);
            }
            UnmakeMove(move);
        }

        return legalMoves;
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
        // 複製 Zobrist 歷史以便 Clone 後繼續做和棋判定
        b.zobristHistory.AddRange(zobristHistory);
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
    /// 判定是否達到無吃子步數和棋條件（六十步）。
    /// </summary>
    public bool IsDrawByNoCapture(int limit = 60)
    {
        return halfMoveClock >= limit;
    }

    /// <summary>
    /// 判定是否達到任一和棋條件（三次重覆局面或六十步無吃子）。
    /// </summary>
    public bool IsDraw()
    {
        return IsDrawByRepetition() || IsDrawByNoCapture();
    }
}
