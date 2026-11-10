using System.CommandLine;
using System.Text.Json;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;

namespace Nachos.Cli;

/// <summary>
/// <c>nachos embeddings reset</c>: switches a database to another embedding model. Every conclusion vector goes back to pending,
/// every message chunk is deleted (it was cut for the old model's tokenizer), and the database is stamped with the new model; the
/// worker's reconciler then re-chunks and re-embeds with it.
/// </summary>
internal static class EmbeddingCommands
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Declaration order is the output's property order.
    private sealed record ResetJson(string Model, long Reset);

    public static Command Create() => new("embeddings", "Manage the embedding model a database's vectors come from.")
    {
        Reset(),
    };

    /// <summary>The Azure OpenAI embedding deployment the hosts are configured with, when this shell has it too.</summary>
    private const string AzureDeploymentVariable = "AzureOpenAI__EmbeddingDeployment";

    private static bool IsKnownModel(string id) =>
        LocalEmbeddingModel.Find(id) is not null
        || id == DeterministicEmbeddingGenerator.ModelId
        || id == Environment.GetEnvironmentVariable(AzureDeploymentVariable);

    private static Command Reset()
    {
        var connection = SchemaCommands.ConnectionOption();
        var model = new Option<string>("--model")
        {
            Description = "The model the hosts are configured with: a local model id (such as bge-small-en-v1.5), the Azure OpenAI " +
                          $"embedding deployment name, or {DeterministicEmbeddingGenerator.ModelId} for the fake AI.",
            Required = true,
        };
        var batchSize = new Option<int>("--batch-size")
        {
            Description = "Rows reset or deleted per statement.",
            DefaultValueFactory = _ => 1000,
        };
        var command = new Command(
            "reset",
            "Send every conclusion vector back to pending, delete every message chunk, and stamp the database with --model (a catalog id " +
            "is stamped as the catalog spells it; an unknown id gets a warning). Stop every API and worker first: a host still running is " +
            "not detected, and with the old model it would embed rows again. Until the worker re-chunks and re-embeds, those rows are " +
            "missing from vector search (lexical search still finds messages). Prints {model, reset}, reset counting conclusions reset " +
            "and chunks deleted. Exit 0: done; 2: the schema is not current; 1: error.")
        {
            connection, model, batchSize,
        };
        command.Validators.Add(result =>
        {
            if (UsageErrors.SingleValue(result, model) is { } id
                && (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Length > EmbeddingSpace.MaxModelLength))
            {
                UsageErrors.Add(result, UsageError.EmbeddingModelInvalid);
            }

            if (result.GetResult(batchSize) is { Implicit: false } && UsageErrors.SingleValue(result, batchSize) < 1)
            {
                UsageErrors.Add(result, UsageError.BatchSizeInvalid);
            }
        });
        command.SetAction((parse, ct) => SchemaCommands.GuardAsync(parse, connection, deployer => GrantCommands.WithStoreAsync(parse, connection, deployer, async store =>
        {
            var requested = parse.GetRequiredValue(model);
            // Hosts report a catalog model by its exact id, so a differently cased one would never match the stamp.
            var id = LocalEmbeddingModel.Find(requested)?.Id ?? requested;
            if (!IsKnownModel(id))
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync(
                    $"warning: '{id}' is not a local catalog model, the fake AI's '{DeterministicEmbeddingGenerator.ModelId}', or " +
                    $"{AzureDeploymentVariable}. It must be exactly the model id the hosts report (for Azure OpenAI, the embedding " +
                    "deployment name), or they will refuse to start. If you configured the deployment elsewhere (appsettings, " +
                    "user-secrets, Aspire), ignore this.");
            }

            var space = EmbeddingSpaceCheck.SpaceOf(id);
            var reset = await store.Embeddings.ResetSpaceAsync(space, parse.GetValue(batchSize), ct);
            await parse.InvocationConfiguration.Output.WriteLineAsync(JsonSerializer.Serialize(new ResetJson(space.Model, reset), Json));
            return ExitCodes.Success;
        }, ct)));
        return command;
    }
}
