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
    [InlineData("script-no-changes.sql")]
    [InlineData("script-missing-table.sql")]
    [InlineData("script-database-option-drift.sql")]
    [InlineData("script-grant-execute.sql")]
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

    // ---- the whole-script allowlist (non-bootstrap databases) ----

    [Theory]
    [InlineData("script-no-changes.sql")]
    [InlineData("script-missing-table.sql")]
    [InlineData("script-add-nullable-column.sql")]
    [InlineData("script-add-not-null-column-with-default.sql")]
    [InlineData("script-grant-execute.sql")]
    public void RealDacFxScripts_OfAdditiveChanges_HaveNothingOutsideTheAllowlist(string fixture)
    {
        var analysis = DeployScriptAnalysis.TryParse(Fixtures.Read(fixture))!;

        analysis.DisallowedStatements.ShouldBeEmpty();
        analysis.DatabaseOptionChanges.ShouldBeEmpty();
        analysis.HasOpaqueExecution.ShouldBeFalse();
    }

    [Fact]
    public void RealDacFxScript_OfDatabaseOptionDrift_NamesTheOptions()
    {
        var analysis = DeployScriptAnalysis.TryParse(Fixtures.Read("script-database-option-drift.sql"))!;

        // The ALTER DATABASE statements sit inside IF EXISTS ... BEGIN ... END, and are still found.
        analysis.DatabaseOptionChanges.ShouldBe(["PAGE_VERIFY", "TARGET_RECOVERY_TIME"]);
        analysis.DisallowedStatements.ShouldBeEmpty();
    }

    [Fact]
    public void RealDacFxScript_OfABootstrap_ChangesDatabaseOptions()
    {
        var analysis = DeployScriptAnalysis.TryParse(Fixtures.Read("script-empty-database.sql"))!;

        analysis.DatabaseOptionChanges.ShouldContain("READ_COMMITTED_SNAPSHOT");
    }

    [Theory]
    [InlineData("ALTER DATABASE [d] SET PAGE_VERIFY NONE WITH ROLLBACK IMMEDIATE;", "PAGE_VERIFY")]
    [InlineData("ALTER DATABASE [d] SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;", "READ_COMMITTED_SNAPSHOT")]
    [InlineData("IF 1 = 1 BEGIN ALTER DATABASE [d] SET RECOVERY SIMPLE; END", "RECOVERY")]
    [InlineData("ALTER DATABASE [d] SET QUERY_STORE = OFF;", "QUERY_STORE")]
    [InlineData("ALTER DATABASE [d] SET ANSI_NULLS ON, QUOTED_IDENTIFIER ON;", "ANSI_NULLS")]
    [InlineData("ALTER DATABASE [d] ADD FILE (NAME = N'f', FILENAME = N'/tmp/f.ndf');", "AlterDatabaseAddFileStatement")]
    public void AlterDatabase_IsReportedAsADatabaseOptionChange(string script, string expected)
    {
        var analysis = DeployScriptAnalysis.TryParse(script)!;

        analysis.DatabaseOptionChanges.ShouldContain(expected);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE [dbo].[T];", "TruncateTableStatement")]
    [InlineData("DELETE FROM [dbo].[T];", "DeleteStatement")]
    [InlineData("UPDATE [dbo].[T] SET [C] = 1;", "UpdateStatement")]
    [InlineData("INSERT INTO [dbo].[T] ([C]) VALUES (1);", "InsertStatement")]
    [InlineData("DROP INDEX [IX] ON [dbo].[T];", "DropIndexStatement")]
    [InlineData("ALTER INDEX [IX] ON [dbo].[T] REBUILD;", "AlterIndexStatement")]
    [InlineData("DBCC SHRINKFILE (1, 1);", "DbccStatement")]
    [InlineData("SELECT [C] INTO [dbo].[U] FROM [dbo].[T];", "SelectStatement")]
    [InlineData("DROP TABLE [dbo].[T];", "DropTableStatement")]
    [InlineData("DROP PROCEDURE [dbo].[P];", "DropProcedureStatement")]
    [InlineData("CREATE TRIGGER [dbo].[Tr] ON [dbo].[T] AFTER INSERT AS SELECT 1;", "CreateTriggerStatement")]
    [InlineData("GRANT SELECT ON [dbo].[T] TO [u];", "GrantStatement")]
    [InlineData("GRANT CONTROL ON OBJECT::[dbo].[F] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE, SELECT ON OBJECT::[dbo].[F] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO PUBLIC WITH GRANT OPTION;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO PUBLIC AS [dbo];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON SCHEMA::[dbo] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON [otherdb].[dbo].[F] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[sys].[sp_executesql] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON [sys].[F] TO [public];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[other].[F] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[F] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO [app];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO [app], [other];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO PUBLIC, [app];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO [guest];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] ([c]) TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON [dbo].[F] ([c]) TO [public];", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F].[G] TO PUBLIC;", "GrantStatement")]
    [InlineData("GRANT EXECUTE ON [dbo].[F].[G] TO [public];", "GrantStatement")]
    [InlineData("DENY EXECUTE ON OBJECT::[dbo].[F] TO PUBLIC;", "DenyStatement")]
    [InlineData("REVOKE EXECUTE ON OBJECT::[dbo].[F] FROM PUBLIC;", "RevokeStatement")]
    [InlineData("CREATE LOGIN [l] WITH PASSWORD = 'x';", "CreateLoginStatement")]
    [InlineData("ALTER TABLE [dbo].[T] NOCHECK CONSTRAINT [CK];", "AlterTableConstraintModificationStatement")]
    [InlineData("ALTER TABLE [dbo].[T] WITH NOCHECK CHECK CONSTRAINT [CK];", "AlterTableConstraintModificationStatement")]
    [InlineData("ALTER TABLE [dbo].[T] DROP COLUMN [C];", "AlterTableDropTableElementStatement")]
    [InlineData("ALTER TABLE [dbo].[T] ALTER COLUMN [C] BIGINT NOT NULL;", "AlterTableAlterColumnStatement")]
    [InlineData("MERGE [dbo].[Other] AS t USING (SELECT 1 AS [Id]) AS s ON t.[Id] = s.[Id] WHEN MATCHED THEN UPDATE SET [C] = 1;", "MergeStatement")]
    [InlineData("IF 1 = 1 BEGIN DELETE FROM [dbo].[T]; END", "DeleteStatement")]
    public void StatementsOutsideTheAllowlist_AreListed(string script, string expected)
    {
        var analysis = DeployScriptAnalysis.TryParse(script)!;

        analysis.DisallowedStatements.ShouldContain(expected);
    }

    [Theory]
    [InlineData("PRINT N'hello';")]
    [InlineData("USE [$(DatabaseName)];")]
    [InlineData("SET ANSI_NULLS, QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF; SET NOEXEC ON; SET XACT_ABORT ON;")]
    [InlineData("IF 1 = 1 BEGIN PRINT N'x'; SET NOEXEC ON; END")]
    [InlineData("IF EXISTS (SELECT TOP 1 1 FROM [dbo].[T]) RAISERROR (N'Rows were detected.', 16, 127) WITH NOWAIT;")]
    [InlineData("BEGIN TRANSACTION; COMMIT TRANSACTION;")]
    [InlineData("CREATE TABLE [dbo].[T] ([Id] INT NOT NULL, CONSTRAINT [PK] PRIMARY KEY CLUSTERED ([Id]));")]
    [InlineData("CREATE NONCLUSTERED INDEX [IX] ON [dbo].[T] ([Id]);")]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL;")]
    [InlineData("ALTER TABLE [dbo].[T] WITH NOCHECK ADD CONSTRAINT [FK] FOREIGN KEY ([Id]) REFERENCES [dbo].[U] ([Id]);")]
    [InlineData("ALTER TABLE [dbo].[T] WITH CHECK CHECK CONSTRAINT [FK];")]
    [InlineData("CREATE PROCEDURE [dbo].[P] AS SELECT 1;")]
    [InlineData("ALTER PROCEDURE [dbo].[P] AS SELECT 2;")]
    [InlineData("CREATE VIEW [dbo].[V] AS SELECT 1 AS [C];")]
    [InlineData("ALTER VIEW [dbo].[V] AS SELECT 2 AS [C];")]
    [InlineData("CREATE FUNCTION [dbo].[F] () RETURNS INT AS BEGIN RETURN 1; END")]
    [InlineData("EXECUTE sp_refreshsqlmodule N'[dbo].[V]';")]
    [InlineData("GRANT EXECUTE ON OBJECT::[dbo].[F] TO PUBLIC;")]
    [InlineData("GRANT EXECUTE ON [dbo].[F] TO [public];")]
    [InlineData("GRANT EXECUTE ON OBJECT::[DBO].[F] TO [PUBLIC];")]
    [InlineData("MERGE [dbo].[SchemaVersion] AS target USING (SELECT CAST(1 AS TINYINT) AS [Id], 1 AS [Version]) AS source ON target.[Id] = source.[Id] WHEN MATCHED AND target.[Version] < source.[Version] THEN UPDATE SET [Version] = source.[Version] WHEN NOT MATCHED THEN INSERT ([Id], [Version]) VALUES (source.[Id], source.[Version]);")]
    public void AdditiveScaffolding_IsNotFlagged(string script)
    {
        var analysis = DeployScriptAnalysis.TryParse(script)!;

        analysis.DisallowedStatements.ShouldBeEmpty();
        analysis.DatabaseOptionChanges.ShouldBeEmpty();
    }

    [Fact]
    public void ExecuteOfAnUnknownProcedure_IsOpaque()
    {
        DeployScriptAnalysis.TryParse("EXECUTE [dbo].[Mystery];")!.HasOpaqueExecution.ShouldBeTrue();
    }

    // ---- hardening of the names the allowlist trusts ----

    private const string StampBody = "USING (SELECT CAST(1 AS TINYINT) AS [Id], 1 AS [Version]) AS s ON t.[Id] = s.[Id] WHEN MATCHED THEN UPDATE SET [Version] = s.[Version]";

    [Theory]
    [InlineData("USE [master];")]
    [InlineData("USE [nachos];")]
    [InlineData("USE [$(Other)];")]
    public void UseOfAnythingButTheDeployVariable_IsDisallowed(string script)
    {
        DeployScriptAnalysis.TryParse(script)!.DisallowedStatements.ShouldContain("UseStatement");
    }

    [Theory]
    [InlineData("MERGE [SchemaVersion] AS t " + StampBody + ";")]
    [InlineData("MERGE [dbo].[SchemaVersion] AS t " + StampBody + ";")]
    [InlineData("MERGE [DBO].[schemaversion] AS t " + StampBody + ";")]
    public void StampMerge_OnThisDatabasesSchemaVersion_IsAllowed(string script)
    {
        DeployScriptAnalysis.TryParse(script)!.DisallowedStatements.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MERGE [otherdb].[dbo].[SchemaVersion] AS t " + StampBody + ";")]
    [InlineData("MERGE [linked].[otherdb].[dbo].[SchemaVersion] AS t " + StampBody + ";")]
    [InlineData("MERGE [other].[SchemaVersion] AS t " + StampBody + ";")]
    [InlineData("MERGE [dbo].[SchemaVersion] AS t " + StampBody + " OUTPUT inserted.[Id] INTO [dbo].[Elsewhere] ([Id]);")]
    [InlineData("MERGE [dbo].[SchemaVersion] AS t " + StampBody + " OUTPUT inserted.[Id];")]
    [InlineData("WITH [SchemaVersion] AS (SELECT CAST(1 AS TINYINT) AS [Id], 1 AS [Version]) MERGE [SchemaVersion] AS t " + StampBody + ";")]
    [InlineData("MERGE [dbo].[SchemaVersion] AS t USING (SELECT 1 AS [Id]) AS s ON t.[Id] = s.[Id] WHEN MATCHED THEN DELETE;")]
    public void StampMerge_ThatIsNotExactlyTheStamp_IsDisallowed(string script)
    {
        DeployScriptAnalysis.TryParse(script)!.DisallowedStatements.ShouldContain("MergeStatement");
    }

    [Theory]
    [InlineData("EXECUTE sp_refreshsqlmodule N'[dbo].[V]';")]
    [InlineData("EXEC [sys].[sp_refreshsqlmodule] N'[dbo].[V]';")]
    [InlineData("EXEC [SYS].[sp_refreshview] N'[dbo].[V]';")]
    public void KnownProcedure_InTheSysSchema_IsNotOpaque(string script)
    {
        DeployScriptAnalysis.TryParse(script)!.HasOpaqueExecution.ShouldBeFalse();
    }

    [Theory]
    [InlineData("EXECUTE [linked].[otherdb].[dbo].[sp_refreshsqlmodule] N'x';")]
    [InlineData("EXECUTE [linked].[otherdb].[sys].[sp_refreshsqlmodule] N'x';")]
    [InlineData("EXECUTE [otherdb].[sys].[sp_refreshsqlmodule] N'x';")]
    [InlineData("EXECUTE [dbo].[sp_refreshsqlmodule] N'x';")]
    [InlineData("EXECUTE [other].[sp_refreshview] N'x';")]
    [InlineData("DECLARE @p sysname = N'sp_refreshsqlmodule'; EXECUTE @p N'x';")]
    public void KnownProcedureName_ThatIsNotTheSystemOne_IsOpaque(string script)
    {
        DeployScriptAnalysis.TryParse(script)!.HasOpaqueExecution.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 0;", "DATABASE SCOPED CONFIGURATION MAXDOP")]
    [InlineData("ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = ON;", "DATABASE SCOPED CONFIGURATION LEGACYCARDINALITYESTIMATE")]
    [InlineData("IF 1 = 1 BEGIN ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 8; END", "DATABASE SCOPED CONFIGURATION MAXDOP")]
    [InlineData("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", "AlterDatabaseScopedConfigurationClearStatement")]
    public void AlterDatabaseScopedConfiguration_IsReportedAsADatabaseOptionChange(string script, string expected)
    {
        var analysis = DeployScriptAnalysis.TryParse(script)!;

        analysis.DatabaseOptionChanges.ShouldContain(expected);
        analysis.DisallowedStatements.ShouldBeEmpty();
    }
}
