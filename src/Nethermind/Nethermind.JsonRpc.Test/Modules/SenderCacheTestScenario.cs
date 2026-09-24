// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>
/// Shared scenario for the sender-cache regression tests of <c>proof_call</c>, <c>debug_traceCall</c>,
/// <c>debug_traceCallMany</c> and <c>trace_rawTransaction</c>: a call that carries a genuine transaction's content and
/// signature but executes other content, because the nonce is loaded from state or, for <c>trace_rawTransaction</c>,
/// the gas limit is lowered to the gas cap.
/// </summary>
internal static class SenderCacheTestScenario
{
    // The sender cache is process-wide: a value per transaction gives every test its own content, which nothing else
    // recovers before the call under test, so no test can mask another's cache write.
    private static long _counter = 100;

    /// <summary>
    /// A signed EIP-1559 transaction, the same transaction as it arrives from the wire (decoded, so its hash comes from
    /// the raw bytes), and its signer.
    /// </summary>
    /// <param name="nonce">The nonce; by default one that differs from the state nonce of every test account.</param>
    /// <param name="unfundedSigner">Signs with <see cref="TestItem.PrivateKeyF"/>, which has no account, instead of the
    /// funded <see cref="TestItem.PrivateKeyA"/>, with zero fees and value so the unfunded signer can run it.</param>
    public static (Transaction built, Transaction network, Address signer) BuildGenuineTypedTx(IEthereumEcdsa ecdsa, ulong chainId, ulong? nonce = null, bool unfundedSigner = false)
    {
        PrivateKey signer = unfundedSigner ? TestItem.PrivateKeyF : TestItem.PrivateKeyA;
        ulong unique = (ulong)Interlocked.Increment(ref _counter);
        Transaction built = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithChainId(chainId)
            .WithNonce(nonce ?? unique)
            .To(TestItem.AddressB)
            .WithMaxFeePerGas(unfundedSigner ? 0 : 100_000_000_000UL)
            .WithMaxPriorityFeePerGas(unfundedSigner ? 0 : 1_000_000_000UL)
            .WithGasLimit(100_000)
            .WithValue(unfundedSigner ? 0 : unique)
            .SignedAndResolved(ecdsa, signer)
            .TestObject;

        return (built, Decode(built), signer.Address);
    }

    /// <summary>A call request with the fields and signature of <paramref name="tx"/> and the given <paramref name="from"/>.</summary>
    public static EIP1559TransactionForRpc BuildRpcClone(Transaction tx, Address from, ulong chainId) => new()
    {
        Nonce = tx.Nonce,
        To = tx.To,
        Gas = tx.GasLimit,
        MaxFeePerGas = tx.MaxFeePerGas,
        MaxPriorityFeePerGas = tx.MaxPriorityFeePerGas,
        Value = tx.Value,
        ChainId = chainId,
        From = from,
        R = new UInt256(tx.Signature!.RAsSpan, isBigEndian: true),
        S = new UInt256(tx.Signature!.SAsSpan, isBigEndian: true),
        V = tx.Signature!.RecoveryId == 0 ? UInt256.Zero : UInt256.One,
    };

    public static void AssertRecoversTrueSigner(IEthereumEcdsa ecdsa, Transaction networkTx, Address trueSigner) =>
        Assert.That(ecdsa.RecoverAddress(networkTx), Is.EqualTo(trueSigner),
            "recovering the genuine transaction must return its signer, not a sender cached by the call");

    /// <summary>The sender the process-wide cache holds for <paramref name="tx"/>'s content and signature, or <see langword="null"/>.</summary>
    /// <remarks>Recovers through an ecdsa that recovers nothing, so only a cache hit yields an address and a miss writes
    /// nothing.</remarks>
    public static Address? CachedSender(IEthereumEcdsa ecdsa, Transaction tx)
    {
        IEthereumEcdsa nonRecovering = Substitute.For<IEthereumEcdsa>();
        nonRecovering.ChainId.Returns(ecdsa.ChainId);
        return nonRecovering.RecoverAddress(tx);
    }

    private static Transaction Decode(Transaction tx) =>
        Rlp.Decode<Transaction>(TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, RlpBehaviors.SkipTypedWrapping)!;
}
