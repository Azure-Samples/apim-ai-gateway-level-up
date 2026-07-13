using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddHttpClient("GraphApi", client =>
        {
            client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        });
    })
    .Build();

host.Run();
