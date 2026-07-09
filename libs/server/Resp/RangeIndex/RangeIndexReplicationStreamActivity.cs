// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Garnet.server
{
    /// <summary>
    /// Activity trace for streaming one migrated Range Index's snapshot file into the AOF as chunked
    /// <c>RangeIndexStreamChunk</c> entries (the primary/producer side of AOF replication), mirroring
    /// the source-side <c>RangeIndexMigrationActivities.TransmitActivity</c>. Started when streaming
    /// begins and logged (with the key) when it ends, whether it succeeded or threw.
    /// </summary>
    internal sealed class RangeIndexReplicationStreamActivity
    {
        private readonly long timestampStart;
        private readonly int chunkSize;
        private long fileSizeBytes;
        private int chunkCount;
        private long totalBytesEnqueued;
        private string error;

        private RangeIndexReplicationStreamActivity(int chunkSize)
        {
            timestampStart = Stopwatch.GetTimestamp();
            this.chunkSize = chunkSize;
        }

        internal static RangeIndexReplicationStreamActivity StartActivity(int chunkSize) => new(chunkSize);

        internal void OnFileLength(long fileBytes) => fileSizeBytes = fileBytes;

        internal void OnChunkEnqueued(int bytes)
        {
            chunkCount++;
            totalBytesEnqueued += bytes;
        }

        internal void OnError(string error) => this.error ??= error;

        internal void EndAndLog(ILogger logger, ReadOnlySpan<byte> key)
        {
            if (logger == null)
                return;

            var totalTicks = Stopwatch.GetElapsedTime(timestampStart).Ticks;
            logger.LogInformation("RangeIndexReplicationStreamActivity: key={key} isError={isError} errorStr={errorStr} chunkSize={chunkSize} fileSizeBytes={fileSizeBytes} chunkCount={chunkCount} totalBytesEnqueued={totalBytesEnqueued} totalTicks={totalTicks}",
                Encoding.UTF8.GetString(key), error != null, error, chunkSize, fileSizeBytes, chunkCount, totalBytesEnqueued, totalTicks);
        }
    }
}
