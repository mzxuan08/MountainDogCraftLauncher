# MDCL continuation ledger — plan: docs/DSH-开发任务书.md

Baseline: 46d02ca; branch: codex/mdcl-completion. Existing T00—T12 commits are preserved; this continuation repairs and verifies gaps in task order. No remote is configured; commits remain local.

## Pre-flight

- T01 → T03/T07: instance revisions, schema protection and leases govern all mutations.
- T02 → T04/T06: metadata Java requirements and architecture must reach runtime selection and launch unchanged.
- T03 → T07/T11: completed journals must revalidate artifacts in the actual target; a prior completion cannot suppress repair or a second instance's files.
- T04 → T08: macOS Temurin archives are tar.gz; ZIP-only extraction breaks managed installation.
- T05 → T06/T07: every process/UI log must pass the same redaction boundary.
- T06 → T07: one monitored session must not be overwritten; closing must keep monitoring or stop the owned session.
- T08 → T12: shipped package layout and updater's expected archive layout must agree.
- Ruling: continue in the user-named checkout on a new branch, preserving existing runtime/cache assets, rather than duplicate the large checkout. Cost: branch changes are visible in that checkout.
- Ruling: external Mac, Microsoft client, and signing conditions remain separate manual gates; do not convert build/protocol tests into real-world success.

## Verification

- Baseline locked restore: exit 0. Initial attempt encountered denied default TEMP; rerun with workspace TEMP succeeded.
- Baseline Release tests: Core 19, Integration 108, Desktop 1; 128 passed, 0 failed, 0 skipped. TRX in artifacts/verification/ (not committed because it contains machine paths).
- User selected MIT for MDCL-owned source in this conversation. Third-party terms stay separate.

## Task status

T00–T12: existing implementation accepted only to the extent of recorded evidence; continuation results will be appended per commit. Unrun matrix cases remain unrun.
`nT01: fixed unsupported-schema creation and mismatched manifest identity; both regression tests failed before the fix. Full Release suite: 130 passed, 0 failed. T02: existing deterministic resolver fixtures verified in the same suite; no parser changes in this step.
`nT03: completed journals now reconcile actual artifacts and bind to target identity. Corrupted-file regression failed before fix; verified repair, second-target installation, then cache reuse without another HTTP request. Full Release suite: 131 passed. Ruling: revalidate completed steps through the existing downloader, trading local hashing for reliable repair.
`nT04: added content-detected tar.gz extraction for managed Java, with traversal/link/type/size/count/ratio checks and Unix execution-mode restoration. Five TAR regressions failed against ZIP-only code, then passed. Parent reparse checks now include ancestors outside the extraction root. Full Release suite: 136 passed. Official Adoptium live API returned macOS arm64 JDK 17.0.21+9 as tar.gz; macOS execution remains untested.
`nT05: short labelled OAuth/PKCE/Minecraft secrets and cookies now use the common redactor; MicrosoftLoginResult suppresses refresh-token serialization/ToString. Five regression cases failed before fix. Full Release suite: 141 passed. Actual Microsoft login is still conditional on an MDCL public client and account-owner interaction.
`nT06: process stdout/stderr now pass common redaction before events; rejected Java 8 commands and invalid argfiles no longer leave temporary secrets. Three regressions failed before fix. Full Release suite: 143 passed. Real menu evidence remains independent of process-start/exit tests.
`nT07: native command cancellation reaches the resolver, global task cancellation reaches all long operations, launch/install/stop states are guarded, process logs and UI exceptions are redacted, selection refresh ignores stale results, and close offers continued monitoring by minimization or owned-session shutdown. Two behavior regressions failed before fix; full Release suite: 145 passed. Added opt-in LiveAcceptance runner for repeatable real-network/menu/exit evidence; not included in CI test execution.
`nT08: version 0.2.0-alpha.1; one source of launcher version; packaging now exports tracked source, all lockfiles, MIT plus 40 dependency license records and full available vendor notices; checks PE/Mach-O CPU headers and matching source commit; stores macOS executable modes in TAR. New publication directories preserve prior outputs. CI uses current macos-15 arm64 and macos-15-intel labels and includes updater/package evidence; CI has not been run remotely. Syntax checks and full Release suite: 145 passed. Three final RID packages will be rebuilt after the last code commit.
`nT09: fresh Fabric 0.16.14 installation succeeded, first remap failed again. In-JVM agent and plain .NET Process reproduction show Java NIO writability false while shell JVM succeeds; CreateNoWindow is excluded. F03/F04 remain failed. No product workaround or ACL change. Diagnostic agent removed from the test instance. See continuation-T09.md. Original 1.20.1 independently reached menu twice and exited code 0 twice; screenshots retained.
