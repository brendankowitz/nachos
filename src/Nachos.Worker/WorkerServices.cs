using Nachos.Core.Configuration;

namespace Nachos.Worker;

public static class WorkerServices
{
    /// <summary>
    /// The worker host's Nachos registration. Hosted services start in registration order, so the embedding model check
    /// (from <c>UseConfiguredAI</c>) must come before the loops (<c>AddNachosWorker</c>); a test pins that order through this
    /// method, which <c>Program.cs</c> also calls.
    /// </summary>
    public static IServiceCollection AddNachosWorkerHost(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddNachos(nachos => nachos
            .UseSqlServer(configuration)
            .UseConfiguredAI(configuration)
            .AddNachosWorker());
        services.Configure<NachosOptions>(configuration.GetSection("Nachos"));
        return services;
    }
}
