# Third-party notices

The MIT license in [LICENSE](LICENSE) covers Codex Monitor's original source,
documentation and generated artwork. Dependencies retain their own licenses.

The application's **Settings → Open-source licenses** page includes the shipped
components' original license text and notices. Its catalog is generated from the
restored NuGet packages by `scripts/Update-LicenseCatalog.ps1` and embedded in
`src/CodexMonitor.Core/Licenses/components.json`. Version-pinned supplemental
notices are preserved under `third-party/` when a package archive omits them.

Microsoft Windows App SDK, Windows SDK, WinRT projections and .NET runtime
components are distributed under their respective terms. Markdown rendering is
implemented in this project. JavaScript syntax parsing uses Acornima
(BSD-3-Clause), including its upstream notices. MSTest and Axe.Windows are used
for development and testing.

Codex CLI is an external dependency for live connections and is not bundled.
Codex Monitor is an independent project and is not affiliated with OpenAI or
Microsoft. Their product names identify compatible products and do not imply
endorsement or grant trademark rights.
