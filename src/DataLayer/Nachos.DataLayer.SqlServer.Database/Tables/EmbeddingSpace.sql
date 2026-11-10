-- The one embedding model whose vectors this database holds. Vectors from different models are not comparable, so every
-- host compares its configured model with this row at startup; 'nachos embeddings reset' is the only way to change it.
CREATE TABLE [dbo].[EmbeddingSpace]
(
    [Id]         TINYINT        NOT NULL,
    [Model]      NVARCHAR (128) NOT NULL,
    -- The model's own vector size, before zero padding to the VECTOR(1536) columns.
    [Dimensions] INT            NOT NULL,
    [StampedAt]  DATETIMEOFFSET (7) NOT NULL, -- application clock; no DB default

    CONSTRAINT [PK_EmbeddingSpace] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_EmbeddingSpace_SingleRow] CHECK ([Id] = 1)
);
