using Nachos.Abstractions.Schema;
using Nachos.DataLayer.SqlServer.Schema;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>ScriptDom reading of DacFx-generated deploy scripts (captured from DacFx; the rest hand-written).</summary>
public sealed class DeployScriptAnalysisTests
{
    [Theory]
    [InlineData("script-empty-database.sql")]
    [InlineData("script-add-nullable-column.sql")]
    [InlineData("script-add-not-null-column-with-default.sql")]
    [InlineData("script-add-not-null-column-without-default.sql")]
    [InlineData("script-alter-column-nullability.sql")]
    public void RealDacFxScripts_Parse(string fixture)
    {
        // The whole schema (json columns, constraints, indexes) and the SQLCMD wrapper must parse, or every table change would be unclassifiable.
        DeployScriptAnalysis.TryParse(Fixtures.Read(fixture)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("script-add-nullable-column.sql", "[dbo].[SessionPeers]", DeployClassification.AutoSafe)]
    [InlineData("script-add-not-null-column-with-default.sql", "[dbo].[SchemaVersion]", DeployClassification.AutoSafe)]
    [InlineData("script-add-not-null-column-without-default.sql", "[dbo].[IdempotencyRecords]", DeployClassification.Unsafe)]
    [InlineData("script-alter-column-nullability.sql", "[dbo].[SessionPeers]", DeployClassification.Unsafe)]
    [InlineData("script-add-nullable-column.sql", "[DBO].[sessionpeers]", DeployClassification.AutoSafe)]
    [InlineData("script-add-nullable-column.sql", "[dbo].[Peers]", DeployClassification.Unclassifiable)]
    public void ClassifiesTheTableTheScriptChanges(string fixture, string table, DeployClassification expected)
    {
        DeployScriptAnalysis.TryParse(Fixtures.Read(fixture))!.ClassifyTable(table).ShouldBe(expected);
    }

    [Theory]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL;", DeployClassification.AutoSafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT CONSTRAINT [DF] DEFAULT 0 NOT NULL;", DeployClassification.AutoSafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL, [D] INT CONSTRAINT [DF] DEFAULT 0 NOT NULL;", DeployClassification.AutoSafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NOT NULL;", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT;", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL, [D] INT NOT NULL;", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT IDENTITY (1, 1) NOT NULL CONSTRAINT [DF] DEFAULT 0;", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL CHECK ([C] > 0);", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL UNIQUE;", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] AS ([D] + 1);", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD CONSTRAINT [PK] PRIMARY KEY ([Id]);", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; ALTER TABLE [dbo].[T] ALTER COLUMN [D] BIGINT NOT NULL;", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; ALTER TABLE [dbo].[T] DROP COLUMN [D];", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; DROP TABLE [dbo].[T];", DeployClassification.Unsafe)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; EXECUTE sp_rename N'[dbo].[T]', N'U';", DeployClassification.Unclassifiable)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; EXECUTE ('ALTER TABLE T DROP COLUMN D');", DeployClassification.Unclassifiable)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; EXECUTE sp_executesql N'SELECT 1';", DeployClassification.Unclassifiable)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL; EXECUTE sp_refreshsqlmodule N'[dbo].[V]';", DeployClassification.AutoSafe)]
    [InlineData("SELECT 1;", DeployClassification.Unclassifiable)]
    public void ClassifiesHandWrittenScripts(string script, DeployClassification expected)
    {
        DeployScriptAnalysis.TryParse(script)!.ClassifyTable("[dbo].[T]").ShouldBe(expected);
    }

    [Fact]
    public void BatchesAndSqlcmdDirectivesAreSplitOut()
    {
        const string script = ":setvar DatabaseName \"x\"\nGO\n:on error exit\nGO\nALTER TABLE [dbo].[T] ADD [C] INT NULL;\nGO\nPRINT N'done';\nGO\n";

        DeployScriptAnalysis.TryParse(script)!.ClassifyTable("[dbo].[T]").ShouldBe(DeployClassification.AutoSafe);
    }

    [Fact]
    public void UnparseableScript_IsNull()
    {
        DeployScriptAnalysis.TryParse("ALTER TABLE ??? ADD").ShouldBeNull();
    }
}