CREATE TABLE [dbo].[Workspaces]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [Name]             NVARCHAR (512) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [LifecycleState]   TINYINT        NOT NULL, -- 0 = Active, 1 = Inactive, 2 = Deleting
    [LifecycleVersion] INT            NOT NULL CONSTRAINT [DF_Workspaces_LifecycleVersion] DEFAULT 0,
    [DeletionJobId]    BIGINT         NULL,
    [Metadata]         JSON           NOT NULL CONSTRAINT [DF_Workspaces_Metadata] DEFAULT '{}',
    [InternalMetadata] JSON           NOT NULL CONSTRAINT [DF_Workspaces_InternalMetadata] DEFAULT '{}',
    [Configuration]    JSON           NOT NULL CONSTRAINT [DF_Workspaces_Configuration] DEFAULT '{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Workspaces] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Workspaces_Name] UNIQUE NONCLUSTERED ([Name])
);