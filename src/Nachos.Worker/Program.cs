using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer;
using Nachos.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

if (!SqlServerConnectionSelection.IsConfigured(builder.Configuration))
{
    throw new InvalidOperationException(
        $"The worker needs SQL Server: set {SqlServerConnectionSelection.CanonicalKey} or {SqlServerConnectionSelection.AspireKey}. " +
        "The in-memory provider is only visible to its own process, so with it the API runs the loops instead.");
}

builder.Services.AddNachos(nachos => nachos
    .UseSqlServer(builder.Configuration)
    .UseConfiguredAI(builder.Configuration)
    .AddNachosWorker());

builder.Services.Configure<NachosOptions>(builder.Configuration.GetSection("Nachos"));

await builder.Build().RunAsync();
