CREATE TABLE [dbo].[MessageEmbeddings]
(
    [Id]          BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId] BIGINT         NOT NULL,
    [MessageId]   BIGINT         NOT NULL,
    -- Denormalized from the message so session- and window-filtered vector search needs no join.
    [SessionId]   BIGINT         NOT NULL,
    [ChunkIndex]  SMALLINT       NOT NULL,
    [Content]     NVARCHAR (MAX) NOT NULL,
    -- The dimension is fixed in DDL (text-embedding-3-small); startup validates the configured embedding model against it.
    [Embedding]   VECTOR (1536)  NULL,
    [SyncState]   TINYINT        NOT NULL, -- 0 = pending, 1 = synced, 2 = failed
    [Attempts]    INT            NOT NULL CONSTRAINT [DF_MessageEmbeddings_Attempts] DEFAULT 0,
    [CreatedAt]   DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_MessageEmbeddings] PRIMARY KEY CLUSTERED ([Id]),
    -- An IN list is stored as a reversed OR chain.
    CONSTRAINT [CK_MessageEmbeddings_SyncState] CHECK (([SyncState]=(2) OR [SyncState]=(1) OR [SyncState]=(0))),
    -- The FK carries WorkspaceId and SessionId, so a chunk's denormalized session is always its message's own session.
    CONSTRAINT [FK_MessageEmbeddings_Messages] FOREIGN KEY ([WorkspaceId], [SessionId], [MessageId]) REFERENCES [dbo].[Messages] ([WorkspaceId], [SessionId], [Id]),
    CONSTRAINT [UQ_MessageEmbeddings_Message_Chunk] UNIQUE NONCLUSTERED ([MessageId], [ChunkIndex])
);
GO

-- Serves session filters.
CREATE NONCLUSTERED INDEX [IX_MessageEmbeddings_Workspace_Session]
    ON [dbo].[MessageEmbeddings] ([WorkspaceId], [SessionId]);
GO

-- Serves the reconciler's pending-embedding scan. Not filtered (WHERE SyncState = 0): the project declares
-- ANSI_NULLS OFF (the box defaults), and SQL Server refuses to create a filtered index on a table created that way.
CREATE NONCLUSTERED INDEX [IX_MessageEmbeddings_SyncState]
    ON [dbo].[MessageEmbeddings] ([SyncState]);
