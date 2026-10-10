CREATE TABLE [dbo].[Peers]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [Name]             NVARCHAR (512) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [IsInternal]       BIT            NOT NULL CONSTRAINT [DF_Peers_IsInternal] DEFAULT 0,
    [Metadata]         NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Peers_Metadata] DEFAULT N'{}',
    [InternalMetadata] NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Peers_InternalMetadata] DEFAULT N'{}',
    [Configuration]    NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Peers_Configuration] DEFAULT N'{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Peers] PRIMARY KEY CLUSTERED ([Id]),
    -- JSON columns hold a JSON object; each CHECK is written in the form SQL Server stores
    -- (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_Peers_Metadata_IsJsonObject] CHECK ((isjson([Metadata],OBJECT)=(1))),
    CONSTRAINT [CK_Peers_InternalMetadata_IsJsonObject] CHECK ((isjson([InternalMetadata],OBJECT)=(1))),
    CONSTRAINT [CK_Peers_Configuration_IsJsonObject] CHECK ((isjson([Configuration],OBJECT)=(1))),
    CONSTRAINT [FK_Peers_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id]),
    CONSTRAINT [UQ_Peers_Workspace_Name] UNIQUE NONCLUSTERED ([WorkspaceId], [Name]),
    -- Alternate key: lets child tables use a composite FK that carries WorkspaceId.
    CONSTRAINT [UQ_Peers_Workspace_Id] UNIQUE NONCLUSTERED ([WorkspaceId], [Id])
);
GO

CREATE NONCLUSTERED INDEX [IX_Peers_Workspace_CreatedAt]
    ON [dbo].[Peers] ([WorkspaceId], [CreatedAt]);