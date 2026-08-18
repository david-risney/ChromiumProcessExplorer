# Release packaging

Tagged releases publish self-contained Windows packages for `win-x64` and
`win-arm64`. Each ZIP contains:

- `cpe.exe`, the command-line interface and handle-query worker;
- `ChromiumProcessExplorer.exe`, the WPF GUI;
- `cpe-broker.exe`, the optional privileged broker;
- `cpe-mcp.exe`, the MCP server;
- `README.md` and this release guide.

Self-contained, single-file publishing makes the packages runnable without a
separate .NET installation. The tradeoff is a larger download and one package
per CPU architecture. Symbols, XML documentation, intermediate build output,
test binaries, and repository automation files are excluded. Releases are not
code-signed until a signing identity and protected signing workflow are
configured.

## Install and uninstall

1. Download the ZIP matching the machine architecture and
   `SHA256SUMS.txt` from the GitHub release.
2. Verify the SHA-256 checksum, then extract the ZIP to a user-chosen folder.
3. Run `cpe.exe --version` and `cpe.exe --help`, or start
   `ChromiumProcessExplorer.exe`.
4. Optionally add the extracted folder to `PATH`.

No installer, service, driver, registry registration, or system-wide state is
created. Uninstall by stopping the applications and deleting the extracted
folder. User-data and diagnostic artifacts inspected by the tool are never
owned or removed by uninstalling it.

## Administrator behavior

All executables use `asInvoker`; none automatically requests elevation. Basic
discovery works unelevated but inaccessible processes, handles, modules, and
registrations are reported as partial coverage. For privileged operations,
explicitly start `cpe-broker.exe` from an elevated terminal and leave the CLI,
GUI, MCP server, and Copilot client unelevated. This keeps elevation isolated
to the broker's fixed, read-only protocol instead of elevating every frontend.

## Versioning and release process

The repository uses semantic versions. Local builds use the version prefix in
`Directory.Build.props`; a tag such as `v1.2.3` injects `1.2.3` into every
published assembly. `cpe.exe --version` reports the release version, while
`cpe.exe --version --json` and the Core `ProductVersion` API expose complete
informational/source-revision metadata.

The release workflow restores, builds, tests, verifies formatting, publishes
both architectures, checks the x64 CLI's reported version, creates ZIPs and
SHA-256 checksums, and publishes a GitHub release with generated release notes.
