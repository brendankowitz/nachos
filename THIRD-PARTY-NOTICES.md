# Third-party notices

This file records the scoped DacFx, SqlClient SNI runtime and SQL Server Types
entries below. It is not a complete inventory of Nachos dependencies. Release
producers must supply all other required third-party notices and license texts.

## Microsoft.SqlServer.DacFx 170.4.83

Publisher: Microsoft. This component is governed by **Microsoft Software License
Terms, Microsoft SQL Server Data-Tier Application Framework**, not the Nachos
MIT license.

The complete, unmodified [license text](eng/licenses/Microsoft.SqlServer.DacFx/170.4.83/license.txt)
accompanies this notice. The [published package license](https://www.nuget.org/packages/Microsoft.SqlServer.DacFx/170.4.83/License)
is another source. The license text, including its regional provisions, controls;
this notice is not a substitute for it.

The [repository owner's approval](https://github.com/brendankowitz/nachos/issues/2#issuecomment-6048060868)
is an engineering-policy exception for this exact package version in the Nachos
API and CLI. That approval itself does not cover other versions, packages or
unrelated artifacts. The separate
[Microsoft-primary policy decision](https://github.com/brendankowitz/nachos/pull/6#issuecomment-6083936795)
supports the other exact records below; it does not expand this DacFx record.
Any version change requires fresh technical identity and complete license-text
review and authorization.

### Redistribution conditions and release obligations

Section 3 identifies the software as Distributable Code and permits distribution
in applications you develop, subject to its conditions. In particular:

- Section 3(b)(i) requires significant primary functionality in the application.
- Section 3(b)(ii) requires distributors and external end users to agree to terms
  protecting the Distributable Code and Microsoft at least as much as the
  Microsoft agreement.
- Section 3(b)(iii) requires indemnifying, defending, and holding Microsoft
  harmless from claims, including attorneys' fees, related to distribution or
  use of the application, except to the extent a claim is based solely on the
  unmodified Distributable Code.
- Section 3(c) restricts misleading use of Microsoft's trademarks/trade dress
  and source-code modification or distribution that would subject the covered
  code or Microsoft intellectual property to the source-disclosure or
  modification-rights licenses described there.

The license's remaining conditions, including notice-preservation and export
restrictions, also apply. The release owner must address the required downstream
terms, agreement/assent process, and indemnification obligations before
redistribution. No such process or compliance determination is implemented by
this notice or the license checker.

Copying these files or passing an engineering audit does not constitute EULA
acceptance, evidence of downstream assent, a waiver, or certification of legal
compliance.

## Microsoft.Data.SqlClient.SNI.runtime 6.0.3

Publisher: Microsoft. This component is governed by **Microsoft Software
License Terms, MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY**, not the Nachos MIT license.
The complete, unchanged [LICENSE.txt](eng/licenses/Microsoft.Data.SqlClient.SNI.runtime/6.0.3/LICENSE.txt)
accompanies this notice. The [exact-version package license](https://www.nuget.org/packages/Microsoft.Data.SqlClient.SNI.runtime/6.0.3/License)
is another source; the complete vendor terms control, not this summary.

The [owner's Microsoft-primary acceptance](https://github.com/brendankowitz/nachos/pull/6#issuecomment-6083936795)
is engineering policy for reviewed exact identities and primary terms. It is
not a new publisher grant, EULA assent or evidence of redistribution compliance.

SNI's separate Distributable Code conditions remain applicable, including use
within applications rather than standalone distribution, protective terms for
distributors and external end users, and indemnification with the document's
stated exception. Data/consent duties, export restrictions, and retention of
Microsoft and supplier notices also remain release-owner obligations.
Separately applicable third-party components retain their own terms; this
primary-policy acceptance does not waive supplemental obligations or select
an OR-license branch.

## Microsoft.SqlServer.Types 170.1000.7

Publisher: Microsoft. The complete, unchanged
[license.md](eng/licenses/Microsoft.SqlServer.Types/170.1000.7/license.md)
accompanies this notice, including its English and French provisions. The
[official exact-version package license](https://www.nuget.org/packages/Microsoft.SqlServer.Types/170.1000.7/License)
also reproduces the evaluation terms; no replacement governing terms have
been established by the retained provenance checks.

The document is titled **MICROSOFT PRE-RELEASE SOFTWARE LICENSE TERMS /
MICROSOFT SQL SERVER VNEXT COMMUNITY PREVIEW** and states **TERM until
09/30/2022**. It describes internal evaluation/feedback, excludes testing in a
live operating environment absent another Microsoft agreement, and includes
restrictions on publishing copies, transfer and commercial hosting.

**Unresolved governing-terms release hold:** the
[owner's Microsoft-primary engineering-policy acceptance](https://github.com/brendankowitz/nachos/pull/6#issuecomment-6083936795)
does not supersede this document, correct a possible publisher packaging
mistake, establish production/redistribution rights or constitute EULA assent.
The release owner must resolve which terms govern this exact package and the
required rights before release. A passing engineering audit records evidence,
not release clearance, and does not automatically enforce that separate hold.

This package is not relicensed MIT. Any applicable third-party terms and
required notices remain obligations. Copying this notice and the license
document neither resolves the governing-terms question nor waives it.

## Ordinary o200k_base tokenization

`TiktokenTokenCounter` uses the existing MIT-licensed
`Microsoft.ML.Tokenizers` and `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0
packages. Its ordinary-text pretokenization pattern comes from OpenAI tiktoken;
the vocabulary remains in Microsoft's package, not a new vendored data file.
Both special-token recognition and special-aware pretokenization are disabled.

The non-public resource name `o200k_base.tiktoken.deflate` is an explicit
compatibility dependency. Initialization fails closed unless its compressed
SHA-256 is `88b2a54dcedc68d39b1af4b8dc744adba8eca01310b7d07a687f1add3be75524`.
The frozen [feasibility evidence](research/token-feasibility/README.md) records
its equivalence to the canonical vocabulary with SHA-256
`446a9538cb6c348e3516120d7c08b09f57c36495e2acfffe59a5bf8b0cfb1a2d`.
Dependency/resource changes require renewed identity verification, not a
fallback to special-token counting.

The existing packages' complete licenses and third-party notices must still
accompany redistribution. Their OpenAI tokenizer/vocabulary notice and the
retained tiktoken 0.14.0 license in the frozen bundle give the following terms
for the copied pattern:

MIT License

Copyright (c) 2022 OpenAI, Shantanu Jain

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
