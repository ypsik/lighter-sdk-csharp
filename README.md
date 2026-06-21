# Lighter.Native (C# bindings for lighter-go's signer)

C# P/Invoke bindings for [`elliottech/lighter-go`](https://github.com/elliottech/lighter-go)'s
native `lighter_signer` shared library — the reference implementation for
signing & hashing Lighter (zkLighter) perpetual futures transactions.

This repo automates the full pipeline: pull official native release assets →
generate P/Invoke bindings from the official header via ClangSharp → build &
smoke-test on every target platform → pack a multi-RID NuGet package.

## Supported platforms

| RID | Native asset source |
|---|---|
| `win-x64` | `lighter-signer-windows-amd64.dll` (official release asset) |
| `linux-x64` | `lighter-signer-linux-amd64.so` (official release asset) |
| `linux-arm64` | `lighter-signer-linux-arm64.so` (official release asset) |
| `osx-arm64` | `lighter-signer-darwin-arm64.dylib` (official release asset) |

**`osx-x64` (Intel Mac) is intentionally NOT supported.** lighter-go's
releases do not ship a `darwin-amd64` asset, and this repo deliberately does
not cross-compile one (would require bootstrapping an osxcross SDK toolchain
in CI, which is brittle and was decided against — see project history). If
you need Intel-Mac support, build `lighter-signer` from Go source yourself
for `GOOS=darwin GOARCH=amd64` and drop the resulting `.dylib` into
`src/Lighter.Native/runtimes/osx-x64/native/liblighter_signer.dylib` before
packing.

## Pipeline

1. **`fetch-native-assets`** — downloads the `.dll`/`.so`/`.dylib` + `.h`
   files for a given `lighter-go` tag directly from its GitHub release.
   Verifies all four platform headers are byte-identical before proceeding
   (cgo generates the same C export signatures regardless of `GOOS`/`GOARCH`,
   so they should always match — if they ever don't, the build fails loudly
   instead of silently picking one as canonical).

2. **`generate-bindings`** — runs `ClangSharpPInvokeGenerator` against the
   canonical header to produce `NativeMethods.g.cs`. This works cleanly here
   because the cgo preamble declares plain C structs (`char*`, `uint8_t`,
   `int64_t` fields only — no Go-specific `GoString`/`GoSlice` types leak
   into the exported header).

3. **`build-managed`** — copies native assets into `runtimes/{rid}/native/`,
   builds the managed wrapper, and runs the smoke tests in
   `tests/Lighter.Native.Tests` **on each platform**.

4. **`pack-and-publish`** — packs the multi-RID `.nupkg` and optionally
   pushes to NuGet.org (gated behind a manual `workflow_dispatch` boolean +
   the `NUGET_API_KEY` repo secret — never runs automatically on push).

## The one real risk: struct-by-value ABI

Four of the native functions return a C struct **by value**
(`StrOrErr`, `SignedTxResponse`, `ApiKeyResponse`) rather than via an output
pointer. Struct-by-value return is the one place where Windows x64, Linux/
macOS x64 (SysV), and ARM64 (AAPCS) calling conventions genuinely differ —
small structs can come back in registers on some ABIs and via a hidden
pointer on others. .NET's P/Invoke marshaller handles this correctly *for
blittable structs with [StructLayout(Sequential)]*, which is what ClangSharp
generates by default for this header — but "should be fine by default" is
exactly the kind of claim this codebase does not want to ship un-verified.

That's why `tests/Lighter.Native.Tests/NativeSmokeTests.cs` runs on every
platform in CI before packing: it calls `GenerateAPIKey()` repeatedly and
asserts the returned key pair is well-formed hex, distinct across calls, and
doesn't crash the allocator across 50 iterations of native `Free()`. See the
comments in `src/Lighter.Native/StructFixups.cs` for what a failure here
would look like and how to diagnose it — that file is the designated landing
spot for a manual override if a future `lighter-go` release ever changes a
struct's shape and the CI's `Validate struct-by-value return signatures` step
catches it.

## What's wrapped

All 19 exported signing/client functions have managed wrappers in
`LighterSigner.cs`: `GenerateApiKey`, `CreateClient`, `CheckClient`,
`SignChangePubKey`, `SignCreateOrder`, `SignCreateGroupedOrders`,
`SignCancelOrder`, `SignCancelAllOrders`, `SignModifyOrder`, `SignTransfer`,
`SignWithdraw`, `SignCreateSubAccount`, `SignCreatePublicPool`,
`SignUpdatePublicPool`, `SignMintShares`, `SignBurnShares`,
`SignUpdateLeverage`, `CreateAuthToken`, `SignUpdateMargin`,
`SignStakeAssets`, `SignUnstakeAssets`, `SignApproveIntegrator`.

They all follow the same mechanical pattern (params → native struct-by-value
return → unwrap via the shared `UnwrapSignedTx` helper → `Free()` every
`char*` field), so wrapping them together in one pass made sense rather than
adding them one-by-one across several review iterations.

**What this does NOT mean:** mechanically identical ≠ verified identical.
Only `GenerateApiKey` has a dedicated smoke test today
(`NativeSmokeTests.cs`) — that's the function most exposed to the
struct-by-value ABI risk (see below) and the cheapest to test without a live
testnet connection. The other 18 compile against the generated bindings and
follow the same struct-marshalling discipline, but have NOT been
exercised against a running native lib in CI yet. Before relying on any of
them in the live LEAN connector, add a smoke test for that specific function
(ideally with a known-good test vector compared against the Go or Python
SDK's output for the same input) — exactly the kind of thing that "will be
visible later if something doesn't run," per the project's working style,
rather than something to pre-verify by guessing.

`SignCreateGroupedOrders` additionally passes an array of `OrderLeg` structs
by pointer — double-check `OrderLeg`'s field order against whatever
`NativeMethods.g.cs` actually generates for `CreateOrderTxReq` once ClangSharp
has run; the manual mirror in `LighterSigner.cs` was written from the cgo
preamble source, not from inspecting generated output (no .NET runtime was
available to run ClangSharp during initial authoring of this repo).

## Manually triggering a release

This is a `workflow_dispatch` workflow — it does not auto-publish to
NuGet.org. To cut a release:

1. Actions → "Build, Generate Bindings & Pack NuGet" → Run workflow.
2. Set `lighter_go_tag` (defaults to the pinned tag in `lighter-go.version`).
3. Set `package_version` for the resulting NuGet package.
4. Leave `publish_nuget` unchecked for a dry run (artifacts are uploaded for
   inspection either way); check it to push to NuGet.org (requires the
   `NUGET_API_KEY` repo secret to be set first).
