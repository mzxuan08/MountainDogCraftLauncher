# Packaging quick reference

- Windows all-in-one: `pwsh ./packaging/package.ps1`
- Build/publish only: `pwsh ./packaging/publish.ps1`
- macOS on a real Mac: publish, assemble `.app`, then run `packaging/macos/sign-and-notarize.sh <path-to-MDCL.app>` with the required signing/notary environment variables.

The Windows packaging script can assemble a macOS `.app` layout from a Windows cross-publish, but it is deliberately named `UNSIGNED` and marked `cross-built-unverified` in the release manifest. Executable bits, Gatekeeper, Keychain, and native game startup cannot be validated on Windows.

Release directory:

```text
artifacts/packages/MDCL-0.1.0-alpha/
  MDCL-0.1.0-alpha-win-x64.zip
  MDCL-0.1.0-alpha-osx-arm64-UNSIGNED.app.tar.gz
  MDCL-0.1.0-alpha-osx-x64-UNSIGNED.app.tar.gz
  SHA256SUMS.txt / SHA256SUMS.json
  release-manifest.json
  THIRD-PARTY-NOTICES.md
  dependency-lockfiles/
```
