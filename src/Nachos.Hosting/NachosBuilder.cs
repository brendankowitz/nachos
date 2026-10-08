using Microsoft.Extensions.DependencyInjection;

namespace Nachos.Hosting;

/// <summary>Provider extensions register their stores through <see cref="Services"/>.</summary>
public sealed class NachosBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
}
