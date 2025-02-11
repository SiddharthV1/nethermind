// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Synchronization.FastSync;

public interface IBlockingVerifyTrie
{
    bool TryStartVerifyTrie(BlockHeader stateAtBlock);
}
