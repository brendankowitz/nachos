CREATE TABLE [dbo].[SessionSummaries]
(
    [Id]                     BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]            BIGINT         NOT NULL,
    [SessionId]              BIGINT         NOT NULL,
    [Kind]                   TINYINT        NOT NULL, -- 1 = short, 2 = long
    [Content]                NVARCHAR (MAX) NOT NULL,
    -- The last message the summary covers, by id and by its per-session sequence number.
    [CoversThroughMessageId] BIGINT         NOT NULL,
    [CoversThroughSeq]       BIGINT         NOT NULL,
    [TokenCount]             INT            NOT NULL,
    [CreatedAt]              DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_SessionSummaries] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_SessionSummaries_Kind] CHECK (([Kind]=(2) OR [Kind]=(1))),
    CONSTRAINT [FK_SessionSummaries_Sessions] FOREIGN KEY ([WorkspaceId], [SessionId]) REFERENCES [dbo].[Sessions] ([WorkspaceId], [Id]),
    CONSTRAINT [FK_SessionSummaries_Messages] FOREIGN KEY ([WorkspaceId], [CoversThroughMessageId]) REFERENCES [dbo].[Messages] ([WorkspaceId], [Id]),
    -- One current summary per kind; a new summary replaces it in place.
    CONSTRAINT [UQ_SessionSummaries_Session_Kind] UNIQUE NONCLUSTERED ([SessionId], [Kind])
);
GO

-- Serves the composite (WorkspaceId, CoversThroughMessageId) FK.
CREATE NONCLUSTERED INDEX [IX_SessionSummaries_Workspace_Message]
    ON [dbo].[SessionSummaries] ([WorkspaceId], [CoversThroughMessageId]);
