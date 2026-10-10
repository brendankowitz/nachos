using Nachos.Abstractions.Stores;
using Nachos.Testing;

namespace Nachos.DataLayer.InMemory.Tests;

public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override IMemoryStore CreateStore(TimeProvider clock) => new InMemoryMemoryStore(clock);
}
