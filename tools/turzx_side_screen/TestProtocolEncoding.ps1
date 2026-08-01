param(
    [string]$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

$protocolFile = Join-Path $PSScriptRoot "TURZX.SideScreen.Protocol.cs"
if (-not (Test-Path -LiteralPath $protocolFile)) {
    throw "Protocol file not found: $protocolFile"
}

$dotnet = Get-Command dotnet -ErrorAction Stop
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("turzx-protocol-test-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workDir | Out-Null

try {
    $programFile = Join-Path $workDir "Program.cs"
    $projectFile = Join-Path $workDir "TestProtocolEncoding.csproj"

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>false</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$([System.Security.SecurityElement]::Escape($programFile))" />
    <Compile Include="$([System.Security.SecurityElement]::Escape($protocolFile))" Link="TURZX.SideScreen.Protocol.cs" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectFile -Encoding UTF8

    @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using TURZX.SideScreen;

internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        TestConstants();
        TestCommandPacketHeader();
        TestBrightnessCommandPacket();
        TestCommandPacketPayloadValidation();
        TestRawRgbEncoding();
        TestBitmapEncoding();
        TestFullFrameStreamWrite();
        TestParameterValidation();

        Console.WriteLine("PASS " + _passed + " protocol encoding checks");
        return 0;
    }

    private static void TestConstants()
    {
        Equal("width", 480, TurzxSideScreenProtocol.Width);
        Equal("height", 1920, TurzxSideScreenProtocol.Height);
        Equal("bytes per pixel", 4, TurzxSideScreenProtocol.BytesPerPixel);
        Equal("full frame bytes", 3686400, TurzxSideScreenProtocol.FullFrameByteCount);
        Equal("command packet bytes", 250, TurzxSideScreenProtocol.CommandPacketByteCount);
        Equal("raw chunk bytes", 24900, TurzxSideScreenProtocol.RawFrameChunkByteCount);
        Equal("brightness command", 123, TurzxSideScreenProtocol.BrightnessCommand);
    }

    private static void TestBrightnessCommandPacket()
    {
        byte[] packet = TurzxSideScreenProtocol.BuildBrightnessPacket(170);

        Equal("brightness packet length", 250, packet.Length);
        Equal("brightness packet command", 123, packet[0]);
        Equal("brightness packet magic high", 0xEF, packet[1]);
        Equal("brightness packet magic low", 0x69, packet[2]);
        Equal("brightness packet declared length", 1, packet[6]);
        Equal("brightness packet extra", 0, packet[7]);
        Equal("brightness packet payload", 170, packet[10]);
        Equal("brightness packet trailing zero", 0, packet[11]);

        using (MemoryStream stream = new MemoryStream())
        {
            TurzxSideScreenProtocol.WriteBrightness(stream, 0);
            byte[] written = stream.ToArray();
            Equal("brightness write length", 250, written.Length);
            Equal("brightness off command", 123, written[0]);
            Equal("brightness off payload", 0, written[10]);
        }
    }

    private static void TestCommandPacketHeader()
    {
        byte[] packet = TurzxSideScreenProtocol.BuildCommandPacket(
            TurzxSideScreenProtocol.FullFrameCommand,
            TurzxSideScreenProtocol.FullFrameByteCount,
            null,
            0);

        Equal("command packet length", 250, packet.Length);
        Equal("command", 200, packet[0]);
        Equal("magic high", 0xEF, packet[1]);
        Equal("magic low", 0x69, packet[2]);
        Equal("length b0", 0x00, packet[3]);
        Equal("length b1", 0x38, packet[4]);
        Equal("length b2", 0x40, packet[5]);
        Equal("length b3", 0x00, packet[6]);
        Equal("extra", 0, packet[7]);
        Equal("reserved 8", 0, packet[8]);
        Equal("reserved 9", 0, packet[9]);
    }

    private static void TestCommandPacketPayloadValidation()
    {
        byte[] payload = new byte[] { 0xAA, 0xBB, 0xCC };
        byte[] packet = TurzxSideScreenProtocol.BuildCommandPacket(204, payload.Length, payload, 7);

        Equal("payload command", 204, packet[0]);
        Equal("payload extra", 7, packet[7]);
        Equal("payload byte 0", 0xAA, packet[10]);
        Equal("payload byte 1", 0xBB, packet[11]);
        Equal("payload byte 2", 0xCC, packet[12]);
        Equal("payload trailing zero", 0, packet[13]);

        Throws<ArgumentException>("payload too large", delegate
        {
            TurzxSideScreenProtocol.BuildCommandPacket(204, 241, new byte[241], 0);
        });
    }

    private static void TestRawRgbEncoding()
    {
        byte[] rgb = new byte[TurzxSideScreenProtocol.Width * TurzxSideScreenProtocol.Height * 3];
        rgb[0] = 255;
        rgb[1] = 1;
        rgb[2] = 2;
        rgb[3] = 3;
        rgb[4] = 255;
        rgb[5] = 4;

        byte[] frame = TurzxSideScreenProtocol.EncodeRaw24(rgb, TurzxSourcePixelOrder.Rgb);
        Equal("raw rgb frame length", TurzxSideScreenProtocol.FullFrameByteCount, frame.Length);
        Equal("rgb pixel 0 b", 2, frame[0]);
        Equal("rgb pixel 0 g", 1, frame[1]);
        Equal("rgb pixel 0 r", 255, frame[2]);
        Equal("rgb pixel 0 a", 255, frame[3]);
        Equal("rgb pixel 1 b", 4, frame[4]);
        Equal("rgb pixel 1 g", 255, frame[5]);
        Equal("rgb pixel 1 r", 3, frame[6]);
        Equal("rgb pixel 1 a", 255, frame[7]);

        byte[] bgr = new byte[TurzxSideScreenProtocol.Width * TurzxSideScreenProtocol.Height * 3];
        bgr[0] = 9;
        bgr[1] = 8;
        bgr[2] = 7;
        frame = TurzxSideScreenProtocol.EncodeRaw24(bgr, TurzxSourcePixelOrder.Bgr);
        Equal("bgr pixel b", 9, frame[0]);
        Equal("bgr pixel g", 8, frame[1]);
        Equal("bgr pixel r", 7, frame[2]);
        Equal("bgr pixel a", 255, frame[3]);
    }

    private static void TestBitmapEncoding()
    {
        using (Bitmap bitmap = new Bitmap(
            TurzxSideScreenProtocol.Width,
            TurzxSideScreenProtocol.Height,
            PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(0, 0, 0, 0));
            bitmap.SetPixel(0, 0, Color.FromArgb(255, 255, 1, 2));
            bitmap.SetPixel(1, 0, Color.FromArgb(255, 3, 255, 4));
            bitmap.SetPixel(0, 1, Color.FromArgb(255, 5, 6, 255));
            bitmap.SetPixel(479, 1919, Color.FromArgb(255, 30, 31, 32));

            byte[] frame = TurzxSideScreenProtocol.EncodeBitmap(bitmap);
            Equal("bitmap frame length", TurzxSideScreenProtocol.FullFrameByteCount, frame.Length);
            AssertBytes("bitmap 0,0", frame, 0, 2, 1, 255, 255);
            AssertBytes("bitmap 1,0", frame, 4, 4, 255, 3, 255);
            AssertBytes("bitmap 0,1", frame, TurzxSideScreenProtocol.Width * 4, 255, 6, 5, 255);
            AssertBytes("bitmap 479,1919", frame, TurzxSideScreenProtocol.FullFrameByteCount - 4, 32, 31, 30, 255);
        }
    }

    private static void TestFullFrameStreamWrite()
    {
        byte[] frame = new byte[TurzxSideScreenProtocol.FullFrameByteCount];
        frame[0] = 0x11;
        frame[1] = 0x22;
        frame[frame.Length - 1] = 0xEE;

        using (MemoryStream stream = new MemoryStream())
        {
            TurzxSideScreenProtocol.WriteFullFrame(stream, frame, false);
            byte[] written = stream.ToArray();
            Equal("full write length", TurzxSideScreenProtocol.CommandPacketByteCount + frame.Length, written.Length);
            Equal("full write command", 200, written[0]);
            Equal("full write raw 0", 0x11, written[250]);
            Equal("full write raw 1", 0x22, written[251]);
            Equal("full write raw last", 0xEE, written[written.Length - 1]);
        }

        using (MemoryStream stream = new MemoryStream())
        {
            TurzxSideScreenProtocol.WriteFullFrame(stream, frame, true);
            Equal("alternate command", 202, stream.ToArray()[0]);
        }

        Equal("full frame chunk count", 149, TurzxSideScreenProtocol.GetRawFrameChunkCount(frame.Length));
        Equal("exact chunk count", 2, TurzxSideScreenProtocol.GetRawFrameChunkCount(TurzxSideScreenProtocol.RawFrameChunkByteCount + 1));
    }

    private static void TestParameterValidation()
    {
        Throws<ArgumentNullException>("null bitmap", delegate { TurzxSideScreenProtocol.EncodeBitmap(null); });

        using (Bitmap wrong = new Bitmap(2, 2))
        {
            Throws<ArgumentException>("wrong bitmap size", delegate { TurzxSideScreenProtocol.EncodeBitmap(wrong); });
        }

        Throws<ArgumentException>("wrong raw length", delegate
        {
            TurzxSideScreenProtocol.EncodeRaw24(new byte[3], TurzxSourcePixelOrder.Rgb);
        });

        Throws<ArgumentException>("wrong frame length", delegate
        {
            TurzxSideScreenProtocol.WriteFullFrame(new MemoryStream(), new byte[4], false);
        });

        Throws<NotSupportedException>("diff todo", delegate
        {
            TurzxSideScreenProtocol.WriteDifferentialFrame(new MemoryStream(), new byte[4], new byte[4], 0);
        });
    }

    private static void Equal(string name, int expected, int actual)
    {
        if (expected != actual)
        {
            throw new Exception(name + ": expected " + expected + ", got " + actual);
        }

        _passed++;
    }

    private static void AssertBytes(string name, byte[] data, int offset, byte b0, byte b1, byte b2, byte b3)
    {
        Equal(name + " byte0", b0, data[offset]);
        Equal(name + " byte1", b1, data[offset + 1]);
        Equal(name + " byte2", b2, data[offset + 2]);
        Equal(name + " byte3", b3, data[offset + 3]);
    }

    private static void Throws<T>(string name, Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            _passed++;
            return;
        }

        throw new Exception(name + ": expected " + typeof(T).Name);
    }
}
'@ | Set-Content -LiteralPath $programFile -Encoding UTF8

    & $dotnet.Source run --project $projectFile --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
