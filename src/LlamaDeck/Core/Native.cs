using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LlamaDeck.Core;

/// <summary>Win32-anrop för minnesstatistik och RAM-vakt (jobbobjekt).</summary>
public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    public static (ulong Idle, ulong Kernel, ulong User) GetCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return (0, 0, 0);
        return (idle, kernel, user);
    }

    public static (double TotalGb, double AvailableGb, double UsedGb) GetRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return (0, 0, 0);
        const double gb = 1024d * 1024d * 1024d;
        return (mem.ullTotalPhys / gb, mem.ullAvailPhys / gb, (mem.ullTotalPhys - mem.ullAvailPhys) / gb);    }

    // ---- Jobbobjekt: dödar barnprocesser när appen stängs och kan sätta RAM-tak ----

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

    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass,
        IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    public static void AttachConsoleForOutput()
    {
        if (!AttachConsole(0xFFFFFFFF)) AllocConsole();
    }

    /// <summary>Jobbobjekt som håller koll på serverprocessen.</summary>
    public sealed class Job : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public string? Error { get; private set; }

        public static Job? Create(long? memoryLimitBytes)
        {
            var h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) return null;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (memoryLimitBytes is > 0)
            {
                info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_JOB_MEMORY;
                info.JobMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes.Value;
            }

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    var err = Marshal.GetLastWin32Error();
                    CloseHandle(h);
                    return new Job { Handle = IntPtr.Zero, Error = $"SetInformationJobObject misslyckades ({err})" };
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return new Job { Handle = h };
        }

        public bool Assign(Process process)
        {
            if (Handle == IntPtr.Zero) return false;
            try { return AssignProcessToJobObject(Handle, process.Handle); }
            catch { return false; }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    // ---- Mörk titelrad på Windows 10/11 ----
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int value = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch
        {
            // äldre Windows – ignorera
        }
    }

    // ---- Disktyp: snurrar disken? (avgör om mmap är dyrt eller billigt) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize, ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer, int nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);


    private static readonly Dictionary<char, bool?> _seekPenalty = new();

    /// <summary>True om enheten är mekanisk (söktid), false för SSD/NVMe, null om det inte gick att avgöra.</summary>
    public static bool? DriveIncursSeekPenalty(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length < 2) return null;
            var letter = char.ToUpperInvariant(root[0]);
            lock (_seekPenalty)
            {
                if (_seekPenalty.TryGetValue(letter, out var cached)) return cached;
                bool? result = null;
                const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
                var handle = CreateFileW($@"\\.\{letter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    try
                    {
                        var query = new STORAGE_PROPERTY_QUERY { PropertyId = 7, QueryType = 0 };
                        var desc = new DEVICE_SEEK_PENALTY_DESCRIPTOR { Version = 1, Size = 12 };
                        if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref query,
                                Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), ref desc,
                                Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(), out _, IntPtr.Zero))
                        {
                            result = desc.IncursSeekPenalty;
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
                _seekPenalty[letter] = result;
                return result;
            }
        }
        catch
        {
            return null;
        }
    }
}
