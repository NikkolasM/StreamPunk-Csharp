using StreamPunk.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace StreamPunk.Threading.Linux
{
    public partial class Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        // The values passed back via 'out' arguments will auto cleanup the underlying 
        // 
        [LibraryImport("PinThreadLinux.so")]
        public static partial int PinThreadUnsafe(
            in ulong[] suppliedAffinityMask,
            ulong suppliedMaskLength,
            out int tid,
            // For passing an array on the heap allocated in the C back to the C# safely
            // 4 for 'SizeParamIndex' represents the 'appliedMaskLength' arg on a 0-based index.
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)]
            out ulong[] appliedAffinityMask,
            out ulong appliedMaskLength
            );

        public static int PinThread(ulong[] affinityMask, out int tid, out ulong[] appliedAffinityMask) 
        {
            if (affinityMask.Length <= 0) throw new ArgumentException("Supplied affinity mask needs atleast one element.");

            bool hasAtleastOneMarkedCore = false;

            foreach (ulong element in affinityMask) if (element > 0UL) hasAtleastOneMarkedCore = true;

            if (!hasAtleastOneMarkedCore) throw new ArgumentException("Supplied affinity mask needs atleast one marked core.");

            int outcomeCode = Native.PinThreadUnsafe(
             suppliedAffinityMask: affinityMask,
             suppliedMaskLength: (ulong)affinityMask.Length,
             tid: out int id,
             appliedAffinityMask: out ulong[] aam,
             appliedMaskLength: out _ // because its just for a passed back property from the C so C# knows the length of 'appliedAffinityMask'
             );

            tid = id;
            appliedAffinityMask = aam;

            return outcomeCode;
        }

        [LibraryImport("UnpinThreadLinux.so")]
        public static partial int UnpinThreadUnsafe();
    }
    public class Affinity
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

        public BootstrapState()
        {
            this.isBootstrapped = false;
            this.hasFailed = false;
        }

        // encapsulation good, which can allow a WAL-like log for the CRUD going on here potentially
        public bool GetIsBootstrapped() { return this.isBootstrapped; }
        public void SetIsBootstrapped(bool isBootstrapped) { this.isBootstrapped = isBootstrapped; }
        public bool GetHasFailed() { return this.hasFailed; }
        public void SetHasFailed(bool hasFailed) { this.hasFailed = hasFailed; }
    }

    public class Thread<StartState>
    {
        public readonly Affinity affinity;
        public readonly long timeoutMs;

        // make these two volatile, because the Thread instance may be reused. 
        private volatile int tid;
        private volatile System.Threading.Thread? SystemThread;
        public Thread(Affinity affinity, long timeoutMs = 500L)
        {
            this.affinity = affinity;
            this.timeoutMs = timeoutMs;
            this.tid = 0;
            this.SystemThread = null;

        }
        private System.Threading.Thread? GetThread()
        {
            return this.SystemThread;
        }
        public int GetTid()
        {
            return this.tid;
        }
        private void SetTid(int tid)
        {
            this.tid = tid;
        }
        public bool GetIsBackground()
        {
            if (this.SystemThread == null) throw new ThreadNotFoundException();

            return this.SystemThread.IsBackground;
        }
        public void SetIsBackground()
        {
            if (this.SystemThread == null) throw new ThreadNotFoundException();

            this.SystemThread.IsBackground = true;
        }
        public int Start(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
        {
            try
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

                var self = this;
                int tid = 0;

                BootstrapState bss = new BootstrapState(); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                this.SystemThread = new System.Threading.Thread(() =>
                {
                    System.Threading.Thread? thread = self.GetThread();

                    try
                    {
                        if (bss.GetHasFailed() || ct.IsCancellationRequested) return; // for timeout and cancellation only

                        if (thread == null) throw new FailedToGetThreadException(message: "currContextThread=null");

                        int pinThreadOutcomeCode = Native.PinThread(self.affinity.affinityMask, out tid, out ulong[] appliedAffinityMask);

                        if (pinThreadOutcomeCode < 0) throw new FailedToPinThreadException($"pinThreadOutcomeCode={pinThreadOutcomeCode}");

                        if (bss.GetHasFailed() || ct.IsCancellationRequested)
                        { 
                            // for timeout only. If unpin doesn't work, then it should terminate the entire program.

                            int unpinThreadOutcomeCode = Native.UnpinThreadUnsafe();

                            if (unpinThreadOutcomeCode < 0) throw new FailedToUnpinThreadException($"unpinThreadOutcomecode={unpinThreadOutcomeCode}");

                            return;
                        }

                        self.SetTid(tid);
                        bss.SetIsBootstrapped(true);
                    }
                    catch (Exception e)
                    {
                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                        executionContext(state, thread, ct);
                    }
                    catch (Exception e)
                    {
                        int unpinThreadOutcomeCode = Native.UnpinThreadUnsafe();

                        if (unpinThreadOutcomeCode < 0) throw new FailedToUnpinThreadException($"unpinThreadOutcomecode={unpinThreadOutcomeCode}");

                        throw new ThreadRuntimeException(null, e);
                    }
                });

                if (ct.IsCancellationRequested) return 0;

                // need to make it a background thread, since its a foreground thread by default. 
                // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                // if this called method returns without exceptions.
                this.SystemThread.IsBackground = true;
                this.SystemThread.Start();

                // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                // instant an accurate timeouts. The kernel underneath should already schedule out the underlying thread so that its not locked on just
                // this CPU bound task here.
                Stopwatch sw = new Stopwatch();
                sw.Start();

                while (sw.ElapsedMilliseconds <= this.timeoutMs)
                {
                    if (ct.IsCancellationRequested) return 0;

                    if (bss.GetIsBootstrapped()) return tid > 0 ? tid : throw new ThreadBootstrapException(message: $"Invalid Linux Thread ID. tid={tid}");
                }

                bss.SetHasFailed(true);

                throw new ThreadBootstrapException(message: "Bootstrap timed out.");
            } catch (Exception e)
            {
                throw new StartException(null, e);
            }
        }

        public Task<int> StartAsync(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
        {
            var self = this;

            Task<int> task = new Task<int>(() => {
                    try
                    {
                        return self.Start(state: state, executionContext: executionContext, ct: ct);
                    }
                    catch (Exception e) 
                    {
                        throw new StartAsyncException(null, e);
                    }
                }
            );

            task.Start();

            return task;
        }
    }
    class TrySetResultFailedException : Exception
    {
        public TrySetResultFailedException() { }
        public TrySetResultFailedException(string message) : base(message) { }
        public TrySetResultFailedException(string? message, Exception? innerException) : base(message, innerException) { }
    }
    class ThreadBootstrapException : Exception
    {
        public ThreadBootstrapException() { }
        public ThreadBootstrapException(string message) : base(message) { }
        public ThreadBootstrapException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class ThreadRuntimeException : Exception
    {
        public ThreadRuntimeException() { }
        public ThreadRuntimeException(string message) : base(message) { }
        public ThreadRuntimeException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class ThreadNotFoundException : Exception {
        public ThreadNotFoundException() { }
        public ThreadNotFoundException(string message) : base(message) { }
        public ThreadNotFoundException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class FailedToPinThreadException : Exception
    {
        public FailedToPinThreadException() { }
        public FailedToPinThreadException(string message) : base(message) { }
        public FailedToPinThreadException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class FailedToUnpinThreadException : Exception
    {
        public FailedToUnpinThreadException() { }
        public FailedToUnpinThreadException(string message) : base(message) { }
        public FailedToUnpinThreadException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class StartAsyncException : Exception
    {
        public StartAsyncException() { }
        public StartAsyncException(string message) : base(message) { }
        public StartAsyncException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class StartException : Exception
    {
        public StartException() { }
        public StartException(string message) : base(message) { }
        public StartException(string? message, Exception? innerException) : base(message, innerException) { }
    }

}