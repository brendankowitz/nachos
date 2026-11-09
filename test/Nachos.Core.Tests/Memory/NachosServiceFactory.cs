using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Context;
using Nachos.Core.Keys;
using Nachos.Core.Memory;
using Nachos.Core.Search;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;

namespace Nachos.Core.Tests.Memory;

/// <summary>Builds a <see cref="NachosService"/> by hand, with memory services over the same store and no embedding generator.</summary>
internal static class NachosServiceFactory
{
    public static NachosService Create(IMemoryStore store, ITokenCounter counter, RequestValidator validator, IKeyIssuer issuer)
    {
        var peerContext = new PeerContextService(store, new RepresentationBuilder(store));
        return new(store, counter, validator, issuer,
            new ConclusionService(store, NullLogger<ConclusionService>.Instance),
            peerContext,
            new ConfigurationResolver(Options.Create(new NachosOptions()), counter),
            new HybridSearchService(store),
            new SessionContextService(store, peerContext, counter));
    }
}
