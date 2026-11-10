# Local embeddings: dedup calibration and parity

M3 Task 11. Date: 2026-11-09. Host: Intel Core i7-14700K (28 logical processors), Windows 11, .NET 10,
`Microsoft.ML.OnnxRuntime` 1.31.1 (CPU execution provider, default session options).

## What is calibrated

The Deriver merges a new conclusion into an existing one when the cosine distance between their
vectors is at most `Nachos:Deriver:DedupMaxDistance`. The 0.05 default was chosen on OpenAI's
`text-embedding-3-small` scale. Each local model has its own scale, so each catalog entry
(`src/Nachos.Core/Llm/LocalEmbeddingModel.cs`) carries a `DedupMaxDistance`, used when the key is
not configured.

A false merge (two different facts merged) loses information: the second fact corroborates or
replaces the first. A missed merge only leaves a near-duplicate conclusion. So the rule is: **no
false merge on the fixture, with a margin**, and recall is whatever that leaves.

## Fixture

`test/fixtures/dedup-pairs.json`, written for this task: 54 pairs of third-person conclusions of
the kind the Deriver stores.

- 26 **paraphrase** pairs state one fact two ways ("Bob prefers dark roast coffee." /
  "Bob likes his coffee dark roasted.").
- 28 **distinct** pairs are near misses: same person and topic, a different fact ("Bob prefers
  dark roast coffee." / "Bob prefers light roast coffee."; "Tina moved to Toronto last year." /
  "Tina moved away from Toronto last year.").

Distance is `1 - dot(a, b)` on the normalized vectors the generator returns (zero padding does not
change it).

## Results

| Model | Paraphrase distance min / median / max | Distinct distance min / median / max |
|---|---|---|
| `bge-small-en-v1.5` | 0.0114 / 0.0510 / 0.2042 | 0.0505 / 0.1423 / 0.2916 |
| `all-minilm-l6-v2` | 0.0278 / 0.0904 / 0.2250 | 0.0432 / 0.1686 / 0.3603 |

The two populations overlap for both models: the closest distinct pair is nearer than the median
paraphrase. In both, the closest distinct pair is "dark roast" against "light roast".

Merges at each threshold (recall = paraphrase pairs merged; false merges = distinct pairs merged):

| Threshold | bge-small recall | bge-small false merges | MiniLM recall | MiniLM false merges |
|---|---|---|---|---|
| 0.020 | 1/26 (4%) | 0/28 | 0/26 (0%) | 0/28 |
| 0.025 | 4/26 (15%) | 0/28 | 0/26 (0%) | 0/28 |
| 0.030 | 7/26 (27%) | 0/28 | 1/26 (4%) | 0/28 |
| **0.035** | 8/26 (31%) | 0/28 | **3/26 (12%)** | **0/28** |
| **0.040** | **9/26 (35%)** | **0/28** | 3/26 (12%) | 0/28 |
| 0.045 | 12/26 (46%) | 0/28 | 5/26 (19%) | 1/28 (3.6%) |
| 0.050 | 13/26 (50%) | 0/28 | 5/26 (19%) | 1/28 (3.6%) |
| 0.055 | 17/26 (65%) | 1/28 (3.6%) | 5/26 (19%) | 2/28 (7.1%) |
| 0.060 | 18/26 (69%) | 1/28 (3.6%) | 8/26 (31%) | 2/28 (7.1%) |
| 0.070 | 22/26 (85%) | 5/28 (17.9%) | 11/26 (42%) | 2/28 (7.1%) |
| 0.080 | 23/26 (88%) | 6/28 (21.4%) | 12/26 (46%) | 4/28 (14.3%) |
| 0.100 | 24/26 (92%) | 9/28 (32.1%) | 18/26 (69%) | 6/28 (21.4%) |
| 0.150 | 25/26 (96%) | 17/28 (60.7%) | 21/26 (81%) | 13/28 (46.4%) |

## Decision

| Model | `DedupMaxDistance` | False-merge rate on the fixture | Paraphrase recall | Margin to the closest distinct pair |
|---|---|---|---|---|
| `bge-small-en-v1.5` | **0.04** | 0% (0/28) | 35% (9/26) | 0.0105 |
| `all-minilm-l6-v2` | **0.035** | 0% (0/28) | 12% (3/26) | 0.0082 |

bge-small would still have no false merge at 0.045 or 0.05, but the margin there (0.0055 and
0.0005) is too thin for a 28-pair fixture. MiniLM gains nothing between 0.035 and 0.04 and loses
its margin.

What it means: with a local model, most paraphrases are **not** merged. The Deriver stores them
as separate conclusions; they rank together in semantic search, and representation building sees
both. That is the price of never merging different facts at this model size. An operator who
prefers fewer duplicates can raise `Nachos:Deriver:DedupMaxDistance`, using the table above to
see the cost.

The test `LocalEmbeddingGeneratorTests.DedupCalibration_CatalogThreshold_MergesNoDistinctPair`
re-runs the fixture against each catalog value with the real models and fails if any distinct
pair falls within it.

## Parity with the reference implementation

The pinned `model.onnx` files were run a second way, with Hugging Face `tokenizers` (each model's
own `tokenizer.json` at the same revision) and Python `onnxruntime`, pooling as each model's
`1_Pooling/config.json` says (bge: CLS; MiniLM: mean) and L2-normalizing. For
"Café owners in Zürich prefer espresso." the first eight values match the .NET generator to six
decimals for both models, and the token ids of "日本語のテキスト and ÉLAN" match exactly. That
pins the tokenizer settings: `Microsoft.ML.Tokenizers` 2.0.0 `BertTokenizer` needs
`RemoveNonSpacingMarks = true` to match (its default is false, which turns "café" into `[UNK]`),
with lower-casing and CJK splitting on. `LocalEmbeddingGeneratorTests.MatchesTheReferenceImplementation`
asserts both.

| Model | First 8 values, "Café owners in Zürich prefer espresso." |
|---|---|
| `bge-small-en-v1.5` | 0.003398, 0.037437, -0.014255, 0.016646, 0.029501, -0.053571, -0.006984, 0.053195 |
| `all-minilm-l6-v2` | 0.023424, 0.025649, 0.016590, 0.030706, 0.062560, 0.026866, 0.024313, 0.042838 |

## Throughput (same host)

| Model | One short input | 32 inputs at the model's full length |
|---|---|---|
| `bge-small-en-v1.5` | about 2.8 ms | about 1.36 s (512 tokens each) |
| `all-minilm-l6-v2` | about 1.2-1.6 ms | about 0.28 s (256 tokens each) |

## Chunking

`MessageChunker` counts with the model's `BertTokenizer`, at most 510 (bge-small) or 254 (MiniLM)
content tokens, with the 20% overlap. `BertTokenizer` reports token offsets into its normalized
text (lower-cased, accents stripped, spaces around CJK characters, control characters removed),
whose length differs from the original, and encoding with `considerNormalization: false` changes
the tokens (an upper-case word becomes `[UNK]`), so neither gives a cut in the original text. For a
normalizing tokenizer the chunker instead measures the original text: the longest piece from each
start that counts within the limit (galloping, then bisecting), moved back to a whitespace in its
second half, with the next chunk starting at the latest word start that keeps about 20% of the
limit. Every chunk is a substring of the message and re-counts within the limit. Tokenizers that do
not normalize (cl100k_base) keep the offset-based cut.
