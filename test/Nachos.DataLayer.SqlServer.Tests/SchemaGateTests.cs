using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>The gate's decision table, against a scripted <see cref="ISchemaManager"/> (no database).</summary>
public sealed class SchemaGateTests
{
    private sealed class ScriptedSchema(SchemaState state, DeployClassification classification) : ISchemaManager
    {
        private bool _applied;

        public int StatusCalls { get; private set; }

        public int Deploys { get; private set; }

        public Exception? FailStatusOnce { get; set; }

        public TaskCompletionSource? HoldFirstStatus { get; set; }

        /// <summary>True when the database still differs from the model after a deploy was applied.</summary>
        public bool PendingChangesAfterDeploy { get; set; }

        public string[] Reasons { get; set; } = [];

        public Task<SchemaStatus> GetStatusAsync(CancellationToken ct)
        {
            StatusCalls++;
            if (FailStatusOnce is { } failure)
            {
                FailStatusOnce = null;
                throw failure;
            }

            var hold = HoldFirstStatus;
            HoldFirstStatus = null;
            return hold is null ? Task.FromResult(Status()) : HoldAsync(hold);
        }

        public Task<SchemaReport> ReportAsync(CancellationToken ct) => Task.FromResult(Report(applied: false));

        public Task<SchemaReport> DeployAsync(DeployApproval approval, bool allowDataLoss, bool adoptUnstamped, CancellationToken ct)
        {
            approval.ShouldBe(DeployApproval.AutoSafeOnly, "the gate must never apply a change nobody reviewed");
            allowDataLoss.ShouldBeFalse("the gate must never allow data loss");
            adoptUnstamped.ShouldBeFalse("the gate must never adopt a database");
            Deploys++;

            // Mirrors the real deployer: only an auto-safe change is applied, anything else comes back unapplied.
            _applied = classification == DeployClassification.AutoSafe;
            return Task.FromResult(Report(_applied));
        }

        private async Task<SchemaStatus> HoldAsync(TaskCompletionSource hold)
        {
            await hold.Task;
            return Status();
        }

        private SchemaStatus Status()
        {
            var effective = _applied ? SchemaState.Current : state;
            return new SchemaStatus("Sql2025", effective == SchemaState.Empty ? null : 0, SchemaInfo.CurrentVersion, effective);
        }

        private SchemaReport Report(bool applied) =>
            new(classification, "<r/>", applied, HasPendingChanges: _applied ? PendingChangesAfterDeploy : state != SchemaState.Current, Reasons);
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
    public async Task Behind_Enabled_NotAutoSafe_Refuses_AndSaysWhy(DeployClassification classification)
    {
        var schema = new ScriptedSchema(SchemaState.Behind, classification) { Reasons = ["Database option READ_COMMITTED_SNAPSHOT is OFF."] };

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Gate(schema, automatic: true).EnsureAsync(default));

        failure.Message.ShouldContain("nachos schema upgrade");
        failure.Message.ShouldContain(classification.ToString());
        failure.Message.ShouldContain("READ_COMMITTED_SNAPSHOT");
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
    public async Task Gate_PostDeployReReportMustBeEmpty()
    {
        // The stamp says current, but the schema still differs from the model: a half-applied or silently failed deploy.
        var schema = new ScriptedSchema(SchemaState.Behind, DeployClassification.AutoSafe) { PendingChangesAfterDeploy = true };
        var gate = Gate(schema, automatic: true);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => gate.EnsureAsync(default));
        failure.Message.ShouldContain("nachos schema upgrade");
        failure.Message.ShouldContain("still differs");
        schema.Deploys.ShouldBe(1);
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
    public async Task FailureAfterEveryCallerCancelled_IsNotServedToTheNextCall()
    {
        var hold = new TaskCompletionSource();
        var schema = new ScriptedSchema(SchemaState.Current, DeployClassification.AutoSafe) { HoldFirstStatus = hold };
        var gate = Gate(schema, automatic: false);
        using var cancelled = new CancellationTokenSource();

        var first = gate.EnsureAsync(cancelled.Token);
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        // The verification keeps running with nobody waiting, then fails.
        hold.SetException(new TimeoutException("database unreachable"));
        await Task.Delay(300);

        // A call that arrives after that starts afresh instead of replaying a failure nobody was waiting for.
        await gate.EnsureAsync(default);
        schema.StatusCalls.ShouldBe(2);
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