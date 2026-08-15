namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Helpers for consuming endpoint-enriched Mojo evidence.</summary>
public static class MojoPipeInspectionExtensions
{
    /// <summary>
    /// Gets all process IDs identified by endpoint inspection, using the pipe
    /// name PID hint only when a pipe has no resolved endpoints.
    /// </summary>
    public static IReadOnlySet<int> GetRelatedProcessIds(
        this MojoPipeInspectionResult inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        HashSet<int> processIds = [];
        foreach (MojoPipeInfo pipe in inspection.Pipes)
        {
            bool resolvedEndpoint = false;
            foreach (NamedPipeConnection connection in pipe.Connections)
            {
                if (connection.ServerProcessId is int serverProcessId)
                {
                    resolvedEndpoint = true;
                    processIds.Add(serverProcessId);
                }

                if (connection.ClientProcessId is int clientProcessId)
                {
                    resolvedEndpoint = true;
                    processIds.Add(clientProcessId);
                }

                Add(connection.HandleOwnerProcessId);
            }

            if (!resolvedEndpoint)
            {
                Add(pipe.ProcessIdHint);
            }
        }

        return processIds;

        bool Add(int? processId)
        {
            return processId is int value && processIds.Add(value);
        }
    }
}
