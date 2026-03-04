# ChineseChess — Claude Code Guide

## Project Overview
Chinese Chess (Xiangqi) game with AI engine, built as a WPF desktop application.
- **Language**: C# with .NET 9.0
- **UI**: WPF (Windows-only), canvas-based board rendering
- **Architecture**: Clean Architecture with 4 project layers

## Build & Test Commands
```bash
dotnet build                                          # Build entire solution
dotnet test                                           # Run all xUnit tests
dotnet run --project ChineseChess.WPF                 # Run the app
dotnet test ChineseChess.Tests --logger "console;verbosity=detailed"  # Verbose tests
```

## Architecture

Clean Architecture — dependency flow goes inward only (UI → App → Domain; Infra → Domain).

| Project | Role | Key Constraint |
|---|---|---|
| `ChineseChess.Domain` | Core entities & rules | Zero external dependencies |
| `ChineseChess.Application` | Game logic, use cases | Depends on Domain only |
| `ChineseChess.Infrastructure` | AI engine, persistence | Depends on Domain & Application |
| `ChineseChess.WPF` | WPF/MVVM UI | Depends on all layers |

## Key Files

### Domain
- `ChineseChess.Domain/Entities/Board.cs` — 9×10 board (90 squares), core game state
- `ChineseChess.Domain/Entities/Piece.cs` — immutable readonly struct
- `ChineseChess.Domain/Entities/Move.cs` — move data structure (readonly struct)
- `ChineseChess.Domain/Helpers/ZobristHash.cs` — Zobrist hashing support

### Application
- `ChineseChess.Application/Services/GameService.cs` — main game orchestrator
- `ChineseChess.Application/Services/BookmarkManager.cs` — game state save/restore
- `ChineseChess.Application/Interfaces/IAiEngine.cs` — AI engine contract
- `ChineseChess.Application/Interfaces/IGameService.cs` — game service contract

### Infrastructure (AI Engine)
- `ChineseChess.Infrastructure/AI/Search/SearchEngine.cs` — negamax + alpha-beta + iterative deepening
- `ChineseChess.Infrastructure/AI/Search/SearchWorker.cs` — Lazy SMP parallel search helper
- `ChineseChess.Infrastructure/AI/Search/TranspositionTable.cs` — Zobrist-hashed TT with persistence
- `ChineseChess.Infrastructure/AI/Evaluators/HandcraftedEvaluator.cs` — position evaluation
- `ChineseChess.Infrastructure/AI/Evaluators/PieceSquareTables.cs` — PST values

### UI
- `ChineseChess.WPF/Views/ChessBoardView.xaml` — canvas-based board rendering
- `ChineseChess.WPF/Views/ControlPanelView.xaml` — game controls
- `ChineseChess.WPF/Styles/AppTheme.xaml` — visual theme

## Coding Conventions

### C# Style
- **File-scoped namespaces** — `namespace ChineseChess.Domain.Entities;`
- **Nullable reference types** enabled in all projects (`<Nullable>enable</Nullable>`)
- **Implicit usings** enabled
- **Readonly structs** for immutable value types (`Piece`, `Move`)
- **Interface-driven design** — `IAiEngine`, `IGameService`, `IBoard`

### Patterns
- Constructor injection (Microsoft.Extensions.DependencyInjection)
- Async/await + `CancellationToken` for all AI search operations
- Event-based communication between layers (`BoardUpdated`, `GameMessage`, `HintReady`, `ThinkingProgress`)
- Pause/resume signal mechanism for AI thinking control

### Commits
Follow Conventional Commits: `feat:`, `fix:`, `style:`, `docs:`, `test:`, `refactor:`

## AI Engine Architecture
- **Search**: Negamax with alpha-beta pruning + iterative deepening
- **Parallelism**: Lazy SMP via `SearchWorker` helper threads
- **Pruning**: Null-move pruning, quiescence search for tactical stability
- **Caching**: Transposition table with Zobrist hashing (persistent, importable/exportable)
- **Control**: Time limits + pause/resume, difficulty via depth cap

## Language & Localization
- Source code comments are in **Traditional Chinese (zh-TW)**
- UI strings use Traditional Chinese
- Keep this convention when adding new comments or UI text
