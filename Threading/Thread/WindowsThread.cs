using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using StreamPunk.Threading.Thread.Errors;

namespace StreamPunk.Threading.Thread.Windows
{
    public partial class Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        // The values passed back via 'out' arguments will auto cleanup the underlying 
        [LibraryImport("SetAffinityWindows.dll")]
        public static partial int SetAffinityUnsafe(
            ulong suppliedAffinityMask,
            out ulong appliedAffinityMask
        );

        public static void SetAffinity(ulong suppliedAffinityMask, out ulong appliedAffinityMask)
        {
            int outcomeCode = Native.SetAffinityUnsafe(suppliedAffinityMask, out appliedAffinityMask);

            if (outcomeCode > 0) throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");

            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid argument initialization.",
                    -2 => "Failed to get handle.",
                    -3 => "Failed to set thread affinity mask.",
                    -4 => "Applied affinity mask does not match.",
                    _ => "Unknown error.",
                };

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
            }
        }

        [LibraryImport("ResetAffinityWindows.dll")]
        public static partial int ResetAffinityUnsafe(out ulong appliedAffinityMask);

        // Idempotent; can be invoked multiple times safely for the calling thread.
        // Just sets the given thread affinity to 1 for every physically available CPU.
        public static void ResetAffinity(out ulong appliedAffinityMask)
        {
            int outcomeCode = Native.ResetAffinityUnsafe(out appliedAffinityMask);

            if (outcomeCode > 0) throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");

            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid argument initialization.",
                    -2 => "Failed to get handle.",
                    -3 => "Failed to set thread affinity mask.",
                    -4 => "Applied affinity mask does not match.",
                    _ => "Unknown error.",
                };

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
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
        public readonly ulong affinityMask;
        public Affinity(ulong affinityMask)
        {
            this.affinityMask = affinityMask;
        }
    }
    public class Thread<StartState>
    {
        public readonly Affinity affinity;
        public readonly long timeoutMs;

        // make these two volatile, because the Thread instance may be reused. 
        private volatile System.Threading.Thread? SystemThread;
        public Thread(Affinity affinity, long timeoutMs = 100L)
        {
            this.affinity = affinity;
            this.timeoutMs = timeoutMs;
            this.SystemThread = null;
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

                        if (thread == null) throw new ThreadNotFoundException("thread=null");

                        Native.SetAffinityUnsafe(self.affinity.affinityMask, out ulong _);

                        if (bss.hasFailed || ct.IsCancellationRequested)
                        {
                            Native.ResetAffinityUnsafe(out ulong _);
                            return;
                        }

                        bss.isBootstrapped = true;
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinityUnsafe(out ulong _);
                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                        executionContext(state, thread, ct);
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinityUnsafe(out ulong _);
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
            });
        }
    }
}
