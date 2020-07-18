//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class SimpleReceiptsSyncFeed : ActivatedSyncFeed<SimpleReceiptsSyncBatch>
    {
        private int _requestSize = GethSyncLimits.MaxReceiptFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncPeerPool _syncPeerPool;

        private FastStatusList _fastStatusList;
        private readonly long _pivotNumber;

        private bool ShouldFinish => !_syncConfig.DownloadReceiptsInFastSync || _receiptStorage.LowestInsertedReceiptBlock == 1;

        public SimpleReceiptsSyncFeed(
            ISyncModeSelector syncModeSelector,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            ILogManager logManager)
            : base(syncModeSelector, logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _fastStatusList = new FastStatusList(_blockTree, _pivotNumber, _blockTree.LowestInsertedBodyNumber);
        }

        protected override SyncMode ActivationSyncModes { get; }
            = SyncMode.FastReceipts & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Receipts;

        private bool ShouldBuildANewBatch()
        {
            bool shouldDownloadReceipts = _syncConfig.DownloadReceiptsInFastSync;
            bool allReceiptsDownloaded = _receiptStorage.LowestInsertedReceiptBlock == 1;
            bool isGenesisDownloaded = _fastStatusList.LowestInsertWithoutGaps == 0;
            bool noBatchesLeft = !shouldDownloadReceipts
                                 || allReceiptsDownloaded
                                 || isGenesisDownloaded;

            if (noBatchesLeft)
            {
                if (ShouldFinish)
                {
                    Finish();
                    PostFinishCleanUp();
                }

                return false;
            }

            return true;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksReceipts.Update(_pivotNumber);
            _syncReport.FastBlocksReceipts.MarkEnd();
            _syncReport.ReceiptsInQueue.Update(0);
            _syncReport.ReceiptsInQueue.MarkEnd();
        }

        public override Task<SimpleReceiptsSyncBatch> PrepareRequest()
        {
            SimpleReceiptsSyncBatch batch = null;
            if (ShouldBuildANewBatch())
            {
                BlockInfo[] infos = new BlockInfo[_requestSize];
                _fastStatusList.GetInfosForBatch(infos);
                if (infos[0] != null)
                {
                    batch = new SimpleReceiptsSyncBatch();
                    batch.Infos = infos;
                    batch.MinNumber = infos[0].BlockNumber;
                    batch.Prioritized = true;
                }
                
                // Array.Reverse(infos);
            }

            _receiptStorage.LowestInsertedReceiptBlock = _fastStatusList.LowestInsertWithoutGaps;

            return Task.FromResult(batch);
        }

        public override SyncResponseHandlingResult HandleResponse(SimpleReceiptsSyncBatch batch)
        {
            batch.MarkHandlingStart();
            try
            {
                int added = InsertReceipts(batch);
                return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
            }
            finally
            {
                batch.MarkHandlingEnd();
            }
        }

        private bool TryPrepareReceipts(BlockInfo blockInfo, TxReceipt[] receipts, out TxReceipt[] preparedReceipts)
        {
            BlockHeader header = _blockTree.FindHeader(blockInfo.BlockHash);
            if (header.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                preparedReceipts = receipts.Length == 0 ? receipts : null;
            }
            else
            {
                Keccak receiptsRoot = new ReceiptTrie(blockInfo.BlockNumber, _specProvider, receipts).RootHash;
                if (receiptsRoot != header.ReceiptsRoot)
                {
                    preparedReceipts = null;
                }
                else
                {
                    preparedReceipts = receipts;
                }
            }

            return preparedReceipts != null;
        }

        private int InsertReceipts(SimpleReceiptsSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;

            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo blockInfo = batch.Infos[i];
                TxReceipt[] receipts = (batch.Response?.Length ?? 0) <= i
                    ? null
                    : (batch.Response![i] ?? Array.Empty<TxReceipt>());
                
                if (receipts != null)
                {
                    TxReceipt[] prepared = null;
                    // last batch
                    if (blockInfo == null)
                    {
                        break;
                    }
                    
                    bool isValid = !hasBreachedProtocol && TryPrepareReceipts(blockInfo, receipts, out prepared);
                    if (isValid)
                    {
                        validResponsesCount++;
                        Block block = _blockTree.FindBlock(blockInfo.BlockHash);
                        _receiptStorage.Insert(block, prepared);
                        _fastStatusList.MarkInserted(block.Number);
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        // if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                        _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "invalid tx or ommers root");
                        _fastStatusList.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
                else
                {
                    _fastStatusList.MarkUnknown(blockInfo.BlockNumber);
                }
            }

            lock (_fastStatusList)
            {
                if (validResponsesCount == batch.Infos.Length)
                {
                    _requestSize = Math.Min(256, _requestSize * 2);
                }

                if (validResponsesCount == 0)
                {
                    _requestSize = Math.Max(4, _requestSize / 2);
                }
            }

            if(_logger.IsDebug) _logger.Debug(
                $"Receipts sync batch back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");

            _syncReport.FastBlocksReceipts.Update(_pivotNumber - _fastStatusList.LowestInsertWithoutGaps);
            _syncReport.ReceiptsInQueue.Update(_fastStatusList.QueueSize);
            return validResponsesCount;
        }
    }
}