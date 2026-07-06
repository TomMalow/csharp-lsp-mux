namespace ServiceA.Api;

public class ServiceAClient
{
    /// <summary>
    /// Retrieves the client's status string. SENTINEL_SERVICE_A_GETSTATUS_DOC
    /// </summary>
    public string GetStatus(int id)
    {
        return $"status-{id}";
    }
}
