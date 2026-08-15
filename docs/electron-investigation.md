# Electron investigation for Chromium Process Explorer

**Research date:** 2026-08-10  
**Scope:** Electron on Windows, with emphasis on what Chromium Process Explorer can detect externally versus what requires cooperative runtime inspection.  
**Method:** current Electron, Chromium, Electron Forge, electron-builder, Squirrel.Windows, Node, and Microsoft documentation plus authoritative repositories fetched during this session.

## How to read this report

- **Confirmed facts** are directly documented in the cited source(s).
- **Heuristics / inference** are reasonable implementation ideas for Chromium Process Explorer, but not guaranteed by the platform contract.
- **Version-dependent behavior** means the docs explicitly allow drift, or the behavior depends on Electron/Chromium/packager/Windows version or app configuration.
- **Unanswered questions** are places where the public docs do not give Chromium Process Explorer a stable guarantee and targeted experiments are still needed.

## Executive summary

Electron gives Chromium Process Explorer enough documented surface to do **coarse** Windows process-role classification reliably (browser/main vs renderer/tab vs GPU vs utility) using the OS process tree, actual command lines, and Chromium’s documented child-process markers such as `--type`, `--utility-sub-type`, and related debug launch switches. Electron also exposes cooperative APIs (`app.getAppMetrics()`, `webContents`, `process.type`) that can make the mapping much better when a helper, CDP session, or app-side probe is allowed. (Sources: [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model), [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md))

The weakest area for a purely external tool is **precise renderer-to-window / renderer-to-host association** in the presence of multiple `BrowserWindow`s, `BrowserView`/`<webview>` embeddings, DevTools, service workers, and app-spawned helpers. Electron’s own docs confirm that extra `webContents` exist for opened DevTools and extension background pages, and Chromium/Electron do not publish a universal HWND-to-renderer contract for outside tools. Chromium Process Explorer should therefore ship with a passive mode and an optional “enriched” mode that uses CDP or a tiny app-side helper to collect `webContents`/TargetID/app metrics data. (Sources: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Electron BrowserWindow API](https://www.electronjs.org/docs/latest/api/browser-window), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

Electron’s default Windows runtime-data conventions are **not the same as Chrome’s**. Electron documents `userData` under `%APPDATA%\<app name>` and `sessionData` defaulting to `userData`, while Chromium’s browser user data defaults are documented under `%LOCALAPPDATA%\...\User Data`. Chromium Process Explorer should not import Chrome/Chromium path assumptions directly into Electron detection. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))

Packaging/install detection is practical for **MSIX/AppX** and **MSI**, pretty good for **Squirrel**, and much weaker for **portable/custom/manual** layouts. The strongest externally discoverable markers are: WindowsApps + `AppxManifest.xml`/`Get-AppxPackage` for MSIX, uninstall-registry/ProductVersion mappings for MSI, and `Setup.exe`/`.nupkg`/`RELEASES`/Squirrel flags for Squirrel. Portable/manual installs often collapse to “renamed Electron EXE + nearby `resources\app` or `resources\app.asar`”, which is useful but heuristic. (Sources: [Electron application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution), [Electron ASAR archives](https://www.electronjs.org/docs/latest/tutorial/asar-archives), [Electron Forge Squirrel maker](https://www.electronforge.io/config/makers/squirrel.windows), [electron/windows-installer README](https://github.com/electron/windows-installer/blob/main/README.md), [Squirrel custom events](https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/custom-squirrel-events-non-cs.md), [electron-builder Windows docs](https://www.electron.build/docs/win/), [electron-builder NSIS docs](https://www.electron.build/docs/nsis/), [electron-builder MSI docs](https://www.electron.build/docs/msi/), [electron-builder AppX docs](https://www.electron.build/docs/appx/), [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [Windows Installer uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key))

---

## 1. Process model and reliable host/child/process-role association

### Confirmed facts

- Electron inherits Chromium’s multi-process architecture. Electron documents one **main** process per app, one renderer process per `BrowserWindow`, and additional renderer processes for web embeds such as `BrowserView` and `<webview>`. It also documents **UtilityProcess** children as separate Node-capable child processes launched through Chromium’s Services API. (Sources: [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model), [Electron utilityProcess API](https://www.electronjs.org/docs/latest/api/utility-process))
- Electron exposes **two different role vocabularies** that Chromium Process Explorer will need to normalize:
  - `process.type` can be `browser`, `renderer`, `service-worker`, `worker`, or `utility`.
  - `ProcessMetric.type` (returned by `app.getAppMetrics()`) is documented as `Browser`, `Tab`, `Utility`, `Zygote`, `Sandbox helper`, `GPU`, `Pepper Plugin`, `Pepper Plugin Broker`, or `Unknown`.  
  This mismatch is documented, not inferred. (Sources: [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md), [Electron app API](https://www.electronjs.org/docs/latest/api/app))
- `app.getAppMetrics()` returns one `ProcessMetric` per process associated with the app, including `pid`, `type`, `serviceName`, `name`, `cpu`, `memory`, `creationTime`, and on Windows `integrityLevel`. Electron explicitly says `pid` can be reused and recommends using **`pid` + `creationTime`** together to uniquely identify a process. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md))
- `child-process-gone` and `render-process-gone` are distinct surfaces. Electron documents that `child-process-gone` covers non-renderer children and can report `Utility`, `GPU`, and names such as `Audio Service`, `Content Decryption Module Service`, `Network Service`, and `Video Capture`; renderer failures instead show up through `render-process-gone`. (Source: [Electron app API](https://www.electronjs.org/docs/latest/api/app))
- `utilityProcess.fork(..., { serviceName })` is documented to feed the `name`/`serviceName` fields visible through `app.getAppMetrics()` and the `child-process-gone` event. That gives Chromium Process Explorer a cooperative, app-side way to surface utility-process purpose with a stable string. (Source: [Electron utilityProcess API](https://www.electronjs.org/docs/latest/api/utility-process))
- Chromium’s own child-process markers are documented in source comments:
  - `kProcessType = "type"`; if the value is empty, the process is the browser.
  - `kRendererProcess = "renderer"`.
  - `kGpuProcess = "gpu-process"`.
  - `kUtilityProcess = "utility"`.
  - `kUtilitySubType = "utility-sub-type"`; Chromium comments say this exists to make the purpose of a utility process easier to identify.
  - `kBrowserSubprocessPath = "browser-subprocess-path"` changes the EXE used for renderer/plugin subprocesses.  
  These are direct source comments, not reverse engineering. (Sources: [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc), [Chromium content_switches.h](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.h))
- `webContents.getAllWebContents()` is broader than “user windows”. Electron explicitly says it includes all windows, webviews, **opened DevTools**, and DevTools extension background pages. Chromium Process Explorer must therefore treat some renderers/targets as tooling rather than user content. (Source: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents))
- BrowserWindow parent/child relationships are **windowing** relationships, not Chromium subprocess-role truth. Electron documents the `parent` option for child windows and separately documents that destroying a `BrowserWindow` terminates its corresponding renderer. (Sources: [Electron BrowserWindow API](https://www.electronjs.org/docs/latest/api/browser-window), [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model))
- An Electron-badged executable is not always a Chromium browser process. Electron documents `ELECTRON_RUN_AS_NODE`, which starts the process as a normal Node.js process, and documents `ELECTRON_NO_ASAR` support in forked/spawned child processes that use that mode. Chromium Process Explorer should therefore avoid assuming that `electron.exe` descendants are always browser/renderer/GPU children. (Source: [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))

### Heuristics / inference for Chromium Process Explorer

- **Passive external association model:** use the Windows process tree, each process’s real image path, actual command line, and Chromium’s documented child-role markers (`--type`, `--utility-sub-type`, `--browser-subprocess-path`) as the first-pass classifier. Treat the top-level process without `--type` as the likely Electron browser/main process unless contradicted by `ELECTRON_RUN_AS_NODE`-style evidence or debug-only switches such as `--single-process`. (Sources: [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables), [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))
- **Renderer/window precision requires enrichment:** external-only observation can classify “renderer/tab-like” processes, but it cannot, from public docs alone, guarantee which renderer belongs to which visible Electron window in multi-window, `BrowserView`, `<webview>`, DevTools, or worker-heavy cases. For high-confidence association, add an optional cooperative layer that collects `app.getAppMetrics()`, `webContents.getAllWebContents()`, and/or CDP Target metadata. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
- **Normalize documented taxonomies:** map OS/process-switch roles to a Chromium Process Explorer internal model such as `main/browser`, `renderer/tab`, `gpu`, `utility`, `worker/service-worker`, `tooling/devtools`, `node-helper`, and `unknown-helper`. This is an implementation recommendation driven by the documented mismatch between `process.type`, `ProcessMetric.type`, and CDP target types. (Sources: [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

### Version-dependent behavior

- `--single-process` is a documented Chromium debug mode that collapses browser and renderer into one process and is explicitly called unrealistic for normal Chrome operation. If Electron honors equivalent Chromium behavior in a given build, it will invalidate most passive role assumptions for that run. (Source: [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))
- Electron’s documented default renderer sandboxing changed: `BrowserWindow.webPreferences.sandbox` is documented as `true` by default **since Electron 20**. That changes the observable privilege surface of renderer processes and may affect what in-app helpers can report from the renderer side. (Source: [Electron BrowserWindow API](https://www.electronjs.org/docs/latest/api/browser-window))
- The set of utility subtypes/services is not documented as closed or stable. Electron only gives examples such as `Network Service` and `Video Capture`, so Chromium Process Explorer should display unknown future values rather than hard-fail on a closed enum. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc))

### Unanswered questions

- Electron’s public docs do not provide a stable, external-only contract for HWND-to-renderer mapping across `BrowserWindow`, `BrowserView`, `<webview>`, DevTools, workers, and service workers. This is still an experiment gap.
- Electron does not publish a “complete role mapping matrix” from OS command lines to `app.getAppMetrics()` roles to CDP target types. Chromium Process Explorer should expect some roles to need empirical normalization.

---

## 2. Runtime folders: userData, cache/session, logs, crashes, temp, and external discoverability

### Confirmed facts

- Electron documents these Windows-relevant `app.getPath()` defaults and meanings:
  - `appData`: per-user application data, default `%APPDATA%` on Windows.
  - `userData`: default `appData` + app name; intended for app configuration/data.
  - `sessionData`: default `userData`; stores localStorage, cookies, disk cache, downloaded dictionaries, network state, and DevTools files.
  - `logs`: app log folder.
  - `crashDumps`: crash-dump directory.
  - `temp`: temporary directory.
  - `assets`: directory where assets such as `resources.pak` are stored on Windows/Linux.
  - `exe`/`module`: executable / Chromium-module paths.  
  Electron also explicitly warns not to put large app-specific files directly in `userData` because Chromium already uses subdirectories such as `Cache`, `GPUCache`, and `Local Storage`. (Source: [Electron app API](https://www.electronjs.org/docs/latest/api/app))
- Electron’s documented default is **not Chrome’s default**. Electron puts default `userData` under `%APPDATA%\<app name>`, whereas Chromium documents Chrome/Chromium browser user data under `%LOCALAPPDATA%\...\User Data` on Windows. Chromium Process Explorer should keep those families separate. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))
- `app.setAppLogsPath()` without an explicit path creates/uses the default log directory **inside `userData` on Windows and Linux**. Electron also says `app.getPath('logs')` will auto-create that default if needed. (Source: [Electron app API](https://www.electronjs.org/docs/latest/api/app))
- Electron crash reports are stored temporarily under a `Crashpad` directory beneath the app’s user data directory unless the app overrides `app.setPath('crashDumps', ...)` before starting the crash reporter. (Source: [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter))
- Electron exposes path override hooks that can completely defeat default-folder heuristics:
  - `app.setPath(name, path)` can override documented path names such as `userData`, `sessionData`, and `crashDumps`.
  - Electron says `sessionData` must be overridden **before** the `ready` event if you want to move cookies/caches.
  - `session.fromPartition('persist:name')` creates a persistent session; `session.fromPath(absPath)` binds session storage to an absolute path; sessions without `persist:` are in-memory. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron session API](https://www.electronjs.org/docs/latest/api/session))
- Chromium documents `--user-data-dir` as the command-line override for the browser user data directory. While that doc is written for Chrome/Chromium rather than Electron specifically, Electron is Chromium-based and Chromium Process Explorer should inspect for the flag because it is a documented Chromium override path. (Source: [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))
- `process.resourcesPath` gives the app’s resources directory. In Windows manual packaging, Electron documents that app code lives under `electron\resources\app` or `electron\resources\app.asar`; that means resources discovery and runtime-data discovery are separate jobs. (Sources: [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution))

### Heuristics / inference for Chromium Process Explorer

- **External discovery order for data folders:**
  1. inspect the live command line for `--user-data-dir` or other explicit path switches;
  2. if absent, derive candidate `userData` from `%APPDATA%\<known app name>` rather than Chrome’s `%LOCALAPPDATA%\...\User Data` pattern;
  3. look for Electron-typical Chromium subdirectories (`Cache`, `GPUCache`, `Local Storage`) plus optional `Crashpad` and log files nearby;
  4. if a cooperative helper is allowed, call `app.getPath()`/`session.fromPath()` instead of guessing.  
  This is an implementation heuristic built from the documented defaults and override hooks. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron session API](https://www.electronjs.org/docs/latest/api/session), [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))
- **Treat default folder absence as inconclusive.** Electron only creates some directories lazily (for example, logs when first requested), and the app may override `userData`, `sessionData`, or `crashDumps`. Therefore, “I did not find `%APPDATA%\AppName\...`” is not proof that an app is not Electron. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter))
- **Cache/session is better modeled as “sessionData-rooted browser state” than as one fixed `Cache` folder.** Electron’s current docs center browser storage on `sessionData`, not on a separately documented cache root. Chromium Process Explorer should therefore present session storage as a session-data tree with likely cache subdirectories inside it. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron session API](https://www.electronjs.org/docs/latest/api/session))

### Version-dependent behavior

- Electron documents the persistence model for `persist:` partitions and `session.fromPath()`, but it does **not** document the exact on-disk naming convention used for persistent partitions inside `sessionData`. That layout should be treated as implementation detail unless measured.
- If an Electron app is distributed as MSIX/AppX, Windows package identity and virtualization rules can affect install/read-only behavior and some AppData/registry semantics. The exact result depends on manifest/runtime behavior/trust level and OS version, not on “Electron” alone. (Source: [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes))

### Unanswered questions

- Electron’s public docs do not define a stable external naming pattern for named persistent session partitions or DevTools data under `sessionData`.
- There is no published Electron contract for the exact filename/location created by `ELECTRON_LOG_ASAR_READS`; Electron only says it writes to the system `tmpdir`. (Source: [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))

---

## 3. Install and packaging layouts: Squirrel, MSI, MSIX, portable/custom, Forge/electron-builder, identification and version extraction

### Confirmed facts

- **Manual / prebuilt Electron packaging** on Windows is documented as either:
  - `electron\resources\app\...` with an unpacked app folder, or
  - `electron\resources\app.asar` with an ASAR archive.  
  Electron also documents that Windows distributors may rename `electron.exe` and edit icon/version metadata. (Source: [Electron application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution))
- **ASAR markers** are documented and useful:
  - app code is often in `app.asar`;
  - `app.asar.unpacked` exists when files are left unpacked with `asar --unpack` and should ship beside the archive;
  - some APIs unpack ASAR content to temporary files on demand.  
  Those are strong packaging clues for Chromium Process Explorer. (Source: [Electron ASAR archives](https://www.electronjs.org/docs/latest/tutorial/asar-archives))
- **Electron Forge Squirrel.Windows** documents three generated artifacts: `{appName} Setup.exe`, `{appName}-full.nupkg`, and `RELEASES`. Forge also says the maker inherits most options from `electron-winstaller`. (Source: [Electron Forge Squirrel maker](https://www.electronforge.io/config/makers/squirrel.windows))
- **Squirrel startup/update markers** are directly documented by authoritative repos:
  - `electron/windows-installer` says Squirrel spawns the app with `--squirrel-install`, `--squirrel-updated`, `--squirrel-uninstall`, `--squirrel-obsolete`, and `--squirrel-firstrun` during install/update/uninstall flows.
  - Squirrel.Windows documents a `SquirrelAwareVersion` string in the PE version resource with value `1` for Squirrel-aware EXEs. (Sources: [electron/windows-installer README](https://github.com/electron/windows-installer/blob/main/README.md), [Squirrel custom events](https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/custom-squirrel-events-non-cs.md))
- `electron/windows-installer` also documents that after building you get `.nupkg`, `RELEASES`, and `.exe`, and it exposes `setupMsi` / `noMsi` options, meaning some Squirrel-based distributions can also emit an MSI wrapper as part of the installer toolchain. (Source: [electron/windows-installer README](https://github.com/electron/windows-installer/blob/main/README.md))
- **Electron Forge WiX MSI** produces `.msi` packages and explicitly recommends Squirrel.Windows for typical user experience, reserving WiX MSI mainly for enterprise/policy-driven scenarios. (Source: [Electron Forge WiX MSI maker](https://www.electronforge.io/config/makers/wix-msi))
- **Electron Forge MSIX** exists, but Forge documents it as **experimental** as of v7.10 and requiring Windows 10/11 plus the Windows SDK. (Source: [Electron Forge MSIX maker](https://www.electronforge.io/config/makers/msix))
- **electron-builder** documents these Windows packaging defaults/options:
  - NSIS is the default Windows target.
  - Supported targets include `nsis`, `nsis-web`, `portable`, `appx`/MSIX, and `msi`.
  - `portable` builds expose `PORTABLE_EXECUTABLE_FILE`, `PORTABLE_EXECUTABLE_DIR`, and `PORTABLE_EXECUTABLE_APP_FILENAME` at runtime.
  - AppX/MSIX is for Store/sideloading; `electron-updater` does **not** provide AppX auto-update outside Store flows.
  - MSI is the enterprise-oriented Windows Installer path and uses a persistent `upgradeCode`. (Sources: [electron-builder Windows docs](https://www.electron.build/docs/win/), [electron-builder NSIS docs](https://www.electron.build/docs/nsis/), [electron-builder AppX docs](https://www.electron.build/docs/appx/), [electron-builder MSI docs](https://www.electron.build/docs/msi/))
- **MSIX/AppX install layout** is documented by Microsoft:
  - default install location is `C:\Program Files\WindowsApps\<package_full_name>`;
  - package files are read-only after deployment;
  - `Get-AppxPackage` lists installed packages and returns the package object/full name;
  - `Get-AppxPackageManifest` returns the package manifest XML, including the package ID and application metadata. (Sources: [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [Get-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackage?view=windowsserver2025-ps), [Get-AppxPackageManifest](https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackagemanifest?view=windowsserver2025-ps))
- **MSI install/version markers** are documented by Microsoft:
  - uninstall-registration lives under `HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode-GUID}`;
  - `DisplayName` maps from `ProductName`;
  - `DisplayVersion`, `Version`, `VersionMajor`, and `VersionMinor` derive from `ProductVersion`;
  - `InstallLocation` maps from `ARPINSTALLLOCATION`;
  - `ProductVersion` is required and only the first three fields (`major.minor.build`) are used by Windows Installer. (Sources: [Windows Installer uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key), [MSI ProductVersion](https://learn.microsoft.com/en-us/windows/win32/msi/productversion))
- **Version extraction** can come from several documented sources:
  - `app.getVersion()` returns the app version from `package.json`, or falls back to the executable/bundle version if `package.json` has none.
  - `process.versions.electron` and `process.versions.chrome` return Electron/Chrome runtime versions.
  - Windows `VERSIONINFO` resources carry `FILEVERSION`, `PRODUCTVERSION`, `FileVersion`, `ProductVersion`, `OriginalFilename`, `ProductName`, etc.
  - electron-builder documents that `buildVersion` maps to Windows `FileVersion`, and `buildNumber` can be appended to it. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron process API](https://www.electronjs.org/docs/latest/api/process), [VERSIONINFO resource](https://learn.microsoft.com/en-us/windows/win32/menurc/versioninfo-resource), [electron-builder configuration docs](https://www.electron.build/docs/configuration/))
- **Runtime packaging detection**: Electron documents `process.windowsStore`, which is `true` when the app is running as an MSIX/AppX package. There is no equivalent documented runtime boolean for NSIS/Squirrel/MSI/manual packaging. (Source: [Electron process API](https://www.electronjs.org/docs/latest/api/process))

### Heuristics / inference for Chromium Process Explorer

- **Strong external markers by packaging family:**
  - **MSIX/AppX:** process path or install record under `WindowsApps`, package identity from `Get-AppxPackage` / manifest, and optionally `process.windowsStore` when cooperative runtime access is available.
  - **MSI:** uninstall-registry key + `DisplayVersion`/`InstallLocation` + MSI product GUIDs.
  - **Squirrel:** nearby `RELEASES`, `.nupkg`, `Update.exe`, Squirrel startup flags, or a `SquirrelAwareVersion=1` version-resource marker.
  - **Manual/portable/custom:** renamed EXE plus nearby `resources\app`, `resources\app.asar`, and sometimes `app.asar.unpacked`. (Sources: [Electron application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution), [Electron ASAR archives](https://www.electronjs.org/docs/latest/tutorial/asar-archives), [Electron Forge Squirrel maker](https://www.electronforge.io/config/makers/squirrel.windows), [electron/windows-installer README](https://github.com/electron/windows-installer/blob/main/README.md), [Squirrel custom events](https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/custom-squirrel-events-non-cs.md), [Windows Installer uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key), [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes))
- **Use multiple version channels, not one:** report app version (`app.getVersion` / `ProductVersion`), runtime version (`process.versions.electron`, `process.versions.chrome`), file version (`VERSIONINFO`), and package version (MSIX package identity or MSI `DisplayVersion`) separately. These are related but not guaranteed identical. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron process API](https://www.electronjs.org/docs/latest/api/process), [VERSIONINFO resource](https://learn.microsoft.com/en-us/windows/win32/menurc/versioninfo-resource), [MSI ProductVersion](https://learn.microsoft.com/en-us/windows/win32/msi/productversion))
- **Do not overfit to EXE names.** Electron explicitly allows renaming `electron.exe`, and manual/portable builds may preserve very few Electron-branded strings in filenames. Folder layout and runtime module/resource evidence are more reliable than the EXE name alone. (Sources: [Electron application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution), [Electron process API](https://www.electronjs.org/docs/latest/api/process))

### Version-dependent behavior

- Current Forge/MSIX support is explicitly experimental, so artifact shapes/config conventions may change across Forge versions. (Source: [Electron Forge MSIX maker](https://www.electronforge.io/config/makers/msix))
- Squirrel’s exact installed directory structure (for example, versioned app subdirectories and how shortcuts resolve them) is strongly implied by tooling examples but is not fully spelled out in the current Electron Forge/electron-winstaller docs. Chromium Process Explorer should treat detailed folder-layout assumptions as test-backed heuristics, not hard-coded law. (Sources: [Electron Forge Squirrel maker](https://www.electronforge.io/config/makers/squirrel.windows), [electron/windows-installer README](https://github.com/electron/windows-installer/blob/main/README.md), [Squirrel custom events](https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/custom-squirrel-events-non-cs.md))
- MSIX file-system behavior varies by manifest/runtime behavior/trust level and OS version; Microsoft documents multiple modes rather than a single “all packaged desktop apps behave this way” rule. (Source: [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes))

### Unanswered questions

- How well do custom enterprise repackaging workflows preserve Squirrel, NSIS, and manual-layout markers after redistribution?
- Are there reliable externally visible fingerprints that distinguish electron-builder NSIS from other NSIS-based installers once the app is installed? The public docs do not promise one.

---

## 4. Remote debugging

### Confirmed facts

- Electron documents `--remote-debugging-port=<port>` as a supported command-line switch that enables remote debugging over HTTP on the specified port. (Source: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches))
- The Chrome DevTools Protocol (CDP) docs define the relevant HTTP/WebSocket surfaces on a remote-debugging port:
  - `GET /json/version` for browser metadata and the browser `webSocketDebuggerUrl`.
  - `GET /json` or `/json/list` for target enumeration.
  - `GET /json/protocol` for the current protocol schema.
  - WebSocket endpoints under `/devtools/browser/...` or `/devtools/page/...`. (Source: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
- Electron’s `webContents.fromDevToolsTargetId(targetId)` is a documented bridge from a CDP TargetID back to an Electron `WebContents`, which is exactly the kind of cooperative association primitive Chromium Process Explorer can use when CDP is available. (Source: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents))
- Electron’s **main process** is not remotely debugged the same way as renderer/CDP targets. Electron documents `--inspect` and `--inspect-brk` for the main process and directs users to `chrome://inspect` or other V8-inspector clients. (Source: [Electron debugging-main-process tutorial](https://www.electronjs.org/docs/latest/tutorial/debugging-main-process))
- CDP itself is explicitly versioned. The protocol site documents a frequently changing tip-of-tree protocol, a V8 inspector protocol, and an older stable `1.3` snapshot. (Source: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

### Heuristics / inference for Chromium Process Explorer

- If a running Electron app already exposes `--remote-debugging-port`, Chromium Process Explorer should prefer **enumerating live targets** over guessing renderer/window relationships from HWNDs alone. CDP gives URLs/titles/target IDs; Electron gives a cooperative API to map target IDs back to `WebContents`. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
- Chromium’s CDP docs state that if Chrome is launched with `--remote-debugging-port=0`, the chosen port is written to stderr and to a `DevToolsActivePort` file in the browser profile folder. Electron’s own docs do not explicitly promise this behavior, but because Electron uses Chromium, it is a high-value experiment for Chromium Process Explorer. Treat it as a Chromium-derived heuristic until validated on target Electron versions. (Sources: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/), [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))
- Remote debugging should be treated as **privileged instrumentation**. CDP can inspect and control browser state; Chromium Process Explorer should only auto-attach when explicitly allowed and should surface that the endpoint exposes sensitive browser/app state. This is an implementation caution inferred from CDP’s capabilities rather than from an Electron-specific warning page. (Source: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

### Version-dependent behavior

- CDP docs say tip-of-tree changes frequently and does not guarantee backward compatibility. Chromium Process Explorer should therefore negotiate or tolerate protocol drift rather than hard-coding one schema. (Source: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
- CDP docs also note that opening embedded DevTools can terminate some existing remote-extension connections, and that Chrome 63 introduced support for multiple simultaneous clients. Tooling should be prepared for detach/re-attach behavior. (Source: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

### Unanswered questions

- Electron’s docs do not state whether all target types Chromium Process Explorer may care about expose stable PID/process metadata through CDP across all supported Electron versions.
- Electron does not document whether `--remote-debugging-port=0` is officially supported in the same way Chromium documents it for Chrome.

---

## 5. DevTools access

### Confirmed facts

- Electron documents `win.webContents.openDevTools()` as the standard way to open Chromium DevTools for renderer-side content. Its application-debugging guide explicitly says DevTools are available for `BrowserWindow`, `BrowserView`, and `WebView` renderer processes. (Source: [Electron application debugging](https://www.electronjs.org/docs/latest/tutorial/application-debugging))
- `BrowserWindow` documents `webPreferences.devTools` with default `true`, and explicitly says that if it is set to `false`, `BrowserWindow.webContents.openDevTools()` cannot be used. (Source: [Electron BrowserWindow API](https://www.electronjs.org/docs/latest/api/browser-window))
- Electron explicitly distinguishes renderer DevTools from main-process debugging: DevTools only debug JavaScript executed in the window/page, while main-process JavaScript needs `--inspect` / `--inspect-brk`. (Sources: [Electron application debugging](https://www.electronjs.org/docs/latest/tutorial/application-debugging), [Electron debugging-main-process tutorial](https://www.electronjs.org/docs/latest/tutorial/debugging-main-process))
- `webContents.getAllWebContents()` includes opened DevTools and DevTools extension background pages. This is a hard edge case for any tool that enumerates renderers or targets and assumes “one renderer = one app window”. (Source: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents))

### Heuristics / inference for Chromium Process Explorer

- Chromium Process Explorer should surface **DevTools as its own role** (for example `tooling/devtools`) when it can detect it via CDP/WebContents enrichment, instead of folding it into generic renderer counts. (Sources: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Electron application debugging](https://www.electronjs.org/docs/latest/tutorial/application-debugging))
- A passive external tool cannot reliably prove that a given renderer process is “the DevTools frontend” from public docs alone. If precise labeling matters, use CDP target metadata or app-side `webContents` enumeration.

### Version-dependent behavior

- DevTools access can be intentionally disabled per window with `devTools: false`, so Chromium Process Explorer should not assume the presence of renderer DevTools means the app is non-production or non-hardened. (Source: [Electron BrowserWindow API](https://www.electronjs.org/docs/latest/api/browser-window))

### Unanswered questions

- Electron does not publish a stable external marker for “this renderer is a DevTools frontend” without using Electron/CDP surfaces. (Sources: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Electron application debugging](https://www.electronjs.org/docs/latest/tutorial/application-debugging), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

---

## 6. Electron and relevant Chromium command-line switches: support, inheritance, markers, caveats

### Confirmed facts

- Electron’s `app.commandLine.appendSwitch()` and `appendArgument()` modify **Chromium’s** command line, **not `process.argv`**. Electron explicitly says application-specific command-line arguments should still be read from `process.argv`. (Source: [Electron command-line API](https://www.electronjs.org/docs/latest/api/command-line))
- Electron documents these particularly relevant switches for diagnostics and experiments: `--remote-debugging-port`, `--enable-logging[=file]`, `--log-file`, `--log-net-log`, `--log-level`, `--v`, `--vmodule`, `--disable-http-cache`, `--disk-cache-size`, `--disable-renderer-backgrounding`, `--ignore-certificate-errors`, `--proxy-server`, `--proxy-bypass-list`, `--proxy-pac-url`, `--js-flags`, `--no-sandbox`, `--force_high_performance_gpu`, and `--force_low_power_gpu`. (Source: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches))
- Electron also documents important caveats for some of those switches:
  - On Windows, child-process logs cannot be sent to stderr reliably; file logging is the most reliable collection strategy.
  - `--js-flags` must be passed **on startup** to affect the main process.
  - `--disable-renderer-backgrounding` is global to all renderers.
  - `--no-sandbox` should only be used for testing. (Source: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches))
- Chromium documents child/debug-specific switches that matter for process association and experiments:
  - `--type`, `--utility-sub-type`, `--browser-subprocess-path`, `--renderer-cmd-prefix`, `--utility-cmd-prefix`, `--gpu-launcher`, `--single-process`, `--wait-for-debugger`, and `--wait-for-debugger-children`. (Sources: [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc), [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))
- Chromium’s debugging docs say `--single-process` is not a realistic representation of normal browser execution and can mask/create bugs; `--no-sandbox` also has security implications. (Source: [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))
- Chromium documents `--user-data-dir` as the supported command-line override for the user-data directory. (Source: [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))
- Electron documents several environment variables that materially change process behavior or observability, including `ELECTRON_RUN_AS_NODE`, `ELECTRON_NO_ASAR`, `ELECTRON_ENABLE_LOGGING`, `ELECTRON_LOG_FILE`, `ELECTRON_ENABLE_STACK_DUMPING`, `ELECTRON_DEFAULT_ERROR_MODE`, `ELECTRON_LOG_ASAR_READS`, and `ELECTRON_DEBUG_MSIX_UPDATER`. (Source: [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))

### Practical switch/marker set Chromium Process Explorer should care about first

- **Role markers:** `--type`, `--utility-sub-type`, `--browser-subprocess-path`. (Sources: [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc), [Chromium content_switches.h](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.h))
- **Storage/location markers:** `--user-data-dir`. (Source: [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md))
- **Debug attach markers:** `--remote-debugging-port`, `--inspect`, `--inspect-brk`, `--wait-for-debugger`, `--wait-for-debugger-children`, `--renderer-cmd-prefix`, `--utility-cmd-prefix`, `--gpu-launcher`. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron debugging-main-process tutorial](https://www.electronjs.org/docs/latest/tutorial/debugging-main-process), [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md), [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc))
- **Logging/tracing markers:** `--enable-logging`, `--log-file`, `--log-net-log`, `--log-level`, `--v`, `--vmodule`, `ELECTRON_ENABLE_LOGGING`, `ELECTRON_LOG_FILE`. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))
- **Behavior-changing risk markers:** `--single-process`, `--no-sandbox`, `--ignore-certificate-errors`, `--disable-http-cache`, `ELECTRON_RUN_AS_NODE`. (Sources: [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md), [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))

### Heuristics / inference for Chromium Process Explorer

- **Observe actual child command lines instead of assuming inheritance.** Electron says it manipulates Chromium’s command line, and Chromium documents child-launch switches, but the exact switch set that reaches each child depends on Chromium/Electron launch code. Chromium Process Explorer should therefore capture each process’s real command line and not assume “all browser flags propagate to all children”. (Sources: [Electron command-line API](https://www.electronjs.org/docs/latest/api/command-line), [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc))
- **Treat `browser-subprocess-path` as a trust-reducing marker for image-name heuristics.** If set, renderers/plugins can launch from a different EXE than the main browser image, so child image name alone is not authoritative for role classification. (Source: [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc))
- **Surface risky switches prominently.** A process tree with `--no-sandbox`, `--ignore-certificate-errors`, `--disable-web-security`-style Chromium flags, or `ELECTRON_RUN_AS_NODE`, is qualitatively different from a normal production Electron launch and should be visually flagged as a diagnostics/security caveat. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables), [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc))

### Version-dependent behavior

- Chromium’s switch inventory is large and evolves. Even source-documented switches can move files, gain comments, or be conditionally built across versions. Chromium Process Explorer should store unknown switches verbatim instead of maintaining a closed allowlist. (Sources: [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc), [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))
- Electron docs do not attempt to guarantee that every Chromium switch is supported or meaningful in every Electron version/build configuration. (Sources: [Electron command-line API](https://www.electronjs.org/docs/latest/api/command-line), [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches))

### Unanswered questions

- Electron does not publish a “supported Chromium switch pass-through matrix”. A target-version validation pass is still needed for the switches Chromium Process Explorer plans to expose as first-class UI concepts.

---

## 7. Logging, environment variables, crash reporting, and ETW/Windows sources

### Confirmed facts

- Electron’s documented Chromium logging controls are:
  - `--enable-logging[=file]` / `ELECTRON_ENABLE_LOGGING`;
  - `--log-file` / `ELECTRON_LOG_FILE`;
  - `--log-level`, `--v`, `--vmodule`;
  - `--log-net-log=<path>` for network events.  
  Electron also documents that if `--enable-logging=file` is used without `--log-file`, logs go to `electron_debug.log` in the user-data directory. On Windows, Electron explicitly says **file logging is the most reliable way to collect logs from child processes**. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))
- Electron documents several Windows-relevant diagnostic environment variables:
  - `ELECTRON_ENABLE_STACK_DUMPING` prints a stack trace when Electron crashes.
  - `ELECTRON_DEFAULT_ERROR_MODE` shows the Windows crash dialog.
  - `ELECTRON_DEBUG_MSIX_UPDATER` adds MSIX-updater logs on Windows.
  - `ELECTRON_LOG_ASAR_READS` logs ASAR read offset/path data to the system `tmpdir`.  
  Electron also says the first two do not work if `crashReporter` is started. (Source: [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))
- Electron’s `crashReporter` docs say:
  - Electron uses **Crashpad**.
  - crash reports are temporarily stored under the app’s user data directory in `Crashpad` unless `app.setPath('crashDumps', ...)` overrides the location;
  - `crashReporter.start()` should be called as early as possible and then monitors all subsequently created processes;
  - the crash payload includes `process_type` and an uploaded `minidump` file. (Source: [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter))
- Windows Error Reporting (WER) is Microsoft’s platform crash/fault pipeline. Microsoft documents WER as handling application faults, hangs, and related failures, and documents local dump collection under `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`. The documented default dump folder is `%LOCALAPPDATA%\CrashDumps`, and `DumpType` can be mini (`1`) or full (`2`). (Sources: [Windows Error Reporting](https://learn.microsoft.com/en-us/windows/win32/wer/windows-error-reporting), [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps))
- Microsoft’s LocalDumps page also explicitly says that applications doing their **own custom crash reporting are not supported by this feature**. That caveat matters when Chromium Process Explorer tries to correlate Crashpad and WER output. (Source: [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps))
- Windows ETW is a documented Windows tracing facility for user-mode apps. Chromium’s Windows source tree contains an `EtwTraceProvider` abstraction with source comments stating it is a Windows event trace provider used with ETW. Chromium’s profiling guidance recommends Windows Performance Toolkit/UIforETW and notes that some Chrome tracing categories can be emitted into ETW traces. (Sources: [Windows ETW portal](https://learn.microsoft.com/en-us/windows/win32/etw/event-tracing-portal), [Chromium ETW provider header](https://chromium.googlesource.com/chromium/src/+/main/base/win/event_trace_provider.h), [Chromium profiling guide](https://www.chromium.org/developers/profiling-chromium-and-webkit/))
- For MSIX packaging/deployment diagnostics, Microsoft documents Event Viewer under `Applications and Services Logs > Microsoft > Windows > AppxDeployment-Server` and the PowerShell `Get-AppxLog` flow for deployment failures. (Source: [MSIX deployment troubleshooting](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-troubleshooting))

### Heuristics / inference for Chromium Process Explorer

- **Default Windows collection strategy:** prefer log files, net logs, and dump files over stderr scraping. Electron’s own docs say Windows child-process stderr is unreliable for Chromium logs. (Source: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches))
- **Treat crash artifacts as multi-pipeline:** when Chromium Process Explorer finds `Crashpad`, `%LOCALAPPDATA%\CrashDumps`, ProcDump output, or MSIX deployment logs, it should label which subsystem produced them rather than flattening them into one generic “crash log” bucket. That distinction is grounded in the separate Electron/Microsoft docs. (Sources: [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter), [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps), [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump), [MSIX deployment troubleshooting](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-troubleshooting))
- **ETW should be an optional advanced feature.** The public docs prove ETW exists and Chromium uses it, but they do not publish one stable “parse these exact providers and keywords for Electron builds” recipe. Chromium Process Explorer should expose ETW capture/analysis as a power-user mode backed by tested provider presets. (Sources: [Windows ETW portal](https://learn.microsoft.com/en-us/windows/win32/etw/event-tracing-portal), [Chromium ETW provider header](https://chromium.googlesource.com/chromium/src/+/main/base/win/event_trace_provider.h), [Chromium profiling guide](https://www.chromium.org/developers/profiling-chromium-and-webkit/))

### Version-dependent behavior

- Electron’s interaction with WER is only partially documented. Electron says `ignoreSystemCrashHandler` defaults to `false` for main-process crashes, while Microsoft says LocalDumps does not support apps doing their own custom crash reporting. Chromium Process Explorer should treat Crashpad/WER coexistence as a version/configuration experiment area rather than a universal rule. (Sources: [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter), [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps))
- Chromium’s ETW provider details are implementation-driven and may vary by build/version. The existence of ETW support is confirmed; a fixed provider/event inventory is not.

### Unanswered questions

- Which ETW provider/event combinations are consistently useful across target Electron versions for renderer/GPU/network/utility correlation?
- Which combination of Crashpad, WER LocalDumps, and ProcDump produces the most actionable dump set for Electron production incidents without duplicating too much data?

---

## 8. Tracing, net logs, process metrics, task manager, CDP, Node inspect, minidumps, and security caveats

### Confirmed facts

- Electron’s `contentTracing` API records trace data **across all processes**. Electron documents:
  - view traces in `chrome://tracing`;
  - `contentTracing.getCategories()`;
  - `startRecording()` / `stopRecording()`;
  - an extra Electron-specific trace category named `electron`;
  - optional heap profiling via `enableHeapProfiling()` plus `disabled-by-default-memory-infra`, with symbolication steps that use Electron breakpad symbols and Chromium tooling. (Source: [Electron contentTracing API](https://www.electronjs.org/docs/latest/api/content-tracing))
- Electron’s `netLog` API records Chromium network events. It documents `captureMode: default | includeSensitive | everything`, and explicitly says `includeSensitive` captures cookies/auth data while `everything` includes all bytes transferred on sockets. (Source: [Electron netLog API](https://www.electronjs.org/docs/latest/api/net-log))
- Electron exposes fine-grained process telemetry through:
  - `app.getAppMetrics()` / `ProcessMetric`;
  - `process.getCPUUsage()`;
  - `process.getProcessMemoryInfo()`;
  - `process.getBlinkMemoryInfo()`;
  - `process.getHeapStatistics()`;
  - `process.takeHeapSnapshot(filePath)`. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md), [Electron process API](https://www.electronjs.org/docs/latest/api/process))
- Chromium’s debugging guide says Chrome’s built-in Task Manager (Shift+Esc) can be used to find PIDs for browser child processes. Electron does not document an equivalent built-in task-manager UI API. (Source: [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))
- CDP exposes browser/page targets and protocol schema through `/json/version`, `/json/list`, and `/json/protocol`, and Electron exposes `webContents.fromDevToolsTargetId()` to connect target IDs back to Electron `WebContents`. (Sources: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/), [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents))
- Node’s inspector docs say:
  - `--inspect` defaults to `127.0.0.1:9229`;
  - a debugger client with access to the port can execute arbitrary code in the target process;
  - binding to `0.0.0.0` or a public IP is dangerous;
  - even a localhost-only inspector port is intentionally accessible to local applications. (Source: [Node debugging guide](https://nodejs.org/learn/getting-started/debugging))
- Microsoft documents additional dump options outside Electron’s built-in crash pipeline:
  - WER LocalDumps at `%LOCALAPPDATA%\CrashDumps` by default;
  - ProcDump mini/full/triage/custom dumps, hang detection, exception triggers, performance-counter triggers, and `-wer` to queue the largest dump to WER. (Sources: [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps), [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump))

### Heuristics / inference for Chromium Process Explorer

- **Best-practical Windows diagnostics bundle for Electron** is likely: current process tree + real command lines + packaging markers + `app.getAppMetrics()` enrichment when available + optional CDP target list + optional netlog + optional content trace + optional dump-on-trigger. Each piece is documented; the bundle itself is a recommended workflow. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron netLog API](https://www.electronjs.org/docs/latest/api/net-log), [Electron contentTracing API](https://www.electronjs.org/docs/latest/api/content-tracing), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/), [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump))
- **Security labeling is mandatory.** Net logs, dumps, heap snapshots, inspector ports, and CDP endpoints can all expose sensitive content such as cookies, authentication data, memory-resident secrets, source paths, or executable control surfaces. Chromium Process Explorer should mark those features as sensitive exports/attachments rather than routine telemetry. (Sources: [Electron netLog API](https://www.electronjs.org/docs/latest/api/net-log), [Node debugging guide](https://nodejs.org/learn/getting-started/debugging), [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter))
- **Task Manager gap:** because Electron lacks a documented built-in task-manager API, Chromium Process Explorer should plan to supply its own app-specific “task manager” view using `ProcessMetric`, CDP targets, and Windows process data instead of waiting for an Electron-native surface. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/), [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md))

### Version-dependent behavior

- CDP docs explicitly separate unstable tip-of-tree from older stable snapshots, so Chromium Process Explorer should feature-detect or tolerate protocol drift. (Source: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
- Task Manager availability inside a given Electron app depends on what the app exposes; Chromium’s own Task Manager docs should not be treated as an Electron guarantee.

### Unanswered questions

- What is the lowest-overhead always-on telemetry subset (for example, `ProcessMetric` polling plus selective log collection) that still meaningfully helps Electron investigations in production?
- How much trace/log overhead is acceptable for Chromium Process Explorer’s “diagnostic mode” on large Electron apps?

---

## 9. Existing similar utilities with overlap and gaps

### Confirmed overlaps and gaps

- **WebView2Utilities** already does several things Chromium Process Explorer wants conceptually: it tries to associate a host process with a Chromium-based browser process, reports runtime path/version/channel, user-data folder, browser PID, and can create a report bundle with crash dumps and Chromium logs. Its own README also admits some associations are uncertain and may require a slower “Discover more” scan. **Gap:** it is WebView2-specific, not Electron-aware, and its own UI labels some fields as “probable” or “Unknown”. (Source: [WebView2Utilities README](https://github.com/david-risney/WebView2Utilities/blob/main/README.md))
- **Process Explorer** gives a strong generic Windows process tree, handle view, DLL view, and search across handles/DLLs. **Gap:** its official docs are Windows-generic; they do not understand Electron process roles, Electron session/userData semantics, Chromium command-line role markers, or Electron packaging families. (Source: [Process Explorer](https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer))
- **ProcDump** is excellent for targeted dump capture on spikes, hangs, exceptions, and counter thresholds. **Gap:** it is a capture engine, not a Chromium/Electron association or packaging-analysis tool. (Source: [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump))
- **System Informer** overlaps on portable process/system inspection, graphs, network connections, stack traces, and service/process visibility. **Gap:** its official README positions it as a powerful general-purpose system monitor/debugger, not a Chromium/Electron-semantic explorer. (Source: [System Informer README](https://github.com/winsiderss/systeminformer/blob/master/README.md))
- **Electron/Chromium built-ins** (DevTools, CDP, `contentTracing`, `netLog`, `app.getAppMetrics`) give deep app-native diagnostics. **Gap:** they are fragmented, app-cooperative, and do not themselves solve Windows install-discovery, cross-app inventory, or a unified operator view. (Sources: [Electron application debugging](https://www.electronjs.org/docs/latest/tutorial/application-debugging), [Electron contentTracing API](https://www.electronjs.org/docs/latest/api/content-tracing), [Electron netLog API](https://www.electronjs.org/docs/latest/api/net-log), [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))

### Practical implication

Chromium Process Explorer should not try to beat Process Explorer/ProcDump/System Informer at generic OS inspection. Its differentiated value is **Chromium/Electron semantics**: process-role normalization, app-host association, packaging/runtime discovery, Electron-specific paths, switch awareness, and opt-in enrichment through CDP/Electron APIs. The comparative tools above confirm that most of the generic Windows plumbing already exists elsewhere. (Sources: [Process Explorer](https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer), [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump), [System Informer README](https://github.com/winsiderss/systeminformer/blob/master/README.md), [WebView2Utilities README](https://github.com/david-risney/WebView2Utilities/blob/main/README.md))

---

## Recommendations for Chromium Process Explorer

### Implementable detection / association ideas

1. **Adopt a two-layer model:**
   - **Passive layer:** Windows process tree, image path, file version metadata, real command lines, MSI/MSIX/Squirrel/install-layout markers, and documented Chromium switch parsing (`--type`, `--utility-sub-type`, `--user-data-dir`, `--remote-debugging-port`, etc.).
   - **Enriched layer (opt-in):** CDP target enumeration and/or a tiny Electron-side probe that reports `app.getAppMetrics()`, `webContents`, `process.type`, `process.versions`, `process.windowsStore`, and `app.getPath(...)`.  
   This matches the strengths and limits documented in Electron/Chromium. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/), [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc))
2. **Normalize role taxonomies explicitly** inside Chromium Process Explorer: `process.type`, `ProcessMetric.type`, CDP target type, and Windows-process evidence should be stored separately and then rendered as a normalized role with provenance. This avoids losing detail when the documented taxonomies disagree or drift. (Sources: [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
3. **Model paths separately:** show install path, resources path, userData, sessionData, logs, crash dumps, temp, and package identity separately. The docs show these can diverge significantly. (Sources: [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron process API](https://www.electronjs.org/docs/latest/api/process), [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [Windows Installer uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key))
4. **Treat DevTools, workers, and Node-mode helpers as first-class roles, not noise.** Electron/Chromium explicitly document that these can exist and can confuse naïve “renderer == user window” assumptions. (Sources: [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Electron process API](https://www.electronjs.org/docs/latest/api/process), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))
5. **Prefer file-based diagnostics on Windows** and clearly label sensitive artifacts. Electron’s own docs recommend file logging for child processes on Windows, and both net logs and dumps can contain secrets. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Electron netLog API](https://www.electronjs.org/docs/latest/api/net-log), [Node debugging guide](https://nodejs.org/learn/getting-started/debugging))

### Prioritized experiments

1. **Packaging/install matrix (highest priority):** create and install sample Electron apps using
   - manual `resources\app`,
   - manual `app.asar`,
   - Forge Squirrel,
   - Forge WiX MSI,
   - Forge MSIX,
   - electron-builder NSIS,
   - electron-builder portable,
   - electron-builder MSI,
   - electron-builder AppX/MSIX.  
   Record actual install paths, registry entries, runtime paths, userData/sessionData/log/crash locations, and externally visible markers. This will convert several current heuristics into product-specific truth tables. (Sources: [Electron application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution), [Electron Forge Squirrel maker](https://www.electronforge.io/config/makers/squirrel.windows), [Electron Forge WiX MSI maker](https://www.electronforge.io/config/makers/wix-msi), [Electron Forge MSIX maker](https://www.electronforge.io/config/makers/msix), [electron-builder Windows docs](https://www.electron.build/docs/win/), [electron-builder MSI docs](https://www.electron.build/docs/msi/), [electron-builder AppX docs](https://www.electron.build/docs/appx/))
2. **Runtime-association matrix:** build sample apps that exercise
   - one window,
   - multiple windows,
   - `BrowserView`,
   - `<webview>`,
   - opened DevTools,
   - service worker,
   - web worker,
   - `utilityProcess`,
   - Node child processes with `ELECTRON_RUN_AS_NODE`.  
   Compare OS-only results against `app.getAppMetrics()`, `webContents`, and CDP target lists. This directly answers the hardest association problem in this investigation. (Sources: [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model), [Electron app API](https://www.electronjs.org/docs/latest/api/app), [Electron webContents API](https://www.electronjs.org/docs/latest/api/web-contents), [Electron utilityProcess API](https://www.electronjs.org/docs/latest/api/utility-process), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables))
3. **Remote-debugging validation:** test `--remote-debugging-port=<fixed>`, and separately test Chromium-derived `--remote-debugging-port=0` behavior on target Electron baselines to see whether `DevToolsActivePort` is written in practice and whether CDP target metadata is sufficient for stable target/process association. (Sources: [Electron command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches), [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/))
4. **Crash/logging matrix:** run with and without `crashReporter`, WER LocalDumps, `ELECTRON_ENABLE_STACK_DUMPING`, `ELECTRON_DEFAULT_ERROR_MODE`, ProcDump, and MSIX deployment logging. Record which artifacts appear and whether the pipelines interfere with one another. (Sources: [Electron crashReporter API](https://www.electronjs.org/docs/latest/api/crash-reporter), [Electron environment variables](https://www.electronjs.org/docs/latest/api/environment-variables), [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps), [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump), [MSIX deployment troubleshooting](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-troubleshooting))
5. **ETW/WPT prototype:** capture a Windows Performance Toolkit/UIforETW trace from a representative Electron app, identify which Chromium/Electron-relevant provider/event sets are consistently useful, and decide whether Chromium Process Explorer should automate capture, analysis, or both. (Sources: [Windows ETW portal](https://learn.microsoft.com/en-us/windows/win32/etw/event-tracing-portal), [Chromium profiling guide](https://www.chromium.org/developers/profiling-chromium-and-webkit/), [Chromium ETW provider header](https://chromium.googlesource.com/chromium/src/+/main/base/win/event_trace_provider.h))

## Sources

### Primary sources

- Electron documentation and repo docs:
  - [Process model](https://www.electronjs.org/docs/latest/tutorial/process-model)
  - [app](https://www.electronjs.org/docs/latest/api/app)
  - [process](https://www.electronjs.org/docs/latest/api/process)
  - [ProcessMetric structure](https://github.com/electron/electron/blob/main/docs/api/structures/process-metric.md)
  - [BrowserWindow](https://www.electronjs.org/docs/latest/api/browser-window)
  - [webContents](https://www.electronjs.org/docs/latest/api/web-contents)
  - [session](https://www.electronjs.org/docs/latest/api/session)
  - [utilityProcess](https://www.electronjs.org/docs/latest/api/utility-process)
  - [commandLine](https://www.electronjs.org/docs/latest/api/command-line)
  - [command-line switches](https://www.electronjs.org/docs/latest/api/command-line-switches)
  - [environment variables](https://www.electronjs.org/docs/latest/api/environment-variables)
  - [crashReporter](https://www.electronjs.org/docs/latest/api/crash-reporter)
  - [netLog](https://www.electronjs.org/docs/latest/api/net-log)
  - [contentTracing](https://www.electronjs.org/docs/latest/api/content-tracing)
  - [application debugging](https://www.electronjs.org/docs/latest/tutorial/application-debugging)
  - [debugging the main process](https://www.electronjs.org/docs/latest/tutorial/debugging-main-process)
  - [application distribution](https://www.electronjs.org/docs/latest/tutorial/application-distribution)
  - [ASAR archives](https://www.electronjs.org/docs/latest/tutorial/asar-archives)
- Chromium / CDP / Node authoritative docs and source:
  - [Chromium content_switches.cc](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.cc)
  - [Chromium content_switches.h](https://chromium.googlesource.com/chromium/src/+/main/content/public/common/content_switches.h)
  - [Chromium debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/debugging.md)
  - [Chromium GPU debugging guide](https://chromium.googlesource.com/chromium/src/+/main/docs/gpu/debugging_gpu_related_code.md)
  - [Chromium user_data_dir.md](https://chromium.googlesource.com/chromium/src/+/main/docs/user_data_dir.md)
  - [Chromium ETW provider header](https://chromium.googlesource.com/chromium/src/+/main/base/win/event_trace_provider.h)
  - [Chromium profiling guide](https://www.chromium.org/developers/profiling-chromium-and-webkit/)
  - [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/)
  - [Node debugging guide](https://nodejs.org/learn/getting-started/debugging)
  - [Node inspector API](https://nodejs.org/api/inspector.html)
- Packaging / installer docs:
  - [Electron Forge Squirrel maker](https://www.electronforge.io/config/makers/squirrel.windows)
  - [Electron Forge WiX MSI maker](https://www.electronforge.io/config/makers/wix-msi)
  - [Electron Forge MSIX maker](https://www.electronforge.io/config/makers/msix)
  - [electron/windows-installer README](https://github.com/electron/windows-installer/blob/main/README.md)
  - [Squirrel custom events](https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/custom-squirrel-events-non-cs.md)
  - [electron-builder Windows docs](https://www.electron.build/docs/win/)
  - [electron-builder NSIS docs](https://www.electron.build/docs/nsis/)
  - [electron-builder MSI docs](https://www.electron.build/docs/msi/)
  - [electron-builder AppX docs](https://www.electron.build/docs/appx/)
  - [electron-builder configuration docs](https://www.electron.build/docs/configuration/)
- Microsoft documentation:
  - [MSIX packaged desktop apps](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
  - [MSIX deployment troubleshooting](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-troubleshooting)
  - [Get-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackage?view=windowsserver2025-ps)
  - [Get-AppxPackageManifest](https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackagemanifest?view=windowsserver2025-ps)
  - [Windows Installer uninstall key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)
  - [MSI ProductVersion](https://learn.microsoft.com/en-us/windows/win32/msi/productversion)
  - [VERSIONINFO resource](https://learn.microsoft.com/en-us/windows/win32/menurc/versioninfo-resource)
  - [Windows Error Reporting](https://learn.microsoft.com/en-us/windows/win32/wer/windows-error-reporting)
  - [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)
  - [Windows ETW portal](https://learn.microsoft.com/en-us/windows/win32/etw/event-tracing-portal)

### Secondary sources

- Comparative utility docs / repos:
  - [WebView2Utilities README](https://github.com/david-risney/WebView2Utilities/blob/main/README.md)
  - [Process Explorer](https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer)
  - [ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump)
  - [System Informer README](https://github.com/winsiderss/systeminformer/blob/master/README.md)
