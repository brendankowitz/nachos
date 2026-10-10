CREATE TABLE [dbo].[SessionPeers]
(
    [WorkspaceId]   BIGINT NOT NULL,
    [SessionId]     BIGINT NOT NULL,
    [PeerId]        BIGINT NOT NULL,
    [Configuration] NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_SessionPeers_Configuration] DEFAULT N'{}',
    [JoinedAt]      DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default
    [LeftAt]        DATETIMEOFFSET (7) NULL,

    CONSTRAINT [PK_SessionPeers] PRIMARY KEY CLUSTERED ([WorkspaceId], [SessionId], [PeerId]),
    -- JSON columns hold a JSON object; each CHECK is written in the form SQL Server stores
    -- (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_SessionPeers_Configuration_IsJsonObject] CHECK ((isjson([Configuration],OBJECT)=(1))),
    -- Composite FKs include WorkspaceId: a cross-workspace membership cannot be stored.
    CONSTRAINT [FK_SessionPeers_Sessions] FOREIGN KEY ([WorkspaceId], [SessionId]) REFERENCES [dbo].[Sessions] ([WorkspaceId], [Id]),
    CONSTRAINT [FK_SessionPeers_Peers] FOREIGN KEY ([WorkspaceId], [PeerId]) REFERENCES [dbo].[Peers] ([WorkspaceId], [Id])
);
GO

CREATE NONCLUSTERED INDEX [IX_SessionPeers_Workspace_Peer]
    ON [dbo].[SessionPeers] ([WorkspaceId], [PeerId]);