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
        private const int InsertInvalidKv = 1;
        private const int ReadInvalidKey = -3;
        private const int DeleteInvalidKey = -1;

        private nint _tree;

        [SetUp]
        public void Setup()
        {
            _tree = NativeBfTreeMethods.bftree_create(0, 0, 0, 0, 0, StorageMemory, null, 0, null, 0);
            Assert.That(_tree, Is.Not.EqualTo(nint.Zero), "failed to create memory-backed tree");
        }

        [TearDown]
        public void TearDown()
        {
            if (_tree != nint.Zero)
                NativeBfTreeMethods.bftree_drop(_tree);
            _tree = nint.Zero;
        }

        [Test]
        public void Insert_NegativeKeyLen_ReturnsInvalidKv()
        {
            byte value = 0;
            var rc = NativeBfTreeMethods.bftree_insert(_tree, null, -1, &value, 1);
            Assert.That(rc, Is.EqualTo(InsertInvalidKv));
        }

        [Test]
        public void Insert_NegativeValueLen_ReturnsInvalidKv()
        {
            byte key = 0;
            var rc = NativeBfTreeMethods.bftree_insert(_tree, &key, 1, null, -1);
            Assert.That(rc, Is.EqualTo(InsertInvalidKv));
        }

        [Test]
        public void Insert_NullTree_ReturnsInvalidKv()
        {
            byte key = 0, value = 0;
            var rc = NativeBfTreeMethods.bftree_insert(nint.Zero, &key, 1, &value, 1);
            Assert.That(rc, Is.EqualTo(InsertInvalidKv));
        }

        [Test]
        public void Read_NegativeKeyLen_ReturnsInvalidKey()
        {
            byte* outBuffer = stackalloc byte[16];
            int outLen = 0;
            var rc = NativeBfTreeMethods.bftree_read(_tree, null, -1, outBuffer, 16, &outLen);
            Assert.That(rc, Is.EqualTo(ReadInvalidKey));
        }

        [Test]
        public void Read_NegativeOutBufferLen_ReturnsInvalidKey()
        {
            byte key = 0;
            int outLen = 0;
            var rc = NativeBfTreeMethods.bftree_read(_tree, &key, 1, null, -1, &outLen);
            Assert.That(rc, Is.EqualTo(ReadInvalidKey));
        }

        [Test]
        public void Delete_NegativeKeyLen_ReturnsInvalidKey()
        {
            var rc = NativeBfTreeMethods.bftree_delete(_tree, null, -1);
            Assert.That(rc, Is.EqualTo(DeleteInvalidKey));
        }

        [Test]
        public void Delete_NullTree_ReturnsInvalidKey()
        {
            byte key = 0;
            var rc = NativeBfTreeMethods.bftree_delete(nint.Zero, &key, 1);
            Assert.That(rc, Is.EqualTo(DeleteInvalidKey));
        }

        [Test]
        public void ScanWithCount_NegativeStartKeyLen_ReturnsNullHandle()
        {
            byte start = 0;
            var handle = NativeBfTreeMethods.bftree_scan_with_count(_tree, &start, -1, 10, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanWithCount_NegativeCount_ReturnsNullHandle()
        {
            byte start = 0;
            var handle = NativeBfTreeMethods.bftree_scan_with_count(_tree, &start, 1, -1, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanWithEndKey_NegativeStartKeyLen_ReturnsNullHandle()
        {
            byte start = 0, end = 0xff;
            var handle = NativeBfTreeMethods.bftree_scan_with_end_key(_tree, &start, -1, &end, 1, 2);
            Assert.That(handle, Is.EqualTo(nint.Zero));
        }

        [Test]
        public void ScanWithEndKey_NegativeEndKeyLen_ReturnsNullHandle()
        {
            byte start = 0, end = 0xff;
            var handle = NativeBfTreeMethods.bftree_scan_with_end_key(_tree, &start, 1, &end, -1, 2);
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