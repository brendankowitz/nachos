using Microsoft.Data.SqlClient;
using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// The same two settings, and the same rule, as the API's ProviderSelection: Nachos:SqlServer:ConnectionString (Bicep) wins over
// Aspire's ConnectionStrings:nachos, and a key that exists must hold a usable value rather than fall back to the other one.
var canonicalKey = $"{SqlServerOptions.SectionName}:ConnectionString";
var key = builder.Configuration.GetSection(canonicalKey).Exists() ? canonicalKey
    : builder.Configuration.GetSection("ConnectionStrings:nachos").Exists() ? "ConnectionStrings:nachos"
    : throw new InvalidOperationException(
        "The worker needs SQL Server: set Nachos:SqlServer:ConnectionString or ConnectionStrings:nachos. " +
        "The in-memory provider is only visible to its own process, so with it the API runs the loops instead.");
var connectionString = builder.Configuration[key];
if (!NamesDatabase(connectionString))
{
    throw new InvalidOperationException(
        $"Configuration value '{key}' must be a SQL Server connection string that names a database.");
}

builder.Services.AddNachos(nachos => nachos
    .UseSqlServer(options =>
    {
        builder.Configuration.GetSection(SqlServerOptions.SectionName).Bind(options);
        options.ConnectionString = connectionString!;
    })
    .UseConfiguredAI(builder.Configuration)
    .AddNachosWorker());

builder.Services.Configure<NachosOptions>(builder.Configuration.GetSection("Nachos"));

await builder.Build().RunAsync();

static bool NamesDatabase(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return false;
    }

    try
    {
        return !string.IsNullOrWhiteSpace(new SqlConnectionStringBuilder(connectionString).InitialCatalog);
    }
    catch (Exception exception) when (exception is ArgumentException or FormatException)
    {
        return false;
    }
}
