using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Worker;

/// <summary>The batch policy of each registered <see cref="IWorkHandler"/>, by task type.</summary>
internal static class WorkHandlerPolicies
{
    /// <summary>Reads each handler's policy from a scope of its own (handlers may be scoped).</summary>
    /// <exception cref="InvalidOperationException">Two handlers are registered for one task type.</exception>
    public static async Task<Dictionary<WorkTaskType, WorkBatchPolicy>> ReadAsync(IServiceScopeFactory scopes)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var policies = new Dictionary<WorkTaskType, WorkBatchPolicy>();
            foreach (var handler in scope.ServiceProvider.GetServices<IWorkHandler>())
            {
                if (!policies.TryAdd(handler.TaskType, handler.BatchPolicy))
                {
                    throw new InvalidOperationException(
                        $"More than one IWorkHandler is registered for task type {handler.TaskType}; register exactly one per type.");
                }
            }

            return policies;
        }
    }
}
