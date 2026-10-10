# Conformance suite dependencies

Identity and licence evidence for every package resolved by `python/requirements.lock` (18 packages) and
`typescript/package-lock.json` (2 packages). These packages are test tooling for the upstream-SDK conformance suites:
nothing here is shipped in a Nachos artifact.

Generated from the archives themselves, not from memory, on 2026-10-10. Only package metadata (`METADATA`,
`package.json`) and licence files inside the archives were read; no SDK or package source module was opened.

```bash
# pip: download each locked artifact; pip rejects any whose sha256 is not in the lock.
python3 -m pip download --no-deps --require-hashes -r test/conformance/python/requirements.lock -d <scratch>/pip
# colorama is locked only for Windows (sys_platform == 'win32'), so pip skips it on Linux: its lock entry was
# downloaded the same way from a copy with the marker removed.
# npm: fetch each tarball and compare its sha512 with the integrity value in package-lock.json.
npm pack @honcho-ai/sdk@2.5.1 zod@4.0.0     # run in <scratch>/npm
```

* The Python lock is universal (`uv pip compile --universal --python-version 3.11`): it resolves for every platform
  and Python 3.11 or newer, and lists the hashes of every published artifact. Only the artifact the author's host
  selects was downloaded and inspected (`pydantic-core` is a platform wheel, so another platform installs a different,
  equally locked wheel of the same release).
* "Declared licence" is the `License-Expression` field of the wheel's `METADATA` (pip) or the `license` field of
  `package.json` (npm), copied verbatim. When a wheel has no such field (colorama), the trove classifier is given.
  Compound expressions are not interpreted and no choice is recorded.
* "Archive hash" is the sha256 of the downloaded wheel (pip) or the `integrity` value of the tarball (npm).
* "Licence file(s)" are the files whose names match licence (either spelling), copying or notice inside the dist-info
  directory (pip) or at the package root (npm), with the sha256 of each file's bytes.
* CI behind a package mirror may need `PIP_INDEX_URL` (and the npm registry setting) pointed at it; the hashes and
  integrity values stay the same.

| Ecosystem | Package | Version | Artifact | Archive hash | Declared licence | Licence file(s) in archive | Flag |
|---|---|---|---|---|---|---|---|
| npm | @honcho-ai/sdk | 2.5.1 | `honcho-ai-sdk-2.5.1.tgz` | `sha512-CYUy22D4+wnrLMLcuHfhLVhSOvuvv8wBievDfbMkT4B+PMAjuDlPVGdP+3/aWaKd2J+M6ufFD78jyavft9yZXw==` | Apache-2.0 | no licence file in archive | no licence file in archive |
| npm | zod | 4.0.0 | `zod-4.0.0.tgz` | `sha512-9diLdTPc/L7w/5jI4C3gHYNiGHDV9IZYxo1e5LSD8cabi65WVTWWb+g2BGPEpUUCOxR4D+6O5B0AzyMdUAXwrw==` | MIT | `package/LICENSE` sha256:`3f1189b28e3866e0d979968d466b78f813f76827cfdca1fbb124cc0a5c8841f8` | - |
| pip | annotated-types | 0.8.0 | `annotated_types-0.8.0-py3-none-any.whl` | `sha256:f072f4d804ea359e4eaf198b1af7a8b0943881a87f31bb764f8bf219bb9419e0` | MIT | `annotated_types-0.8.0.dist-info/licenses/LICENSE` sha256:`fe1049884b1a0d9342901e88e07f32925d24b3121d9972b6a6805fb9824b095d` | - |
| pip | anyio | 4.15.1 | `anyio-4.15.1-py3-none-any.whl` | `sha256:6152fdbbf9a77fdec97731721bebf7c4c44f7c29b424b0065826173efc7ed101` | MIT | `anyio-4.15.1.dist-info/licenses/LICENSE` sha256:`5361ac9dc58f2ef5fd2e9b09c68297c17f04950909bbc8023bdb82eacf22c2b0` | - |
| pip | certifi | 2026.7.22 | `certifi-2026.7.22-py3-none-any.whl` | `sha256:62f22742b58a1a33014a2b6b706588a8d7e2a88ae7bd1a6ebe8c992928483775` | MPL-2.0 | `certifi-2026.7.22.dist-info/licenses/LICENSE` sha256:`e93716da6b9c0d5a4a1df60fe695b370f0695603d21f6f83f053e42cfc10caf7` | NOT plainly MIT/Apache-2.0/BSD/ISC/PSF |
| pip | colorama | 0.4.6 | `colorama-0.4.6-py2.py3-none-any.whl` | `sha256:4f1d9991f5acc0ca119f9d443620b77f9d6b33703e51011c16baf57afb285fc6` | License :: OSI Approved :: BSD License | `colorama-0.4.6.dist-info/licenses/LICENSE.txt` sha256:`cac35c02686e5d04a5a7140bfb3b36e73aed496656e891102e428886d7930318` | licence declared only as a trove classifier; BSD variant not stated (no SPDX expression) |
| pip | h11 | 0.16.0 | `h11-0.16.0-py3-none-any.whl` | `sha256:63cf8bbe7522de3bf65932fda1d9c2772064ffb3dae62d55932da54b31cb6c86` | MIT | `h11-0.16.0.dist-info/licenses/LICENSE.txt` sha256:`37db5bb85926db28a427a25867f10b1232003aea1be69ccb851138adb8e6f361` | - |
| pip | honcho-ai | 2.5.1 | `honcho_ai-2.5.1-py3-none-any.whl` | `sha256:ef354c086b05d911b8f9db14cc552488ffd332e57947ee0378bbb3657f51cd7e` | Apache-2.0 | no licence file in archive | no licence file in archive |
| pip | httpcore | 1.0.9 | `httpcore-1.0.9-py3-none-any.whl` | `sha256:2d400746a40668fc9dec9810239072b40b4484b640a8c38fd654a024c7a1bf55` | BSD-3-Clause | `httpcore-1.0.9.dist-info/licenses/LICENSE.md` sha256:`fdcb59154c74cbaba16a11242f7740bea9f23d6feb5547917d8c5f94a80392a5` | - |
| pip | httpx | 0.28.1 | `httpx-0.28.1-py3-none-any.whl` | `sha256:d909fcccc110f8c7faf814ca82a9a4d816bc5a6dbfea25d6591d6985b8ba59ad` | BSD-3-Clause | `httpx-0.28.1.dist-info/licenses/LICENSE.md` sha256:`4ec59d544f12b5f539a3a716fd321ac58ccd8030b465221f2c880200cdf28d8d` | - |
| pip | idna | 3.20 | `idna-3.20-py3-none-any.whl` | `sha256:ab7ae7122974553370f0bdb919e1a960b2cd1bc1ef0276416d896db81c14582c` | BSD-3-Clause | `idna-3.20.dist-info/licenses/LICENSE.md` sha256:`1a9a4f0e3d479a27240ddd59a9137a66ab4a0f9dfdc8ca6188cc0bfd85187f04` | - |
| pip | iniconfig | 2.3.1 | `iniconfig-2.3.1-py3-none-any.whl` | `sha256:9121e2c1fdb355232495be3194c8dfe87ccc2d5dee45947b78e68f499790d7a7` | MIT | `iniconfig-2.3.1.dist-info/licenses/LICENSE` sha256:`3409fa91f7ace557894632676656e32264fe5ef7581535725dc9a23774551bd4` | - |
| pip | packaging | 26.3 | `packaging-26.3-py3-none-any.whl` | `sha256:d7193f7c8e4e93f444fde0262bf90af30e16fa0ad0ad44cb553c87339b23cd1c` | Apache-2.0 OR BSD-2-Clause | `packaging-26.3.dist-info/licenses/LICENSE` sha256:`cad1ef5bd340d73e074ba614d26f7deaca5c7940c3d8c34852e65c4909686c48`<br>`packaging-26.3.dist-info/licenses/LICENSE.APACHE` sha256:`0d542e0c8804e39aa7f37eb00da5a762149dc682d7829451287e11b938e94594`<br>`packaging-26.3.dist-info/licenses/LICENSE.BSD` sha256:`b70e7e9b742f1cc6f948b34c16aa39ffece94196364bc88ff0d2180f0028fac5` | NOT plainly MIT/Apache-2.0/BSD/ISC/PSF (compound expression, recorded verbatim; not interpreted) |
| pip | pluggy | 1.6.0 | `pluggy-1.6.0-py3-none-any.whl` | `sha256:e920276dd6813095e9377c0bc5566d94c932c33b27a3e3945d8389c374dd4746` | MIT | `pluggy-1.6.0.dist-info/licenses/LICENSE` sha256:`d6b65e6c213a5d0b577911d34d6e5949b9f59d76c238c5071a2f3fc16cfb2606` | - |
| pip | pydantic | 2.14.0 | `pydantic-2.14.0-py3-none-any.whl` | `sha256:15fab1bea6f1dc5003b54fc2ecab230c1fd1dbade2acd4addc52d81e32416d4b` | MIT | `pydantic-2.14.0.dist-info/licenses/LICENSE` sha256:`a9e186f3ca16b5eef84318e7a701721351a00cb7b8ae3a4394b67b49e3529ef3` | - |
| pip | pydantic_core | 2.50.0 | `pydantic_core-2.50.0-cp313-cp313-manylinux_2_17_x86_64.manylinux2014_x86_64.whl` | `sha256:e58acd43ac8d3905659c1d5309318dd576243e723dd7c3b5dd4f555479b77d4b` | MIT | `pydantic_core-2.50.0.dist-info/licenses/LICENSE` sha256:`2afdd30d54b4d62b6f488a6bcc1546e84ec5061f13f4209c03d012348783795a` | - |
| pip | Pygments | 2.21.0 | `pygments-2.21.0-py3-none-any.whl` | `sha256:2363c69b61c4a97c838da3b130dcd6468f4848992b21a82f2a63ec34377137d9` | BSD-2-Clause | `pygments-2.21.0.dist-info/licenses/LICENSE` sha256:`a9d66f1d526df02e29dce73436d34e56e8632f46c275bbdffc70569e882f9f17` | - |
| pip | pytest | 9.1.1 | `pytest-9.1.1-py3-none-any.whl` | `sha256:37a86b45efb9a47a61a36449063e8e18d0cab3161329fc099eb21783169c4f0c` | MIT | `pytest-9.1.1.dist-info/licenses/LICENSE` sha256:`ca836a5f9ecca3b2f350230faa20a48fb8b145653b5568d784862df864706b9b` | - |
| pip | typing-inspection | 0.4.4 | `typing_inspection-0.4.4-py3-none-any.whl` | `sha256:65b8397ba37ccbce054456aaccddfc91e6e3083c92824df348d96ca832f3f147` | MIT | `typing_inspection-0.4.4.dist-info/licenses/LICENSE` sha256:`804b59b25f2c31bd278f9202a19ae49a3945aa2664387e2d0a128c7cacc61ec3` | - |
| pip | typing_extensions | 4.16.0 | `typing_extensions-4.16.0-py3-none-any.whl` | `sha256:481caa481374e813c1b176ada14e97f1f67a4539ce9cfeb3f350d78d6370c2e8` | PSF-2.0 | `typing_extensions-4.16.0.dist-info/licenses/LICENSE` sha256:`3b2f81fe21d181c499c59a256c8e1968455d6689d269aa85373bfb6af41da3bf` | - |

## Flags

* npm `@honcho-ai/sdk` 2.5.1: no licence file in archive.
* pip `certifi` 2026.7.22: NOT plainly MIT/Apache-2.0/BSD/ISC/PSF.
* pip `colorama` 0.4.6: licence declared only as a trove classifier; BSD variant not stated (no SPDX expression).
* pip `honcho-ai` 2.5.1: no licence file in archive.
* pip `packaging` 26.3: NOT plainly MIT/Apache-2.0/BSD/ISC/PSF (compound expression, recorded verbatim; not interpreted).
