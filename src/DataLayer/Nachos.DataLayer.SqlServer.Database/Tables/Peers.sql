CREATE TABLE [dbo].[Peers]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [Name]             NVARCHAR (512) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [IsInternal]       BIT            NOT NULL CONSTRAINT [DF_Peers_IsInternal] DEFAULT 0,
    [Metadata]         JSON           NOT NULL CONSTRAINT [DF_Peers_Metadata] DEFAULT '{}',
    [InternalMetadata] JSON           NOT NULL CONSTRAINT [DF_Peers_InternalMetadata] DEFAULT '{}',
    [Configuration]    JSON           NOT NULL CONSTRAINT [DF_Peers_Configuration] DEFAULT '{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Peers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Peers_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id]),
    CONSTRAINT [UQ_Peers_Workspace_Name] UNIQUE NONCLUSTERED ([WorkspaceId], [Name]),
    -- Alternate key: lets child tables use a composite FK that carries WorkspaceId.
    CONSTRAINT [UQ_Peers_Workspace_Id] UNIQUE NONCLUSTERED ([WorkspaceId], [Id])
);
GO

CREATE NONCLUSTERED INDEX [IX_Peers_Workspace_CreatedAt]
    ON [dbo].[Peers] ([WorkspaceId], [CreatedAt]);