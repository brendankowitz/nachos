using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nachos.Core.Llm;
using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>
/// <c>nachos models fetch</c> against a local HTTP fixture (a message handler serving fixed bytes), never the internet.
/// </summary>
public sealed class ModelCommandsTests : IDisposable
{
    private static readonly byte[] ModelBytes = Encoding.UTF8.GetBytes("pretend onnx weights");
    private static readonly byte[] VocabularyBytes = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n");

    private static readonly LocalEmbeddingModel TestModel = LocalEmbeddingModel.Default with
    {
        Id = "test-model",
        Files =
        [
            new(LocalEmbeddingModel.ModelFileName, new("https://models.test/rev1/model.onnx"), Hash(ModelBytes)),
            new(LocalEmbeddingModel.VocabularyFileName, new("https://models.test/rev1/vocab.txt"), Hash(VocabularyBytes)),
        ],
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nachos-models-" + Guid.NewGuid().ToString("N"));
    private readonly FixedFiles _server = new();

    public void Dispose()
    {
        _server.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Folder => Path.Combine(_directory, TestModel.Id);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<CliRun> FetchAsync(params string[] args)
    {
        var root = new RootCommand { ModelCommands.Create([TestModel], () => new HttpClient(_server, disposeHandler: false)) };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync(root, ["models", "fetch", .. args], output, error, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Fetch_DownloadsAndVerifiesEveryFile()
    {
        _server.Files["/rev1/model.onnx"] = ModelBytes;
        _server.Files["/rev1/vocab.txt"] = VocabularyBytes;

        var run = await FetchAsync("test-model", "--dir", _directory);

        run.ExitCode.ShouldBe(0, run.Error);
        File.ReadAllBytes(Path.Combine(Folder, "model.onnx")).ShouldBe(ModelBytes);
        File.ReadAllBytes(Path.Combine(Folder, "vocab.txt")).ShouldBe(VocabularyBytes);
        run.Out.ShouldContain("\"status\":\"downloaded\"");
        Directory.GetFiles(Folder).Length.ShouldBe(2);
    }

    [Fact]
    public async Task HashMismatch_FailsAndKeepsNothing()
    {
        _server.Files["/rev1/model.onnx"] = Encoding.UTF8.GetBytes("tampered weights");
        _server.Files["/rev1/vocab.txt"] = VocabularyBytes;

        var run = await FetchAsync("test-model", "--dir", _directory);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("not the pinned");
        run.Out.ShouldBeEmpty();
        Directory.GetFiles(Folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistingGoodFile_Skipped()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "model.onnx"), ModelBytes);
        _server.Files["/rev1/vocab.txt"] = VocabularyBytes;

        var run = await FetchAsync("test-model", "--dir", _directory);

        run.ExitCode.ShouldBe(0, run.Error);
        _server.Requested.ShouldBe(["/rev1/vocab.txt"]);
        run.Out.ShouldContain("{\"name\":\"model.onnx\",\"status\":\"present\"}");
        run.Out.ShouldContain("{\"name\":\"vocab.txt\",\"status\":\"downloaded\"}");
    }

    [Fact]
    public async Task ExistingDifferentFile_RefusedAndLeftAlone()
    {
        Directory.CreateDirectory(Folder);
        var mine = Encoding.UTF8.GetBytes("my own fine-tuned weights");
        File.WriteAllBytes(Path.Combine(Folder, "model.onnx"), mine);

        var run = await FetchAsync("test-model", "--dir", _directory);

        run.ExitCode.ShouldBe(2);
        run.Error.ShouldContain("was left alone");
        File.ReadAllBytes(Path.Combine(Folder, "model.onnx")).ShouldBe(mine);
        _server.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task ServerError_FailsAndKeepsNothing()
    {
        var run = await FetchAsync("test-model", "--dir", _directory);

        run.ExitCode.ShouldBe(1);
        Directory.GetFiles(Folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownModel_IsAUsageError()
    {
        var run = await CliRun.RunAsync("models", "fetch", "gpt-embed", "--dir", _directory);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("The model must be one of: bge-small-en-v1.5, all-minilm-l6-v2.");
        Directory.Exists(_directory).ShouldBeFalse();
    }

    /// <summary>Answers each GET with the bytes registered for its path, or 404; records the paths asked for.</summary>
    private sealed class FixedFiles : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            return Task.FromResult(Files.TryGetValue(path, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
