namespace Nachos.Testing;

/// <summary>
/// The xunit collection every derivation of <see cref="WorkQueueContractTests"/> and <see cref="MemoryContractTests"/>
/// must join. The work queue spans workspaces, so those suites must not run in parallel with each other on a shared store.
/// </summary>
/// <remarks>
/// xunit only reads collection definitions from the test assembly itself, so each provider's test project declares
/// <c>[CollectionDefinition(M2StoreContracts.Name, DisableParallelization = true)]</c> on a class of its own.
/// </remarks>
public static class M2StoreContracts
{
    public const string Name = "M2 store contracts";
}
