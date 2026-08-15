# Copilot instructions

Chromium Process Explorer is a Windows .NET 9 solution.

## Commands

- Restore: `dotnet restore ChromiumProcessExplorer.sln`
- Build: `dotnet build ChromiumProcessExplorer.sln --configuration Debug --no-restore`
- Lint/format check: `dotnet format ChromiumProcessExplorer.sln --no-restore --verify-no-changes`
- Full tests: `dotnet test ChromiumProcessExplorer.sln --configuration Debug --no-build`
- Single test: `dotnet test tests\ChromiumProcessExplorer.Core.Tests\ChromiumProcessExplorer.Core.Tests.csproj --configuration Debug --no-build --filter "FullyQualifiedName~TestClassOrMethodName"`

## Architecture

- `src\ChromiumProcessExplorer.Core` is the reusable, programmatic API used by
  all front ends. Keep process discovery, evidence, graph construction, and
  platform adapters here.
- `src\ChromiumProcessExplorer.Cli` is a thin wrapper responsible only for
  argument parsing, formatting, cancellation, and exit codes.
- `tests\ChromiumProcessExplorer.Core.Tests` contains focused xUnit tests.
- A future GUI must consume the core library rather than duplicate discovery.

## Conventions

- Target `net9.0-windows` and preserve nullable reference type safety.
- Treat warnings as errors and run `dotnet format`.
- Keep native Windows calls behind provider classes in the core library.
- Capture process identity before parallel enrichment. Use PID plus creation
  time to guard against PID reuse.
- Treat command-line, pipe-name, module, and HWND observations as evidence;
  preserve raw values and do not present heuristics as authoritative edges.
- Bound parallel work. Potentially hanging foreign-handle queries must use the
  helper-process design described in `docs\windows-pipe-handle-query-hangs.md`.
- Keep handle-query deadlines finite and positive. A timed-out worker process
  must be terminated and replaced before processing more handles.
- Surface partial results and per-item errors instead of silently dropping
  inaccessible or exited processes.
