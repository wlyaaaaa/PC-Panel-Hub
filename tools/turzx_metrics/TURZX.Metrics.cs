using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TURZX.Metrics
{
    public static class GpuMemoryClockReader
    {
        private const uint FileMapRead = 0x0004;
        private const uint MahmSignature = 0x4D41484D;
        private const uint SourceIdMemoryClock = 0x22;
        private const int HeaderMinSize = 32;
        private const int EntryMinSize = 1324;
        private const int GpuEntryMinSize = 1304;
        private const int CacheMilliseconds = 500;

        private static readonly object CacheLock = new object();
        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static string _lastPreferred = "";
        private static string _lastValue;

        public static string ReadMhzString()
        {
            return ReadMhzString("NVIDIA");
        }

        public static string ReadMhzString(string preferredDeviceContains)
        {
            string preferred = preferredDeviceContains ?? "";

            lock (CacheLock)
            {
                if (_lastValue != null &&
                    string.Equals(_lastPreferred, preferred, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - _lastReadUtc).TotalMilliseconds < CacheMilliseconds)
                {
                    return _lastValue;
                }
            }

            string value = null;
            try
            {
                value = ReadMhzStringUncached(preferred);
            }
            catch
            {
                value = null;
            }

            lock (CacheLock)
            {
                _lastPreferred = preferred;
                _lastReadUtc = DateTime.UtcNow;
                _lastValue = value;
            }

            return value;
        }

        private static string ReadMhzStringUncached(string preferredDeviceContains)
        {
            IntPtr mapping = OpenAfterburnerMapping();
            if (mapping == IntPtr.Zero)
            {
                return null;
            }

            IntPtr view = IntPtr.Zero;
            try
            {
                view = MapViewOfFile(mapping, FileMapRead, 0, 0, UIntPtr.Zero);
                if (view == IntPtr.Zero)
                {
                    return null;
                }

                uint signature = ReadUInt32(view, 0);
                uint version = ReadUInt32(view, 4);
                uint headerSize = ReadUInt32(view, 8);
                uint entryCount = ReadUInt32(view, 12);
                uint entrySize = ReadUInt32(view, 16);
                uint gpuCount = ReadUInt32(view, 24);
                uint gpuEntrySize = ReadUInt32(view, 28);

                if (signature != MahmSignature ||
                    version < 0x00020000 ||
                    headerSize < HeaderMinSize ||
                    entrySize < EntryMinSize)
                {
                    return null;
                }

                Dictionary<uint, string> gpuDevices = ReadGpuDevices(view, headerSize, entryCount, entrySize, gpuCount, gpuEntrySize);
                Candidate best = null;

                for (uint index = 0; index < entryCount; index++)
                {
                    int offset = CheckedOffset(headerSize, index, entrySize);
                    uint sourceId = ReadUInt32(view, offset + 1320);
                    if (sourceId != SourceIdMemoryClock)
                    {
                        continue;
                    }

                    float data = ReadFloat(view, offset + 1300);
                    if (!IsUsableReading(data))
                    {
                        continue;
                    }

                    uint gpuId = ReadUInt32(view, offset + 1316);
                    string device = gpuDevices.ContainsKey(gpuId) ? gpuDevices[gpuId] : "";
                    string sourceName = ReadAnsiString(view, offset, 260);
                    string units = ReadAnsiString(view, offset + 260, 260);
                    if (units.Length > 0 && units.IndexOf("MHz", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Candidate candidate = new Candidate
                    {
                        Value = data,
                        Device = device,
                        SourceName = sourceName,
                        Score = ScoreCandidate(device, sourceName, preferredDeviceContains, data)
                    };

                    if (best == null || candidate.Score > best.Score)
                    {
                        best = candidate;
                    }
                }

                if (best == null)
                {
                    return null;
                }

                int mhz = (int)Math.Round(best.Value);
                return mhz > 0 ? mhz.ToString() : null;
            }
            finally
            {
                if (view != IntPtr.Zero)
                {
                    UnmapViewOfFile(view);
                }

                CloseHandle(mapping);
            }
        }

        private static Dictionary<uint, string> ReadGpuDevices(
            IntPtr view,
            uint headerSize,
            uint entryCount,
            uint entrySize,
            uint gpuCount,
            uint gpuEntrySize)
        {
            Dictionary<uint, string> devices = new Dictionary<uint, string>();
            if (gpuEntrySize < GpuEntryMinSize || gpuCount == 0)
            {
                return devices;
            }

            uint gpuBase = headerSize + entryCount * entrySize;
            for (uint index = 0; index < gpuCount; index++)
            {
                int offset = CheckedOffset(gpuBase, index, gpuEntrySize);
                uint id = ReadUInt32(view, offset);
                string device = ReadAnsiString(view, offset + 520, 260);
                if (!devices.ContainsKey(id))
                {
                    devices.Add(id, device);
                }
            }

            return devices;
        }

        private static bool IsUsableReading(float value)
        {
            return !float.IsNaN(value) &&
                   !float.IsInfinity(value) &&
                   value != float.MaxValue &&
                   value > 0;
        }

        private static double ScoreCandidate(string device, string sourceName, string preferredDeviceContains, float value)
        {
            double score = value;
            string preferred = preferredDeviceContains ?? "";

            if (preferred.Length > 0 && device.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 1000000;
            }

            if (device.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                device.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 500000;
            }

            if (sourceName.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 10000;
            }

            return score;
        }

        private static IntPtr OpenAfterburnerMapping()
        {
            string[] names = new[] { "MAHMSharedMemory", "Global\\MAHMSharedMemory" };
            for (int i = 0; i < names.Length; i++)
            {
                IntPtr handle = OpenFileMapping(FileMapRead, false, names[i]);
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }

        private static int CheckedOffset(uint baseOffset, uint index, uint size)
        {
            ulong value = (ulong)baseOffset + (ulong)index * (ulong)size;
            if (value > int.MaxValue)
            {
                throw new InvalidOperationException("MAHMSharedMemory offset is too large");
            }

            return (int)value;
        }

        private static uint ReadUInt32(IntPtr ptr, int offset)
        {
            byte[] bytes = new byte[4];
            Marshal.Copy(IntPtr.Add(ptr, offset), bytes, 0, bytes.Length);
            return BitConverter.ToUInt32(bytes, 0);
        }

        private static float ReadFloat(IntPtr ptr, int offset)
        {
            byte[] bytes = new byte[4];
            Marshal.Copy(IntPtr.Add(ptr, offset), bytes, 0, bytes.Length);
            return BitConverter.ToSingle(bytes, 0);
        }

        private static string ReadAnsiString(IntPtr ptr, int offset, int length)
        {
            byte[] bytes = new byte[length];
            Marshal.Copy(IntPtr.Add(ptr, offset), bytes, 0, bytes.Length);

            int end = Array.IndexOf<byte>(bytes, 0);
            if (end < 0)
            {
                end = bytes.Length;
            }

            return Encoding.Default.GetString(bytes, 0, end).Trim();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private sealed class Candidate
        {
            public float Value;
            public string Device;
            public string SourceName;
            public double Score;
        }
    }

    public static class ThemeValuePatch
    {
        public static object PatchReturnedMData(object data, string dataName)
        {
            if (data == null)
            {
                return data;
            }

            string value = null;
            if (string.Equals(dataName, "GPUFAN", StringComparison.OrdinalIgnoreCase))
            {
                value = GpuMemoryClockReader.ReadMhzString("NVIDIA");
            }
            else if (string.Equals(dataName, "DRVLOAD", StringComparison.OrdinalIgnoreCase))
            {
                value = DriveSummaryReader.ReadSummary();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return data;
            }

            try
            {
                SetPropertyValue(data, "Value", value);
                SetPropertyValue(data, "ShowUnit", false);
            }
            catch
            {
            }

            return data;
        }

        public static void ApplyGpuMemoryClockToTheme(object theme)
        {
            if (theme == null)
            {
                return;
            }

            string value = GpuMemoryClockReader.ReadMhzString("NVIDIA");
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            try
            {
                object graphList = GetPropertyValue(theme, "GraphList");
                IEnumerable items = graphList as IEnumerable;
                if (items == null)
                {
                    return;
                }

                foreach (object graphItem in items)
                {
                    object data = GetPropertyValue(graphItem, "m_data");
                    if (data == null)
                    {
                        continue;
                    }

                    string dataName = Convert.ToString(GetPropertyValue(data, "DataName"));
                    if (!string.Equals(dataName, "GPUFAN", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SetPropertyValue(data, "Value", value);
                    SetPropertyValue(data, "ShowUnit", false);
                }
            }
            catch
            {
            }
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static void SetPropertyValue(object instance, string propertyName, object value)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value, null);
            }
        }
    }

    public static class DriveSummaryReader
    {
        private const int CacheMilliseconds = 1000;
        private static readonly object CacheLock = new object();
        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static string _lastSummary;

        public static string ReadSummary()
        {
            lock (CacheLock)
            {
                if (_lastSummary != null &&
                    (DateTime.UtcNow - _lastReadUtc).TotalMilliseconds < CacheMilliseconds)
                {
                    return _lastSummary;
                }
            }

            string summary;
            try
            {
                summary = ReadSummaryUncached();
            }
            catch
            {
                summary = null;
            }

            lock (CacheLock)
            {
                _lastReadUtc = DateTime.UtcNow;
                _lastSummary = summary;
            }

            return summary;
        }

        private static string ReadSummaryUncached()
        {
            List<string> parts = new List<string>();
            DriveInfo[] drives = DriveInfo.GetDrives();
            Array.Sort(drives, CompareDriveLetters);

            for (int i = 0; i < drives.Length; i++)
            {
                DriveInfo drive = drives[i];
                if (!drive.IsReady ||
                    (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable))
                {
                    continue;
                }

                long total = drive.TotalSize;
                if (total <= 0)
                {
                    continue;
                }

                long used = total - drive.AvailableFreeSpace;
                int percent = (int)Math.Round(used * 100.0 / total);
                if (percent < 0)
                {
                    percent = 0;
                }
                else if (percent > 100)
                {
                    percent = 100;
                }

                string letter = drive.Name.Substring(0, 1).ToUpperInvariant();
                parts.Add(letter + percent.ToString() + "%");
            }

            if (parts.Count == 0)
            {
                return null;
            }

            string summary = string.Join(" ", parts.ToArray());
            if (summary.Length <= 48)
            {
                return summary;
            }

            while (parts.Count > 1 && string.Join(" ", parts.ToArray()).Length > 46)
            {
                parts.RemoveAt(parts.Count - 1);
            }

            return string.Join(" ", parts.ToArray()) + " +";
        }

        private static int CompareDriveLetters(DriveInfo left, DriveInfo right)
        {
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
