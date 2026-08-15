# Other Chromium Platforms and Models for Chromium Process Explorer
Research date: **2026-08-10**

## Summary

The highest-value Windows additions after direct WebView2/Electron/CEF support are **Qt WebEngine**, **NW.js**, and **installed browser apps/PWAs/app-mode**, because each has strong, actionable fingerprints and materially different host/runtime relationships. Qt WebEngine is a distinct Chromium embedder with its own helper process (`QtWebEngineProcess.exe`), resources, and Qt-specific environment variables and profile paths; it merits **first-class support**. NW.js is also distinct enough for **first-class support** because it combines Chromium and Node.js, carries a required `package.json`/`package.nw` model, has a stable default user-data path on Windows, and exposes DevTools/crash-report features through documented runtime flags and manifest fields.  
Most language-specific CEF wrappers do **not** need separate engine detectors; they need **wrapper annotations** layered on top of generic CEF detection. The best examples are `CefSharp.BrowserSubprocess.exe`, `jcef_helper.exe`, and Delphi-style custom subprocess names such as `*_sp.exe`, all of which are useful for naming/annotation but still fundamentally map to CEF behavior.  
WebView2 integrations such as WPF, WinForms, WinUI 2/3, .NET MAUI, MAUI Blazor Hybrid, and Office Add-ins should generally stay under a single `webview2` runtime family, with optional **host-framework annotations**, because the Chromium-side process tree is still WebView2-based. Tauri is the most important **false-positive guard**: on Windows it uses **WebView2**, not a bundled Chromium runtime. Sciter and Ultralight are **not Chromium-based**; GeckoView is **Gecko** and Android-only; WKWebView is **WebKit** and Apple-only.  
For vendor-specific “Steam/Spotify-style” desktop clients, the safer design is a **generic `cef` or `chromium-generic` classification with evidence scoring** unless product-specific artifacts are verified locally.

### Legend

- **Verified fingerprint** = directly documented in a primary source below.
- **Heuristic** = useful in practice, but not guaranteed by primary docs.
- **Recommendation** = `first-class`, `generic + annotate`, `research later`, or `out of scope`.

---

## Taxonomy

| Layer | Meaning | Examples relevant to Windows diagnostics |
|---|---|---|
| **Engine family** | The underlying web engine/runtime | Chromium/WebView2, Chromium/CEF, Chromium/Qt WebEngine, WebKit, Gecko |
| **Embedding framework** | The native API surface that hosts the engine | Qt WebEngine, CEF, WebView2, NW.js |
| **Language wrapper** | A language/runtime-specific binding over an embedder | CefSharp, JCEF, CEF4Delphi, cefpython |
| **UI integration** | A framework-specific control or bridge | WebView2 WPF, WinForms, WinUI 2/3, .NET MAUI |
| **Distribution model** | Whether the runtime is shared or app-local | WebView2 Evergreen/shared, WebView2 Fixed/app-local, CEF app-local, Qt WebEngine app-local, browser-installed PWA/shared browser |
| **Installed web app model** | Browser-managed installed app/windowing mode | Chrome/Edge installed PWAs, browser app mode |
| **False-positive guard** | Something often mistaken for bundled Chromium | Tauri-on-Windows, Sciter, Ultralight, GeckoView, WKWebView |

---

## Comparison matrix for prioritization

### In-scope additions

| Platform ID | Uses Chromium on Windows? | Best verified fingerprints | Shared vs app-local | Recommendation |
|---|---|---|---|---|
| `qt-webengine` | Yes | `QtWebEngineProcess.exe`, `qtwebengine_*.pak`, `QTWEBENGINE_*` env vars, Qt profile/cache locations | App-local | **First-class** |
| `nwjs` | Yes | `nw.exe` or renamed stub, `package.json`, `package.nw`, `chromium-args`, `%LOCALAPPDATA%\<name>` | App-local | **First-class** |
| `cef` | Yes | `libcef.dll`, CEF `.pak`/locale files, CEF subprocess model/settings | App-local | Already first-class |
| `cef.cefsharp` | Yes, via CEF | `CefSharp.BrowserSubprocess.exe`, `CefSharp.*.dll` | App-local | **Generic CEF + annotate** |
| `cef.jcef` | Yes, via CEF | `jcef.dll`, `jcef_helper.exe`, JAR set | App-local | **Generic CEF + annotate** |
| `cef.cef4delphi` | Yes, via CEF | Delphi hosts, custom `*_sp.exe`, demo `cef\User Data` / `cef\cache` patterns | App-local | **Generic CEF + annotate** |
| `cef.cefpython` | Yes, via CEF | `subprocess` helper near module dir, old `cefpython3==66.1` packaging | App-local | **Research later** |
| `webview2.*` integrations | Yes, via WebView2 | same WebView2 runtime; host-framework packages/assemblies differentiate | Shared or fixed | **Generic WebView2 + annotate** |
| `chrome-pwa` / `edge-pwa` | Yes | `--app-id`, profile-bound install dirs, browser-managed app shortcuts/registrations | Shared browser runtime | **First-class app model** |
| `chromium-generic` | Maybe | standard Chromium process roles with no stronger family match | Varies | Fallback only |

### Explicit guards / exclusions

| Platform ID | Chromium on Windows? | Why it matters | Recommendation |
|---|---|---|---|
| `tauri-webview2` | Uses WebView2 on Windows, not bundled Chromium | Prevent false “custom Chromium runtime” claims | **Guard + map to WebView2** |
| `nonchromium.sciter` | No | Own engine + QuickJS | **Out of scope guard** |
| `nonchromium.ultralight` | No | WebKit-based | **Out of scope guard** |
| `nonchromium.geckoview` | No | Gecko, Android-only | **Out of scope guard** |
| `nonchromium.wkwebview` | No | WebKit, Apple-only | **Out of scope guard** |
| `chromium-mobile.android-webview` | Not Windows | Mobile-only scope boundary | **Out of scope** |
| `chromium-mobile.custom-tabs` | Not Windows | Browser delegation, Android-only | **Out of scope** |
| `chromium-embedded.cobalt` | Not a practical Windows desktop target | Chromium/Blink-derived, but embedded/TV-focused, single-process | **Out of scope** |

---

## Platform notes

## 1) Qt WebEngine (`qt-webengine`)
**Recommendation:** **first-class**  
**Confidence:** **high**

### Underlying engine
Qt WebEngine is explicitly **based on Chromium**, while remaining distinct from Google Chrome services and packaging. The Qt docs also expose runtime Chromium version APIs (`qWebEngineChromiumVersion()` and `qWebEngineChromiumSecurityPatchVersion()`), and the current source tree carries a `CHROMIUM_VERSION` file.  
Sources: [Qt overview](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-overview.qdoc), [Qt CHROMIUM_VERSION](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/CHROMIUM_VERSION).

### Process model and host association
Qt separates page rendering and JavaScript execution into **`QtWebEngineProcess`**, distinct from the GUI host process. Qt’s deployment docs say the helper must be shipped alongside the application, and the debug deployment helper logic shows Windows candidates `QtWebEngineProcess` and `QtWebEngineProcessd`.  
Sources: [Qt overview](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-overview.qdoc), [Qt deploying](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-deploying.qdoc), [Qt deploy support](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/api/Qt6WebEngineCoreDeploySupport.cmake).

### Verified fingerprints
- `QtWebEngineProcess.exe` on Windows; debug deployments can use `QtWebEngineProcessd`.  
- Required resources: `qtwebengine_resources.pak`, `qtwebengine_devtools_resources.pak`, `qtwebengine_resources_100p.pak`, `qtwebengine_resources_200p.pak`, `icudtl.dat`, `v8_context_snapshot.bin`, and `qtwebengine_locales\*.pak`.  
- Path controls: `QTWEBENGINEPROCESS_PATH`, `QTWEBENGINE_RESOURCES_PATH`, `QTWEBENGINE_LOCALES_PATH`, or `--webEngineArgs --webengine-*-path=...`.  
Sources: [Qt deploying](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-deploying.qdoc), [Qt core CMake resource lists](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/api/CMakeLists.txt).

### Runtime/user-data paths
Qt profile storage is **not a single global Windows path** like WebView2/CEF. Instead, Qt defaults profile data below `QStandardPaths::AppDataLocation/QtWebEngine/<storageName>` and cache below `QStandardPaths::CacheLocation/QtWebEngine/<storageName>`. Qt’s tests also show the default/off-the-record profile using `.../QtWebEngine/OffTheRecord`, while cache remains memory-only.  
Sources: [QWebEngineProfileBuilder](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/api/qwebengineprofilebuilder.cpp), [QML profile prototype](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/webenginequick/api/qquickwebengineprofileprototype.cpp), [Qt tests](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/tests/auto/quick/qmltests/data/tst_basicProfiles.qml).

### Remote debugging / logging / diagnostics
Qt provides unusually strong diagnostics:
- `--webEngineArgs --remote-debugging-port=<port>`
- `QTWEBENGINE_REMOTE_DEBUGGING`
- `QTWEBENGINE_CHROMIUM_FLAGS`
- `--enable-logging --log-level=0`, `--v=1`
- `QT_LOGGING_RULES=qt.webenginecontext.debug=true`
- `--single-process`
- `--enable-features=NetworkServiceInProcess`  
Sources: [Qt debugging](https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-debugging.qdoc).

### Why it deserves first-class support
Qt WebEngine has a distinct helper executable, a stable resource set, stable environment-variable knobs, and source-documented profile/cache behavior. Those are strong enough for reliable process-tree and install-tree detection.

---

## 2) NW.js (`nwjs`)
**Recommendation:** **first-class**  
**Confidence:** **high**

### Underlying engine
NW.js is an app runtime based on **Chromium and Node.js**, intended for packaged desktop apps. Current official downloads (as of the repo README captured here) show Windows builds and a release line based on **Chromium 151**.  
Sources: [NW.js README](https://github.com/nwjs/nw.js/blob/main/README.md), [Getting Started](https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Getting%20Started.md).

### Process model and host association
NW.js is clearly Chromium-derived and documents a **single-process override**: `--disable-crash-handler=true` only matters when combined with `--single-process`, and the docs note that this results in **only one NW process**. That implies the normal model is multi-process. In production, the host is `nw.exe` or a renamed copy, while Chromium subprocess roles are still expected underneath.  
Source: [NW.js command-line options](https://github.com/nwjs/nw.js/blob/main/docs/References/Command%20Line%20Options.md).

### Verified fingerprints
- App manifest: required `package.json` with at least `name` and `main`.  
- Packaging forms: plain files next to `nw.exe`, folder named `package.nw`, zip renamed `package.nw`, or `package.nw` appended directly to `nw.exe` to create `app.exe`.  
- App-specific Chromium flags via manifest `chromium-args`; runtime pre-args via `NW_PRE_ARGS`.  
- Windows launcher name is often still `nw.exe`, but packaging guidance explicitly supports hiding NW.js inside a renamed executable.  
Sources: [Getting Started](https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Getting%20Started.md), [Manifest Format](https://github.com/nwjs/nw.js/blob/main/docs/References/Manifest%20Format.md), [Package and Distribute](https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Package%20and%20Distribute.md).

### Runtime/user-data paths
`--user-data-dir` is supported, and the default Windows data directory is `%LOCALAPPDATA%/<name-in-manifest>/`. NW.js documents that this directory contains **stored data, caches, and crash dumps**.  
Source: [NW.js command-line options](https://github.com/nwjs/nw.js/blob/main/docs/References/Command%20Line%20Options.md).

### Remote debugging / logging / crash reporting
- DevTools are available in **SDK flavor only** and can be opened with `F12` or `win.showDevTools()`.  
- Remote debugging uses `--remote-debugging-port=<port>` and serves DevTools at `http://localhost:<port>/`.  
- Crash upload can be configured through manifest field `crash_report_url`; the manifest docs say the request includes the app `name`, `version`, the minidump, and crashing-process command-line switches.  
Sources: [Debugging with DevTools](https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Debugging%20with%20DevTools.md), [Manifest Format](https://github.com/nwjs/nw.js/blob/main/docs/References/Manifest%20Format.md).

### Why it deserves first-class support
NW.js is not “just generic Chromium”: the required manifest, `package.nw` model, default `%LOCALAPPDATA%\<name>` profile path, and documented packaging/stub patterns make it distinguishable and valuable to detect directly.

---

## 3) Generic CEF and CEF-family wrappers
**Recommendation:** generic `cef` detector first, then wrapper annotations  
**Confidence:** **high** for generic CEF; **medium-high** for wrapper-specific naming

### Generic CEF baseline
CEF is a simple framework for embedding **Chromium-based browsers** in other applications. CEF’s general usage guide documents:
- browser/render/GPU multi-process architecture,
- same-exe subprocess spawning by default,
- optional separate helper via `browser_subprocess_path`,
- app-local Windows layout with `libcef.dll`, `icudtl.dat`, `chrome_100_percent.pak`, `chrome_200_percent.pak`, `resources.pak`, `snapshot_blob.bin`, `v8_context_snapshot.bin`, `locales\*.pak`, and ANGLE/D3D support DLLs,
- `cache_path`, `log_file`, `resources_dir_path`, `locales_dir_path`, `remote_debugging_port`.  
Sources: [CEF README](https://github.com/chromiumembedded/cef/blob/master/README.md), [CEF general usage](https://github.com/chromiumembedded/cef/blob/master/docs/general_usage.md), [cefsimple Win entry point](https://github.com/chromiumembedded/cef/blob/master/tests/cefsimple/cefsimple_win.cc).

### Crash reporting / diagnostics
CEF’s crash docs say Windows/macOS use **Crashpad**, with `crash_reporter.cfg` next to the main EXE on Windows, and local storage under `C:\Users\[CurrentUser]\AppData\Local\[AppName]\User Data` when `AppName` is set. The docs also describe minidump metadata containing process type and command-line switches.  
Source: [CEF crash reporting](https://github.com/chromiumembedded/cef/blob/master/docs/crash_reporting.md).

### What this means for Chromium Process Explorer
Treat “raw CEF” as the engine/runtime family, then annotate wrapper or vendor flavor only when strong extra evidence exists. Product names alone are weak; `libcef.dll` + CEF resource layout + CEF-style helper/configuration are strong.

---

### 3a) CefSharp (`cef.cefsharp`)
**Recommendation:** **generic CEF + annotate**  
**Confidence:** **high**

CefSharp is a .NET wrapper around CEF and provides WPF, WinForms, OffScreen, and HwndHost variants. Its settings surface exposes `BrowserSubprocessPath`, `CachePath`, `RootCachePath`, `LocalesDirPath`, `ResourcesDirPath`, `LogFile`, and `RemoteDebuggingPort`. For .NET apps, the default subprocess is the provided **`CefSharp.BrowserSubprocess.exe`** unless overridden. CefSharp also documents the default Windows root cache location as `AppData\Local\CEF\User Data` if `RootCachePath` and `CachePath` are left empty. Its WPF packaging docs explicitly say the NuGet package copies the required `.dll` and `.pak` files into the output path.  
Sources: [CefSharp README](https://github.com/cefsharp/CefSharp/blob/master/README.md), [CefSharp settings](https://github.com/cefsharp/CefSharp/blob/master/CefSharp.Core/CefSettingsBase.cs), [CefSharp BrowserSubprocess](https://github.com/cefsharp/CefSharp/blob/master/CefSharp.BrowserSubprocess/Program.cs), [CefSharp WPF README](https://github.com/cefsharp/CefSharp/blob/master/README.WPF.md).

**Best annotation clues**
- `CefSharp.BrowserSubprocess.exe`
- `CefSharp.Wpf.dll`, `CefSharp.WinForms.dll`, `CefSharp.Core.dll`
- .NET host EXE + CEF subtree

---

### 3b) JCEF (`cef.jcef`)
**Recommendation:** **generic CEF + annotate**  
**Confidence:** **high**

JCEF is the Java wrapper for CEF. On Windows, `CefApp.java` sets a default subprocess path of **`jcef_helper.exe`** if none is provided. The redistribution readme lists a strong Windows artifact set: `jcef.jar`, `jcef.dll`, `jcef_helper.exe`, `libcef.dll`, `icudtl.dat`, `natives_blob.bin`, `snapshot_blob.bin`, `locales`, `cef.pak`, `cef_100_percent.pak`, `cef_200_percent.pak`, `cef_extensions.pak`, `devtools_resources.pak`, and D3D/ANGLE DLLs. JCEF settings expose the usual CEF knobs (`browser_subprocess_path`, `cache_path`, `root_cache_path`, `log_file`, `resources_dir_path`, `locales_dir_path`, `remote_debugging_port`).  
Sources: [JCEF README](https://github.com/chromiumembedded/java-cef/blob/master/README.md), [CefSettings.java](https://github.com/chromiumembedded/java-cef/blob/master/java/org/cef/CefSettings.java), [CefApp.java](https://github.com/chromiumembedded/java-cef/blob/master/java/org/cef/CefApp.java), [Windows redistrib README](https://github.com/chromiumembedded/java-cef/blob/master/tools/distrib/win64/README.redistrib.txt), [jcef_helper.rc](https://github.com/chromiumembedded/java-cef/blob/master/native/jcef_helper.rc), [context.cpp](https://github.com/chromiumembedded/java-cef/blob/master/native/context.cpp).

**Best annotation clues**
- `jcef_helper.exe`
- `jcef.dll`
- JAR payload near the EXE (`jcef.jar`, JOGL/GlueGen jars)

---

### 3c) CEF4Delphi (`cef.cef4delphi`)
**Recommendation:** **generic CEF + annotate**  
**Confidence:** **medium-high**

CEF4Delphi is a Delphi/Lazarus wrapper over CEF and currently advertises **CEF 151.3.16 / Chromium 151.0.7922.109**. Its core application source exposes `BrowserSubprocessPath`, `Cache`, `RootCache`, `LogFile`, `ResourcesDirPath`, `LocalesDirPath`, and `RemoteDebuggingPort`. The remote-debugging comments are especially useful: port `0` can request an ephemeral DevTools port, which is then printed to stderr and, when a cache directory exists, written to **`<cache-dir>/DevToolsActivePort`**. The docs also tell users to inspect through `chrome://inspect`. Demo apps commonly use explicit Windows-relative paths such as `cef\cache`, `cef\User Data`, and custom subprocess executables such as `SimpleBrowser_sp.exe`.  
Sources: [CEF4Delphi README](https://github.com/salvadordf/CEF4Delphi/blob/master/README.md), [uCEFApplicationCore.pas](https://github.com/salvadordf/CEF4Delphi/blob/master/source/uCEFApplicationCore.pas), [Delphi VCL subprocess demo](https://github.com/salvadordf/CEF4Delphi/blob/master/demos/Delphi_VCL/SubProcess/uCEFLoader.pas).

**Best annotation clues**
- Custom helper EXEs such as `*_sp.exe`
- Delphi/Lazarus host artifacts
- Optional `cef\User Data`, `cef\cache`, `cef\locales` layouts in packaged apps

---

### 3d) cefpython (`cef.cefpython`)
**Recommendation:** **research later**  
**Confidence:** **medium**

cefpython is also a CEF wrapper, but its official README still installs **`cefpython3==66.1`** and documents Windows support only through Python 3.9-era releases, which makes it less relevant for a 2026 Windows-first prioritization. Its application settings docs still expose good fingerprints: `browser_subprocess_path`, `cache_path`, `log_file`, `remote_debugging_port`, and default behaviors. The initialization code sets:
- `resources_dir_path` to the module directory,
- `locales_dir_path` to `module_dir\locales`,
- `browser_subprocess_path` to `module_dir\subprocess`,
- `remote_debugging_port` to a **random port** in `49152–65535` when left at `0`,
- `cache_path` empty => incognito/in-memory, with a unique temp cache dir per run.  
Sources: [cefpython README](https://github.com/cztomczak/cefpython/blob/master/README.md), [ApplicationSettings](https://github.com/cztomczak/cefpython/blob/master/api/ApplicationSettings.md), [cefpython.pyx](https://github.com/cztomczak/cefpython/blob/master/src/cefpython.pyx).

**Why not first**
The wrapper looks historically important, but current official packaging/versioning suggests lower present-day Windows relevance than Qt/NW.js/CefSharp/JCEF.

---

## 4) WebView2 wrappers and integrations
**Recommendation:** **generic WebView2 + annotate**, not separate engine families  
**Confidence:** **high**

These do **not** change the Chromium runtime family on Windows; they change the **host-side control/framework**.

### WPF / WinForms
Microsoft’s WebView2 getting-started docs use the `Microsoft.Web.WebView2` SDK package and expose a `WebView2` control in WPF and WinForms. For detection, these are best treated as WebView2 hosts with optional static annotations if `Microsoft.Web.WebView2.Wpf.dll` / `Microsoft.Web.WebView2.WinForms.dll` or corresponding package metadata are present.  
Sources: [WebView2 WPF](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf), [WebView2 WinForms](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms).

### WinUI 3 (Windows App SDK)
WinUI 3 apps use the **`Microsoft.WindowsAppSDK`** package, which includes the WebView2 SDK, and the WinUI 3-specific docs call out custom `CoreWebView2Environment` support. For diagnostics, this matters because a WinUI 3 app may deliberately select a custom browser folder or user-data folder even though it is still fundamentally WebView2.  
Sources: [WinUI 3 getting started](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winui), [WinUI 3 platform notes](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/winui3-windows-app-sdk).

### WinUI 2 (UWP)
WinUI 2 installs `Microsoft.UI.Xaml`, which brings in `Microsoft.Web.WebView2` as a dependency. It is UWP-only, exposes only a subset of WebView2 interfaces directly, and its platform notes say **store-signed WinUI 2 apps cannot launch DevTools directly**; remote debugging is the workaround.  
Sources: [WinUI 2 getting started](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winui2), [WinUI 2 platform notes](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/winui2-uwp).

### .NET MAUI / MAUI Blazor Hybrid on Windows
.NET MAUI’s `WebView` uses **WebView2 on Windows**, and the docs explicitly warn that Program Files installs may fail unless `WEBVIEW2_USER_DATA_FOLDER` is redirected to a writable location. The .NET MAUI Blazor Hybrid docs separately say **WebView2 is required on Windows** for native apps.  
Sources: [.NET MAUI WebView](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/webview?view=net-maui-10.0), [.NET MAUI Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/tutorials/maui?view=aspnetcore-10.0).

### Office Add-ins
Office Add-ins on Windows use **Microsoft Edge (Chromium-based) with WebView2** on desktop clients. The Office docs also give a very useful enterprise/WIP fingerprint for `msedgewebview2.exe`:
- **Publisher**: `O=MICROSOFT CORPORATION, L=REDMOND, S=WASHINGTON, C=US`
- **Product Name**: `MICROSOFT EDGE WEBVIEW2`
- **File**: `MSEDGEWEBVIEW2.EXE`  
This is valuable for host-to-browser association in Office processes such as Word/Excel/PowerPoint.  
Source: [Office Add-ins browser/webview matrix](https://learn.microsoft.com/en-us/office/dev/add-ins/concepts/browsers-used-by-office-web-add-ins).

### Detection guidance
Do **not** split WPF/WinForms/WinUI/MAUI into separate runtime detectors at the process-tree level. Keep one `webview2` engine/runtime detector and add host-framework annotations only when extra evidence exists.

---

## 5) Installed browser apps / PWA / app mode
**Recommendation:** **first-class app model**  
**Confidence:** **high** for Chrome-specific fingerprints; **medium** for Edge implementation details beyond user-visible behavior

### Why this is a different model
Installed PWAs are **not bundled Chromium runtimes**. They are browser-managed installed apps using a shared Chrome/Edge installation, a shared user profile, and Chromium’s normal multi-process browser model. That makes them diagnostically important for Chromium Process Explorer, but they should be represented as **browser-installed apps**, not as standalone embedders.  
Sources: [Chromium Windows PWA integration](https://raw.githubusercontent.com/chromium/chromium/main/docs/windows_pwa_integration.md), [Chromium process model](https://raw.githubusercontent.com/chromium/chromium/main/docs/process_model_and_site_isolation.md), [Edge PWA UX](https://learn.microsoft.com/en-us/microsoft-edge/progressive-web-apps/ux).

### Verified Chrome-specific fingerprints
Chromium’s Windows PWA integration doc provides unusually strong Windows artifacts:
- desktop shortcut launches **`chrome_proxy.exe`**
- command line includes **`--app-id=<app_id>`** and the profile
- file-handler support uses **`chrome_pwa_launcher.exe`**
- per-app install dir lives under **`<profile_dir>/Web Applications/<app_id>`**
- file-association command is stored under `HKCU\Software\Classes\<progID>\shell\open\command`
- launcher resolves the browser path through the **`Last Browser`** file in the user-data directory.  
Sources: [Chromium Windows PWA integration](https://raw.githubusercontent.com/chromium/chromium/main/docs/windows_pwa_integration.md), [chrome_pwa_launcher README](https://raw.githubusercontent.com/chromium/chromium/main/chrome/browser/web_applications/chrome_pwa_launcher/README.md).

### Verified Edge user-visible behavior
Edge’s docs confirm that installed PWAs appear in:
- **Taskbar**
- **Start menu**
- **Alt+Tab**
- **Windows Apps & features**
- **`edge://apps`**  
This is enough to justify a first-class installed-app model even if this pass did not uncover a primary Edge source equivalent to Chromium’s `chrome_proxy.exe` doc.  
Source: [Edge PWA UX](https://learn.microsoft.com/en-us/microsoft-edge/progressive-web-apps/ux).

### Detection guidance
**Verified**
- `--app-id=...`
- profile-bound browser-app install relationships
- Windows Start/Taskbar/Apps & features presence
- per-profile `Web Applications\<app_id>` directory for Chromium/Chrome

**Heuristics to validate locally**
- Edge-specific proxy/helper names and shortcut forms
- non-installed browser “app mode” launched with ad-hoc flags

### Why it deserves first-class support
PWAs change the relationship between “host app”, browser process tree, install location, and user-data location in ways that ordinary Chromium browser detection will miss.

---

## 6) Generic raw Chromium / custom embedders
**Recommendation:** fallback family only (`chromium-generic`)  
**Confidence:** **medium**

Some Windows apps embed Chromium more directly or fork/customize it without advertising a clean framework identity. Chromium’s own docs still provide useful common ground:
- desktop Chromium uses a **multi-process** model with browser/renderer separation and site isolation on Windows,
- command-line switches are visible via `chrome://version`,
- command-line behavior is a primary diagnostic surface.  
Sources: [Chromium process model](https://raw.githubusercontent.com/chromium/chromium/main/docs/process_model_and_site_isolation.md), [Chromium flags/how-to](https://www.chromium.org/developers/how-tos/run-chromium-with-flags/).

### Recommendation for this category
Only use `chromium-generic` when:
1. the process tree clearly looks Chromium-like, **and**
2. no stronger family match exists for WebView2/Electron/CEF/Qt/NW.js/PWA.

For named commercial apps, prefer generic classification unless you have verified vendor-specific disk layout or helper-process fingerprints from local samples.

---

## 7) Common false positives and scope boundaries

### Tauri (`tauri-webview2`)
On Windows, Tauri uses **WebView2** via WRY; on macOS/iOS it uses WKWebView; on Linux it uses WebKitGTK; on Android it uses Android System WebView. So Tauri should never be reported as “bundled Chromium” on Windows unless the app separately ships something else.  
Sources: [Tauri README](https://github.com/tauri-apps/tauri/blob/dev/README.md), [Tauri webview versions](https://github.com/tauri-apps/tauri-docs/blob/v2/src/content/docs/reference/webview-versions.md).

### Sciter
Sciter uses its **own HTML/CSS engine and JS runtime**; its docs say it uses QuickJS and its own architecture. It is not Chromium-based.  
Sources: [Sciter intro](https://docs.sciter.com/docs/intro/), [Sciter architecture](https://sciter.com/developers/engine-architecture/).

### Ultralight
Ultralight says it is **based on WebKit** and describes itself as a platform-agnostic WebKit port for games and desktop apps. It is not Chromium-based.  
Sources: [Ultralight README](https://github.com/ultralight-ux/Ultralight/blob/master/README.md), [Ultralight home](https://ultralig.ht/).

### Cobalt
Cobalt is powered by **Chromium and Blink**, but its official docs describe it as a **single-process**, resource-constrained, embedded/TV-focused web platform with Starboard portability, not a normal Windows desktop app model. Treat it as out of current Windows scope.  
Source: [Cobalt overview](https://developers.google.cn/youtube/cobalt/docs/overview).

### GeckoView
GeckoView wraps **Mozilla Gecko** in a reusable **Android** library and is explicitly not just Android WebView. It is not a Windows platform target.  
Source: [GeckoView wiki](https://wiki.mozilla.org/Mobile/GeckoView).

### Android WebView / Chrome Custom Tabs
Android `WebView` is an Android in-app web component; Chrome Custom Tabs are powered by the user’s browser and share browser state. Both are mobile-only scope items.  
Sources: [Android WebView docs](https://developer.android.com/develop/ui/views/layout/webapps/webview), [Chrome Custom Tabs docs](https://developer.chrome.com/docs/android/custom-tabs/).

### WKWebView
WKWebView is Apple’s WebKit in-app browser view; it is not Chromium and not relevant to Windows runtime detection.  
Source: [Apple WKWebView](https://developer.apple.com/documentation/webkit/wkwebview).

---

## Recommendations for Chromium Process Explorer

## 1) Detection architecture

### A. Separate **engine family**, **runtime family**, and **host annotation**
Use a layered model:

1. **Engine family**: `chromium`, `webkit`, `gecko`, `unknown`
2. **Runtime family**: `webview2`, `cef`, `qt-webengine`, `nwjs`, `browser-pwa`, `chromium-generic`
3. **Host annotation**: `cefsharp`, `jcef`, `cef4delphi`, `wpf`, `winforms`, `winui2`, `winui3`, `maui`, `office-addin`, `tauri-webview2`

That prevents false claims like “Tauri bundles Chromium” while still allowing “Tauri app using WebView2 on Windows”.

### B. Score evidence instead of relying on one filename
Use a weighted evidence model:

- **Strong**: unique helper exe (`QtWebEngineProcess.exe`, `jcef_helper.exe`, `CefSharp.BrowserSubprocess.exe`), `libcef.dll`, `package.json`, `package.nw`, Chromium PWA launcher/proxy artifacts
- **Medium**: command-line flags (`--app-id`, `--remote-debugging-port`, `--user-data-dir`, `--webEngineArgs`), known resource packs, known user-data directories
- **Weak**: app name/vendor folklore, generic Chromium subprocess role names

### C. Prefer **search on disk near the image** over product-name folklore
For active processes:
- process image path
- sibling files/directories
- parent/child process tree
- command line
- version/product metadata
- optional loaded modules (if later added)

For install discovery:
- known runtime layouts
- Start-menu shortcuts / PWA launcher dirs
- registry (especially PWA file associations)
- profile/cache/log directories

## 2) Proposed normalized platform identifiers

### Runtime family
- `qt-webengine`
- `nwjs`
- `cef`
- `webview2`
- `browser-pwa`
- `chromium-generic`

### Optional annotations
- `cef.cefsharp`
- `cef.jcef`
- `cef.cef4delphi`
- `cef.cefpython`
- `webview2.wpf`
- `webview2.winforms`
- `webview2.winui2`
- `webview2.winui3`
- `webview2.maui`
- `webview2.office-addin`
- `webview2.tauri`

### False-positive / exclusion labels
- `nonchromium.sciter`
- `nonchromium.ultralight`
- `nonchromium.geckoview`
- `nonchromium.wkwebview`
- `chromium-mobile.android-webview`
- `chromium-mobile.custom-tabs`
- `chromium-embedded.cobalt`

## 3) High-value first additions after WebView2/Electron/CEF

1. **`qt-webengine`**
   - strongest new Windows-specific embedder family
   - strong helper/resource/env-var fingerprints

2. **`nwjs`**
   - distinct packaging and profile model
   - still active and Chromium-current
   - common enough to justify direct support

3. **`browser-pwa`**
   - diagnostically different shared-browser model
   - important for host/browser/install confusion
   - high value for Start-menu/shortcut/registry/install views

4. **CEF wrapper annotations**
   - `cef.cefsharp`
   - `cef.jcef`
   - `cef.cef4delphi`

5. **False-positive guard for `tauri-webview2`**
   - common enough to matter
   - cheap to implement once WebView2 and non-Chromium guards exist

## 4) Prioritized validation experiments

### Tier 1
1. **Qt WebEngine sample app**
   - confirm `QtWebEngineProcess.exe`
   - confirm remote debugging and logs
   - confirm profile/cache directories for named and off-the-record profiles

2. **NW.js sample + packaged/renamed build**
   - confirm `package.json` / `package.nw` detection
   - confirm `%LOCALAPPDATA%\<name>` path
   - confirm DevTools / remote debug / crash-report behaviors

3. **Chrome installed PWA with file handlers**
   - inspect desktop/start shortcut targets
   - inspect `Web Applications\<app_id>`
   - inspect `HKCU\Software\Classes\<progID>`

### Tier 2
4. **CefSharp WinForms/WPF sample**
   - verify `CefSharp.BrowserSubprocess.exe`
   - verify default/log/cache/resource paths

5. **JCEF sample**
   - verify `jcef_helper.exe` + redistribution artifact set

6. **CEF4Delphi sample**
   - verify `*_sp.exe`, `cef\User Data`, `DevToolsActivePort`

### Tier 3
7. **Office Add-in on desktop Office**
   - verify association from Office host process to WebView2 subtree

8. **Tauri sample**
   - confirm WebView2 child process and absence of app-local Chromium runtime artifacts

9. **Negative controls**
   - Sciter app
   - Ultralight app
   - ensure they are **not** labeled Chromium

## Real-world validation targets

The following applications are useful diagnostic targets, but commercial
products can change frameworks between releases. Treat the named product as a
test candidate and verify the installed version using local process, module,
and filesystem evidence.

| Platform | Candidate | Useful fingerprints or setup |
| --- | --- | --- |
| CEF | Steam | `steamwebhelper.exe`, `libcef.dll`, CEF resources; verify the current client installation |
| CEF | OBS Studio Browser Source | Add a Browser Source and inspect the `obs-browser` helper/runtime files |
| CEF | `cefclient` / `cefsimple` | Controlled upstream baselines for process roles, switches, DevTools, and packaging |
| Electron | Visual Studio Code, Slack, Discord, Signal, GitHub Desktop, Postman | Verify package resources, Electron metadata, main process, and Chromium children |
| NW.js | RPG Maker MV/MZ games, Construct desktop exports, WebTorrent Desktop | Look for `nw.dll`, `package.json`, `package.nw`, and the NW.js profile layout |
| Qt WebEngine | Qt Simple Browser or another app shipping `QtWebEngineProcess.exe` | Controlled Qt baseline with helper/resource/profile evidence |
| CEF through an engine | Unreal Engine Web Browser widget samples | Feature-dependent; verify the shipped CEF runtime rather than assuming every Unreal product uses it |

Steam and OBS are particularly useful because they exercise realistic CEF
trees. An RPG Maker MV/MZ application is an accessible packaged NW.js example.
Qt's Simple Browser is preferable to guessing which version of a commercial Qt
application still uses Qt WebEngine.

Claims about products such as Spotify, Adobe, Autodesk, or other commercial
clients should remain version-dependent until the local installation shows
corroborating fingerprints.

Additional references:

- OBS Browser Source: https://github.com/obsproject/obs-browser
- Electron application directory: https://www.electronjs.org/apps
- CEF sample applications:
  https://github.com/chromiumembedded/cef/tree/master/tests
- Qt WebEngine Simple Browser:
  https://doc.qt.io/qt-6/qtwebengine-webenginewidgets-simplebrowser-example.html

---

## Bottom line

If Chromium Process Explorer adds only a few things next, they should be:

1. **Qt WebEngine**
2. **NW.js**
3. **installed PWA/app-mode detection**
4. **CEF wrapper annotations**
5. **Tauri/non-Chromium false-positive guards**

That set maximizes Windows diagnostic value while minimizing false claims.

---

## Sources

### Qt WebEngine
- Qt overview: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-overview.qdoc
- Qt deploying: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-deploying.qdoc
- Qt debugging: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/doc/src/qtwebengine-debugging.qdoc
- Qt deploy support: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/api/Qt6WebEngineCoreDeploySupport.cmake
- Qt resource/process naming: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/api/CMakeLists.txt
- Qt profile defaults: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/src/core/api/qwebengineprofilebuilder.cpp
- Qt test evidence: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/tests/auto/quick/qmltests/data/tst_basicProfiles.qml
- Qt Chromium version file: https://github.com/qt/qtwebengine/blob/d4cf8fa733e42a7fce5ffdaf817d32c3721c18b9/CHROMIUM_VERSION

### CEF and wrappers
- CEF README: https://github.com/chromiumembedded/cef/blob/master/README.md
- CEF general usage: https://github.com/chromiumembedded/cef/blob/master/docs/general_usage.md
- CEF crash reporting: https://github.com/chromiumembedded/cef/blob/master/docs/crash_reporting.md
- CEF Win sample entry point: https://github.com/chromiumembedded/cef/blob/master/tests/cefsimple/cefsimple_win.cc
- CefSharp README: https://github.com/cefsharp/CefSharp/blob/master/README.md
- CefSharp settings: https://github.com/cefsharp/CefSharp/blob/master/CefSharp.Core/CefSettingsBase.cs
- CefSharp subprocess program: https://github.com/cefsharp/CefSharp/blob/master/CefSharp.BrowserSubprocess/Program.cs
- CefSharp WPF packaging notes: https://github.com/cefsharp/CefSharp/blob/master/README.WPF.md
- JCEF README: https://github.com/chromiumembedded/java-cef/blob/master/README.md
- JCEF settings: https://github.com/chromiumembedded/java-cef/blob/master/java/org/cef/CefSettings.java
- JCEF app startup/helper path: https://github.com/chromiumembedded/java-cef/blob/master/java/org/cef/CefApp.java
- JCEF native context: https://github.com/chromiumembedded/java-cef/blob/master/native/context.cpp
- JCEF Windows redistrib: https://github.com/chromiumembedded/java-cef/blob/master/tools/distrib/win64/README.redistrib.txt
- JCEF helper version resource: https://github.com/chromiumembedded/java-cef/blob/master/native/jcef_helper.rc
- CEF4Delphi README: https://github.com/salvadordf/CEF4Delphi/blob/master/README.md
- CEF4Delphi core settings: https://github.com/salvadordf/CEF4Delphi/blob/master/source/uCEFApplicationCore.pas
- CEF4Delphi Windows demo: https://github.com/salvadordf/CEF4Delphi/blob/master/demos/Delphi_VCL/SubProcess/uCEFLoader.pas
- cefpython README: https://github.com/cztomczak/cefpython/blob/master/README.md
- cefpython application settings: https://github.com/cztomczak/cefpython/blob/master/api/ApplicationSettings.md
- cefpython initialization defaults: https://github.com/cztomczak/cefpython/blob/master/src/cefpython.pyx

### NW.js
- NW.js README: https://github.com/nwjs/nw.js/blob/main/README.md
- Getting started: https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Getting%20Started.md
- Package and distribute: https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Package%20and%20Distribute.md
- Manifest format: https://github.com/nwjs/nw.js/blob/main/docs/References/Manifest%20Format.md
- Command-line options: https://github.com/nwjs/nw.js/blob/main/docs/References/Command%20Line%20Options.md
- Debugging with DevTools: https://github.com/nwjs/nw.js/blob/main/docs/For%20Users/Debugging%20with%20DevTools.md

### WebView2 integrations
- WPF: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf
- WinForms: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms
- WinUI 3 getting started: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winui
- WinUI 3 platform notes: https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/winui3-windows-app-sdk
- WinUI 2 getting started: https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winui2
- WinUI 2 platform notes: https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/winui2-uwp
- .NET MAUI WebView: https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/webview?view=net-maui-10.0
- .NET MAUI Blazor Hybrid: https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/tutorials/maui?view=aspnetcore-10.0
- Office Add-ins browser/webview matrix: https://learn.microsoft.com/en-us/office/dev/add-ins/concepts/browsers-used-by-office-web-add-ins

### PWAs / Chromium process model
- Chromium Windows PWA integration: https://raw.githubusercontent.com/chromium/chromium/main/docs/windows_pwa_integration.md
- Chrome PWA launcher README: https://raw.githubusercontent.com/chromium/chromium/main/chrome/browser/web_applications/chrome_pwa_launcher/README.md
- Chromium process model and site isolation: https://raw.githubusercontent.com/chromium/chromium/main/docs/process_model_and_site_isolation.md
- Chromium flags/how-to: https://www.chromium.org/developers/how-tos/run-chromium-with-flags/
- Edge PWA user/OS integration: https://learn.microsoft.com/en-us/microsoft-edge/progressive-web-apps/ux

### False-positive / out-of-scope references
- Tauri README: https://github.com/tauri-apps/tauri/blob/dev/README.md
- Tauri webview versions: https://github.com/tauri-apps/tauri-docs/blob/v2/src/content/docs/reference/webview-versions.md
- Sciter intro: https://docs.sciter.com/docs/intro/
- Sciter architecture: https://sciter.com/developers/engine-architecture/
- Ultralight README: https://github.com/ultralight-ux/Ultralight/blob/master/README.md
- Ultralight home: https://ultralig.ht/
- Cobalt overview: https://developers.google.cn/youtube/cobalt/docs/overview
- GeckoView wiki: https://wiki.mozilla.org/Mobile/GeckoView
- Android WebView docs: https://developer.android.com/develop/ui/views/layout/webapps/webview
- Chrome Custom Tabs docs: https://developer.chrome.com/docs/android/custom-tabs/
- Apple WKWebView docs: https://developer.apple.com/documentation/webkit/wkwebview
