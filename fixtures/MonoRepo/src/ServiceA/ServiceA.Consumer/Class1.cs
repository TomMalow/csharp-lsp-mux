using ServiceA.Api;

namespace ServiceA.Consumer;

public class ServiceAConsumer
{
    public string Report()
    {
        var client = new ServiceAClient();
        return client.GetStatus(1); // call site: ServiceAClient.GetStatus
    }
}
