namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Provides a reusable process snapshot.</summary>
public interface IProcessSnapshotProvider
{
    /// <summary>Captures and enriches a process snapshot.</summary>
    ValueTask<IReadOnlyList<ProcessSnapshotEntry>> CaptureAsync(
        int? maximumConcurrency = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a process snapshot while reusing exact prior process generations.
    /// </summary>
    ValueTask<IReadOnlyList<ProcessSnapshotEntry>> CaptureIncrementalAsync(
        IReadOnlyList<ProcessSnapshotEntry> previousProcesses,
        int? maximumConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        return CaptureAsync(maximumConcurrency, cancellationToken);
    }
}

/// <summary>Provides an optional snapshot of native Windows window topology.</summary>
public interface IWindowSnapshotProvider
{
    /// <summary>
    /// Captures top-level and descendant windows, preserving partial failures.
    /// </summary>
    ValueTask<WindowSnapshotResult> CaptureAsync(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides Mojo named-pipe candidates.</summary>
public interface IMojoPipeProvider
{
    /// <summary>Enumerates visible Mojo pipe candidates.</summary>
    ValueTask<MojoPipeEnumerationResult> EnumerateAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Discovers Chromium-related software installations.</summary>
public interface IInstallationProvider
{
    /// <summary>
    /// Discovers known installations, filesystem markers, and installations
    /// represented by running Chromium processes.
    /// </summary>
    ValueTask<InstallationDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
        CancellationToken cancellationToken = default);
}

/// <summary>Discovers configured and validated CDP transports.</summary>
public interface ICdpEndpointProvider
{
    /// <summary>Analyzes CDP transport evidence for one process snapshot.</summary>
    ValueTask<CdpDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        HandleQueryWorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default);
}
