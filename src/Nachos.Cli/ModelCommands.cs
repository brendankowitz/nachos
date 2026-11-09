using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Nachos.Core.Llm;

namespace Nachos.Cli;

/// <summary>
/// <c>nachos models fetch</c>: downloads a local embedding model's pinned files over HTTPS into <c>{dir}/{model}/</c> and checks
/// each against its pinned SHA-256. A file already there with the right hash is skipped; one with another hash is never overwritten.
/// </summary>
internal static class ModelCommands
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Declaration order is the output's property order.
    private sealed record FetchJson(string Model, string Folder, IReadOnlyList<FileJson> Files);

    private sealed record FileJson(string Name, string Status);

    public static Command Create() => Create(LocalEmbeddingModel.All, () => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });

    /// <summary>The command over <paramref name="catalog"/>, downloading with clients from <paramref name="createClient"/>; a test passes its own.</summary>
    internal static Command Create(IReadOnlyList<LocalEmbeddingModel> catalog, Func<HttpClient> createClient) =>
        new("models", "Manage the local embedding models used by UseLocalEmbeddings (Nachos:Ai:Embeddings=Local).")
        {
            Fetch(catalog, createClient),
        };

    private static Command Fetch(IReadOnlyList<LocalEmbeddingModel> catalog, Func<HttpClient> createClient)
    {
        var ids = string.Join(", ", catalog.Select(model => model.Id));
        var model = new Argument<string>("model") { Description = $"The model to fetch: {ids}." };
        var directory = new Option<string>("--dir")
        {
            Description = "The models directory; the files go to <dir>/<model>/. Default: nachos/models under the user's local application data " +
                          "(%LOCALAPPDATA% on Windows, ~/.local/share on Linux), where the hosts look when Embeddings:Local:ModelDirectory is unset.",
        };
        var command = new Command(
            "fetch",
            "Download the model's pinned files over HTTPS and verify each SHA-256. Prints {model, folder, files:[{name, status}]}, status " +
            "'present' or 'downloaded'. Exit 0: every file is in place; 2: a file is there with another hash and was left alone; 1: error " +
            "(a download whose hash differs is deleted).")
        {
            model, directory,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(model) is { } id && !catalog.Any(entry => entry.Id == id))
            {
                UsageErrors.Add(result, UsageError.ModelUnknown);
            }
        });
        command.SetAction((parse, ct) => CommandFailure.GuardAsync(parse.InvocationConfiguration.Error, async () =>
        {
            var entry = catalog.Single(candidate => candidate.Id == parse.GetRequiredValue(model));
            var folder = entry.FolderIn(Path.GetFullPath(parse.GetValue(directory) ?? LocalEmbeddingModel.DefaultDirectory));
            Directory.CreateDirectory(folder);
            var files = new List<FileJson>();
            using var client = createClient();
            foreach (var file in entry.Files)
            {
                var target = Path.Combine(folder, file.Name);
                if (File.Exists(target))
                {
                    if (!file.IsAt(target))
                    {
                        await parse.InvocationConfiguration.Error.WriteLineAsync(
                            $"Refused: '{target}' exists but its SHA-256 is not the pinned {file.Sha256}. It was left alone; delete it to fetch again.");
                        return ExitCodes.Refused;
                    }

                    files.Add(new FileJson(file.Name, "present"));
                    continue;
                }

                await DownloadAsync(client, file, target, ct);
                files.Add(new FileJson(file.Name, "downloaded"));
            }

            await parse.InvocationConfiguration.Output.WriteLineAsync(JsonSerializer.Serialize(new FetchJson(entry.Id, folder, files), Json));
            return ExitCodes.Success;
        }));
        return command;
    }

    /// <summary>
    /// Streams <paramref name="file"/> into a temporary file beside <paramref name="target"/>, hashing as it goes, and moves it into
    /// place only when the hash matches. Any failure deletes the temporary file, so nothing unverified is ever left behind.
    /// </summary>
    private static async Task DownloadAsync(HttpClient client, LocalModelFile file, string target, CancellationToken ct)
    {
        if (file.Source.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Refusing to download {file.Name} over {file.Source.Scheme}; model files are fetched over HTTPS only.");
        }

        var partial = $"{target}.{Guid.NewGuid():N}.partial";
        try
        {
            using var response = await client.GetAsync(file.Source, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The download of {file.Name} from {file.Source} has SHA-256 {actual}, not the pinned {file.Sha256}; it was deleted.");
            }

            File.Move(partial, target, overwrite: false);
        }
        finally
        {
            File.Delete(partial);
        }
    }
}
