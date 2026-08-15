# Windows pipe-handle query hangs

**Researched:** 2026-08-14

The commonly reported hang is not system handle enumeration itself. It is
usually `NtQueryObject(ObjectNameInformation)` on an arbitrary duplicated
file-like handle. Current projects also protect `NtQueryInformationFile`
because named pipes, console handles, devices, and remote filesystems can block
inside the underlying `FILE_OBJECT` or provider.

`NtQuerySystemInformation(SystemExtendedHandleInformation)` and
`DuplicateHandle` are normally performed synchronously. The potentially
blocking name and file-information queries are moved to a disposable worker.

## Verified implementations

### System Informer

Current System Informer routes file-handle `NtQueryObject` and
`NtQueryInformationFile` operations through `PhCallWithTimeout`. On timeout it
terminates the syscall-only worker and returns `STATUS_IO_TIMEOUT`.

- [`phlib/hndlinfo.c`](https://github.com/winsiderss/systeminformer/blob/a0e5068939e637bcb099b34a1878bcc093b3b1da/phlib/hndlinfo.c#L2173-L2203)
- [`SystemInformer/hndlprp.c`](https://github.com/winsiderss/systeminformer/blob/a0e5068939e637bcb099b34a1878bcc093b3b1da/SystemInformer/hndlprp.c#L1448-L1593)

Its handle-properties code explicitly identifies named pipes,
`\Device\ConDrv\CurrentIn`, and `\Device\VolMgrControl` as handles that can
deadlock without a timeout.

The Extended Tools named-pipe viewer can open a pipe by name and query endpoint
information directly. When inspecting arbitrary existing handles, it
timeout-wraps `FilePipeLocalInformation` and `FilePipeInformation`.

### psutil

psutil's Windows open-files implementation runs `GetFileType`,
`NtQueryInformationFile`, and `NtQueryObject(ObjectNameInformation)` in a
worker with a 100 ms timeout:

- [`psutil/arch/windows/proc_handles.c`](https://github.com/giampaolo/psutil/blob/bb82857b20c87994b4f772a7df78b66c95eb72ce/psutil/arch/windows/proc_handles.c#L7-L79)

Its comments describe waits on:

- A shared `FILE_OBJECT` lock
- An idle named pipe
- Network and user-mode filesystem providers behind `\Device\Mup`

The killable worker deliberately performs syscalls only. Heap, runtime, loader,
and application locks must not be acquired by a thread that may be forcibly
terminated.

Related discussions:

- [psutil issue #1967](https://github.com/giampaolo/psutil/issues/1967)
- [psutil PR #2190 discussion](https://github.com/giampaolo/psutil/pull/2190#issuecomment-1376947717)
- [psutil PR #2894](https://github.com/giampaolo/psutil/pull/2894)

### Process Hacker 1.x

Historic Process Hacker used a persistent worker for
`NtQueryObject(ObjectNameInformation)`, waited one second, and called
`TerminateThread` on timeout:

- [`NProcessHacker/obj.c`](https://github.com/mirror/processhacker/blob/66fe601ef5018b19cb08e02d8589310fa963b604/1.x/trunk/NProcessHacker/obj.c#L49-L152)

This is likely the implementation pattern being recalled.

### wtop

wtop performs `SystemExtendedHandleInformation` enumeration normally, but runs
the object-name query on a disposable worker. After 150 ms it abandons the
worker and duplicated handle instead of forcibly terminating the thread:

- [`src/procdetail.c`](https://github.com/lorenzo-cingano/wtop/blob/be307cf203f8f7d2811f22637544a798b57fc963/src/procdetail.c#L258-L274)

The source specifically calls out synchronous file handles and named pipes.
Abandonment avoids unsafe thread termination but permits leaked, permanently
blocked threads, so it needs a strict global budget.

### LimaCharlie

LimaCharlie describes its worker as a workaround for a handle that can hang. It
waits one second around `ObjectNameInformation`, then terminates the worker:

- [`processLib.c`](https://github.com/DimChris0/lima-charlie/blob/e3a6898e80d97b5966e9d81724727431e24126ba/sensor/lib/processLib/processLib.c#L1700-L1789)

## Mojo endpoint examples

Chromium itself calls `GetNamedPipeClientProcessId` and
`GetNamedPipeServerProcessId` once it already owns a connected Mojo pipe
transport handle:

- [`mojo/core/ipcz_driver/invitation.cc`](https://github.com/chromium/chromium/blob/fd5d1cacd53debc7fe33129c936259a39d4de843/mojo/core/ipcz_driver/invitation.cc#L108-L149)

libuv uses the same public endpoint APIs:

- [`src/win/pipe.c`](https://github.com/libuv/libuv/blob/v1.x/src/win/pipe.c)

System Informer also implements endpoint queries using
`NtFsControlFile` with the `ClientProcessId` and `ServerProcessId` pipe
attributes:

- [`phlib/nativepipe.c`](https://github.com/winsiderss/systeminformer/blob/a0e5068939e637bcb099b34a1878bcc093b3b1da/phlib/nativepipe.c)

## Recommended design

1. Enumerate processes and system handles on the normal worker path.
2. Filter by object type before querying names.
3. Prefer opening known candidate pipe names with minimal attribute access and
   using `GetNamedPipeClientProcessId` / `GetNamedPipeServerProcessId`.
4. If arbitrary foreign handles must be inspected, isolate:
   - `NtQueryObject(ObjectNameInformation)`
   - Every `NtQueryInformationFile` call, including pipe information classes
5. Prefer a helper **process** for the dangerous queries. It can be terminated
   without corrupting locks in the main application.
6. If an in-process worker is used, make it syscall-only, preallocate all
   buffers, impose a short timeout, and cap abandoned or terminated workers.
7. Treat timeouts as a normal per-handle result, not a fatal scan error.

A heartbeat is not necessary if each request has a bounded wait and request
identifier. For a long-lived helper process, a heartbeat additionally detects
a helper stuck between requests. Restart the helper after a timeout rather than
trying to reuse an uncertain worker.

## Parallel scan architecture

Use a staged pipeline with bounded queues. Do not create one thread or task per
system handle: a machine can have hundreds of thousands of handles, and several
blocked queries could otherwise exhaust the thread pool.

### Stage 1: immutable snapshots

Capture the process table and system handle table once. Record process creation
times so later results can be rejected if a PID was reused. The handle snapshot
should include owner PID, handle value, object pointer, granted access, object
type index, and attributes.

This stage is a single operation. Parallelizing multiple system-wide handle
snapshots would add overhead and make results less internally consistent.

### Stage 2: cheap filtering and deduplication

Parallelize CPU-only filtering over partitions of the snapshot:

- Keep only object type indexes known to represent file objects.
- Apply product, process, session, and access-mask filters.
- Deduplicate repeated `(owner PID, handle value)` work.
- Use the kernel object pointer only as a scan-local deduplication hint.
- Group surviving handles by owner PID.

Grouping by owner lets a worker open each source process once with
`PROCESS_DUP_HANDLE`, duplicate all relevant handles, and close the process
handle after the batch.

### Stage 3: safe enrichment pool

Use a normal bounded worker pool for operations that have not shown indefinite
blocking behavior:

- Open source processes.
- Duplicate candidate handles.
- Query cached object type information.
- Correlate endpoint PIDs with the process snapshot.
- Parse Chromium command lines and enrich process metadata.

Cache object type names by type index and file/version metadata by stable file
identity. Avoid repeating the same query for every handle.

### Stage 4: hazardous query pool

Route only potentially blocking operations to a small pool of helper
**processes**:

- `NtQueryObject(ObjectNameInformation)`
- `NtQueryInformationFile`
- Any future query empirically shown to block on arbitrary foreign handles

Each helper accepts one request at a time over private IPC. Every request has:

- A unique request ID
- A hard deadline
- Input and output size limits
- A duplicated handle owned by that helper

On deadline expiry, terminate and replace the entire helper. Do not return it to
the pool. A heartbeat can distinguish an idle/live helper from one stuck
between requests, but it does not replace per-request deadlines.

Keep this pool deliberately small and configurable. More helpers improve
throughput only until the kernel, target process, or backing filesystem becomes
the bottleneck. Excess concurrency increases memory use and can amplify waits
against the same underlying object.

### Stage 5: endpoint and graph correlation

Endpoint PID lookups and process-graph updates can run concurrently, but publish
results through a single graph writer or immutable result batches. Validate
every PID against its captured creation time before adding an edge.

Deduplicate pipe instances using the best available combination of pipe name,
server PID, client PID, and scan-local object identity. Do not collapse all
instances that share a named-pipe path.

### Backpressure and failure handling

- Bound every queue to prevent the snapshot from becoming an unbounded task
  list.
- Stop scheduling lower-value enrichment when cancellation is requested.
- Apply per-owner and per-object concurrency limits so one process or device
  cannot monopolize the scan.
- Add a circuit breaker when repeated queries for one device class, process, or
  pipe prefix time out.
- Report partial results with explicit timeout, access-denied, process-exited,
  and PID-reused statuses.
- Emit progress counters for discovered, filtered, duplicated, queried,
  timed-out, failed, and completed handles.

### Suggested scheduling model

A channel-based producer/consumer pipeline or work-stealing scheduler is
appropriate. Start with configuration rather than fixed thread counts:

- CPU filtering workers: derived from logical processor count
- Safe enrichment workers: bounded separately because they open processes and
  duplicate handles
- Hazardous helper processes: small fixed default with an upper limit
- Per-query timeout: configurable by operation
- Overall scan deadline and cancellation token

Measure queue depth, latency percentiles, timeout frequency, helper restarts,
and handles processed per second. Tune defaults from real scans rather than
assuming maximum parallelism is fastest.
