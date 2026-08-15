# WebView2Utilities process-association analysis

**Reviewed:** 2026-08-14  
**Source:** [`david-risney/WebView2Utilities`](https://github.com/david-risney/WebView2Utilities)

WebView2Utilities combines several imperfect signals rather than relying on a
single parent/child mechanism. Chromium Process Explorer should follow that
model: maintain a generic Chromium process graph, then let platform adapters add
stronger host-association evidence.

## Techniques used by WebView2Utilities

### Parent PID tree

[`ProcessUtil.ParentProcessUtil`](https://github.com/david-risney/WebView2Utilities/blob/main/wv2util/ProcessUtil.cs)
calls `NtQueryInformationProcess(ProcessBasicInformation)` and reads
`InheritedFromUniqueProcessId`. `HostAppList` records that value as `ParentPID`,
indexes runtime entries by PID, and attaches each entry to its parent.

This is broadly applicable to Chromium, Chrome, Edge, WebView2, Electron, CEF,
Qt WebEngine, and other conventional Chromium embedders. Parent PID alone is
not sufficient:

- A parent can exit before inspection.
- Windows can reuse the parent's PID.
- A browser process may be launched by an intermediate bootstrapper.
- A snapshot assembled over time can mix different process generations.
- The host-to-browser relationship differs by embedding platform.

Chromium Process Explorer should store `(PID, creation time)` as process
identity and only accept a parent edge when the parent's creation time precedes
the child's. Prefer one system-wide process snapshot over opening and querying
each process independently.

### Chromium role classification

[`HostAppList.GetUserDataPathAndProcessTypeFromProcessViaCommandLine`](https://github.com/david-risney/WebView2Utilities/blob/main/wv2util/HostAppList.cs)
reads:

- `--type=<role>` to identify renderer, GPU, utility, and other subprocesses.
- `--user-data-dir=<path>` to identify the runtime profile.
- Absence of `--type` as the browser-process convention.

This logic is generally useful across Chromium products and should become a
shared Chromium command-line parser. It remains a heuristic: embedders can add
switches, roles change between releases, and unrelated programs can use the
same argument names. Preserve the raw command line and record the evidence used
for each inferred role.

### WebView2 host association through HWNDs

[`HostAppList.AddRuntimeProcessInfoToHostAppEntriesByHwndWalking`](https://github.com/david-risney/WebView2Utilities/blob/main/wv2util/HostAppList.cs)
starts with top-level windows owned by a known host PID and walks descendants.
It looks for leaf classes:

- `Chrome_WidgetWin_0`
- `Windows.UI.Core.CoreComponentInputSource`

For each leaf, it obtains either its child HWND or the `CrossProcessChildHWND`
window property, then uses `GetWindowThreadProcessId` to find the process that
owns the cross-process child. That PID is treated as the WebView2 browser
process.

[`HwndUtil`](https://github.com/david-risney/WebView2Utilities/blob/main/wv2util/HwndUtil.cs)
uses both `FindWindowEx` and `EnumChildWindows` because each can discover HWNDs
the other misses.

This is strong WebView2-specific evidence because it finds a live UI
relationship rather than merely a creation relationship. Some pieces can help
with other windowed Chromium embedders:

- HWND ownership and cross-process window relationships are generic.
- `Chrome_WidgetWin_*` classes can be supporting Chromium evidence.
- `CrossProcessChildHWND` and the exact topology must remain WebView2-specific
  until validated for each platform.
- Windowless, offscreen, hidden, and not-yet-initialized content will not
  produce this evidence.

### Loaded-module fingerprints

[`ProcessUtil.GetInterestingDllsUsedByPid`](https://github.com/david-risney/WebView2Utilities/blob/main/wv2util/ProcessUtil.cs)
enumerates modules with `CreateToolhelp32Snapshot` and looks for WebView2 DLLs,
including:

- `EmbeddedBrowserWebView.dll`
- `WebView2Loader.dll`
- `Microsoft.Web.WebView2.Core.dll`
- WebView2 WinForms and WPF assemblies

This is platform-specific but suggests a reusable adapter design. Electron,
CEF, Qt WebEngine, CefSharp, and other platforms can contribute their own
module and resource fingerprints. Module matches should identify a likely
platform, not create parent/child edges by themselves.

Module enumeration is sensitive to process architecture, access rights,
protected processes, and exit races. A failed scan must be represented as
unknown rather than as evidence that a platform is absent.

### Mojo named-pipe discovery

[`HostAppList.GetHostAppEntriesFromMachineByPipeEnumeration`](https://github.com/david-risney/WebView2Utilities/blob/main/wv2util/HostAppList.cs)
enumerates `\\.\pipe\`, finds names containing `mojo.`, parses a PID from the
name, and verifies that the process loaded WebView2 modules.

Mojo is general Chromium infrastructure, so its presence can be generic
Chromium evidence. The pipe-name format is an implementation-dependent
heuristic: it can change, an embedded PID can become stale, and non-WebView2
Chromium processes also use Mojo. Use this only for candidate discovery,
followed by executable, command-line, module, creation-time, and process-tree
validation.

## Proposed architecture

### Generic process graph

Capture all relevant processes in one snapshot with:

- PID and creation time
- Parent PID
- Executable path and file identity
- Raw command line
- Parsed `--type`, `--user-data-dir`, and other Chromium switches
- Session, user, integrity level, architecture, and package identity
- Owned top-level and relevant child HWNDs

Create OS parent edges only after validating process generations. Classify a
process with confidence-bearing evidence rather than an enum derived from its
executable name.

### Platform adapters

| Adapter | High-value evidence |
| --- | --- |
| WebView2 | Loaded WebView2 DLLs; host HWND to `CrossProcessChildHWND`; `msedgewebview2.exe`; profile/runtime metadata |
| Electron | Packaged executable/resources; Electron version metadata; Electron command-line conventions |
| CEF | `libcef.dll`, CEF resources/locales, configured subprocess executable, CEF command-line conventions |
| Browser | Product executable/version/install channel and browser-profile conventions |
| Generic Chromium | `--type`, Chromium resources/modules, parent tree, Mojo and window-class hints |

### Distinct relationship types

Keep different edges separate:

- `os-parent`: Windows process creation relationship
- `chromium-subprocess`: inferred Chromium browser-to-child relationship
- `embedded-by`: host application to embedded browser process
- `owns-window`: process-to-HWND relationship
- `cross-process-window`: HWND topology linking host and browser processes
- `shares-profile`: processes using the same normalized user-data directory

Every inferred edge should include its evidence, confidence, and timestamp.
This prevents a WebView2 HWND association from being confused with an OS parent
relationship and allows conflicting evidence to be displayed honestly.

## Improvements over the reviewed implementation

1. **Avoid a null-parent race.** `HostAppList` reads `parentProcess.Id` while
   constructing an entry before checking whether `GetParentProcess()` returned
   null.
2. **Guard against PID reuse.** Validate parent and child creation times and use
   creation time as part of process identity.
3. **Snapshot first.** Repeated live queries can produce a tree assembled from
   different moments. Capture the process table first, then enrich entries.
4. **Preserve access failures.** Record access denied, process exited,
   architecture mismatch, or unsupported query rather than broadly swallowing
   failures.
5. **Separate detection from association.** Loaded DLLs and Mojo pipes indicate
   candidates; PPIDs and HWND topology provide relationship evidence.
6. **Make window discovery optional.** It cannot cover headless, offscreen,
   hidden, or not-yet-initialized Chromium content.
7. **Retain raw evidence.** Store command lines, matched module paths, HWND
   classes/properties, and each edge's source.

## Recommended implementation order

1. Implement a generation-safe Windows process snapshot and OS parent graph.
2. Add the shared Chromium command-line parser and process-role classification.
3. Port WebView2 module detection as a platform adapter.
4. Port the HWND algorithm behind a WebView2-specific evidence provider.
5. Add Electron and CEF adapters using the same graph and evidence model.
6. Experimentally validate Mojo pipe naming before using it as an optional
   discovery accelerator.
