---
name: chromium-process-explorer
description: Inspect local Chromium, Chrome, Edge, WebView2, Electron, and CEF processes, installations, CDP, logs, and crash artifacts through the typed Chromium Process Explorer MCP tools. Use for local Chromium diagnostics.
---

Use only the `cpe_*` MCP tools supplied by the
`chromium-process-explorer` server. Do not construct elevated shell commands,
read arbitrary files, or substitute generic process/file tools.

1. Call `cpe_probe` first.
2. If the broker is available, choose the narrowest tool:
   - `cpe_process_details` for process identity, role, command-line switches,
     architecture, elevation, package, and version metadata.
   - `cpe_installations` for browsers, runtimes, app roots, versions, channels,
     package identity, and install provenance.
   - `cpe_diagnostics` for passive redacted log, Crashpad, WER, netlog, trace,
     and risky-switch metadata.
   - `cpe_cdp` for configured and validated DevTools transports.
3. Treat `partial: true` and per-item issues as useful partial results.
4. Never claim a heuristic relationship is authoritative.
5. Never request sensitive values through MCP. Ask the user to run the normal
   CLI with `--include-sensitive` when they explicitly need local path or
   command-line values.

If `cpe_probe` returns `broker_not_running`, tell the user to build the
solution and manually start the fixed broker executable once from an
interactive PowerShell prompt:

```powershell
Start-Process .\src\ChromiumProcessExplorer.Broker\bin\Debug\net9.0-windows\cpe-broker.exe -Verb RunAs
```

Do not run that elevation command automatically.
