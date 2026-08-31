using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Gui;

public interface IGuiDiscoveryService
{
    ValueTask<ChromiumDiscoveryResult> DiscoverProcessesAsync(
        CancellationToken cancellationToken);

    ValueTask<MojoPipeEnumerationResult> EnumerateMojoPipesAsync(
        CancellationToken cancellationToken);

    ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
        int processId,
        CancellationToken cancellationToken);

    ValueTask<DiagnosticArtifactDiscoveryResult> DiscoverDiagnosticsAsync(
        CancellationToken cancellationToken);

    ValueTask<InstallationDiscoveryResult> DiscoverInstallationsAsync(
        IReadOnlyList<string> additionalSearchRoots,
        CancellationToken cancellationToken);

    ValueTask<CdpTargetListResult> DiscoverCdpTargetsAsync(
        CdpTransportInfo transport,
        CancellationToken cancellationToken);

    ValueTask OpenDevToolsAsync(
        CdpTransportInfo transport,
        string targetId,
        CancellationToken cancellationToken);

    ValueTask<CdpProcessInternalsResult> DiscoverProcessInternalsAsync(
        CdpTransportInfo transport,
        string? imageName,
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CancellationToken cancellationToken);

}

public sealed class GuiDiscoveryService : IGuiDiscoveryService
{
    private readonly ChromiumProcessDiscovery _discovery = new();
    private readonly CdpBrowserToolsProvider _browserTools = new();
    private readonly string _workerPath;

    public GuiDiscoveryService(string workerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        _workerPath = workerPath;
    }

    public async ValueTask<ChromiumDiscoveryResult> DiscoverProcessesAsync(
        CancellationToken cancellationToken)
    {
        return await _discovery.DiscoverAsync(
            new HandleQueryWorkerOptions(_workerPath, 0),
            includeWindowEvidence: true,
            maximumProcessConcurrency: null,
            cancellationToken: cancellationToken);
    }

    public ValueTask<MojoPipeEnumerationResult> EnumerateMojoPipesAsync(
        CancellationToken cancellationToken)
    {
        return _discovery.EnumerateMojoPipesAsync(cancellationToken);
    }

    public async ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        return await _discovery.DiscoverProcessDetailsAsync(
            processId,
            includeSensitiveValues: true,
            cancellationToken: cancellationToken);
    }

    public async ValueTask<DiagnosticArtifactDiscoveryResult>
        DiscoverDiagnosticsAsync(CancellationToken cancellationToken)
    {
        return await _discovery.DiscoverDiagnosticArtifactsAsync(
            includeSensitiveValues: true,
            cancellationToken: cancellationToken);
    }

    public async ValueTask<InstallationDiscoveryResult> DiscoverInstallationsAsync(
        IReadOnlyList<string> additionalSearchRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(additionalSearchRoots);
        return await _discovery.DiscoverInstallationsWithOptionsAsync(
            new WindowsInstallationDiscoveryOptions
            {
                AdditionalSearchRoots = additionalSearchRoots,
            },
            cancellationToken: cancellationToken);
    }

    public ValueTask<CdpTargetListResult> DiscoverCdpTargetsAsync(
        CdpTransportInfo transport,
        CancellationToken cancellationToken)
    {
        return _browserTools.DiscoverTargetsAsync(
            transport,
            cancellationToken);
    }

    public ValueTask OpenDevToolsAsync(
        CdpTransportInfo transport,
        string targetId,
        CancellationToken cancellationToken)
    {
        return _browserTools.OpenDevToolsAsync(
            transport,
            targetId,
            cancellationToken);
    }

    public ValueTask<CdpProcessInternalsResult> DiscoverProcessInternalsAsync(
        CdpTransportInfo transport,
        string? imageName,
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CancellationToken cancellationToken)
    {
        return _browserTools.CaptureProcessInternalsAsync(
            transport,
            imageName,
            processes,
            cancellationToken);
    }
}
