using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Shouldly;

namespace Nachos.AppHost.Tests;

/// <summary>
/// The AppHost's contract with the API and the database, read from the application model: nothing is started, so no
/// Docker is needed. The smoke test proves the pieces work together; these tests pin which pieces the AppHost wires, and
/// in which modes, so a dropped wait, a misspelled setting or dev-only wiring leaking into a published manifest is caught.
/// </summary>
public sealed class AppHostModelTests
{
    private const string SchemaDeployment = "Nachos__SqlServer__AutomaticSchemaDeploymentEnabled";
    private const string KeyId = "Nachos__Auth__NachosKey__Keys__0__Kid";
    private const string KeySecret = "Nachos__Auth__NachosKey__Keys__0__Secret";

    public static TheoryData<string, bool> AllModes => new()
    {
        { "Development", false },
        { "Production", false },
        { "Development", true },
        { "Production", true },
    };

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task DevelopmentWiring_ExistsOnlyInDevelopmentRunMode(string environment, bool publish)
    {
        var model = await BuildModelAsync(environment, publish);
        var env = await ApiEnvironmentAsync(model);
        var developmentRun = environment == "Development" && !publish;

        if (developmentRun)
        {
            env[SchemaDeployment].ShouldBe("true");
            env[KeyId].ShouldBe("dev");
            var secret = env[KeySecret].ShouldBeOfType<ParameterResource>();
            secret.Name.ShouldBe("nachos-signing-key");
            secret.Secret.ShouldBeTrue("the signing key must never appear in plain text in a manifest or the dashboard");
        }
        else
        {
            env.Keys.ShouldNotContain(SchemaDeployment);
            env.Keys.ShouldNotContain(KeyId);
            env.Keys.ShouldNotContain(KeySecret);
            model.Resources.OfType<ParameterResource>().ShouldNotContain(parameter => parameter.Name == "nachos-signing-key");
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task AuthIsNeverDisabledByTheAppHost(string environment, bool publish)
    {
        var env = await ApiEnvironmentAsync(await BuildModelAsync(environment, publish));

        env.Keys.ShouldNotContain("Nachos__Auth__Enabled");
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task AspNetCoreEnvironment_IsForwardedInRunModeOnly(string environment, bool publish)
    {
        var env = await ApiEnvironmentAsync(await BuildModelAsync(environment, publish));

        if (publish)
        {
            env.Keys.ShouldNotContain("ASPNETCORE_ENVIRONMENT");
        }
        else
        {
            env["ASPNETCORE_ENVIRONMENT"].ShouldBe(environment);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Api_ReferencesAndWaitsForTheNachosDatabase(string environment, bool publish)
    {
        var model = await BuildModelAsync(environment, publish);
        var api = Resource(model, "api");
        var database = Resource(model, "nachos");

        var connection = (await ApiEnvironmentAsync(model))["ConnectionStrings__nachos"];
        connection.ShouldBeOfType<ConnectionStringReference>().Resource.ShouldBeSameAs(database, "the API gets the nachos database connection string through WithReference");
        api.Annotations.OfType<WaitAnnotation>().ShouldContain(
            wait => ReferenceEquals(wait.Resource, database) && wait.WaitType == WaitType.WaitUntilHealthy,
            "the API must not start before the nachos database is healthy");
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Api_IsHealthCheckedOnReadiness(string environment, bool publish)
    {
        var model = await BuildModelAsync(environment, publish);

        Resource(model, "api").Annotations.OfType<HealthCheckAnnotation>()
            .Select(check => check.Key).ShouldBe(["api_http_/health/ready_200_check"]);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task SqlServer_RunsThePinnedImage(string environment, bool publish)
    {
        var model = await BuildModelAsync(environment, publish);

        var image = Resource(model, "sql").Annotations.OfType<ContainerImageAnnotation>().Single();
        image.Tag.ShouldBe("2025-CU9-ubuntu-24.04", "keep in step with eng/docker/mssql-fts/Dockerfile");
    }

    private static async Task<IDistributedApplicationTestingBuilder> BuildModelAsync(string environment, bool publish)
    {
        List<string> args = [$"--environment={environment}"];
        if (publish)
        {
            // Publish mode, as for `dotnet run --project src/Nachos.AppHost -- --publisher manifest`. The model is only
            // inspected: it is never built, so the manifest path is never written.
            args.AddRange(["--publisher", "manifest", "--output-path", Path.Combine(Path.GetTempPath(), "nachos-apphost-model.json")]);
        }

        var model = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Nachos_AppHost>([.. args]);
        model.ExecutionContext.IsPublishMode.ShouldBe(publish, "the arguments must put the AppHost in the intended mode");
        return model;
    }

    private static IResource Resource(IDistributedApplicationTestingBuilder model, string name) =>
        model.Resources.Single(resource => resource.Name == name);

    /// <summary>The API's environment as the AppHost declares it, before any value is resolved.</summary>
    private static async Task<Dictionary<string, object>> ApiEnvironmentAsync(IDistributedApplicationTestingBuilder model)
    {
        var api = Resource(model, "api");
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(model.ExecutionContext, api, values);
        foreach (var annotation in api.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return values;
    }
}
