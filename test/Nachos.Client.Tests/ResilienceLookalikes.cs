// Handler types whose names resemble the resilience package's handler. The filter removes a handler by its exact type
// full name only: the one in the package's old internal namespace is removed, the lookalikes are kept.
#pragma warning disable CA1812 // instantiated by the tests through AddHttpMessageHandler
namespace Microsoft.Extensions.Http.Resilience.Internal
{
    /// <summary>The full name the package's handler had in versions 8.0.0 and 8.1.0.</summary>
    internal sealed class ResilienceHandler : DelegatingHandler
    {
    }
}

namespace Microsoft.Extensions.Http.Resilience
{
    /// <summary>Shares the package's namespace and a prefix of the name, nothing else.</summary>
    internal sealed class ResilienceHandlerLookalike : DelegatingHandler
    {
    }
}

namespace MyApp.Http
{
    /// <summary>A caller's own handler that happens to share the short name.</summary>
    internal sealed class ResilienceHandler : DelegatingHandler
    {
    }
}

namespace X
{
    /// <summary>A caller's own handler whose name ends with the short name.</summary>
    internal sealed class NachosResilienceHandler : DelegatingHandler
    {
    }
}
#pragma warning restore CA1812
