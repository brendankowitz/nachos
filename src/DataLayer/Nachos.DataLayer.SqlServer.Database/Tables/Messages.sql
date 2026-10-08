CREATE TABLE [dbo].[Messages]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [SessionId]        BIGINT         NOT NULL,
    [PeerId]           BIGINT         NOT NULL,
    [PublicId]         NVARCHAR (32)  COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [Seq]              BIGINT         NOT NULL, -- allocated atomically per session via Sessions.NextMessageSeq
    [Content]          NVARCHAR (MAX) NOT NULL,
    [TokenCount]       INT            NOT NULL,
    [Metadata]         NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Messages_Metadata] DEFAULT N'{}',
    [InternalMetadata] NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Messages_InternalMetadata] DEFAULT N'{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; may be backdated by the caller

    CONSTRAINT [PK_Messages] PRIMARY KEY CLUSTERED ([Id]),
    -- JSON columns hold a JSON object; each CHECK is written in the form SQL Server stores
    -- (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_Messages_Metadata_IsJsonObject] CHECK ((isjson([Metadata],OBJECT)=(1))),
    CONSTRAINT [CK_Messages_InternalMetadata_IsJsonObject] CHECK ((isjson([InternalMetadata],OBJECT)=(1))),
    -- Composite FKs include WorkspaceId: a message cannot reference another workspace's session or peer.
    CONSTRAINT [FK_Messages_Sessions] FOREIGN KEY ([WorkspaceId], [SessionId]) REFERENCES [dbo].[Sessions] ([WorkspaceId], [Id]),
    CONSTRAINT [FK_Messages_Peers] FOREIGN KEY ([WorkspaceId], [PeerId]) REFERENCES [dbo].[Peers] ([WorkspaceId], [Id]),
    CONSTRAINT [UQ_Messages_PublicId] UNIQUE NONCLUSTERED ([PublicId]),
    CONSTRAINT [UQ_Messages_Session_Seq] UNIQUE NONCLUSTERED ([SessionId], [Seq])
);
GO

CREATE NONCLUSTERED INDEX [IX_Messages_Workspace_Peer]
    ON [dbo].[Messages] ([WorkspaceId], [PeerId]);