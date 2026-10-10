using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nachos.Abstractions;
using Nachos.Abstractions.Stores;

namespace Nachos.Api.Health;

internal sealed class StoreReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        await store.Workspaces.ListAsync(null, new PageRequest(Size: 1), cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
