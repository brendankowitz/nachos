using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// The same two settings the API accepts: Nachos:SqlServer:ConnectionString (Bicep) wins over Aspire's ConnectionStrings:nachos.
var connectionString = builder.Configuration[$"{SqlServerOptions.SectionName}:ConnectionString"] is { Length: > 0 } canonical
    ? canonical
    : builder.Configuration["ConnectionStrings:nachos"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "The worker needs SQL Server: set Nachos:SqlServer:ConnectionString or ConnectionStrings:nachos. " +
        "The in-memory provider is only visible to its own process, so with it the API runs the loops instead.");
}

builder.Services.AddNachos(nachos => nachos
    .UseSqlServer(options =>
    {
        builder.Configuration.GetSection(SqlServerOptions.SectionName).Bind(options);
        options.ConnectionString = connectionString;
    })
    .UseConfiguredAI(builder.Configuration)
    .AddNachosWorker());

builder.Services.Configure<NachosOptions>(builder.Configuration.GetSection("Nachos"));

await builder.Build().RunAsync();
