using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RepoTracker.Desktop.TestHost.Performance;

/// <summary>Windows retains CPU accounting for exited descendants, including short-lived git.exe processes.</summary>
internal sealed class JobAccounting : IDisposable
{
    private readonly nint handle;
    public JobAccounting()
    {
        handle = CreateJobObjectW(0, null);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var self = Process.GetCurrentProcess();
        if (!AssignProcessToJobObject(handle, self.Handle))
        { var error = Marshal.GetLastWin32Error(); CloseHandle(handle); throw new Win32Exception(error); }
    }
    public JobSample Read()
    {
        if (!QueryInformationJobObject(handle, 1, out Accounting info, Marshal.SizeOf<Accounting>(), 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new((info.TotalUserTime + info.TotalKernelTime) / 10000.0, info.TotalProcesses, info.ActiveProcesses);
    }
    public long ActiveDescendantWorkingSet()
    {
        var buffer = Marshal.AllocHGlobal(8 + 256 * nint.Size);
        try
        {
            if (!QueryProcessIds(handle, 3, buffer, 8 + 256 * nint.Size, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = Marshal.ReadInt32(buffer, 4);
            long total = 0;
            for (var index = 0; index < count; index++)
            {
                var pid = (int)Marshal.ReadIntPtr(buffer, 8 + index * nint.Size);
                if (pid == Environment.ProcessId) continue;
                try { using var process = Process.GetProcessById(pid); total += process.WorkingSet64; }
                catch (ArgumentException) { } // Process exited between the job snapshot and open.
                catch (InvalidOperationException) { }
            }
            return total;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(nint job, int infoClass, out Accounting info, int length, nint returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryProcessIds(nint job, int infoClass, nint info, int length, nint returned);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
internal sealed record JobSample(double CpuMs, uint TotalProcesses, uint ActiveProcesses);
