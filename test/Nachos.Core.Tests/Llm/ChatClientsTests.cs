using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Azure.Core;
using Nachos.Core.Llm;
using Nachos.Testing;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests.Llm;

public sealed class ChatClientsTests
{
    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "hi")];

    [Fact]
    public async Task AgentProfile_AppliesProfileOptions_AndFallsBackAlongTheChain()
    {
        var primary = new ScriptedChatClient().EnqueueFailure(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests));
        var backup = new ScriptedChatClient().EnqueueText("backup answer");
        var options = new LlmOptions
        {
            Profiles =
            {
                ["default"] = new ModelProfile { Model = "unused" },
                ["fast"] = new ModelProfile { Model = "mini", MaxOutputTokens = 256, Temperature = 0.2f, Fallback = "big" },
                ["big"] = new ModelProfile { Model = "large", MaxOutputTokens = 1024 },
            },
            Agents = { ["deriver"] = "fast" },
        };
        using var clients = new NachosChatClients(options, profile => profile.Model == "mini" ? primary : backup);

        var response = await clients.ForAgent("deriver").GetResponseAsync(Prompt, cancellationToken: CancellationToken.None);

        response.Text.ShouldBe("backup answer");
        primary.Requests.ShouldHaveSingleItem().Options!.MaxOutputTokens.ShouldBe(256);
        backup.Requests.ShouldHaveSingleItem().Options!.MaxOutputTokens.ShouldBe(1024);
    }

    [Fact]
    public void UnmappedAgent_UsesDefaultProfile_AndMissingProfileFailsClearly()
    {
        var provider = new ScriptedChatClient();
        using var withDefault = new NachosChatClients(
            new LlmOptions { Profiles = { ["default"] = new ModelProfile() } }, _ => provider);
        using var withoutDefault = new NachosChatClients(new LlmOptions(), _ => provider);

        withDefault.ForAgent("anything").ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => withoutDefault.ForAgent("anything")).Message.ShouldContain("Llm:Profiles:default");
    }

    [Fact]
    public void FallbackCycle_Throws()
    {
        var options = new LlmOptions
        {
            Profiles =
            {
                ["default"] = new ModelProfile { Fallback = "other" },
                ["other"] = new ModelProfile { Fallback = "default" },
            },
        };
        using var clients = new NachosChatClients(options, _ => new ScriptedChatClient());

        Should.Throw<InvalidOperationException>(() => clients.ForAgent("x")).Message.ShouldContain("twice");
    }

    [Fact]
    public async Task UseChatClientAndEmbeddings_RegisterThroughTheBuilder()
    {
        var provider = new ScriptedChatClient().EnqueueText("ok");
        var services = new ServiceCollection();
        services.AddOptions<LlmOptions>().Configure(o => o.Profiles["default"] = new ModelProfile());
        services.AddNachos(nachos => nachos
            .UseChatClient((_, _) => provider)
            .UseEmbeddingGenerator(_ => new DeterministicEmbeddingGenerator()));
        await using var root = services.BuildServiceProvider();

        var chat = root.GetRequiredService<NachosChatClients>().ForAgent("deriver");
        (await chat.GetResponseAsync(Prompt, cancellationToken: CancellationToken.None)).Text.ShouldBe("ok");
        root.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().ShouldBeOfType<DeterministicEmbeddingGenerator>();
    }

    [Fact]
    public async Task RequestDimensions_AreAppliedToEmbeddingRequests()
    {
        var services = new ServiceCollection();
        services.AddOptions<EmbeddingOptions>().Configure(o => o.RequestDimensions = 1536);
        services.AddNachos(nachos => nachos.UseEmbeddingGenerator(_ => new DeterministicEmbeddingGenerator(dimensions: 8)));
        await using var root = services.BuildServiceProvider();

        var embeddings = await root.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GenerateAsync(["x"], cancellationToken: CancellationToken.None);

        embeddings[0].Vector.Length.ShouldBe(1536);
    }

    [Fact]
    public async Task UseAzureOpenAI_BuildsClientsFromOptions_WithoutNetwork()
    {
        var services = new ServiceCollection();
        services.AddOptions<LlmOptions>().Configure(o => o.Profiles["default"] = new ModelProfile());
        services.AddNachos(nachos => nachos.UseAzureOpenAI(Substitute.For<TokenCredential>(), azure =>
        {
            azure.Endpoint = new Uri("https://contoso.openai.azure.com/");
            azure.ChatDeployment = "gpt-chat";
            azure.EmbeddingDeployment = "embed";
        }));
        await using var root = services.BuildServiceProvider();

        root.GetRequiredService<NachosChatClients>().ForAgent("deriver").ShouldNotBeNull();
        root.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().ShouldNotBeNull();
    }

    [Fact]
    public async Task UseAzureOpenAI_MissingEndpoint_FailsWithActionableMessage()
    {
        var services = new ServiceCollection();
        services.AddOptions<LlmOptions>().Configure(o => o.Profiles["default"] = new ModelProfile());
        services.AddNachos(nachos => nachos.UseAzureOpenAI(Substitute.For<TokenCredential>(), azure => azure.ChatDeployment = "gpt-chat"));
        await using var root = services.BuildServiceProvider();

        var error = Should.Throw<InvalidOperationException>(() => root.GetRequiredService<NachosChatClients>().ForAgent("deriver"));

        error.Message.ShouldContain("AzureOpenAI:Endpoint");
    }
}
