param([string]$WorkRoot = (Join-Path $PSScriptRoot 'out\tests'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Path $WorkRoot -Force
$parent = (Resolve-Path -LiteralPath $WorkRoot).Path
$temp = Join-Path $parent ('serial-failure-evidence-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temp
try {
    $harness = Join-Path $temp 'EvidenceTest.cs'
    $exe = Join-Path $temp 'EvidenceTest.exe'
    @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TURZX.SideScreen;
namespace RJCP.EvidencePositiveControl
{
internal static class EvidenceTest
{
    private static string originalType;
    private static int originalHResult;
    private static void SwallowRealNativeFailure()
    {
        try { Marshal.ThrowExceptionForHR(unchecked((int)0x80070026)); }
        catch (IOException exception) { originalType = exception.GetType().FullName; originalHResult = exception.HResult; }
    }
    private static void RunThread(string name)
    {
        var thread = new Thread(SwallowRealNativeFailure); thread.Name = name;
        thread.Start(); if (!thread.Join(5000)) throw new Exception("Test thread did not exit.");
    }
    private static int Main(string[] args)
    {
        using (var evidence = new SerialFailureEvidence("TEST"))
        {
            using (var caught = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var thread = new Thread(delegate() { SwallowRealNativeFailure(); caught.Set(); release.WaitOne(); });
                thread.Name = "SerialPortStream_TEST"; thread.IsBackground = true; thread.Start();
                try
                {
                    if (!caught.WaitOne(5000)) throw new Exception("Native exception was not caught.");
                    if (!evidence.WriteStopped(Path.Combine(args[0], "alive.json"), "Device Error")) throw new Exception("Live thread exception was lost.");
                }
                finally { release.Set(); if (!thread.Join(5000)) throw new Exception("Test thread did not exit."); }
            }
            if (evidence.WriteStopped(Path.Combine(args[0], "normal.json"), "completed normally")) throw new Exception("Normal failure gate bypassed.");
            if (!evidence.WriteStopped(Path.Combine(args[0], "positive.json"), "Device Error")) throw new Exception("Caught exception was lost.");
            if (!evidence.WriteStopped(Path.Combine(args[0], "positive.json"), "Device Error")) throw new Exception("Atomic replacement failed.");
            File.WriteAllText(Path.Combine(args[0], "original.txt"), originalType + "\n" + originalHResult);
        }
        using (var evidence = new SerialFailureEvidence("TEST"))
        {
            RunThread("SerialPortStream_TEST_OTHER");
            if (evidence.WriteStopped(Path.Combine(args[0], "wrong-thread.json"), "Device Error")) throw new Exception("Wrong thread was captured.");
        }
        var normal = new SerialFailureEvidence("NORMAL"); normal.Dispose();
        if (normal.WriteStopped(Path.Combine(args[0], "empty.json"), "Device Error")) throw new Exception("Normal Dispose retained exceptions.");
        return 0;
    }
}
}
'@ | Set-Content -LiteralPath $harness -Encoding utf8
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /r:System.Drawing.dll /r:System.Core.dll "/out:$exe" (Join-Path $PSScriptRoot 'TURZX.SideScreen.TurzxHelperSender.cs') $harness
    if ($LASTEXITCODE -ne 0) { throw "Framework compiler failed: $LASTEXITCODE" }
    & $exe $temp
    if ($LASTEXITCODE -ne 0) { throw "Framework positive control failed: $LASTEXITCODE" }
    $data = Get-Content -LiteralPath (Join-Path $temp 'positive.json') -Encoding utf8 -Raw | ConvertFrom-Json
    $original = Get-Content -LiteralPath (Join-Path $temp 'original.txt') -Encoding utf8
    $alive = Get-Content -LiteralPath (Join-Path $temp 'alive.json') -Encoding utf8 -Raw | ConvertFrom-Json
    if (@($alive.exceptions).Count -eq 0 -or @($alive.exceptions)[0].thread_exited -ne $false) { throw 'Live thread evidence was dropped or claimed exited.' }
    if ($data.schema -ne 'pcpanel.serial-native-failure.v1' -or $data.session_id -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') { throw 'Invalid schema/session.' }
    $entry = @($data.exceptions)[0]
    if ($entry.type -ne $original[0] -or $entry.hresult -ne [int]$original[1] -or $entry.hresult -ne -2147024858) { throw 'Original exception type/HRESULT lost.' }
    if ($entry.stack -notmatch 'Marshal.ThrowExceptionForHR' -or $entry.stack -notmatch 'SwallowRealNativeFailure' -or -not $entry.thread_exited) { throw 'Original exception stack/thread exit evidence lost.' }
    if ($entry.native_error -ne 38 -or $entry.managed_tid -le 0 -or $entry.native_tid -le 0) { throw 'Native error/thread identity lost.' }
    if ($entry.native_site -ne 'RJCP.EvidencePositiveControl.EvidenceTest.SwallowRealNativeFailure' -or $entry.native_il_offset -lt 0) { throw 'First RJCP frame/method IL offset lost.' }
    if (Test-Path -LiteralPath (Join-Path $temp 'normal.json')) { throw 'Normal completion emitted evidence.' }
    if (Test-Path -LiteralPath (Join-Path $temp 'wrong-thread.json')) { throw 'Wrong thread emitted evidence.' }
    if (Test-Path -LiteralPath (Join-Path $temp 'empty.json')) { throw 'Normal Dispose emitted evidence.' }
    Write-Output 'PASS: real .NET Framework caught IOException/HRESULT/stack retained; exact thread filter and normal Dispose verified; no serial access.'
}
finally {
    if ((Split-Path -Parent $temp) -ne $parent) { throw 'Unexpected test cleanup target.' }
    Remove-Item -LiteralPath $temp -Recurse -Force
}
