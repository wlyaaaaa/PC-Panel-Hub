using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class TurzxHiddenProcessLauncher
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess,
        bool inheritHandle, uint options);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess,
        uint shareMode, ref SecurityAttributes securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    // Windows command-line quoting used by the C runtime and CommandLineToArgvW.
    public static string Quote(string value)
    {
        if (value == null) throw new ArgumentNullException("value");
        if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', slashes);
                result.Append(character);
            }
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        result.Append('"');
        return result.ToString();
    }

    public static Process Start(string executable, string[] arguments, string workingDirectory,
        string stdoutPath, string stderrPath)
    {
        if (!Path.IsPathRooted(executable)) throw new ArgumentException("Executable must be absolute.", "executable");
        var commandLine = new StringBuilder(Quote(executable));
        foreach (string argument in arguments ?? new string[0]) commandLine.Append(' ').Append(Quote(argument));

        FileStream stdout = null;
        FileStream stderr = null;
        IntPtr outputHandle = IntPtr.Zero, errorHandle = IntPtr.Zero, inputHandle = IntPtr.Zero;
        try
        {
            bool redirect = !String.IsNullOrEmpty(stdoutPath) || !String.IsNullOrEmpty(stderrPath);
            var startup = new StartupInfo();
            startup.cb = (uint)Marshal.SizeOf(typeof(StartupInfo));
            if (redirect)
            {
                stdout = String.IsNullOrEmpty(stdoutPath) ? null : new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                stderr = String.IsNullOrEmpty(stderrPath) ? null : new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                var attributes = new SecurityAttributes();
                attributes.nLength = (uint)Marshal.SizeOf(typeof(SecurityAttributes));
                attributes.bInheritHandle = true;
                inputHandle = CreateFile("NUL", GenericRead, FileShareRead | FileShareWrite,
                    ref attributes, OpenExisting, FileAttributeNormal, IntPtr.Zero);
                if (inputHandle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                outputHandle = stdout == null ? CreateNullOutput(ref attributes) : Inherit(stdout.SafeFileHandle.DangerousGetHandle());
                errorHandle = stderr == null ? CreateNullOutput(ref attributes) : Inherit(stderr.SafeFileHandle.DangerousGetHandle());
                startup.dwFlags = StartfUseStdHandles;
                startup.hStdInput = inputHandle;
                startup.hStdOutput = outputHandle;
                startup.hStdError = errorHandle;
            }

            ProcessInformation created;
            if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, redirect,
                CreateNoWindow | CreateSuspended, IntPtr.Zero, workingDirectory, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Process process = null;
            bool resumed = false;
            try
            {
                // The child cannot exit before this managed handle is secured.
                process = Process.GetProcessById(checked((int)created.dwProcessId));
                IntPtr processHandle = process.Handle;
                if (ResumeThread(created.hThread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                resumed = true;
                return process;
            }
            catch
            {
                if (!resumed) TerminateProcess(created.hProcess, 1);
                if (process != null) process.Dispose();
                throw;
            }
            finally { CloseHandle(created.hThread); CloseHandle(created.hProcess); }
        }
        finally
        {
            if (inputHandle != IntPtr.Zero && inputHandle != new IntPtr(-1)) CloseHandle(inputHandle);
            if (outputHandle != IntPtr.Zero && outputHandle != new IntPtr(-1)) CloseHandle(outputHandle);
            if (errorHandle != IntPtr.Zero && errorHandle != new IntPtr(-1)) CloseHandle(errorHandle);
            if (stdout != null) stdout.Dispose();
            if (stderr != null) stderr.Dispose();
        }
    }

    private static IntPtr CreateNullOutput(ref SecurityAttributes attributes)
    {
        IntPtr handle = CreateFile("NUL", 0x40000000, FileShareRead | FileShareWrite,
            ref attributes, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    private static IntPtr Inherit(IntPtr handle)
    {
        IntPtr copy;
        IntPtr current = GetCurrentProcess();
        if (!DuplicateHandle(current, handle, current, out copy, 0, true, DuplicateSameAccess))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return copy;
    }
}
