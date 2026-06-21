namespace Lighter.Native;

/// <summary>
/// Manual fixups for the structs that the cgo-generated header returns
/// BY VALUE from extern "C" functions:
///
///   StrOrErr           { char* str;  char* err; }
///   SignedTxResponse    { uint8_t txType; char* txInfo; char* txHash; char* messageToSign; char* err; }
///   ApiKeyResponse      { char* privateKey; char* publicKey; char* err; }
///
/// WHY THIS FILE EXISTS (read before touching it):
///
/// ClangSharpPInvokeGenerator generates a [StructLayout(LayoutKind.Sequential)]
/// struct for these and a P/Invoke signature that returns the struct by value.
/// That is *usually* correct on .NET's P/Invoke marshaller, which implements
/// the platform-correct calling convention for struct returns (it is not the
/// same convention on Windows x64, Linux/macOS x64 SysV, and ARM64 AAPCS —
/// small structs can be returned in registers on some ABIs and via a hidden
/// pointer on others). The CLR's interop marshaller handles this transition
/// correctly as long as:
///
///   1. Every field is blittable (all four structs here only have pointer
///      and uint8_t fields => blittable, good).
///   2. The [StructLayout] is Sequential with no surprise padding gaps that
///      diverge from the C compiler's layout for the same struct.
///
/// CreateOrderTxReq is passed BY POINTER (in SignCreateGroupedOrders as
/// `CreateOrderTxReq* cOrders`), not by value, so it carries no return-ABI
/// risk — only the layout has to match, which Sequential already guarantees
/// for this field set (int16/int64/uint32/uint8 mix — verify no manual
/// padding was needed by checking sizeof() in NativeSmokeTests).
///
/// WHAT TO ACTUALLY VERIFY (do not skip — this is why the CI smoke tests
/// in tests/Lighter.Native.Tests run on win-x64, linux-x64, linux-arm64,
/// and osx-arm64 runners before any package is published):
///
///   - Call GenerateAPIKey() on each platform and confirm privateKey/
///     publicKey/err come back as valid pointers (or null for err), not
///     garbage / misaligned data. A misaligned struct-by-value ABI bug
///     typically manifests as the LAST field being garbage while earlier
///     fields look fine, because the marshaller read the struct off-by-one
///     register/stack-slot.
///   - Call SignCreateOrder() with known-good test vectors against the
///     lighter testnet and confirm txHash matches what the Go SDK / Python
///     SDK produce for the identical input (cross-SDK vector check).
///
/// If ClangSharp's generated [StructLayout] for any of these three types
/// is missing or uses Auto instead of Sequential, this file overrides it
/// with an explicit partial/redeclaration below. As of the cgo header
/// inspected for this repo (lighter-go v1.0.6), ClangSharp generates
/// Sequential by default for plain C structs, so no override should be
/// necessary — this class exists primarily as the designated landing spot
/// for one if a future lighter-go release changes struct shape and the CI
/// check in the workflow (`Validate struct-by-value return signatures`)
/// fails.
/// </summary>
internal static class StructFixups
{
    // Intentionally empty unless/until a CI run proves ClangSharp's default
    // output is wrong for a specific platform. Do NOT add speculative
    // [StructLayout(Pack = ...)] overrides without a failing test that
    // demonstrates the need — guessing at padding is how subtle native
    // interop bugs get shipped.
}
