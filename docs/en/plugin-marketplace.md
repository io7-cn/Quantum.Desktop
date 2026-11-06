# Plugin marketplace

[简体中文](../zh-CN/plugin-marketplace.md)

Desktop browses reviewed plugins from Quantum Platform at `/marketplace`. The desktop plugin runtime validates the installation and switches versions as a transaction.

## Connect

Set `QUANTUM_PLATFORM_URL` before launching to select a development or private deployment. It accepts an absolute HTTP or HTTPS URL, such as `https://plugins.example.com`. Invalid values fall back to the official platform. `QUANTUM_SDK_VERSION` can override the plugin SDK version for testing. By default Desktop reads the informational version of its loaded `Quantum.Plugin.Abstraction` assembly, preserves prerelease identifiers and omits build metadata. The Desktop application version and `QUANTUM_VERSION` do not determine plugin compatibility.

## Install

1. Desktop searches published plugins via Quantum Platform's JSON-based remote procedure call interface (JSON-RPC). It uses the shared Platform client and NOF to match each response ID to its request and read the NOF result.
2. Platform selects the highest published semantic version compatible with the plugin SDK loaded by Desktop. It compares `sdkVersion` against each release’s `sdkVersionSupport`. Releases that only declare a legacy host range are not selected for SDK compatibility.
3. Desktop downloads the ZIP and verifies the stated size and SHA-256 digest.
4. The runtime checks the manifest, dependencies, entry points, database migrations and package compatibility.
5. After confirmation, Desktop updates `Modules`. If replacement or startup fails, it restores the previous files and runtime. Committed SQL migrations remain applied.

## Desktop and publishing portal

Desktop handles browsing, downloading, installation and upgrades. Signed-in authors can browse their own versions and install pending review builds locally. Rejected builds are unavailable for download. Operating system secure storage protects the session.

The platform's `/portal/` lets authors register, manage plugins, upload ZIPs and inspect review status. Reviewers and administrators publish or reject releases there. When email delivery is configured, registration and review notifications are sent by the platform.

Publish new releases using `quantum-cli plugins publish ./dist/plugin --sdk-version-support "0.3.0-rc.3"`. Both .NET and Web SDK packages share the SDK release version. Deploy the updated Platform and its additive database migration before upgrading Desktop or the publishing CLI. Historical releases retain their host ranges; publish a new plugin version with an explicit SDK range to make it available to SDK-based clients.
