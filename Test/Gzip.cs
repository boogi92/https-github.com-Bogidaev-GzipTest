using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Test.Extension;
using Test.Model;

namespace Test
{
    /// <summary>
    /// Архиватор
    /// </summary>
    public class Gzip
    {  
        /// <summary>
        /// Блокировка потоков
        /// </summary>
        private ManualResetEvent _recordingSignal;

        /// <summary>
        /// Блокировка основного потока
        /// </summary>
        private AutoResetEvent _waitingSignal;

        /// <summary>
        /// Мод для работы
        /// </summary>
        private CompressionMode Mode { get; }

        /// <summary>
        /// Менеджер потоков 
        /// </summary>
        private Queue Queue { get; }

        /// <summary>
        /// Размер блока
        /// </summary>
        private int _sizeBlock = 1024 * 1024 * 25;

        /// <summary>
        /// Размер шапки
        /// </summary>
        private int header = sizeof(int);

        /// <summary>
        /// Объект для блокировки чтения 
        /// </summary>
        private static readonly object ReadLocker = new object();

        /// <summary>
        /// Объект для блокировки записи 
        /// </summary>
        private static readonly object WriteLocker = new object();

        public Gzip(CompressionMode mode)
        {
            this.Mode = mode;
            this.Queue = new Queue();
        }

        /// <summary>
        /// Выполнять
        /// </summary>
        /// <param name="readingStream">Поток чтения</param>
        /// <param name="writeStream">Поток записи</param>
        public void Execute(Stream readingStream, Stream writeStream)
        {
            switch (Mode)
            {
                case CompressionMode.Compress:
                    Compress(readingStream, writeStream);
                    break;

                case CompressionMode.Decompress:
                    Decompress(readingStream, writeStream);
                    break;
            }
        }

        private void Compress(Stream readingStream, Stream writeStream)
        {
            var list = new List<Block>();
            var blocksCount = (int) (readingStream.Length / _sizeBlock + (readingStream.Length % _sizeBlock > 0 ? 1 : 0));

            readingStream.Seek(0, SeekOrigin.Begin);
            writeStream.Seek(0, SeekOrigin.Begin);

            var destBlockIndex = 0;
            _waitingSignal = new AutoResetEvent(false);
            _recordingSignal = new ManualResetEvent(false);

            for (int i = 0; i < blocksCount; i++)
            {
                long number = i;
                Queue.QueueTask(() =>
                {
                    CompressThread(readingStream, writeStream, number, blocksCount, ref destBlockIndex);
                });
            }

            _waitingSignal.WaitOne();

            //var binaryWriter = new BinaryWriter(targetStream);

            //foreach (var item in list)
            //{
            //    binaryWriter.Write((int)item.Number);
            //}
            //binaryWriter.Write(list.Count);

            //destBlockIndex = 0;
        }

        private void Decompress(Stream readingStream, Stream writeStream)
        {
            var blockList = new List<Block>();
            _waitingSignal = new AutoResetEvent(false);
            _recordingSignal = new ManualResetEvent(false);
            var destBlockIndex = 0;
            var binaryReader = new BinaryReader(readingStream);
            var number = 0;
            var header = sizeof(int);


            while (readingStream.Position < readingStream.Length)
            {
                var blockSize = binaryReader.ReadInt32();
                readingStream.Seek(blockSize, SeekOrigin.Current);

                blockList.Add(new Block
                {
                    Number = number,
                    Size = blockSize + header
                });
                number++;
            }


            readingStream.Seek(0, SeekOrigin.Begin);

            foreach (var block in blockList)
            {
                Queue.QueueTask(() =>
                {
                    this.DecompressThread(readingStream, writeStream, block, blockList, ref destBlockIndex);
                });
            }

            this._waitingSignal.WaitOne();
        }

        private byte[] Compression(byte[] date, int length)
        {
            using (var result = new MemoryStream())
            {
                using (var compressionStream = new GZipStream(result, Mode))
                {
                    compressionStream.Write(date, 0, length);
                }

                return result.ToArray();
            }
        }

        private byte[] DecompressBuffer(byte[] from, int length)
        {
            using (var source = new MemoryStream(from, 0, length))
            {
                using (var dest = new MemoryStream())
                {
                    using (var compressionStream = new GZipStream(source, CompressionMode.Decompress))
                    {
                        compressionStream.CopyTo(dest);
                        return dest.ToArray();
                    }
                }
            }
        }

        private void WriteProgress(int number, int count)
        {
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write($"Завершено: {100 * number / count}%");
        }

        private void DecompressThread(Stream readingStream, Stream writeStream, Block block, List<Block> blockList, ref int destBlockIndex)
        {
            var buffer = new byte[10];
            Array.Resize(ref buffer, block.Size);
            int readBlockLength;
            lock (ReadLocker)
            {
                readingStream.Seek(blockList.Where(x => x.Number < block.Number).Sum(x => (long)x.Size) + header,
                    SeekOrigin.Begin);
                readBlockLength = readingStream.Read(buffer, 0, block.Size);
            }

            var arr = DecompressBuffer(buffer, readBlockLength);

            while (destBlockIndex != block.Number)
            {
                _recordingSignal.WaitOne();
                _recordingSignal.Reset();
            }

            lock (WriteLocker)
            {
                writeStream.Write(arr, 0, arr.Length);

                if (++destBlockIndex == blockList.Count)
                {
                    _waitingSignal.Set();
                }

                _recordingSignal.Set();
                WriteProgress(destBlockIndex, blockList.Count);
            }
        }

        private void CompressThread(Stream readingStream, Stream writeStream, long number, int blocksCount, ref int destBlockIndex)
        {
            var buffer = new byte[_sizeBlock];
            int bytesRead;

            lock (ReadLocker)
            {
                readingStream.Seek(_sizeBlock * number, SeekOrigin.Begin);
                bytesRead = readingStream.Read(buffer, 0, _sizeBlock);
            }

            var siz = Compression(buffer, bytesRead);

            while (destBlockIndex != number)
            {
                _recordingSignal.WaitOne();
                _recordingSignal.Reset();
            }

            lock (WriteLocker)
            {
                var binaryWriter = new BinaryWriter(writeStream);
                binaryWriter.Write(siz.Length);
                writeStream.Write(siz, 0, siz.Length);
                //list.Add(new Block{ Number = number, Size = siz.Length });

                if (++destBlockIndex == blocksCount)
                {
                    _waitingSignal.Set();
                }

                _recordingSignal.Set();
                WriteProgress(destBlockIndex, blocksCount);
            }

        }
    }
}