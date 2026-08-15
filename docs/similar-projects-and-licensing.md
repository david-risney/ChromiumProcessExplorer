# Similar projects, reusable ideas, and licensing

## Purpose

Chromium Process Explorer overlaps with several mature Windows diagnostic
projects. They are useful for architecture, API behavior, testing, and UX
ideas, but source reuse must be evaluated per repository, per file, and per
dependency.

This note is not legal advice. Before copying code:

1. confirm the exact license at the commit being used;
2. inspect file-level copyright and license headers;
3. preserve required notices and attribution;
4. check whether copied code pulls in differently licensed dependencies; and
5. make sure Chromium Process Explorer has an explicit project license
   compatible with the imported code.

Chromium Process Explorer currently has no root `LICENSE` file. Until a project
license is selected, prefer clean-room implementation from Microsoft API
documentation and use other projects as behavioral prior art rather than
copying source.

---

## Project matrix

| Project | Relevant ideas | Observed license | Reuse guidance |
| --- | --- | --- | --- |
| WebView2Utilities | WebView2 host/runtime discovery, process parents, HWND topology, loaded modules, runtime/install/version/channel reporting, report UX | MIT | Strongest directly relevant reusable reference; preserve the MIT notice for copied portions |
| PowerToys File Locksmith | `SystemExtendedHandleInformation`, handle duplication, object type/name queries, kernel-path matching, loaded-module enumeration, elevation UX, native core plus WinUI frontend | MIT | Source can be reused under MIT terms, but its forced-thread-termination watchdog should not replace this project's safer helper-process isolation |
| System Informer | Mature NT process/handle/pipe primitives, named-pipe security and endpoint operations, process metadata, native API definitions | MIT in the current repository | Useful native reference; verify current file headers and avoid assuming older Process Hacker licensing applies |
| psutil | Windows process inspection, error normalization, timeout/hang discussions and tests | BSD-3-Clause | Permissive with notice/non-endorsement requirements; mainly useful for portability and failure-behavior patterns |
| VolatileDataCollector `HND.c` | System handle snapshot, duplication, `NtQueryObject` on a worker thread with a timeout | GPL-3.0 | Use as conceptual prior art only unless the consuming project intentionally adopts compatible GPL obligations |
| Matrix86 `enumerateHandles` | Small example explicitly moving `NtQueryObject(ObjectNameInformation)` to a timeout-controlled thread | No repository license detected | Do not copy source; no license means copyright permission is not granted by default |
| Sysinternals Handle / Process Explorer / PipeList | Long-standing product behavior for handles, processes, DLLs, and named pipes | Closed source / Microsoft Sysinternals terms | Behavioral and UX inspiration only; do not copy code or reverse engineer implementation |

License observations were made against the repositories linked below and can
change. Pin a commit when source is actually reused.

---

## WebView2Utilities

Repository: https://github.com/david-risney/WebView2Utilities

License: MIT

High-value prior art:

- snapshot and parent-process association;
- WebView2 host fingerprints such as `WebView2Loader.dll`;
- host HWND to `CrossProcessChildHWND` traversal;
- Mojo-name-assisted WebView2 discovery;
- runtime and install channel/version reporting;
- user-data, logging, trace, and report-bundle UX; and
- explicit display of probable or unknown associations.

Chromium Process Explorer should generalize these ideas into Core providers and
typed evidence rather than copy a WebView2-only object model. The detailed
analysis is in
[webview2-process-association.md](webview2-process-association.md).

Because the author and license are explicit, selected utility code can be
adapted if the MIT copyright and permission notice are retained. Dependencies
and generated/deployed binaries in that repository still need separate review.

---

## PowerToys File Locksmith

Repository:
https://github.com/microsoft/PowerToys/tree/main/src/modules/FileLocksmith

License: MIT

File Locksmith is especially relevant to the Mojo handle scanner:

- `NtdllExtensions.cpp` obtains
  `SystemExtendedHandleInformation`;
- it caches process handles opened for `PROCESS_DUP_HANDLE`;
- it duplicates foreign handles;
- it queries object type and object name information;
- it translates kernel file names and matches selected files/directories;
- it also enumerates loaded process modules; and
- the native scanning library is separated from the WinUI frontend.

Its source comments acknowledge that `NtQueryObject` and `GetFileType` can hang.
The current implementation offloads progress to a thread, checks progress every
200 ms, and calls `TerminateThread` when it appears stuck. It explicitly notes
that this is unsafe and can leak resources.

Chromium Process Explorer should borrow the staged-scanner and UI separation,
not the unsafe termination mechanism. The existing persistent helper-process
pool is a stronger boundary:

- one hung native call sacrifices one helper process;
- the OS reliably reclaims the helper's handles and memory;
- the main .NET process remains consistent; and
- finite deadlines, stage telemetry, and worker replacement are externally
  observable.

File Locksmith filters for disk-file matching, whereas Chromium Process
Explorer intentionally examines named-pipe file objects and resolves pipe
server/client PIDs. Its code is therefore a close architectural example, not a
drop-in Mojo implementation.

Useful files:

- `doc/devdocs/modules/filelocksmith.md`
- `FileLocksmithLibInterop/FileLocksmith.cpp`
- `FileLocksmithLibInterop/NtdllExtensions.cpp`
- `FileLocksmithLibInterop/NativeMethods.cpp`
- `FileLocksmithLibInterop/ProcessResult.cpp`

---

## System Informer

Repository: https://github.com/winsiderss/systeminformer

License: MIT in the current repository

System Informer is the broadest native Windows reference in this set. Relevant
areas include:

- system process and handle snapshots;
- object type/name queries and native structure definitions;
- named-pipe creation, connection, security descriptors, protected prefixes,
  peeking, and information queries;
- process, token, module, window, and service metadata; and
- mature partial-failure behavior across Windows versions.

`phlib/nativepipe.c` is useful for understanding NPFS operations and secure
named-pipe construction. Other `phlib` and System Informer files demonstrate
how a production diagnostic tool wraps NTSTATUS, buffer growth, native object
lifetimes, and version differences.

Do not conflate the current System Informer repository with every historical
Process Hacker release. Confirm the license and headers at the exact commit and
file used.

---

## Timeout and hang prior art

The following projects corroborate that arbitrary foreign file-handle name
queries need isolation:

### VolatileDataCollector

Repository: https://github.com/gtworek/VolatileDataCollector

License: GPL-3.0

`HND.c` snapshots `SystemExtendedHandleInformation`, duplicates handles, starts
a worker thread for `NtQueryObject(ObjectNameInformation)`, and waits with a
timeout. Timed-out threads/allocations may be abandoned. This is useful evidence
for the hang condition, but GPL source should not be copied into a
non-GPL-compatible project.

### Matrix86/enumerateHandles

Repository: https://github.com/Matrix86/enumerateHandles

License: none detected

Its README explicitly says `NtQueryObject(ObjectNameInformation)` can hang on
named pipes and other objects, so it moves the query to another thread with a
timeout. Because no license was detected, treat the repository as readable
prior art only.

### psutil

Repository: https://github.com/giampaolo/psutil

License: BSD-3-Clause

psutil is more valuable for high-level process API design, exception mapping,
cross-version tests, and discussions around Windows handle/path calls than as a
direct named-pipe endpoint implementation. BSD-3-Clause permits reuse with its
copyright, conditions, disclaimer, and non-endorsement requirement.

The detailed hang investigation and source links are in
[windows-pipe-handle-query-hangs.md](windows-pipe-handle-query-hangs.md).

---

## Closed-source behavioral references

Microsoft Sysinternals tools remain useful expectations for users:

- Handle lists open handles and can search by object name.
- Process Explorer shows process trees, handles, DLLs, security, and rich
  process metadata.
- PipeList lists named pipes and their state/instance information.

They are not source libraries. Chromium Process Explorer can learn from their
terminology, output grouping, filtering, and elevation UX, but should implement
behavior from documented Windows APIs and permissively licensed sources.

---

## Recommended reuse policy

1. Prefer Microsoft API documentation and Chromium/CEF/Electron public
   contracts for new Core behavior.
2. Use WebView2Utilities for platform-specific association ideas and
   WebView2 parity.
3. Use PowerToys File Locksmith and System Informer for permissively licensed
   native patterns, with file-level attribution.
4. Preserve Chromium Process Explorer's helper-process timeout design rather
   than importing thread termination or abandonment patterns.
5. Use GPL and unlicensed projects only to confirm behavior or design test
   cases unless the project's licensing decision explicitly permits source
   incorporation.
6. Record copied/adapted source in an attribution file with repository URL,
   commit, original file, license, and local destination.
7. Add automated license scanning before introducing third-party source or
   binary dependencies.

---

## Sources and licenses

- WebView2Utilities:
  - https://github.com/david-risney/WebView2Utilities
  - https://github.com/david-risney/WebView2Utilities/blob/main/LICENSE
- PowerToys File Locksmith:
  - https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/modules/filelocksmith.md
  - https://github.com/microsoft/PowerToys/blob/main/src/modules/FileLocksmith/FileLocksmithLibInterop/FileLocksmith.cpp
  - https://github.com/microsoft/PowerToys/blob/main/src/modules/FileLocksmith/FileLocksmithLibInterop/NtdllExtensions.cpp
  - https://github.com/microsoft/PowerToys/blob/main/LICENSE
- System Informer:
  - https://github.com/winsiderss/systeminformer
  - https://github.com/winsiderss/systeminformer/blob/master/phlib/nativepipe.c
  - https://github.com/winsiderss/systeminformer/blob/master/LICENSE.txt
- psutil:
  - https://github.com/giampaolo/psutil
  - https://github.com/giampaolo/psutil/blob/master/LICENSE
- VolatileDataCollector:
  - https://github.com/gtworek/VolatileDataCollector/blob/main/HND.c
  - https://github.com/gtworek/VolatileDataCollector/blob/main/LICENSE
- Matrix86/enumerateHandles:
  - https://github.com/Matrix86/enumerateHandles
- Sysinternals:
  - https://learn.microsoft.com/en-us/sysinternals/downloads/handle
  - https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer
  - https://learn.microsoft.com/en-us/sysinternals/downloads/pipelist
