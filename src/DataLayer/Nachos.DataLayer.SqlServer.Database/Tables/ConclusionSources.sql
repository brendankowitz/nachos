CREATE TABLE [dbo].[ConclusionSources]
(
    [DerivedId] BIGINT NOT NULL,
    [Position]  INT    NOT NULL, -- order of the source among the derived conclusion's premises
    -- Deliberately no FK: a source may be purged while the derived conclusion keeps its provenance (spec 7.2).
    [SourceId]  BIGINT NOT NULL,

    CONSTRAINT [PK_ConclusionSources] PRIMARY KEY CLUSTERED ([DerivedId], [Position]),
    CONSTRAINT [FK_ConclusionSources_Conclusions] FOREIGN KEY ([DerivedId]) REFERENCES [dbo].[Conclusions] ([Id])
);
GO

-- Serves the deletion job's search for the conclusions citing one it removed (spec 13.1, D7). Added by the M3 upgrade, so an
-- index (additive, AutoSafe), not a constraint.
CREATE NONCLUSTERED INDEX [IX_ConclusionSources_Source]
    ON [dbo].[ConclusionSources] ([SourceId])
    INCLUDE ([DerivedId]);
