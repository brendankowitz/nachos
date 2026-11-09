using Microsoft.Extensions.DependencyInjection.Extensions;
using Nachos.Core.Summarizer;
using Nachos.Core.Worker;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosSummarizerBuilderExtensions
{
    /// <summary>
    /// Registers the Summarizer as the scoped <see cref="IWorkHandler"/> for summary work. It needs a chat client
    /// (<c>UseChatClient</c>, <c>UseAzureOpenAI</c> or <c>UseFakeAI</c>).
    /// </summary>
    public static NachosBuilder AddSummarizer(this NachosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkHandler, SummarizerService>());
        return builder;
    }
}
