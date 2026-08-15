# Windows launch-time command-line instrumentation

## Goal

Chromium-based applications often need diagnostic switches enabled before the
browser process initializes. The difficult case is an external diagnostic tool
that wants to modify only the initial browser/main process, not every renderer,
GPU, utility, or crash-handler process that later uses the same executable.

The safest answer is to control the original launch or cooperate with the
embedder. Windows has no general, supported, system-wide facility that
transparently appends arguments only to the first process in an arbitrary
Chromium process family.

---

## Recommended approaches

### 1. Launch wrapper or test harness

When Chromium Process Explorer controls startup, it should call
`CreateProcessW` with the desired command line, environment, working directory,
and restricted inherited handles.

This is the most reliable external approach because:

- the intended arguments are present from process creation;
- the kernel, ETW, debuggers, crash reports, and the target agree on the launch
  command line;
- only the selected initial executable is modified;
- child-process command lines remain under the browser/embedder's normal
  control; and
- no injection, PEB mutation, or machine-wide registry interception is needed.

A wrapper can also preserve the original shortcut, protocol, file association,
or shell invocation and add an explicit diagnostics profile.

Limitations:

- it cannot retroactively change an already-running singleton instance;
- apps may forward a second launch to an existing process and exit; and
- some deployment systems or protected application launchers may reject
  wrappers.

### 2. Cooperative embedder APIs

If the application can be changed, use its supported startup APIs:

- Electron exposes `app.commandLine.appendSwitch()` and
  `appendArgument()` for Chromium's command line. This does not change
  `process.argv`.
- CEF exposes browser-process callbacks and
  `OnBeforeChildProcessLaunch()` for modifying a child command line before
  launch.
- WebView2 uses environment/options APIs and documented browser arguments when
  creating the environment.

Cooperation is the best way to distinguish browser-process options from
child-process options and to avoid fighting Chromium's own command-line
construction.

---

## Debugger and suspended-process techniques

### Editing the PEB at the initial breakpoint

A debugger that launches the target and stops before application/runtime
argument parsing can inspect:

```text
PEB -> ProcessParameters -> CommandLine
```

In principle it can overwrite the UTF-16 buffer and update the
`UNICODE_STRING.Length`, or allocate a replacement buffer and update the
pointer and maximum length.

This is useful for experiments but is not a supported production mechanism:

- Microsoft documents `RTL_USER_PROCESS_PARAMETERS` as an internal structure
  that may change;
- `GetCommandLineW` says applications must not modify the system-managed
  returned value;
- the CRT, framework, loader, injected code, or application may already have
  cached arguments;
- ETW/process-start auditing and the parent process retain the original command
  line; and
- changing the PEB alters what the process observes, not how Windows originally
  created it.

The replacement must fit the existing buffer unless the debugger correctly
allocates target memory and updates every relevant field. Incorrect lengths,
termination, pointer width, or cross-architecture handling can corrupt startup.

### `CREATE_SUSPENDED`

A custom launcher can create a process suspended, locate its process
parameters, mutate the command-line buffer, and resume the primary thread.
This is somewhat more deterministic than attaching later, but has the same
unsupported PEB-mutation and audit inconsistencies.

If the launcher already owns `CreateProcessW`, it should simply pass the desired
command line instead. Suspended PEB mutation is only justified for controlled
research into launchers that transform or hide arguments after creation.

---

## Image File Execution Options

An Image File Execution Options `Debugger` value causes `CreateProcess` to
insert the debugger in front of the original command line. The returned process
information and startup settings describe/apply to the debugger, not directly
to the eventual target.

IFEO is a poor default for Chromium instrumentation:

- it is machine-wide and normally requires administrative registry changes;
- matching is fundamentally executable-oriented, so same-executable Chromium
  browser and child launches are all intercepted;
- applications using multiple installations with the same image name can be
  affected unintentionally;
- crashes, recursive relaunch, startup UI, handle inheritance, and process
  identity become more complicated; and
- the mechanism is also used for persistence and security testing, so leaving
  an entry behind is risky.

A debugger proxy could inspect the original command line and modify launches
without `--type`, while passing child launches through. That is still brittle:
not every embedder uses the same role switches, single-process/test modes break
the assumption, singleton relaunches are different, and the proxy must avoid
IFEO recursion.

Path-filtering behavior sometimes used with IFEO is not a substitute for
browser-versus-child filtering when both roles use the same executable path.
Any implementation would need explicit version testing and cleanup guarantees.

---

## Compatibility shims, hooking, and injection

Other mechanisms can intercept or alter launch behavior, but are not suitable
as the normal Chromium Process Explorer design:

- Application Compatibility shim databases can redirect or patch process
  behavior but are deployment-heavy and version-sensitive.
- Detours-style `CreateProcess` hooks only affect launches made through hooked
  code and require code injection into the parent/embedder.
- `AppInit_DLLs`, global hooks, and similar injection mechanisms have security,
  architecture, signing, session, and compatibility restrictions.
- kernel process-notify callbacks and ETW/WMI process-start notifications are
  observation mechanisms, not supported command-line mutation APIs.

These approaches increase the risk of destabilizing the application and can
change the security properties the diagnostic run is supposed to observe.

---

## Chromium-specific filtering

If an experimental launch interceptor is required, evidence should be evaluated
in this order:

1. Explicitly selected executable path and expected product identity.
2. Absence/presence of Chromium child markers such as `--type`.
3. Known embedder-specific main/child executable layout.
4. Process generation and parent identity.
5. Singleton/relaunch state.

Do not assume that every process without `--type` is always the desired first
browser process. Node-mode Electron helpers, CEF relaunch behavior,
single-process modes, crash handlers, and vendor-specific launchers require
platform-specific handling.

---

## Product recommendation

Chromium Process Explorer should support:

1. **Preferred:** generate or execute an explicit diagnostic launch command.
2. **Preferred when controlled:** provide small cooperative helpers/examples
   for Electron, CEF, and WebView2.
3. **Optional research mode:** debugger/suspended-process experiments with
   strong warnings and no claim of transparency.
4. **Avoid as a product default:** persistent IFEO interception, compatibility
   shims, or injected launch hooks.

The tool should always show both the originally observed process-start command
line when available and the target-visible command line if an experiment
changes it.

---

## Sources

- Microsoft `CreateProcessW`:
  https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
- Microsoft `GetCommandLineW`:
  https://learn.microsoft.com/en-us/windows/win32/api/processenv/nf-processenv-getcommandlinew
- Microsoft `RTL_USER_PROCESS_PARAMETERS`:
  https://learn.microsoft.com/en-us/windows/win32/api/winternl/ns-winternl-rtl_user_process_parameters
- Raymond Chen, IFEO debugger command-line insertion:
  https://devblogs.microsoft.com/oldnewthing/20070702-00/?p=26193
- Electron `CommandLine` API:
  https://www.electronjs.org/docs/latest/api/command-line
- Electron command-line switches:
  https://www.electronjs.org/docs/latest/api/command-line-switches
- CEF browser-process handler and child launch callback:
  https://github.com/chromiumembedded/cef/blob/master/include/cef_browser_process_handler.h
- CEF application callback for command-line processing:
  https://github.com/chromiumembedded/cef/blob/master/include/cef_app.h
- WebView2 browser arguments:
  https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags
