CREATE TABLE [dbo].[Workspaces]
(
    [Id]               BIGINT         IDENTITY (1, 1) NOT NULL,
    [Name]             NVARCHAR (512) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
    [LifecycleState]   TINYINT        NOT NULL, -- 0 = Active, 1 = Inactive, 2 = Deleting
    [LifecycleVersion] INT            NOT NULL CONSTRAINT [DF_Workspaces_LifecycleVersion] DEFAULT 0,
    [DeletionJobId]    BIGINT         NULL,
    [Metadata]         NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Workspaces_Metadata] DEFAULT N'{}',
    [InternalMetadata] NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Workspaces_InternalMetadata] DEFAULT N'{}',
    [Configuration]    NVARCHAR (MAX) NOT NULL CONSTRAINT [DF_Workspaces_Configuration] DEFAULT N'{}',
    [CreatedAt]        DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_Workspaces] PRIMARY KEY CLUSTERED ([Id]),
    -- JSON columns hold a JSON object; each CHECK is written in the form SQL Server stores
    -- (sys.check_constraints.definition), so DacFx sees no drift.
    CONSTRAINT [CK_Workspaces_Metadata_IsJsonObject] CHECK ((isjson([Metadata],OBJECT)=(1))),
    CONSTRAINT [CK_Workspaces_InternalMetadata_IsJsonObject] CHECK ((isjson([InternalMetadata],OBJECT)=(1))),
    CONSTRAINT [CK_Workspaces_Configuration_IsJsonObject] CHECK ((isjson([Configuration],OBJECT)=(1))),
    CONSTRAINT [UQ_Workspaces_Name] UNIQUE NONCLUSTERED ([Name]),
    -- Written in the form SQL Server stores (sys.check_constraints.definition): an IN list is normalized to a reversed
    -- OR chain, and DacFx would otherwise report a drop+create of this constraint on every deploy.
    CONSTRAINT [CK_Workspaces_LifecycleState] CHECK (([LifecycleState]=(2) OR [LifecycleState]=(1) OR [LifecycleState]=(0)))
);