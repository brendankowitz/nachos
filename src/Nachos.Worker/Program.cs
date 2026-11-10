using Nachos.DataLayer.SqlServer;
using Nachos.ServiceDefaults;
using Nachos.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

if (!SqlServerConnectionSelection.IsConfigured(builder.Configuration))
{
    throw new InvalidOperationException(
        $"The worker needs SQL Server: set {SqlServerConnectionSelection.CanonicalKey} or {SqlServerConnectionSelection.AspireKey}. " +
        "The in-memory provider is only visible to its own process, so with it the API runs the loops instead.");
}

builder.Services.AddNachosWorkerHost(builder.Configuration);

await builder.Build().RunAsync();
