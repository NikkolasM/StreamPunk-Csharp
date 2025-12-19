using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamPunk.Threading
{
    // will be for sched_setaffinity() in the C standard lib in linux
    struct LinuxThread
    {
        public readonly int pid; 

        public LinuxThread(int pid)
        {
            this.pid = pid;
        }
    }

    // will be for SetThreadAffinityMask() in Win32
    struct WindowsThread
    {
        // will convert the pid into the affinity mask for a thread
        public readonly int pid;

        //
        public readonly int[] affinityMask;

        public WindowsThread(int pid)
        {
            this.pid = pid;
            this.affinityMask = new int[]{ -1 };
        }

        public WindowsThread(int[] affinityMask)
        {
            this.affinityMask = affinityMask;
            this.pid = -1;
        }

    }
    struct Thread 
    {
        private readonly System.Threading.Thread? SystemThread;

        // What you would use in prod if on a Linux-based system
        public Thread(System.Threading.ThreadStart start, LinuxThread thread)
        {
            System.Threading.ThreadStart wrapper = () => {
                // INSERT HERE: routine to execute a C module call that makes a syscall to pin the thread at the kernel level.

                start();
            };

            this.SystemThread = new System.Threading.Thread(start: wrapper);
        }

        // Used for more advanced usecases on Linux-based systems + testing
        // The action syscall will wrap the actual call to your .so of choice
        // you still use the linux thread type interface though
        public Thread(System.Threading.ThreadStart start, LinuxThread thread, Action<LinuxThread> syscall)
        {
            System.Threading.ThreadStart wrapper = () =>
            {
                syscall(thread);
                start();
            };

            this.SystemThread = new System.Threading.Thread(start: wrapper);
        }

        // What you would use in prod if on a Windows-based system
        public Thread(System.Threading.ThreadStart start, WindowsThread thread)
        {
            System.Threading.ThreadStart wrapper = () => {
                // INSERT HERE: routine to execute a C module call that makes a syscall to pin the thread at the kernel level.

                start();
            };

            this.SystemThread = new System.Threading.Thread(start: wrapper);
        }

        // Used for more advanced usecases on Windows-based systems + testing
        // The action syscall will wrap the actual call to your .dll of choice
        public Thread(System.Threading.ThreadStart start, WindowsThread thread, Action<WindowsThread> syscall)
        {
            System.Threading.ThreadStart wrapper = () =>
            {
                syscall(thread);
                start();
            };

            this.SystemThread = new System.Threading.Thread(start: wrapper);
        }
    }
}
