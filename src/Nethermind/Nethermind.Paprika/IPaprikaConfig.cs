// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Paprika;

[ConfigCategory(HiddenFromDocs = true)]
public interface IPaprikaConfig : IConfig
{
    [ConfigItem(Description = "The depths of reads that should be cached for State data. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = "8")]
    public ushort CacheStateBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block for state", DefaultValue = "5000")]
    public int CacheStatePerBlock { get; set; }

    [ConfigItem(Description = "The depths of reads that should be cached for Merkle data. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = "4")]
    public ushort CacheMerkleBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block", DefaultValue = "10000")]
    public int CacheMerklePerBlock { get; set; }

    [ConfigItem(Description = "Whether Merkle should use parallelism", DefaultValue = "true")]
    public bool ParallelMerkle { get; set; }
}

/// <summary>
/// One slot takes ~100 bytes of managed + unmanaged memory.
/// This multiplied by 10,000 entries, gives 1MGB per block of total budget to cache things.
/// </summary>
public class PaprikaConfig : IPaprikaConfig
{
    public ushort CacheStateBeyond { get; set; } = 8;
    public int CacheStatePerBlock { get; set; } = 5000;

    public ushort CacheMerkleBeyond { get; set; } = 8;
    public int CacheMerklePerBlock { get; set; } = 5000;

    public bool ParallelMerkle { get; set; } = true;
}