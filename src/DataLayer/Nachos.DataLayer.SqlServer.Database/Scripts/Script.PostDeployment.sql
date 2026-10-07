/*
Post-deployment script: stamps the single SchemaVersion row.
Idempotent: safe to run on every deploy.

The version literal below must equal SchemaInfo.CurrentVersion in
Nachos.DataLayer.SqlServer (asserted by SchemaDeployerTests.PostDeployVersion_MatchesSchemaInfo).
*/
MERGE [dbo].[SchemaVersion] AS target
USING (SELECT CAST(1 AS TINYINT) AS [Id], 1 AS [Version]) AS source -- SCHEMA VERSION LITERAL: keep in sync with SchemaInfo.CurrentVersion
ON target.[Id] = source.[Id]
WHEN MATCHED THEN
    UPDATE SET [Version] = source.[Version], [AppliedAt] = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT ([Id], [Version], [AppliedAt]) VALUES (source.[Id], source.[Version], SYSUTCDATETIME());