﻿/*
 * Copyright (C) 2008, Shawn O. Pearce <spearce@spearce.org>
 * Copyright (C) 2009, Henon <meinrad.recheis@gmail.com>
 *
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or
 * without modification, are permitted provided that the following
 * conditions are met:
 *
 * - Redistributions of source code must retain the above copyright
 *   notice, this list of conditions and the following disclaimer.
 *
 * - Redistributions in binary form must reproduce the above
 *   copyright notice, this list of conditions and the following
 *   disclaimer in the documentation and/or other materials provided
 *   with the distribution.
 *
 * - Neither the name of the Git Development Community nor the
 *   names of its contributors may be used to endorse or promote
 *   products derived from this software without specific prior
 *   written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 * NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */



using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace GitSharp.Util
{
    /**
     * A fully buffered output stream using local disk storage for large data.
     * <p>
     * Initially this output stream buffers to memory, like ByteArrayOutputStream
     * might do, but it shifts to using an on disk temporary file if the output gets
     * too large.
     * <p>
     * The content of this buffered stream may be sent to another OutputStream only
     * after this stream has been properly closed by {@link #close()}.
     */
    public class TemporaryBuffer : Stream
    {
        public static int DEFAULT_IN_CORE_LIMIT = 1024 * 1024;

        /** Chain of data, if we are still completely in-core; otherwise null. */
        private List<Block> blocks;

        /**
         * Maximum number of bytes we will permit storing in memory.
         * <p>
         * When this limit is reached the data will be shifted to a file on disk,
         * preventing the JVM heap from growing out of control.
         */
        private int inCoreLimit;

        /**
         * Location of our temporary file if we are on disk; otherwise null.
         * <p>
         * If we exceeded the {@link #inCoreLimit} we nulled out {@link #blocks} and
         * created this file instead. All output goes here through {@link #diskOut}.
         */
        private FileInfo onDiskFile;

        /** If writing to {@link #onDiskFile} this is a buffered stream to it. */
        private BufferedStream diskOut;

        /** Create a new empty temporary buffer. */
        public TemporaryBuffer()
        {
            inCoreLimit = DEFAULT_IN_CORE_LIMIT;
            blocks = new List<Block>(inCoreLimit / Block.SZ);
            blocks.Add(new Block());
        }


        public void write(int b)
        {
            if (blocks == null)
            {
                diskOut.WriteByte((byte)b);
                return;
            }

            Block s = last();
            if (s.isFull())
            {
                if (reachedInCoreLimit())
                {
                    diskOut.WriteByte((byte)b);
                    return;
                }

                s = new Block();
                blocks.Add(s);
            }
            s.buffer[s.count++] = (byte)b;
        }

        public void write(byte[] b, int off, int len)
        {
            if (blocks != null)
            {
                while (len > 0)
                {
                    Block s = last();
                    if (s.isFull())
                    {
                        if (reachedInCoreLimit())
                            break;

                        s = new Block();
                        blocks.Add(s);
                    }

                    int n = Math.Min(Block.SZ - s.count, len);
                    Array.Copy(b, off, s.buffer, s.count, n);
                    s.count += n;
                    len -= n;
                    off += n;
                }
            }

            if (len > 0)
                diskOut.Write(b, off, len);
        }


        public void write(byte[] bytes)
        {
            write(bytes, 0, bytes.Length);
        }

        /**
         * Copy all bytes remaining on the input stream into this buffer.
         *
         * @param in
         *            the stream to read from, until EOF is reached.
         * @
         *             an error occurred reading from the input stream, or while
         *             writing to a local temporary file.
         */
        public void copy(Stream @in)
        {
            if (blocks != null)
            {
                for (; ; )
                {
                    Block s = last();
                    if (s.isFull())
                    {
                        if (reachedInCoreLimit())
                            break;
                        s = new Block();
                        blocks.Add(s);
                    }

                    int n = @in.Read(s.buffer, s.count, Block.SZ - s.count);
                    if (n < 1)
                        return;
                    s.count += n;
                }
            }

            byte[] tmp = new byte[Block.SZ];
            int nn;
            while ((nn = @in.Read(tmp, 0, tmp.Length)) > 0)
                diskOut.Write(tmp, 0, nn);
        }

        private Block last()
        {
            return blocks[blocks.Count - 1];
        }

        private bool reachedInCoreLimit()
        {
            if (blocks.Count * Block.SZ < inCoreLimit)
                return false;
            onDiskFile = new FileInfo("gitsharp_" + Path.GetRandomFileName());
            Block last = blocks[blocks.Count - 1];
            blocks.RemoveAt(blocks.Count - 1);
            var diskOut_filestream = new FileStream(onDiskFile.FullName, System.IO.FileMode.Create, FileAccess.Write);
            foreach (Block b in blocks)
                diskOut_filestream.Write(b.buffer, 0, b.count);
            blocks = null;

            diskOut = new BufferedStream(diskOut_filestream, Block.SZ);
            diskOut.Write(last.buffer, 0, last.count);
            return true;
        }

        public void close()
        {
            if (diskOut != null)
            {
                try
                {
                    diskOut.Close();
                }
                finally
                {
                    diskOut = null;
                }
            }
        }

        /**
         * Obtain the length (in bytes) of the buffer.
         * <p>
         * The length is only accurate after {@link #close()} has been invoked.
         *
         * @return total length of the buffer, in bytes.
         */
        public override long Length
        {
            get
            {
                if (onDiskFile != null)
                    return onDiskFile.Length;
                Block last = this.last();
                return ((long)blocks.Count) * Block.SZ - (Block.SZ - last.count);
            }
        }

        /**
         * Convert this buffer's contents into a contiguous byte array.
         * <p>
         * The buffer is only complete after {@link #close()} has been invoked.
         *
         * @return the complete byte array; length matches {@link #length()}.
         * @
         *             an error occurred reading from a local temporary file
         * @throws OutOfMemoryError
         *             the buffer cannot fit in memory
         */
        public byte[] ToArray()
        {
            long len = this.Length;
            if (int.MaxValue < len)
                throw new OutOfMemoryException("Length exceeds maximum array size");

            byte[] @out = new byte[(int)len];
            if (blocks != null)
            {
                int outPtr = 0;
                foreach (Block b in blocks)
                {
                    Array.Copy(b.buffer, 0, @out, outPtr, b.count);
                    outPtr += b.count;
                }
            }
            else
            {
                using (var @in = new FileStream(onDiskFile.FullName, System.IO.FileMode.Open, FileAccess.Read))
                {
                    NB.ReadFully(@in, @out, 0, (int)len);
                }
            }
            return @out;
        }

        /**
         * Send this buffer to an output stream.
         * <p>
         * This method may only be invoked after {@link #close()} has completed
         * normally, to ensure all data is completely transferred.
         *
         * @param os
         *            stream to send this buffer's complete content to.
         * @param pm
         *            if not null progress updates are sent here. Caller should
         *            initialize the task and the number of work units to
         *            <code>{@link #length()}/1024</code>.
         * @
         *             an error occurred reading from a temporary file on the local
         *             system, or writing to the output stream.
         */
        public void writeTo(Stream os, ProgressMonitor pm)
        {
            if (pm == null)
                pm = new NullProgressMonitor();
            if (blocks != null)
            {
                // Everything is in core so we can stream directly to the output.
                //
                foreach (Block b in blocks)
                {
                    os.Write(b.buffer, 0, b.count);
                    pm.Update(b.count / 1024);
                }
            }
            else
            {
                // Reopen the temporary file and copy the contents.
                //
                using (var @in = new FileStream(onDiskFile.FullName, System.IO.FileMode.Open, FileAccess.Read))
                {
                    int cnt;
                    byte[] buf = new byte[Block.SZ];
                    while ((cnt = @in.Read(buf, 0, buf.Length)) > 0)
                    {
                        os.Write(buf, 0, cnt);
                        pm.Update(cnt / 1024);
                    }
                }
            }
        }

        /** Clear this buffer so it has no data, and cannot be used again. */
        public void destroy()
        {
            blocks = null;

            if (diskOut != null)
            {
                try
                {
                    diskOut.Close();
                }
                catch (IOException err)
                {
                    // We shouldn't encounter an error closing the file.
                }
                finally
                {
                    diskOut = null;
                }
            }

            if (onDiskFile != null)
            {
                onDiskFile.Delete();
                if (onDiskFile.Exists)
                {
                    ;
                    //    onDiskFile.deleteOnExit(); // [henon] <--- hmm, how to do this?
                }
                onDiskFile = null;
            }
        }

        public class Block
        {
            public const int SZ = 8 * 1024;

            public byte[] buffer = new byte[SZ];

            public int count;

            public bool isFull()
            {
                return count == SZ;
            }
        }


        public override bool CanRead
        {
            get { throw new NotImplementedException(); }
        }

        public override bool CanSeek
        {
            get { throw new NotImplementedException(); }
        }

        public override bool CanWrite
        {
            get { throw new NotImplementedException(); }
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override long Position
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            write(buffer, offset, count);
        }
    }
}
