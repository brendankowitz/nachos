# Managed exact-BPE token counter: frozen feasibility bundle (NOT production code)

Status: **evidence from a scratch prototype**, frozen as produced on 2026-10-08 by agent Salsa for PR #6 review (Cedar request, comment 6066854195). It is not wired into any Nachos project, is not built by `Nachos.slnx`, and carries no policy. Production ownership and any port belong to the Core owner. The results are reported in PR #6 comment 6066782451.

## What this shows (and does not)
- A small managed counter (heap BPE for pieces of 100 bytes or more, scalar-aware hand-written pre-tokenizer over UTF-8, cooperative cancellation) reproduces tiktoken 0.14.0 `o200k_base` `encode_ordinary` token IDs exactly on 30,106 generated cases and on every Unicode scalar in 11 templates (12,232,704 comparisons): 0 mismatches. The copied-pattern .NET `Regex` variant (3,949 mismatches in 30,106) and the pinned `Microsoft.ML.Tokenizers` 2.0.0 counter (4,757) do not; every deviation was minimized and checked against the oracle (`out/repros_*_verified.txt`).
- Not proven: other encodings, special tokens (not implemented), other tiktoken/Rust versions, future Unicode data (the native oracle and .NET 10.0.12 both behave as Unicode 16.0 today), inputs outside the generated distribution, thread-safety under load, any production behaviour.

## Layout
- `O200k/` prototype library (`RankTable.cs`, `BytePairMerger.cs`, `ScalarPreTokenizer.cs`, `O200kCounter.cs`), no package references.
- `Harness/` .NET 10 console harness (modes: `hash`, `gencorpora`, `diff`, `cpsweep`, `unicode`, `cancel`, bench modes; it references `Microsoft.ML.Tokenizers` 2.0.0 and `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 as the secondary oracle).
- `oracle/` Python scripts using the pinned native oracle (`tiktoken==0.14.0`): case generation, repro verification/classification, per-scalar sweep, Unicode version probes.
- `data/corpora/` seeded corpora C1-C6, W1, P1 (1k/5k/25k/100k), `data/ucd/DerivedAge-17.0.0.txt` (UCD, used for labels only; sha256 f8ecdf768bdc210f201abd271d9bc587825618a86a7046a8146cc816393f1998).
- `out/` the raw evidence: `hash.txt`, `diff_*.txt`, `repros_*_verified.txt`, `cpsweep_*.txt`, `native_unicode.txt`, `unicode_dotnet.txt`, `bench.txt`, `bpebench.txt`, `tables.md`, `cancel.txt`, `cedar_case_compare.txt`.
- `notices/` licence texts as found: tiktoken 0.14.0 LICENSE (MIT, Copyright (c) 2022 OpenAI, Shantanu Jain), `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 LICENSE (MIT) and its `THIRD-PARTY-NOTICES.TXT` (contains a section "License notice for OpenAI Tiktoken Tokenizer & Tokenizer's vocab files"), and the nupkg sha512.
- `run_bench.sh`, `run_bpebench.sh` timing drivers (one `timeout 120` process per cell).
- `MANIFEST.sha256` (this directory): sha256 of every file in the bundle and of the externals below.

## Deliberately NOT in the bundle (hash-pinned instead)
| Item | Why | Pin |
|---|---|---|
| `data/cases.tsv` (109 MB: 30,106 cases + oracle IDs) | too large for git; regenerated deterministically | sha256 `2ee8c215126f3ee5be9abd81c4b606548ee1d122760fd20fa9dc420cce9927d8` |
| `data/cpsweep.tsv` (268 MB: per-scalar sweep inputs + oracle IDs) | same | sha256 `663a7852f52a18e1c353526ceb7bc0c5ce06215e4ec70d28e6e56d8b3617b3af` |
| `data/ages.tsv` (3.8 MB) | derived from `DerivedAge` by `oracle/ages.py` | regenerate |
| `o200k_base.tiktoken` vocabulary (3,613,922 bytes) | licence/provenance not settled for redistribution here; downloaded by tiktoken from `https://openaipublic.blob.core.windows.net/encodings/o200k_base.tiktoken` | sha256 `446a9538cb6c348e3516120d7c08b09f57c36495e2acfffe59a5bf8b0cfb1a2d` (equals tiktoken's own `expected_hash`) |
| Microsoft's embedded resource `o200k_base.tiktoken.deflate` and its inflated form | inside the NuGet package | deflate sha256 `88b2a54dcedc68d39b1af4b8dc744adba8eca01310b7d07a687f1add3be75524`; inflated `b42b97d43069c0a1d6cab8567f4febc4f35c6150738dc9614df8542535fc26c4`; rebuilt with Microsoft's loader rules (decompiled `TiktokenTokenizer.cs:503-534`: `Capacity: 199999` header, base64 token per CRLF line, rank = line number) it is byte-identical to the canonical file (`446a9538...1a2d`) |

## Environment used
Linux container, 4-core Intel Xeon 2.1 GHz, .NET SDK 10.0.401 / runtime 10.0.12, Python 3.13 with `tiktoken==0.14.0` (native Rust core: fancy-regex 0.19.0, regex-automata 0.4.18, regex-syntax 0.8.11, bstr 1.13.1, pyo3 0.29.2, read from the `.so`), `Microsoft.ML.Tokenizers` 2.0.0 + `Data.O200kBase` 2.0.0. Unicode data: native oracle behaves as 16.0, .NET 10.0.12 as 16.0, Python `regex` 2026.9.29 as 17.0 (labels only).

## Replay (outline; paths relative to this directory; run from a scratch copy, never inside the repo build)
1. `pip install tiktoken==0.14.0`; run `python -c "import tiktoken; tiktoken.get_encoding('o200k_base')"` to populate the cache; verify the cached file's sha256 equals the pin above.
2. `dotnet build -c Release Harness` (needs the two NuGet packages; central package management must be OFF in the build root, e.g. build from a copy outside the repo).
3. `dotnet Harness.dll gencorpora data/corpora` (corpora are included; this regenerates and can be diffed).
4. `python -I oracle/gen_cases.py data/corpora data/cases.tsv 30000` then compare `sha256sum data/cases.tsv` with the pin (seed 20261008 is in the script).
5. `python -I oracle/cpsweep.py data/cpsweep.tsv` and compare with the pin.
6. `dotnet Harness.dll diff data/cases.tsv scan,scan-heap,scan-quad,regex out/diff_proto.txt out/repros_proto.tsv` and `dotnet Harness.dll diff data/cases.tsv ms out/diff_ms.txt out/repros_ms.tsv`; then `python -I oracle/verify_repros.py` / `classify_repros.py` on the repro files.
7. Sweep: `dotnet Harness.dll cpsweep data/cpsweep.tsv {scan|regex|ms} out/cpsweep_<impl>.txt`; Unicode probes: `oracle/ages.py`, `oracle/native_unicode.py`, `dotnet Harness.dll unicode ...`.
8. Timing and cancellation: `run_bench.sh`, `run_bpebench.sh`, `dotnet Harness.dll cancel {scan|regex} C1 100000 50`.
Exact command lines used are in the PR #6 comment and in `out/*.txt` headers. The results listed in `out/` were produced on the environment above; timings are environment-specific and are not expected to reproduce numerically.

## Known gaps in this evidence
- The native-oracle results were produced only on this Linux host; replay on another host is the way to independently check them.
- A durable contract for Unicode classification across .NET servicing/runtime versions is NOT provided; cancellation is checked in the merge loop (every 1,024 heap pops) and per 256 pieces, not through every stage (UTF-8 conversion, setup, scanning of one very long piece).
- Thread-safety was not stress-tested; the residual batch cost (100 x 25k characters) is linear but not constant.
- Build warning (harness only): NU1903 for `Microsoft.Bcl.Memory` 9.0.4, a transitive dependency of the pinned Microsoft package.

## Notices added after publication
`notices/UNICODE-PROVENANCE.md` and `notices/UNICODE-LICENSE-V3.txt` (this directory, outside the frozen tarball) give the provenance and the Unicode License v3 permission notice for the one UCD file in `data/ucd/`. The tarball and its hash are unchanged.
