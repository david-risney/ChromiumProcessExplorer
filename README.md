# Chromium Process Explorer

Chromium Process Explorer is a Windows diagnostics tool for native developers who
need to inspect Chromium-based applications and their runtime environment. It
brings process relationships, Chromium process roles, logs, launch parameters,
installation details, and executable metadata into one place.

> [!NOTE]
> This project is in early development. The solution provides a reusable .NET
> discovery library, CLI, Windows GUI, privileged broker, and Copilot MCP
> bridge.

## Current implementation

The solution targets .NET 9 on Windows and contains:

- **ChromiumProcessExplorer.Core** - reusable process discovery, Chromium
  command-line parsing, typed process-graph and generation-safe process-tree
  construction, CEF, WebView2, Electron, Qt WebEngine, NW.js, and
  browser-installed app runtime analysis, optional HWND topology, and Mojo
  pipe and installation enumeration. The public APIs can be consumed by the
  CLI, GUI, or other .NET applications.
- **cpe** - a thin command-line wrapper with human-readable and JSON output.
- **ChromiumProcessExplorer** - a WPF frontend for process graph/tree,
  process details, Mojo/CDP evidence, installations, issues, and JSON export.
- **ChromiumProcessExplorer.Core.Tests** - focused tests for command-line
  parsing, discovery, graph construction, runtime adapters, broker/MCP
  contracts, and GUI view-model behavior.

Discovery takes one process snapshot, enriches process metadata with bounded
parallelism, and validates parent relationships with process creation times
when available. Both `process-tree` and `mojo-pipes` consume the same
endpoint-enriched Mojo inspection. Resolved server, client, and handle-owner
PIDs are used as process evidence; the pipe-name PID is only a fallback when no
endpoint can be resolved. The typed graph retains distinct OS-parent and Mojo
edges, their raw evidence, source, confidence, and observation time. The
default process tree and graph contain only processes with Chromium or Mojo
evidence; unrelated ancestors are omitted. Use `--all` for the complete Windows
process snapshot.

### Build and test

Prerequisite: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
dotnet restore ChromiumProcessExplorer.sln
dotnet build ChromiumProcessExplorer.sln --configuration Debug --no-restore
dotnet test ChromiumProcessExplorer.sln --configuration Debug --no-build
```

Check formatting without changing files:

```powershell
dotnet format ChromiumProcessExplorer.sln --no-restore --verify-no-changes
```

### CLI

```powershell
# Show Chromium-related processes, their ancestors, and descendants.
dotnet run --project src\ChromiumProcessExplorer.Cli -- process-tree

# Emit structured output for automation and Copilot integration.
dotnet run --project src\ChromiumProcessExplorer.Cli -- process-tree --json

# Report assembly and release version metadata.
dotnet run --project src\ChromiumProcessExplorer.Cli -- --version --json

# Inspect visible Mojo pipes and resolve server/client endpoint processes.
dotnet run --project src\ChromiumProcessExplorer.Cli -- mojo-pipes

# Find browsers, WebView2 runtimes, and installed Chromium-based applications.
dotnet run --project src\ChromiumProcessExplorer.Cli -- installations

# Discover configured CDP ports and private debugging-pipe transports.
dotnet run --project src\ChromiumProcessExplorer.Cli -- cdp

# Opt in to cooperative/CDP renderer-frame enrichment.
dotnet run --project src\ChromiumProcessExplorer.Cli -- renderer-origins --json

# Add a short, version-sensitive trace correlation experiment.
dotnet run --project src\ChromiumProcessExplorer.Cli -- renderer-origins --trace --json

# Show redacted details for Chromium-related processes.
dotnet run --project src\ChromiumProcessExplorer.Cli -- process-details

# Inspect one PID and explicitly include sensitive paths and command-line values.
dotnet run --project src\ChromiumProcessExplorer.Cli -- process-details --pid 1234 --include-sensitive --json

# Passively discover diagnostic settings and redacted artifact metadata.
dotnet run --project src\ChromiumProcessExplorer.Cli -- diagnostics --json

# Probe the explicitly started local privileged broker.
dotnet run --project src\ChromiumProcessExplorer.Cli -- broker-probe --json

# Emit validated CDP endpoints and unavailable/configured transport states.
dotnet run --project src\ChromiumProcessExplorer.Cli -- cdp --json

# Emit installation records and their supporting evidence as JSON.
dotnet run --project src\ChromiumProcessExplorer.Cli -- installations --json

# Quickly list names and implementation-dependent PID hints without inspecting
# process handles.
dotnet run --project src\ChromiumProcessExplorer.Cli -- mojo-pipes --names-only

# Show every process and bound process metadata enrichment.
dotnet run --project src\ChromiumProcessExplorer.Cli -- process-tree --all --concurrency 4

# Add optional HWND topology for WebView2 host/browser association.
dotnet run --project src\ChromiumProcessExplorer.Cli -- process-tree --windows
```

`mojo-pipes` duplicates candidate file handles into bounded helper processes. Queries
that can block in `NtQueryObject` or `NtQueryInformationFile` have deadlines;
the helper is terminated and replaced after a timeout so the main process
continues with partial results. The footer identifies each timed-out handle's
owner, value, access mask, blocked query stage, and elapsed time; JSON exposes
the same data in `TimedOutQueries`. Administrator access improves coverage.

### Windows GUI

```powershell
dotnet run --project src\ChromiumProcessExplorer.Gui
```

The WPF frontend refreshes process and relationship data asynchronously,
retains stale/exited process generations, displays OS-parent and logical edges
separately with evidence and confidence, and exposes process details, Mojo,
CDP, installations, partial-coverage issues, broker status, cancellation, and
JSON export. All discovery is performed through public Core APIs.

`installations` combines six evidence sources:

- well-known Chromium browser and WebView2 runtime locations;
- per-machine and per-user uninstall registrations from both registry views;
- accessible MSIX/AppX package roots and WindowsApps identity;
- Chrome, Edge, Brave, and Chromium profile app directories, Start-menu
  shortcuts, and current-user file/protocol registrations containing
  `--app-id`;
- bounded scans of Program Files and per-user application folders for markers
  such as `libcef.dll`, `WebView2Loader.dll`, `app.asar`, `nw.dll`, and Qt
  WebEngine libraries; and
- executable folders represented by currently running Chromium processes.

Records retain their evidence and report inaccessible or depth-limited
directories rather than silently presenting the scan as complete. Metadata
includes install type (MSI, Squirrel, NSIS, MSIX/AppX, known location, or
portable), publisher, package identity, PE/package architecture, version
provenance, resources/runtime paths, confidence, and shared-versus-app-local
runtime evidence. Browser-managed apps retain their app ID, browser family,
profile, and shared-runtime relationship rather than being presented as
bundled Chromium installations. Nested dependency/SDK markers are normalized to an
application executable root when possible and are not promoted to standalone
applications without application evidence. Explicit search roots and maximum
depth remain configurable, and scans stop after 50,000 directories by default;
explicit-root scans omit registry/package sources unless those options are
enabled. A WebView2 loader marker alone leaves runtime scope unknown because it
can select either Evergreen/shared or fixed/app-local deployment.

`cdp` parses remote-debugging switches on browser processes, resolves ephemeral
ports through `DevToolsActivePort`, and validates loopback endpoints through a
bounded `/json/version` request. A port is only labeled CDP when the response
contains a matching loopback `webSocketDebuggerUrl`. Existing debugging pipes
are reported as private, already-owned transports only after passive
browser/controller handle correlation; protocol bytes are never read or
written. Branded Chrome 136+ default-profile restrictions are surfaced
explicitly.

`renderer-origins` is deliberately opt-in because frame URLs can be sensitive.
Supported WebView2 `GetProcessExtendedInfosAsync` observations map renderer OS
PIDs to associated frame IDs and sources authoritatively for that cooperative
snapshot. Public CDP `Target.getTargets` and `SystemInfo.getProcessInfo` are
reported as separate topology and process lists because CDP does not expose a
stable target-to-PID join. `--trace` adds a bounded experimental capture;
trace-derived mappings remain version-sensitive, medium-confidence, and
non-authoritative.

`process-details` emits the stable `1.0` diagnostics schema for either one PID
or the Chromium-related snapshot. It includes generation identity, parent PID,
observed and inferred roles, parsed switches, versions, architecture, native
architecture, integrity/elevation, package identity, evidence, and per-process
issues. Paths, command lines, switch values, user-data directories, and loaded
module paths use explicit sensitive-value wrappers and are redacted unless
`--include-sensitive` is supplied.

`diagnostics` is passive-only: it discovers configured/default Chromium,
WebView2, Electron, and CEF log paths, Crashpad and WER locations, dumps,
netlogs, traces, crash configuration, packaged-app deployment logs, and
security-relevant switches. It reads filesystem metadata but never reads
artifact contents, starts a capture, or uploads data. Artifact paths and
switch values are redacted unless `--include-sensitive` is supplied; dumps,
netlogs, traces, logs, and command-line-derived settings are always labeled
potentially sensitive. Any future capture operation requires a separate,
explicit consent flow.
Its versioned JSON schema is currently `1.0`; associated process IDs identify
current processes that led to a location, not the historical process that
created a dump.

### Privileged broker and Copilot

The prototype privileged architecture keeps the CLI/MCP client unelevated and
uses an explicitly started elevated `cpe-broker.exe`. The broker exposes only
typed read-only Core operations over a same-user, same-logon named pipe with
bounded JSON frames, finite deadlines, request IDs, structured errors, and
argument-free audit logs. `.github\mcp.json` and the
`chromium-process-explorer` project skill expose redacted MCP tools without a
generic elevated shell. See [privileged broker](docs/privileged-broker.md) for
the threat model, build/start/stop instructions, error contract, and
least-privilege service migration.

### Releases

Pull requests and `main` builds run restore, build, all tests, and formatting
verification on GitHub-hosted Windows runners. Semantic-version tags publish
self-contained `win-x64` and `win-arm64` ZIPs containing the CLI, GUI, broker,
and MCP server, plus SHA-256 checksums and generated release notes. The
packages are currently unsigned and use portable extraction rather than an
installer. See [release packaging](docs/release-packaging.md) for contents,
installation/uninstallation, architecture tradeoffs, versioning, and explicit
administrator behavior.

### Programmatic use

```csharp
using ChromiumProcessExplorer.Core.Discovery;

ChromiumProcessDiscovery discovery = new();
ChromiumDiscoveryResult result = await discovery.DiscoverAsync();

foreach (ProcessGraphEdge edge in result.ProcessGraph.Edges)
{
    Console.WriteLine($"{edge.Type}: {edge.Source} -> {edge.Target}");
}

foreach (ProcessTreeNode root in result.ProcessTree.Roots)
{
    Console.WriteLine($"{root.Process.ProcessId} {root.Process.ImageName}");
}
```

`IProcessSnapshotProvider`, `IMojoPipeProvider`, and `IInstallationProvider`
allow other projects to replace or extend the built-in Windows providers.

## Supported application types

The tool is designed with specific knowledge of:

- Chromium and Google Chrome
- Microsoft Edge
- WebView2-based applications
- Electron-based applications
- Chromium Embedded Framework (CEF) applications
- Qt WebEngine applications
- NW.js applications
- Browser-installed apps and PWAs
- Corroborated generic Chromium embedders

## Planned capabilities

### Process explorer

Display a grouped process tree that connects a browser or host application to
its Chromium child processes. The view identifies Chromium process roles such
as:

- Browser
- Renderer
- GPU
- Network service
- Utility and other specialized subprocesses

For CEF applications, the tree classifies browser and subprocess roles and can
associate a browser with its native host when generation-safe ancestry and
explicit command-line references corroborate the relationship. For WebView2,
loaded SDK/client modules classify hosts, while generation-safe ancestry,
observed Mojo endpoints, and optional `--windows` HWND topology corroborate
host-to-browser relationships. For Electron, renamed packaged executables are
detected from `resources\app.asar` or loose application metadata; main,
renderer, DevTools, GPU, utility, worker, service-worker, Crashpad, and Node
helper roles retain their raw taxonomies and confidence-scored associations.
Cooperative app-side process data can override passive role inference.
Qt WebEngine and NW.js detection combines helper/runtime names, loaded modules,
filesystem markers, Chromium switches, generation-safe ancestry, executable
directories, and user-data paths. Browser app mode is identified by `--app-id`
or `--app`, then propagated through generation-safe Chromium subprocess
ancestry. A generic Chromium fallback requires at least two corroborating
signals, while known Sciter and Ultralight modules are explicitly excluded and
existing WebView2/Electron/CEF classifications take precedence.

### Runtime diagnostics

Inspect information useful when diagnosing startup, deployment, and runtime
issues, including:

- Process and executable details
- Command-line parameters
- Chromium logging configuration and log output
- User data folder locations
- Product, file, and runtime versions
- Executable paths and metadata
- Browser, runtime, application, and library associations

### Install explorer

Show where Chromium-related software is installed on disk, including browsers,
runtimes, libraries, and applications. Installation records may include:

- Product and component type
- Installation path
- Version
- Release channel
- Architecture and other available metadata

The project aims to provide many of the diagnostic and discovery features
available in WebView2Utilities while extending them across additional
Chromium-based application models.

## Interfaces and architecture

Features are designed to be exposed through two executables backed by shared
.NET code:

- **CLI** - currently supports terminal use, scripting, automation, and
  structured output.
- **GUI** - provides interactive process graph/tree, details, Mojo/CDP,
  installation, issue, and JSON views with refresh and cancellation.

The repository includes a Copilot skill and typed stdio MCP bridge. Copilot
remains unelevated and can call only the broker's fixed, read-only, redacted
operations after the user explicitly starts the broker as administrator.

## Administrative access

Complete cross-process diagnostics generally require administrator access, but
the packaged executables intentionally do not auto-elevate. Basic discovery
runs unelevated and reports inaccessible data as partial coverage. Prefer
explicitly starting the fixed-operation broker as administrator while keeping
the CLI, GUI, MCP server, and Copilot client unelevated; an explicitly elevated
frontend is also possible for direct local use.

Treat diagnostic output as potentially sensitive: command lines, paths, logs,
and user data locations can contain application data or secrets. Review output
before sharing it.

## Project status

The process, Mojo endpoint, and initial installation discovery foundations are
implemented. CEF process roles, deployment layouts, explicit runtime paths,
wrapper markers, risky switches, and confidence-scored browser/subprocess and
host/browser associations are exposed. WebView2 loaded-module and optional
generation-safe HWND evidence are also exposed without changing the strict OS
parent tree. Electron packaged/development layouts, process roles, application
and runtime paths, Windows package identity, and main/child associations are
also exposed. Passive logging diagnostics and the Windows GUI are implemented;
CI and tagged self-contained release packaging are implemented; code signing
and a conventional installer remain future work.

Open design investigations include:

- Additional host-to-browser evidence for CEF and Electron applications
- Production migration of the privileged broker to a demand-start,
  least-privilege Windows service
- The exact compatibility scope with WebView2Utilities

## Research and design notes

Detailed investigations are available under `docs\`, including:

- [WebView2 process association](docs/webview2-process-association.md)
- [Electron on Windows](docs/electron-investigation.md)
- [CEF on Windows](docs/cef-investigation.md)
- [Other Chromium-based platforms](docs/other-chromium-platforms.md)
- [Windows pipe-handle query hangs](docs/windows-pipe-handle-query-hangs.md)
- [Renderer PID to origin mapping](docs/renderer-origin-investigation.md)
- [CDP transports and Windows accessibility](docs/cdp-transports-and-accessibility.md)
- [Windows launch-time command-line instrumentation](docs/windows-command-line-instrumentation.md)
- [Similar projects, reusable ideas, and licensing](docs/similar-projects-and-licensing.md)
- [Admin-capable Copilot integration](docs/admin-copilot-integration.md)
- [Copilot automation](docs/copilot-issue-automation.md)
