CREATE TABLE [dbo].[PrincipalGrants]
(
    [Id]          BIGINT        IDENTITY (1, 1) NOT NULL,
    [ObjectId]    NVARCHAR (64) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL, -- Entra object id
    [WorkspaceId] BIGINT        NULL,                                         -- NULL = grant applies to all workspaces
    [Role]        NVARCHAR (32) NOT NULL,

    CONSTRAINT [PK_PrincipalGrants] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_PrincipalGrants_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id]),
    -- An ordinary (unfiltered) unique constraint is deliberate: SQL Server treats NULLs as equal in
    -- unique indexes, so at most one workspace-wide (WorkspaceId IS NULL) grant exists per
    -- (ObjectId, Role), and per-workspace grants are unique per (ObjectId, WorkspaceId, Role).
    -- A filtered index would not be needed and would restrict plan usage.
    CONSTRAINT [UQ_PrincipalGrants_Object_Workspace_Role] UNIQUE NONCLUSTERED ([ObjectId], [WorkspaceId], [Role])
);