CREATE TABLE [dbo].[IdempotencyRecords]
(
    [Id]             BIGINT         IDENTITY (1, 1) NOT NULL,
    [WorkspaceId]    BIGINT         NOT NULL,
    [KeyHash]        BINARY (32)    NOT NULL, -- SHA-256 of [Key]
    [Key]            NVARCHAR (255) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [RequestHash]    CHAR (64)      NOT NULL, -- lowercase hex SHA-256 of the canonical request
    [ResponseStatus] INT            NOT NULL,
    [ResponseBody]   NVARCHAR (MAX) NOT NULL,
    [ExpiresAt]      DATETIMEOFFSET (7) NOT NULL, -- application clock

    CONSTRAINT [PK_IdempotencyRecords] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_IdempotencyRecords_Workspaces] FOREIGN KEY ([WorkspaceId]) REFERENCES [dbo].[Workspaces] ([Id]),
    CONSTRAINT [UQ_IdempotencyRecords_Workspace_KeyHash] UNIQUE NONCLUSTERED ([WorkspaceId], [KeyHash])
);