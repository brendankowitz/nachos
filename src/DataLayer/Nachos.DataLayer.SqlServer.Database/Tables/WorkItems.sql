CREATE TABLE [dbo].[WorkItems]
(
    [Id]          BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId] BIGINT         NULL, -- NULL for global work such as the reconciler
    -- 1 representation, 2 summary (M3), 3 dream (M5), 4 webhook (M6), 5 deletion (M3), 6 reconciler, 7 scope_backfill, 8 scope_removal (M6).
    [TaskType]    TINYINT        NOT NULL,
    -- 450 UTF-16 code units: the same key is the clustered key of WorkUnitLeases, whose limit is 900 bytes.
    [WorkUnitKey] NVARCHAR (450) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [Payload]     NVARCHAR (MAX) NOT NULL,
    [Status]      TINYINT        NOT NULL,
    [Attempts]    INT            NOT NULL CONSTRAINT [DF_WorkItems_Attempts] DEFAULT 0,
    [AvailableAt] DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default
    [TokenCount]  INT            NOT NULL,
    -- No FKs on MessageId and SessionId: queue rows are transient and must not block deleting what they refer to.
    [MessageId]   BIGINT         NULL,
    [SessionId]   BIGINT         NULL,
    [Error]       NVARCHAR (4000) NULL,
    [CreatedAt]   DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default
    [CompletedAt] DATETIMEOFFSET (7) NULL,

    CONSTRAINT [PK_WorkItems] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_WorkItems_Payload_IsJsonObject] CHECK ((isjson([Payload],OBJECT)=(1))),
    -- Reserved task types are listed now so later milestones need no CHECK change. An IN list is stored as a reversed OR chain.
    CONSTRAINT [CK_WorkItems_TaskType] CHECK (([TaskType]=(8) OR [TaskType]=(7) OR [TaskType]=(6) OR [TaskType]=(5) OR [TaskType]=(4) OR [TaskType]=(3) OR [TaskType]=(2) OR [TaskType]=(1))),
    CONSTRAINT [FK_WorkItems_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id])
);
GO

-- Serves the claim query: candidate units by status and key.
CREATE NONCLUSTERED INDEX [IX_WorkItems_Status_WorkUnitKey_Id]
    ON [dbo].[WorkItems] ([Status], [WorkUnitKey], [Id])
    INCLUDE ([AvailableAt], [TokenCount], [CreatedAt]);
GO

-- Serves queue status.
CREATE NONCLUSTERED INDEX [IX_WorkItems_Workspace_Session_Status]
    ON [dbo].[WorkItems] ([WorkspaceId], [SessionId], [Status]);
