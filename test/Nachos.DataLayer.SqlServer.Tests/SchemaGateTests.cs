using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The gate's decision table, against a scripted <see cref="ISchemaManager"/> (no database).</summary>
public sealed class SchemaGateTests
{
    private sealed class ScriptedSchema(SchemaState state, DeployClassification classification) : ISchemaManager
    {
        public int StatusCalls { get; private set; }
        public int Deploys { get; private set; }
        public Exception? FailStatusOnce { get; set; }

        public Task<SchemaStatus> GetStatusAsync(CancellationToken ct)
        {
            StatusCalls++;
            if (FailStatusOnce is { } failure)
            {
                FailStatusOnce = null;
                throw failure;
            }

            // Once deployed, the database reads as current.
            var effective = Deploys > 0 ? SchemaState.Current : state;
            return Task.FromResult(new SchemaStatus("Sql2025", effective == SchemaState.Empty ? null : 0, SchemaInfo.CurrentVersion, effective));
        }

        public Task<SchemaReport> ReportAsync(CancellationToken ct) => Task.FromResult(new SchemaReport(classification, "<r/>"));

        public Task<SchemaReport> DeployAsync(bool allowDataLoss, CancellationToken ct)
        {
            allowDataLoss.ShouldBeFalse("the gate must never allow data loss");
            Deploys++;
            return ReportAsync(ct);
        }
    }

    private static SchemaGate Gate(ISchemaManager schema, bool automatic) =>
        new(schema, new SqlServerOptions { AutomaticSchemaDeploymentEnabled = automatic });

    [Fact]
    public async Task Current_IsNoOp_EvenWhenEnabled()
    {
        var schema = new ScriptedSchema(SchemaState.Current, DeployClassification.AutoSafe);

        await Gate(schema, automatic: true).EnsureAsync(default);

        schema.Deploys.ShouldBe(0);
    }

    [Fact]
    public async Task Empty_Enabled_Deploys()
    {
        var schema = new ScriptedSchema(SchemaState.Empty, DeployClassification.AutoSafe);

        await Gate(schema, automatic: true).EnsureAsync(default);

        schema.Deploys.ShouldBe(1);
    }

    [Fact]
    public async Task Behind_Enabled_AutoSafe_Deploys()
    {
        var schema = new ScriptedSchema(SchemaState.Behind, DeployClassification.AutoSafe);

        await Gate(schema, automatic: true).EnsureAsync(default);

        schema.Deploys.ShouldBe(1);
    }

    [Theory]
    [InlineData(SchemaState.Empty)]
    [InlineData(SchemaState.Behind)]
    [InlineData(SchemaState.Unstamped)]
    [InlineData(SchemaState.Ahead)]
    public async Task Disabled_RefusesEveryStateButCurrent_NamingTheRemedy(SchemaState state)
    {
        var schema = new ScriptedSchema(state, DeployClassification.AutoSafe);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(schema, automatic: false).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        schema.Deploys.ShouldBe(0);
    }

    [Theory]
    [InlineData(DeployClassification.Unsafe)]
    [InlineData(DeployClassification.Unclassifiable)]
    public async Task Behind_Enabled_NotAutoSafe_Refuses(DeployClassification classification)
    {
        var schema = new ScriptedSchema(SchemaState.Behind, classification);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(schema, automatic: true).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        schema.Deploys.ShouldBe(0);
    }

    [Theory]
    [InlineData(SchemaState.Unstamped)]
    [InlineData(SchemaState.Ahead)]
    public async Task UnstampedAndAhead_AreNeverChanged_EvenWhenEnabled(SchemaState state)
    {
        var schema = new ScriptedSchema(state, DeployClassification.AutoSafe);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(schema, automatic: true).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        schema.Deploys.ShouldBe(0);
    }

    [Fact]
    public async Task Success_IsCached_FailureIsNot()
    {
        var schema = new ScriptedSchema(SchemaState.Current, DeployClassification.AutoSafe)
        {
            FailStatusOnce = new TimeoutException("database unreachable"),
        };
        var gate = Gate(schema, automatic: false);

        await Should.ThrowAsync<TimeoutException>(() => gate.EnsureAsync(default));
        await gate.EnsureAsync(default);
        var callsAfterRecovery = schema.StatusCalls;
        await gate.EnsureAsync(default);

        schema.StatusCalls.ShouldBe(callsAfterRecovery);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneDeploy()
    {
        var schema = new ScriptedSchema(SchemaState.Empty, DeployClassification.AutoSafe);
        var gate = Gate(schema, automatic: true);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => gate.EnsureAsync(default)));

        schema.Deploys.ShouldBe(1);
    }
}