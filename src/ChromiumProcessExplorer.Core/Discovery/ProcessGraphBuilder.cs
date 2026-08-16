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
        DateTimeOffset processObservedAt)
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

        return new ProcessGraph(processArray, edges);

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
