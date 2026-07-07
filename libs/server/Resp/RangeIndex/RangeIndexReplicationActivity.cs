// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Activity trace for reassembling one migrated Range Index's AOF stream on the replica /
    /// recovery side. Started on the stream's first chunk, accumulated per chunk, and logged (with a
    /// reason) when the reassembly leaves <c>RangeIndexManager.streamReassembly</c> — on completion,
    /// publish failure, supersession by a retry, or an incomplete stream dropped at end of replay.
    /// Mirrors the source-side <c>RangeIndexMigrationActivities</c> traces.
    /// </summary>
    internal sealed class RangeIndexReplicationActivity
    {
        private readonly long timestampStart;
        private int chunkCount;
        private long totalBytesReceived;
        private RangeIndexManager.PublishMigratedIndexResult? publishResult;

        private RangeIndexReplicationActivity() => timestampStart = Stopwatch.GetTimestamp();

        internal static RangeIndexReplicationActivity StartActivity() => new();

        internal void OnChunkReceived(int chunkLength)
        {
            chunkCount++;
            totalBytesReceived += chunkLength;
        }

        internal void OnPublishResult(RangeIndexManager.PublishMigratedIndexResult result) => publishResult = result;

        internal void EndAndLog(ILogger logger, string reason)
        {
            var totalTicks = Stopwatch.GetElapsedTime(timestampStart).Ticks;
            logger?.LogInformation("RangeIndexReplicationReassembly: reason={reason} publishResult={publishResult} chunkCount={chunkCount} totalBytesReceived={totalBytesReceived} totalTicks={totalTicks}",
                reason, publishResult?.ToString() ?? "n/a", chunkCount, totalBytesReceived, totalTicks);
        }
    }
}
