using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nachos.Core.Configuration;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosConfiguredAIExtensions
{
    /// <summary>
    /// Chooses the AI backend from configuration, the same way for every Nachos host. Azure OpenAI
    /// (<see cref="NachosAzureOpenAIExtensions.UseAzureOpenAI"/>, <c>DefaultAzureCredential</c>, never a key) is used only when
    /// <c>AzureOpenAI:Endpoint</c> is set and <c>Nachos:Ai:Mode</c> is not <c>Fake</c>; otherwise the offline fake AI is used
    /// (<see cref="NachosDeriverBuilderExtensions.UseFakeAI"/>), which is not a memory-quality mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Falling back to the fake AI (no endpoint and no mode) is allowed only in the Development environment: anywhere
    /// else the host fails at startup unless <c>Nachos:Ai:Mode=Fake</c> asks for it explicitly. Either way the fake AI
    /// logs a warning at startup.
    /// </para>
    /// <para>
    /// With the fake AI the Deriver is made eager (any pending message is due at once) so that a demo or smoke run turns a
    /// posted message into conclusions without waiting for 512 tokens or 30 minutes. Explicit <c>Nachos:Deriver</c>
    /// settings still win, because the host binds them after this registration.
    /// </para>
    /// </remarks>
    public static NachosBuilder UseConfiguredAI(this NachosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        var forcedFake = string.Equals(configuration["Nachos:Ai:Mode"], "Fake", StringComparison.OrdinalIgnoreCase);
        if (!forcedFake && !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"]))
        {
            return builder.UseAzureOpenAI(new DefaultAzureCredential(), _ => { });
        }

        builder.Services.Configure<NachosOptions>(options =>
        {
            options.Deriver.EligibilityTokens = 1;
            options.Deriver.MaxAge = TimeSpan.Zero;
        });
        var selection = forcedFake ? FakeAiSelection.Explicit : FakeAiSelection.Fallback;
        // Registered before UseFakeAI's own hosted services, so a refused fallback fails the host before anything embeds.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, FakeAiStartupCheck>(
            provider => ActivatorUtilities.CreateInstance<FakeAiStartupCheck>(provider, selection)));
        return builder.UseFakeAI();
    }
}
