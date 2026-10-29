using Nachos.Abstractions.Stores;
using Nachos.Testing;

namespace Nachos.DataLayer.InMemory.Tests;

public sealed class InMemoryWorkQueueContractTests : WorkQueueContractTests
{
    protected override IMemoryStore CreateStore(TimeProvider clock) => new InMemoryMemoryStore(clock);
}
