CREATE TABLE [dbo].[SchemaVersion]
(
    [Id]        TINYINT NOT NULL,
    [Version]   INT     NOT NULL,
    [AppliedAt] DATETIMEOFFSET (7) NOT NULL CONSTRAINT [DF_SchemaVersion_AppliedAt] DEFAULT SYSDATETIMEOFFSET(),

    CONSTRAINT [PK_SchemaVersion] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_SchemaVersion_SingleRow] CHECK ([Id] = 1)
);