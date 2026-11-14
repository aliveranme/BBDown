# AGENTS.md

## Project

BBDown — command-line Bilibili downloader (C#, .NET 10). Projects:
- `BBDown/` — CLI app (Spectre.Console.Cli, subcommands in `Commands/`) + API server mode
- `BBDown.Core/` — engine library: link fetchers (`Fetcher/`), download/mux, Widevine DRM, danmaku, gRPC protos
- `BBDown.Tests/` — xUnit suite

## Commands

```bash
dotnet build BBDown.sln -c Release          # CI builds Release

dotnet restore BBDown.sln --locked-mode    # Full six-RID graph; do not pass -r to restore

# Unit tests (what PR CI gates on):
dotnet test BBDown.sln -c Release --no-build --filter "Category!=Integration&Category!=NetworkIntegration&Category!=LocalIntegration"

# Local integration — needs ffmpeg on PATH; tests early-return silently if missing:
dotnet test BBDown.sln -c Release --filter "Category=LocalIntegration"

# Real-network integration — hits live Bilibili APIs, flaky by nature (non-blocking in CI):
dotnet test BBDown.sln -c Release --filter "Category=NetworkIntegration"

# Single test class:
dotnet test --filter "FullyQualifiedName~MuxerArgsTests"

dotnet format BBDown.sln                    # MUST pass --verify-no-changes in CI; run before committing

dotnet publish BBDown -c Release -r win-x64 --no-restore # After full locked restore

dotnet run --project BBDown -- --help       # quick manual run
```

## Hard constraints

- SDK pinned by `global.json` (.NET 10.0.403, rollForward latestPatch).
- **Native AOT everywhere**: `BBDown/Directory.Build.props` sets `PublishAot=true`. No dynamic reflection; JSON serialization must use source-generator contexts (see `MyOptionJsonContext` in `Program.cs`). Trim/AOT warnings are expanded and checked against `scripts/aot-warnings-baseline.txt`; do not add reflection or warning suppressions casually. CI uses SDK 10.0.403 within the pinned latestPatch policy, matching implicit AOT dependencies in the lock files.
- **Format gate is hard CI**: UTF-8, LF line endings, 4-space indent, final newline (`.editorconfig`). On Windows, keep new files LF.
- **`failSkips: true`** in `BBDown.Tests/xunit.runner.json` — `[Fact(Skip = "...")]` fails CI. Guard conditional tests with an early `return` in the body instead (pattern used by ffmpeg tests in `MuxerArgsTests.cs`).
- Protobuf C# in `BBDown.Core` is generated at build time by Grpc.Tools from `APP/**/*.proto` and `DRM/Proto/*.proto`; edit the `.proto` sources, never generated output.
- Tests reach internal members via `InternalsVisibleTo("BBDown.Tests")` (set in `BBDown.csproj`).

## Workflow

- `master` is protected — never push directly. Branch prefixes: `feature/`, `fix/`, `refactor/`, `docs/`, `deps/`. Conventional Commits required (`feat:`, `fix(drm):`, …). Details in `CONTRIBUTING.md`.
- User-facing changes must update docs: `README.md`, `CHANGELOG.md`, and `docs/wiki/` (the GitHub wiki is a separate repo, synced manually via `scripts/sync-wiki.ps1`).
- Backward compatibility: don't break existing CLI options or `BBDown.config` file format.
- `serve` subcommand gotchas: its options (`-l`, `--max-concurrent`, `--serve-token`) are never read from `BBDown.config`; non-loopback listen without a token deliberately refuses to start (so bare `docker run` exiting nonzero is expected, not a bug).
