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
            // .NET 10 means that the heap from C is cleaned up during the marshalling.
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)]
            out ulong[] appliedAffinityMask,
            out ulong appliedMaskLength
            );

        public static void PinThread(ulong[] affinityMask, out int tid, out ulong[] appliedAffinityMask)
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
             appliedMaskLength: out ulong _ // because its just for a passed back property from the C so C# knows the length of 'appliedAffinityMask'
             );

            if (outcomeCode < 0)
            {
                string message;

                switch (outcomeCode)
                {
                    case -1:
                        message = "Invalid arg initialization.";
                        break;
                    case -2:
                        message = "Failed to allocate cpu set.";
                        break;
                    case -3:
                        message = "Real bit position too large.";
                        break;
                    case -4:
                        message = "Failed to set affinity.";
                        break;
                    case -5:
                        message = "Failed to get real number of cpus.";
                        break;
                    case -6:
                        message = "Failed to allocate real cpu set.";
                        break;
                    case -7:
                        message = "Failed to get affinity";
                        break;
                    case -8:
                        message = "Failed to allocate comparison mask";
                        break;
                    default:
                        message = "Unknown error.";
                        break;
                }

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");

            }
            else if (outcomeCode > 0)
            {
                throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");
            }

            // Check to ensure that a valid tid was written back from the native context.

            if (id <= 0) throw new InvalidTidException($"tid={id}");

            // Check to see if the supplied and applied masks are the same.
            // the affinity mask can be longer, all that's being checked is whether what's actually applied is
            // at the very least a matching subset of what's supplied.
            // iterate from right to left, and make raw number comparisons.
            // No need to check if the real cpu subset of affinityMask is all 0's or not, the Linux kernel will return an error in such
            // cases automatically through sched_setaffinity()
            for (int i = affinityMask.Length - 1; i >= 0; i--)
            {
                int aamIndex = (aam.Length - 1) - (affinityMask.Length - 1 - i);

                if (aamIndex >= 0 && affinityMask[i] != aam[aamIndex]) throw new AppliedMaskMismatchException($"i={i},aamIndex={aamIndex},affinityMask[i]={affinityMask[i]},aam[aamIndex]={aam[aamIndex]}");
            }

            tid = id;
            appliedAffinityMask = aam;
        }

        [LibraryImport("UnpinThreadLinux.so")]
        public static partial int UnpinThreadUnsafe();

        // Idempotent, just sets the given thread affinity to 1 for every physically available CPU.
        public static void UnpinThread()
        {
            int outcomeCode = Native.UnpinThreadUnsafe();

            if (outcomeCode < 0)
            {
                string message;

                switch (outcomeCode)
                {
                    case -1:
                        message = "Failed to get real number of cpus.";
                        break;
                    case -2:
                        message = "Failed to allocate cpu set.";
                        break;
                    case -3:
                        message = "Failed to set affinity.";
                        break;
                    default:
                        message = "Unknown error.";
                        break;
                }

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
            }
            else if (outcomeCode > 0)
            {
                throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");
            }
        }
        public class Affinity
        {
            // an arbitrarily long bitmask read from right to left
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
            public Thread(Affinity affinity, long timeoutMs = 250L)
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
                    BootstrapState bss = new BootstrapState(); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                    this.SystemThread = new System.Threading.Thread(() =>
                    {
                        System.Threading.Thread? thread = self.GetThread();
                        int tid = 0;

                        try
                        {
                            if (bss.GetHasFailed() || ct.IsCancellationRequested) return;

                            if (thread == null) throw new FailedToGetThreadException(message: "currContextThread=null");

                            Native.PinThread(self.affinity.affinityMask, out tid, out ulong[] _);

                            if (bss.GetHasFailed() || ct.IsCancellationRequested)
                            {
                                Native.UnpinThread();
                                return;
                            }

                            self.SetTid(tid);
                            bss.SetIsBootstrapped(true);
                        }
                        catch (Exception e)
                        {
                            Native.UnpinThread();
                            throw new ThreadBootstrapException(null, e);
                        }

                        try
                        {
                            executionContext(state, thread, ct);
                        }
                        catch (Exception e)
                        {
                            Native.UnpinThread();
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
                    // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                    // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                    // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                    // the underlying thread, but not the CLR.
                    Stopwatch sw = new Stopwatch();
                    sw.Start();

                    while (sw.ElapsedMilliseconds <= this.timeoutMs)
                    {
                        if (ct.IsCancellationRequested) return 0;

                        int tid = this.GetTid();

                        if (bss.GetIsBootstrapped()) return tid > 0 ? tid : throw new InvalidTidException($"tid={tid}");
                    }

                    bss.SetHasFailed(true);
                    throw new ThreadBootstrapException(message: "Timed out.");

                }
                catch (Exception e)
                {
                    throw new StartException(null, e);
                }
            }

            public Task<int> StartAsync(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
            {
                var self = this;

                return Task.Run<int>(() =>
                {
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
            }
        }

        class NativeCallException : Exception
        {
            public NativeCallException() { }
            public NativeCallException(string message) : base(message) { }
            public NativeCallException(string? message, Exception? innerException) : base(message, innerException) { }
        }
        class TrySetResultFailedException : Exception
        {
            public TrySetResultFailedException() { }
            public TrySetResultFailedException(string message) : base(message) { }
            public TrySetResultFailedException(string? message, Exception? innerException) : base(message, innerException) { }
        }

        class InvalidTidException : Exception
        {
            public InvalidTidException() { }
            public InvalidTidException(string message) : base(message) { }
            public InvalidTidException(string? message, Exception? innerException) : base(message, innerException) { }
        }

        class AppliedMaskMismatchException : Exception
        {
            public AppliedMaskMismatchException() { }
            public AppliedMaskMismatchException(string message) : base(message) { }
            public AppliedMaskMismatchException(string? message, Exception? innerException) : base(message, innerException) { }
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

        class ThreadNotFoundException : Exception
        {
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
}