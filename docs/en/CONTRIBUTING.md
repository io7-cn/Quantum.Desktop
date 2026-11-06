# Contributing

[简体中文](../zh-CN/CONTRIBUTING.md)

Branch from the current `main` and keep commits focused. Describe the behavior change and how you verified it. Run the [desktop checks](README.md#development-and-release). For platform code, verify the corresponding native target.

## Branch protection

The active GitHub ruleset `Protect main` protects the default branch, currently `main`, without bypass actors, including administrators. Changes must go through a pull request with at least one independent approval. New reviewable commits dismiss stale approvals, the latest push requires approval from someone other than its pusher, and review discussions must be resolved before merging.

The GitHub Actions checks `standards`, `test` and `windows` are required, and the pull request must be up to date with the target branch. Direct pushes, force pushes and branch deletion are blocked. Repository permissions limit merges to designated maintainers. Changes to authentication, authorization, secrets or critical delivery controls require the additional independent reviews specified by the [review standards](../../.standards/docs/en/01-governance/review-standards.md).

The `test` gate requires Linux core tests plus macOS storage tests and unsigned Catalyst Release builds for Apple Silicon and Intel. Signing and installer acceptance remain release checks.

## Plugin capabilities

When changing a public plugin capability, review the .NET SDK, the TypeScript SDK and the host adapters together. Update the affected tests and both editions of each affected Desktop document. Contributions follow the [Apache License 2.0](../../LICENSE).

Attribution and third-party license notices are provided in [NOTICE](../../NOTICE).
