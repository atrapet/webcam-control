using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WebcamControl
{
    // Un "job object" Windows par processus de capture lance.
    //
    // Deux raisons, decouvertes a l'usage :
    //
    // 1. Le ffmpeg trouve dans le PATH peut etre un lanceur - c'est le cas des
    //    shims Chocolatey - qui demarre le vrai binaire dans un processus enfant.
    //    Arreter le processus qu'on a lance ne tue alors que le lanceur, et
    //    l'enfant continue de tenir la camera : plus aucune application ne peut
    //    l'ouvrir. Fermer le job emporte tout l'arbre.
    //
    // 2. Si l'application est tuee ou plante pendant une mesure, la fermeture du
    //    handle par le systeme tue les membres du job. Aucun processus de capture
    //    ne peut donc survivre a l'application.
    internal sealed class ProcessJob : IDisposable
    {
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
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
        struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        IntPtr handle;

        public ProcessJob()
        {
            try { handle = Create(); }
            catch (Exception ex)
            {
                handle = IntPtr.Zero;
                Log.Write("Job object indisponible : " + ex.Message);
            }
        }

        public void Adopt(Process p)
        {
            if (handle == IntPtr.Zero || p == null) return;
            try
            {
                if (!AssignProcessToJobObject(handle, p.Handle))
                    Log.Write("Rattachement au job object refuse (code " + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex)
            {
                Log.Write("Rattachement au job object impossible : " + ex.Message);
            }
        }

        // Fermer le handle suffit : le job est configure pour tuer ses membres.
        public void Dispose()
        {
            IntPtr h = handle;
            handle = IntPtr.Zero;
            if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
        }

        static IntPtr Create()
        {
            IntPtr h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) return IntPtr.Zero;

            ExtendedLimits limits = new ExtendedLimits();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int size = Marshal.SizeOf(typeof(ExtendedLimits));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    Log.Write("Configuration du job object refusee (code " + Marshal.GetLastWin32Error() + ")");
                    CloseHandle(h);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return h;
        }
    }
}
