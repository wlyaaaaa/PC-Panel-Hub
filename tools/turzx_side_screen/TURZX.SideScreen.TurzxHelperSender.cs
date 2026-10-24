using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

        internal static byte[] BuildDifferentialPayload(byte[] previous, byte[] current, out int length)
        {
            if (previous == null) throw new ArgumentNullException("previous");
            if (current == null) throw new ArgumentNullException("current");
            if (previous.Length != current.Length || (current.Length & 3) != 0)
                throw new ArgumentException("Frames must have equal, four-byte-aligned lengths.");

            length = 0;
            byte[] payload = new byte[checked(current.Length + 9)];
            int offset = 0, written = 0;
            while (offset < current.Length)
            {
                if (PixelsEqual(previous, current, offset))
                {
                    offset += 4;
                    continue;
                }

                int start = offset;
                do { offset += 4; }
                while (offset < current.Length && !PixelsEqual(previous, current, offset));
                int count = (offset - start) / 4;
                // The vendor checks the limit only when an equal pixel ends a run.
                // At EOF it writes the low 16 count bits, even above that limit.
                bool atEnd = offset == current.Length;
                if (!atEnd && count > 65000) return null;
                bool single = count == 1 && !atEnd;
                int required = checked(written + (single ? 3 : 5) + count * 4);
                if (required > payload.Length)
                    Array.Resize(ref payload, (int)Math.Min(int.MaxValue,
                        Math.Max((long)required, payload.Length * 2L)));

                int index = start / 4;
                payload[written++] = (byte)((index >> 16) | (single ? 0x80 : 0));
                payload[written++] = (byte)(index >> 8);
                payload[written++] = (byte)index;
                if (!single)
                {
                    payload[written++] = (byte)(count >> 8);
                    payload[written++] = (byte)count;
                }
                Buffer.BlockCopy(current, start, payload, written, count * 4);
                written += count * 4;
            }
            length = written;
            return payload;
        }

        private static bool PixelsEqual(byte[] previous, byte[] current, int offset)
        {
            return previous[offset] == current[offset] && previous[offset + 1] == current[offset + 1] &&
                previous[offset + 2] == current[offset + 2] && previous[offset + 3] == current[offset + 3];
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
            private readonly MethodInfo _sendCommand;
            private readonly MethodInfo _sendBody;
            private readonly MethodInfo _prime;
            private readonly MethodInfo _setBrightness;
            private readonly MethodInfo _close;
            private bool _disposed;
            private volatile bool _abandoned;
            private volatile SendProgress _sendProgress;

            [DllImport("kernel32.dll", EntryPoint = "GetThreadTimes", SetLastError = true)]
            private static extern bool Win32GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);
            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentThread();
            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();

            private static long CurrentThreadCpuTicks()
            {
                try
                {
                    long created, exited, kernel, user;
                    return Win32GetThreadTimes(GetCurrentThread(), out created, out exited, out kernel, out user) ? kernel + user : -1;
                }
                catch { return -1; }
            }

            private static string PhaseCpuMilliseconds(SendProgress progress)
            {
                if (progress.StartedThreadId != Thread.CurrentThread.ManagedThreadId || progress.StartedCpuTicks < 0)
                    return "unavailable";
                long ended = CurrentThreadCpuTicks();
                return ended < progress.StartedCpuTicks ? "unavailable" :
                    ((ended - progress.StartedCpuTicks) / 10000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            }

            private sealed class SendProgress
            {
                public readonly string Phase;
                public readonly long StartedAt;
                public readonly long StartedCpuTicks;
                public readonly int StartedThreadId;
                public readonly uint NativeThreadId;
                public readonly DateTime StartedUtc;
                public readonly string CompletedStages;

                public SendProgress(string phase, string completedStages)
                {
                    Phase = phase;
                    StartedAt = Stopwatch.GetTimestamp();
                    StartedUtc = DateTime.UtcNow;
                    StartedThreadId = Thread.CurrentThread.ManagedThreadId;
                    NativeThreadId = GetCurrentThreadId();
                    StartedCpuTicks = CurrentThreadCpuTicks();
                    CompletedStages = completedStages;
                }
            }

            private static long PhaseElapsedMilliseconds(SendProgress progress)
            {
                return (long)((Stopwatch.GetTimestamp() - progress.StartedAt) * 1000.0 / Stopwatch.Frequency);
            }

            private void SetSendPhase(string phase)
            {
                SendProgress previous = _sendProgress;
                DateTime completedUtc = DateTime.UtcNow;
                string completed = previous == null ? "" : previous.CompletedStages +
                    previous.Phase + "=" + PhaseElapsedMilliseconds(previous) + "ms(cpuMs=" + PhaseCpuMilliseconds(previous) +
                    ",startUtc=" + previous.StartedUtc.ToString("o") + ",endUtc=" + completedUtc.ToString("o") +
                    ",nativeTid=" + previous.NativeThreadId + "),";
                // Publish phase and its monotonic start together so the timeout
                // thread can report the blocked step without touching the COM object.
                _sendProgress = new SendProgress(phase, completed);
            }

            private string DescribeSendProgress()
            {
                SendProgress progress = _sendProgress;
                DateTime readUtc = DateTime.UtcNow;
                return progress == null ? "phase=unknown snapshotReadUtc=" + readUtc.ToString("o") : "phase=" + progress.Phase +
                    " phaseElapsedMs=" + PhaseElapsedMilliseconds(progress) +
                    " phaseStartUtc=" + progress.StartedUtc.ToString("o") +
                    " snapshotReadUtc=" + readUtc.ToString("o") +
                    " nativeTid=" + progress.NativeThreadId +
                    " cpuWallEndpoints=non-atomic" +
                    " completedStages=[" + progress.CompletedStages.TrimEnd(',') + "]";
            }

            private sealed class WorkerIdentity
            {
                public readonly DateTime EnteredUtc = DateTime.UtcNow;
                public readonly uint NativeThreadId = GetCurrentThreadId();
            }

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
                ThrowIfDisposed();
                if (previousFrameData == null) throw new ArgumentNullException("previousFrameData");
                if (currentFrameData == null) throw new ArgumentNullException("currentFrameData");
                _sendProgress = new SendProgress("diff-dispatch", "");

                if (!flag)
                {
                    return SendDiffCommand204(previousFrameData, currentFrameData, sequence, swapOrder);
                }

                object[] args = swapOrder
                    ? new object[] { currentFrameData, previousFrameData, sequence, flag }
                    : new object[] { previousFrameData, currentFrameData, sequence, flag };
                SetSendPhase("vendor-diff");
                object result = _sendDiff.Invoke(_driver, args);
                return result == null ? 0 : System.Convert.ToInt32(result);
            }

            public bool SendDiffWithTimeout(
                byte[] previousFrameData,
                byte[] currentFrameData,
                long sequence,
                bool swapOrder,
                bool flag,
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
                        flag);
                }, timeoutMs, "DIFF", out message);
                result = localResult;
                return ok;
            }

            private bool RunBoundedSend(Action send, int timeoutMs, string operation, out string message)
            {
                Stopwatch boundedWatch = Stopwatch.StartNew();
                DateTime boundedStartUtc = DateTime.UtcNow;
                DateTime threadStartUtc;
                DateTime threadStartedUtc;
                WorkerIdentity workerIdentity = null;
                // Until the worker publishes its own samples, its phase and
                // native identity are unknown; never substitute the caller's.
                _sendProgress = null;
                Exception workerError = null;
                Thread worker = null;
                try
                {
                    worker = new Thread(delegate()
                    {
                        Interlocked.Exchange(ref workerIdentity, new WorkerIdentity());
                        _sendProgress = new SendProgress("worker-start", "");
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
                    threadStartUtc = DateTime.UtcNow;
                    worker.Start();
                    threadStartedUtc = DateTime.UtcNow;
                }
                catch
                {
                    throw;
                }

                DateTime joinStartUtc = DateTime.UtcNow;
                Stopwatch joinWatch = Stopwatch.StartNew();
                bool joined = worker.Join(timeoutMs);
                // Preserve abandonment before diagnostics can throw: a writer
                // that timed out must never have its COM object closed/reused.
                if (!joined) _abandoned = true;
                joinWatch.Stop();
                DateTime joinReturnUtc = DateTime.UtcNow;
                WorkerIdentity started = Interlocked.CompareExchange(ref workerIdentity, null, null);
                string timing = "boundedStartUtc=" + boundedStartUtc.ToString("o") +
                    " threadStartUtc=" + threadStartUtc.ToString("o") +
                    " threadStartedUtc=" + threadStartedUtc.ToString("o") +
                    " workerEnteredUtc=" + (started == null ? "unknown" : started.EnteredUtc.ToString("o")) +
                    " workerNativeTid=" + (started == null ? "unknown" : started.NativeThreadId.ToString()) +
                    " joinStartUtc=" + joinStartUtc.ToString("o") +
                    " joinReturnUtc=" + joinReturnUtc.ToString("o") +
                    " joinMs=" + joinWatch.ElapsedMilliseconds + " joined=" + joined;
                string progress = DescribeSendProgress();
                timing += " boundedMs=" + boundedWatch.ElapsedMilliseconds +
                    " boundedSampleUtc=" + DateTime.UtcNow.ToString("o");
                if (!joined)
                {
                    // Never close or reuse a serial object while its writer may
                    // still be blocked. The caller exits this process; Windows
                    // then releases the abandoned COM handle atomically.
                    message = operation + " TIMEOUT after " + timeoutMs + " ms; " + timing + "; " + progress;
                    return false;
                }

                if (workerError != null)
                {
                    Exception detail = workerError is TargetInvocationException && workerError.InnerException != null
                        ? workerError.InnerException
                        : workerError;
                    message = operation + " EXCEPTION - " + detail.GetType().Name + ": " + detail.Message +
                        "; " + timing + "; " + progress;
                    return false;
                }

                // A successful Join may return after the nominal budget due to
                // caller scheduling. Retain the same verdict and report its
                // actual boundaries rather than treating elapsed time as cause.
                message = operation + " OK; " + timing + "; " + progress;
                return true;
            }

            private int SendDiffCommand204(byte[] previousFrameData, byte[] currentFrameData, long sequence, bool swapOrder)
            {
                byte[] from = swapOrder ? currentFrameData : previousFrameData;
                byte[] to = swapOrder ? previousFrameData : currentFrameData;
                SetSendPhase("diff-calculate");
                int bodyLength;
                byte[] payload = BuildDifferentialPayload(from, to, out bodyLength);
                SetSendPhase("diff-payload-prepare");
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

                SetSendPhase("command-204-header");
                _sendCommand.Invoke(_serial, new object[] { 204, bodyLength, header, (byte)0 });
                SetSendPhase("command-204-body");
                _sendBody.Invoke(_serial, new object[] { payload, bodyLength });
                SetSendPhase("diff-complete");
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
