using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameStream.Server.Sandbox;

/// <summary>
/// Job Object do Windows. O que ele faz hoje:
///  - mata o jogo e todos os processos filhos quando o job é fechado;
///  - limita a CPU (afinidade de núcleos) e a memória total do jogo;
///  - bloqueia o jogo de mexer em clipboard, resolução, desligar o Windows, trocar de desktop etc.
/// NÃO isola arquivos nem rede. Isso é a próxima etapa (token restrito / AppContainer).
/// </summary>
internal sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    public JobObject(ResourceLimits limits)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        ext.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_AFFINITY | JOB_OBJECT_LIMIT_JOB_MEMORY;

        // Núcleos que o jogo (e seus filhos) podem usar.
        ext.BasicLimitInformation.Affinity = (UIntPtr)limits.GameMask;

        // Memória confirmada (commit) somada de todos os processos do job.
        ext.JobMemoryLimit = (UIntPtr)((ulong)limits.MemoryMb * 1024UL * 1024UL);
        SetInfo(JobObjectExtendedLimitInformation, ext);

        SetInfo(JobObjectBasicUIRestrictions, new JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass = UILIMIT_DESKTOP | UILIMIT_DISPLAYSETTINGS | UILIMIT_EXITWINDOWS |
                                  UILIMIT_GLOBALATOMS | UILIMIT_READCLIPBOARD | UILIMIT_SYSTEMPARAMETERS |
                                  UILIMIT_WRITECLIPBOARD
        });
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle); // KILL_ON_JOB_CLOSE encerra tudo que estiver no job
            _handle = IntPtr.Zero;
        }
    }

    private void SetInfo<T>(int infoClass, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            if (!SetInformationJobObject(_handle, infoClass, ptr, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ---- P/Invoke ----
    private const int JobObjectBasicUIRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;

    private const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private const uint UILIMIT_DISPLAYSETTINGS = 0x00000010;
    private const uint UILIMIT_EXITWINDOWS = 0x00000080;
    private const uint UILIMIT_DESKTOP = 0x00000040;
    private const uint UILIMIT_GLOBALATOMS = 0x00000020;
    private const uint UILIMIT_READCLIPBOARD = 0x00000002;
    private const uint UILIMIT_SYSTEMPARAMETERS = 0x00000008;
    private const uint UILIMIT_WRITECLIPBOARD = 0x00000004;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
