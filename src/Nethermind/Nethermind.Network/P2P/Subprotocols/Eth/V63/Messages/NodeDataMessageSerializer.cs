// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessageSerializer : IZeroInnerMessageSerializer<NodeDataMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, NodeDataMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Data.Count; i++)
            {
                rlpStream.Encode(message.Data[i]);
            }
        }

        public NodeDataMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            ArrayPoolList<byte[]> result = rlpStream.DecodeArrayPoolList(static stream => stream.DecodeByteArray());
            return new NodeDataMessage(result);
        }

        public int GetLength(NodeDataMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Data.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.Data[i]);
            }

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
