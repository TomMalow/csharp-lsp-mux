namespace ServiceA.Api;

public class ServiceAClient : IServiceAClient
{
    /// <summary>
    /// Retrieves the client's status string. SENTINEL_SERVICE_A_GETSTATUS_DOC
    /// </summary>
    public string GetStatus(int id)
    {
        return $"status-{id}";
    }
}

public interface IServiceAClient
{
    /// <summary>
    /// Retrieves the client's status string.
    /// </summary>
    string GetStatus(int id);
}
