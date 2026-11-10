using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using X4MP.Launcher.Core;

namespace X4MP.Launcher;

/// <summary>
/// Starts <c>x4mp-server.exe</c> without a window and keeps it in a Job Object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so a launcher crash cannot leave an orphaned server behind. The same
/// creation flags as the mod's <c>server_process.cpp</c> are used, including a new process group, so a
/// <c>CTRL_BREAK_EVENT</c> reaches the server's own graceful shutdown and lets it save the session.
/// </summary>
internal sealed class WindowsServerProcess : ILocalServerProcess
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;
    private const uint CtrlBreakEvent = 1;
    private const uint WaitObject0 = 0;
    private const uint StillActive = 259;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _process;
    private IntPtr _job;
    private int _pid;

    public int Id => _pid;

    public bool HasExited
    {
        get
        {
            if (_process == IntPtr.Zero)
            {
                return true;
            }

            return WaitForSingleObject(_process, 0) == WaitObject0;
        }
    }

    public int? ExitCode
    {
        get
        {
            if (_process == IntPtr.Zero || !GetExitCodeProcess(_process, out var code) || code == StillActive)
            {
                return null;
            }

            return unchecked((int)code);
        }
    }

    public void Start(LocalServerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!HasExited)
        {
            throw new InvalidOperationException("The local server is already running.");
        }

        CloseProcess();
        Directory.CreateDirectory(plan.DataDirectory);
        Directory.CreateDirectory(plan.LogDirectory);

        EnsureJob();
        var commandLine = new StringBuilder(Quote(plan.ExePath));
        foreach (var argument in plan.Arguments)
        {
            commandLine.Append(' ').Append(Quote(argument));
        }

        var environment = BuildEnvironment(plan);
        var environmentBlock = Marshal.StringToHGlobalUni(environment);
        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = (int)StartfUseShowWindow,
            wShowWindow = SwHide,
        };

        try
        {
            if (!CreateProcessW(
                    plan.ExePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateNoWindow | CreateNewProcessGroup | CreateUnicodeEnvironment,
                    environmentBlock,
                    plan.WorkingDirectory,
                    ref startup,
                    out var process))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The local server could not be started. See " + plan.LogDirectory + ".");
            }

            CloseHandle(process.hThread);
            _process = process.hProcess;
            _pid = process.dwProcessId;

            if (!AssignProcessToJobObject(_job, _process))
            {
                // A parent job that forbids nesting can refuse the assignment (pre-Windows 8, or a locked-down host).
                // The server still runs and still stops normally; only the crash guarantee is missing. This mirrors
                // mod/native/features/server/server_process.cpp instead of refusing to host at all.
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    public bool StopProcess(TimeSpan grace)
    {
        if (HasExited)
        {
            return true;
        }

        // The launcher is a GUI process and normally has no console. Attach to the server's hidden console and
        // send CTRL_BREAK to its process group; the server treats it as the graceful stop the mod also uses.
        var attached = AttachConsole((uint)_pid);
        if (!attached)
        {
            FreeConsole();
            attached = AttachConsole((uint)_pid);
        }

        if (attached)
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)_pid);
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }

        var milliseconds = (uint)Math.Clamp(grace.TotalMilliseconds, 0, int.MaxValue);
        if (WaitForSingleObject(_process, milliseconds) == WaitObject0)
        {
            return true;
        }

        TerminateProcess(_process, 1);
        _ = WaitForSingleObject(_process, 5000);
        return HasExited;
    }

    public void Dispose()
    {
        // Closing the job kills a server that is somehow still running. This is the orphan guarantee, and it is
        // also why the window asks before it closes while X4 is still connected.
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        CloseProcess();
    }

    private void EnsureJob()
    {
        if (_job != IntPtr.Zero)
        {
            return;
        }

        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The launcher could not create a Job Object.");
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
                _job,
                JobObjectExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The launcher could not configure its Job Object.");
        }
    }

    private void CloseProcess()
    {
        if (_process != IntPtr.Zero)
        {
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }

        _pid = 0;
    }

    private static string BuildEnvironment(LocalServerPlan plan)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && key.Length > 0 && key[0] != '=')
            {
                values[key] = value;
            }
        }

        foreach (var pair in plan.Environment)
        {
            values[pair.Key] = pair.Value;
        }

        var block = new StringBuilder();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        // StringToHGlobalUni adds the second terminating null the environment block needs.
        return block.ToString();
    }

    /// <summary>The Windows command-line quoting rules, including the backslash-before-quote case.</summary>
    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability",
        "CA1838:Avoid 'StringBuilder' parameters for P/Invokes",
        Justification = "CreateProcessW requires a mutable, null-terminated command-line buffer.")]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
