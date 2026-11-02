using Nachos.Abstractions.Stores;
using Nachos.Testing;
using Xunit;

namespace Nachos.DataLayer.InMemory.Tests;

[Collection(M2StoreContracts.Name)]
public sealed class InMemoryMemoryContractTests : MemoryContractTests
{
    protected override IMemoryStore CreateStore(TimeProvider clock) => new InMemoryMemoryStore(clock);
}
