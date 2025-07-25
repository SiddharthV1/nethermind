// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public static class AddressExtensions
{
    public static bool IsPrecompile(this Address address, IReleaseSpec releaseSpec)
    {
        Span<uint> data = MemoryMarshal.Cast<byte, uint>(address.Bytes.AsSpan());
        return (data[4] & 0x0000ffff) == 0
            && data[3] == 0 && data[2] == 0 && data[1] == 0 && data[0] == 0
            && ((data[4] >>> 16) & 0xff) switch
            {
                0x00 => (data[4] >>> 24) switch
                {
                    0x01 => true,
                    0x02 => true,
                    0x03 => true,
                    0x04 => true,
                    0x05 => releaseSpec.ModExpEnabled,
                    0x06 => releaseSpec.BN254Enabled,
                    0x07 => releaseSpec.BN254Enabled,
                    0x08 => releaseSpec.BN254Enabled,
                    0x09 => releaseSpec.BlakeEnabled,
                    0x0a => releaseSpec.IsEip4844Enabled,
                    0x0b => releaseSpec.Bls381Enabled,
                    0x0c => releaseSpec.Bls381Enabled,
                    0x0d => releaseSpec.Bls381Enabled,
                    0x0e => releaseSpec.Bls381Enabled,
                    0x0f => releaseSpec.Bls381Enabled,
                    0x10 => releaseSpec.Bls381Enabled,
                    0x11 => releaseSpec.Bls381Enabled,
                    _ => false
                },
                0x01 => (data[4] >>> 24) switch
                {
                    0x00 => releaseSpec.IsEip7951Enabled || releaseSpec.IsRip7212Enabled,
                    _ => false
                },
                _ => false
            };
    }
}
