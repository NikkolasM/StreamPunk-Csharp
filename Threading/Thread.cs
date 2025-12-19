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
        public readonly int tid; 

        public LinuxThread(int tid)
        {
            this.tid = tid;
        }
    }

    // will be for SetThreadAffinityMask() in Win32
    struct WindowsThread
    {
        // can only have either-or, not both vvv
        // Non-selected members are alloc'ed to hold -1 in some fashion.

        // will convert the pid into the affinity mask for a thread
        public readonly int? tid;

        // or you can use the affinity mask API 1-for-1.
        public readonly int[]? affinityMask;

        public WindowsThread(int tid)
        {
            this.tid = tid;
            this.affinityMask = null;
        }

        public WindowsThread(int[] affinityMask)
        {
            this.affinityMask = affinityMask;
            this.tid = null;
        }

    }
    struct Thread<StartObj>
    {
        private System.Threading.Thread? SystemThread;

        // These are public to simplify testing
        public readonly LinuxThread? LinuxThread;
        public readonly WindowsThread? WindowsThread;
        public readonly Action? ThreadPinningRoutine;

        // *** CONSTRUCTORS ***

        // What you would use in prod if on a Linux-based system
        public Thread(LinuxThread thread)
        {
            this.LinuxThread = thread;
            this.WindowsThread = null;
            this.ThreadPinningRoutine = null;
        }

        // What you would use in prod if on a Windows-based system
        public Thread(WindowsThread thread)
        {
            this.LinuxThread = null;
            this.WindowsThread = thread;
            this.ThreadPinningRoutine = null;
        }

        // Used for more advanced usecases on Windows-based systems + testing
        // The action syscall will wrap the actual call to your .dll of choice
        public Thread(Action threadPinningRoutine)
        {
            this.LinuxThread = null;
            this.WindowsThread = null;
            this.ThreadPinningRoutine = threadPinningRoutine;
        }

        // *** APIs *** 

        // For starting the thread.
        //
        // Allowed to reuse the Thread instance if the underlying system thread fails for some reason.
        // Just ensure that you resupply the lambda representing the Thread execution context (start) along with 
        // the start object.
        public void Start(StartObj obj, Action<StartObj> start)
        {
            // to ensure that start can only execute if the existing thread (if any) isn't running currently.
            if (this.SystemThread != null)
            {
                ThreadState threadState = this.SystemThread.ThreadState;
                bool isWaitSleepJoin = threadState == ThreadState.WaitSleepJoin;
                bool isRunning = threadState == ThreadState.Running;

                if (isRunning || isWaitSleepJoin)
                {
                    throw new ThreadStateException($"Invalid ThreadState for Start(). ThreadState={threadState}");
                }
            }

            // to fix context issues in the lambda below. Only one of these values will be not-null, since 
            // there are dedicated constructors for each above.
            LinuxThread? linuxThread = this.LinuxThread;
            WindowsThread? windowsThread = this.WindowsThread;
            Action? threadPinningRoutine = this.ThreadPinningRoutine;

            this.SystemThread = new System.Threading.Thread(() => { 
                try
                {
                    // to make sure the VM doesn't move work onto different kernel threads, even though
                    // we are pinning the kernel thread in the given scenario. 
                    System.Threading.Thread.BeginThreadAffinity();  

                    if (linuxThread != null)
                    {
                        // routine for thread pinning on Linux
                    }
                    else if (windowsThread != null)
                    {
                        // routine for thread pinning on Windows
                    }
                    else if (threadPinningRoutine != null)
                    {
                        // executing the supplied threadPinningRoutine
                    }
                    else
                    {
                        throw new NoThreadPinningRoutineException("Failed to pin thread.");
                    }

                    start(obj);

                } catch (Exception e)
                {
                    System.Threading.Thread.EndThreadAffinity();

                    throw new Exception(message: "Thread exception.", innerException: e);
                }
            });

            this.SystemThread.Start();
        }

    }
    class NoThreadPinningRoutineException : Exception { 
        public NoThreadPinningRoutineException() { }
        public NoThreadPinningRoutineException(string message) : base(message) { }
    }
}
