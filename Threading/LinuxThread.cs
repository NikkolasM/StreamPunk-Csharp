using StreamPunk.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamPunk.Threading.Linux
{
    public partial struct Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        // The values passed back via 'out' arguments will auto cleanup the underlying 
        // 
        [LibraryImport("PinThreadLinux.so")]
        public static partial int PinThread(
            in ulong[] suppliedAffinityMask,
            ulong suppliedMaskLength,
            out int tid,
            // For passing an array on the heap allocated in the C back to the C# safely
            // 4 for 'SizeParamIndex' represents the 'appliedMaskLength' arg on a 0-based index.
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)]
            out ulong[] appliedAffinityMask,
            out ulong appliedMaskLength
            );

        [LibraryImport("UnpinThreadLinux.so")]
        public static partial int UnpinThread();
    }
    public struct Affinity
    {
        // The pid (thread ID) will be fetched from within the execution context within the lib function being called

        // an arbitrarily long bitmask
        public readonly ulong[] affinityMask;
        public Affinity(ulong[] affinityMask)
        {
            this.affinityMask = affinityMask;
        }
    }

    // important so that the calling thread of 'Start()' can do a simple spinlock with a timeout.
    // awaiting the task context directly will screw things up.
    internal class BootstrapState
    {
        // use volatile keyword so that access to the class instance isn't cached by the VM
        // in tight loops i.e. spinlocks in 'Start()'
        private volatile bool isBootstrapped;
        private volatile bool hasFailed;
        private volatile Exception? e;

        public BootstrapState()
        {
            this.isBootstrapped = false;
            this.hasFailed = false;
            this.e = null;
        }
        public bool GetIsBootstrapped() { return this.isBootstrapped; }
        public void SetIsBootstrapped(bool isBootstrapped) { this.isBootstrapped = isBootstrapped; }
        public bool GetHasFailed() { return this.hasFailed; }
        public void SetHasFailed(bool hasFailed) { this.hasFailed = hasFailed; }
        public Exception? GetException() { return this.e; }
        public void SetException(Exception? e) { this.e = e; }
    }

    public struct Thread<StartContext>
    {
        public readonly Affinity affinity;
        private int tid;

        private System.Threading.Thread? SystemThread;

        public Thread(Affinity affinity)
        {
            this.affinity = affinity;
            this.tid = 0;
        }

        private void SetTid(int tid)
        {
            this.tid = tid;
        }
        public int GetTid()
        {
            return this.tid;
        }

        private int PinThread()
        {
            // take the affinity and run the native func using it.
            // also write to update the tid.
        }

        private int UnpinThread()
        {

        }

        private System.Threading.Thread? GetThread()
        {
            return this.SystemThread;
        }

        public int Start(StartContext context, Action<StartContext, System.Threading.Thread> action)
        {
            // The thread instance can be reused, but requires the current thread to not be running or blocked for some reason.
            // Requires aborting the current thread using the yet-to-be-made 'Abort()' method. 
            if (this.SystemThread != null)
            {
                System.Threading.ThreadState threadState = this.SystemThread.ThreadState;
                bool isWaitSleepJoin = threadState == System.Threading.ThreadState.WaitSleepJoin;
                bool isRunning = threadState == System.Threading.ThreadState.Running;

                if (isRunning || isWaitSleepJoin) throw new ThreadStateException($"Invalid ThreadState for Start(). ThreadState={threadState}");
            }

            Func<System.Threading.Thread?> getThread = this.GetThread;
            Func<int> pinThread = this.PinThread;
            Func<int> unpinThread = this.UnpinThread;
            int tid = 0;

            // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such
            BootstrapState bss = new BootstrapState();

            this.SystemThread = new System.Threading.Thread(() =>
            {
                try
                {
                    if (bss.GetHasFailed()) return;

                    tid = pinThread();
                    if (bss.GetHasFailed()) return;

                    System.Threading.Thread? currContextThread = getThread();
                    if (bss.GetHasFailed()) return;

                    if (currContextThread == null)
                    {
                        if (bss.GetHasFailed()) return;

                        bss.SetException(new FailedToGetThreadException(message: "currContextThread=null"));
                        bss.SetHasFailed(true);

                        return;
                    }

                    int pinThreadOutcomeCode = pinThread();

                    if (pinThreadOutcomeCode < 0)
                    {
                        int unpinThreadOutcomeCode = unpinThread();

                        if (unpinThreadOutcomeCode < 0)
                        {

                        }
                        else
                        {
                            
                        }
                    }

                    bss.SetIsBootstrapped(true);
                    if (bss.GetHasFailed()) return;

                    action(context, currContextThread);
                }
                catch (Exception e)
                {
                    bss.SetException(new ThreadRuntimeException(message: "Thread runtime exception.", innerException: e));
                    bss.SetHasFailed(true);
                }
            });

            this.SystemThread.Start();

            // use a spin lock to check bootstrap state.
            // implement a simple timeout mechanism rather than blocking using existing abstraction.
            // Doing all this so that the calling thread isn't preempted. using async/await will otherwise do that.
            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (sw.ElapsedMilliseconds <= 3000L)
            {
                bool hasFailed = bss.GetHasFailed();

                if (hasFailed)
                {
                    throw new ThreadBootstrapFailedException(message: null, innerException: bss.GetException());
                }

                bool isBootstrapped = bss.GetIsBootstrapped();

                if (isBootstrapped)
                {
                    if (tid <= 0)
                    {
                        throw new InvalidThreadIdException(message: $"Invalid Linux Thread ID. tid={tid}");
                    }
                    else
                    {
                        return tid;
                    }
                }
            }

            bss.SetHasFailed(true);

            throw new ThreadBootstrapFailedException(message: "Bootstrap timed out.");
        }
    }
    class TrySetResultFailedException : Exception
    {
        public TrySetResultFailedException() { }
        public TrySetResultFailedException(string message) : base(message) { }
        public TrySetResultFailedException(string? message, Exception? innerException) { }
    }
    class InvalidThreadIdException : Exception
    {
        public InvalidThreadIdException() { }
        public InvalidThreadIdException(string message) : base(message) { }
        public InvalidThreadIdException(string? message, Exception? innerException) : base(message, innerException) { }
    }
    class ThreadBootstrapFailedException : Exception
    {
        public ThreadBootstrapFailedException() { }
        public ThreadBootstrapFailedException(string message) : base(message) { }
        public ThreadBootstrapFailedException(string? message, Exception? innerException) : base(message, innerException) { }
    }
}