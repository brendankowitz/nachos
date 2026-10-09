using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// The 2025 tag matches the SQL Server suite and the schema the data layer targets. The data volume keeps the
// database across restarts, so the default (persisted) SA password parameter is kept rather than regenerated.
var sql = builder.AddSqlServer("sql")
    .WithImageTag("2025-latest")
    .WithDataVolume();
var nachosDb = sql.AddDatabase("nachos");

var api = builder.AddProject<Projects.Nachos_Api>("api")
    .WithReference(nachosDb)
    .WaitFor(nachosDb)
    .WithHttpEndpoint() // The API ships no launch profile, so Aspire is asked for an endpoint explicitly.
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", builder.Environment.EnvironmentName) // ...and no launch profile means no environment either.
    .WithHttpHealthCheck("/health/ready");

if (builder.Environment.IsDevelopment())
{
    // A fresh random key per run: tokens minted in one run are not valid in the next, which is what a dev loop wants.
    var signingKey = builder.AddParameter(
        "nachos-signing-key",
        () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        secret: true);

    api.WithEnvironment("Nachos__SqlServer__AutomaticSchemaDeploymentEnabled", "true")
        .WithEnvironment("Nachos__Auth__NachosKey__Keys__0__Kid", "dev")
        .WithEnvironment("Nachos__Auth__NachosKey__Keys__0__Secret", signingKey);
}

builder.Build().Run();



