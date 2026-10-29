using Nachos.Abstractions.Stores;
using Nachos.Testing;

namespace Nachos.DataLayer.InMemory.Tests;

public sealed class InMemoryMemoryContractTests : MemoryContractTests
{
    protected override IMemoryStore CreateStore(TimeProvider clock) => new InMemoryMemoryStore(clock);
}
