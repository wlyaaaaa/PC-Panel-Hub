using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace TURZX.SideScreen
{
    public static class TurzxHelperSender
    {
        public const int Width = 480;
        public const int Height = 1920;
        public const string DefaultDevCode = "VID_0525&PID_A4A7";

        internal static ThreadPriority DesiredSenderThreadPriorityForTest()
        {
            return ThreadPriority.Highest;
        }

        public static byte[] ConvertBitmapToFrameData(string root, Bitmap bitmap)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("Root is required.", "root");
            }

            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            if (bitmap.Width != Width || bitmap.Height != Height)
            {
                throw new InvalidOperationException("Bitmap must be 480x1920; got " + bitmap.Width + "x" + bitmap.Height);
            }

            Assembly asm = LoadTurzxAssembly(root);
            Type frameType = asm.GetType("\u8DB5", true);
            MethodInfo convert = frameType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "\u86F3" &&
                        p.Length == 3 &&
                        p[0].ParameterType == typeof(Bitmap) &&
                        p[1].ParameterType == typeof(int) &&
                        p[2].ParameterType == typeof(int);
                });

            return (byte[])convert.Invoke(null, new object[] { bitmap, 4, Width });
        }

        public static bool SendPng(string root, string comPort, string pngPath, string devCode, int timeoutMs, out string message)
        {
            if (string.IsNullOrWhiteSpace(pngPath))
            {
                throw new ArgumentException("PNG path is required.", "pngPath");
            }

            using (Bitmap bitmap = new Bitmap(pngPath))
            {
                return SendBitmap(root, comPort, bitmap, devCode, timeoutMs, out message);
            }
        }

        public static bool SendBitmap(string root, string comPort, Bitmap bitmap, string devCode, int timeoutMs, out string message)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("Root is required.", "root");
            }

            if (string.IsNullOrWhiteSpace(comPort))
            {
                throw new ArgumentException("COM port is required.", "comPort");
            }

            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            if (bitmap.Width != Width || bitmap.Height != Height)
            {
                throw new InvalidOperationException("Bitmap must be 480x1920; got " + bitmap.Width + "x" + bitmap.Height);
            }

            if (timeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException("timeoutMs");
            }

            if (string.IsNullOrWhiteSpace(devCode))
            {
                devCode = DefaultDevCode;
            }

            bool ok = false;
            string localMessage = "not started";
            Bitmap senderBitmap = (Bitmap)bitmap.Clone();
            Thread thread;
            try
            {
                thread = new Thread(delegate()
                {
                    try
                    {
                        Assembly asm = LoadTurzxAssembly(root);
                        byte[] frameData = ConvertBitmapToFrameData(root, senderBitmap);

                        Type driverType = asm.GetType("\u99DF", true);
                        object driver = Activator.CreateInstance(driverType, new object[] { devCode });
                        FieldInfo serialField = driverType.GetField("\u97D8", BindingFlags.Instance | BindingFlags.Public);
                        if (serialField == null)
                        {
                            throw new MissingFieldException(driverType.FullName, "\u97D8");
                        }

                        object serial = serialField.GetValue(driver);
                        MethodInfo open = serial.GetType().GetMethod("\u8A54", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                        if (open == null)
                        {
                            throw new MissingMethodException(serial.GetType().FullName, "\u8A54(string)");
                        }

                        bool opened = (bool)open.Invoke(serial, new object[] { comPort });
                        if (!opened)
                        {
                            localMessage = "FAILED - TURZX serial helper could not open " + comPort;
                            return;
                        }

                        try
                        {
                            MethodInfo send = driverType.GetMethod("\u8A54", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]), typeof(int), typeof(bool) }, null);
                            if (send == null)
                            {
                                throw new MissingMethodException(driverType.FullName, "\u8A54(byte[],int,bool)");
                            }

                            send.Invoke(driver, new object[] { frameData, frameData.Length, false });
                        }
                        finally
                        {
                            MethodInfo close = serial.GetType().GetMethod("\u8FBC", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                            if (close != null)
                            {
                                close.Invoke(serial, null);
                            }
                        }

                        ok = true;
                        localMessage = "OK frameBytes=" + frameData.Length;
                    }
                    catch (TargetInvocationException ex)
                    {
                        localMessage = "EXCEPTION - " + (ex.InnerException == null ? ex.Message : ex.InnerException.Message);
                    }
                    catch (Exception ex)
                    {
                        localMessage = "EXCEPTION - " + ex.GetType().Name + ": " + ex.Message;
                    }
                    finally
                    {
                        senderBitmap.Dispose();
                    }
                });

                thread.IsBackground = true;
                try
                {
                    thread.Priority = DesiredSenderThreadPriorityForTest();
                }
                catch
                {
                    // The bounded send timeout still protects the stream if the OS
                    // refuses a priority change.
                }
                thread.Start();
            }
            catch
            {
                senderBitmap.Dispose();
                throw;
            }
            if (!thread.Join(timeoutMs))
            {
                message = "TIMEOUT after " + timeoutMs + " ms";
                return false;
            }

            message = localMessage;
            return ok;
        }

        public sealed class DiffSession : IDisposable
        {
            private readonly string _root;
            private readonly object _driver;
            private readonly object _serial;
            private readonly MethodInfo _sendFull;
            private readonly MethodInfo _sendDiff;
            private readonly MethodInfo _frameDiff;
            private readonly MethodInfo _frameDiffAlt;
            private readonly MethodInfo _sendCommand;
            private readonly MethodInfo _sendBody;
            private readonly MethodInfo _prime;
            private readonly MethodInfo _setBrightness;
            private readonly MethodInfo _close;
            private bool _disposed;
            private volatile bool _abandoned;

            public DiffSession(string root, string comPort, string devCode)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new ArgumentException("Root is required.", "root");
                }

                if (string.IsNullOrWhiteSpace(comPort))
                {
                    throw new ArgumentException("COM port is required.", "comPort");
                }

                if (string.IsNullOrWhiteSpace(devCode))
                {
                    devCode = DefaultDevCode;
                }

                _root = root;
                Assembly asm = LoadTurzxAssembly(root);
                Type driverType = asm.GetType("\u99DF", true);
                _driver = Activator.CreateInstance(driverType, new object[] { devCode });

                FieldInfo serialField = driverType.GetField("\u97D8", BindingFlags.Instance | BindingFlags.Public);
                if (serialField == null)
                {
                    throw new MissingFieldException(driverType.FullName, "\u97D8");
                }

                _serial = serialField.GetValue(_driver);
                MethodInfo open = _serial.GetType().GetMethod("\u8A54", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (open == null)
                {
                    throw new MissingMethodException(_serial.GetType().FullName, "\u8A54(string)");
                }

                bool opened = (bool)open.Invoke(_serial, new object[] { comPort });
                if (!opened)
                {
                    throw new InvalidOperationException("TURZX serial helper could not open " + comPort);
                }

                _sendFull = driverType.GetMethod("\u8A54", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]), typeof(int), typeof(bool) }, null);
                if (_sendFull == null)
                {
                    throw new MissingMethodException(driverType.FullName, "\u8A54(byte[],int,bool)");
                }

                _sendDiff = driverType.GetMethod("\u88FA", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]), typeof(byte[]), typeof(long), typeof(bool) }, null);
                if (_sendDiff == null)
                {
                    throw new MissingMethodException(driverType.FullName, "\u88FA(byte[],byte[],long,bool)");
                }

                Type frameType = asm.GetType("\u8DB5", true);
                _frameDiff = frameType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return m.Name == "\u86F3" &&
                            p.Length == 6 &&
                            p[0].ParameterType == typeof(byte[]) &&
                            p[1].ParameterType == typeof(byte[]) &&
                            p[2].ParameterType == typeof(int) &&
                            p[3].ParameterType == typeof(int) &&
                            p[4].ParameterType == typeof(int).MakeByRefType() &&
                            p[5].ParameterType == typeof(int);
                    });
                _frameDiffAlt = frameType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return m.Name == "\u87A0" &&
                            p.Length == 6 &&
                            p[0].ParameterType == typeof(byte[]) &&
                            p[1].ParameterType == typeof(byte[]) &&
                            p[2].ParameterType == typeof(int) &&
                            p[3].ParameterType == typeof(int) &&
                            p[4].ParameterType == typeof(int).MakeByRefType() &&
                            p[5].ParameterType == typeof(int);
                    });

                _sendCommand = _serial.GetType().GetMethod("\u8A54", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int), typeof(int), typeof(byte[]), typeof(byte) }, null);
                if (_sendCommand == null)
                {
                    throw new MissingMethodException(_serial.GetType().FullName, "\u8A54(int,int,byte[],byte)");
                }

                _sendBody = _serial.GetType().GetMethod("\u8A54", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte[]), typeof(int) }, null);
                if (_sendBody == null)
                {
                    throw new MissingMethodException(_serial.GetType().FullName, "\u8A54(byte[],int)");
                }

                _prime = _serial.GetType().GetMethod("\u8E62", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                _setBrightness = driverType.GetMethod("\u931D", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(byte) }, null);

                _close = _serial.GetType().GetMethod("\u8FBC", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            }

            public byte[] Convert(Bitmap bitmap)
            {
                ThrowIfDisposed();
                return ConvertBitmapToFrameData(_root, bitmap);
            }

            public void SendFull(byte[] frameData)
            {
                ThrowIfDisposed();
                if (frameData == null) throw new ArgumentNullException("frameData");
                _sendFull.Invoke(_driver, new object[] { frameData, frameData.Length, false });
            }

            public bool SendFullWithTimeout(
                byte[] frameData,
                int timeoutMs,
                int repeatCount,
                int repeatDelayMs,
                bool primeBaseline,
                byte brightness,
                out string message)
            {
                ThrowIfDisposed();
                if (frameData == null) throw new ArgumentNullException("frameData");
                if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
                if (repeatCount <= 0) throw new ArgumentOutOfRangeException("repeatCount");
                if (repeatDelayMs < 0) throw new ArgumentOutOfRangeException("repeatDelayMs");

                return RunBoundedSend(delegate()
                {
                    if (primeBaseline)
                    {
                        if (_prime == null)
                        {
                            throw new MissingMethodException(_serial.GetType().FullName, "\u8E62()");
                        }
                        if (_setBrightness == null)
                        {
                            throw new MissingMethodException(_driver.GetType().FullName, "\u931D(byte)");
                        }

                        // Match the vendor's 8.8-inch baseline lifecycle: raw
                        // 0x2C priming, brightness restore, then duplicate full
                        // command-200 frames before starting command 204.
                        _prime.Invoke(_serial, null);
                        Thread.Sleep(10);
                        _setBrightness.Invoke(_driver, new object[] { brightness });
                        Thread.Sleep(100);
                    }

                    for (int repeat = 0; repeat < repeatCount; repeat++)
                    {
                        _sendFull.Invoke(_driver, new object[] { frameData, frameData.Length, false });
                        if (repeat + 1 < repeatCount && repeatDelayMs > 0)
                        {
                            Thread.Sleep(repeatDelayMs);
                        }
                    }
                    if (primeBaseline)
                    {
                        Thread.Sleep(10);
                    }
                }, timeoutMs, "FULL", out message);
            }

            public int SendDiff(byte[] previousFrameData, byte[] currentFrameData, long sequence, bool swapOrder, bool flag)
            {
                return SendDiff(previousFrameData, currentFrameData, sequence, swapOrder, flag, false);
            }

            public int SendDiff(byte[] previousFrameData, byte[] currentFrameData, long sequence, bool swapOrder, bool flag, bool useAltHelper)
            {
                ThrowIfDisposed();
                if (previousFrameData == null) throw new ArgumentNullException("previousFrameData");
                if (currentFrameData == null) throw new ArgumentNullException("currentFrameData");

                if (!flag)
                {
                    return SendDiffCommand204(previousFrameData, currentFrameData, sequence, swapOrder, useAltHelper);
                }

                object[] args = swapOrder
                    ? new object[] { currentFrameData, previousFrameData, sequence, flag }
                    : new object[] { previousFrameData, currentFrameData, sequence, flag };
                object result = _sendDiff.Invoke(_driver, args);
                return result == null ? 0 : System.Convert.ToInt32(result);
            }

            public bool SendDiffWithTimeout(
                byte[] previousFrameData,
                byte[] currentFrameData,
                long sequence,
                bool swapOrder,
                bool flag,
                bool useAltHelper,
                int timeoutMs,
                out int result,
                out string message)
            {
                ThrowIfDisposed();
                if (previousFrameData == null) throw new ArgumentNullException("previousFrameData");
                if (currentFrameData == null) throw new ArgumentNullException("currentFrameData");
                if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");

                int localResult = 0;
                bool ok = RunBoundedSend(delegate()
                {
                    localResult = SendDiff(
                        previousFrameData,
                        currentFrameData,
                        sequence,
                        swapOrder,
                        flag,
                        useAltHelper);
                }, timeoutMs, "DIFF", out message);
                result = localResult;
                return ok;
            }

            private bool RunBoundedSend(Action send, int timeoutMs, string operation, out string message)
            {
                Exception workerError = null;
                Thread worker = null;
                try
                {
                    worker = new Thread(delegate()
                    {
                        try
                        {
                            send();
                        }
                        catch (Exception ex)
                        {
                            workerError = ex;
                        }
                    });
                    worker.IsBackground = true;
                    try
                    {
                        worker.Priority = DesiredSenderThreadPriorityForTest();
                    }
                    catch
                    {
                        // The timeout remains authoritative if the OS refuses
                        // the requested managed thread priority.
                    }
                    worker.Start();
                }
                catch
                {
                    throw;
                }

                if (!worker.Join(timeoutMs))
                {
                    // Never close or reuse a serial object while its writer may
                    // still be blocked. The caller exits this process; Windows
                    // then releases the abandoned COM handle atomically.
                    _abandoned = true;
                    message = operation + " TIMEOUT after " + timeoutMs + " ms";
                    return false;
                }

                if (workerError != null)
                {
                    Exception detail = workerError is TargetInvocationException && workerError.InnerException != null
                        ? workerError.InnerException
                        : workerError;
                    message = operation + " EXCEPTION - " + detail.GetType().Name + ": " + detail.Message;
                    return false;
                }

                message = operation + " OK";
                return true;
            }

            private int SendDiffCommand204(byte[] previousFrameData, byte[] currentFrameData, long sequence, bool swapOrder, bool useAltHelper)
            {
                byte[] from = swapOrder ? currentFrameData : previousFrameData;
                byte[] to = swapOrder ? previousFrameData : currentFrameData;
                object[] args = new object[] { from, to, 0, 4, 0, 65000 };
                if (useAltHelper && _frameDiffAlt == null)
                {
                    throw new MissingMethodException("Requested alternate TURZX diff helper is unavailable.");
                }
                MethodInfo helper = useAltHelper ? _frameDiffAlt : _frameDiff;
                byte[] payload = (byte[])helper.Invoke(null, args);
                int bodyLength = System.Convert.ToInt32(args[4]);
                if (payload == null)
                {
                    throw new InvalidOperationException("TURZX diff helper returned null payload.");
                }

                if (bodyLength == 0)
                {
                    if (payload.Length < 8)
                    {
                        Array.Resize(ref payload, 8);
                    }

                    payload[0] = 128;
                    bodyLength = 6;
                }

                if (payload.Length < bodyLength + 2)
                {
                    Array.Resize(ref payload, bodyLength + 2);
                }

                payload[bodyLength] = 0xEF;
                payload[bodyLength + 1] = 0x69;
                bodyLength += 2;

                byte[] header = new byte[8];
                uint seq = unchecked((uint)sequence);
                header[0] = (byte)((seq >> 24) & 0xFF);
                header[1] = (byte)((seq >> 16) & 0xFF);
                header[2] = (byte)((seq >> 8) & 0xFF);
                header[3] = (byte)(seq & 0xFF);

                _sendCommand.Invoke(_serial, new object[] { 204, bodyLength, header, (byte)0 });
                _sendBody.Invoke(_serial, new object[] { payload, bodyLength });
                return bodyLength;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_abandoned)
                {
                    return;
                }
                if (_close != null)
                {
                    _close.Invoke(_serial, null);
                }
            }

            private void ThrowIfDisposed()
            {
                if (_disposed || _abandoned)
                {
                    throw new ObjectDisposedException("DiffSession");
                }
            }
        }

        private static Assembly LoadTurzxAssembly(string root)
        {
            EnsureAssemblyResolve(root);
            string patched = System.IO.Path.Combine(root, "TURZX.weatherfix.metrics.exe");
            string original = System.IO.Path.Combine(root, "TURZX.exe");
            string assemblyPath = System.IO.File.Exists(patched) ? patched : original;
            return Assembly.LoadFrom(assemblyPath);
        }

        private static bool _assemblyResolveInstalled;

        private static void EnsureAssemblyResolve(string root)
        {
            if (_assemblyResolveInstalled)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
            {
                string name = new AssemblyName(eventArgs.Name).Name;
                string dll = System.IO.Path.Combine(root, name + ".dll");
                if (System.IO.File.Exists(dll))
                {
                    return Assembly.LoadFrom(dll);
                }

                string exe = System.IO.Path.Combine(root, name + ".exe");
                return System.IO.File.Exists(exe) ? Assembly.LoadFrom(exe) : null;
            };
            _assemblyResolveInstalled = true;
        }
    }
}
