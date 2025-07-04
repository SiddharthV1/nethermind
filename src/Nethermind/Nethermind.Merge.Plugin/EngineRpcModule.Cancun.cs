// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV3Result?> _getPayloadHandlerV3;
    private readonly IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> _getBlobsHandler;

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Cancun);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot) =>
        NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot), EngineApiVersions.Cancun);

    public Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId) =>
        _getPayloadHandlerV3.HandleAsync(payloadId);

    public Task<ResultWrapper<IEnumerable<BlobAndProofV1?>>> engine_getBlobsV1(byte[][] blobVersionedHashes) =>
        _getBlobsHandler.HandleAsync(blobVersionedHashes);
}
