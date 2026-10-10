using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nachos.DataLayer.InMemory;

/// <summary>Warns at host start that the in-memory provider must not serve production.</summary>
internal sealed partial class InMemoryProviderNotice(ILogger<InMemoryProviderNotice> logger, IServiceProvider services)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var environment = (IHostEnvironment?)services.GetService(typeof(IHostEnvironment));
        if (environment is null || !environment.IsDevelopment())
            LogNotDurable(logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The in-memory provider is not durable: all data is lost when the process restarts. " +
                  "Production must use SQL Server.")]
    private static partial void LogNotDurable(ILogger logger);
}
