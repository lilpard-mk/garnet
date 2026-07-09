// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Garnet.common;
using Garnet.server.BfTreeInterop;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    /// <summary>
    /// Provides methods for managing range index operations, including replication and handling of AOF (Append-Only
    /// File) entries for create, set, and delete operations.
    /// </summary>
    public sealed partial class RangeIndexManager
    {
        /// <summary>
        /// Log RI.SET to AOF via direct enqueue (no synthetic RMW).
        /// Skipped when <paramref name="storedProcMode"/> is true (stored procedure logs as a unit).
        /// </summary>
        internal void ReplicateRangeIndexSet(PinnedSpanByte key, PinnedSpanByte field, PinnedSpanByte value,
            GarnetAppendOnlyFile appendOnlyFile, long version, int sessionId, bool storedProcMode)
        {
            if (appendOnlyFile == null || storedProcMode) return;

            var replicateParseState = new SessionParseState();
            replicateParseState.InitializeWithArguments(field, value);
            var input = new StringInput(RespCommand.RISET, ref replicateParseState);
            input.header.flags |= RespInputFlags.Deterministic;

            appendOnlyFile.Log.Enqueue(
                AofEntryType.StoreRMW,
                version,
                sessionId,
                key.ReadOnlySpan,
                ref input,
                out _);
        }

        /// <summary>
        /// Log RI.DEL to AOF via direct enqueue (no synthetic RMW).
        /// Skipped when <paramref name="storedProcMode"/> is true (stored procedure logs as a unit).
        /// </summary>
        internal void ReplicateRangeIndexDel(PinnedSpanByte key, PinnedSpanByte field,
            GarnetAppendOnlyFile appendOnlyFile, long version, int sessionId, bool storedProcMode)
        {
            if (appendOnlyFile == null || storedProcMode) return;

            var replicateParseState = new SessionParseState();
            replicateParseState.InitializeWithArgument(field);
            var input = new StringInput(RespCommand.RIDEL, ref replicateParseState);
            input.header.flags |= RespInputFlags.Deterministic;

            appendOnlyFile.Log.Enqueue(
                AofEntryType.StoreRMW,
                version,
                sessionId,
                key.ReadOnlySpan,
                ref input,
                out _);
        }

        /// <summary>
        /// Handle RI.CREATE replay from AOF.
        /// </summary>
        /// <remarks>
        /// The AOF entry contains the serialized stub bytes (including a stale TreeHandle
        /// from the original process). This method:
        /// <list type="number">
        /// <item>Extracts BfTree configuration from the stale stub.</item>
        /// <item>Creates a fresh BfTree instance with a new native pointer.</item>
        /// <item>Replaces the stale TreeHandle in the stub bytes with the new pointer.</item>
        /// <item>Lets the normal RMW path (InitialUpdater) create the store record.</item>
        /// </list>
        /// If the key already exists (e.g., AOF replay of a duplicate RI.CREATE after
        /// checkpoint recovery), the RMW returns <c>InPlaceUpdated</c> and the fresh
        /// BfTree is disposed.
        /// </remarks>
        /// <param name="session">The storage session for issuing the RMW.</param>
        /// <param name="key">The Garnet key being created.</param>
        /// <param name="input">The RMW input containing the stub bytes in parseState.</param>
        internal unsafe void HandleRangeIndexCreateReplay(StorageSession session, ReadOnlySpan<byte> key, ref StringInput input)
        {
            var stubSpan = input.parseState.GetArgSliceByRef(0).Span;
            if (stubSpan.Length != IndexSizeBytes)
                throw new GarnetException($"Corrupt RI.CREATE AOF entry: stub size {stubSpan.Length}, expected {IndexSizeBytes}");

            ref var stub = ref Unsafe.As<byte, RangeIndexStub>(ref MemoryMarshal.GetReference(stubSpan));

            // Create a fresh BfTree from the config in the stub
            BfTreeService bfTree;
            try
            {
                bfTree = CreateBfTree(
                    (StorageBackendType)stub.StorageBackend, key,
                    stub.CacheSize, stub.MinRecordSize, stub.MaxRecordSize,
                    stub.MaxKeyLen, stub.LeafPageSize);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to recreate BfTree during AOF replay");
                return;
            }

            // Replace stale handle with fresh one in the stub bytes
            stub.TreeHandle = bfTree.NativePtr;
            stub.ResetFlags();

            // Let the normal RMW path create the record from the updated stub bytes
            var output = new StringOutput();
            var pinnedKey = PinnedSpanByte.FromPinnedSpan(key);
            var status = session.stringBasicContext.RMW((FixedSpanByteKey)pinnedKey, ref input, ref output);
            if (status.IsPending)
                StorageSession.CompletePendingForSession(ref status, ref output, ref session.stringBasicContext);

            if (status.Record.Created)
            {
                var keyHash = session.stringBasicContext.GetKeyHash((FixedSpanByteKey)pinnedKey);
                RegisterIndex(bfTree, keyHash, key);
            }
            else
            {
                bfTree.Dispose();
            }
        }

        /// <summary>
        /// Handle RI.SET replay from AOF. Acquires a shared lock, reads the stub to get
        /// the live BfTree pointer, then performs the native insert.
        /// </summary>
        /// <param name="session">The storage session for reading the stub.</param>
        /// <param name="key">The Garnet key of the RangeIndex.</param>
        /// <param name="input">The RMW input containing field and value in parseState.</param>
        internal void HandleRangeIndexSetReplay(StorageSession session, ReadOnlySpan<byte> key, ref StringInput input)
        {
            var field = input.parseState.GetArgSliceByRef(0);
            var value = input.parseState.GetArgSliceByRef(1);

            var pinnedKey = PinnedSpanByte.FromPinnedSpan(key);
            var inputCopy = input;
            inputCopy.arg1 = default;
            Span<byte> stubSpan = stackalloc byte[IndexSizeBytes];

            using (ReadRangeIndex(session, pinnedKey, ref inputCopy, stubSpan, out var status))
            {
                if (status != GarnetStatus.OK) return;
                var treePtr = ReadIndex(stubSpan).TreeHandle;
                if (treePtr == nint.Zero) return;
                BfTreeService.InsertByPtr(treePtr, field, value);
            }
        }

        /// <summary>
        /// Handle RI.DEL replay from AOF. Acquires a shared lock, reads the stub to get
        /// the live BfTree pointer, then performs the native delete.
        /// </summary>
        /// <param name="session">The storage session for reading the stub.</param>
        /// <param name="key">The Garnet key of the RangeIndex.</param>
        /// <param name="input">The RMW input containing the field in parseState.</param>
        internal void HandleRangeIndexDelReplay(StorageSession session, ReadOnlySpan<byte> key, ref StringInput input)
        {
            var field = input.parseState.GetArgSliceByRef(0);

            var pinnedKey = PinnedSpanByte.FromPinnedSpan(key);
            var inputCopy = input;
            inputCopy.arg1 = default;
            Span<byte> stubSpan = stackalloc byte[IndexSizeBytes];

            using (ReadRangeIndex(session, pinnedKey, ref inputCopy, stubSpan, out var status))
            {
                if (status != GarnetStatus.OK) return;
                var treePtr = ReadIndex(stubSpan).TreeHandle;
                if (treePtr == nint.Zero) return;
                BfTreeService.DeleteByPtr(treePtr, field);
            }
        }

        /// <summary>
        /// Sentinel placed in <c>StringInput.arg1</c> of the migration-publish <see cref="RespCommand.RICREATE"/>
        /// RMW so <c>MainSessionFunctions.WriteLogRMW</c> skips auto-logging it: the range index stream
        /// is the single AOF source of truth for a migrated key. Used only in the migration code path.
        /// </summary>
        internal const long StreamedPublishLogArg = long.MinValue;

        /// <summary>
        /// In-progress AOF-stream reassembly state, keyed by RangeIndex key. During AOF replay
        /// (replica replication or crash recovery) the chunks of one migrated key's
        /// <see cref="AofEntryType.RangeIndexStreamChunk"/> stream may be interleaved with unrelated AOF entries;
        /// per-key state lets reassembly survive those gaps. All entries for a given key hash to the
        /// same virtual sublog, so same-key chunks arrive in order on a single replay task.
        /// </summary>
        private readonly ConcurrentDictionary<byte[], StreamReassemblyState> streamReassembly = new(ByteArrayComparer.Instance);

        /// <summary>Number of in-progress per-key range index stream reassemblies (test visibility).</summary>
        internal int PendingStreamReassemblyCount => streamReassembly.Count;

        /// <summary>
        /// Per-key AOF-stream reassembly state: the deserializer reassembling the stream plus the
        /// <see cref="RangeIndexReplicationReassemblyActivity"/> tracing it.
        /// </summary>
        private sealed class StreamReassemblyState(RangeIndexChunkedDeserializer deserializer, RangeIndexReplicationReassemblyActivity activity)
        {
            internal readonly RangeIndexChunkedDeserializer deserializer = deserializer;
            internal readonly RangeIndexReplicationReassemblyActivity activity = activity;
        }

        // Bit flags packed into the range index stream chunk AOF entry's StringInput.arg1.
        private const long StreamChunkIsLastFlag = 1L;
        private const long StreamChunkIsFirstFlag = 2L;

        /// <summary>
        /// Stream a migrated RangeIndex's serialized BfTree file into the AOF as a sequence of
        /// chunked <see cref="AofEntryType.RangeIndexStreamChunk"/> entries, reusing <see cref="RangeIndexChunkedSerializer"/>
        /// for framing. No-op when <paramref name="appendOnlyFile"/> is null (AOF disabled / replica or
        /// recovery replay, which replays with <c>recordToAof:false</c>). Each chunk is one AOF entry no
        /// larger than <paramref name="chunkSize"/>, which must fit within a single AOF page.
        /// </summary>
        internal unsafe void ReplicateRangeIndexStream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> stub, string filePath,
            GarnetAppendOnlyFile appendOnlyFile, long version, int sessionId, int chunkSize = DefaultMigrationChunkSize)
        {
            if (appendOnlyFile == null) return;

            // Cap the chunk so each AOF entry (chunk payload + per-key framing) fits within one AOF
            // page. The exact overhead is obtained from the log itself (GetMaxAofEntryOverhead), so it
            // stays correct for the actual key length and if the AOF framing ever changes.
            var pageBytes = 1L << appendOnlyFile.Log.UnsafeGetLogPageSizeBits();
            var perEntryOverhead = appendOnlyFile.Log.GetMaxAofEntryOverhead(key.Length, ChunkStringInputFramingBytes());
            var maxChunkForPage = (int)(pageBytes - perEntryOverhead);
            if (maxChunkForPage < RangeIndexChunkedSerializer.MinChunkSize)
                throw new GarnetException($"AOF page ({pageBytes} bytes) is too small to stream a migrated RangeIndex with a {key.Length}-byte key");
            if (chunkSize > maxChunkForPage)
                chunkSize = maxChunkForPage;

            if (chunkSize < RangeIndexChunkedSerializer.MinChunkSize)
                chunkSize = RangeIndexChunkedSerializer.MinChunkSize;

            var fileLen = new FileInfo(filePath).Length;
            var serializer = new RangeIndexChunkedSerializer(key.ToArray(), stub.ToArray(), fileLen);
            var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: chunkSize);

            // Reuse the migration reader to drive the serializer — the exact same chunking the
            // source-side migration transmit path uses. tempFilePath is null so the reader disposes the
            // FileStream but does NOT delete the snapshot: PublishMigratedIndex moves it into place after
            // streaming completes.
            using var reader = new RangeIndexMigrationReader(serializer, fs, tempFilePath: null, chunkSize, logger);

            var destBuffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                var isFirst = true;
                while (!reader.IsComplete)
                {
                    var written = reader.ReadNextChunk(destBuffer.AsSpan(0, chunkSize));
                    if (written == 0)
                        continue;

                    EnqueueRangeIndexStreamChunk(appendOnlyFile, version, sessionId, key, destBuffer.AsSpan(0, written), isFirst, reader.IsComplete);
                    isFirst = false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(destBuffer);
            }
        }

        /// <summary>
        /// Non-payload framing bytes of the <see cref="StringInput"/> that wraps a single stream chunk
        /// (input header + <c>arg1</c> + parse-state prefixes for one argument), independent of the chunk size.
        /// </summary>
        private static int ChunkStringInputFramingBytes()
        {
            var parseState = new SessionParseState();
            parseState.InitializeWithArgument(default); // single zero-length argument
            var probe = new StringInput(RespCommand.NONE, ref parseState, arg1: 0, flags: RespInputFlags.Deterministic);
            return probe.SerializedLength; // equals the framing, since the argument payload is empty
        }

        /// <summary>Enqueue a single <see cref="AofEntryType.RangeIndexStreamChunk"/> chunk to the AOF.</summary>
        private unsafe void EnqueueRangeIndexStreamChunk(GarnetAppendOnlyFile appendOnlyFile, long version, int sessionId,
            ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunk, bool isFirst, bool isLast)
        {
            fixed (byte* chunkPtr = chunk)
            {
                var chunkSlice = PinnedSpanByte.FromPinnedPointer(chunkPtr, chunk.Length);
                var parseState = new SessionParseState();
                parseState.InitializeWithArgument(chunkSlice);
                var input = new StringInput(RespCommand.NONE, ref parseState,
                    arg1: (isLast ? StreamChunkIsLastFlag : 0) | (isFirst ? StreamChunkIsFirstFlag : 0),
                    flags: RespInputFlags.Deterministic);

                appendOnlyFile.Log.Enqueue(
                    AofEntryType.RangeIndexStreamChunk,
                    version,
                    sessionId,
                    key,
                    ref input,
                    out _);
            }
        }

        /// <summary>
        /// Handle a <see cref="AofEntryType.RangeIndexStreamChunk"/> chunk on AOF replay (replica replication or
        /// crash recovery). Feeds the chunk into the per-key <see cref="RangeIndexChunkedDeserializer"/>;
        /// once the stream completes, publishes the reassembled BfTree via <see cref="PublishMigratedIndex"/>.
        /// </summary>
        internal void HandleRangeIndexStreamReplay(StorageSession session, ReadOnlySpan<byte> key, ref StringInput input)
        {
            var chunk = input.parseState.GetArgSliceByRef(0).ReadOnlySpan;
            var isLast = (input.arg1 & StreamChunkIsLastFlag) != 0;
            var isFirst = (input.arg1 & StreamChunkIsFirstFlag) != 0;
            ProcessStreamChunk(session, key, chunk, isFirst, isLast);
        }

        /// <summary>
        /// Core range index stream reassembly step: reset stale state on a stream's first chunk, feed the chunk
        /// to the per-key deserializer, and publish when the stream completes.
        /// </summary>
        internal void ProcessStreamChunk(StorageSession session, ReadOnlySpan<byte> key, ReadOnlySpan<byte> chunk, bool isFirst, bool isLast)
        {
            var keyArr = key.ToArray();

            // A new stream's first chunk supersedes any incomplete reassembly for the same key.
            if (isFirst)
                RemoveAndDisposeStreamReassembly(keyArr, "NewStream");

            var state = streamReassembly.GetOrAdd(keyArr, _ => new StreamReassemblyState(new RangeIndexChunkedDeserializer(DeriveTempMigrationPath(), logger), RangeIndexReplicationReassemblyActivity.StartActivity()));
            var deserializer = state.deserializer;
            state.activity.OnChunkReceived(chunk.Length);

            if (!deserializer.ProcessChunk(chunk) || deserializer.HasError)
            {
                logger?.LogError("HandleRangeIndexStreamReplay: failed to process range index stream chunk for key {key}", Encoding.UTF8.GetString(key));
                RemoveAndDisposeStreamReassembly(keyArr, "ChunkProcessingError");
                return;
            }

            if (deserializer.IsComplete)
            {
                var publishResult = PublishMigratedIndex(deserializer.Key, deserializer.Stub, deserializer.TempPath, replaceOption: false, ref session.stringBasicContext, session.functionsState.appendOnlyFile);
                state.activity.OnPublishResult(publishResult);

                if (publishResult == PublishMigratedIndexResult.Failed)
                    logger?.LogError("HandleRangeIndexStreamReplay: PublishMigratedIndex failed during AOF replay for key {key}", Encoding.UTF8.GetString(key));

                RemoveAndDisposeStreamReassembly(keyArr, publishResult == PublishMigratedIndexResult.Failed ? "PublishFailed" : "Completed");
                return;
            }

            if (isLast)
            {
                // Final-chunk flag set but the deserializer did not reach completion — the stream is
                // malformed/truncated. Drop the partial state.
                logger?.LogError("HandleRangeIndexStreamReplay: final range index stream chunk flag set but stream is incomplete for key {key}", Encoding.UTF8.GetString(key));
                RemoveAndDisposeStreamReassembly(keyArr, "FinalChunkButDeserializerIncomplete");
            }
        }

        /// <summary>
        /// Dispose and drop any in-progress AOF-stream reassembly state. Called at the end of an AOF
        /// replay / recovery pass so a stream that was truncated mid-flight (e.g. a crash between
        /// chunks) does not leak its temp file or deserializer.
        /// </summary>
        internal void CleanupIncompleteStreamReassembly()
        {
            // Runs from AofProcessor.Dispose after all replay tasks have finished, so there are no
            // concurrent writers. Iterate the ConcurrentDictionary.Keys snapshot (a copy) so removal
            // during the loop is safe regardless.
            foreach (var key in streamReassembly.Keys)
            {
                logger?.LogWarning("CleanupIncompleteStreamReassembly: discarding incomplete range index stream reassembly for key {key}", Encoding.UTF8.GetString(key));
                RemoveAndDisposeStreamReassembly(key, "incomplete at end of replay");
            }
        }

        private void RemoveAndDisposeStreamReassembly(byte[] key, string reason)
        {
            if (streamReassembly.TryRemove(key, out var state))
            {
                state.activity.EndAndLog(logger, key, reason);
                state.deserializer.Dispose();
            }
        }
    }
}