// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PongMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<PongMsg>
{
    public PongMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, PongMsg msg)
    {
        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.", NetworkExceptionType.Discovery);
        }

        (int totalLength, int contentLength, int farAddressLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        Encode(stream, msg.FarAddress, farAddressLength);
        stream.Encode(msg.PingMdc);
        stream.Encode(msg.ExpirationTime);

        byteBuffer.ResetIndex();

        AddSignatureAndMdc(byteBuffer, totalLength + 1);
    }

    public PongMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, _, IByteBuffer Data) = PrepareForDeserialization(msgBytes);

        NettyRlpStream rlp = new(Data);

        rlp.ReadSequenceLength();
        rlp.ReadSequenceLength();

        // GetAddress(rlp.DecodeByteArray(), rlp.DecodeInt());
        rlp.DecodeByteArraySpan();
        rlp.DecodeInt(); // UDP port (we ignore and take it from Netty)
        rlp.DecodeInt(); // TCP port
        byte[] token = rlp.DecodeByteArray();
        long expirationTime = rlp.DecodeLong();

        PongMsg msg = new(FarPublicKey, expirationTime, token);
        return msg;
    }

    public int GetLength(PongMsg message, out int contentLength)
    {
        (int totalLength, contentLength, int _) = GetLength(message);
        return totalLength;
    }

    private static (int totalLength, int contentLength, int farAddressLength) GetLength(PongMsg message)
    {
        if (message.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(message.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }

        int farAddressLength = GetIPEndPointLength(message.FarAddress);
        int contentLength = Rlp.LengthOfSequence(farAddressLength);
        contentLength += Rlp.LengthOf(message.PingMdc);
        contentLength += Rlp.LengthOf(message.ExpirationTime);

        return (Rlp.LengthOfSequence(contentLength), contentLength, farAddressLength);
    }
}
