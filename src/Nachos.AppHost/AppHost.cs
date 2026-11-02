using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Local development only: dev-only wiring (automatic schema deployment, a development signing key, the Development
// environment) must never reach a published manifest, so it is gated on run mode as well as on the environment.
var developmentRun = builder.ExecutionContext.IsRunMode && builder.Environment.IsDevelopment();

// The image tag is the same build the FTS image in eng/docker/mssql-fts/Dockerfile is based on; bump both together.
// The data volume keeps the database across restarts, so the default (persisted) SA password parameter is kept
// rather than regenerated.
var sql = builder.AddSqlServer("sql")
    .WithImageTag("2025-CU9-ubuntu-24.04")
    .WithDataVolume();
var nachosDb = sql.AddDatabase("nachos");

// WithReference hands the API its connection string as ConnectionStrings__nachos. That is all the AppHost sets: the API
// prefers Nachos:SqlServer:ConnectionString when that is configured too (as the Bicep deployment does), and the AppHost
// deliberately leaves it unset. The API never sees authentication switched off by the AppHost.
var api = builder.AddProject<Projects.Nachos_Api>("api")
    .WithReference(nachosDb)
    .WaitFor(nachosDb)
    .WithHttpEndpoint() // The API ships no launch profile, so Aspire is asked for an endpoint explicitly.
    .WithHttpHealthCheck("/health/ready");

if (builder.ExecutionContext.IsRunMode)
{
    // No launch profile means no environment either; a published manifest must not carry one.
    api.WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName);
}

if (developmentRun)
{
    // A fresh random key per run (tokens from one run are not valid in the next) unless Parameters:nachos-signing-key is
    // set in user secrets or the environment, which pins a known key. To mint tokens that this API accepts, use
    // `nachos keys create ... --kid dev` with that same key, because the ring's key id is "dev".
    var signingKey = builder.AddParameter(
        "nachos-signing-key",
        () => builder.Configuration["Parameters:nachos-signing-key"] ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        secret: true);

    api.WithEnvironment("Nachos__SqlServer__AutomaticSchemaDeploymentEnabled", "true")
        .WithEnvironment("Nachos__Auth__NachosKey__Keys__0__Kid", "dev")
        .WithEnvironment("Nachos__Auth__NachosKey__Keys__0__Secret", signingKey);
}

// The worker runs the Deriver and the reconciler against the same database. It waits for the API, which deploys the schema
// on its first readiness check in Development; the worker never deploys it.
var worker = builder.AddProject<Projects.Nachos_Worker>("worker")
    .WithReference(nachosDb)
    .WaitFor(api);

// Offline fake AI unless an Azure OpenAI endpoint is configured for the AppHost (AzureOpenAI:Endpoint in user secrets or the
// environment). The fake AI is not a memory-quality mode: conclusions are copies of messages. Real Azure OpenAI authenticates
// with DefaultAzureCredential, so the developer's `az login` is what the hosts use.
var azureOpenAiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
foreach (var project in new[] { api, worker })
{
    if (string.IsNullOrWhiteSpace(azureOpenAiEndpoint))
    {
        project.WithEnvironment("Nachos__Ai__Mode", "Fake");
    }
    else
    {
        project.WithEnvironment("AzureOpenAI__Endpoint", azureOpenAiEndpoint);
        foreach (var setting in new[] { "ChatDeployment", "EmbeddingDeployment" })
        {
            if (builder.Configuration[$"AzureOpenAI:{setting}"] is { Length: > 0 } deployment)
            {
                project.WithEnvironment($"AzureOpenAI__{setting}", deployment);
            }
        }
    }
}

if (developmentRun && string.IsNullOrWhiteSpace(azureOpenAiEndpoint))
{
    // Idle polling backs off to 15 seconds by default; a local demo should not wait that long for its first conclusion.
    worker.WithEnvironment("Worker__MaxIdleDelay", "00:00:02");
}

builder.Build().Run();
