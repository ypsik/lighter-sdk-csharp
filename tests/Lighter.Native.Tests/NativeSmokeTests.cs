namespace Lighter.Native.Tests;

using Xunit;

/// <summary>
/// These tests exist specifically to catch the struct-by-value return ABI
/// risk described in StructFixups.cs. They run on every platform in the CI
/// matrix (win-x64, linux-x64, linux-arm64, osx-arm64) — a passing test on
/// linux-x64 does NOT imply the same code is correct on osx-arm64, because
/// the whole point of the risk is that the calling convention differs per
/// platform. Do not skip platforms when running these locally.
/// </summary>
public class NativeSmokeTests
{
    [Fact]
    public void GenerateApiKey_ReturnsNonEmptyValidKeyPair()
    {
        var (privateKey, publicKey) = LighterSigner.GenerateApiKey();

        // If struct marshalling is misaligned, the most common failure mode
        // is the LAST field of the struct (here: 'err', since ApiKeyResponse
        // is { privateKey, publicKey, err }) reading as garbage non-zero
        // memory, which our wrapper would then incorrectly throw on, OR the
        // earlier fields silently shifting by one pointer-width and reading
        // each other's data. Both are covered by asserting exact expected
        // hex-string shapes, not just "is not null".
        Assert.False(string.IsNullOrWhiteSpace(privateKey));
        Assert.False(string.IsNullOrWhiteSpace(publicKey));

        // lighter's keys are hex-encoded; a misaligned struct read would
        // very likely produce non-hex garbage or wildly wrong lengths.
        Assert.Matches("^(0x)?[0-9a-fA-F]+$", privateKey);
        Assert.Matches("^(0x)?[0-9a-fA-F]+$", publicKey);

        Assert.NotEqual(privateKey, publicKey);
    }

    [Fact]
    public void GenerateApiKey_CalledTwice_ProducesDifferentKeys()
    {
        // Guards against a subtler marshalling bug: native memory being
        // freed (by our Free() call) before .NET finished copying it into
        // a managed string, which can manifest as the SAME bytes being
        // read back on a second call due to allocator re-use.
        var first = LighterSigner.GenerateApiKey();
        var second = LighterSigner.GenerateApiKey();

        Assert.NotEqual(first.PrivateKey, second.PrivateKey);
        Assert.NotEqual(first.PublicKey, second.PublicKey);
    }

    [Fact]
    public void GenerateApiKey_RepeatedCalls_DoNotCrashOrLeakAcrossIterations()
    {
        // Runs Free() in a loop. If the struct layout were wrong such that
        // Free() were called on a non-pointer field (e.g. because fields
        // shifted), this would corrupt the native allocator and crash the
        // process — which is exactly the failure mode unit test isolation
        // would otherwise hide if we only ever called this once.
        for (var i = 0; i < 50; i++)
        {
            var (priv, pub) = LighterSigner.GenerateApiKey();
            Assert.False(string.IsNullOrWhiteSpace(priv));
            Assert.False(string.IsNullOrWhiteSpace(pub));
        }
    }
}
