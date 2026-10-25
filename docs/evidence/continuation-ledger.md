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
