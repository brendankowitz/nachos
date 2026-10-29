CREATE TABLE [dbo].[ConclusionSources]
(
    [DerivedId] BIGINT NOT NULL,
    [Position]  INT    NOT NULL, -- order of the source among the derived conclusion's premises
    -- Deliberately no FK: a source may be purged while the derived conclusion keeps its provenance (spec 7.2).
    [SourceId]  BIGINT NOT NULL,

    CONSTRAINT [PK_ConclusionSources] PRIMARY KEY CLUSTERED ([DerivedId], [Position]),
    CONSTRAINT [FK_ConclusionSources_Conclusions] FOREIGN KEY ([DerivedId]) REFERENCES [dbo].[Conclusions] ([Id])
);
