// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Activity trace for reassembling one migrated Range Index's AOF stream on the replica /
    /// recovery side. Started on the stream's first chunk, accumulated per chunk, and logged (with a
    /// reason) when the reassembly leaves <c>RangeIndexManager.streamReassembly</c> — on completion,
    /// publish failure, supersession by a retry, or an incomplete stream dropped at end of replay.
    /// </summary>
    internal sealed class RangeIndexReplicationReassemblyActivity
    {
        private readonly long timestampStart;
        private int chunkCount;
        private long totalBytesReceived;
        private RangeIndexManager.PublishMigratedIndexResult? publishResult;

        private RangeIndexReplicationReassemblyActivity() => timestampStart = Stopwatch.GetTimestamp();

        internal static RangeIndexReplicationReassemblyActivity StartActivity() => new();

        internal void OnChunkReceived(int chunkLength)
        {
            chunkCount++;
            totalBytesReceived += chunkLength;
        }

        internal void OnPublishResult(RangeIndexManager.PublishMigratedIndexResult result) => publishResult = result;

        internal void EndAndLog(ILogger logger, ReadOnlySpan<byte> key, string reason)
        {
            if (logger == null)
                return;

            var totalTicks = Stopwatch.GetElapsedTime(timestampStart).Ticks;
            logger.LogInformation("RangeIndexReplicationReassemblyActivity: key={key} reason={reason} publishResult={publishResult} chunkCount={chunkCount} totalBytesReceived={totalBytesReceived} totalTicks={totalTicks}",
                Encoding.UTF8.GetString(key), reason, publishResult?.ToString() ?? "n/a", chunkCount, totalBytesReceived, totalTicks);
        }
    }
}
