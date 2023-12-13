// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks;

public abstract class BarrierSyncFeed<T> : ActivatedSyncFeed<T>
{
    internal const int DepositContractBarrier = 11052984;
    protected abstract long? LowestInsertedNumber { get; }
    protected abstract int BarrierWhenStartedMetadataDbKey { get; }
    protected abstract long SyncConfigBarrierCalc { get; }
    protected abstract Func<bool> HasPivot { get; }

    protected readonly ISpecProvider _specProvider;
    protected readonly ILogger _logger;
    protected long _barrier;
    protected long _pivotNumber;
    protected long? _barrierWhenStarted;

    protected readonly IDb _metadataDb;

    // This property was introduced when we switched defaults of barriers on mainnet from 11052984 to 0 to not disturb existing node operators
    protected bool WithinOldBarrierDefault => _specProvider.ChainId == BlockchainIds.Mainnet
        && _barrierWhenStarted == DepositContractBarrier
        && LowestInsertedNumber <= DepositContractBarrier
        && LowestInsertedNumber > DepositContractBarrier - GethSyncLimits.MaxBodyFetch; // this is intentional. using this as an approxamation assuming a minimum of 1 receipt in per block in case of receipts

    public BarrierSyncFeed(IDb metadataDb, ISpecProvider specProvider, ILogger logger)
    {
        _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void InitializeMetadataDb()
    {
        if (!HasPivot())
        {
            _barrierWhenStarted = SyncConfigBarrierCalc;
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
        else if (_metadataDb.KeyExists(BarrierWhenStartedMetadataDbKey))
        {
            _barrierWhenStarted = _metadataDb.Get(BarrierWhenStartedMetadataDbKey).ToLongFromBigEndianByteArrayWithoutLeadingZeros();
        }
        else if (_specProvider.ChainId == BlockchainIds.Mainnet)
        {
            // Assume the  barrier was the previous defualt (deposit contract barrier) only for mainnet
            _barrierWhenStarted = DepositContractBarrier;
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
        else
        {
            _barrierWhenStarted = _barrier;
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
    }
}