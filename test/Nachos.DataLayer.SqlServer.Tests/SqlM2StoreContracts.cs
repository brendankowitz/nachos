using Nachos.Testing;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// The non-parallel collection of the M2 store suites (<see cref="M2StoreContracts"/>): the work queue spans workspaces,
/// so a suite that claims whatever is due must not share its database with a concurrently running one. It runs after the
/// parallel collections finish (xunit runs non-parallel collections last), on the container they share.
/// </summary>
[CollectionDefinition(M2StoreContracts.Name, DisableParallelization = true)]
public sealed class SqlM2StoreContracts : ICollectionFixture<SqlServerFixture>;
