using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamPunk.Threading
{
    struct LinuxThread
    {
        public readonly int pid; 

        public LinuxThread(int pid)
        {
            this.pid = pid;
        }
    }

    struct WindowsThread
    {
        // INSERT HERE: add members for defining windows thread affinity data
    }
    struct Thread 
    {
        private readonly System.Threading.Thread? SystemThread;
        public Thread(System.Threading.ThreadStart start, LinuxThread thread)
        {
            LinuxThread linuxThread = thread;
            System.Threading.ThreadStart wrapper = () => {
                // INSERT HERE: routine to execute a C module call that makes a syscall to pin the thread.

                start();
            };

            this.SystemThread = new System.Threading.Thread(start: wrapper);
        }

        public Thread(System.Threading.ThreadStart start, WindowsThread thread)
        {
            System.Threading.ThreadStart wrapper = () => {
                // INSERT HERE: routine to execute a C module call that makes a syscall to pin the thread.

                start();
            };

            this.SystemThread = new System.Threading.Thread(start: wrapper);
        }
    }
}
