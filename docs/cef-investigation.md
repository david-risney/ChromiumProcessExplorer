# CEF investigation for Chromium Process Explorer

Research date: 2026-08-10  
Scope: Windows-focused CEF research for Chromium Process Explorer, prioritizing official CEF API/source/docs, Chromium source/docs, Microsoft docs, and closely related project documentation.

## Legend

- **Confirmed fact**: directly supported by the cited source(s).
- **Heuristic / inference**: likely useful for Chromium Process Explorer, but not stated as a supported external contract.
- **Version / branch / embedder-dependent**: behavior that changes across Chromium/CEF branches or depends on how the app embeds CEF.
- **Unanswered question**: I did not find an authoritative reviewed source for a stronger claim.

## 1. Process model, subprocess executables, command-line markers, and host association

### Confirmed facts

- Modern CEF3 is a **multi-process** embedder on top of Chromium. CEF describes the main **browser** process as handling window creation, UI, and network access; Blink rendering and JavaScript execution happen in a separate **renderer** process; and other processes such as **GPU** are spawned as needed. CEF architecture/history docs also make clear that current supported CEF is the multi-process CEF3 line, while CEF1 was the historical single-process implementation. [P1][P16]
- By default on Windows and Linux, CEF reuses the **main application executable** for subprocesses; a separate helper executable can be specified with `CefSettings.browser_subprocess_path`. `CefExecuteProcess()` identifies the browser process by the absence of a `--type` value and handles recognized secondary processes when `--type` is present. [P1][P2][P3]
- Chromium process-role markers are command-line driven. Reviewed source defines `--type=renderer`, `--type=gpu-process`, `--type=utility`, and `--type=crashpad-handler`. Chromium also defines `--utility-sub-type=<string>` as an aid for identifying utility-process purpose, and `--service-sandbox-type=<value>` as the sandbox-role discriminator used for utility processes. [P17][P18][P19][P21]
- Chromium’s network service is usually **out-of-process** and, in that mode, runs on the IO thread of a dedicated **utility** process. Chromium’s sandbox source also maps the utility sandbox type value `network` to the network-service sandbox. [P19][P20]
- On Windows, CEF sandboxing requires the **same executable** for browser and subprocesses. `browser_subprocess_path` cannot be combined with the Windows sandbox, and current CEF startup code forces `no_sandbox` when a separate subprocess executable is configured on Windows. [P5][P8]

### Heuristics / inference for Chromium Process Explorer

- **Highest-confidence external role classifier**: treat a process with **no `--type` switch** as the browser/host candidate, and treat `--type=renderer|gpu-process|utility|crashpad-handler` as authoritative child-role labels when present. [P3][P17][P21]
- For `--type=utility`, prefer `--service-sandbox-type=<value>` as the role label (`network`, `audio`, `print_backend`, etc.), and treat `--utility-sub-type=<string>` as a secondary/friendly label only because Chromium says it “does not affect the services offered by the process.” [P17][P19]
- If `browser_subprocess_path` is **not** used, expect browser and child processes to often share the **same image name**. That means image-name-only association is weak, but “same EXE + different `--type`” is still useful. This matches both CEF docs and CEF forum guidance from the project maintainer. [P1][P3][S1]
- If `ExternalHandler` is used for crash reporting, the crash handler may be a **different executable** from the browser process. In that case, associate crashpad by `--type=crashpad-handler`, `--user-data-dir`, startup proximity, and shared CEF module/runtime evidence rather than by image name alone. [P12][P13][P21][P22]

### Version / branch / embedder-dependent notes

- CEF3’s supported architecture is multi-process, but Chromium still defines `--single-process`, and some code paths adapt to it. That is not equivalent to a supported CEF production mode. CEF forum guidance from the project lead says single-process mode is “not stable and is not supported.” [P16][P17][S1]
- Utility-process role names and counts are **Chromium-version dependent**. `network` is confirmed, but other utility roles visible in the wild can vary by branch and enabled features. [P19][P20]
- Starting with Windows sandbox/bootstrap changes in M138+, some deployments may launch through bootstrap binaries or DLL-hosted entry points rather than a conventional app EXE-only layout. [P8][P14]

### Unanswered questions

- I did not find an official reviewed Windows document that defines a single canonical **external** parent/child association algorithm for arbitrary third-party CEF apps. Any Windows-wide “reliable host association” implementation will still need heuristics around parent PID, creation time, command line, and runtime-folder signals. [P3][P4][P19]

## 2. Runtime folders: cache, root cache, user data, logs, crash data, temp, resources, locales, dictionaries, and external discoverability

### Confirmed facts

- `CefSettings.cache_path` is the persistent **profile/cache** location. If it is empty, browsers run in “incognito mode” with in-memory caches and no profile-specific persistence; HTML5 storage such as `localStorage` only persists across sessions when a cache path is specified. If a child directory is provided, CEF ignores it and uses the `"default"` profile child directory instead. [P2]
- `CefSettings.root_cache_path` is the **installation-specific root** and parent for profile-specific data. If both root and cache are empty, current CEF documents the default Windows root as `AppData\Local\CEF\User Data` under the user profile directory. From CEF version 120 onward, a process-singleton lock is based on `root_cache_path`, and relaunches can be handled via `OnAlreadyRunningAppRelaunch()`. [P2][P4][P6]
- Current CEF Windows startup code derives the Chromium **user-data directory** from `root_cache_path` or, for subprocesses, from `--user-data-dir`; Chromium defines `--user-data-dir` as the directory where the browser looks for all of its state. CEF also overrides the internal crash-dump directory from that user-data path. [P6][P23]
- Spell-check dictionary files are explicitly redirected by CEF to `<user_data_path>\Dictionaries`. [P6]
- On Windows, `CefSettings.log_file` defaults to a `debug.log` file in the **main executable directory** when unset; CEF also exposes `--log-file`, `--log-severity`, and `--log-items`. [P2][P5]
- On Windows, resources default to the **module directory** unless overridden. `resources_dir_path` controls non-localized packs, and `locales_dir_path` controls the `locales\` directory. [P2][P5][P7]
- Crash-reporting config on Windows is `crash_reporter.cfg` next to the main executable. If `AppName` is set, crash report information is stored under `C:\Users\[CurrentUser]\AppData\Local\[AppName]\User Data`. [P12][P13]
- Temp-related reviewed evidence was limited but concrete: if standard user-data resolution fails, CEF can fall back to the OS temp directory for user-data-root discovery, and `CefEndTracing()` uses a new **temporary file** when no output path is provided. [P6][P11]

### Heuristics / inference for Chromium Process Explorer

- For external discovery, parse command lines for `--user-data-dir`, `--log-file`, `--resources-dir-path`, `--locales-dir-path`, and remote-debugging switches first; they are stronger than pure file-system guessing. [P2][P5][P23]
- If `cache_path` is empty, show profile storage as **“likely in-memory/incognito”** instead of promising a persistent `Default` profile on disk. [P2]
- If `root_cache_path` is explicit but `cache_path` is not externally visible, show the root as **confirmed** and the per-profile path as **inferred/default-profile likely**. The source supports `"default"` profile behavior, but a third-party observer still may not know whether a custom request-context cache path is in use. [P2]
- Treat `DevToolsActivePort`, `debug.log`, `crash_reporter.cfg`, and `Dictionaries` as practical external breadcrumbs when present. [P2][P6][P12]

### Version / branch / embedder-dependent notes

- The `root_cache_path` singleton semantics are explicitly documented for **version 120 or newer**. Older branches may behave differently. [P2]
- Not every CEF deployment will surface all folders in the app directory. Resources/locales can be relocated, some profile data may be in-memory, and crash-data location changes if `AppName` is configured. [P2][P5][P12]

### Unanswered questions

- I did not find a reviewed official Windows source that documents a stable, exhaustive on-disk schema for every Chromium/CEF subfolder below the sourced `User Data` root. Beyond the reviewed `Dictionaries` override and the explicit `User Data` parent paths, deeper cache/profile subfolders should be treated as branch- and embedder-specific until verified empirically. [P6][P12][P13]

## 3. CEF binary distribution, deployed install layouts, required components, identification, and version extraction

### Confirmed facts

- Official CEF binary distributions share a common top-level structure including `Debug`, `Release`, `include`, `libcef_dll`, `Resources`, and sample/test apps such as `tests/cefclient`, `tests/cefsimple`, and `tests/ceftests`. [P1]
- CEF’s Windows layout example places the browser EXE, `libcef.dll`, resource packs, locale packs, and support DLLs next to the application. The example layout for the 4692 branch includes `snapshot_blob.bin` and `v8_context_snapshot.bin`. [P1]
- The current Windows redistribution README lists **required** runtime files as `libcef.dll`, `chrome_elf.dll`, `icudtl.dat`, and `v8_context_snapshot.bin`. It lists **optional** but relevant files/directories such as `locales\`, `chrome_100_percent.pak`, `chrome_200_percent.pak`, `resources.pak`, `d3dcompiler_47.dll`, `dxil.dll`, `dxcompiler.dll`, `libEGL.dll`, `libGLESv2.dll`, `vk_swiftshader.dll`, `vk_swiftshader_icd.json`, and `vulkan-1.dll`. [P7]
- On Windows, sandbox deployment changed significantly in **M138+**. Instead of shipping `cef_sandbox.lib` for general third-party linking, CEF now documents bootstrap executables (`bootstrap.exe`, `bootstrapc.exe`) and DLL-hosted client entry points as the standard sandbox-compatible path. [P8]
- Official version/compatibility extraction APIs are explicit: `cef_version_info(int entry)`, `cef_version_info_all()`, `cef_version_full()`, and `cef_api_hash(int version, int entry)`. On newer APIs, `cef_version_info_t` also includes Windows-specific `sandbox_compat_hash`, installer-mode `libcef_path`, `libcef_is_bundled`, `libcef_version_full`, `installer_error_code`, and `installer_error_message`. [P14][P15]

### Heuristics / inference for Chromium Process Explorer

- High-confidence disk identification is: `libcef.dll` plus one or more of `chrome_elf.dll`, `icudtl.dat`, `v8_context_snapshot.bin`, `resources.pak`, or `locales\*.pak`. [P7]
- For live-process identification, prioritize **loaded module evidence** (`libcef.dll`, optionally `chrome_elf.dll`) plus command-line markers over directory layout alone, because bootstrap/shared-install cases can move `libcef.dll` away from the app folder. [P8][P14]
- If Chromium Process Explorer ever gains in-process inspection or a cooperating helper, `cef_version_info_all()` / `cef_version_full()` / `cef_api_hash()` are the cleanest supported version-read path. [P14][P15]

### Version / branch / embedder-dependent notes

- Do **not** hard-code `snapshot_blob.bin` as required. Older layout examples include it, but the current redistributable README does not list it among required Windows components. [P1][P7]
- `sandbox_compat_hash` exists only on newer APIs, and installer/bootstrap metadata fields such as `libcef_path` and `libcef_version_full` require still newer API levels. [P14]
- Bootstrap/shared-install behavior is Windows-sandbox-era specific and materially different from older “everything beside the EXE” layouts. [P8][P14]

### Unanswered questions

- I did not find a single reviewed official document that publishes one stable Windows path template for all **installer-managed/shared** CEF installs. When available, Chromium Process Explorer should display resolved `libcef_path` from the version API instead of inventing a path convention. [P8][P14]

## 4. Remote debugging

### Confirmed facts

- `CefSettings.remote_debugging_port` enables the Chrome DevTools remote-debugging protocol on ports **1024-65535**. CEF also documents that `--remote-debugging-port=0` selects an **ephemeral port**, prints the WebSocket endpoint to stderr, and writes the selected port to `<cache-dir>\DevToolsActivePort` when a cache directory path is provided. Remote debugging can be reached via `chrome://inspect`, and ports 9222/9229 are discoverable by default. [P2]
- Current CEF startup code only maps the `remote_debugging_port` **setting** to the Chromium switch when the value is between 1024 and 65535. [P5]
- Chromium also defines `--remote-debugging-pipe`, which enables DevTools remote debugging over stdio/IO pipes instead of an HTTP port. [P17]

### Heuristics / inference for Chromium Process Explorer

- For external discovery, prefer this order: (1) `DevToolsActivePort` file, (2) `--remote-debugging-port`, (3) `--remote-debugging-pipe` / pipe-related switches. [P2][P17]
- Treat any remote-debugging signal as a **debug surface worth surfacing prominently** to the user, even if it is intended only for local diagnostics. [P2][P17]

### Version / branch / embedder-dependent notes

- The **ephemeral-port** path appears to be switch-driven rather than setting-driven in current reviewed CEF startup code. Embedders wanting port `0` behavior may have to use command-line processing rather than only `CefSettings.remote_debugging_port`. [P2][P5]
- Pipe mode is a Chromium capability, not a reviewed CEF settings field. Availability and production use should be verified per target branch/app. [P17]

### Unanswered questions

- I did not find a reviewed official CEF document describing how often third-party Windows embedders use `remote-debugging-pipe`, or whether they commonly add additional policy/origin restrictions. That needs real-app validation. [P17]

## 5. DevTools access

### Confirmed facts

- CEF exposes local DevTools UI through `CefBrowserHost::ShowDevTools()`, `CloseDevTools()`, and `HasDevTools()`. `ShowDevTools()` can also target a specific inspected element point. [P9]
- CEF exposes programmatic DevTools Protocol access through `SendDevToolsMessage()`, `ExecuteDevToolsMethod()`, and `AddDevToolsMessageObserver()`. CEF explicitly says these APIs **do not require** an active DevTools front-end or remote-debugging session. [P9]
- DevTools observers receive raw messages, structured method results, events, and agent attach/detach notifications. CEF warns some protocol messages can exceed **1 MB**. [P10]
- CEF can log DevTools front-end communication with `--devtools-protocol-log-file=<path>`. [P9]
- Official sample/test coverage exists: `cefclient` advertises DevTools integration, and `tests/ceftests/devtools_message_unittest.cc` exercises the message observer / send / execute flow. [P25][P27]

### Heuristics / inference for Chromium Process Explorer

- Chromium Process Explorer can treat **“DevTools window open”** and **“CDP reachable”** as separate states. `HasDevTools()` maps to the former when you have in-process visibility; remote debugging and `DevToolsActivePort` map to the latter from outside the process. [P2][P9][P10]
- If you later add a helper/injected probe, `AddDevToolsMessageObserver()` is a clean supported place to mirror CDP activity without needing a visible DevTools window. [P9][P10]

### Version / branch / embedder-dependent notes

- DevTools window behavior can vary with runtime style (Chrome vs Alloy) and hosting style (`CefBrowserView` vs native/windowless integration). [P9][P16]

### Unanswered questions

- I did not find a public reviewed external API for enumerating all live `CefBrowser` instances in an arbitrary third-party CEF process without either remote debugging or in-process cooperation. Browser-to-window mapping remains embedder-specific. [P9][P10]

## 6. CEF/Chromium command-line options, processing hooks, inheritance, and unsafe/version caveats

### Confirmed facts

- CEF documents command-line configuration as a core mechanism. `command_line_args_disabled` can start the browser process from an empty command line; settings that map to switches are applied **before** `OnBeforeCommandLineProcessing()`; and CEF warns that modifying non-browser process command lines may result in **undefined behavior including crashes**. [P1][P2][P3][P5]
- `OnBeforeChildProcessLaunch()` exists specifically so the app can modify child-process command lines. CEF documents that it is called on the UI thread for render-process launch and on the IO thread for GPU-process launch. [P4]
- Current CEF startup code maps many settings into switches, including `browser-subprocess-path`, `no-sandbox`, `log-file`, `log-severity`, `log-items`, `js-flags`, `resources-dir-path`, `locales-dir-path`, `remote-debugging-port`, and `uncaught-exception-stack-size`. [P2][P5]
- Chromium source marks several switches as especially risky or diagnostic-only. Reviewed examples include `--single-process`, `--no-sandbox` (“for testing purposes only”), `--disable-kill-after-bad-ipc` (“a bad idea from a security perspective”), and `--disable-web-security` (only effective with `--user-data-dir`). [P17][P18]

### Heuristics / inference for Chromium Process Explorer

- Chromium Process Explorer should maintain a curated list of **high-value switches** to classify and highlight, instead of pretending to semantically understand all Chromium flags. Good first-class badges are `--single-process`, `--no-sandbox`, `--disable-kill-after-bad-ipc`, `--disable-web-security`, `--remote-debugging-port`, and `--remote-debugging-pipe`. [P17][P18]
- Child command lines should be treated as **browser + Chromium defaults + app mutations**, not as a stable inheritance contract. Present them raw, but classify only sourced/high-confidence flags. [P3][P4][P5]

### Version / branch / embedder-dependent notes

- CEF release branches track specific Chromium releases, so switch names, defaults, and side effects change over time. The most stable cross-version contract is the smaller subset explicitly surfaced by current CEF headers/settings. [P1][P14][P15]
- Utility-process flags are especially branch-sensitive because service decomposition changes over time. [P17][P19][P20]

### Unanswered questions

- I did not find a reviewed official **machine-readable** switch catalog intended for long-term external-tool consumption. Chromium source is authoritative, but it is also a moving target. [P1][P17]

## 7. `CefSettings` logging, Chromium logging, crash reporting, `debug.log`, and Windows diagnostics

### Confirmed facts

- On Windows, the default CEF debug log is `debug.log` in the **main executable directory** unless `log_file` / `--log-file` overrides it. CEF also exposes `log_severity` and `log_items`, and current startup code initializes logging from those switches/settings. [P2][P5]
- Crash reporting is configured by `crash_reporter.cfg` next to the main executable on Windows. It supports `ProductName`, `ProductVersion`, `AppName`, `ExternalHandler`, `ServerURL`, rate limiting, database size/age limits, and declared crash keys; the public API exposes `CefCrashReportingEnabled()` and `CefSetCrashKeyValue()`. [P12][P13]
- CEF’s crash reporting metadata includes process type, command-line switches, and crash keys. Official docs show example crash sources for browser (`chrome://inducebrowsercrashforrealz`), renderer (`chrome://crash`), and GPU (`chrome://gpucrash`) crashes, and show sample uploaded metadata containing `ptype`, `pid`, `num-switches`, and `switch-*` fields. [P13]
- Microsoft documents Windows Error Reporting local dumps under `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`, with default dump folder `%LOCALAPPDATA%\CrashDumps`, and per-application subkeys such as `...\LocalDumps\MyApplication.exe`. Microsoft also warns that applications doing their own custom crash reporting are **not supported** by that feature. [S2]

### Heuristics / inference for Chromium Process Explorer

- Surface **both** CEF/Crashpad and WER evidence: whether `crash_reporter.cfg` exists, whether a CEF log path is configured, and whether LocalDumps is enabled for the app EXE. [P12][P13][S2]
- Treat WER as **supplemental** for CEF apps, not guaranteed primary capture, because Microsoft explicitly excludes apps with their own custom crash reporting from the documented LocalDumps feature. [P12][P13][S2]
- Parse crash metadata (`ptype`, `switch-*`, crash keys) when available; it is a high-value postmortem source for reconstructing process roles and launch conditions. [P13]

### Version / branch / embedder-dependent notes

- `docs/crash_reporting.md` still references `CefSettings.user_data_path` on non-Windows, while the current header comments reference `root_cache_path`. For Windows work, the reviewed current header/source is the safer authority. [P12][P13]

### Unanswered questions

- I did not find a reviewed authoritative Windows CEF source that names the exact Crashpad database/dump **subfolders** below the sourced `User Data` root for all current branches. Only the parent `User Data` location is authoritative from the reviewed material. [P12][P13]

## 8. Tracing, net logging, CDP, DevTools APIs, crashpad/minidumps, sample apps/tests, sandbox, and diagnostics/security caveats

### Confirmed facts

- `CefBeginTracing()` starts tracing on **all active processes**, and `CefEndTracing()` stops tracing and writes the result to a caller-specified path or a new temporary file. The client is responsible for deleting the trace file. [P11]
- Official sample/test coverage exists for tracing and DevTools protocol use. `tests/ceftests/tracing_unittest.cc` exercises `CefBeginTracing()` and trace events, while `tests/ceftests/devtools_message_unittest.cc` exercises DevTools observer/message flows. `cefclient` is the broader reference app for advanced integration scenarios. [P25][P26][P27]
- Chromium’s network logging switch is `--log-net-log`. Programmatic CDP access is available through the CEF DevTools APIs discussed above. [P9][P10][P24]
- On Windows, Chromium/CEF crash handling can use an embedded `--type=crashpad-handler` path or an external crashpad handler, and child processes inherit the exception handler but still run their own initialization. [P21][P22]
- Windows sandbox/security caveats are concrete: separate `browser_subprocess_path` disables the Windows sandbox in current CEF, and Chromium defines `--no-sandbox` as a browser-level switch meant for testing purposes only. [P5][P8][P18]

### Heuristics / inference for Chromium Process Explorer

- Treat tracing, net logging, CDP message capture, and minidumps as **on-demand diagnostics** because they can expose sensitive URLs, headers, script content, and crash state. Chromium Process Explorer should make those artifacts visible but not silently collect/share them. [P10][P11][P13][P24]
- When a utility process is classified as `network`, a simultaneous `--log-net-log` capture is especially valuable because it helps tie browser-side failures to the out-of-process network service. [P19][P20][P24]
- If you later add a “capture diagnostics” feature, prioritize: CDP endpoint discovery -> optional trace capture -> optional net-log capture -> dump/log collection, with explicit user confirmation. [P2][P9][P11][P24]

### Version / branch / embedder-dependent notes

- CEF’s crash backend differs by platform in the reviewed docs (Crashpad on Windows/macOS, Breakpad on Linux), and sample/test surface evolves across branches/runtime styles. [P13][P25]
- Windows sandbox/bootstrap rules changed significantly in M138+, so the appearance and launch flow of sandboxed apps is materially different from older branches. [P8][P14]

### Unanswered questions

- I did not find a reviewed official external API for enumerating “active trace session” or “active net-log capture” state from a different Windows process. That likely requires explicit configuration, file observation, or in-process cooperation. [P11][P24]

## 9. Existing similar utilities with overlap and gaps

### Confirmed facts

- **Sysinternals Process Explorer** is a generic Windows process/handle/DLL inspector. It shows active processes and can show handles and loaded DLLs, but Microsoft does not describe it as Chromium-aware. [S3]
- **WebView2Utilities** is much closer in spirit. Its Host Apps tab finds running WebView2 host apps, tries to associate them with runtime browser processes using mojo connection evidence, HWND trees, and process-parent examination, and surfaces runtime path/version/channel plus user-data-folder and browser PID when it can. The project also documents that association can fail in some cases or require a slower “Discover more” mode. [S4]
- **cefclient** is CEF’s comprehensive reference/sample app. It demonstrates DevTools, off-screen rendering, process messaging, tests, multi-window behavior, and multiple runtime modes, but it is not a Windows-wide explorer for arbitrary third-party apps. [P25]

### Heuristics / inference for Chromium Process Explorer

- The best overlap model is: **Process Explorer’s OS-level visibility** + **WebView2Utilities’ Chromium-aware host/browser association** + **CEF-specific packaging/runtime knowledge**. That would fill a real gap for CEF apps. [S3][S4][P25]
- WebView2Utilities is especially useful as a design reference for multi-signal association logic (process relationships, runtime metadata, user-data discovery, optional deeper scan), even though its heuristics are WebView2-specific. [S4]

### Version / branch / embedder-dependent notes

- WebView2Utilities’ specific signals (for example mojo-connection-based discovery and WebView2 runtime conventions) do not transfer directly to CEF. `cefclient` demonstrates what CEF can do, but not necessarily how arbitrary vendors package or launch it. [S4][P25]

### Unanswered questions

- I did not find an existing reviewed utility that already provides a Windows-wide, CEF-specific explorer correlating **host app**, **Chromium child roles**, **runtime folders**, **install layout**, **logs**, and **remote-debugging state** in one tool. [S3][S4][P25]

## Recommendations for Chromium Process Explorer

### Implementable detection/association ideas

| Priority | Idea | Why it is implementable |
| --- | --- | --- |
| 1 | Classify process roles from command lines: browser candidate = **no `--type`**; child roles = `--type=renderer|gpu-process|utility|crashpad-handler`; utility subrole = `--service-sandbox-type=<value>`. [P3][P17][P18][P19][P21] | These are explicit reviewed source contracts and should be your primary classifier. |
| 1 | Detect CEF presence from loaded/runtime files: `libcef.dll` plus corroborating files such as `chrome_elf.dll`, `icudtl.dat`, `v8_context_snapshot.bin`, `resources.pak`, `locales\*.pak`. [P7] | This is the strongest disk/module signature that is both Windows-specific and officially documented. |
| 1 | Parse and surface explicit runtime-folder switches: `--user-data-dir`, `--log-file`, `--resources-dir-path`, `--locales-dir-path`, `--remote-debugging-port`, `--remote-debugging-pipe`. [P2][P5][P17][P23] | These values are externally discoverable and avoid guesswork. |
| 2 | Add a confidence-scored association model: **High** = command-line role + module evidence; **Medium** = parent PID + startup-time proximity + same EXE/helper path; **Lower** = shared user-data root / shared install path. [P1][P3][P6][S4] | This mirrors the practical multi-signal design used by related tooling without overstating certainty. |
| 2 | When available, watch for `<cache-dir>\DevToolsActivePort` and expose CDP endpoint state. [P2] | This is an official, concrete external breadcrumb for remote debugging. |
| 2 | Flag risky/diagnostic switches prominently: `--single-process`, `--no-sandbox`, `--disable-kill-after-bad-ipc`, `--disable-web-security`, remote-debugging switches. [P17][P18] | These switches are either explicitly risky, testing-oriented, or materially change the security/debug surface. |
| 3 | If you later add a cooperating helper/injected probe, prefer `cef_version_info_all()`, `cef_version_full()`, and `cef_api_hash()` for authoritative runtime version/compatibility reporting. [P14][P15] | These are the supported CEF APIs for version and compatibility extraction. |

### Prioritized experiments

1. **Role-classifier validation**: run `cefclient`/`cefsimple` and at least one real Windows CEF app, then verify that `--type`, `--service-sandbox-type`, and module evidence classify browser/renderer/GPU/utility/crashpad correctly. [P1][P19][P25]
2. **Packaging-matrix validation**: test three Windows layouts separately: (a) same-EXE subprocesses, (b) separate `browser_subprocess_path`, and (c) M138+ bootstrap/DLL-hosted sandbox deployment. [P5][P8][P14]
3. **Runtime-folder validation**: confirm which paths are explicit vs inferred by comparing command lines with observed `User Data`, `Dictionaries`, `debug.log`, `resources`, and `locales` locations. Do not claim `cache_path` certainty when only `root_cache_path` / `user-data-dir` is known. [P2][P6]
4. **Remote-debugging validation**: test fixed-port, ephemeral-port, and pipe-mode cases; verify when `DevToolsActivePort` appears and how well it correlates with live browser processes. [P2][P5][P17]
5. **Crash-path validation**: enable `crash_reporter.cfg`, trigger browser/renderer/GPU crash scenarios, and confirm whether collected metadata (`ptype`, switches, crash keys) is sufficient to improve postmortem association in Chromium Process Explorer. [P12][P13]
6. **Safety/UX validation**: decide which diagnostics are passive visibility only vs opt-in capture, because logs, traces, CDP traffic, and dumps may contain sensitive application/user data. [P10][P11][P13][S2]

## Sources

### Primary

- **[P1]** CEF `docs/general_usage.md` — https://raw.githubusercontent.com/chromiumembedded/cef/master/docs/general_usage.md
- **[P2]** CEF `include/internal/cef_types.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/internal/cef_types.h
- **[P3]** CEF `include/cef_app.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_app.h
- **[P4]** CEF `include/cef_browser_process_handler.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_browser_process_handler.h
- **[P5]** CEF `libcef/common/chrome/chrome_main_delegate_cef.cc` — https://raw.githubusercontent.com/chromiumembedded/cef/master/libcef/common/chrome/chrome_main_delegate_cef.cc
- **[P6]** CEF `libcef/common/resource_util.cc` — https://raw.githubusercontent.com/chromiumembedded/cef/master/libcef/common/resource_util.cc
- **[P7]** CEF `tools/distrib/win/README.redistrib.txt` — https://raw.githubusercontent.com/chromiumembedded/cef/master/tools/distrib/win/README.redistrib.txt
- **[P8]** CEF `docs/sandbox_setup.md` — https://raw.githubusercontent.com/chromiumembedded/cef/master/docs/sandbox_setup.md
- **[P9]** CEF `include/cef_browser.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_browser.h
- **[P10]** CEF `include/cef_devtools_message_observer.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_devtools_message_observer.h
- **[P11]** CEF `include/cef_trace.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_trace.h
- **[P12]** CEF `include/cef_crash_util.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_crash_util.h
- **[P13]** CEF `docs/crash_reporting.md` — https://raw.githubusercontent.com/chromiumembedded/cef/master/docs/crash_reporting.md
- **[P14]** CEF `include/cef_version_info.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_version_info.h
- **[P15]** CEF `include/cef_api_hash.h` — https://raw.githubusercontent.com/chromiumembedded/cef/master/include/cef_api_hash.h
- **[P16]** CEF `docs/architecture.md` — https://raw.githubusercontent.com/chromiumembedded/cef/master/docs/architecture.md
- **[P17]** Chromium `content/public/common/content_switches.cc` — https://raw.githubusercontent.com/chromium/chromium/main/content/public/common/content_switches.cc
- **[P18]** Chromium `sandbox/policy/switches.cc` — https://raw.githubusercontent.com/chromium/chromium/main/sandbox/policy/switches.cc
- **[P19]** Chromium `sandbox/policy/sandbox_type.cc` — https://raw.githubusercontent.com/chromium/chromium/main/sandbox/policy/sandbox_type.cc
- **[P20]** Chromium `services/network/README.md` — https://raw.githubusercontent.com/chromium/chromium/main/services/network/README.md
- **[P21]** Chromium `components/crash/core/app/crash_switches.cc` — https://raw.githubusercontent.com/chromium/chromium/main/components/crash/core/app/crash_switches.cc
- **[P22]** Chromium `components/crash/core/app/crashpad.h` — https://raw.githubusercontent.com/chromium/chromium/main/components/crash/core/app/crashpad.h
- **[P23]** Chromium `chrome/common/chrome_switches.cc` — https://raw.githubusercontent.com/chromium/chromium/main/chrome/common/chrome_switches.cc
- **[P24]** Chromium `net/base/switches.h` — https://raw.githubusercontent.com/chromium/chromium/main/net/base/switches.h
- **[P25]** CEF `tests/cefclient/README.md` — https://raw.githubusercontent.com/chromiumembedded/cef/master/tests/cefclient/README.md
- **[P26]** CEF `tests/ceftests/tracing_unittest.cc` — https://raw.githubusercontent.com/chromiumembedded/cef/master/tests/ceftests/tracing_unittest.cc
- **[P27]** CEF `tests/ceftests/devtools_message_unittest.cc` — https://raw.githubusercontent.com/chromiumembedded/cef/master/tests/ceftests/devtools_message_unittest.cc

### Secondary

- **[S1]** CEF Forum thread “Stable CEF3 for single process mode” — https://magpcss.org/ceforum/viewtopic.php?t=13427
- **[S2]** Microsoft Learn, “Collecting User-Mode Dumps - Win32 apps” — https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps
- **[S3]** Microsoft Learn, “Process Explorer - Sysinternals” — https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer
- **[S4]** `WebView2Utilities` README — https://raw.githubusercontent.com/david-risney/WebView2Utilities/main/README.md
