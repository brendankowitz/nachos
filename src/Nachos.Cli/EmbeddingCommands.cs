using System.CommandLine;
using System.Text.Json;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;

namespace Nachos.Cli;

/// <summary>
/// <c>nachos embeddings reset</c>: switches a database to another embedding model. Every conclusion and chunk vector goes back to
/// pending and the database is stamped with the new model; the worker's reconciler then re-embeds them with it.
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
            Description = "Rows cleared per statement.",
            DefaultValueFactory = _ => 1000,
        };
        var command = new Command(
            "reset",
            "Clear every stored vector back to pending and stamp the database with --model. Stop every API and worker first: a host " +
            "still running the old model would embed rows again with it. Until the worker re-embeds them, those rows are missing from " +
            "vector search (lexical search still finds messages). Prints {model, reset}. Exit 0: done; 2: the schema is not current; 1: error.")
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
            var space = EmbeddingSpaceCheck.SpaceOf(parse.GetRequiredValue(model));
            var reset = await store.Embeddings.ResetSpaceAsync(space, parse.GetValue(batchSize), ct);
            await parse.InvocationConfiguration.Output.WriteLineAsync(JsonSerializer.Serialize(new ResetJson(space.Model, reset), Json));
            return ExitCodes.Success;
        }, ct)));
        return command;
    }
}
