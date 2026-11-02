CREATE TABLE [dbo].[WorkUnitLeases]
(
    -- The lease identity is the clustered key. 450 UTF-16 code units is 900 bytes, the clustered key limit.
    [WorkUnitKey]  NVARCHAR (450) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [Owner]        NVARCHAR (128) NOT NULL,
    [ExpiresAt]    DATETIMEOFFSET (7) NOT NULL, -- application clock
    [FencingToken] BIGINT         NOT NULL,

    CONSTRAINT [PK_WorkUnitLeases] PRIMARY KEY CLUSTERED ([WorkUnitKey])
);
