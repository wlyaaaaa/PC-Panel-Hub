using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace TURZX.SideScreen
{
    public enum TurzxSourcePixelOrder
    {
        Rgb = 0,
        Bgr = 1
    }

    public static class TurzxSideScreenProtocol
    {
        public const int Width = 480;
        public const int Height = 1920;
        public const int BytesPerPixel = 4;
        public const int FullFrameByteCount = Width * Height * BytesPerPixel;
        public const int RawFrameChunkByteCount = 24900;
        public const int CommandPacketByteCount = 250;
        public const int CommandPacketPayloadOffset = 10;
        public const int MaxCommandPacketPayloadByteCount = CommandPacketByteCount - CommandPacketPayloadOffset;

        public const byte FullFrameCommand = 200;
        public const byte AlternateFullFrameCommand = 202;
        public const byte DifferentialFrameCommand = 204;
        public const byte BrightnessCommand = 123;

        private const byte CommandMagicHigh = 0xEF;
        private const byte CommandMagicLow = 0x69;
        private const int DefaultBaudRate = 115200;
        private const int DefaultDataBits = 8;
        private const int DefaultTimeoutMilliseconds = 5000;
        private const int DeviceInterChunkDelayMilliseconds = 1;

        public static byte[] EncodeBitmap(Bitmap bitmap)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            ValidateDimensions(bitmap.Width, bitmap.Height);

            using (Bitmap normalized = bitmap.Clone(
                new Rectangle(0, 0, Width, Height),
                PixelFormat.Format32bppArgb))
            {
                return EncodeFormat32BgraBitmap(normalized);
            }
        }

        public static byte[] EncodeRaw24(byte[] pixels, TurzxSourcePixelOrder sourceOrder)
        {
            if (pixels == null)
            {
                throw new ArgumentNullException("pixels");
            }

            int expectedLength = Width * Height * 3;
            if (pixels.Length != expectedLength)
            {
                throw new ArgumentException(
                    "Raw 24-bit source must be exactly " + expectedLength + " bytes for 480x1920.",
                    "pixels");
            }

            byte[] frame = new byte[FullFrameByteCount];
            int sourceOffset = 0;
            int targetOffset = 0;
            for (int i = 0; i < Width * Height; i++)
            {
                byte r;
                byte g;
                byte b;
                if (sourceOrder == TurzxSourcePixelOrder.Rgb)
                {
                    r = pixels[sourceOffset];
                    g = pixels[sourceOffset + 1];
                    b = pixels[sourceOffset + 2];
                }
                else if (sourceOrder == TurzxSourcePixelOrder.Bgr)
                {
                    b = pixels[sourceOffset];
                    g = pixels[sourceOffset + 1];
                    r = pixels[sourceOffset + 2];
                }
                else
                {
                    throw new ArgumentOutOfRangeException("sourceOrder");
                }

                frame[targetOffset] = b;
                frame[targetOffset + 1] = g;
                frame[targetOffset + 2] = r;
                frame[targetOffset + 3] = 0xFF;
                sourceOffset += 3;
                targetOffset += BytesPerPixel;
            }

            return frame;
        }

        public static byte[] BuildCommandPacket(byte command, int declaredLength, byte[] payload, byte extra)
        {
            if (declaredLength < 0)
            {
                throw new ArgumentOutOfRangeException("declaredLength", "Declared length cannot be negative.");
            }

            if (payload != null && payload.Length > MaxCommandPacketPayloadByteCount)
            {
                throw new ArgumentException(
                    "Command packet payload cannot exceed " + MaxCommandPacketPayloadByteCount + " bytes.",
                    "payload");
            }

            byte[] packet = new byte[CommandPacketByteCount];
            packet[0] = command;
            packet[1] = CommandMagicHigh;
            packet[2] = CommandMagicLow;
            packet[3] = (byte)((declaredLength & unchecked((int)0xFF000000)) >> 24);
            packet[4] = (byte)((declaredLength & 0x00FF0000) >> 16);
            packet[5] = (byte)((declaredLength & 0x0000FF00) >> 8);
            packet[6] = (byte)(declaredLength & 0x000000FF);
            packet[7] = extra;

            if (payload != null && payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, packet, CommandPacketPayloadOffset, payload.Length);
            }

            return packet;
        }

        public static byte[] BuildBrightnessPacket(byte brightness)
        {
            return BuildCommandPacket(
                BrightnessCommand,
                1,
                new byte[] { brightness },
                0);
        }

        public static void WriteBrightness(Stream stream, byte brightness)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            byte[] packet = BuildBrightnessPacket(brightness);
            stream.Write(packet, 0, packet.Length);
            stream.Flush();
        }

        public static void WriteFullFrame(Stream stream, byte[] frame, bool alternateFrame)
        {
            WriteFullFrame(stream, frame, alternateFrame, 0);
        }

        public static void WriteFullFrame(
            Stream stream,
            byte[] frame,
            bool alternateFrame,
            int interChunkDelayMilliseconds)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            ValidateFullFrame(frame);
            if (interChunkDelayMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException("interChunkDelayMilliseconds");
            }

            byte command = alternateFrame ? AlternateFullFrameCommand : FullFrameCommand;
            byte[] startPacket = BuildCommandPacket(command, frame.Length, null, 0);
            stream.Write(startPacket, 0, startPacket.Length);
            WriteRawFrameBytes(stream, frame, interChunkDelayMilliseconds);
            stream.Flush();
        }

        public static int GetRawFrameChunkCount(int byteCount)
        {
            if (byteCount < 0)
            {
                throw new ArgumentOutOfRangeException("byteCount");
            }

            if (byteCount == 0)
            {
                return 0;
            }

            return (byteCount + RawFrameChunkByteCount - 1) / RawFrameChunkByteCount;
        }

        public static Stream OpenRjcpSerialStream(string comPort)
        {
            return OpenRjcpSerialStream(comPort, null);
        }

        public static Stream OpenRjcpSerialStream(string comPort, string rjcpDllPath)
        {
            if (string.IsNullOrWhiteSpace(comPort))
            {
                throw new ArgumentException("COM port is required.", "comPort");
            }

            string dllPath = ResolveRjcpDllPath(rjcpDllPath);
            Assembly assembly = Assembly.LoadFrom(dllPath);
            Type portType = assembly.GetType("RJCP.IO.Ports.SerialPortStream", true);
            object port = Activator.CreateInstance(portType, new object[] { comPort });

            try
            {
                SetPropertyIfExists(port, "StopBits", ParseEnum(assembly, "RJCP.IO.Ports.StopBits", "One"));
                SetPropertyIfExists(port, "Parity", ParseEnum(assembly, "RJCP.IO.Ports.Parity", "None"));
                SetPropertyIfExists(port, "DataBits", DefaultDataBits);
                SetPropertyIfExists(port, "BaudRate", DefaultBaudRate);
                SetPropertyIfExists(port, "DtrEnable", true);
                SetPropertyIfExists(port, "RtsEnable", true);
                SetPropertyIfExists(port, "ReadTimeout", DefaultTimeoutMilliseconds);
                SetPropertyIfExists(port, "WriteTimeout", DefaultTimeoutMilliseconds);

                MethodInfo open = portType.GetMethod("Open", Type.EmptyTypes);
                if (open == null)
                {
                    throw new MissingMethodException(portType.FullName, "Open");
                }

                open.Invoke(port, null);

                Stream stream = port as Stream;
                if (stream == null)
                {
                    throw new InvalidOperationException("RJCP.SerialPortStream did not inherit System.IO.Stream.");
                }

                return stream;
            }
            catch
            {
                IDisposable disposable = port as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }

                throw;
            }
        }

        public static void SendFullFrame(string comPort, byte[] frame)
        {
            SendFullFrame(comPort, frame, null, false);
        }

        public static void SendFullFrame(string comPort, byte[] frame, string rjcpDllPath, bool alternateFrame)
        {
            using (Stream stream = OpenRjcpSerialStream(comPort, rjcpDllPath))
            {
                WriteFullFrame(stream, frame, alternateFrame, DeviceInterChunkDelayMilliseconds);
            }
        }

        public static void SendBrightness(string comPort, byte brightness, string rjcpDllPath)
        {
            using (Stream stream = OpenRjcpSerialStream(comPort, rjcpDllPath))
            {
                WriteBrightness(stream, brightness);
            }
        }

        public static void SendBitmapFullFrame(string comPort, Bitmap bitmap)
        {
            SendBitmapFullFrame(comPort, bitmap, null, false);
        }

        public static void SendBitmapFullFrame(
            string comPort,
            Bitmap bitmap,
            string rjcpDllPath,
            bool alternateFrame)
        {
            byte[] frame = EncodeBitmap(bitmap);
            SendFullFrame(comPort, frame, rjcpDllPath, alternateFrame);
        }

        public static void WriteDifferentialFrame(
            Stream stream,
            byte[] previousFrame,
            byte[] currentFrame,
            long frameSequence)
        {
            throw new NotSupportedException(
                "TURZX command 204 differential refresh is not implemented in v1. " +
                "The probe verified full-frame command 200 only; partial/differential payload layout still needs device-side validation.");
        }

        private static byte[] EncodeFormat32BgraBitmap(Bitmap bitmap)
        {
            byte[] frame = new byte[FullFrameByteCount];
            Rectangle rectangle = new Rectangle(0, 0, Width, Height);
            BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = Width * BytesPerPixel;
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < Height; y++)
                {
                    IntPtr rowPointer = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(rowPointer, row, 0, rowBytes);
                    Buffer.BlockCopy(row, 0, frame, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return frame;
        }

        private static void WriteRawFrameBytes(
            Stream stream,
            byte[] frame,
            int interChunkDelayMilliseconds)
        {
            int offset = 0;
            while (offset < frame.Length)
            {
                int count = Math.Min(RawFrameChunkByteCount, frame.Length - offset);
                stream.Write(frame, offset, count);
                offset += count;

                if (interChunkDelayMilliseconds > 0 && offset < frame.Length)
                {
                    Thread.Sleep(interChunkDelayMilliseconds);
                }
            }
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width != Width || height != Height)
            {
                throw new ArgumentException(
                    "TURZX side screen frame must be exactly " + Width + "x" + Height + ".");
            }
        }

        private static void ValidateFullFrame(byte[] frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            if (frame.Length != FullFrameByteCount)
            {
                throw new ArgumentException(
                    "Frame must be exactly " + FullFrameByteCount + " bytes of BGRA data.",
                    "frame");
            }
        }

        private static string ResolveRjcpDllPath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                if (!File.Exists(explicitPath))
                {
                    throw new FileNotFoundException("RJCP.SerialPortStream.dll not found.", explicitPath);
                }

                return Path.GetFullPath(explicitPath);
            }

            string[] candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RJCP.SerialPortStream.dll"),
                Path.Combine(Environment.CurrentDirectory, "RJCP.SerialPortStream.dll")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            throw new FileNotFoundException(
                "RJCP.SerialPortStream.dll not found. Pass the root TURZX RJCP dll path explicitly.");
        }

        private static void SetPropertyIfExists(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name);
            if (property != null && property.CanWrite && value != null)
            {
                property.SetValue(target, value, null);
            }
        }

        private static object ParseEnum(Assembly assembly, string typeName, string value)
        {
            Type type = assembly.GetType(typeName, false);
            return type == null ? null : Enum.Parse(type, value);
        }
    }
}
