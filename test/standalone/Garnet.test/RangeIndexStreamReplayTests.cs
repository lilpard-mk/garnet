// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>
    /// Unit tests for the AOF-replay reassembly bookkeeping in <see cref="RangeIndexManager"/>
    /// (<c>ProcessStreamChunk</c> / <c>CleanupIncompleteStreamReassembly</c>): a stream's first chunk
    /// must reset stale per-key state (so a retry after a failed/partial stream reassembles cleanly),
    /// incomplete reassemblies must be cleaned up, and malformed chunks must be dropped.
    /// </summary>
    [TestFixture]
    public class RangeIndexStreamReplayTests : TestBase
    {
        private string testDir;

        [SetUp]
        public void Setup()
        {
            testDir = Path.Combine(TestUtils.MethodTestDir, "ri-stream-replay-test");
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
            Directory.CreateDirectory(testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
            TestUtils.OnTearDown();
        }

        private static byte[] MakeStub()
        {
            var stub = new byte[RangeIndexManager.IndexSizeBytes];
            for (var i = 0; i < stub.Length; i++)
                stub[i] = (byte)(0xC0 + i);
            return stub;
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            new Random(1234).NextBytes(b);
            return b;
        }

        /// <summary>Frame a full stream into (chunk, isFirst, isLast) tuples, mirroring ReplicateRangeIndexStream.</summary>
        private static List<(byte[] Chunk, bool IsFirst, bool IsLast)> BuildStreamChunks(byte[] key, byte[] stub, byte[] fileData, int chunkSize)
        {
            var serializer = new RangeIndexChunkedSerializer(key, stub, fileData.Length);
            var result = new List<(byte[], bool, bool)>();
            var dest = new byte[chunkSize];
            var fileOffset = 0;
            var isFirst = true;

            while (!serializer.IsComplete)
            {
                var written = 0;
                while (!serializer.IsComplete && written < chunkSize)
                {
                    if (serializer.NeedsFileData)
                    {
                        var take = Math.Min(chunkSize, fileData.Length - fileOffset);
                        serializer.SupplyFileData(fileData.AsMemory(fileOffset, take));
                        fileOffset += take;
                    }

                    var n = serializer.MoveNext(dest.AsSpan(written, chunkSize - written));
                    if (n == 0)
                        break;
                    written += n;
                }

                if (written == 0)
                    continue;

                result.Add((dest.AsSpan(0, written).ToArray(), isFirst, serializer.IsComplete));
                isFirst = false;
            }

            return result;
        }

        [Test]
        public void PartialStreamIsPendingThenCleanedUp()
        {
            using var mgr = new RangeIndexManager(testDir);
            var key = Encoding.UTF8.GetBytes("k1");
            var chunks = BuildStreamChunks(key, MakeStub(), RandomBytes(8192), chunkSize: 512);
            ClassicAssert.Greater(chunks.Count, 2, "test needs a multi-chunk stream");

            // Feed only the first chunk: reassembly is now in progress.
            mgr.ProcessStreamChunk(session: null, key, chunks[0].Chunk, chunks[0].IsFirst, chunks[0].IsLast);
            ClassicAssert.AreEqual(1, mgr.PendingStreamReassemblyCount);

            // End-of-replay cleanup drops the incomplete reassembly (no leak).
            mgr.DisposeIncompleteStreamReassembly();
            ClassicAssert.AreEqual(0, mgr.PendingStreamReassemblyCount);
        }

        [Test]
        public void RetryFirstChunkResetsStalePartialReassembly()
        {
            using var mgr = new RangeIndexManager(testDir);
            var key = Encoding.UTF8.GetBytes("k1");
            var chunks = BuildStreamChunks(key, MakeStub(), RandomBytes(8192), chunkSize: 512);
            ClassicAssert.Greater(chunks.Count, 3, "test needs a multi-chunk stream");

            // Attempt 1 fails after the first chunk, leaving a partial reassembly for this key.
            mgr.ProcessStreamChunk(session: null, key, chunks[0].Chunk, isFirst: true, isLast: false);
            ClassicAssert.AreEqual(1, mgr.PendingStreamReassemblyCount);

            // Attempt 2 (retry) replays the same stream. Its first chunk (isFirst) must reset the stale
            // partial so the retry's chunks reassemble cleanly. Feed every chunk except the trailer so
            // we stay incomplete (avoids the publish path). If the reset did NOT happen, the retry's
            // header bytes would be fed into the stale mid-file deserializer and corrupt it (checksum
            // failure -> the reassembly would be dropped, leaving count 0).
            for (var i = 0; i < chunks.Count - 1; i++)
                mgr.ProcessStreamChunk(session: null, key, chunks[i].Chunk, isFirst: i == 0, isLast: false);

            ClassicAssert.AreEqual(1, mgr.PendingStreamReassemblyCount,
                "retry stream should reassemble cleanly after the first chunk reset the stale partial");

            mgr.DisposeIncompleteStreamReassembly();
            ClassicAssert.AreEqual(0, mgr.PendingStreamReassemblyCount);
        }

        [Test]
        public void MalformedChunkIsDropped()
        {
            using var mgr = new RangeIndexManager(testDir);
            var key = Encoding.UTF8.GetBytes("k1");

            // A first chunk that begins with a non-positive key length is rejected by the deserializer,
            // and the failed reassembly must be removed (not left pending).
            var malformed = new byte[16]; // leading 4 bytes = 0 => invalid key length
            mgr.ProcessStreamChunk(session: null, key, malformed, isFirst: true, isLast: false);
            ClassicAssert.AreEqual(0, mgr.PendingStreamReassemblyCount);
        }

        [Test]
        public void FinalFlagOnIncompleteStreamDropsReassembly()
        {
            using var mgr = new RangeIndexManager(testDir);
            var key = Encoding.UTF8.GetBytes("k1");
            var chunks = BuildStreamChunks(key, MakeStub(), RandomBytes(8192), chunkSize: 512);
            ClassicAssert.Greater(chunks.Count, 2, "test needs a multi-chunk stream");

            // Feed the first chunk flagged as the final one while the stream is still incomplete:
            // this is a malformed/truncated stream and must be dropped.
            mgr.ProcessStreamChunk(session: null, key, chunks[0].Chunk, isFirst: true, isLast: true);
            ClassicAssert.AreEqual(0, mgr.PendingStreamReassemblyCount);
        }
    }
}
