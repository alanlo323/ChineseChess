using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace ChineseChess.Domain.Entities;

public class Board : IBoard
{
    public const int BoardSize = 90;
    public const int Width = 9;
    public const int Height = 10;

    private readonly Piece[] _pieces = new Piece[BoardSize];
    private PieceColor _turn;
    private ulong _zobristKey;
    
    // History for Undo
    private readonly Stack<GameState> _history = new Stack<GameState>();

    private struct GameState
    {
        public Move Move;
        public Piece CapturedPiece;
        public ulong ZobristKey;
        // Can add more like Rule50, etc.
    }

    public PieceColor Turn => _turn;
    public ulong ZobristKey => _zobristKey;

    public Board()
    {
        // Initialize empty
        for (int i = 0; i < BoardSize; i++) _pieces[i] = Piece.None;
        _turn = PieceColor.Red;
        _zobristKey = 0;
        
        // Initial Zobrist for Turn (if we include it)
        // _zobristKey ^= ZobristHash.SideToMoveKey; // If Red starts and we XOR for Black, or vice versa. 
        // Let's say Key includes SideToMoveKey if it's Black's turn?
        // Usually: Key ^= SideToMoveKey when switching turns.
        // If start is Red, we don't XOR. If Black, we XOR.
    }

    public Board(string fen) : this()
    {
        ParseFen(fen);
    }

    public Piece GetPiece(int index)
    {
        if (index < 0 || index >= BoardSize) return Piece.None;
        return _pieces[index];
    }
    
    // --- State Manipulation ---

    public void MakeMove(Move move)
    {
        var piece = _pieces[move.From];
        var target = _pieces[move.To];

        // Update Zobrist - Remove Piece at From
        _zobristKey ^= ZobristHash.GetPieceKey(move.From, piece.Color, piece.Type);
        
        // Update Zobrist - Remove Target at To (Capture)
        if (!target.IsNone)
        {
            _zobristKey ^= ZobristHash.GetPieceKey(move.To, target.Color, target.Type);
        }

        // Update Board
        _pieces[move.To] = piece;
        _pieces[move.From] = Piece.None;

        // Update Zobrist - Add Piece at To
        _zobristKey ^= ZobristHash.GetPieceKey(move.To, piece.Color, piece.Type);

        // Switch Turn
        _turn = _turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        _zobristKey ^= ZobristHash.SideToMoveKey;

        // Push History
        _history.Push(new GameState
        {
            Move = move,
            CapturedPiece = target,
            ZobristKey = _zobristKey // Store the NEW key? Or OLD? Usually store state to restore.
            // Wait, if I store Key in history, I should store the OLD key to restore it easily, 
            // OR I can re-calculate the XORs (reversible).
            // Storing the old key is safer if we have irreversible state. 
            // Actually, Zobrist is fully reversible with XORs.
            // Let's store the key BEFORE the move to be safe/simple? 
            // But if I rely on XORs, I don't need to store it.
            // Let's store the Old Key for verification/simplicity.
        });
    }

    public void UnmakeMove(Move move)
    {
        if (_history.Count == 0) throw new InvalidOperationException("No history to undo.");
        
        var state = _history.Pop();
        // Verify move matches?
        if (state.Move != move) 
        {
            // For safety, just trust the stack if called correctly. 
            // Ideally UnmakeMove() shouldn't take args if it uses stack.
            // But if interface requires it... I'll ignore the arg or check it.
        }

        // 1. Switch Turn Back
        _turn = _turn == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
        // _zobristKey ^= ZobristHash.SideToMoveKey; // Handled by restoring or XORing back

        // 2. Restore Pieces
        var movedPiece = _pieces[state.Move.To];
        var capturedPiece = state.CapturedPiece;

        _pieces[state.Move.From] = movedPiece;
        _pieces[state.Move.To] = capturedPiece;

        // 3. Restore Zobrist (Re-XORing is cleaner than storing if we trust the logic, 
        // but restoring from state is robust against bugs)
        // Let's re-XOR to prove correctness or just restore.
        // Since I didn't store the OLD key in the struct above (I stored the NEW one in the comment logic, let's fix that).
        
        // Let's change MakeMove to store the PREVIOUS key or just re-calculate.
        // Re-calculation:
        // Key is currently Key_After.
        // Key ^= SideToMove (Back to previous turn side)
        // Remove Piece at To
        // Add Piece at From
        // Add Captured at To (if any)
        
        _zobristKey ^= ZobristHash.SideToMoveKey;
        _zobristKey ^= ZobristHash.GetPieceKey(state.Move.To, movedPiece.Color, movedPiece.Type);
        _zobristKey ^= ZobristHash.GetPieceKey(state.Move.From, movedPiece.Color, movedPiece.Type);
        if (!capturedPiece.IsNone)
        {
            _zobristKey ^= ZobristHash.GetPieceKey(state.Move.To, capturedPiece.Color, capturedPiece.Type);
        }
    }

    public void UndoMove()
    {
        if (_history.Count > 0)
        {
            UnmakeMove(_history.Peek().Move);
        }
    }

    // --- FEN ---
    
    public void ParseFen(string fen)
    {
        // Very basic parser
        // Example: rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1
        var parts = fen.Split(' ');
        var rows = parts[0].Split('/');
        
        if (rows.Length != 10) throw new ArgumentException("Invalid FEN rows");

        // Clear board
        for (int i = 0; i < BoardSize; i++) _pieces[i] = Piece.None;
        _zobristKey = 0;

        for (int r = 0; r < 10; r++)
        {
            int c = 0;
            foreach (char ch in rows[r])
            {
                if (char.IsDigit(ch))
                {
                    c += (ch - '0');
                }
                else
                {
                    int index = r * 9 + c;
                    Piece p = CharToPiece(ch);
                    _pieces[index] = p;
                    _zobristKey ^= ZobristHash.GetPieceKey(index, p.Color, p.Type);
                    c++;
                }
            }
        }

        _turn = parts[1] == "w" || parts[1] == "r" ? PieceColor.Red : PieceColor.Black; // 'w' is standard, 'r' sometimes used
        if (_turn == PieceColor.Black) _zobristKey ^= ZobristHash.SideToMoveKey;
        
        // TODO: Move clocks/counters parsing
    }

    public string ToFen()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 10; r++)
        {
            int empty = 0;
            for (int c = 0; c < 9; c++)
            {
                var p = _pieces[r * 9 + c];
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
        
        sb.Append(_turn == PieceColor.Red ? " w" : " b");
        sb.Append(" - - 0 1"); // Dummy counters
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
            'b' => PieceType.Elephant, // FEN standard uses 'b' for Elephant (Bishop equivalent)
            'e' => PieceType.Elephant, // Support 'e' as well
            'n' => PieceType.Horse,    // 'n' for Knight/Horse
            'h' => PieceType.Horse,    // Support 'h'
            'r' => PieceType.Rook,
            'c' => PieceType.Cannon,
            'p' => PieceType.Pawn,
            _ => PieceType.None
        };
        return new Piece(color, type);
    }

    // --- Placeholder Move Gen ---

    public IEnumerable<Move> GenerateLegalMoves()
    {
        // TODO: Full implementation
        // For now return empty or simple
        return new List<Move>(); 
    }
    
    public IEnumerable<Move> GeneratePseudoLegalMoves()
    {
        var moves = new List<Move>();
        // Iterate pieces of current turn
        for (int i = 0; i < BoardSize; i++)
        {
            var p = _pieces[i];
            if (p.Color == _turn)
            {
                // Generate moves for this piece
                // This requires logic for each piece type
            }
        }
        return moves;
    }

    public bool IsCheck(PieceColor color)
    {
        // TODO: Find King, check attacks
        return false;
    }

    public bool IsCheckmate(PieceColor color)
    {
        return false;
    }

    public IBoard Clone()
    {
        var b = new Board();
        Array.Copy(_pieces, b._pieces, BoardSize);
        b._turn = _turn;
        b._zobristKey = _zobristKey;
        // History not cloned usually for search, but deep clone might need it
        return b;
    }
}
