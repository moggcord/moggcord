# Build and publish a release

[Documentation](../README.md) / Developers / Releasing

This guide is for repository maintainers. Publishing exposes installers to users and can update the release selected by clients. Build and verify the intended commit before choosing the publish option.

## Prepare the version

1. Work from the intended clean commit and preserve unrelated/local plugin work.
2. Update `package.json` and the Windows `.csproj` version together.
3. Add `docs/releases/<version>.md` and link it in the release index.
4. Run the relevant [tests](testing.md), `pnpm buildStandalone` and `node scripts/build/verify-release.mjs`.
5. Review defaults, release notes and package names before publication.

The release workflow reads the package version. Tag-triggered releases require the tag's version to match it. Embedded `build-info.json` must also agree; editing a version label alone does not rebuild stale code.

## Preferred workflow

Use **Actions → Build & Release Moggcord → Run workflow** on the intended branch.

| Publish input | Result |
| --- | --- |
| `false` | Build and verify platform artifacts without publishing a release |
| `true` | Publish after the Windows, macOS and Linux jobs all pass |

Pushing a `v*` tag also triggers the release workflow. Do not both dispatch and push a tag for the same release unless you deliberately need a separate run. Inspect any workflow artifact before assuming that a successful build published it.

## Platform jobs

**Windows** uses Node 24, pnpm 10.30.3 and .NET 8. It builds the desktop bundle, runs plugin/installer checks, collects the Windows support assets, validates the embedded installer build and produces release metadata.

**macOS and Linux** build on their own runner OS. They prepare the shared interface and verified desktop files with a pinned Equilotl helper, run native fixture checks, package the installer and launch its verification mode. macOS also checks the app signature and DMG. Linux emits AppImage and DEB packages.

The publish job depends on all platform jobs. It merges their artifacts, creates checksums and validates the asset layout. It first publishes technical files as the prerelease `updates-v<version>` with `make_latest: false`, then publishes the four installer packages and any signed checksums as `v<version>` and marks that release latest. **Build & Release Moggcord** is the installer publication workflow.

## Automatic signing

Enable each signing step through the repository's **Settings → Secrets and variables → Actions** once its signing credentials are ready.

| Platform | Actions variables | Actions secrets |
| --- | --- | --- |
| Windows | `MOGGCORD_WINDOWS_SIGNING=true`, `MOGGCORD_AZURE_CLIENT_ID`, `MOGGCORD_AZURE_TENANT_ID`, `MOGGCORD_AZURE_SUBSCRIPTION_ID`, `MOGGCORD_WINDOWS_SIGNING_ENDPOINT`, `MOGGCORD_WINDOWS_SIGNING_ACCOUNT`, `MOGGCORD_WINDOWS_SIGNING_PROFILE` | Azure authenticates through GitHub OIDC. Assign the Artifact Signing Certificate Profile Signer role to the federated identity for the intended release refs. |
| macOS | `MOGGCORD_MAC_SIGNING=true`, `MOGGCORD_MAC_CERT_THUMBPRINT`, `MOGGCORD_APPLE_NOTARY_KEY_ID`, `MOGGCORD_APPLE_NOTARY_ISSUER` | `MOGGCORD_MAC_CERTIFICATE` (base64 Developer ID Application PKCS#12), `MOGGCORD_MAC_CERTIFICATE_PASSWORD`, `MOGGCORD_APPLE_NOTARY_KEY` (App Store Connect team API key, complete `.p8` contents) |
| Linux and release checksums | `MOGGCORD_CHECKSUM_SIGNING=true`, `MOGGCORD_GPG_FINGERPRINT` (full fingerprint) | `MOGGCORD_GPG_PRIVATE_KEY` (armored OpenPGP secret key), `MOGGCORD_GPG_PASSPHRASE` if protected |

Windows verifies the timestamped Authenticode signature before checksums are generated. macOS signs the native helper and the app, then notarizes and staples the final DMG. When checksum signing is enabled, publish the OpenPGP public key and its fingerprint through an independently trusted project channel. Users can run `gpg --verify SHA256SUMS.txt.asc SHA256SUMS.txt` followed by `sha256sum --check --ignore-missing SHA256SUMS.txt` in the directory containing the installer downloads.

For local Windows builds, install SignTool with the Windows SDK and set `MOGGCORD_WINDOWS_CERT_THUMBPRINT` to the code-signing certificate in the current user's personal certificate store. Both the .NET and Rust builds sign and verify their EXE before packaging it. Set `MOGGCORD_SIGNTOOL` if your SDK installed the executable to a custom path. [Windows SignTool](https://learn.microsoft.com/windows/win32/seccrypto/signtool) · [Azure Artifact Signing](https://github.com/Azure/artifact-signing-action) · [Apple notarization](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow)

## Release assets

| Asset | Location | Intended use |
| --- | --- | --- |
| `Moggcord-Installer.exe` | Main release | Windows x64 setup |
| `Moggcord-Installer-macOS.dmg` | Main release | Universal macOS preview setup |
| `Moggcord-Installer-Linux.AppImage`, `.deb` | Main release | Linux x64 preview setup |
| `moggcord-update.zip` | Technical release | Small verified desktop-only update |
| `moggcord-community-compiler.tar.gz` | Technical release | Version-matched source and dependencies for community plugin compilation |
| `moggcord-dist.zip` | Technical release | Full installation/repair bundle |
| `desktop.asar`, `moggcord.asar` | Technical release | Additional bundled application artifacts |
| `version.json` | Technical release | Version, pinned update URLs and generated plugin catalog statistics |
| `installer-verification*.json` | Technical release | Embedded/package verification reports |
| `SHA256SUMS.txt` | Technical release | Checksums covering technical assets and installer packages |

The staging script rejects missing, empty or unrecognized files before creating the upload groups. Do not replace the installer's asset list with a wildcard for the entire staging root. GitHub's own source-code archive links are separate from the uploaded assets.

When migrating from an installer-plus-update main release, retain verified backups and publish the technical copies before removing the old attachments. Clients older than 1.18.44 need one Repair installation because their updater does not know the new technical release path.

## Local packaging

On Windows, after `pnpm buildStandalone`, the release workflow runs asset collection and verification before `scripts/build/windows-installer.ps1`. That script embeds the freshest full bundle and verifies the installer with `--verify-bundle`. Publication bundles the .NET runtime into the single Windows executable, so users do not need a separate .NET Desktop Runtime installation. Use the current .NET 8 SDK when rebuilding to include its runtime patches.

For macOS/Linux, run `pnpm build:platform-installer` on the target platform. Package the generated project under `.codex-temp/platform-installer/<platform>` using `installer-desktop/electron-builder.cjs`, then run `node scripts/build/verify-platform-package.mjs`. Use the workflow as the exact command reference; do not substitute the legacy configuration in `scripts/legacy/windows/`.

The pinned helper's version and hash are in `installer-desktop/upstream.json`. Changing them requires reviewing the source release and updating the expected fingerprint deliberately. A fingerprint mismatch should stop packaging.

## After publication

Check the public asset URLs, the version metadata, checksum entries and verification reports. Exercise an update from an older build in an isolated target. Confirm that the small update archive is selected by compatible clients and that no failed download is reported as installed.

Do not treat artifact verification as an account-login or voice-session test. Keep preview/signing limitations visible in release notes. If a published release is wrong, diagnose it and publish a corrected version rather than silently changing a tag's history.

## Source of truth

[Release workflow](../../.github/workflows/release.yml) · [Asset layout](../../scripts/build/release-layout.mjs) · [Windows packaging](../../scripts/build/windows-installer.ps1) · [Native preparation](../../scripts/build/platform-installer.mjs) · [Pinned helper](../../installer-desktop/upstream.json) · [Small archive](../../scripts/build/update-archive.mjs) · [Version verification](../../scripts/build/verify-release.mjs)
