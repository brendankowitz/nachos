CREATE TABLE [dbo].[Sessions]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [Name]             NVARCHAR (512) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [LifecycleState]   TINYINT        NOT NULL, -- 0 = Active, 1 = Inactive, 2 = Deleting
    [LifecycleVersion] INT            NOT NULL CONSTRAINT [DF_Sessions_LifecycleVersion] DEFAULT 0,
    [DeletionJobId]    BIGINT         NULL,
    [NextMessageSeq]   BIGINT         NOT NULL CONSTRAINT [DF_Sessions_NextMessageSeq] DEFAULT 1,
    [Metadata]         NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Sessions_Metadata] DEFAULT N'{}',
    [InternalMetadata] NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Sessions_InternalMetadata] DEFAULT N'{}',
    [Configuration]    NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Sessions_Configuration] DEFAULT N'{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Sessions] PRIMARY KEY CLUSTERED ([Id]),
    -- JSON columns hold a JSON object; each CHECK is written in the form SQL Server stores
    -- (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_Sessions_Metadata_IsJsonObject] CHECK ((isjson([Metadata],OBJECT)=(1))),
    CONSTRAINT [CK_Sessions_InternalMetadata_IsJsonObject] CHECK ((isjson([InternalMetadata],OBJECT)=(1))),
    CONSTRAINT [CK_Sessions_Configuration_IsJsonObject] CHECK ((isjson([Configuration],OBJECT)=(1))),
    CONSTRAINT [FK_Sessions_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id]),
    CONSTRAINT [UQ_Sessions_Workspace_Name] UNIQUE NONCLUSTERED ([WorkspaceId], [Name]),
    -- Alternate key: lets child tables use a composite FK that carries WorkspaceId.
    CONSTRAINT [UQ_Sessions_Workspace_Id] UNIQUE NONCLUSTERED ([WorkspaceId], [Id]),
    -- Written in the form SQL Server stores (sys.check_constraints.definition): an IN list is normalized to a reversed
    -- OR chain, and DacFx would otherwise report a drop+create of this constraint on every deploy.
    CONSTRAINT [CK_Sessions_LifecycleState] CHECK (([LifecycleState]=(2) OR [LifecycleState]=(1) OR [LifecycleState]=(0)))
);
GO

CREATE NONCLUSTERED INDEX [IX_Sessions_Workspace_CreatedAt]
    ON [dbo].[Sessions] ([WorkspaceId], [CreatedAt]);
GO

-- Serves the "EXISTS active sessions in workspace" check and the is_active list filter.
CREATE NONCLUSTERED INDEX [IX_Sessions_Workspace_State]
    ON [dbo].[Sessions] ([WorkspaceId], [LifecycleState]);
