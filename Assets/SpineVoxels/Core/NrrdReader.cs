using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace SpineVoxels.Core
{
    public static class NrrdReader
    {
        public static NrrdHeader ReadHeader(string path)
        {
            var header = new NrrdHeader();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            // Use a StreamReader that does NOT close the underlying stream,
            // so we can recover the byte offset after parsing.
            using var reader = new StreamReader(fs, Encoding.ASCII, false, 4096, leaveOpen: true);

            string firstLine = reader.ReadLine();
            if (firstLine == null || !firstLine.StartsWith("NRRD"))
                throw new Exception($"Not a valid NRRD file (missing NRRD magic): {path}");

            while (true)
            {
                string line = reader.ReadLine();
                if (line == null) break;        // EOF
                if (line.Length == 0) break;    // blank line = end of header
                if (line[0] == '#') continue;   // comment

                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string key   = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).TrimStart('=').Trim();

                ParseField(header, key, value);
            }

            // After the StreamReader buffers ahead, the underlying stream position
            // may be ahead of what we actually consumed. We re-open to find the real
            // data offset by scanning for the blank line in raw bytes.
            header.DataByteOffset = FindDataOffset(path);
            return header;
        }

        public static short[] ReadData(string path, NrrdHeader header)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            fs.Seek(header.DataByteOffset, SeekOrigin.Begin);

            int count = header.VoxelCount;
            byte[] raw;

            string enc = (header.Encoding ?? "raw").ToLowerInvariant();
            if (enc == "gzip" || enc == "gz")
            {
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                raw = ReadAllBytes(gz);
            }
            else if (enc == "raw")
            {
                raw = new byte[count * 2];
                ReadExact(fs, raw);
            }
            else
            {
                throw new NotSupportedException($"NRRD encoding not supported: '{header.Encoding}'");
            }

            if (raw.Length != count * 2)
                throw new Exception(
                    $"NRRD data length mismatch: expected {count * 2} bytes, got {raw.Length}. " +
                    $"Volume: {header.Sizes[0]}x{header.Sizes[1]}x{header.Sizes[2]}");

            return DecodeShorts(raw, count, header.LittleEndian);
        }

        public static (NrrdHeader header, short[] data) Read(string path)
        {
            var header = ReadHeader(path);
            var data   = ReadData(path, header);
            return (header, data);
        }

        // ── Internals ────────────────────────────────────────────────────────

        static void ParseField(NrrdHeader h, string key, string value)
        {
            switch (key)
            {
                case "type":
                    h.Type = value;
                    break;
                case "sizes":
                    var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    h.Sizes = Array.ConvertAll(parts, int.Parse);
                    break;
                case "space directions":
                    h.SpaceDirections = ParseSpaceDirections(value);
                    break;
                case "space origin":
                    h.SpaceOrigin = ParseTuple(value);
                    break;
                case "endian":
                    h.LittleEndian = value.Trim().ToLowerInvariant() == "little";
                    break;
                case "encoding":
                    h.Encoding = value.Trim().ToLowerInvariant();
                    break;
                // All other fields (dimension, space, kinds, …) are intentionally ignored.
            }
        }

        // Scans the raw bytes for the first "\n\n" (or "\r\n\r\n") and returns
        // the byte offset immediately after it.
        static long FindDataOffset(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            int prev = -1;
            long pos = 0;
            int b;
            while ((b = fs.ReadByte()) >= 0)
            {
                pos++;
                if (b == '\n' && prev == '\n')
                    return pos;   // byte right after "\n\n"
                // Handle "\r\n\r\n": we track the last non-\r byte
                if (b != '\r') prev = b;
            }
            throw new Exception("NRRD header not terminated by a blank line.");
        }

        static Vector3[] ParseSpaceDirections(string value)
        {
            // Format: "(a,b,c) (d,e,f) (g,h,i)"
            var result = new Vector3[3];
            int idx = 0, start = 0;
            while (idx < 3 && start < value.Length)
            {
                int open  = value.IndexOf('(', start);
                int close = value.IndexOf(')', open + 1);
                if (open < 0 || close < 0) break;
                result[idx++] = ParseTuple(value.Substring(open, close - open + 1));
                start = close + 1;
            }
            return result;
        }

        static Vector3 ParseTuple(string s)
        {
            s = s.Trim().Trim('(', ')');
            var p = s.Split(',');
            return new Vector3(
                float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
        }

        static short[] DecodeShorts(byte[] raw, int count, bool isLittleEndian)
        {
            bool needSwap = isLittleEndian != BitConverter.IsLittleEndian;
            var result = new short[count];
            for (int i = 0; i < count; i++)
            {
                int b0 = raw[i * 2];
                int b1 = raw[i * 2 + 1];
                result[i] = needSwap
                    ? (short)(b1 | (b0 << 8))
                    : (short)(b0 | (b1 << 8));
            }
            return result;
        }

        static byte[] ReadAllBytes(Stream stream)
        {
            using var ms = new MemoryStream();
            byte[] buf = new byte[81920];
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                ms.Write(buf, 0, n);
            return ms.ToArray();
        }

        static void ReadExact(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = stream.Read(buffer, offset, buffer.Length - offset);
                if (n == 0) break;
                offset += n;
            }
        }
    }
}
