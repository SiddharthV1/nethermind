// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class HeaderDecoderTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void Can_decode(bool hasWithdrawalsRoot)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithMixHash(Keccak.Compute("mix_hash"))
            .WithNonce(1000)
            .WithWithdrawalsRoot(hasWithdrawalsRoot ? Keccak.EmptyTreeHash : null)
            .TestObject;

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);
        Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Can_decode_aura()
    {
        var auRaSignature = new byte[64];
        new Random().NextBytes(auRaSignature);
        BlockHeader header = Build.A.BlockHeader
            .WithAura(100000000, auRaSignature)
            .TestObject;

        HeaderDecoder decoder = new();
        Rlp rlp = decoder.Encode(header);
        Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
        BlockHeader? decoded = decoder.Decode(ref decoderContext);
        decoded!.Hash = decoded.CalculateHash();

        Assert.That(decoded.Hash, Is.EqualTo(header.Hash), "hash");
    }

    [Test]
    public void Get_length_null()
    {
        HeaderDecoder decoder = new();
        Assert.That(decoder.GetLength(null, RlpBehaviors.None), Is.EqualTo(1));
    }

    [Test]
    public void Can_handle_nulls()
    {
        Rlp rlp = Rlp.Encode((BlockHeader?)null);
        BlockHeader decoded = Rlp.Decode<BlockHeader>(rlp);
        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Can_encode_decode_with_base_fee()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
        blockHeader.BaseFeePerGas.Should().Be((UInt256)123);
    }

    [Test]
    public void If_baseFee_is_zero_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(0).WithNonce(0).WithDifficulty(0).TestObject;
        Rlp rlp = Rlp.Encode(header);
        Convert.ToHexString(rlp.Bytes).ToLower().Should().Be("f901f6a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2880000000000000000");
    }

    [Test]
    public void Can_encode_with_withdrawals()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0)
            .WithWithdrawalsRoot(Keccak.Compute("withdrawals")).TestObject;
        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
        blockHeader.WithdrawalsRoot.Should().Be(Keccak.Compute("withdrawals"));
    }

    [Test]
    public void If_withdrawals_are_null_should_not_encode()
    {
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(1).WithNonce(0).WithDifficulty(0).TestObject;
        Rlp rlp = Rlp.Encode(header);
        Convert.ToHexString(rlp.Bytes).ToLower().Should().Be("f901f7a0ff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09ca01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008080833d090080830f424083010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e288000000000000000001");
    }

    [TestCase(-1)]
    [TestCase(long.MinValue)]
    public void Can_encode_decode_with_negative_long_fields(long negativeLong)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(negativeLong).
            WithGasUsed(negativeLong).
            WithGasLimit(negativeLong).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);

        blockHeader.GasUsed.Should().Be(negativeLong);
        blockHeader.Number.Should().Be(negativeLong);
        blockHeader.GasLimit.Should().Be(negativeLong);
    }

    [TestCase(-1)]
    [TestCase(long.MinValue)]
    public void Can_encode_decode_with_negative_long_when_using_span(long negativeLong)
    {
        BlockHeader header = Build.A.BlockHeader.
            WithNumber(negativeLong).
            WithGasUsed(negativeLong).
            WithGasLimit(negativeLong).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.GasUsed.Should().Be(negativeLong);
        blockHeader.Number.Should().Be(negativeLong);
        blockHeader.GasLimit.Should().Be(negativeLong);
    }

    [TestCaseSource(nameof(CancunFieldsSource))]
    public void Can_encode_decode_with_cancun_fields(ulong? blobGasUsed, ulong? excessBlobGas, Hash256? parentBeaconBlockRoot)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(blobGasUsed)
            .WithExcessBlobGas(excessBlobGas)
            .WithParentBeaconBlockRoot(parentBeaconBlockRoot).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.BlobGasUsed.Should().Be(blobGasUsed);
        blockHeader.ExcessBlobGas.Should().Be(excessBlobGas);
    }

    [Test]
    public void Can_encode_decode_with_WithdrawalRequestRoot()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakB)
            .WithRequestsHash(Keccak.Zero).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.ParentBeaconBlockRoot.Should().Be(TestItem.KeccakB);
    }

    [Test]
    public void Can_encode_decode_with_ValidatorExitRoot_equals_to_null()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(1)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakB)
            .WithRequestsHash(Keccak.Zero).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.Should().BeEquivalentTo(header);
    }

    [Test]
    public void Can_encode_decode_with_missing_excess_blob_gass()
    {
        BlockHeader header = Build.A.BlockHeader
                .WithHash(new Hash256("0x3d8b9cc98eee58243461bd5a83663384b50293cd1e459a6841cb005296305590"))
                .WithNumber(1000)
                .WithParentHash(new Hash256("0x793b1ee71748f4b1b70cf70a53e083e6d5d356bffee9946e15a13fed8d70d7d6"))
                .WithBeneficiary(new Address("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099"))
                .WithGasLimit(100000000)
                .WithGasUsed(299331)
                .WithTimestamp(1736575828)
                .WithExtraData(Bytes.FromHexString("4e65746865726d696e64"))
                .WithDifficulty(1)
                .WithMixHash(new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000"))
                .WithNonce(0)
                .WithUnclesHash(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithTransactionsRoot(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithReceiptsRoot(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithStateRoot(new Hash256("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                .WithBaseFee(8)
                .WithWithdrawalsRoot(new Hash256("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"))
                .WithBlobGasUsed(0)
                .TestObject;
        ;

        Rlp rlp = Rlp.Encode(header);
        _ = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());
    }

    [Test]
    public void Can_encode_decode_with_zero_basefee_but_has_later_field()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithTimestamp(ulong.MaxValue)
            .WithBaseFee(0)
            .WithWithdrawalsRoot(Keccak.Zero)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakB)
            .WithRequestsHash(Keccak.Zero).TestObject;

        Rlp rlp = Rlp.Encode(header);
        BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp.Bytes.AsSpan());

        blockHeader.Should().BeEquivalentTo(header);
    }

    public static IEnumerable<object?[]> CancunFieldsSource()
    {
        yield return new object?[] { null, null, null };
        yield return new object?[] { 0ul, 0ul, TestItem.KeccakA };
        yield return new object?[] { 1ul, 2ul, TestItem.KeccakB };
        yield return new object?[] { ulong.MaxValue / 2, ulong.MaxValue, null };
        yield return new object?[] { ulong.MaxValue, ulong.MaxValue / 2, null };
    }
}
