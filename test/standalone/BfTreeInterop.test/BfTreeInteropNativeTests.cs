// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Garnet.server.BfTreeInterop;
using NUnit.Framework;

namespace BfTreeInterop.test
{
    /// <summary>
    /// Tests that call the native P/Invoke layer (<see cref="NativeBfTreeMethods"/>) directly with
    /// invalid arguments — negative lengths, null data pointers, and a null tree handle — bypassing
    /// the managed <c>PinnedSpanByte</c> / <c>BfTreeService</c> wrappers. These exercise the
    /// FFI-boundary guards at the exact point where a signed length would be cast to an unsigned
    /// size, verifying they return a sentinel instead of constructing an invalid slice.
    /// </summary>
    [TestFixture]
    public unsafe class BfTreeInteropNativeTests
    {
        private const byte StorageMemory = 1;

        // Native result codes (mirror the Rust constants in lib.rs).
        private const int InsertInvalidArgs = -1;
        private const int ReadInvalidArgs = -4;
        private const int DeleteInvalidArgs = -1;

        private nint tree;

        [SetUp]
        public void Setup()
        {
            tree = NativeBfTreeMethods.bftree_create(0, 0, 0, 0, 0, StorageMemory, null, 0, null, 0);
            Assert.That(tree, Is.Not.EqualTo(nint.Zero), "failed to create memory-backed tree");
        }

        [TearDown]
        public void TearDown()
        {
            if (tree != nint.Zero)
                NativeBfTreeMethods.bftree_drop(tree);
            tree = nint.Zero;
        }

        [Test]
        public void Insert_NegativeKeyLen_ReturnsInvalidArgs()
        {
            byte value = 0;
            var rc = NativeBfTreeMethods.bftree_insert(tree, null, -1, &value, 1);
            Assert.That(rc, Is.EqualTo(InsertInvalidArgs));
        }

        [Test]
        public void Insert_NegativeValueLen_ReturnsInvalidArgs()
        {
            byte key = 0;
            var rc = NativeBfTreeMethods.bftree_insert(tree, &key, 1, null, -1);
            Assert.That(rc, Is.EqualTo(InsertInvalidArgs));
        }

        [Test]
        public void Insert_NullTree_ReturnsInvalidArgs()
        {
            byte key = 0, value = 0;
            var rc = NativeBfTreeMethods.bftree_insert(nint.Zero, &key, 1, &value, 1);
            Assert.That(rc, Is.EqualTo(InsertInvalidArgs));
        }

        [Test]
        public void Read_NegativeKeyLen_ReturnsInvalidArgs()
        {
            byte* outBuffer = stackalloc byte[16];
            int outLen = 0;
            var rc = NativeBfTreeMethods.bftree_read(tree, null, -1, outBuffer, 16, &outLen);
            Assert.That(rc, Is.EqualTo(ReadInvalidArgs));
        }

        [Test]
        public void Read_NegativeOutBufferLen_ReturnsInvalidArgs()
        {
            byte key = 0;
            int outLen = 0;
            var rc = NativeBfTreeMethods.bftree_read(tree, &key, 1, null, -1, &outLen);
            Assert.That(rc, Is.EqualTo(ReadInvalidArgs));
        }

        [Test]
        public void Delete_NegativeKeyLen_ReturnsInvalidArgs()
        {
            var rc = NativeBfTreeMethods.bftree_delete(tree, null, -1);
            Assert.That(rc, Is.EqualTo(DeleteInvalidArgs));
        }

        [Test]
        public void Delete_NullTree_ReturnsInvalidArgs()
        {
            byte key = 0;
            var rc = NativeBfTreeMethods.bftree_delete(nint.Zero, &key, 1);
            Assert.That(rc, Is.EqualTo(DeleteInvalidArgs));
        }

        [Test]
        public void ScanWithCount_NegativeStartKeyLen_ReturnsNullHandle()
        {
            byte start = 0;
            var handle = NativeBfTreeMethods.bftree_scan_with_count(tree, &start, -1, 10, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanWithCount_NegativeCount_ReturnsNullHandle()
        {
            byte start = 0;
            var handle = NativeBfTreeMethods.bftree_scan_with_count(tree, &start, 1, -1, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanWithEndKey_NegativeStartKeyLen_ReturnsNullHandle()
        {
            byte start = 0, end = 0xff;
            var handle = NativeBfTreeMethods.bftree_scan_with_end_key(tree, &start, -1, &end, 1, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanWithEndKey_NegativeEndKeyLen_ReturnsNullHandle()
        {
            byte start = 0, end = 0xff;
            var handle = NativeBfTreeMethods.bftree_scan_with_end_key(tree, &start, 1, &end, -1, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanNext_NullHandleNegativeBuffer_ReturnsZero()
        {
            int keyLen = 0, valueLen = 0;
            var rc = NativeBfTreeMethods.bftree_scan_next(nint.Zero, null, -1, &keyLen, &valueLen);
            Assert.That(rc, Is.EqualTo(0));
        }
    }
}