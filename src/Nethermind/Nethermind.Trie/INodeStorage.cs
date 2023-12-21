// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

public interface INodeStorage
{
    public KeyScheme Scheme { get; set; }
    public bool RequirePath { get; }

    byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None);
    void Set(Hash256? address, in TreePath path, in ValueHash256 hash, byte[] toArray, WriteFlags writeFlags = WriteFlags.None);
    WriteBatch StartWriteBatch();

    /// <summary>
    /// Used by StateSync
    /// </summary>
    bool KeyExists(Hash256? address, in TreePath path, in ValueHash256 hash);

    /// <summary>
    /// Used for serving with hash.
    /// </summary>
    byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags);

    /// <summary>
    /// Used by StateSync to make sure values are flushed.
    /// </summary>
    void Flush();

    public enum KeyScheme
    {
        Hash,
        HalfPath,
        Current,
    }

    public interface WriteBatch : IDisposable
    {
        void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, byte[] toArray, WriteFlags writeFlags);
    }
}