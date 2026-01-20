using StreamPunk.Threading.Thread.Errors;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StreamPunk.Threading.Thread.Windows
{
    public partial class Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        // The values passed back via 'out' arguments will auto cleanup the underlying 
        [LibraryImport("SetAffinityWindows.dll", EntryPoint = "SetAffinityUnsafe")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        public static partial int SetAffinityUnsafe(
            System.UInt64 suppliedAffinityMask,
            out System.UInt64 appliedAffinityMask
        );

        public static void SetAffinity(ulong suppliedAffinityMask, out ulong appliedAffinityMask)
        {
            System.Int32 outcomeCode = Native.SetAffinityUnsafe((System.UInt64)suppliedAffinityMask, out System.UInt64 aam);

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

            // DONT CAST THE TYPE. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
            appliedAffinityMask = aam;
        }

        [LibraryImport("ResetAffinityWindows.dll", EntryPoint = "ResetAffinityUnsafe")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        public static partial int ResetAffinityUnsafe(out System.Int32 appliedAffinityMask);

        // Idempotent; can be invoked multiple times safely for the calling thread.
        // Just sets the given thread affinity to 1 for every physically available CPU.
        public static void ResetAffinity(out ulong appliedAffinityMask)
        {
            System.Int32 outcomeCode = Native.ResetAffinityUnsafe(out System.Int32 aam);

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

            appliedAffinityMask = (ulong)aam;
        }
    }

    // important so that the calling thread of 'Start()' can do a simple spinlock with a timeout.
    internal class BootstrapState
    {
        public bool isBootstrapped;
        public bool hasFailed;

        public bool GetIsBootstrapped()
        {
            return Volatile.Read(ref this.isBootstrapped);
        }
        public void SetIsBootstrapped(bool IsBootstrapped)
        {
            Volatile.Write(ref this.isBootstrapped, IsBootstrapped);
        }
        public bool GetHasFailed()
        {
            return Volatile.Read(ref this.hasFailed);
        }
        public void SetHasFailed(bool HasFailed)
        {
            Volatile.Write(ref this.hasFailed, HasFailed);
        }
        public BootstrapState()
        {
            this.SetIsBootstrapped(false);
            this.SetHasFailed(false);
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
        private System.Threading.Thread? DotnetThread;
        public Thread(Affinity affinity, long timeoutMs = 100L)
        {
            this.affinity = affinity;
            this.timeoutMs = timeoutMs;
            this.SetDotnetThread(null);
        }
        public System.Threading.Thread? GetDotnetThread() { return Volatile.Read(ref this.DotnetThread); }
        public void SetDotnetThread(System.Threading.Thread? DotnetThread) { Volatile.Write(ref this.DotnetThread, DotnetThread); }

        // Checks current state of the given Thread instance it's a part of to ensure that no existing running thread routine is happening.
        // Create a bootstrap state to be used to mediate shared memory flags between the thread that is to be created and teh consumer thread calling Start().
        // Within the body of the new thread created, the thread is pinned, which includes an unpin saga to ensure the underlying thread is reset to a valid state.
        // If bootstrap is successful, the thread will be pinned and begin executing the supplied routine with the supplied state.
        public void Start(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested) return;

                if (this.GetDotnetThread() != null) throw new ThreadStateException($"Thread already exists.");
                
                if (ct.IsCancellationRequested) return;

                BootstrapState bss = new (); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                if (ct.IsCancellationRequested) return;

                System.Threading.Thread DotnetThread = new (() =>
                {
                    System.Threading.Thread? thread = this.GetDotnetThread();

                    try
                    {
                        if (bss.GetHasFailed() || ct.IsCancellationRequested) return;

                        if (thread == null) throw new ThreadNotFoundException("thread=null");

                        if (bss.GetHasFailed() || ct.IsCancellationRequested) return;

                        Native.SetAffinity(this.affinity.affinityMask, out ulong _);

                        if (bss.GetHasFailed() || ct.IsCancellationRequested) { Native.ResetAffinity(out ulong _); return; }

                        bss.SetIsBootstrapped(true);

                        if (bss.GetHasFailed() || ct.IsCancellationRequested) { Native.ResetAffinity(out ulong _); return; }
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity(out ulong _);
                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                        if (bss.GetHasFailed() || ct.IsCancellationRequested) { Native.ResetAffinity(out ulong _); return; }

                        executionContext(state, thread, ct);
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity(out ulong _);
                        throw new ThreadRuntimeException(null, e);
                    }
                });

                this.SetDotnetThread(DotnetThread);

                if (ct.IsCancellationRequested) return;

                // need to make it a background thread, since its a foreground thread by default. 
                // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                // if this called method returns without exceptions.
                DotnetThread.IsBackground = true;
                DotnetThread.Start();

                // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                // the underlying thread, but not the CLR. The CFS is your friend.
                Stopwatch sw = new ();
                sw.Start();

                while (sw.ElapsedMilliseconds < this.timeoutMs) if (ct.IsCancellationRequested || bss.GetIsBootstrapped()) return;

                bss.SetHasFailed(true);

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
            return Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    this.Start(state: state, executionContext: executionContext, ct: ct);

                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    throw;  // Throw directly so that the given task can transition to the Cancelled state properly
                }
                catch (Exception e)
                {
                    throw new StartAsyncException(null, e);
                }
            }, ct);
        }
    }
}
