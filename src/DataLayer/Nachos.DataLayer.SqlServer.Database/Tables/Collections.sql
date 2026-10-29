CREATE TABLE [dbo].[Collections]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [ObserverPeerId]   BIGINT         NOT NULL,
    [ObservedPeerId]   BIGINT         NOT NULL,
    -- The three dream columns are the M5 seam: the Deriver maintains them, nothing reads them yet.
    [NewExplicitCount] INT            NOT NULL CONSTRAINT [DF_Collections_NewExplicitCount] DEFAULT 0,
    [LastActivityAt]   DATETIMEOFFSET (7) NULL,
    [LastDreamAt]      DATETIMEOFFSET (7) NULL,
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Collections] PRIMARY KEY CLUSTERED ([Id]),
    -- Composite FKs include WorkspaceId: a collection cannot reference another workspace's peers.
    CONSTRAINT [FK_Collections_Observer] FOREIGN KEY ([WorkspaceId], [ObserverPeerId]) REFERENCES [dbo].[Peers] ([WorkspaceId], [Id]),
    CONSTRAINT [FK_Collections_Observed] FOREIGN KEY ([WorkspaceId], [ObservedPeerId]) REFERENCES [dbo].[Peers] ([WorkspaceId], [Id]),
    CONSTRAINT [UQ_Collections_Workspace_Observer_Observed] UNIQUE NONCLUSTERED ([WorkspaceId], [ObserverPeerId], [ObservedPeerId]),
    -- Alternate key: lets child tables use a composite FK that carries WorkspaceId.
    CONSTRAINT [UQ_Collections_Workspace_Id] UNIQUE NONCLUSTERED ([WorkspaceId], [Id])
);
