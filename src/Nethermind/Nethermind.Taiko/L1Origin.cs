// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Taiko;

public class L1Origin(UInt256 blockId, ValueHash256? l2BlockHash, long l1BlockHeight, Hash256 l1BlockHash, int[]? buildPayloadArgsId)
{
    public UInt256 BlockId { get; set; } = blockId;
    public ValueHash256? L2BlockHash { get; set; } = l2BlockHash;
    public long L1BlockHeight { get; set; } = l1BlockHeight;
    public Hash256 L1BlockHash { get; set; } = l1BlockHash;

    // Taiko uses int-like serializer
    public int[]? BuildPayloadArgsId { get; set; } = buildPayloadArgsId;

    /// <summary>
    /// IsPreconfBlock returns true if the L1Origin is for a preconfirmation block.
    /// </summary>    
    public bool IsPreconfBlock => L1BlockHeight == 0;
}
