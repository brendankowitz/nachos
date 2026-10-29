CREATE TABLE [dbo].[PeerCards]
(
    [Id]             BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]    BIGINT         NOT NULL,
    [ObserverPeerId] BIGINT         NOT NULL,
    [ObservedPeerId] BIGINT         NOT NULL,
    [Entries]        NVARCHAR (MAX) NOT NULL,
    [UpdatedAt]      DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default
    [UpdatedBy]      TINYINT        NOT NULL,     -- 0 = api, 1 = dream

    CONSTRAINT [PK_PeerCards] PRIMARY KEY CLUSTERED ([Id]),
    -- Entries is a JSON array; written in the form SQL Server stores (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_PeerCards_Entries_IsJsonArray] CHECK ((isjson([Entries],ARRAY)=(1))),
    CONSTRAINT [FK_PeerCards_Observer] FOREIGN KEY ([WorkspaceId], [ObserverPeerId]) REFERENCES [dbo].[Peers] ([WorkspaceId], [Id]),
    CONSTRAINT [FK_PeerCards_Observed] FOREIGN KEY ([WorkspaceId], [ObservedPeerId]) REFERENCES [dbo].[Peers] ([WorkspaceId], [Id]),
    CONSTRAINT [UQ_PeerCards_Workspace_Observer_Observed] UNIQUE NONCLUSTERED ([WorkspaceId], [ObserverPeerId], [ObservedPeerId])
);
