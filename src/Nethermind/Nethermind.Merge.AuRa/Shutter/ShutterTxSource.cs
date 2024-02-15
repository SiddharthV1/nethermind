// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Abi;
using Nethermind.Crypto;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxSource : ITxSource
{
    public DecryptionKeys decryptionKeys = new();
    private ILogFinder? _logFinder;
    private LogFilter? _logFilter;
    private static readonly UInt256 EncryptedGasLimit = 300;
    internal static readonly AbiSignature TransactionSubmmitedSig = new AbiSignature(
        "TransactionSubmitted",
        [
            AbiType.UInt64, // eon
            AbiType.Bytes32, // identity prefix
            AbiType.Address, // sender
            AbiType.DynamicBytes, // encrypted transaction
            AbiType.UInt256 // gas limit
        ]
    );

    public ShutterTxSource(string sequencerAddress, ILogFinder logFinder, IFilterStore filterStore)
        : base()
    {
        IEnumerable<object> topics = new List<object>() { TransactionSubmmitedSig.Hash };
        _logFinder = logFinder;
        _logFilter = filterStore.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest, sequencerAddress, topics);
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        // todo: cache? check changes in header?

        IEnumerable<SequencedTransaction> sequencedTransactions = GetNextTransactions(decryptionKeys.Eon, (int)decryptionKeys.TxPointer);
        return sequencedTransactions.Zip(decryptionKeys.Keys).Select((x) => DecryptSequencedTransaction(x.Item1, x.Item2));
    }

    // todo: how / where will libp2p set this?
    public struct DecryptionKeys
    {
        public ulong InstanceId;
        public ulong Eon;
        public ulong Slot;
        public ulong TxPointer;
        public IEnumerable<(byte[], byte[])> Keys; // (identity, key)
        public IEnumerable<ulong> SignerIndices;
        public IEnumerable<byte> Signatures;
    }

    internal IEnumerable<TransactionSubmittedEvent> GetEvents()
    {
        IEnumerable<IFilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        return logs.Select(log => new TransactionSubmittedEvent(AbiEncoder.Instance.Decode(AbiEncodingStyle.None, TransactionSubmmitedSig, log.Data)));
    }

    internal Transaction DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, (byte[], byte[]) decryptionKey)
    {
        ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);
        (byte[] identity, byte[] key) = decryptionKey;

        if (!new G1(identity).is_equal(sequencedTransaction.Identity))
        {
            throw new Exception("Transaction identity did not match decryption key.");
        }

        byte[] transaction = ShutterCrypto.Decrypt(encryptedMessage, new G1(key));
        return Rlp.Decode<Transaction>(new Rlp(transaction), RlpBehaviors.AllowUnsigned);
    }

    internal IEnumerable<SequencedTransaction> GetNextTransactions(ulong eon, int txPointer)
    {
        IEnumerable<TransactionSubmittedEvent> events = GetEvents();
        events = events.Where(e => e.Eon == eon).Skip(txPointer);

        List<SequencedTransaction> txs = new List<SequencedTransaction>();
        UInt256 totalGas = 0;

        foreach (TransactionSubmittedEvent e in events)
        {
            if (totalGas + e.GasLimit > EncryptedGasLimit)
            {
                break;
            }

            SequencedTransaction sequencedTransaction = new()
            {
                Eon = eon,
                EncryptedTransaction = e.EncryptedTransaction,
                GasLimit = e.GasLimit,
                Identity = ShutterCrypto.ComputeIdentity(e.IdentityPrefix, e.Sender)
            };
            txs.Add(sequencedTransaction);

            totalGas += e.GasLimit;
        }

        return txs;
    }

    internal struct SequencedTransaction
    {
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public G1 Identity;
    }

    internal class TransactionSubmittedEvent
    {
        public ulong Eon;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;

        public TransactionSubmittedEvent(object[] decodedEvent)
        {
            Eon = (ulong)decodedEvent[0];
            IdentityPrefix = new Bytes32((byte[])decodedEvent[1]);
            Sender = (Address)decodedEvent[2];
            EncryptedTransaction = (byte[])decodedEvent[3];
            GasLimit = (UInt256)decodedEvent[4];
        }
    }
}