CREATE TABLE [dbo].[Conclusions]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]      BIGINT         NOT NULL,
    [PublicId]         NVARCHAR (32)  COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [CollectionId]     BIGINT         NOT NULL,
    [SessionId]        BIGINT         NULL,
    [Level]            TINYINT        NOT NULL, -- 0 = explicit, 1 = deductive, 2 = inductive, 3 = contradiction
    [Content]          NVARCHAR (MAX) NOT NULL,
    -- The dimension is fixed in DDL (text-embedding-3-small); startup validates the configured embedding model against it.
    [Embedding]        VECTOR (1536)  NULL,
    [EmbeddingState]   TINYINT        NOT NULL, -- 0 = pending, 1 = synced, 2 = failed
    [TimesDerived]     INT            NOT NULL CONSTRAINT [DF_Conclusions_TimesDerived] DEFAULT 1,
    [PatternType]      NVARCHAR (64)  NULL,
    [Confidence]       FLOAT (53)     NULL,
    [InternalMetadata] NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Conclusions_InternalMetadata] DEFAULT N'{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default
    [DeletedAt]        DATETIMEOFFSET (7) NULL,     -- tombstone; the reconciler hard-deletes later

    CONSTRAINT [PK_Conclusions] PRIMARY KEY CLUSTERED ([Id]),
    -- JSON columns hold a JSON object; each CHECK is written in the form SQL Server stores
    -- (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_Conclusions_InternalMetadata_IsJsonObject] CHECK ((isjson([InternalMetadata],OBJECT)=(1))),
    -- An IN list is stored as a reversed OR chain.
    CONSTRAINT [CK_Conclusions_Level] CHECK (([Level]=(3) OR [Level]=(2) OR [Level]=(1) OR [Level]=(0))),
    -- Composite FKs include WorkspaceId: a conclusion cannot reference another workspace's collection or session.
    CONSTRAINT [FK_Conclusions_Collections] FOREIGN KEY ([WorkspaceId], [CollectionId]) REFERENCES [dbo].[Collections] ([WorkspaceId], [Id]),
    CONSTRAINT [FK_Conclusions_Sessions] FOREIGN KEY ([WorkspaceId], [SessionId]) REFERENCES [dbo].[Sessions] ([WorkspaceId], [Id]),
    CONSTRAINT [UQ_Conclusions_PublicId] UNIQUE NONCLUSTERED ([PublicId])
);
GO

CREATE NONCLUSTERED INDEX [IX_Conclusions_Collection_DeletedAt_CreatedAt]
    ON [dbo].[Conclusions] ([CollectionId], [DeletedAt], [CreatedAt]);
GO

-- Serves the reconciler's pending-embedding scan. Not filtered (WHERE EmbeddingState = 0): the project declares
-- ANSI_NULLS OFF (the box defaults), and SQL Server refuses to create a filtered index on a table created that way.
CREATE NONCLUSTERED INDEX [IX_Conclusions_EmbeddingState]
    ON [dbo].[Conclusions] ([EmbeddingState]);
