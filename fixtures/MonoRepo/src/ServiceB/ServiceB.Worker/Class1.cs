namespace ServiceB.Worker;

public class ServiceBWorker
{
    /// <summary>
    /// Processes a work item and returns a result token. SENTINEL_SERVICE_B_PROCESS_DOC
    /// </summary>
    public string Process(int id)
    {
        return $"processed-{id}";
    }
}
