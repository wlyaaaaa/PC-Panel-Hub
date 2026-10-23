param(
    [Parameter(Mandatory = $true)][string]$VendorRoot,
    [string]$WorkRoot = (Join-Path $PSScriptRoot 'out\tests')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$vendor = (Resolve-Path -LiteralPath $VendorRoot).Path
$null = New-Item -ItemType Directory -Path $WorkRoot -Force
$parent = (Resolve-Path -LiteralPath $WorkRoot).Path
$temp = Join-Path $parent ('turzx-diff-payload-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temp
try {
    $harness = Join-Path $temp 'OfflineDiffTest.cs'
    $exe = Join-Path $temp 'OfflineDiffTest.exe'
    @'
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using TURZX.SideScreen;
internal static class OfflineDiffTest
{
    private static MethodInfo vendor;
    private static int passed, vendorExceptions;
    private static byte[] Vendor(byte[] previous, byte[] current, out int length)
    {
        object[] args = { previous, current, 0, 4, 0, 65000 };
        byte[] result = (byte[])vendor.Invoke(null, args);
        length = (int)args[4];
        return result;
    }
    private static byte[] Changed(int pixels, int start, int count)
    {
        byte[] frame = new byte[pixels * 4];
        for (int i = start; i < start + count; i++)
            for (int c = 0; c < 4; c++) frame[i * 4 + c] = (byte)(1 + (i + c) % 251);
        return frame;
    }
    private static void Check(string name, byte[] previous, byte[] current, int expected,
        bool allowVendorOverflow = false)
    {
        int actualLength;
        byte[] actual = TurzxHelperSender.BuildDifferentialPayload(previous, current, out actualLength);
        if (actualLength != Math.Max(expected, 0) || (actual == null) != (expected < 0))
            throw new Exception(name + ": unexpected new length/null result " + actualLength);
        int vendorLength;
        byte[] reference;
        try { reference = Vendor(previous, current, out vendorLength); }
        catch (TargetInvocationException ex)
        {
            if (!allowVendorOverflow || !(ex.InnerException is IndexOutOfRangeException)) throw;
            Console.WriteLine("VENDOR_EXCEPTION {0}: {1}; newLength={2}", name,
                ex.InnerException.GetType().Name, actualLength);
            vendorExceptions++;
            return;
        }
        if (vendorLength != actualLength || (reference == null) != (actual == null))
            throw new Exception(name + ": vendor length/null mismatch " + vendorLength + "/" + actualLength);
        for (int i = 0; i < actualLength; i++)
            if (reference[i] != actual[i]) throw new Exception(name + ": byte mismatch at " + i);
        passed++;
        Console.WriteLine("PASS {0} length={1} null={2}", name, actualLength, actual == null);
    }
    private static void Measure(string name, byte[] previous, byte[] current, bool useVendor)
    {
        Process process = Process.GetCurrentProcess();
        double elapsed = 0, cpu = 0;
        for (int i = 0; i < 3; i++)
        {
            TimeSpan cpuStart = process.TotalProcessorTime;
            Stopwatch watch = Stopwatch.StartNew();
            int length;
            byte[] result = useVendor ? Vendor(previous, current, out length) :
                TurzxHelperSender.BuildDifferentialPayload(previous, current, out length);
            watch.Stop();
            double ms = watch.Elapsed.TotalMilliseconds;
            double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            elapsed += ms; cpu += cpuMs;
            GC.KeepAlive(result);
            Console.WriteLine("TIMING {0} {1} iteration={2} elapsedMs={3:F3} cpuMs={4:F3}",
                name, useVendor ? "vendor" : "linear", i + 1, ms, cpuMs);
        }
        Console.WriteLine("MEAN {0} {1} elapsedMs={2:F3} cpuMs={3:F3}",
            name, useVendor ? "vendor" : "linear", elapsed / 3, cpu / 3);
        if (useVendor) { vendorElapsed = elapsed; vendorCpu = cpu; }
        else Console.WriteLine("RATIO {0} elapsed={1:F2}x; CPU resolution is coarse", name,
            vendorElapsed / elapsed);
    }
    private static double vendorElapsed, vendorCpu;
    private static int Main(string[] args)
    {
        try
        {
            string root = args[0];
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            string patched = Path.Combine(root, "TURZX.weatherfix.metrics.exe");
            Assembly assembly = Assembly.LoadFrom(File.Exists(patched) ? patched : Path.Combine(root, "TURZX.exe"));
            vendor = assembly.GetType("\u8DB5", true).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "\u86F3" && p.Length == 6 &&
                        p[0].ParameterType == typeof(byte[]) && p[1].ParameterType == typeof(byte[]) &&
                        p[2].ParameterType == typeof(int) && p[3].ParameterType == typeof(int) &&
                        p[4].ParameterType == typeof(int).MakeByRefType() && p[5].ParameterType == typeof(int);
                });
            Console.WriteLine("OFFLINE vendor={0} MVID={1}", Path.GetFileName(assembly.Location), assembly.ManifestModule.ModuleVersionId);
            Check("unchanged", new byte[64], new byte[64], 0);
            Check("isolated", new byte[64], Changed(16, 3, 1), 7);
            Check("terminal-single", new byte[64], Changed(16, 15, 1), 9);
            Check("continuous", new byte[64], Changed(16, 2, 3), 17);
            Check("terminal-continuous", new byte[64], Changed(16, 13, 3), 17);
            byte[] channels = new byte[64];
            for (int c = 0; c < 4; c++) channels[(c * 2 + 1) * 4 + c] = (byte)(17 + c);
            Check("multiple-runs-all-channels", new byte[64], channels, 28);
            Check("run65000-equal-ended", new byte[65002 * 4], Changed(65002, 0, 65000), 260005);
            Check("run65001-equal-ended", new byte[65003 * 4], Changed(65003, 0, 65001), -1);
            Check("run65001-terminal", new byte[65003 * 4], Changed(65003, 2, 65001), 260009);
            Check("run65536-terminal-count-wrap", new byte[65538 * 4], Changed(65538, 2, 65536), 262149);
            Check("tiny-vendor-buffer", new byte[4], Changed(1, 0, 1), 9, true);
            byte[] fragmented = new byte[64];
            for (int p = 0; p < 16; p += 2) fragmented[p * 4] = 1;
            Check("fragmented", new byte[64], fragmented, 56);
            byte[] growth = new byte[256];
            for (int p = 0; p < 64; p++) if (p % 3 != 2) growth[p * 4] = 1;
            Check("bounded-buffer-growth", new byte[256], growth, 282, true);
            byte[] previous = new byte[480 * 1920 * 4];
            byte[] sparse = Changed(480 * 1920, 50000, 64);
            sparse[(480 * 1919 + 200) * 4 + 3] = 1;
            byte[] blocks = new byte[previous.Length];
            for (int i = 0; i < 8; i++)
                Buffer.BlockCopy(Changed(16000, 0, 16000), 0, blocks, (10000 + i * 100000) * 4, 64000);
            Check("480x1920-mostly-same", previous, sparse, 268);
            Check("480x1920-blocks", previous, blocks, 512040);
            Measure("480x1920-mostly-same", previous, sparse, true);
            Measure("480x1920-mostly-same", previous, sparse, false);
            Measure("480x1920-blocks", previous, blocks, true);
            Measure("480x1920-blocks", previous, blocks, false);
            Console.WriteLine("RESULT passed={0} vendorExceptions={1}; no DiffSession or serial access", passed, vendorExceptions);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
'@ | Set-Content -LiteralPath $harness -Encoding UTF8
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /r:System.Drawing.dll /r:System.Core.dll "/out:$exe" (Join-Path $PSScriptRoot 'TURZX.SideScreen.TurzxHelperSender.cs') $harness
    if ($LASTEXITCODE -ne 0) { throw "Offline compilation failed: $LASTEXITCODE" }
    Push-Location -LiteralPath $temp
    try {
        & $exe $vendor
        if ($LASTEXITCODE -ne 0) { throw "Offline comparison failed: $LASTEXITCODE" }
    } finally { Pop-Location }
} finally {
    $resolved = [System.IO.Path]::GetFullPath($temp)
    if ([System.IO.Path]::GetDirectoryName($resolved) -ne $parent) { throw 'Unexpected temporary directory boundary.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
