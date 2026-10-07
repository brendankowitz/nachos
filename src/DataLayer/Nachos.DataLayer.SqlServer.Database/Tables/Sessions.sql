CREATE TABLE [dbo].[Sessions]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [Name]             NVARCHAR (512) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [LifecycleState]   TINYINT        NOT NULL, -- 0 = Active, 1 = Inactive, 2 = Deleting
    [LifecycleVersion] INT            NOT NULL CONSTRAINT [DF_Sessions_LifecycleVersion] DEFAULT 0,
    [DeletionJobId]    BIGINT         NULL,
    [NextMessageSeq]   BIGINT         NOT NULL CONSTRAINT [DF_Sessions_NextMessageSeq] DEFAULT 1,
    [Metadata]         JSON           NOT NULL CONSTRAINT [DF_Sessions_Metadata] DEFAULT '{}',
    [InternalMetadata] JSON           NOT NULL CONSTRAINT [DF_Sessions_InternalMetadata] DEFAULT '{}',
    [Configuration]    JSON           NOT NULL CONSTRAINT [DF_Sessions_Configuration] DEFAULT '{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Sessions] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Sessions_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id]),
    CONSTRAINT [UQ_Sessions_Workspace_Name] UNIQUE NONCLUSTERED ([WorkspaceId], [Name]),
    -- Alternate key: lets child tables use a composite FK that carries WorkspaceId.
    CONSTRAINT [UQ_Sessions_Workspace_Id] UNIQUE NONCLUSTERED ([WorkspaceId], [Id])
);
GO

CREATE NONCLUSTERED INDEX [IX_Sessions_Workspace_CreatedAt]
    ON [dbo].[Sessions] ([WorkspaceId], [CreatedAt]);