# Evidence: pinned placeholder image (`ghcr.io/mendhak/http-https-echo@sha256:a265f55c…`)

Raw inspection data for the third-party image that the M1 Bicep (`infra/main.bicep`, `placeholderImage`) runs as the
first revision of `nachos-api`, until the first `azd deploy` replaces it. It exists so Task 17's license check and the
owner can classify the image from facts. **It is evidence, not an approval:** nothing here authorizes anything, and no
license-text review has been done.

Image: `ghcr.io/mendhak/http-https-echo@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d4`
(the OCI index for tag `42`; the `linux/amd64` manifest was inspected). Regenerate everything with `./collect.sh`
(needs a Docker daemon; it only pulls the image and runs it locally to read files; it never pushes or contacts Azure).

## Files

| File | What it is |
|---|---|
| `image-identity.txt` | Platform, creation time, user, entrypoint/cmd, ports, image id, `os-release`, Node/yarn versions, `docker history`. |
| `os-packages.tsv` | The 18 installed apk packages: name, version, **declared** license, read from `/lib/apk/db/installed`. |
| `app-packages.tsv` | The 123 packages under `/app/node_modules`: name, version, **declared** license (`package.json`), whether a license-like file exists in the package directory, path. |
| `licenses/app/**` | The 122 license-like files (`LICENSE*`, `LICENCE*`, `COPYING*`, `NOTICE*`) that exist in those package directories, with their package paths preserved. |
| `licenses/node/LICENSE` | Node.js's own `/usr/local/LICENSE` (145 KB; it bundles many third-party components' notices). |
| `bundled-key-material.txt` | **Names only** of key-material files in `/app` (`fullchain.pem`, `testpk.pem`). Their bytes are deliberately not collected. |
| `collect.sh` | Regenerates all of the above and refuses to finish if key-like content is present. |

## What is covered, and what is not

- **OS layer (Alpine 3.24.2, 18 packages): metadata declarations only.** The image contains **no license documents** for these
  packages (`/usr/share/licenses` and `/usr/share/doc` do not exist). The licenses in `os-packages.tsv` are what the package
  database *declares*; they have not been checked against license text. They include GPL-2.0-only (8 packages),
  GPL-2.0-or-later AND LGPL-2.1-or-later (libgcc, libstdc++), MPL-2.0 AND MIT (ca-certificates-bundle), and
  MIT AND BSD-2-Clause AND GPL-2.0-or-later (musl-utils).
- **Application dependencies (123 packages): declarations plus files.** 122 packages have a license-like file, copied under
  `licenses/app/`. One package has **a declaration only**: `cookie-signature@1.0.6` (nested under `cookie-parser`, declared MIT).
  Nobody has compared the copied files' text with each package's declaration; they are provided for that review.
- **Node.js runtime:** the `LICENSE` file is included. Its bundled third-party components (for example OpenSSL, ICU, zlib, V8
  and others) were **not enumerated** here. yarn 1.22.22 is present in the image; its license was **not collected**.
- **The echo application itself** (`/app/index.js`) has no license file in the image; the upstream repository's `LICENSE.md`
  (MIT) was read from a clone but is not part of this image's contents and is not reproduced here.
- **Not an SBOM scan.** No scanner was used and no vulnerability assessment was made. Alpine package sources and patches,
  and anything compiled into the Node binary beyond its `LICENSE` file, are out of scope.
- **Platform:** only `linux/amd64` was inspected (Container Apps runs `amd64`). The index also lists `linux/arm64` and `linux/arm`
  manifests, which were not inspected.

## Related facts

- The image runs as user `1000` (not root) and listens on 8080 (HTTP) and 8443 (HTTPS); Container Apps ingress targets 8080 only.
- It contains a bundled self-signed TLS test key for the 8443 listener (see `bundled-key-material.txt`; bytes not collected).
- It is a third-party image that Nachos only **references by digest**; it is not copied into any Nachos package, image, CLI or
  docs bundle. How the license policy (spec §3 rule 7) applies to it is an open question for the Task 17 owner and the
  repository owner; see issue #2 and PR #6.
