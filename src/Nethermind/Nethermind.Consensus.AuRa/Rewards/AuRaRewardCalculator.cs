// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Rewards
{
    public class AuRaRewardCalculator : IRewardCalculator
    {
        private readonly StaticRewardCalculator _blockRewardCalculator;
        private readonly IList<IRewardContract> _contracts;

        public AuRaRewardCalculator(AuRaChainSpecEngineParameters auRaParameters, IAbiEncoder abiEncoder, ITransactionProcessor transactionProcessor)
        {
            ArgumentNullException.ThrowIfNull(auRaParameters);
            ArgumentNullException.ThrowIfNull(abiEncoder);
            ArgumentNullException.ThrowIfNull(transactionProcessor);

            IList<IRewardContract> BuildTransitions()
            {
                var contracts = new List<IRewardContract>();

                if (auRaParameters.BlockRewardContractTransitions is not null)
                {
                    contracts.AddRange(auRaParameters.BlockRewardContractTransitions.Select(t => new RewardContract(transactionProcessor, abiEncoder, t.Value, t.Key)));
                    contracts.Sort((a, b) => a.Activation.CompareTo(b.Activation));
                }

                if (auRaParameters.BlockRewardContractAddress is not null)
                {
                    var contractTransition = auRaParameters.BlockRewardContractTransition ?? 0;
                    if (contractTransition > (contracts.FirstOrDefault()?.Activation ?? long.MaxValue))
                    {
                        throw new ArgumentException($"{nameof(auRaParameters.BlockRewardContractTransition)} provided for {nameof(auRaParameters.BlockRewardContractAddress)} is higher than first {nameof(auRaParameters.BlockRewardContractTransitions)}.");
                    }

                    contracts.Insert(0, new RewardContract(transactionProcessor, abiEncoder, auRaParameters.BlockRewardContractAddress, contractTransition));
                }

                return contracts;
            }

            ArgumentNullException.ThrowIfNull(auRaParameters);
            _contracts = BuildTransitions();
            _blockRewardCalculator = new StaticRewardCalculator(auRaParameters.BlockReward);
        }

        public BlockReward[] CalculateRewards(Block block)
        {
            if (block.IsGenesis)
            {
                return [];
            }

            return _contracts.TryGetForBlock(block.Number, out var contract)
                ? CalculateRewardsWithContract(block, contract)
                : _blockRewardCalculator.CalculateRewards(block);
        }


        private static BlockReward[] CalculateRewardsWithContract(Block block, IRewardContract contract)
        {
            (Address[] beneficieries, ushort[] kinds) GetBeneficiaries()
            {
                var length = block.Uncles.Length + 1;

                Address[] beneficiariesList = new Address[length];
                ushort[] kindsList = new ushort[length];
                beneficiariesList[0] = block.Beneficiary;
                kindsList[0] = BenefactorKind.Author;

                for (int i = 0; i < block.Uncles.Length; i++)
                {
                    var uncle = block.Uncles[i];
                    if (BenefactorKind.TryGetUncle(block.Number - uncle.Number, out var kind))
                    {
                        beneficiariesList[i + 1] = uncle.Beneficiary;
                        kindsList[i + 1] = kind;
                    }
                }

                return (beneficiariesList, kindsList);
            }

            var (beneficiaries, kinds) = GetBeneficiaries();
            var (addresses, rewards) = contract.Reward(block.Header, beneficiaries, kinds);

            var blockRewards = new BlockReward[addresses.Length];
            for (int index = 0; index < addresses.Length; index++)
            {
                var address = addresses[index];
                blockRewards[index] = new BlockReward(address, rewards[index], BlockRewardType.External);
            }

            return blockRewards;
        }


        public class AuRaRewardCalculatorSource : IRewardCalculatorSource
        {
            private readonly AuRaChainSpecEngineParameters _auRaParameters;
            private readonly IAbiEncoder _abiEncoder;

            public AuRaRewardCalculatorSource(AuRaChainSpecEngineParameters auRaParameters, IAbiEncoder abiEncoder)
            {
                _auRaParameters = auRaParameters;
                _abiEncoder = abiEncoder;
            }

            public IRewardCalculator Get(ITransactionProcessor processor) => new AuRaRewardCalculator(_auRaParameters, _abiEncoder, processor);
        }

        public static class BenefactorKind
        {
            public const ushort Author = 0;
            public const ushort EmptyStep = 2;
            public const ushort External = 3;
            private const ushort uncleOffset = 100;
            private const ushort minDistance = 1;
            private const ushort maxDistance = 6;

            public static bool TryGetUncle(long distance, out ushort kind)
            {
                if (IsValidDistance(distance))
                {
                    kind = (ushort)(uncleOffset + distance);
                    return true;
                }

                kind = 0;
                return false;
            }

            public static BlockRewardType ToBlockRewardType(ushort kind)
            {
                return kind switch
                {
                    Author => BlockRewardType.Block,
                    External => BlockRewardType.External,
                    EmptyStep => BlockRewardType.EmptyStep,
                    ushort uncle when IsValidDistance(uncle - uncleOffset) => BlockRewardType.Uncle,
                    _ => throw new ArgumentException($"Invalid BlockRewardType for kind {kind}", nameof(kind)),
                };
            }

            private static bool IsValidDistance(long distance)
            {
                return distance >= minDistance && distance <= maxDistance;
            }
        }
    }
}
