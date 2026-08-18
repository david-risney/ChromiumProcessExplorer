# Privileged broker prototype

Chromium Process Explorer keeps Copilot CLI and the MCP bridge unelevated. A
user explicitly starts `cpe-broker.exe` elevated once, then fixed read-only
operations cross a local named pipe.

## Boundary and threat model

- The pipe uses .NET's `PipeOptions.CurrentUserOnly`, which creates a
  current-user-only local pipe. Every accepted connection is then impersonated
  and its user SID and token authentication LUID (Windows logon-session ID)
  must exactly match the broker token.
- Each connection carries one 4 MiB maximum length-prefixed JSON request and
  one response. Requests have a protocol version, GUID request ID, approved
  operation name, strict object arguments, and a 90-second deadline.
- There are no shell, command execution, arbitrary path, arbitrary file-read,
  wildcard, URL-fetch, or long-lived handle operations.
- The MCP bridge exposes only `probe`, redacted process details, installations,
  passive diagnostics, and CDP discovery. The broker protocol has no
  sensitive-output option; sensitive exports require explicitly running the
  normal CLI elevated with `--include-sensitive`.
- The current-user pipe DACL rejects other users. A different logon session can
  submit at most one bounded frame and is rejected before argument
  deserialization or operation execution. A stale/mismatched response ID is
  rejected by the client. Each new call opens a new connection, so broker
  restart does not leave reusable handles.
- Audit JSONL records timestamp, request ID, operation, caller SID/logon session,
  status, and duration. It never records request arguments or result payloads.

The prototype still runs as the elevated interactive user. Production should
move the same strict RPC surface into a demand-start, least-privilege Windows
service with an explicit service SID after privilege experiments establish
the minimum required account and privileges.

## Build and start

```powershell
dotnet restore ChromiumProcessExplorer.sln
dotnet build ChromiumProcessExplorer.sln --configuration Debug --no-restore
Start-Process .\src\ChromiumProcessExplorer.Broker\bin\Debug\net9.0-windows\cpe-broker.exe -Verb RunAs
```

The broker deliberately uses an `asInvoker` manifest: elevation is an explicit
launcher/user decision rather than an automatic prompt whenever the executable
is inspected or started accidentally.

For local development only, `cpe-broker --allow-unelevated` exercises transport
and partial-result behavior without granting additional access.

## CLI bridge

```powershell
dotnet run --project src\ChromiumProcessExplorer.Cli -- broker-probe --json
dotnet run --project src\ChromiumProcessExplorer.Cli -- broker-process-details --pid 1234 --json
dotnet run --project src\ChromiumProcessExplorer.Cli -- broker-installations --json
dotnet run --project src\ChromiumProcessExplorer.Cli -- broker-diagnostics --json
dotnet run --project src\ChromiumProcessExplorer.Cli -- broker-cdp --json
```

Stable error codes include `broker_not_running`, `broker_timeout`,
`unsupported_version`, `malformed_request`, `invalid_operation`,
`invalid_arguments`, `access_denied`, and `stale_response`.

## Copilot CLI MCP and skill

The repository commits `.github\mcp.json` and
`.github\skills\chromium-process-explorer\SKILL.md`. After trusting the
repository and building Debug, start the broker manually, then reload:

```text
/mcp show chromium-process-explorer
/skills reload
/skills info chromium-process-explorer
```

The project MCP command uses `dotnet run --no-build`; change the configuration
or build configuration if you use a non-Debug output.

## Stop and uninstall

Close the broker console or press Ctrl+C. No service, scheduled task, registry
entry, firewall rule, HTTP listener, credential, or machine-wide MCP
configuration is installed. To remove the integration, delete the project
MCP/skill files or disable the server with `/mcp disable
chromium-process-explorer`.

Audit records are written to
`%LOCALAPPDATA%\ChromiumProcessExplorer\broker-audit.jsonl` and may be removed
after the broker has stopped.
