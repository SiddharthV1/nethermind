// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IEip1559Spec
    {
        /// <summary>
        /// Gas target and base fee, and fee burning.
        /// </summary>
        bool IsEip1559Enabled { get; }
        public long Eip1559TransitionBlock { get; }
        // Collects for both EIP-1559 and EIP-4844-Pectra
        public Address? FeeCollector => null;
        public UInt256? Eip1559BaseFeeMinValue => null;
        public UInt256 ForkBaseFee { get; }
        public UInt256 BaseFeeMaxChangeDenominator { get; }
        public long ElasticityMultiplier { get; }
    }
}
