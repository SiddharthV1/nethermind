// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public interface IChainHeadInfoProvider
    {
        IChainHeadSpecProvider SpecProvider { get; }

        IReadOnlyStateProvider ReadOnlyStateProvider { get; }

        ICodeInfoRepository CodeInfoRepository { get; }

        long HeadNumber { get; }

        long? BlockGasLimit { get; }

        UInt256 CurrentBaseFee { get; }

        public UInt256 CurrentFeePerBlobGas { get; }

        ProofVersion CurrentProofVersion { get; }

        bool IsSyncing { get; }
        bool IsProcessingBlock { get; }

        event EventHandler<BlockReplacementEventArgs> HeadChanged;
    }
}
