# WebView2Utilities compatibility matrix

**Reviewed:** 2026-08-20  
**Source baseline:** [`david-risney/WebView2Utilities` README](https://github.com/david-risney/WebView2Utilities/blob/main/README.md)
and the checked-in
[process-association analysis](webview2-process-association.md)

Chromium Process Explorer is not a drop-in replacement for
WebView2Utilities. It implements the read-only diagnostics that fit a
generation-safe, evidence-bearing process graph and generalizes them across
WebView2, browsers, Electron, CEF, Qt WebEngine, NW.js, and browser-managed
apps. It intentionally does not reproduce registry mutation, automatic
debugging changes, or collection of sensitive dump/log contents.

## Status definitions

- **Implemented** - the capability is available through the named surfaces.
- **Partial / platform-specific** - useful evidence is available, but the
  WebView2Utilities presentation or certainty is not reproduced.
- **Intentionally different** - the same diagnostic goal is served through the
  evidence/confidence model or a different interaction.
- **Out of scope** - mutation, launching external configuration tools,
  downloading software, or unsafe collection is not a product capability.
- **Planned** - reserved for a committed compatibility item linked to an open,
  focused implementation issue.

There are currently no **Planned** rows. Any future planned compatibility item
must add a focused GitHub issue and link it in this matrix before implementation.

## WebView2-specific feature matrix

| Source feature (WebView2Utilities) | Target Core API | CLI surface | GUI surface | Status | Limits / intentional differences | Related issue |
|---|---|---|---|---|---|---|
| List running WebView2 host apps | `ChromiumProcessDiscovery.DiscoverAsync`, `WebView2RuntimeAnalysis.Processes` | `process-tree`, JSON | Process graph/tree | **Implemented** | Results retain process generations and partial-access issues instead of presenting a flat authoritative host list. | [#2](https://github.com/david-risney/ChromiumProcessExplorer/issues/2) |
| Associate host, browser, and Chromium subprocesses using Mojo, HWNDs, and parentage | `ProcessGraph`, `WebView2HostAssociation`, `WindowSnapshotResult`, `MojoPipeInspectionResult` | `process-tree --windows`, `mojo-pipes` | Typed relationships, Mojo | **Intentionally different** | OS-parent, `embedded-by`, Mojo, and cross-process-window edges stay distinct. HWND evidence is optional; PID plus creation time guards against reuse. No single heuristic is promoted to truth. | [#1](https://github.com/david-risney/ChromiumProcessExplorer/issues/1), [#2](https://github.com/david-risney/ChromiumProcessExplorer/issues/2) |
| “Discover more” slower scan | `ChromiumProcessDiscovery`, bounded providers | `process-tree --all --windows` | Refresh with cancellation | **Intentionally different** | Core snapshots first and performs bounded enrichment by default. `--all` controls display scope; `--windows` opts into HWND evidence. There is no mode that silently changes association authority. | [#1](https://github.com/david-risney/ChromiumProcessExplorer/issues/1), [#12](https://github.com/david-risney/ChromiumProcessExplorer/issues/12) |
| Host integrity level and elevation | `ProcessDetailsResult`, `ProcessDetailEntry` | `process-details` | Process details | **Implemented** | Integrity and elevation can be unknown when token access is denied. Packaged identity is exposed, but AppContainer token details are not currently a separate field. | [#7](https://github.com/david-risney/ChromiumProcessExplorer/issues/7) |
| SDK DLL version | Loaded-module evidence in `ProcessSnapshotEntry` and process details | `process-details --include-sensitive --json` | Process details JSON | **Partial / platform-specific** | WebView2 SDK/client module paths are retained as evidence, but module file versions are not summarized as one authoritative “SDK version”; multiple SDK-related DLLs can coexist. | [#2](https://github.com/david-risney/ChromiumProcessExplorer/issues/2), [#14](https://github.com/david-risney/ChromiumProcessExplorer/issues/14) |
| Probable UI framework (WinForms/WPF/WinUI) and API kind (Win32/WinRT/.NET) | `WebView2ProcessInfo.Evidence`, loaded-module details | `process-details --include-sensitive --json` | Process details JSON | **Intentionally different** | Matching modules are preserved, but the product does not collapse them into a single probable framework/API label. Consumers can apply their own versioned taxonomy without losing raw evidence. | [#2](https://github.com/david-risney/ChromiumProcessExplorer/issues/2), [#14](https://github.com/david-risney/ChromiumProcessExplorer/issues/14) |
| Runtime path, version, and channel for a running host | `WebView2RuntimeAnalysis`, `InstallationDiscoveryResult`, process loaded modules | `process-tree --json`, `process-details`, `installations` | Process details, Installations | **Partial / platform-specific** | Runtime installations expose path/version/channel. A host-to-exact-runtime binding is reported only when process evidence supports it; installed runtime metadata is not substituted for an unknown live binding. | [#2](https://github.com/david-risney/ChromiumProcessExplorer/issues/2), [#9](https://github.com/david-risney/ChromiumProcessExplorer/issues/9) |
| User-data folder and browser PID | `ProcessSnapshotEntry.UserDataDirectory`, `WebView2HostAssociation` | `process-tree --json`, `process-details` | Process graph, Process details | **Implemented** | Sensitive paths are redacted by default in process details. Missing command-line/HWND access remains unknown rather than guessed. | [#2](https://github.com/david-risney/ChromiumProcessExplorer/issues/2), [#7](https://github.com/david-risney/ChromiumProcessExplorer/issues/7) |
| Watch for host-process changes every three seconds | Repeated caller-driven discovery snapshots | Re-run command | Refresh, stale-process retention, cancellation | **Intentionally different** | The GUI does not poll continuously by default. Explicit refresh avoids background privileged scanning and preserves exited process generations as stale rows. | [#12](https://github.com/david-risney/ChromiumProcessExplorer/issues/12) |
| List installed WebView2 runtimes and Edge preview channels | `InstallationDiscoveryResult`, `ChromiumInstallation` | `installations` | Installations | **Implemented** | Discovery combines known locations, registry, packages, filesystem markers, and running processes; inaccessible sources and confidence remain visible. | [#9](https://github.com/david-risney/ChromiumProcessExplorer/issues/9) |
| Create a report ZIP containing displayed data, crash dumps, and Chromium logs | `DiagnosticArtifactDiscoveryResult`, versioned result models | `diagnostics`, `process-details`, other `--json` commands | No combined report/bundle action | **Intentionally different** | Discovery is passive and does not read or bundle dump/log contents. Paths and switch values are redacted unless explicitly requested. Users choose what JSON and sensitive artifacts to collect and share. | [#8](https://github.com/david-risney/ChromiumProcessExplorer/issues/8), [#12](https://github.com/david-risney/ChromiumProcessExplorer/issues/12) |
| Discover logs, crash dumps, Crashpad/WER state, and logging switches | `DiagnosticArtifactDiscoveryResult` | `diagnostics [--include-sensitive]` | Not currently exposed as a dedicated view | **Partial / platform-specific** | Core/CLI report metadata and configuration only; no capture, upload, content read, or success-shaped fallback is performed. | [#8](https://github.com/david-risney/ChromiumProcessExplorer/issues/8), [#12](https://github.com/david-risney/ChromiumProcessExplorer/issues/12) |
| Open DevTools / inspect remote-debugging access | `CdpDiscoveryResult`, `RendererEnrichmentResult` | `cdp`, `renderer-origins` | CDP | **Intentionally different** | Existing endpoints are passively validated. Private `--remote-debugging-pipe` transports are not hijacked, and the tool does not mutate future WebView2 browser arguments to auto-open DevTools. | [#5](https://github.com/david-risney/ChromiumProcessExplorer/issues/5), [#6](https://github.com/david-risney/ChromiumProcessExplorer/issues/6) |
| Executable path, version, architecture, package, command line, and switches | `ProcessDetailsResult` | `process-details` | Process details | **Implemented** | Sensitive values are redacted by default; per-process access and exit errors are retained. | [#7](https://github.com/david-risney/ChromiumProcessExplorer/issues/7) |
| About/version information | `ProductVersion` | `--version [--json]` | Release/package metadata; no dedicated About tab | **Partial / platform-specific** | API and CLI expose release, informational, and source-revision metadata. The GUI title has no separate About panel. | [#13](https://github.com/david-risney/ChromiumProcessExplorer/issues/13) |

## Unsupported or unsafe WebView2Utilities behaviors

| WebView2Utilities behavior | Status | Reason / alternative | Related issue |
|---|---|---|---|
| Create, edit, or remove WebView2 loader override policy registry entries | **Out of scope** | Chromium Process Explorer and its privileged broker are read-only diagnostics. Registry mutation would expand the threat model and can alter unrelated applications. Inspect effective processes, installations, switches, and diagnostic configuration instead. | [#11](https://github.com/david-risney/ChromiumProcessExplorer/issues/11), [#14](https://github.com/david-risney/ChromiumProcessExplorer/issues/14) |
| Force Evergreen, preview, or Fixed Version runtime selection | **Out of scope** | No runtime-selection policy is written. Installed channels and fixed/app-local evidence are discoverable through `installations`. | [#9](https://github.com/david-risney/ChromiumProcessExplorer/issues/9), [#14](https://github.com/david-risney/ChromiumProcessExplorer/issues/14) |
| Inject browser arguments, auto-open DevTools, or enable logging for future launches | **Out of scope** | Persistent launch mutation is security-sensitive and can change app behavior. The tool passively reports existing switches, CDP transports, and logging configuration. | [#5](https://github.com/david-risney/ChromiumProcessExplorer/issues/5), [#8](https://github.com/david-risney/ChromiumProcessExplorer/issues/8) |
| Override the user-data path | **Out of scope** | The tool discovers and redacts observed paths; it does not redirect application state. | [#7](https://github.com/david-risney/ChromiumProcessExplorer/issues/7), [#14](https://github.com/david-risney/ChromiumProcessExplorer/issues/14) |
| Launch RegEdit at an override key | **Out of scope** | Launching a mutation-oriented external tool is not diagnostics. Registry evidence is surfaced with its source path when read. | [#9](https://github.com/david-risney/ChromiumProcessExplorer/issues/9), [#14](https://github.com/david-risney/ChromiumProcessExplorer/issues/14) |
| Download/install additional WebView2 runtimes | **Out of scope** | Release/install management remains with Microsoft and application deployment tooling. Chromium Process Explorer reports installed runtimes and links evidence, not installers. | [#9](https://github.com/david-risney/ChromiumProcessExplorer/issues/9) |
| Automatically include crash dumps or log contents in a shareable ZIP | **Out of scope** | Dump/log contents can contain credentials, page data, memory, and personal information. JSON and artifact metadata are separable so users can review before sharing. | [#8](https://github.com/david-risney/ChromiumProcessExplorer/issues/8), [#12](https://github.com/david-risney/ChromiumProcessExplorer/issues/12) |

## Cross-platform generalization

| WebView2-specific concept | Chromium Process Explorer generalization |
|---|---|
| Host app list | One process graph with WebView2, Electron, CEF, Qt WebEngine, NW.js, browser/PWA, and generic Chromium annotations. |
| Browser PID association | Typed, confidence-bearing `embedded-by` and `chromium-subprocess` edges; platform adapters contribute evidence without replacing OS ancestry. |
| WebView2 runtime path/channel | Installation records distinguish browser, shared runtime, app-local runtime, packaged app, and browser-managed app models. |
| WebView2 user-data folder | Raw `--user-data-dir` and platform-specific path observations are preserved with redaction and provenance. |
| WebView2 logs/crashes | A common passive artifact model covers Chromium, WebView2, Electron, and CEF without reading contents. |
| WebView2 DevTools | A common CDP transport model distinguishes validated TCP endpoints, configured/unavailable states, and private owned pipes. |
| Probable framework labels | Raw module/layout evidence and confidence are preferred over one unversioned framework guess. |
| Loader overrides | No cross-platform mutation equivalent; launch and registry configuration remains external to the read-only product. |

This matrix is the compatibility contract. A feature being absent from
Chromium Process Explorer does not imply an accidental gap when its row is
marked **Intentionally different** or **Out of scope**.
