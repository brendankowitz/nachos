using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions.Stores;
using Nachos.Core.Keys;
using Nachos.Core.Memory;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;

namespace Nachos.Core.Tests.Memory;

/// <summary>Builds a <see cref="NachosService"/> by hand, with memory services over the same store and no embedding generator.</summary>
internal static class NachosServiceFactory
{
    public static NachosService Create(IMemoryStore store, ITokenCounter counter, RequestValidator validator, IKeyIssuer issuer)
    {
        return new(store, counter, validator, issuer,
            new ConclusionService(store, NullLogger<ConclusionService>.Instance),
            new PeerContextService(store, new RepresentationBuilder(store)));
    }
}
