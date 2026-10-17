# Building SlimCode Desktop

How to build, run, package and release SlimCode from source. For using the
app, see the [README](../README.md); for architecture and conventions, see
[DEVELOPER.md](DEVELOPER.md).

## Build & run

Requires the .NET 10 SDK.

```pwsh
dotnet build         # any errors here are real compile errors
dotnet run           # launches the Avalonia GUI
```

## Cross-compiling / checking the other platform

`$(TargetOS)` selects which `Platform/Platform.<OS>.cs` compiles. It defaults
to the build host, so the command above needs no arguments. To build the other
side, which is worth doing regularly (see below):

```pwsh
dotnet build -p:TargetOS=Linux
```

Only one `Platform.<OS>.cs` is ever compiled, so **the compiler cannot warn you
that the inactive one has drifted**. Adding a member on one side and forgetting
the other stays invisible until you're actually on that OS.

`publish.ps1` is the backstop: it builds every target, so a broken
`Platform.Linux.cs` fails the publish rather than shipping. The command above is
the fast local version of the same check. It's worth running while you're still
editing `Platform/`, rather than finding out at release time.

## Packaging

```pwsh
.\publish.ps1                     # win-x64 + linux-x64, self-contained
.\publish.ps1 -Runtimes win-x64   # Windows only, skips the Linux drift check
```

Artifacts land in `dist\` as `slimcode_desktop_v{version}_{rid}.{zip|tar.gz}`.
Linux builds are packaged as `.tar.gz`. Because they're produced on Windows,
whose filesystem has no executable bit to record, a locally built binary needs a
`chmod +x SlimCodeDesktop` after extraction. The script prints this reminder.
Archives from a GitHub release already have the bit set (see below).

## Releasing

Releases are cut by the **Release** workflow
([.github/workflows/release.yml](../.github/workflows/release.yml)). It never runs
on its own; start it by hand from the Actions tab or with:

```pwsh
gh workflow run release.yml              # draft release (default)
gh workflow run release.yml -f draft=false -f prerelease=true
```

Day-to-day work happens on `dev`; `main` always points at the latest release.

1. Bump `<Version>` in `SlimCodeDesktop.csproj` on `dev`, commit, push.
2. Run the workflow (from `dev` or `main` -- either way it releases the tip of
   `dev`). It fails early if the tag `v{version}` already exists, so a
   forgotten bump can't overwrite a release, and if `main` has commits `dev`
   lacks (merge `main` into `dev` once, then rerun).
3. A Windows runner executes `publish.ps1` (Windows tests + both archives); an
   Ubuntu runner runs the Linux tests in parallel. Both must pass.
4. The Linux tarball is repacked on Ubuntu with `chmod +x SlimCodeDesktop`.
5. `main` is fast-forwarded to the released `dev` commit -- no merge commit,
   and never to a commit whose build failed.
6. The release `v{version}` is created on that commit with both archives plus
   `SHA256SUMS.txt` attached. Drafts must be published by hand on GitHub.

A release whose `dev` commits change `.github/workflows/` needs the
`RELEASE_PUSH_TOKEN` secret: GitHub's built-in token may not move a branch
across workflow changes. Use a fine-grained PAT for this repo only with
**Contents** and **Workflows** set to read and write.
