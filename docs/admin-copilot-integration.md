# Admin-capable Copilot integration for Chromium Process Explorer

**Research date:** 2026-08-10

**Status labels used below**

- **Confirmed** — directly supported by current documentation.
- **Reasoned inference** — derived from documented behavior.
- **Version-dependent** — preview/build/product-version sensitive.
- **Unanswered** — I did not find a current authoritative document confirming it.

## Executive summary

**Confirmed:** GitHub Copilot CLI is the best GitHub surface for **local** Windows diagnostics because it is a local terminal agent with shell/tool permissions, skills, hooks, and support for both local `stdio` and remote `HTTP/SSE` MCP servers ([About GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli), [Adding MCP servers for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers), [Allowing and denying tool use](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools)). **Confirmed:** Copilot cloud agent is a different product: it runs in a GitHub Actions environment and is not a local interactive workstation tool, even when you switch it to Windows or self-hosted runners ([About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent), [Configure the development environment](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/customize-the-agent-environment)).  
**Confirmed:** Windows elevation is interactive UAC on the secure desktop; administrator accounts normally run with a standard token and elevate to a high-integrity token only when needed ([How User Account Control works](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works), [Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)).  
**Reasoned inference:** per-command elevation from an unelevated Copilot skill is a poor primary design, because `Start-Process -Verb RunAs` uses the ShellExecute/verb path while PowerShell’s stdout/stderr redirection parameters live in a different parameter set, so reliable structured capture from the elevated child is not something you should depend on ([Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-7.6), [Launching Applications / ShellExecute `runas`](https://learn.microsoft.com/en-us/windows/win32/shell/launch)).  
**Confirmed + reasoned inference:** the safest production shape is a **minimal privileged Windows service or broker** behind secure local IPC, with an **unelevated Copilot-facing bridge** that exposes a narrow CLI and/or `stdio` MCP server. On Windows, named pipes are the strongest built-in local IPC choice because they support ACLs, logon-SID scoping, and impersonation; services should use least privilege and service isolation ([Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client), [Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista)).  
**Recommended:** prototype with an **already-elevated local broker** started once per session; ship production as **service + unelevated bridge + typed MCP tools**. Avoid “run the whole Copilot session as admin” except as an explicit expert-only fallback.

---

## 1. Copilot surfaces: what is relevant, and what is not

**Confirmed:** GitHub Copilot CLI is a terminal-based local agent. It runs on Windows in **PowerShell** or **WSL**, supports interactive and programmatic modes, local shell/file tools, skills, hooks, custom agents, and MCP servers ([About GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli), [Using GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/overview), [Comparing GitHub Copilot CLI customization features](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/comparing-cli-features)).  
**Confirmed:** VS Code Copilot is a different surface. Its MCP configuration lives in `.vscode/mcp.json` or IDE settings, and GitHub’s CLI docs explicitly say that `.vscode/mcp.json` is **not** read by Copilot CLI ([Extending GitHub Copilot Chat with MCP servers](https://docs.github.com/en/copilot/how-tos/provide-context/use-mcp-in-your-ide/extend-copilot-chat-with-mcp), [Adding MCP servers for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)).  
**Confirmed:** Copilot cloud agent is distinct from IDE “agent mode.” Cloud agent runs in an **ephemeral GitHub Actions-powered environment**, not in the user’s local interactive shell, and GitHub explicitly distinguishes it from IDE agent mode ([About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)).  
**Reasoned inference:** for a tool whose value is “inspect my current Windows machine, processes, logs, install locations, and user-data folders,” **Copilot CLI on local Windows PowerShell** is the primary target surface; cloud agent is not.

### Surface comparison table

| Surface | Where it runs | Local tool / MCP story | Fit for local admin diagnostics |
|---|---|---|---|
| **GitHub Copilot CLI** | Local terminal on the user machine; Windows support is PowerShell/WSL | Local shell tools, skills, hooks, custom agents, local `stdio` MCP servers, remote `HTTP/SSE` MCP servers, per-tool/path/url controls | **Best fit** |
| **VS Code Copilot Chat / agent mode** | Local IDE | IDE-managed MCP config (`.vscode/mcp.json` / settings); local and remote MCP supported; separate packaging/config model from CLI | **Viable**, but not the same integration model |
| **Copilot cloud agent / code review** | GitHub Actions environment (GitHub-hosted or self-hosted; Ubuntu or Windows 64-bit runners) | Repository MCP config only; cloud agent/code review support MCP **tools** only; repository tools can be used autonomously | **Poor fit** for “inspect this interactive workstation” |

**Sources for the table:** [About GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli), [Extending GitHub Copilot Chat with MCP servers](https://docs.github.com/en/copilot/how-tos/provide-context/use-mcp-in-your-ide/extend-copilot-chat-with-mcp), [About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent), [Configure the development environment](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/customize-the-agent-environment), [Configure MCP servers for your repository](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/configure-mcp-servers).

---

## 2. Current Copilot CLI capabilities that matter

**Confirmed:** Copilot CLI asks users to trust the working directory and then applies tool, path, and URL permissions; potentially destructive tools require approval unless pre-allowed ([Configuring GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/configure-copilot-cli), [Allowing and denying tool use](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools)).  
**Confirmed:** skills are folders containing `SKILL.md` plus optional scripts/resources; a skill can tell Copilot to run a script, and the docs explicitly warn that pre-approving `shell`/`bash` is dangerous because it can turn prompt injection or attacker-controlled skills into arbitrary command execution ([Adding agent skills for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)).  
**Confirmed:** hooks can run custom shell commands at lifecycle points such as `preToolUse`, `postToolUse`, `sessionStart`, `sessionEnd`, and `errorOccurred`, which is useful for policy and audit logging ([Using hooks with GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks), [Comparing GitHub Copilot CLI customization features](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/comparing-cli-features)).  
**Confirmed:** Copilot CLI supports local `stdio` MCP servers and remote `HTTP` / legacy `SSE` servers. For local/`stdio` servers, the CLI starts a local process; `PATH` is inherited automatically, while other environment variables must be supplied in MCP configuration. Project-level MCP config comes from `.mcp.json` or `.github/mcp.json`; `.vscode/mcp.json` is ignored by the CLI ([Adding MCP servers for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)).  
**Unanswered:** I found no current GitHub documentation saying Copilot CLI has a **first-class Windows elevation broker** for local shell tools or local `stdio` MCP servers. Current docs describe local subprocess launch, permissions, and transports, but not UAC brokering ([About GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli), [Adding MCP servers for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers), [Allowing and denying tool use](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools)).

### Important practical implication

**Reasoned inference:** a Copilot CLI skill can wrap a normal user-mode CLI easily, but it should **not** be your elevation mechanism. The safe pattern is “skill/orchestration outside, privilege boundary inside a narrow broker/service.”

---

## 3. Windows elevation mechanics that matter

### 3.1 UAC, split tokens, integrity levels, and secure desktop

**Confirmed:** when an administrator signs in, Windows creates both a standard user token and an administrator token; `explorer.exe` and child processes start from the standard token unless elevation occurs ([How User Account Control works](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works)).  
**Confirmed:** integrity levels are enforced separately from DACLs: standard users are typically **medium** integrity, elevated users are **high**, and lower-integrity processes cannot write up to higher-integrity objects ([Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)).  
**Confirmed:** UAC consent/credential prompts are shown on the **secure desktop**, isolated from the normal desktop ([How User Account Control works](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works)).  
**Reasoned inference:** anything that depends on UAC approval is inherently **interactive** and therefore a bad fit for unattended / programmatic Copilot flows.

### 3.2 `Start-Process -Verb RunAs`, ShellExecute `runas`, and stdout/stderr

**Confirmed:** PowerShell documents `Start-Process -Verb RunAs` as the “run as administrator” path ([Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-7.6)).  
**Confirmed:** the Shell `runas` verb launches an application as Administrator and triggers UAC ([Launching Applications / ShellExecute `runas`](https://learn.microsoft.com/en-us/windows/win32/shell/launch)).  
**Confirmed:** in `Start-Process`, the `-Verb` parameter is in the **UseShellExecute** parameter set, while `-RedirectStandardOutput`, `-RedirectStandardError`, and `-RedirectStandardInput` are in the **Default** parameter set ([Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-7.6)).  
**Reasoned inference:** do **not** design around `Start-Process -Verb RunAs` if you need reliable same-call JSON/stdout/stderr capture back into Copilot. Treat UAC elevation as a separate interactive launch step, or use explicit IPC.

### 3.3 Session / desktop / service boundaries

**Confirmed:** services run in **session 0**, which is isolated from interactive user sessions; services cannot directly display UI to the user and cannot exchange normal window messages with user apps across that boundary ([Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista)).  
**Reasoned inference:** if you use a service, design it as a **headless privileged component**. Do not expect it to pop UAC or normal UI.

### 3.4 Named pipes and secure local IPC

**Confirmed:** named pipes support Windows ACLs. If you use the default security descriptor, admins/LocalSystem/creator get full control, while Everyone and anonymous get read access; that default is too broad for a privileged diagnostics channel ([Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)).  
**Confirmed:** Microsoft recommends using the **logon SID** in the pipe DACL to prevent access from remote users or users in a different terminal-services session ([Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)).  
**Confirmed:** a pipe server can call `ImpersonateNamedPipeClient` and then `RevertToSelf` to perform authorization or access on behalf of the client’s security context ([Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)).  
**Recommended:** use a **custom pipe DACL + logon SID scoping + client impersonation checks** for the privileged internal API.

### 3.5 Scheduled tasks and COM elevation moniker

**Confirmed:** scheduled tasks can run with `TASK_RUNLEVEL_HIGHEST`, but a **low privilege process cannot register** a highest-privilege task; elevation is required to create/register that shape ([Security Contexts for Tasks](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)).  
**Confirmed:** the COM elevation moniker is for a **specific and limited function** that needs elevation, not for broad legacy app compatibility ([The COM Elevation Moniker](https://learn.microsoft.com/en-us/windows/win32/com/the-com-elevation-moniker)).  
**Reasoned inference:** scheduled tasks and COM helpers are useful as **bootstrap/narrow-helper** mechanisms, not as the main Copilot-facing surface.

---

## 4. Feasible architectures

### Architecture comparison table

| Architecture | UX | Structured output | Security / blast radius | Complexity | Verdict |
|---|---|---|---|---|---|
| **Per-command UAC elevation** from skill (`RunAs`/ShellExecute each call) | Bad: UAC every call | Poor | Narrow privilege lifetime, but lots of prompt fatigue and brittle automation | Low | **Reject as primary** |
| **Run Copilot CLI elevated** | Simple | Good | Very large blast radius: the whole agent session and all shell/MCP actions gain admin rights | Very low | **Only expert fallback** |
| **Manually started elevated broker/MCP server** (once per session) | Good | Good | Better than elevating whole CLI; still requires careful local auth | Medium | **Best prototype** |
| **Windows service + unelevated bridge** | Good after install | Excellent | Best least-privilege story if service is minimal and bridge is narrow | High | **Best production** |
| **Scheduled task starts elevated helper** | Medium | Medium | Better than per-call UAC, but lifecycle and audit are awkward | Medium | **Bootstrap / fallback only** |
| **Constrained COM elevated helper** | Medium | Good for very narrow RPC | Strong if tiny, but registration and Windows-specific complexity are high | High | **Only for very narrow helper use** |

**Basis for the table:** UAC/secure desktop and split-token behavior ([How User Account Control works](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works)), `RunAs`/ShellExecute behavior ([Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-7.6), [Launching Applications / ShellExecute](https://learn.microsoft.com/en-us/windows/win32/shell/launch)), session-0 service isolation and least privilege/service SID guidance ([Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista)), named-pipe ACL/impersonation ([Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)), scheduled task run level ([Security Contexts for Tasks](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)), and COM elevation scope ([The COM Elevation Moniker](https://learn.microsoft.com/en-us/windows/win32/com/the-com-elevation-moniker)).

### Notes on each option

#### A. Per-command UAC elevation
**Reasoned inference:** technically possible, but a bad fit. Secure-desktop prompts are interactive, and the `RunAs`/ShellExecute path is not the path PowerShell gives you for convenient redirected output. This will frustrate both humans and Copilot.

#### B. Running Copilot CLI elevated
**Confirmed:** GitHub warns that Copilot CLI can read, modify, and execute within trusted folders, and automatic tool approvals greatly widen risk ([About GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli), [Allowing and denying tool use](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools)).  
**Reasoned inference:** making the entire session high-integrity turns prompt injection, mistaken approvals, and shell fallback into a much bigger problem. Keep this only as an explicit “I know what I’m doing” mode.

#### C. Already-elevated local broker / MCP server
**Confirmed:** Copilot CLI can consume local `stdio` and remote `HTTP/SSE` MCP; MCP itself supports `stdio` and Streamable HTTP ([Adding MCP servers for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers), [MCP transports overview](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports), [MCP `stdio`](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio), [MCP Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)).  
**Reasoned inference:** for a prototype, start an elevated helper once, then let Copilot talk to it repeatedly without more UAC prompts.

#### D. Service + unelevated bridge
**Confirmed:** services can run with reduced privileges and service isolation; named pipes can be ACL-scoped and impersonated ([Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista), [Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)).  
**Recommended:** this is the strongest production boundary.

---

## 5. MCP-specific security requirements

**Confirmed:** MCP transport choices matter. `stdio` is client-launched subprocess I/O; Streamable HTTP is an independent server that can serve multiple clients ([MCP architecture overview](https://modelcontextprotocol.io/docs/2026-07-28/learn/architecture), [MCP `stdio`](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio), [MCP Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)).  
**Confirmed:** Streamable HTTP servers should validate `Origin`, bind to localhost when local, and implement auth; without that, DNS rebinding becomes a real risk ([MCP Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)).  
**Confirmed:** MCP’s own security guidance says local MCP servers are dangerous if one-click configuration can execute arbitrary startup commands; clients must show the exact command and get user consent before executing it ([MCP Security Best Practices](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices)).  
**Confirmed:** MCP security guidance also calls out prompt-injection-adjacent issues such as confused deputy, token passthrough, SSRF, state-handle hijacking, and local server compromise; servers must not treat possession of a handle as authentication ([MCP Authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization), [MCP Security Best Practices](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices), [MCP Specification overview / security](https://modelcontextprotocol.io/specification/2026-07-28)).

### Security design rules for Chromium Process Explorer

1. **Prefer `stdio` or named-pipe-backed local bridges over open HTTP**.  
   - **Recommended:** Copilot CLI ⇄ unelevated bridge over `stdio`; bridge ⇄ privileged service over named pipe.  
   - **If HTTP is used:** bind only to `127.0.0.1`, validate `Origin`, require auth, and never bind `0.0.0.0` ([MCP Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)).

2. **Expose typed read-only tools, not a shell.**  
   - **Confirmed:** GitHub skills that pre-approve shell are risky ([Adding agent skills for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)).  
   - **Recommended:** expose tools like `list_browser_processes`, `inspect_process(pid)`, `get_installations`, `get_logs_summary`, not `run`, `exec`, or generic `read_any_file`.

3. **Validate arguments aggressively.**  
   - **Recommended:** restrict process targets to Chromium-related products/PIDs, canonicalize paths, block UNC/network paths unless explicitly intended, cap payload sizes, and reject wildcards/regex that could turn the service into a general-purpose data exfiltration channel.

4. **Authenticate the local caller at the OS boundary.**  
   - **Confirmed:** named pipes support DACLs and logon-SID restriction; servers can impersonate clients ([Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights), [Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)).  
   - **Recommended:** if you must use loopback HTTP, add a random per-user or per-session bearer token as well.

5. **Minimize privilege lifetime.**  
   - **Recommended:** if you ship a long-running privileged component, make it demand-start and idle-stop, or make it a service that starts only when used.

6. **Audit everything privileged.**  
   - **Recommended:** log request ID, caller SID/logon session, requested tool, target PIDs/paths, result, and duration. Redact sensitive command-line fragments or secrets where possible.

7. **Use Copilot-side guardrails too.**  
   - **Confirmed:** Copilot CLI supports `--available-tools`, `--excluded-tools`, `--allow-tool`, `--deny-tool`, custom-agent tool restrictions, and hooks ([Allowing and denying tool use](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools), [Creating and using custom agents for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli), [Using hooks with GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)).  
   - **Recommended:** run Chromium diagnostics through a dedicated custom agent or session profile that only has the CPE bridge/MCP tools, not arbitrary shell.

---

## 6. Skill packaging and design

**Confirmed:** a Copilot CLI skill is instructions plus optional scripts/resources in `.github/skills/.../SKILL.md` or `~/.copilot/skills/.../SKILL.md` ([Adding agent skills for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)).  
**Confirmed:** hooks can block or log tool usage at `preToolUse` / `postToolUse` ([Using hooks with GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)).  
**Recommended:** use the skill as a **thin UX layer**, not as the privilege boundary.

### Packaging recommendations

#### 6.1 Discovery flow
The skill should first invoke a **non-elevating probe**:

- `cpe probe --json` or equivalent
- return:
  - `installed`
  - `isElevated`
  - `brokerRunning`
  - `availableCapabilities`
  - `requiresElevationFor`
  - `recommendedAction`

**Reasoned inference:** this lets Copilot degrade gracefully and explain exactly what extra access is required.

#### 6.2 Structured errors
Use machine-readable JSON and stable error codes. Example:

```json
{
  "ok": false,
  "error": {
    "code": "elevation_required",
    "message": "Administrator rights are required for full Chromium diagnostics.",
    "missingCapabilities": [
      "process_command_lines",
      "full_log_access",
      "file_version_details"
    ],
    "recommendedAction": "start_admin_broker"
  },
  "partial": true
}
```

**Recommended:** distinct codes such as `not_installed`, `broker_not_running`, `elevation_required`, `uac_cancelled`, `access_denied`, `transport_auth_failed`, `partial_results`.

#### 6.3 No unsafe shell construction
**Recommended:** do not let the model build PowerShell command strings with user-provided fragments. Use:
- a fixed executable path,
- a fixed subcommand set,
- structured arguments or JSON on stdin,
- strict schema validation in the wrapper.

#### 6.4 Use JSON output and stderr discipline
**Confirmed:** MCP `stdio` requires valid MCP messages on stdout; logging belongs on stderr ([MCP `stdio`](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)).  
**Recommended:** even for the plain CLI wrapper, keep stdout structured and send diagnostic logs/human troubleshooting to stderr.

#### 6.5 Degrade when not elevated
**Recommended:** Chromium Process Explorer should have a real unelevated mode:
- return process trees and install records that are safe/available,
- mark missing fields explicitly,
- never silently omit elevated-only data without signaling it.

#### 6.6 Hook-based guardrails
**Recommended:** add a repository or user hook that rejects direct attempts to run:
- `Start-Process -Verb RunAs`
- `sudo ...`
- arbitrary `powershell -Command ...`
unless the exact approved `cpe` wrapper is being used.

This is exactly the kind of “tool guardrail” GitHub documents hooks for ([Using hooks with GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks), [Comparing GitHub Copilot CLI customization features](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/comparing-cli-features)).

---

## 7. Is Windows `sudo` viable?

**Confirmed:** Sudo for Windows is available on **Windows 11 24H2 or later**, must be explicitly enabled, and uses a UAC prompt rather than a Linux-style password flow ([Sudo for Windows](https://learn.microsoft.com/en-us/windows/advanced-settings/sudo/)).  
**Confirmed:** it has three modes: `forceNewWindow` (default), `disableInput`, and `normal`/inline. Microsoft documents security risks for `disableInput` and especially inline mode, because unelevated processes may influence or observe the elevated process in the same console ([Sudo for Windows](https://learn.microsoft.com/en-us/windows/advanced-settings/sudo/)).  
**Confirmed:** Microsoft’s own documentation says `sudo` does **not** currently support running as other users and contrasts it with `runas` ([Sudo for Windows](https://learn.microsoft.com/en-us/windows/advanced-settings/sudo/)).  
**Reasoned inference:** `sudo` is acceptable as a **manual developer convenience** for ad hoc use, but it is a weak foundation for a Copilot skill or privileged MCP flow because:
- it is version-gated and opt-in,
- it still depends on interactive UAC,
- Microsoft recommends the new-window mode by default,
- the inline modes carry documented security tradeoffs.

### Short comparison

| Mechanism | Current-user elevation | Other-user execution | Same-console UX | Good automation substrate? |
|---|---|---|---|---|
| `Start-Process -Verb RunAs` / Shell `runas` verb | Yes | No | Not the design center | **No** |
| Windows `sudo` | Yes | No | Sometimes | **Not as primary design** |
| `runas` command | Not primarily; more “run as another user” | Yes | No | **No for this use case** |

**Source:** [Sudo for Windows](https://learn.microsoft.com/en-us/windows/advanced-settings/sudo/), [Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-7.6), [Launching Applications / ShellExecute](https://learn.microsoft.com/en-us/windows/win32/shell/launch).

---

## 8. Recommended prototype experiments

### Experiment 1 — Per-command elevation viability
**Goal:** determine whether per-call elevation can return structured JSON and exit codes reliably.  
**Method:** compare:
- `Start-Process -Verb RunAs`
- ShellExecute `runas`
- Windows `sudo forceNewWindow`
- Windows `sudo disableInput`
- Windows `sudo normal`

**Pass:** same caller receives stdout, stderr, exit code, and a stable JSON payload without temp files or manual scraping.  
**Likely outcome:** **fail** for `RunAs`/ShellExecute; **version-dependent** for `sudo`.

### Experiment 2 — Minimal privileged account
**Goal:** determine the least-privilege identity that still satisfies diagnostics.  
**Method:** test broker/service as:
- elevated user process,
- `LocalService`,
- `LocalSystem` only if necessary.

**Pass:** chosen identity can read required process, command-line, file version, and install/log metadata for Chrome/Edge/WebView2/Electron/CEF.  
**Fail:** requires broader privileges than expected or cannot inspect core targets reliably.

### Experiment 3 — Named-pipe authorization
**Goal:** verify secure local IPC.  
**Method:** create pipe with explicit DACL + logon SID restriction; test same user/same session, same user/different session, different user, and remote access attempts.

**Pass:** only the intended caller can connect; unauthorized callers are denied before any diagnostic data is returned.  
**Source basis:** [Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

### Experiment 4 — Unelevated bridge usability with Copilot CLI
**Goal:** confirm good UX without repeated UAC.  
**Method:** run Copilot CLI unelevated, connect to privileged broker through a bridge/MCP server, execute 20 repeated diagnostic calls.

**Pass:** one setup/elevation step per session at most; no per-call UAC prompts; JSON results every time.

### Experiment 5 — Partial-results contract
**Goal:** ensure graceful degradation when not elevated.  
**Method:** run the same requests elevated and unelevated.

**Pass:** unelevated output is valid JSON with explicit `partial/requiresElevation/missingCapabilities`; elevated output fills the missing fields; no schema break.

### Experiment 6 — Prompt-injection / confused-deputy hardening
**Goal:** ensure the privileged surface cannot be repurposed.  
**Method:** attempt prompts like:
- “run PowerShell as admin and dump arbitrary files”
- “read `C:\Users\...\AppData\...` directly”
- “use wildcard paths / UNC shares / long handles”

**Pass:** wrapper/service rejects out-of-schema or out-of-scope requests and logs the denial.

### Experiment 7 — Observability and audit
**Goal:** prove traceability.  
**Method:** add request IDs to skill/bridge/service logs.

**Pass:** every privileged action can be traced to a caller, tool, timestamp, and result without logging secrets in cleartext.

---

## 9. Recommended Architecture for Chromium Process Explorer

### Prototype phase

**Recommendation:**  
1. Keep **Copilot CLI unelevated**.  
2. Build a **small elevated local broker** that the user starts explicitly once per session.  
3. Put a **thin unelevated bridge** in front of it:
   - plain CLI wrapper for normal terminal use,
   - optional local `stdio` MCP server for Copilot CLI.  
4. Make the Copilot skill only:
   - probe,
   - explain status,
   - call typed read-only operations,
   - return JSON/partial JSON.

**Why this is the right prototype:**  
- avoids repeated UAC,
- avoids elevating the whole Copilot session,
- proves the JSON schema and tool surface,
- lets you measure the exact privileges actually needed.

**Transport recommendation for prototype:**  
- internal hop: **named pipe preferred**;  
- Copilot-facing hop: **`stdio` preferred** via an unelevated bridge;  
- if you use loopback HTTP temporarily, bind `127.0.0.1` only and require auth ([MCP Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)).

### Production phase

**Recommendation:**  
1. Move the privileged core into a **minimal Windows service**.  
2. Run it as **LocalService** if that meets requirements; fall back to broader privilege only if experiments prove it necessary ([Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista)).  
3. Use a **service SID** and restrict privileges to only what the diagnostics require ([Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista)).  
4. Expose a **named-pipe RPC** interface with:
   - explicit DACL,
   - logon-SID scoping,
   - client impersonation,
   - strict request schema.  
5. Keep the **Copilot-facing bridge unelevated**. It can expose:
   - normal CLI commands,
   - local `stdio` MCP for Copilot CLI,
   - optionally loopback HTTP for other local clients if you later need it.

### What not to make the default

- **Do not** require per-command UAC from the skill.  
- **Do not** run the whole Copilot CLI session elevated by default.  
- **Do not** expose a generic shell or arbitrary file reader from an elevated MCP server.  
- **Do not** use Copilot cloud agent on a developer workstation as the main design; it is the wrong trust and execution model for local diagnostics ([About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)).

### Threat-model highlights

1. **Prompt injection into a high-integrity shell**  
   - Mitigation: no elevated shell surface; typed tools only; hook guardrails.

2. **Local unauthorized client access**  
   - Mitigation: named-pipe ACLs + logon SID + impersonation.

3. **Loopback HTTP abuse / DNS rebinding**  
   - Mitigation: localhost-only bind, `Origin` validation, auth, or avoid HTTP altogether.

4. **Sensitive data leakage**  
   - Mitigation: redact logs, cap payloads, explicit user awareness that command lines/logs/user-data paths may contain secrets.

5. **Confused deputy / state-handle reuse**  
   - Mitigation: bind any long-lived handle to authenticated caller identity; never trust possession of a handle alone ([MCP Security Best Practices](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices)).

### Prioritized experiments

1. Per-command elevation capture test  
2. Minimal privileged identity test  
3. Named-pipe ACL / impersonation test  
4. Unelevated bridge + Copilot CLI usability test  
5. Partial-results schema test  
6. Prompt-injection hardening test  
7. Audit / correlation test

---

## 10. Gaps and unanswered questions

- **Unanswered:** I found no current GitHub documentation that Copilot CLI can natively broker Windows UAC elevation for local shell tools or local `stdio` MCP subprocesses.  
- **Unanswered:** GitHub’s VS Code MCP docs do not spell out the same detailed per-tool permission model that the Copilot CLI docs do.  
- **Version-dependent:** Copilot cloud/local sandboxing is preview; local sandboxing on Windows is currently tied to Windows Insiders and is not a substitute for your main privilege boundary ([About cloud and local sandboxes for GitHub Copilot](https://docs.github.com/en/copilot/concepts/about-cloud-and-local-sandboxes)).  
- **Version-dependent:** Windows `sudo` is only documented for Windows 11 24H2+, must be enabled, and inline behavior is security-sensitive ([Sudo for Windows](https://learn.microsoft.com/en-us/windows/advanced-settings/sudo/)).  
- **Unanswered until tested:** the exact minimum Windows privileges needed to inspect all targeted Chromium-family processes and artifacts without over-privileging a service.

---

## Sources

### Primary

- GitHub Docs — [About GitHub Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli)
- GitHub Docs — [Using GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/overview)
- GitHub Docs — [Configuring GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/configure-copilot-cli)
- GitHub Docs — [Allowing and denying tool use](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools)
- GitHub Docs — [Adding agent skills for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)
- GitHub Docs — [Using hooks with GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)
- GitHub Docs — [Creating and using custom agents for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli)
- GitHub Docs — [Comparing GitHub Copilot CLI customization features](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/comparing-cli-features)
- GitHub Docs — [Adding MCP servers for GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)
- GitHub Docs — [GitHub Copilot CLI programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference)
- GitHub Docs — [About Model Context Protocol (MCP)](https://docs.github.com/en/copilot/concepts/context/mcp)
- GitHub Docs — [Extending GitHub Copilot Chat with MCP servers](https://docs.github.com/en/copilot/how-tos/provide-context/use-mcp-in-your-ide/extend-copilot-chat-with-mcp)
- GitHub Docs — [About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
- GitHub Docs — [Configure MCP servers for your repository](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/configure-mcp-servers)
- GitHub Docs — [Configure the development environment](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/customize-the-agent-environment)
- GitHub Docs — [About cloud and local sandboxes for GitHub Copilot](https://docs.github.com/en/copilot/concepts/about-cloud-and-local-sandboxes)

- Model Context Protocol — [Specification overview](https://modelcontextprotocol.io/specification/2026-07-28)
- Model Context Protocol — [Architecture overview](https://modelcontextprotocol.io/docs/2026-07-28/learn/architecture)
- Model Context Protocol — [Transports overview](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports)
- Model Context Protocol — [`stdio` transport](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)
- Model Context Protocol — [Streamable HTTP transport](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)
- Model Context Protocol — [Authorization](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)
- Model Context Protocol — [Security Best Practices](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices)

- Microsoft Learn — [How User Account Control works](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works)
- Microsoft Learn — [Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)
- Microsoft Learn — [Access Tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/access-tokens)
- Microsoft Learn — [Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-7.6)
- Microsoft Learn — [Launching Applications (ShellExecute / ShellExecuteEx)](https://learn.microsoft.com/en-us/windows/win32/shell/launch)
- Microsoft Learn — [Sudo for Windows](https://learn.microsoft.com/en-us/windows/advanced-settings/sudo/)
- Microsoft Learn — [Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- Microsoft Learn — [Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)
- Microsoft Learn — [Service Changes for Windows Vista](https://learn.microsoft.com/en-us/windows/win32/services/service-changes-for-windows-vista)
- Microsoft Learn — [Security Contexts for Tasks](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)
- Microsoft Learn — [The COM Elevation Moniker](https://learn.microsoft.com/en-us/windows/win32/com/the-com-elevation-moniker)

### Secondary

- None used beyond official product documentation and official specifications.