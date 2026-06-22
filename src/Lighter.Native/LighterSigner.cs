namespace Lighter.Native;

using System;
using System.Runtime.InteropServices;
using Lighter.Native.Generated;

/// <summary>
/// Managed wrapper around the lighter_signer native library.
///
/// Every native call that returns a struct with char* fields hands ownership
/// of that native memory to us. The header's exported `Free(void* ptr)`
/// function must be called on every non-null char* we receive, or we leak
/// native heap memory on every signed transaction. This wrapper centralizes
/// that so call sites can't forget it.
///
/// TYPE NOTE (verified against actual ClangSharp multi-file output, not
/// assumed): the generator emits `char *` parameters/fields as `sbyte*`,
/// not `IntPtr` — this file is `unsafe` throughout to work with that
/// directly rather than fighting it with casts. Struct fields that map to
/// `int64_t` (e.g. CreateOrderTxReq.ClientOrderIndex) come out as `nint`,
/// not `long`; method *parameters* of the same native type come out as
/// `long` instead — an inconsistency in ClangSharp's codegen between
/// struct-field and parameter contexts, not a mistake here. Conversions
/// between `long` and `nint` are implicit on 64-bit but the OrderLeg
/// public API stays on `long` for an ordinary, framework-agnostic surface.
/// `nint == int64_t` only holds on 64-bit targets, which is fine here since
/// every supported RID (win-x64, linux-x64, linux-arm64, osx-arm64) is
/// 64-bit — this would break on a hypothetical 32-bit RID, which is not a
/// target and never has been.
///
/// THREE THINGS NOT YET VERIFIED, FLAGGED HERE RATHER THAN GUESSED AT:
///
/// 1. Every Free()'d char* is assumed to be heap-allocated by the native
///    side specifically for the caller to free. If any native function
///    ever returns a static/string-literal pointer instead (e.g. a
///    hardcoded "ok" on some path), calling Free() on it would crash. This
///    has not caused a problem in the one function actually exercised in
///    CI (GenerateApiKey) — but that doesn't confirm it for the other 18.
/// 2. There is no synchronization here for concurrent calls against the
///    same (apiKeyIndex, accountIndex) client context created by
///    CreateClient. If lighter_signer keeps internal mutable state keyed
///    by those indices (e.g. a nonce counter), concurrent signing calls
///    from multiple threads could race. Nothing in this wrapper enforces
///    single-threaded use — callers building a multi-threaded order
///    pipeline need to serialize access themselves until this is
///    confirmed one way or the other against the native source.
/// 3. `fixed (CreateOrderTxReq* ptr = native) { ... }` in
///    SignCreateGroupedOrders assumes the native call only reads through
///    the pointer synchronously and never retains it past the call
///    returning. This is the standard signer-library assumption (the call
///    hashes/signs and returns) and not something the cgo header
///    contradicts, but it hasn't been independently confirmed against the
///    Go source's actual implementation of SignCreateGroupedOrders.
/// </summary>
public sealed unsafe class LighterSigner
{
    /// <summary>
    /// Generates a new API key pair. Caller is responsible for persisting
    /// the private key securely — it is never logged here.
    /// </summary>
    public static (string PrivateKey, string PublicKey) GenerateApiKey()
    {
        var result = NativeMethods.GenerateAPIKey();
        try
        {
            ThrowIfError(result.err);
            var priv = PtrToStringAndValidate(result.privateKey, nameof(result.privateKey));
            var pub = PtrToStringAndValidate(result.publicKey, nameof(result.publicKey));
            return (priv, pub);
        }
        finally
        {
            FreeIfNotNull(result.privateKey);
            FreeIfNotNull(result.publicKey);
            FreeIfNotNull(result.err);
        }
    }

    /// <summary>
    /// Creates a client context for the given API key index / account index.
    /// Must be called once before any Sign* calls for that (apiKeyIndex,
    /// accountIndex) pair. Returns null on success, or the native error
    /// string on failure.
    /// </summary>
    public static string? CreateClient(string url, string privateKeyHex, int chainId, int apiKeyIndex, long accountIndex)
    {
        var urlPtr = (sbyte*)Marshal.StringToHGlobalAnsi(url);
        var keyPtr = (sbyte*)Marshal.StringToHGlobalAnsi(privateKeyHex);
        try
        {
            var errPtr = NativeMethods.CreateClient(urlPtr, keyPtr, chainId, apiKeyIndex, accountIndex);
            if (errPtr == null) return null;

            var err = PtrToString(errPtr);
            Free(errPtr);
            return err;
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)urlPtr);
            Marshal.FreeHGlobal((IntPtr)keyPtr);
        }
    }

    /// <summary>
    /// Verifies that a client context for (apiKeyIndex, accountIndex) was
    /// created successfully and matches the API key registered server-side.
    /// Returns null on success, or the native error string on failure.
    /// </summary>
    public static string? CheckClient(int apiKeyIndex, long accountIndex)
    {
        var errPtr = NativeMethods.CheckClient(apiKeyIndex, accountIndex);
        if (errPtr == null) return null;
        var err = PtrToString(errPtr);
        Free(errPtr);
        return err;
    }

    public readonly record struct SignedTx(byte TxType, string TxInfo, string TxHash, string MessageToSign);

    public static SignedTx SignChangePubKey(
        string pubKeyHex, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var pubKeyPtr = (sbyte*)Marshal.StringToHGlobalAnsi(pubKeyHex);
        try
        {
            var result = NativeMethods.SignChangePubKey(pubKeyPtr, skipNonce, nonce, apiKeyIndex, accountIndex);
            return UnwrapSignedTx(result);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)pubKeyPtr);
        }
    }

    public static SignedTx SignCreateOrder(
        short marketIndex, long clientOrderIndex, long baseAmount, int price,
        bool isAsk, int orderType, int timeInForce, bool reduceOnly, int triggerPrice,
        long orderExpiry, int apiKeyIndex, long accountIndex,
        long integratorAccountIndex = 0, int integratorTakerFee = 0, int integratorMakerFee = 0,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignCreateOrder(
            marketIndex, clientOrderIndex, baseAmount, price,
            isAsk ? 1 : 0, orderType, timeInForce, reduceOnly ? 1 : 0, triggerPrice,
            orderExpiry, integratorAccountIndex, integratorTakerFee, integratorMakerFee,
            skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    /// <summary>
    /// Represents one order leg for SignCreateGroupedOrders. Mirrors the
    /// native CreateOrderTxReq struct field-for-field — verified directly
    /// against the generated CreateOrderTxReq.cs (field order: MarketIndex,
    /// ClientOrderIndex, BaseAmount, Price, IsAsk, Type, TimeInForce,
    /// ReduceOnly, TriggerPrice, OrderExpiry). Keep in sync if lighter-go
    /// ever changes its shape — the CI's header-diff check will catch a
    /// shape change, but won't catch a silent reordering that still
    /// compiles.
    /// </summary>
    public readonly record struct OrderLeg(
        short MarketIndex, long ClientOrderIndex, long BaseAmount, uint Price,
        bool IsAsk, byte OrderType, byte TimeInForce, bool ReduceOnly,
        uint TriggerPrice, long OrderExpiry);

    public static SignedTx SignCreateGroupedOrders(
        byte groupingType, OrderLeg[] orders, int apiKeyIndex, long accountIndex,
        long integratorAccountIndex = 0, int integratorTakerFee = 0, int integratorMakerFee = 0,
        byte skipNonce = 0, long nonce = -1)
    {
        var native = new CreateOrderTxReq[orders.Length];
        for (var i = 0; i < orders.Length; i++)
        {
            native[i] = new CreateOrderTxReq
            {
                MarketIndex = orders[i].MarketIndex,
                ClientOrderIndex = (nint)orders[i].ClientOrderIndex,
                BaseAmount = (nint)orders[i].BaseAmount,
                Price = orders[i].Price,
                IsAsk = (byte)(orders[i].IsAsk ? 1 : 0),
                Type = orders[i].OrderType,
                TimeInForce = orders[i].TimeInForce,
                ReduceOnly = (byte)(orders[i].ReduceOnly ? 1 : 0),
                TriggerPrice = orders[i].TriggerPrice,
                OrderExpiry = (nint)orders[i].OrderExpiry,
            };
        }

        fixed (CreateOrderTxReq* ptr = native)
        {
            var result = NativeMethods.SignCreateGroupedOrders(
                groupingType, ptr, native.Length,
                integratorAccountIndex, integratorTakerFee, integratorMakerFee,
                skipNonce, nonce, apiKeyIndex, accountIndex);
            return UnwrapSignedTx(result);
        }
    }

    public static SignedTx SignCancelOrder(
        short marketIndex, long orderIndex, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignCancelOrder(marketIndex, orderIndex, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignCancelAllOrders(
        int timeInForce, long time, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignCancelAllOrders(timeInForce, time, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignModifyOrder(
        short marketIndex, long index, long baseAmount, long price, long triggerPrice,
        int apiKeyIndex, long accountIndex,
        long integratorAccountIndex = 0, int integratorTakerFee = 0, int integratorMakerFee = 0,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignModifyOrder(
            marketIndex, index, baseAmount, price, triggerPrice,
            integratorAccountIndex, integratorTakerFee, integratorMakerFee,
            skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    /// <summary>
    /// memo must be exactly 32 raw bytes, 64 hex chars, or 66 chars with a
    /// "0x" prefix — matching the native side's validation in SignTransfer.
    /// Anything else throws native-side as a LighterNativeException.
    /// </summary>
    public static SignedTx SignTransfer(
        long toAccountIndex, short assetIndex, byte fromRouteType, byte toRouteType,
        long amount, long usdcFee, string memo, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var memoPtr = (sbyte*)Marshal.StringToHGlobalAnsi(memo);
        try
        {
            var result = NativeMethods.SignTransfer(
                toAccountIndex, assetIndex, fromRouteType, toRouteType,
                amount, usdcFee, memoPtr, skipNonce, nonce, apiKeyIndex, accountIndex);
            return UnwrapSignedTx(result);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)memoPtr);
        }
    }

    public static SignedTx SignWithdraw(
        short assetIndex, byte routeType, ulong amount, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignWithdraw(assetIndex, routeType, amount, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignCreateSubAccount(
        int apiKeyIndex, long accountIndex, byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignCreateSubAccount(skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignCreatePublicPool(
        long operatorFee, int initialTotalShares, long minOperatorShareRate,
        int apiKeyIndex, long accountIndex, byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignCreatePublicPool(
            operatorFee, initialTotalShares, minOperatorShareRate, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignUpdatePublicPool(
        long publicPoolIndex, int status, long operatorFee, int minOperatorShareRate,
        int apiKeyIndex, long accountIndex, byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignUpdatePublicPool(
            publicPoolIndex, status, operatorFee, minOperatorShareRate, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignMintShares(
        long publicPoolIndex, long shareAmount, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignMintShares(publicPoolIndex, shareAmount, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignBurnShares(
        long publicPoolIndex, long shareAmount, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignBurnShares(publicPoolIndex, shareAmount, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignUpdateLeverage(
        short marketIndex, int initialMarginFraction, int marginMode,
        int apiKeyIndex, long accountIndex, byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignUpdateLeverage(
            marketIndex, initialMarginFraction, marginMode, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    /// <summary>
    /// Creates a time-limited auth token (used e.g. for WS auth). deadline=0
    /// lets the native side default to now+7h, matching CreateAuthToken's
    /// own fallback.
    /// </summary>
    public static string CreateAuthToken(long deadline, int apiKeyIndex, long accountIndex)
    {
        var result = NativeMethods.CreateAuthToken(deadline, apiKeyIndex, accountIndex);
        try
        {
            ThrowIfError(result.err);
            return PtrToStringAndValidate(result.str, nameof(result.str));
        }
        finally
        {
            FreeIfNotNull(result.str);
            FreeIfNotNull(result.err);
        }
    }

    public static SignedTx SignUpdateMargin(
        short marketIndex, long usdcAmount, int direction, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignUpdateMargin(
            marketIndex, usdcAmount, direction, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignStakeAssets(
        long stakingPoolIndex, long shareAmount, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignStakeAssets(stakingPoolIndex, shareAmount, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignUnstakeAssets(
        long stakingPoolIndex, long shareAmount, int apiKeyIndex, long accountIndex,
        byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignUnstakeAssets(stakingPoolIndex, shareAmount, skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    public static SignedTx SignApproveIntegrator(
        long integratorAccountIndex, uint maxPerpsTakerFee, uint maxPerpsMakerFee,
        uint maxSpotTakerFee, uint maxSpotMakerFee, long approvalExpiry,
        int apiKeyIndex, long accountIndex, byte skipNonce = 0, long nonce = -1)
    {
        var result = NativeMethods.SignApproveIntegrator(
            integratorAccountIndex, maxPerpsTakerFee, maxPerpsMakerFee,
            maxSpotTakerFee, maxSpotMakerFee, approvalExpiry,
            skipNonce, nonce, apiKeyIndex, accountIndex);
        return UnwrapSignedTx(result);
    }

    // --- internal helpers -------------------------------------------------

    private static SignedTx UnwrapSignedTx(SignedTxResponse result)
    {
        try
        {
            ThrowIfError(result.err);
            return new SignedTx(
                result.txType,
                PtrToStringAndValidate(result.txInfo, nameof(result.txInfo)),
                PtrToStringAndValidate(result.txHash, nameof(result.txHash)),
                PtrToStringAndValidate(result.messageToSign, nameof(result.messageToSign)));
        }
        finally
        {
            FreeIfNotNull(result.txInfo);
            FreeIfNotNull(result.txHash);
            FreeIfNotNull(result.messageToSign);
            FreeIfNotNull(result.err);
        }
    }

    /// <summary>
    /// Throws if errPtr is non-null. Ownership note: this method does NOT
    /// free errPtr itself — every current call site wraps this in a
    /// try/finally that calls FreeIfNotNull(result.err) regardless of
    /// whether ThrowIfError throws (try/finally guarantees the finally
    /// block runs even when an exception propagates out of try). Adding a
    /// Free() call here too would double-free that pointer. If you add a
    /// new call site for this method, make sure it follows the same
    /// try { ThrowIfError(result.err); ... } finally { FreeIfNotNull(result.err); }
    /// pattern as GenerateApiKey/UnwrapSignedTx/CreateAuthToken below —
    /// calling it outside that pattern will leak errPtr.
    /// </summary>
    private static void ThrowIfError(sbyte* errPtr)
    {
        if (errPtr == null) return;
        var msg = PtrToString(errPtr);
        throw new LighterNativeException(msg ?? "unknown native error (null message with non-null err pointer)");
    }

    private static string PtrToStringAndValidate(sbyte* ptr, string fieldName)
    {
        if (ptr == null)
        {
            throw new LighterNativeException(
                $"Native call returned a null pointer for '{fieldName}' with no error set. " +
                "This indicates a struct-marshalling mismatch (see StructFixups.cs) rather than " +
                "an application-level failure — do not retry, file an issue with platform + RID.");
        }
        return PtrToString(ptr) ?? string.Empty;
    }

    private static string? PtrToString(sbyte* ptr) =>
        ptr == null ? null : Marshal.PtrToStringAnsi((IntPtr)ptr);

    private static void FreeIfNotNull(sbyte* ptr)
    {
        if (ptr != null) Free(ptr);
    }

    private static void Free(sbyte* ptr) => NativeMethods.Free(ptr);
}

public sealed class LighterNativeException : Exception
{
    public LighterNativeException(string message) : base(message) { }
}