# CDP transports and Windows accessibility

## Summary

Chromium Process Explorer should treat **CDP availability**, **DevTools UI
visibility**, and **Windows accessibility data** as three different evidence
sources:

- A validated remote-debugging port provides an attachable CDP endpoint.
- `--remote-debugging-pipe` provides a private, launch-time CDP transport owned
  by the process that launched Chromium.
- WebView2, Electron, CEF, extensions, and automation frameworks can provide
  cooperative in-process or brokered CDP access without exposing a port.
- Windows UI Automation, MSAA, and IAccessible2 can reveal useful visible-UI
  and document evidence, but do not provide CDP access or a reliable
  renderer-PID-to-origin mapping.

An arbitrary port or named pipe owned by Chromium must not be labeled CDP
without protocol-specific evidence.

---

## CDP access paths

### Remote-debugging TCP port

`--remote-debugging-port=<port>` starts an HTTP/WebSocket DevTools server.
Useful discovery endpoints include:

- `/json/version` for browser metadata and the browser
  `webSocketDebuggerUrl`;
- `/json/list` for debuggable targets; and
- `/json/protocol` for the protocol schema.

Chromium Process Explorer can safely classify a listener as CDP only after a
bounded loopback HTTP request validates the response shape. Process ownership,
a command-line port value, or a listening socket alone is insufficient because
a Chromium application can run unrelated application, media, WebRTC, QUIC, or
test servers.

When port `0` selects an ephemeral port, Chromium can write
`DevToolsActivePort` beneath the applicable profile/cache directory. Chromium's
implementation writes the selected port and browser WebSocket path. CEF also
documents this breadcrumb when an appropriate cache path exists.

Starting in Chrome 136, Google Chrome ignores both remote-debugging switches
against the default Chrome data directory. A non-default `--user-data-dir` is
required. Chrome for Testing is the recommended automation target.

### Remote-debugging pipe

`--remote-debugging-pipe` carries the same browser-target CDP messages through
two inherited, one-way pipes:

```text
controller writes -> Chromium file descriptor 3
controller reads  <- Chromium file descriptor 4
```

The default protocol mode sends UTF-8 JSON messages terminated by a NUL byte.
Current Chromium source also recognizes
`--remote-debugging-pipe=cbor`, using length-bearing CBOR envelopes. The CBOR
mode and the Windows implementation details should be treated as
version-sensitive.

On Windows, Chromium converts the descriptors to native handles. Current source
also supports an internal companion switch,
`--remote-debugging-io-pipes=<input-handle>,<output-handle>`, that adopts two
explicitly inherited pipe handles. A launcher should use restricted handle
inheritance rather than making unrelated handles inheritable.

The pipe handler initially attaches to the browser target. The controller uses
the CDP `Target` domain to discover and attach to pages, frames, workers, and
other targets, commonly using flattened sessions and a `sessionId`.

Pipe mode has no HTTP discovery endpoints, WebSocket URL, listening socket, or
`DevToolsActivePort`. It normally cannot be attached to after Chromium has
started because the launcher already owns the opposite pipe ends.

### Can another process join an existing debugging pipe?

Not safely.

Windows anonymous pipes are implemented using named-pipe objects with unique
names, so handle enumeration may reveal object names, directions, and endpoint
processes. That makes passive correlation feasible:

```text
Chromium PID 1234
  remote debugging: pipe
  input/output controller: PID 5678 playwright.exe
```

It does not make the protocol multi-client:

- the pipe instance is already connected to its controller;
- a duplicated read handle would compete for and steal responses/events;
- a duplicated write handle would inject messages into the controller's CDP
  session;
- request IDs, target sessions, and protocol state belong to the existing
  controller.

Chromium Process Explorer should therefore inspect debugging pipes as evidence,
never read from or write to an occupied session. Safe reuse requires the
controller to expose an intentional proxy or mirroring interface.

### Cooperative and brokered CDP

Other supported access paths include:

- WebView2 `CallDevToolsProtocolMethodAsync`;
- Electron `webContents.debugger`;
- CEF DevTools message observer APIs;
- the privileged Chrome extension `chrome.debugger` API;
- ChromeDriver, Playwright, Puppeteer, and similar automation brokers; and
- ADB forwarding for Android Chromium.

ChromeDriver normally listens on its own WebDriver port and separately connects
to Chromium through CDP. Chromium Process Explorer must not classify the
ChromeDriver listener itself as the browser's CDP port.

Stock desktop Chromium does not expose a general API that lets an arbitrary
external process enable or join CDP after startup. Access must have been enabled
at launch, supplied by the embedder, granted through a privileged extension, or
proxied by the existing automation controller.

---

## Windows accessibility

### What it can expose

UI Automation, MSAA, and IAccessible2 can provide useful evidence such as:

- browser windows, tabs, selected state, focus, and bounding rectangles;
- address-bar and other browser-chrome values when exposed by the browser;
- document roles, accessible names, headings, text, forms, controls, links,
  and link destinations;
- native window handles, class/framework information, and provider process
  IDs; and
- enough visible structure to correlate a top-level browser HWND with the
  currently selected document.

Accessibility can therefore support optional browser-window diagnostics and
visible-page evidence.

### Why it does not identify the renderer PID

Chromium constructs web accessibility data in renderers, sends serialized tree
updates to the browser, and exposes the cached browser-side tree through native
platform accessibility APIs. On Windows, the native provider is implemented in
the browser-side accessibility layer.

Consequences:

- `UIA_ProcessIdPropertyId` identifies the process hosting the accessibility
  provider. It is not a Chromium renderer-process identifier.
- UIA runtime IDs, automation IDs, accessibility node IDs, and IAccessible2
  unique IDs are not CDP target IDs or OS renderer PIDs.
- the active document can span multiple renderers because of out-of-process
  iframes, workers, prerendering, and other Chromium process-model features;
- background tabs, workers, windowless content, and disabled/incomplete
  accessibility trees may not be visible; and
- querying accessibility can activate or expand Chromium accessibility work,
  so it is not always a zero-cost passive observation.

Accessibility data may provide a URL-like value from browser chrome, links, or
implementation-specific document attributes, but that value belongs to a
window/document observation. It must not be presented as proof that a
particular renderer PID owns the origin.

### Accessibility is not a CDP transport

UI Automation invokes accessibility control patterns and reads provider
properties. It does not expose CDP framing, target sessions, browser commands,
or a route to a DevTools agent.

Driving the browser UI to open DevTools or copy an address through automation
would be brittle UI automation, not protocol attachment.

---

## Recommended product model

Represent these observations independently:

| Evidence | Example state | Authority |
| --- | --- | --- |
| CDP configuration | `--remote-debugging-port=0` | configured, not yet validated |
| CDP endpoint | validated `/json/version` | attachable endpoint |
| Debugging pipe | browser/controller pipe endpoints | private occupied transport |
| DevTools UI | DevTools window/target evidence | UI state, not endpoint state |
| Accessibility window | selected tab/title/address evidence | visible browser UI |
| Accessibility document | roles/text/links | page semantics, not renderer ownership |

All network and handle probes must have finite deadlines and preserve partial
errors. CDP attachment and accessibility-tree activation should be opt-in when
they could expose sensitive page state or change application behavior.

---

## Sources

- Chromium DevTools pipe implementation:
  https://chromium.googlesource.com/chromium/src/+/main/content/browser/devtools/devtools_pipe_handler.cc
- Chromium DevTools agent host descriptors and browser target:
  https://chromium.googlesource.com/chromium/src/+/main/content/public/browser/devtools_agent_host.h
- Chromium Windows pipe-handle adoption:
  https://chromium.googlesource.com/chromium/src/+/main/content/browser/devtools/devtools_agent_host_impl.cc
- Chromium HTTP DevTools handler:
  https://chromium.googlesource.com/chromium/src/+/main/content/browser/devtools/devtools_http_handler.cc
- Chrome remote-debugging security change:
  https://developer.chrome.com/blog/remote-debugging-port
- Chrome DevTools Protocol:
  https://chromedevtools.github.io/devtools-protocol/
- Microsoft `CreatePipe`:
  https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createpipe
- Chromium accessibility overview:
  https://chromium.googlesource.com/chromium/src/+/HEAD/docs/accessibility/overview.md
- Chromium UI Automation architecture:
  https://chromium.googlesource.com/chromium/src/+/HEAD/docs/accessibility/browser/uiautomation.md
- Chromium accessibility inspection tools:
  https://chromium.googlesource.com/chromium/src/+/HEAD/tools/accessibility/inspect/README.md
- Microsoft UI Automation property identifiers:
  https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-automation-element-propids
- WebView2 CDP:
  https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/chromium-devtools-protocol
- Electron debugger API:
  https://www.electronjs.org/docs/latest/api/debugger
- Chrome extension debugger API:
  https://developer.chrome.com/docs/extensions/reference/api/debugger
