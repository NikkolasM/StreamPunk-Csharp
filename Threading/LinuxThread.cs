using StreamPunk.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;


namespace StreamPunk.Threading.Linux
{
    public partial class Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        // The values passed back via 'out' arguments will auto cleanup the underlying 
        [LibraryImport("SetAffinityLinux.so")]
        public static partial int SetAffinityUnsafe(
            in ulong[] suppliedAffinityMask,
            ulong suppliedMaskLength,
            out int tid,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] // For passing an array on the heap allocated in the C back to the CLR safely. CLR needs to know the size of the arr. This is how. 
            out ulong[] appliedAffinityMask, // latest .NET will dealloc the passed back heap for me, since it knows the size of the array, and the bits per element.
            out ulong appliedMaskLength
            );

        public static void SetAffinity(ulong[] affinityMask, out int tid, out ulong[] appliedAffinityMask)
        {
            // Check the supplied args
            if (affinityMask.Length <= 0) throw new ArgumentException("Supplied affinity mask needs atleast one element.");

            bool hasAtleastOneMarkedCore = false;

            foreach (ulong element in affinityMask) if (element > 0UL) hasAtleastOneMarkedCore = true;

            if (!hasAtleastOneMarkedCore) throw new ArgumentException("Supplied affinity mask needs atleast one marked core.");

            // Once the args are validated, make the native call to pin the thread
            int outcomeCode = Native.SetAffinityUnsafe(
             suppliedAffinityMask: affinityMask,
             suppliedMaskLength: (ulong)affinityMask.Length,
             tid: out int id,
             appliedAffinityMask: out ulong[] aam,
             appliedMaskLength: out ulong _ // because its just for a passed back property from the C so the CLR knows the length of 'appliedAffinityMask'
             );

            // outcomeCode = 0 means success, anything else means something unexpected happened
            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid arg initialization.",
                    -2 => "Failed to allocate cpu set.",
                    -3 => "Real bit position too large.",
                    -4 => "Failed to set affinity.",
                    -5 => "Failed to get real number of cpus.",
                    -6 => "Failed to allocate real cpu set.",
                    -7 => "Failed to get affinity",
                    -8 => "Failed to allocate comparison mask",
                    _ => "Unknown error.",
                };

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");

            }
            else if (outcomeCode > 0)
            {
                throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");
            }

            // Check to ensure that a valid tid was written back from the native context.
            if (id <= 0) throw new InvalidTidException($"tid={id}");

            // Check to see if the supplied and applied masks are the same.
            // the affinity mask can be longer, all that's being checked is whether what's actually applied is a matching subset of what's supplied.
            // iterate from right to left, and make raw number comparisons.
            // No need to check if the real cpu subset of affinityMask is all 0's or not, the Linux kernel will return an error in such
            // cases automatically through sched_setaffinity().
            for (int i = affinityMask.Length - 1; i >= 0; i--)
            {
                int aamIndex = (aam.Length - 1) - (affinityMask.Length - 1 - i);

                if (aamIndex >= 0 && affinityMask[i] != aam[aamIndex]) throw new AppliedMaskMismatchException($"i={i},aamIndex={aamIndex},affinityMask[i]={affinityMask[i]},aam[aamIndex]={aam[aamIndex]}");
            }

            tid = id;
            appliedAffinityMask = aam;
        }

        [LibraryImport("ResetAffinityLinux.so")]
        public static partial int ResetAffinityUnsafe(
            out int tid,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] // For passing an array on the heap allocated in the C back to the CLR safely. CLR needs to know the size of the arr. This is how. 
            out ulong[] appliedAffinityMask, // latest .NET will dealloc the passed back heap for me, since it knows the size of the array, and the bits per element.
            out ulong appliedMaskLength
            );

        // Idempotent; can be invoked multiple times safely for the calling thread.
        // Just sets the given thread affinity to 1 for every physically available CPU.
        public static void ResetAffinity()
        {
            int outcomeCode = Native.ResetAffinityUnsafe(
                tid: out int id,
                appliedAffinityMask: out ulong[] aam,
                appliedMaskLength: out ulong _ // because its just for a passed back property from the C so the CLR knows the length of 'appliedAffinityMask'
                );

            // outcomeCode = 0 means success, anything else means something unexpected happened
            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Failed to get real number of cpus.",
                    -2 => "Failed to allocate cpu set.",
                    -3 => "Failed to set affinity.",
                    _ => "Unknown error.",
                };

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
            }
            else if (outcomeCode > 0)
            {
                throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");
            }
        }
    }

        // important so that the calling thread of 'Start()' can do a simple spinlock with a timeout.
        // awaiting the task context directly will screw things up.
    internal class BootstrapState
    {
        // use volatile keyword so that access to the class instance isn't cached by the VM in the tight loops in 'Start()'
        public volatile bool isBootstrapped;
        public volatile bool hasFailed;

        public BootstrapState()
        {
            this.isBootstrapped = false;
            this.hasFailed = false;
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
    public class Thread<StartState>
    {
        public readonly Affinity affinity;
        public readonly long timeoutMs;

        // make these two volatile, because the Thread instance may be reused. 
        private volatile int tid;
        private volatile System.Threading.Thread? SystemThread;
        public Thread(Affinity affinity, long timeoutMs = 100L)
        {
            this.affinity = affinity;
            this.timeoutMs = timeoutMs;
            this.tid = 0;
            this.SystemThread = null;
        }
        public int GetTid()
        {
            return this.tid;
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

        public System.Threading.ThreadState GetThreadState()
        {
            if (this.SystemThread == null) throw new ThreadNotFoundException();

            return this.SystemThread.ThreadState;
        }

        // Checks current state of the given Thread instance it's a part of to ensure that no existing running thread routine is happening.
        // Create a bootstrap state to be used to mediate shared memory flags between the thread that is to be created and teh consumer thread calling Start().
        // Within the body of the new thread created, the thread is pinned, which includes an unpin saga to ensure the underlying thread is reset to a valid state.
        // If bootstrap is successful, the thread will be pinned and begin executing the supplied routine with the supplied state.
        public void Start(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                // The thread instance can be reused, but requires the current thread to not be running or blocked for some reason.
                // Requires aborting the current thread using the yet-to-be-made 'Abort()' method. 
                if (this.SystemThread != null)
                {
                    System.Threading.ThreadState threadState = this.GetThreadState();
                    bool isWaitSleepJoin = threadState == System.Threading.ThreadState.WaitSleepJoin;
                    bool isRunning = threadState == System.Threading.ThreadState.Running;

                    if (isRunning || isWaitSleepJoin) throw new ThreadStateException($"Invalid ThreadState for Start(). ThreadState={threadState}");
                }

                if (ct.IsCancellationRequested) return;

                var self = this;
                BootstrapState bss = new BootstrapState(); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                this.SystemThread = new System.Threading.Thread(() =>
                {
                    System.Threading.Thread? thread = self.SystemThread;

                    try
                    {
                        if (bss.hasFailed || ct.IsCancellationRequested) return;

                        if (thread == null) throw new FailedToGetThreadException("thread=null");

                        Native.SetAffinity(self.affinity.affinityMask, out int tid, out ulong[] _);

                        if (bss.hasFailed || ct.IsCancellationRequested)
                        {
                            Native.ResetAffinity();
                            return;
                        }

                        self.tid = tid;
                        bss.isBootstrapped = true;
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity();
                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                        executionContext(state, thread, ct);
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity();
                        throw new ThreadRuntimeException(null, e);
                    }
                });

                if (ct.IsCancellationRequested) return;

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
                // the underlying thread, but not the CLR. The CFS is your friend.
                Stopwatch sw = new Stopwatch();
                sw.Start();

                while (sw.ElapsedMilliseconds < this.timeoutMs) if (ct.IsCancellationRequested || bss.isBootstrapped) return;

                bss.hasFailed = true;
                throw new ThreadBootstrapException("Timed out.");

            }
            catch (Exception e)
            {
                throw new StartException(null, e);
            }
        }

        // for encapsulating a synchronous operation to bootstrap a new pinned thread executing a particular routine.
        // using a task to encapsulate the entire routine, so that the given task thread can retain its context on the particular bootstrapping routine.
        public Task StartAsync(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
        {
            var self = this;

            return Task.Run(() =>
            {
                try
                {
                    self.Start(state: state, executionContext: executionContext, ct: ct);
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
