# Renderer process to origin investigation

Research date: **2026-08-14**

## Bottom line

A **passive external Windows tool cannot reliably map a stock Chromium renderer OS PID to the current tab/origin/URL** with high accuracy. Chromium’s **browser process has the authoritative mapping** (`RenderProcessHost` ↔ `RenderFrameHost`/`SiteInstance`/workers/WebContents), but the **public APIs split topology from PIDs**: CDP `Target.*` gives target/frame relationships, while `SystemInfo.getProcessInfo` gives OS PIDs, and there is **no stable public target→PID join**. The best results come from: **(1) cooperative platform APIs** (best: **WebView2**, then **Electron/Qt**, partial **CEF**), or **(2) optional remote-debugging enrichment**, ideally with **Tracing** or Chromium internal pages. `chrome://process-internals` and Task Manager prove Chromium can do the mapping internally, but they are **internal/version-sensitive**; `chrome://process-internals` also uses **Chromium child-process IDs, not OS PIDs**. `chrome://discards/graph` is the strongest internal surface I found because it models **pages/frames/workers/processes** and includes a **process PID**. **Thread names, WER, and crash metadata are not useful live origin sources**; crash keys may contain URL/origin data only in crash-time reporting.  
Key evidence: `chromium/chromium:content/public/browser/render_process_host.h:335-365`, `chromium/chromium:content/public/browser/render_frame_host.h:271-275`, `chromium/chromium:content/browser/devtools/protocol/target_handler.cc:96-125`, `chromium/chromium:content/browser/devtools/protocol/tracing_handler.cc:197-235`.

---

## Feasibility matrix

| Approach | Cooperation / flags | Accuracy | Coverage | Cost | Security / fragility | Portability | Verdict |
|---|---|---:|---|---|---|---|---|
| **Passive OS-only** | none | **Low** for origin/URL; medium for role | sees all processes, but not reliable tab/frame/origin | low | safest, stable | high | good default **only for role/process tree** |
| **CDP basic** (`Browser/Target/SystemInfo`) | remote debugging enabled; on **Google Chrome 136+** use non-default `--user-data-dir` | **Medium** for target topology, **Low** for PID join | pages/iframes/workers/browserContext/opener, but **no stable PID join** | low-med | public API, but opens debug surface | Chromium family | useful optional enrichment, not sufficient alone |
| **CDP + Tracing** | same as above | **Med-high** while tracing is active | strong for active frame↔PID correlation; workers/version nuances remain | med-high | more overhead; event schema is less contractual | Chromium family | best browser-only enrichment path |
| **Internal WebUI / task-model scraping** | debug access or browser-controlled helper | **High** | very good, incl. frames/workers/processes | med | **internal/version-sensitive** | Chromium only | experimental only |
| **WebView2 API** | app cooperation | **High** | strong for active associated frames + OS PID | low | supported API | Windows/WebView2 | **best supported Windows adapter** |
| **Electron API** | app cooperation | **High** main-frame, **Medium** overall | main renderer + target mapping; OOPIF/worker PID gaps | low-med | supported API | Electron | strong adapter |
| **Qt WebEngine API** | app cooperation | **High** main-frame, **Medium** overall | main-frame PID + DevTools target id; child-frame gaps | low-med | supported API | Qt | useful adapter |
| **CEF API** | app cooperation | **Medium** | task titles/types/browser ids; no public task PID | low-med | supported but partial | CEF | partial adapter |
| **Crash/WER/Crashpad** | crash only | **Low live**, medium postmortem | postmortem only | low runtime | not a live API | Windows | not a live mapper |

**Note:** elevation is usually **not** required unless you inspect another user/session’s processes.

---

## Confirmed findings

### 1) Chromium’s process model does **not** support “PID == tab/origin”
- `SiteInstance` is Chromium’s core process-assignment unit: documents/workers with the same principal in the same browsing context group must share a process. It is **usually site-keyed** (scheme + eTLD+1) but increasingly **origin-keyed** for OAC, embedder-dedicated origins, `chrome://`, and extensions; in practice it may still contain more than one site in some cases. `chromium/chromium:content/public/browser/site_instance.h:31-119`
- Desktop Chromium defaults to strong isolation, but still does **aggressive same-site reuse** for OOPIFs, fenced frames, and often service workers; special cases include extensions, WebUI, guest views, sandboxed iframes, `data:` URLs, error pages, and a **spare unlocked renderer**. `chromium/chromium:docs/process_model_and_site_isolation.md:286-341`, `chromium/chromium:docs/process_model_and_site_isolation.md:352-390`, `chromium/chromium:docs/process_model_and_site_isolation.md:400-482`
- A renderer can legitimately have **no visible active page** because `RenderFrameHost` lifecycle includes `kPrerendering` and `kInBackForwardCache`, not just `kActive`. `chromium/chromium:content/public/browser/render_frame_host.h:706-798`
- Extensions/workers also break naive tab assumptions: extension documents/workers use dedicated processes, while content scripts run in page renderers; service workers are generally reused aggressively. `chromium/chromium:docs/process_model_and_site_isolation.md:332-351`, `chromium/chromium:docs/process_model_and_site_isolation.md:415-420`

### 2) Inside the browser process, the authoritative join is **PID → RenderProcessHost → frames/sites/workers**
- `RenderProcessHost::GetProcess()` is the OS process object; `GetID()` / `GetDeprecatedID()` are **Chromium child-process IDs**. Chromium can iterate all hosts with `AllHostsIterator()` and look them up by child ID with `FromID()`. `chromium/chromium:content/public/browser/render_process_host.h:335-365`, `chromium/chromium:content/public/browser/render_process_host.h:853-861`
- `RenderFrameHost` exposes exactly the data an in-browser mapper needs: process, site instance, last committed URL/origin, lifecycle state, and page. `chromium/chromium:content/public/browser/render_frame_host.h:271-275`, `chromium/chromium:content/public/browser/render_frame_host.h:542-545`, `chromium/chromium:content/public/browser/render_frame_host.h:846-852`
- Chromium’s internal `process-internals` page walks all `WebContents`, includes **active root frames**, **BFCache roots**, and **prerender roots**, plus SiteInstance lock/origin-keying info — but its `FrameInfo.process_id` is set from `GetDeprecatedID()`, i.e. **child-process ID, not OS PID**. `chromium/chromium:content/browser/process_internals/process_internals_handler_impl.cc:35-102`, `chromium/chromium:content/browser/process_internals/process_internals_handler_impl.cc:144-159`, `chromium/chromium:content/browser/process_internals/process_internals_handler_impl.cc:295-330`
- Chromium’s internal `discards` graph is stronger: it emits **frame URL**, **page main-frame URL**, **worker URL**, graph relationships, and **process PID**. Frame/worker nodes refer to a graph process node, which then contains the OS PID. `chromium/chromium:chrome/browser/ui/webui/discards/graph_dump_impl.cc:491-559`
- Task Manager code confirms Chromium internally tracks:
  - main renderer task = OS process handle + child-process ID `chromium/chromium:chrome/browser/task_manager/providers/web_contents/renderer_task.cc:61-87`, `chromium/chromium:chrome/browser/task_manager/providers/web_contents/renderer_task.cc:144-146`
  - OOPIF/subframe task = **subframe process**, not main-frame process `chromium/chromium:chrome/browser/task_manager/providers/web_contents/subframe_task.cc:29-38`, `chromium/chromium:chrome/browser/task_manager/providers/web_contents/subframe_task.cc:58-79`
  - dedicated/shared/service workers + script URLs `chromium/chromium:chrome/browser/task_manager/providers/per_profile_worker_task_tracker.cc:57-118`, `chromium/chromium:chrome/browser/task_manager/providers/per_profile_worker_task_tracker.cc:130-141`
  - prerender/fenced-frame tasks `chromium/chromium:chrome/browser/task_manager/providers/web_contents/prerender_task.cc:16-29`, `chromium/chromium:chrome/browser/task_manager/providers/web_contents/fenced_frame_task.cc:16-24`
  - guest-view tasks `chromium/chromium:chrome/browser/task_manager/providers/web_contents/guest_tag.h:10-24`

**Implication:** if you ever add a cooperative in-browser helper, the correct algorithm is to match **OS PID to `RenderProcessHost::GetProcess().Pid()`**, then walk frames/workers from browser-side objects. For a purely external tool, you don’t have this object graph.

---

## 3) CDP: good topology, weak PID mapping

### Public CDP facts
- `Browser.getVersion` returns **protocolVersion, product, revision, userAgent, jsVersion** — useful for versioning, not mapping. `ChromeDevTools/devtools-protocol:pdl/domains/Browser.pdl:176-185`
- `SystemInfo.getProcessInfo` returns `{ type, id, cpuTime }`, where `id` is the **OS PID**; Chromium’s implementation enumerates `RenderProcessHost::AllHostsIterator()` and uses `process.Pid()`. It is **browser-target only**. `ChromeDevTools/devtools-protocol:pdl/domains/SystemInfo.pdl:81-89`, `ChromeDevTools/devtools-protocol:pdl/domains/SystemInfo.pdl:112-116`, `chromium/chromium:content/browser/devtools/protocol/system_info_handler.h:26-34`, `chromium/chromium:content/browser/devtools/protocol/system_info_handler.cc:300-359`
- `Target.getTargets` / `TargetInfo` give `targetId`, `type`, `title`, `url`, `attached`, `parentId`, `openerId`, `openerFrameId`, `parentFrameId`, `browserContextId`, `subtype`, `embedderData` — but **no PID**. `ChromeDevTools/devtools-protocol:pdl/domains/Target.pdl:14-40`, `ChromeDevTools/devtools-protocol:pdl/domains/Target.pdl:203-211`, `chromium/chromium:content/browser/devtools/protocol/target_handler.cc:96-125`
- `Target.attachToTarget` returns a `sessionId`; `Target.setAutoAttach` / `attachedToTarget` are how you recursively follow iframes/workers. `ChromeDevTools/devtools-protocol:pdl/domains/Target.pdl:71-82`, `ChromeDevTools/devtools-protocol:pdl/domains/Target.pdl:225-290`
- `Performance.getMetrics` only returns metric name/value pairs; it does **not** expose PID mapping. `ChromeDevTools/devtools-protocol:pdl/domains/Performance.pdl:7-39`

### Important limitation
Chromium internally **does** know a target’s process ID (`DevToolsAgentHostImpl::ProcessHostChanged()` stores `host->GetProcess().Pid()`), but `BuildTargetInfo()` does **not** expose it. `chromium/chromium:content/browser/devtools/devtools_agent_host_impl.cc:142-150`, `chromium/chromium:content/browser/devtools/devtools_agent_host_impl.cc:606-624`, `chromium/chromium:content/browser/devtools/protocol/target_handler.cc:96-125`

### Advanced CDP path that actually helps
Tracing can emit `ProcessReadyInBrowser` and frame data containing **frame token + URL + processId**. `chromium/chromium:content/browser/devtools/protocol/tracing_handler.cc:197-235`

**Conclusion:**  
- **Plain CDP**: good for **target/frame/session topology**, **not** enough for **stable PID→target**.  
- **CDP + Tracing**: viable advanced enrichment path.

### Implemented tracing experiment

Chromium Process Explorer's opt-in `renderer-origins --trace` capture requests
the `navigation` and `disabled-by-default-devtools.timeline` categories for a
bounded interval. It recognizes frame records carrying `frame`/`frameId`,
`url`, and `processId`/`pid`, including frame arrays emitted by browser tracing
events. These records are useful correlations, but their event shape is not a
stable public target-to-PID API. The tool therefore marks trace-derived
mappings as medium-confidence, non-authoritative, snapshot-lifetime evidence.
Unsupported or empty traces remain explicit partial-result issues.

---

## 4) Remote debugging transport and security

- Chromium supports both `--remote-debugging-port` and `--remote-debugging-pipe`. `chromium/chromium:content/public/common/content_switches.cc:590-597`
- HTTP mode exposes `/json/version`, `/json/protocol`, `/json/list`, `/json/new` (PUT), and the browser websocket path. `chromium/chromium:content/browser/devtools/devtools_http_handler.cc:463-478`, `chromium/chromium:content/browser/devtools/devtools_http_handler.cc:617-650`, `chromium/chromium:content/browser/devtools/devtools_http_handler.cc:658-677`, `chromium/chromium:content/browser/devtools/devtools_http_handler.cc:845-860`
- `DevToolsActivePort` contains the selected port on line 1 and the browser websocket path on line 2. `chromium/chromium:content/browser/devtools/devtools_http_handler.cc:273-305`
- Chromium binds the debugging HTTP server to localhost in upstream code. `chromium/chromium:chrome/browser/devtools/remote_debugging_server.cc:65-149`
- On desktop, upstream Chromium source only enables the new **default-user-data-dir** block unconditionally for **Google Chrome branded** builds; the official Chrome team also documented this in **“Changes to remote debugging switches to improve security”** (Chrome 136+, 2025-03-17). Source: `chromium/chromium:chrome/browser/devtools/remote_debugging_server.cc:151-174`, `chromium/chromium:chrome/browser/devtools/remote_debugging_server.h:23-37`. Official doc: https://developer.chrome.com/blog/remote-debugging-port
- Pipe transport is launch-time/cooperative and does **not** use `/json/*` or `DevToolsActivePort`; it is much less suitable for attaching to an already-running browser. `chromium/chromium:chrome/browser/devtools/remote_debugging_server.cc:293-305`

### Applicability
- **Chrome / Chromium / browsers**: yes, via port or pipe; Chrome branded builds have the new default-profile restriction.
- **CEF**: official `remote_debugging_port`; ephemeral port + `DevToolsActivePort` are documented. `chromiumembedded/cef:include/internal/cef_types.h:418-427`
- **NW.js**: official remote debugging port, **SDK flavor only**. `nwjs/nw.js:docs/For Users/Debugging with DevTools.md:1-26`
- **Qt WebEngine**: `--remote-debugging-port` or `QTWEBENGINE_REMOTE_DEBUGGING`; Qt also documents remote origin handling. `qt/qtwebengine:src/core/doc/src/qtwebengine-debugging.qdoc:23-57`
- **Electron**: if you control the app, the in-process `debugger` API is a better transport than external remote-debugging. `electron/electron:docs/api/debugger.md:1-78`
- **WebView2**: you usually want in-process APIs (`GetProcessExtendedInfosAsync`, `CallDevToolsProtocolMethodAsync`) instead of external remote-debugging. Official docs: https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/chromium-devtools-protocol

---

## 5) Windows thread names / ETW / crash / WER

- On Windows, Chromium sets thread names via **`SetThreadDescription`** and, if a debugger is present, the classic **`0x406D1388` exception** naming path. `chromium/chromium:base/threading/platform_thread_win.cc:41-65`, `chromium/chromium:base/threading/platform_thread_win.cc:251-268`
- Browser and renderer main threads are named `CrBrowserMain` and `CrRendererMain`. That is useful for **role** identification, not origin/URL. `chromium/chromium:content/browser/browser_main_loop.cc:642-648`, `chromium/chromium:content/renderer/renderer_main.cc:190-196`
- Chromium crash keys may include URL/origin-related data, but that is **crash-time** metadata. The docs explicitly say common crash keys include webpage URL, switches, extension IDs, etc. `chromium/chromium:docs/debugging_with_crash_keys.md:11-35`
- A concrete renderer-host example sets crash keys like `navigation_url`, `initiator_rfh_origin`, `last_committed_origin`, `parent_etc_origin`, and `opener_origin`. `chromium/chromium:content/browser/renderer_host/ipc_utils.cc:104-171`
- Chromium’s Windows WER integration is a **runtime exception helper module** (`chrome_wer`), tightly coupled to Crashpad; it is **not** a documented live origin-query API. `chromium/chromium:components/crash/win/README.md:1-17`
- Crashpad handler annotations I verified are generic (`ptype`) plus the **stability report** user stream. `chromium/chromium:components/crash/core/app/run_as_crashpad_handler_win.cc:29-53`, `chromium/chromium:components/crash/core/app/run_as_crashpad_handler_win.cc:82-90`
- The Windows stability report stream contains **process_id, memory, handle counts**, not page/origin/tab identity. `chromium/chromium:components/stability_report/stability_report.proto:15-57`, `chromium/chromium:components/stability_report/user_stream_data_source_win.cc:27-41`, `chromium/chromium:components/stability_report/user_stream_data_source_win.cc:44-91`
- Breadcrumbs can contain per-tab navigation actions, but they are crash-report breadcrumbs, consent-gated, and not a live external API. `chromium/chromium:components/breadcrumbs/README.md:1-9`

**My conclusion on the “Watson black box APIs” idea:** I found **no evidence** in current Chromium source that it pre-registers useful origin metadata through classic WER APIs like `WerRegisterMemoryBlock`, `WerRegisterFile`, or `WerSetFlags`.

---

## 6) Platform-specific cooperative APIs

### WebView2 — **best Windows answer**
Official WebView2 docs provide the strongest supported mapping:
- `CoreWebView2Environment.GetProcessExtendedInfosAsync`
- `CoreWebView2ProcessExtendedInfo.AssociatedFrameInfos`
- `CoreWebView2ProcessInfo.ProcessId`
- `CoreWebView2FrameInfo.FrameId`
- `CoreWebView2FrameInfo.Source`

The docs describe `GetProcessExtendedInfosAsync` as returning process infos **including associated frame infos**, `AssociatedFrameInfos` as the frame infos actively running in that renderer, `ProcessId` as the OS process ID, and `Source` as the frame document URI. Official docs:
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.getprocessextendedinfosasync
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2processextendedinfo.associatedframeinfos
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2processinfo.processid
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2frameinfo.frameid
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2frameinfo.source

### Electron — strong, but mostly **main-frame**
- `webContents.getOSProcessId()` returns the OS PID of the associated renderer. `electron/electron:docs/api/web-contents.md:2315-2324`
- `webContents.getProcessId()` returns the **Chromium internal PID**, comparable to `frameProcessId` in frame navigation events. `electron/electron:docs/api/web-contents.md:2315-2324`
- `webContents.fromDevToolsTargetId(targetId)` maps a DevTools target ID to a `WebContents` if one exists. `electron/electron:docs/api/web-contents.md:86-104`
- Source confirms both PID getters use the **primary main frame’s** process. `electron/electron:shell/browser/api/electron_api_web_contents.cc:2666-2679`
- Source also shows `fromDevToolsTargetId` is effectively `DevToolsAgentHost::GetForId(target_id)->GetWebContents()`. `electron/electron:shell/browser/api/electron_api_web_contents.cc:5103-5110`
- Electron’s `debugger` class is an alternate CDP transport and can send commands to child sessions via `sessionId`. `electron/electron:docs/api/debugger.md:1-78`

**Implication:** Electron can do **main-frame** PID mapping very well, but OOPIF/worker OS PID mapping still needs extra cooperation.

### Qt WebEngine — good for main-frame + CDP target
- `QWebEnginePage::renderProcessPid()` returns the render-process PID for the **current page’s main frame**. `qt/qtwebengine:src/core/api/qwebenginepage.cpp:1184-1195`
- `QWebEnginePage::devToolsId()` / `WebEngineView::devToolsId` gives the DevTools page ID so you can build `ws://localhost:<port>/devtools/page/<id>`. `qt/qtwebengine:src/core/api/qwebenginepage.cpp:2212-2221`, `qt/qtwebengine:src/webenginequick/doc/src/webengineview_lgpl.qdoc:1337-1346`
- Remote debugging is officially documented. `qt/qtwebengine:src/core/doc/src/qtwebengine-debugging.qdoc:23-57`

### CEF — partial
- `remote_debugging_port` is supported and documented; port 0 can be ephemeral and CEF documents `DevToolsActivePort` behavior. `chromiumembedded/cef:include/internal/cef_types.h:418-427`, `chromiumembedded/cef:docs/general_usage.md:530-548`
- `CefTaskManager` gives task IDs, task info, and `GetTaskIdForBrowserId`, and tasks sharing a process are grouped together. `chromiumembedded/cef:include/cef_task_manager.h:43-96`
- But `cef_task_info_t` has **no PID field** — only task id/type/title/cpu/memory/gpu memory. `chromiumembedded/cef:include/internal/cef_types.h:4187-4216`

### NW.js — minimal
- Official docs: remote debugging via `--remote-debugging-port`, but **SDK flavor only**. `nwjs/nw.js:docs/For Users/Debugging with DevTools.md:1-26`
- I did **not** find a stronger supported PID→origin API.

---

## Supported vs internal vs rejected

### Supported / public
- CDP `Browser.getVersion`, `Target.*`, `SystemInfo.getProcessInfo`, `Tracing` (advanced)
- WebView2 `GetProcessExtendedInfosAsync`
- Electron `webContents.getOSProcessId/getProcessId/fromDevToolsTargetId/debugger`
- Qt `renderProcessPid/devToolsId`
- CEF `remote_debugging_port`, `CefTaskManager` (partial)

### Internal / version-sensitive
- `chrome://process-internals`
- `chrome://discards/graph`
- Chromium Task Manager internals
- Exact tracing event payloads as a long-term contract

### Speculative / unsafe (recommended **against**)
- Memory scraping for URL strings
- Parsing browser IPC / Mojo wires as a compatibility layer
- Inferring origin from thread names, command line, HWND ownership, tokens, modules, handle names
- Depending on WER “black box” registration for live origin metadata

---

## Recommended architecture for Chromium Process Explorer

1. **Passive default mode**
   - Keep doing **process tree + Mojo endpoint + role classification**.
   - Show **renderer/gpu/utility/browser** confidently.
   - **Do not claim tab/origin/URL** in passive mode.

2. **Normalize IDs**
   - Treat **OS PID** and **Chromium child-process ID** as separate first-class IDs.
   - You will need both: Chromium internals and Electron frame events often use child IDs; OS tooling and CDP `SystemInfo` use OS PIDs.

3. **Optional CDP enrichment**
   - If the user opts in, connect to the **browser target**.
   - Use `Browser.getVersion`, `Target.getTargets`, `Target.setAutoAttach(flatten=true)`, `browserContextId`, opener/frame relationships.
   - If PID correlation is needed, add **Tracing** as an advanced mode.

4. **Experimental Chromium-internal adapter**
   - For Chromium-family browsers only, consider an **experimental** adapter that reads `chrome://discards/graph` or `chrome://process-internals`.
   - Prefer `discards/graph` over `process-internals` if you need **OS PID**.

5. **Cooperative platform adapters**
   - **WebView2 first**
   - **Electron second**
   - **Qt third**
   - **CEF partial**

6. **Explicitly reject**
   - memory scraping
   - WER black-box dependence
   - thread-name/origin heuristics
   - reverse-engineering Mojo/IPC payloads as a stable product feature

---

## Prioritized experiments

### P0 — prove your ID model
**Setup:** compare Chromium Task Manager / `chrome://process-internals` / OS process list on a page with an OOPIF.  
**Pass:** you can demonstrate that `process-internals` uses **child-process IDs**, not OS PIDs.  
Evidence: `chromium/chromium:content/browser/process_internals/process_internals_handler_impl.cc:35-45`

### P1 — browser-target CDP smoke test
**Setup:** launch Chromium with remote debugging enabled; on Google Chrome 136+, use a non-default `--user-data-dir`.  
**Calls:** `/json/version` → browser websocket → `Browser.getVersion`, `Target.getTargets`, `SystemInfo.getProcessInfo`.  
**Pass:** you get correct target topology and OS PID list; **Fail (expected):** no stable target→PID mapping from those calls alone.

### P2 — tracing correlation
**Setup:** same as P1, plus `Tracing.start` with relevant categories.  
**Pass:** you observe frame/url/processId tuples from tracing and can correlate them to active tabs/frames.

### P3 — `chrome://discards/graph`
**Setup:** open tabs with cross-site iframes + workers.  
**Pass:** you can join frame/worker graph nodes to process graph nodes and recover OS PID + URL relationships.  
Evidence: `chromium/chromium:chrome/browser/ui/webui/discards/graph_dump_impl.cc:491-559`

### P4 — WebView2 adapter
**Setup:** sample app with main frame + cross-site iframe.  
**Pass:** `GetProcessExtendedInfosAsync` returns `ProcessId` + associated frame `Source`/`FrameId` mappings.

### P5 — Electron adapter
**Setup:** Electron sample with main frame + OOPIF + worker.  
**Pass:** main-frame mapping via `getOSProcessId()` is correct, target mapping via `fromDevToolsTargetId()` works.  
**Expected gap:** child-frame/worker OS PID coverage.

### P6 — Qt adapter
**Setup:** Qt WebEngine sample with remote debugging on.  
**Pass:** `renderProcessPid()` + `devToolsId()` work for main-frame mapping.

---

## Gaps / uncertainties

- I did **not** verify whether **Edge** has adopted Chrome’s exact default-profile remote-debugging restriction; upstream Chromium source makes that restriction unconditional only for **Google Chrome branding**.
- I found **no useful public histogram/UMA surface** for live PID→origin mapping.
- I did **not** find a mature open-source **passive** external Windows tool that reliably maps stock Chromium renderer PIDs to origins without debugging or app cooperation; most real implementations are **browser-internal** or **embedder-cooperative**.

---

## Grouped sources

### Primary (code / schema / API reference)
- Chromium process objects and frame lifecycle:
  - `chromium/chromium:content/public/browser/render_process_host.h:335-365`
  - `chromium/chromium:content/public/browser/render_frame_host.h:271-275`
  - `chromium/chromium:content/public/browser/site_instance.h:31-119`
- Chromium internal mapping surfaces:
  - `chromium/chromium:content/browser/process_internals/process_internals_handler_impl.cc:35-102`
  - `chromium/chromium:chrome/browser/ui/webui/discards/graph_dump_impl.cc:491-559`
  - `chromium/chromium:chrome/browser/task_manager/providers/per_profile_worker_task_tracker.cc:57-141`
- CDP schema / implementation:
  - `ChromeDevTools/devtools-protocol:pdl/domains/Target.pdl:14-40`
  - `ChromeDevTools/devtools-protocol:pdl/domains/SystemInfo.pdl:81-116`
  - `chromium/chromium:content/browser/devtools/protocol/target_handler.cc:96-125`
  - `chromium/chromium:content/browser/devtools/protocol/tracing_handler.cc:197-235`
- Remote debugging:
  - `chromium/chromium:chrome/browser/devtools/remote_debugging_server.cc:151-174`
  - `chromium/chromium:content/browser/devtools/devtools_http_handler.cc:273-305`
- Windows crash/thread internals:
  - `chromium/chromium:base/threading/platform_thread_win.cc:41-65`
  - `chromium/chromium:content/browser/renderer_host/ipc_utils.cc:104-171`
  - `chromium/chromium:components/stability_report/stability_report.proto:15-57`
- Platform APIs:
  - `electron/electron:docs/api/web-contents.md:86-104`
  - `electron/electron:shell/browser/api/electron_api_web_contents.cc:2666-2679`
  - `chromiumembedded/cef:include/internal/cef_types.h:418-427`
  - `qt/qtwebengine:src/core/api/qwebenginepage.cpp:1184-1195`

### Secondary (official explanatory docs)
- Chromium process model doc: `chromium/chromium:docs/process_model_and_site_isolation.md:75-140`
- Chrome security change: https://developer.chrome.com/blog/remote-debugging-port
- WebView2 process/CDP docs:
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.getprocessextendedinfosasync
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2processextendedinfo.associatedframeinfos
  - https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/chromium-devtools-protocol
- CEF general usage / ChromeDriver:
  - `chromiumembedded/cef:docs/general_usage.md:530-548`
  - `chromiumembedded/cef:docs/using_chrome_driver.md:11-18`
- NW.js DevTools doc:
  - `nwjs/nw.js:docs/For Users/Debugging with DevTools.md:1-26`

If you want, I can turn this into a **design checklist** or a **concrete .NET integration plan** for Chromium Process Explorer.
