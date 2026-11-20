using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CloudpunkVR.Patcher
{
    /// <summary>
    /// Adds "OpenVR" to <c>BuildSettings.enabledVRDevices</c> in <c>globalgamemanagers</c> (format 17, Unity 2018.4),
    /// like the editor's "Virtual Reality Supported" box. Other objects are copied as is, realigned to 8 bytes.
    /// </summary>
    public static class GlobalGameManagersPatch
    {
        private const int BuildSettingsClassId = 141;

        public sealed class Result
        {
            public bool AlreadyPatched;
            public byte[] Patched;
            public List<string> DevicesBefore = new List<string>();
        }

        private sealed class ObjectEntry
        {
            public long PathId;
            public long ByteStartFieldPos; // where byteStart is stored in the metadata
            public uint ByteStart;         // relative to dataOffset
            public uint ByteSize;
            public int ClassId;
        }

        /// <summary>Reads the VR device list without changing anything.</summary>
        public static List<string> ReadDevices(byte[] file)
        {
            Parse(file, out _, out var objects, out uint dataOffset);
            var bs = objects.Single(o => o.ClassId == BuildSettingsClassId);
            int pos = (int)(dataOffset + bs.ByteStart);
            SkipStringVector(file, ref pos); // scenes
            SkipStringVector(file, ref pos); // preloadedPlugins
            return ReadStringVector(file, ref pos);
        }

        public static Result AddDevice(byte[] file, string device)
        {
            Parse(file, out _, out var objects, out uint dataOffset);
            var bs = objects.SingleOrDefault(o => o.ClassId == BuildSettingsClassId)
                     ?? throw new InvalidDataException("BuildSettings object not found");

            int objStart = (int)(dataOffset + bs.ByteStart);
            int pos = objStart;
            SkipStringVector(file, ref pos); // scenes
            SkipStringVector(file, ref pos); // preloadedPlugins
            int vrListStart = pos;
            var devices = ReadStringVector(file, ref pos);
            int vrListEnd = pos;

            var result = new Result { DevicesBefore = devices };
            if (devices.Contains(device))
            {
                result.AlreadyPatched = true;
                return result;
            }

            // first in the list = loaded by default
            var newList = new List<string> { device };
            newList.AddRange(devices.Where(d => d != "None"));
            byte[] newVector = WriteStringVector(newList);

            byte[] oldObj = new byte[bs.ByteSize];
            Buffer.BlockCopy(file, objStart, oldObj, 0, (int)bs.ByteSize);
            int relListStart = vrListStart - objStart;
            int relListEnd = vrListEnd - objStart;
            byte[] newObj = new byte[oldObj.Length - (relListEnd - relListStart) + newVector.Length];
            Buffer.BlockCopy(oldObj, 0, newObj, 0, relListStart);
            Buffer.BlockCopy(newVector, 0, newObj, relListStart, newVector.Length);
            Buffer.BlockCopy(oldObj, relListEnd, newObj, relListStart + newVector.Length, oldObj.Length - relListEnd);

            // rebuild the data section in order, 8-byte aligned
            var ordered = objects.OrderBy(o => o.ByteStart).ToList();
            using var data = new MemoryStream();
            var newStarts = new Dictionary<ObjectEntry, (uint start, uint size)>();
            uint firstStart = ordered[0].ByteStart;
            data.Write(file, (int)dataOffset, (int)firstStart); // bytes before the first object (normally none)
            foreach (var o in ordered)
            {
                while (data.Length % 8 != 0) data.WriteByte(0);
                uint start = (uint)data.Length;
                byte[] bytes = o == bs ? newObj : Slice(file, (int)(dataOffset + o.ByteStart), (int)o.ByteSize);
                data.Write(bytes, 0, bytes.Length);
                newStarts[o] = (start, (uint)bytes.Length);
            }

            byte[] output = new byte[dataOffset + data.Length];
            Buffer.BlockCopy(file, 0, output, 0, (int)dataOffset);
            Buffer.BlockCopy(data.ToArray(), 0, output, (int)dataOffset, (int)data.Length);
            foreach (var kv in newStarts)
            {
                WriteU32LE(output, (int)kv.Key.ByteStartFieldPos, kv.Value.start);
                WriteU32LE(output, (int)kv.Key.ByteStartFieldPos + 4, kv.Value.size);
            }
            WriteU32BE(output, 4, (uint)output.Length); // header fileSize

            var check = ReadDevices(output);
            if (!check.Contains(device)) throw new InvalidDataException("Patch verification failed");

            result.Patched = output;
            return result;
        }

        private static void Parse(byte[] f, out uint version, out List<ObjectEntry> objects, out uint dataOffset)
        {
            version = ReadU32BE(f, 8);
            dataOffset = ReadU32BE(f, 12);
            if (version != 17)
                throw new NotSupportedException($"globalgamemanagers format {version} is not supported (expected 17, Unity 2018.4)");
            if (f[16] != 0) throw new NotSupportedException("Big-endian serialized file");
            if (ReadU32BE(f, 4) != f.Length) throw new InvalidDataException("File size in header does not match");

            int p = 20;
            ReadCString(f, ref p);              // unity version
            p += 4;                             // target platform
            bool typeTree = f[p++] != 0;
            int typeCount = ReadI32(f, ref p);
            var classIds = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                int classId = ReadI32(f, ref p);
                p += 1;                         // isStrippedType
                p += 2;                         // scriptTypeIndex
                if (classId == 114) p += 16;    // script id
                p += 16;                        // old type hash
                if (typeTree)
                {
                    int nodes = ReadI32(f, ref p);
                    int strings = ReadI32(f, ref p);
                    p += nodes * 24 + strings;
                }
                classIds[i] = classId;
            }

            int count = ReadI32(f, ref p);
            objects = new List<ObjectEntry>(count);
            for (int i = 0; i < count; i++)
            {
                p = (p + 3) & ~3;
                var o = new ObjectEntry();
                o.PathId = BitConverter.ToInt64(f, p); p += 8;
                o.ByteStartFieldPos = p;
                o.ByteStart = BitConverter.ToUInt32(f, p); p += 4;
                o.ByteSize = BitConverter.ToUInt32(f, p); p += 4;
                int typeIndex = ReadI32(f, ref p);
                o.ClassId = classIds[typeIndex];
                objects.Add(o);
            }
            if (p > dataOffset) throw new InvalidDataException("Metadata overlaps data");
        }

        private static List<string> ReadStringVector(byte[] f, ref int p)
        {
            int n = ReadI32(f, ref p);
            if (n < 0 || n > 100000) throw new InvalidDataException("Bad vector size");
            var list = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                int len = ReadI32(f, ref p);
                if (len < 0 || p + len > f.Length) throw new InvalidDataException("Bad string length");
                list.Add(Encoding.UTF8.GetString(f, p, len));
                p += len;
                p = (p + 3) & ~3;
            }
            return list;
        }

        private static void SkipStringVector(byte[] f, ref int p) => ReadStringVector(f, ref p);

        private static byte[] WriteStringVector(List<string> list)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(list.Count);
            foreach (var s in list)
            {
                var b = Encoding.UTF8.GetBytes(s);
                w.Write(b.Length);
                w.Write(b);
                while (ms.Length % 4 != 0) w.Write((byte)0);
            }
            w.Flush();
            return ms.ToArray();
        }

        private static byte[] Slice(byte[] f, int start, int len)
        {
            var b = new byte[len];
            Buffer.BlockCopy(f, start, b, 0, len);
            return b;
        }

        private static string ReadCString(byte[] f, ref int p)
        {
            int s = p;
            while (f[p] != 0) p++;
            var str = Encoding.ASCII.GetString(f, s, p - s);
            p++;
            return str;
        }

        private static int ReadI32(byte[] f, ref int p) { int v = BitConverter.ToInt32(f, p); p += 4; return v; }
        private static uint ReadU32BE(byte[] f, int p) => (uint)(f[p] << 24 | f[p + 1] << 16 | f[p + 2] << 8 | f[p + 3]);
        private static void WriteU32BE(byte[] f, int p, uint v) { f[p] = (byte)(v >> 24); f[p + 1] = (byte)(v >> 16); f[p + 2] = (byte)(v >> 8); f[p + 3] = (byte)v; }
        private static void WriteU32LE(byte[] f, int p, uint v) { var b = BitConverter.GetBytes(v); Buffer.BlockCopy(b, 0, f, p, 4); }
    }
}
