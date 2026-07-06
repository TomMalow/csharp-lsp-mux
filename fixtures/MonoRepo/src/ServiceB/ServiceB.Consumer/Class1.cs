using ServiceB.Worker;

namespace ServiceB.Consumer;

public class ServiceBConsumer
{
    public string Run()
    {
        var worker = new ServiceBWorker();
        return worker.Process(1); // call site: ServiceBWorker.Process
    }
}
