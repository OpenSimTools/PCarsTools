﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Buffers;

using Syroot.BinaryData;
using Syroot.BinaryData.Memory;

using PCarsTools.Encryption;
using PCarsTools.Config;

namespace PCarsTools
{
    public class BPakFile
    {
        public const string TagId = "PAK ";

        public BVersion Version { get; set; }
        public string Name { get; set; }
        public bool BigEndian { get; set; }
        public eEncryptionType EncryptionType { get; set; }

        public List<BPakFileTocEntry> Entries { get; set; }
        public List<BExtendedFileInfoEntry> ExtEntries { get; set; }

        public int KeyIndex { get; set; }

        private FileStream _fs;

        private string _path;

        public static BPakFile FromFile(string inputFile)
        {
            var fs = new FileStream(inputFile, FileMode.Open);
            var pak = FromStream(fs, inputFile);
            pak._fs = fs;

            return pak;
        }

        public static BPakFile FromStream(Stream stream, string filename = null)
        {
            var pak = new BPakFile();
            int pakOffset = (int)stream.Position;
            pak._path = filename.ToLower().Replace('/', '\\');

            using var bs = new BinaryStream(stream, leaveOpen: true);
            int mID = bs.ReadInt32();
            pak.Version = new BVersion(bs.ReadUInt32());
            int fileCount = bs.ReadInt32();
            bs.Position += 12;
            pak.Name = bs.ReadString(0x100).TrimEnd('\0');

            pak.KeyIndex = BConfig.Instance.GetPatternIdx(pak.Name);
            if (pak.KeyIndex == 0 && !string.IsNullOrEmpty(pak._path)) // Default key found, try to see if its in the path
            {
                foreach (var filter in BConfig.Instance.PatternFilters)
                {
                    foreach (var rule in filter.PatternRules)
                    {
                        if (pak._path.Contains(rule.PatternDecrypted))
                        {
                            pak.KeyIndex = filter.Index;
                            goto found;
                        }
                    }
                }
            found:
                ;
            }

            uint pakFileTocEntrySize = bs.ReadUInt32();
            uint crc = bs.ReadUInt32();
            uint extInfoSize = bs.ReadUInt32();
            bs.Position += 8;
            pak.BigEndian = bs.ReadBoolean(BooleanCoding.Byte);
            pak.EncryptionType = (eEncryptionType)bs.Read1Byte();
            bs.Position += 2;

            var pakTocBuffer = bs.ReadBytes((int)pakFileTocEntrySize);

            if (pak.EncryptionType != eEncryptionType.None)
            {
                if (pak.KeyIndex == 0) // Still not found? Attempt bruteforce
                {
                    int j = 0;
                    for (int i = 0; i < 32; i++)
                    {
                        var tmpData = pakTocBuffer.ToArray();
                        BPakFileEncryption.DecryptData(pak.EncryptionType, tmpData, tmpData.Length, i);
                        if (tmpData[14] == 0 && tmpData[15] == 0)
                        {
                            pak.KeyIndex = i;
                            break;
                        }
                    }
                }

                BPakFileEncryption.DecryptData(pak.EncryptionType, pakTocBuffer, pakTocBuffer.Length, pak.KeyIndex);
            }

            if (pakTocBuffer[14] != 0 && pakTocBuffer[15] != 0) // Check if first entry offset is absurdly too big that its possibly not decrypted correctly
                Console.WriteLine($"Warning - possible crash: {pak.Name} toc could most likely not be decrypted correctly using key No.{pak.KeyIndex}");

            pak.Entries = new List<BPakFileTocEntry>(fileCount);
            SpanReader sr = new SpanReader(pakTocBuffer);
            for (int i = 0; i < fileCount; i++)
            {
                sr.Position = (i * 0x2A);
                var pakFileToCEntry = new BPakFileTocEntry();
                pakFileToCEntry.UId = sr.ReadUInt64();
                pakFileToCEntry.Offset = sr.ReadUInt64();
                pakFileToCEntry.PakSize = sr.ReadUInt32();
                pakFileToCEntry.FileSize = sr.ReadUInt32();
                pakFileToCEntry.TimeStamp = sr.ReadUInt64();
                pakFileToCEntry.Compression = (PakFileCompressionType)sr.ReadByte();
                pakFileToCEntry.UnkFlag = sr.ReadByte();
                pakFileToCEntry.CRC = sr.ReadUInt32();
                pakFileToCEntry.Extension = sr.ReadStringRaw(4).ToCharArray();
                pak.Entries.Add(pakFileToCEntry);
            }

            const int unkCertXmlSize = 0x308;
            bs.Position += unkCertXmlSize;

            int baseExtOffset = (int)bs.Position - pakOffset;
            int extInfoEntriesSize = (int)Utils.AlignValue(extInfoSize - unkCertXmlSize, 0x10);
            var extTocBuffer = bs.ReadBytes(extInfoEntriesSize);

            bool returnBuffer = false;
            if (pak.EncryptionType != eEncryptionType.None)
            {
                var scribeDecrypt = new ScribeDecrypt();
                scribeDecrypt.CreateSchedule();

                if (extTocBuffer.Length % 0x10 != 0) // Must be aligned to 0x10
                {
                    int rem = extTocBuffer.Length % 0x10;
                    byte[] extTocBufferAligned = ArrayPool<byte>.Shared.Rent(extTocBuffer.Length + rem);
                    extTocBuffer.AsSpan().CopyTo(extTocBufferAligned);
                    extTocBuffer = extTocBufferAligned;
                    returnBuffer = true;
                }

                var d = MemoryMarshal.Cast<byte, uint>(extTocBuffer);
                scribeDecrypt.Decrypt(d);
            }

            pak.ExtEntries = new List<BExtendedFileInfoEntry>(fileCount);
            sr = new SpanReader(extTocBuffer);
            for (int i = 0; i < fileCount; i++)
            {
                sr.Position = i * 0x10;

                var extEntry = new BExtendedFileInfoEntry();
                extEntry.Offset = sr.ReadInt64();
                extEntry.TimeStamp = sr.ReadInt64();

                sr.Position = (int)extEntry.Offset - baseExtOffset;
                extEntry.Path = sr.ReadString1();

                pak.ExtEntries.Add(extEntry);

                if (extEntry.Path.Contains(@"Properties\GUI\FontsMetadata.bin", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = File.ReadAllBytes(@"C:\Users\nenkai\Desktop\Hydra\64bit\Properties\GUI\FontsMetadata.bin");
                    BPakFileEncryption.DecryptData(pak.EncryptionType, bytes, bytes.Length, pak.KeyIndex);
                }
            }

            if (returnBuffer)
                ArrayPool<byte>.Shared.Return(extTocBuffer);

            return pak;
        }

        public void UnpackAll(string outputDir)
        {
            int totalCount = 0;
            int failed = 0;

            for (int i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                var extEntry = ExtEntries[i];

                string outPath = Path.Combine(outputDir, extEntry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                if (UnpackFromStream(entry, extEntry, outPath))
                {
                    Console.WriteLine($"Unpacked: [{Name}]\\{extEntry.Path}");
                    totalCount++;
                }
                else
                {
                    Console.WriteLine($"Failed to unpack: {extEntry.Path}");
                    failed++;
                }
            }

            Console.WriteLine($"Done. Extracted {totalCount} files ({failed} not extracted)");
        }

        public bool UnpackFromLocalFile(string outputDir, BPakFileTocEntry entry, BExtendedFileInfoEntry extEntry)
        {
            if (_fs is not null)
                throw new InvalidOperationException("Can't extract from local file when the pak is an actual file with data");

            string localPath = Path.Combine(outputDir, extEntry.Path);
            if (File.Exists(localPath))
            {
                return UnpackFromFile(entry, extEntry, localPath, localPath + ".dec");
            }
            else
            {
                Console.WriteLine($"File {extEntry.Path} not found to extract, can be ignored");
            }

            return false;
        }

        public bool UnpackFromStream(BPakFileTocEntry entry, BExtendedFileInfoEntry extEntry, string output)
        {
            if (_fs is null)
                throw new InvalidOperationException("Can't extract from stream from a toc file based pak");

            _fs.Position = (long)entry.Offset;

            var bytes = ArrayPool<byte>.Shared.Rent((int)entry.PakSize);
            _fs.Read(bytes);

            bool result = Unpack(bytes, entry, extEntry, output);
            ArrayPool<byte>.Shared.Return(bytes);
            return result;
        }

        private bool UnpackFromFile(BPakFileTocEntry entry, BExtendedFileInfoEntry extEntry, string inputFile, string output)
        {
            var bytes = File.ReadAllBytes(inputFile);
            return Unpack(bytes, entry, extEntry, output);
        }

        private bool Unpack(byte[] bytes, BPakFileTocEntry entry, BExtendedFileInfoEntry extEntry, string output)
        {
            if (this.EncryptionType != eEncryptionType.None)
                BPakFileEncryption.DecryptData(this.EncryptionType, bytes, bytes.Length, this.KeyIndex);

            if (entry.Compression == PakFileCompressionType.Mermaid || entry.Compression == PakFileCompressionType.Kraken)
            {
                byte[] dec = ArrayPool<byte>.Shared.Rent((int)entry.FileSize);
                bool res = Oodle.Decompress(bytes, dec, entry.FileSize);// Implement this
                if (res)
                    File.WriteAllBytes(output, dec);

                ArrayPool<byte>.Shared.Return(dec);
                return res;
            }
            else if (entry.Compression == PakFileCompressionType.ZLib)
            {
                byte[] dec = ArrayPool<byte>.Shared.Rent((int)entry.FileSize);
                using var ms = new MemoryStream(bytes);
                using var uncompStream = new DeflateStream(ms, CompressionMode.Decompress);
                int len = uncompStream.Read(dec);

                if (len == entry.FileSize)
                    File.WriteAllBytes(output, dec);

                ArrayPool<byte>.Shared.Return(dec);
                return len == entry.FileSize;
            }
            else if (entry.Compression != PakFileCompressionType.None)
            {
                Console.WriteLine($"Warning: Unrecognized compression type {entry.Compression} for {extEntry.Path}");
                return false;
            }
            else
            {
                // No compression
                File.WriteAllBytes(output, bytes);
                return true;
            }

            return false;
        }
    }
}
