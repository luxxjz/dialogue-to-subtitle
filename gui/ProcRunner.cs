// ProcRunner.cs - 子进程执行 + Job Object 进程组管理
// 所有子进程放入同一 Job, 停止时 TerminateJobObject 一键全杀 (whisper/ffmpeg/python/powershell 全带走)
// 比 Process.Kill(true) 可靠: Job 是内核级绑定, 无竞态

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SrtGui
{
    public class ProcRunner : IDisposable
    {
        // ---- Win32 Job Object ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass,
            IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private IntPtr _job;
        private bool _killed;
        private readonly List<Process> _procs = new List<Process>();

        public ProcRunner()
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }

            // KILL_ON_JOB_CLOSE: 关闭句柄时连坐全杀 (防句柄泄漏后子进程残留)
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_job, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, ptr, (uint)size))
                {
                    // 设置失败不致命, 停止时仍可显式 Terminate
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        // 登记一个已启动的进程到 Job
        public void AddProcess(Process p)
        {
            if (p == null || p.HasExited) { return; }
            try { AssignProcessToJobObject(_job, p.Handle); } catch { }
            lock (_procs) { _procs.Add(p); }
        }

        // 停止: 杀 Job 内全部进程
        public void KillAll()
        {
            if (_killed) { return; }
            _killed = true;
            try { TerminateJobObject(_job, 1); } catch { }
        }

        public void Dispose()
        {
            KillAll();
            try { CloseHandle(_job); } catch { }
        }
    }
}
