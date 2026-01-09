# Chinese Chess AI (C# / WPF / .NET 10)

A modular, clean-architecture Chinese Chess (Xiangqi) application with a robust AI engine.

## 專案結構 (Project Structure)

The solution follows Clean Architecture principles:

- **ChineseChess.Domain**: Core entities (`Board`, `Piece`, `Move`) and game rules. Zero dependencies.
- **ChineseChess.Application**: Game state management (`GameService`), interfaces (`IAiEngine`), and use cases.
- **ChineseChess.Infrastructure**: AI implementation (`SearchEngine`, `Evaluator`, `TranspositionTable`) and data persistence.
- **ChineseChess.WPF**: Desktop UI using MVVM pattern, Dependency Injection, and modern XAML.
- **ChineseChess.Tests**: Unit tests for Domain rules and AI algorithms.

## 建置與執行 (Build & Run)

Requirements: .NET 9.0 SDK (or 10.0 Preview)

1. Open `ChineseChess.sln` in Visual Studio or Cursor.
2. Build the solution.
3. Run `ChineseChess.WPF` as the startup project.

Command Line:
```bash
dotnet build
dotnet run --project ChineseChess.WPF
```

## AI 引擎設計 (AI Design)

The AI engine (`ChineseChess.Infrastructure`) implements:

- **Search Framework**: Negamax with Alpha-Beta Pruning and Iterative Deepening.
- **Transposition Table (TT)**: Uses Zobrist Hashing to cache search results and handle transpositions.
- **Move Ordering**: Prioritizes PV moves, Captures (MVV-LVA), and Killer moves.
- **Evaluation**: Handcrafted evaluation function based on Material balance and Piece-Square Tables (PST).
- **Selective Search**:
    - **Quiescence Search**: Extends search at leaf nodes to resolve unstable positions (captures).
    - **Null-Move Pruning**: Skips moves in safe positions to save search effort.

## 如何調整 AI 難度 (Difficulty Adjustment)

Difficulty is controlled by the Search Depth:

- **Easy**: Depth 1-3.
- **Medium**: Depth 4-6.
- **Hard**: Depth 7+ (requires optimization for speed).

Adjust the slider in the UI to change the depth.

## Features

- **Game Modes**: Player vs Player, Player vs AI, AI vs AI.
- **Controls**: Undo, Restart, Bookmarks.
- **Analysis**: Real-time hint and search stats (Nodes, Depth, Score).
