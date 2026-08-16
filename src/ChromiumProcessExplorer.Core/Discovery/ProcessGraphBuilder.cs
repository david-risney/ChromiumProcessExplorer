using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Builds typed process graphs from one endpoint-enriched snapshot.</summary>
public static class ProcessGraphBuilder
{
    /// <summary>Builds a graph with OS-parent and observed Mojo relationships.</summary>
    public static ProcessGraph Build(
        IEnumerable<ProcessSnapshotEntry> processes,
        MojoPipeInspectionResult mojoInspection,
        DateTimeOffset processObservedAt,
        CefRuntimeAnalysis? cefRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(mojoInspection);

        ProcessSnapshotEntry[] processArray = processes.ToArray();
        Dictionary<int, ProcessSnapshotEntry> processesById =
            processArray.ToDictionary(process => process.ProcessId);
        ProcessTree processTree = ProcessTreeBuilder.Build(processArray);
        List<ProcessGraphEdge> edges = [];

        foreach (ProcessTreeNode parent in processTree.NodesByProcessId.Values
            .OrderBy(node => node.Process.ProcessId))
        {
            foreach (ProcessTreeNode child in parent.Children)
            {
                edges.Add(new ProcessGraphEdge(
                    GetIdentity(parent.Process),
                    GetIdentity(child.Process),
                    ProcessRelationshipType.OsParent,
                    new ProcessRelationshipEvidence(
                        "process-snapshot",
                        ProcessRelationshipConfidence.High,
                        processObservedAt,
                        new Dictionary<string, string?>
                        {
                            ["parentProcessId"] = child.Process.ParentProcessId.ToString(
                                CultureInfo.InvariantCulture),
                            ["parentCreationTime"] = Format(parent.Process.CreationTime),
                            ["childCreationTime"] = Format(child.Process.CreationTime),
                        })));
            }
        }

        foreach (MojoPipeInfo pipe in mojoInspection.Pipes
            .OrderBy(pipe => pipe.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (NamedPipeConnection connection in pipe.Connections
                .OrderBy(connection => connection.ServerProcessId)
                .ThenBy(connection => connection.ClientProcessId)
                .ThenBy(connection => connection.HandleOwnerProcessId))
            {
                if (connection.ServerProcessId is not int serverProcessId
                    || connection.ClientProcessId is not int clientProcessId
                    || !TryGetCurrentProcess(serverProcessId, out ProcessSnapshotEntry? server)
                    || !TryGetCurrentProcess(clientProcessId, out ProcessSnapshotEntry? client))
                {
                    continue;
                }

                edges.Add(new ProcessGraphEdge(
                    GetIdentity(server),
                    GetIdentity(client),
                    ProcessRelationshipType.MojoConnection,
                    new ProcessRelationshipEvidence(
                        "mojo-endpoint-inspection",
                        ProcessRelationshipConfidence.High,
                        mojoInspection.CapturedAt,
                        new Dictionary<string, string?>
                        {
                            ["pipeName"] = pipe.Name,
                            ["processIdHint"] = Format(pipe.ProcessIdHint),
                            ["handleOwnerProcessId"] = connection.HandleOwnerProcessId.ToString(
                                CultureInfo.InvariantCulture),
                            ["handleOwnerImageName"] = connection.HandleOwnerImageName,
                            ["serverProcessId"] = Format(connection.ServerProcessId),
                            ["serverImageName"] = connection.ServerImageName,
                            ["clientProcessId"] = Format(connection.ClientProcessId),
                            ["clientImageName"] = connection.ClientImageName,
                            ["localEnd"] = connection.LocalEnd,
                            ["state"] = connection.State,
                        })));
            }
        }

        foreach (CefProcessAssociation association in
            cefRuntime?.Associations ?? [])
        {
            AddCefEdge(
                association.BrowserProcessId,
                association.SubprocessProcessId,
                ProcessRelationshipType.ChromiumSubprocess,
                association.Score,
                association.Confidence,
                association.IsAuthoritative,
                association.Evidence);
        }

        foreach (CefHostAssociation association in
            cefRuntime?.HostAssociations ?? [])
        {
            AddCefEdge(
                association.HostProcessId,
                association.BrowserProcessId,
                ProcessRelationshipType.EmbeddedBy,
                association.Score,
                association.Confidence,
                association.IsAuthoritative,
                association.Evidence);
        }

        return new ProcessGraph(processArray, edges);

        void AddCefEdge(
            int sourceProcessId,
            int targetProcessId,
            ProcessRelationshipType type,
            int score,
            CefAssociationConfidence confidence,
            bool isAuthoritative,
            IReadOnlyList<string> associationEvidence)
        {
            if (!TryGetCurrentProcess(
                sourceProcessId,
                out ProcessSnapshotEntry? source)
                || !TryGetCurrentProcess(
                    targetProcessId,
                    out ProcessSnapshotEntry? target))
            {
                return;
            }

            edges.Add(new ProcessGraphEdge(
                GetIdentity(source),
                GetIdentity(target),
                type,
                new ProcessRelationshipEvidence(
                    "cef-runtime-adapter",
                    confidence switch
                    {
                        CefAssociationConfidence.High =>
                            ProcessRelationshipConfidence.High,
                        CefAssociationConfidence.Medium =>
                            ProcessRelationshipConfidence.Medium,
                        _ => ProcessRelationshipConfidence.Low,
                    },
                    processObservedAt,
                    new Dictionary<string, string?>
                    {
                        ["score"] = score.ToString(CultureInfo.InvariantCulture),
                        ["isAuthoritative"] = isAuthoritative.ToString(
                            CultureInfo.InvariantCulture),
                        ["evidence"] = string.Join(" ", associationEvidence),
                    })));
        }

        bool TryGetCurrentProcess(
            int processId,
            [NotNullWhen(true)]
            out ProcessSnapshotEntry? process)
        {
            return processesById.TryGetValue(processId, out process)
                && !process.IsProcessIdReused;
        }
    }

    private static ProcessIdentity GetIdentity(ProcessSnapshotEntry process)
    {
        return new ProcessIdentity(process.ProcessId, process.CreationTime);
    }

    private static string? Format(DateTimeOffset? value)
    {
        return value?.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? Format(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture);
    }
}
