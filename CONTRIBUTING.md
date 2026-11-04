# Contributing

Open an issue for a reproducible bug or a focused proposal. Include Windows and
Codex CLI versions, the relevant event type, and a minimal synthetic example.
Remove conversation text, local paths, account details and credentials before
sharing logs or screenshots.

Use C# and the native WinUI controls already in the project. Keep user-facing
copy short, localizable and accessible. Add strings to every locale. Preserve
keyboard navigation, focus, light/dark/system themes, RTL and scroll positions.

Changes to transcript handling should use the shared adapter and reducer.
Local files are authoritative before attachment; app-server is authoritative
after attachment. Preserve per-turn metadata and do not interpret a historical
activity marker as a running process. Do not start inference, change approval
policy or write session files as part of discovery or passive viewing.

Run `./scripts/Test.ps1` for core changes. Run `./scripts/Test-Layout.ps1` on an
unlocked Windows desktop for UI changes and inspect its screenshots. Publish
with `./scripts/Build-Installer.ps1`, then run
`./scripts/Test.ps1 -IncludeRelease` for release-affecting changes. Update
`docs/protocol-coverage.md` when adapting protocol fields or events.

Pull requests should explain the resulting behavior and validation. Use
synthetic sessions; never commit real rollouts, authentication files, user
settings, build outputs or copied private conversations. Contributions are
provided under the project's MIT license.
