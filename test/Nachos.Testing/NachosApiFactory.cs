using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nachos.Testing;

/// <summary>Real Core and in-memory HTTP host. Authentication is disabled only in Development.</summary>
public class NachosApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Nachos:Auth:Enabled"] = "false",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = null,
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = null,
            }));
        builder.ConfigureServices(services => services.AddNachos(nachos => nachos.UseInMemory()));
    }
}
