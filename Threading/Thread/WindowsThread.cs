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

        public bool GetIsBootstrapped() { return Volatile.Read(ref this.isBootstrapped); }
        public void SetIsBootstrapped(bool IsBootstrapped)
        {
            Volatile.Write(ref this.isBootstrapped, IsBootstrapped);
        }
        public BootstrapState()
        {
            this.SetIsBootstrapped(false);
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

        // must be handled using volatile semantics
        private System.Threading.Thread? DotnetThread;
        public required CancellationTokenSource CancellationTokenSource;
        private bool IsDisposed;
        private bool IsDisposing;
        public Thread(Affinity affinity, long timeoutMs = 100L, CancellationTokenSource? cts = null)
        {
            this.affinity = affinity;
            this.timeoutMs = timeoutMs;

            this.SetDotnetThread(null);

            if (cts != null) this.SetCancellationTokenSource(cts);
            else this.SetCancellationTokenSource(new CancellationTokenSource()); // need some cancellation token source so that the Dispose method can reuse it. 

            this.SetIsDisposing(false);
            this.SetIsDisposed(false); 
        }
        public System.Threading.Thread? GetDotnetThread() { return Volatile.Read(ref this.DotnetThread); }
        private void SetDotnetThread(System.Threading.Thread? DotnetThread) { Volatile.Write(ref this.DotnetThread, DotnetThread); }
        public CancellationTokenSource GetCancellationTokenSource() { return Volatile.Read(ref this.CancellationTokenSource); }
        private void SetCancellationTokenSource(CancellationTokenSource cts) { Volatile.Write(ref this.CancellationTokenSource, cts); }
        public bool GetIsDisposed() { return Volatile.Read(ref this.IsDisposed); }
        private void SetIsDisposed(bool IsDisposed) { Volatile.Write(ref this.IsDisposed, IsDisposed); }
        public bool GetIsDisposing() { return Volatile.Read(ref this.IsDisposing); }
        private void SetIsDisposing(bool IsDisposing) { Volatile.Write(ref this.IsDisposing, IsDisposing); }
        private bool ShouldExit(CancellationToken ct) { return this.GetIsDisposing() || this.GetIsDisposed() || ct.IsCancellationRequested; }

        // Checks current state of the given Thread instance it's a part of to ensure that no existing running thread routine is happening.
        // Create a bootstrap state to be used to mediate shared memory flags between the thread that is to be created and teh consumer thread calling Start().
        // Within the body of the new thread created, the thread is pinned, which includes an unpin saga to ensure the underlying thread is reset to a valid state.
        // If bootstrap is successful, the thread will be pinned and begin executing the supplied routine with the supplied state.
        public void Start(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext)
        {
            CancellationToken ct = this.CancellationTokenSource.Token;

            // don't allow the cancellation token to be reused if already cancelled.
            if (ct.IsCancellationRequested) throw new ThreadBootstrapException("Already cancelled."); 

            try
            {
                if (this.ShouldExit(ct)) return;

                if (this.GetDotnetThread() != null) throw new ThreadStateException($"Thread already exists.");

                if (this.ShouldExit(ct)) return;

                BootstrapState bss = new (); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                if (this.ShouldExit(ct)) return;

                System.Threading.Thread DotnetThread = new (() =>
                {
                    System.Threading.Thread? thread = this.GetDotnetThread();

                    try
                    {
                        if (this.ShouldExit(ct)) return;

                        if (thread == null) throw new ThreadNotFoundException("thread=null");

                        if (this.ShouldExit(ct)) return;

                        Native.SetAffinity(this.affinity.affinityMask, out ulong _);

                        if (this.ShouldExit(ct)) { Native.ResetAffinity(out ulong _); return; }

                        bss.SetIsBootstrapped(true);

                        if (this.ShouldExit(ct)) { Native.ResetAffinity(out ulong _); return; }
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity(out ulong _);
                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                        if (this.ShouldExit(ct)) { Native.ResetAffinity(out ulong _); return; }

                        executionContext(state, thread, ct);

                        Native.ResetAffinity(out ulong _); // reset the affinity once the work is done. the code can exit out normally now without lingering side effects.
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity(out ulong _);
                        throw new ThreadRuntimeException(null, e);
                    }
                });

                this.SetDotnetThread(DotnetThread);

                if (this.ShouldExit(ct)) return;

                // need to make it a background thread, since its a foreground thread by default. 
                // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                // if this called method returns without exceptions.
                DotnetThread.IsBackground = true;

                if (this.ShouldExit(ct)) return;

                DotnetThread.Start();

                if (this.ShouldExit(ct)) return;

                // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                // the underlying thread, but not the CLR. The CFS is your friend.
                Stopwatch sw = new ();
                sw.Start();

                while (sw.ElapsedMilliseconds < this.timeoutMs) if (ct.IsCancellationRequested || bss.GetIsBootstrapped()) return;

                this.CancellationTokenSource.Cancel();

                throw new ThreadBootstrapException("Timed out.");
            }
            catch (Exception e)
            {
                throw new StartException(null, e);
            }
        }

        // for encapsulating a synchronous operation to bootstrap a new pinned thread executing a particular routine.
        // using a task to encapsulate the entire routine, so that the given task thread can retain its context on the particular bootstrapping routine.
        public Task StartAsync(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext)
        {
            CancellationToken ct = this.CancellationTokenSource.Token;

            return Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    this.Start(state: state, executionContext: executionContext);

                    if (this.GetIsDisposing()) { this.SetIsDisposed(true); this.SetIsDisposing(false); }

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

        public void Dispose()
        {
            this.SetIsDisposing(true);
            this.CancellationTokenSource.Cancel();
        }
    }
}
