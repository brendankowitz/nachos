# Unicode data in the token-feasibility bundle: provenance and notice

Correction requested in PR #6 (Cedar review 5461802726): the research bundle ships one Unicode Character Database file and its notice was missing from the bundle's own `notices/` directory.

| Item | Value |
|---|---|
| File inside `token-feasibility-bundle.tar.gz` | `token-feasibility/data/ucd/DerivedAge-17.0.0.txt` (138,286 bytes) |
| Source | https://www.unicode.org/Public/17.0.0/ucd/DerivedAge.txt (Unicode Character Database 17.0.0, file header: `Date: 2025-07-30`, `(c) 2025 Unicode(R), Inc.`, "For terms of use and license, see https://www.unicode.org/terms_of_use.html") |
| sha256 | `f8ecdf768bdc210f201abd271d9bc587825618a86a7046a8146cc816393f1998` (re-downloaded on 2026-10-08 and compared: byte-identical to the bundled file) |
| Used for | labels only (the age of a scalar, to explain which scalars a native engine treats as unassigned); NOT a production input and not a source of any classification fact |
| Licence | Unicode License v3 ("Unicode-3.0"). The full text published at https://www.unicode.org/license.txt on 2026-10-08 (sha256 `e7a93b009565cfce55919a381437ac4db883e9da2126fa28b91d12732bc53d96`, "Copyright (c) 1991-2026 Unicode, Inc.") is in `UNICODE-LICENSE-V3.txt` next to this file; the permission notice must accompany copies of the data file, which this directory does for the bundle |
| Derived data not shipped | `data/ages.tsv` (derived from the file above by `oracle/ages.py`; regenerate) |

Scope and limits of this note: it is a packaging/provenance correction for the research artifact only. It does not adjudicate whether Unicode-3.0 is acceptable for any production resource (it is not in the spec section 3 allowlist; that disposition is part of the batched owner question), and it does not change the claims in the bundle `README.md`. The tarball itself is unchanged (sha256 in `BUNDLE.sha256`).
