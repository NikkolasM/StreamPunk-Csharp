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
            System.Int32 outcomeCode = Native.SetAffinityUnsafe(suppliedAffinityMask, out System.UInt64 aam);  // DONT CAST THE ARG TYPES. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT

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

            appliedAffinityMask = aam; // DONT CAST THE TYPE. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
        }

        [LibraryImport("ResetAffinityWindows.dll", EntryPoint = "ResetAffinityUnsafe")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        public static partial int ResetAffinityUnsafe(out System.UInt64 appliedAffinityMask);

        // Idempotent; can be invoked multiple times safely for the calling thread.
        // Just sets the given thread affinity to 1 for every physically available CPU.
        public static void ResetAffinity(out ulong appliedAffinityMask)
        {
            System.Int32 outcomeCode = Native.ResetAffinityUnsafe(out System.UInt64 aam);

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

            appliedAffinityMask = aam; // DONT CAST THE TYPE. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
        }
    }
    public class Affinity
    {
        public readonly ulong affinityMask; // an arbitrarily long bitmask read from right to left
        public Affinity(ulong affinityMask) { this.affinityMask = affinityMask; }
    }

    // for providing a wrapper around the Thread instance to allow both public and certain private methods to
    // be exposed to the underlying dotnet thread associated with the given streampunk thread instance, but not from outside
    // threads interacting with the physical streampunk thread class instance itself. Therefore, the wrapper can either be safely copied or stay on the stack.
    public struct ThreadAPIProxy<StartState>
    {
        public readonly Func<System.Threading.Thread?> GetDotnetThreadDelegate;
        public readonly Func<Affinity> GetAffinityDelegate;
        public readonly Action<Affinity> SetAffinityDelegate;
        public ThreadAPIProxy(Func<System.Threading.Thread?> GetDotnetThreadDelegate, Func<Affinity> GetAffinityDelegate, Action<Affinity> SetAffinityDelegate)
        {
            this.GetDotnetThreadDelegate = GetDotnetThreadDelegate;
            this.GetAffinityDelegate = GetAffinityDelegate;
            this.SetAffinityDelegate = SetAffinityDelegate;
        }
    }

    // important so that the calling thread of 'Start()' can do a simple predictable spinlock with a timeout.
    internal class BootstrapState
    {
        private bool IsBootstrapped;
        public bool GetIsBootstrapped() { return Volatile.Read(ref this.IsBootstrapped); }
        public void SetIsBootstrapped(bool IsBootstrapped) { Volatile.Write(ref this.IsBootstrapped, IsBootstrapped); }
        public BootstrapState() { this.SetIsBootstrapped(false); }
    }

    public class Thread<StartState>
    {
        private Affinity Affinity;
        private Affinity AppliedAffinity;

        public readonly decimal TimeoutMs;

        // these fields must be handled using volatile semantics
        private CancellationTokenSource CoreCts;
        private System.Threading.Thread? DotnetThread;
        private bool IsDisposed;
        private bool IsDisposing;
        public Thread(Affinity affinity, decimal timeoutMs = 50m, CancellationTokenSource? cts = null)
        {
            this.Affinity = affinity;
            this.AppliedAffinity = new Affinity(0x0);

            this.TimeoutMs = timeoutMs;

            if (cts != null) this.CoreCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            else this.CoreCts = new CancellationTokenSource();
            
            Volatile.Write(ref this.DotnetThread, null);
            Volatile.Write(ref this.IsDisposing, false);
            Volatile.Write(ref this.IsDisposed, false);
        }

        public Affinity GetAffinity() { return Volatile.Read(ref this.Affinity); }

        // to be called by the thread itself within its execution context. SHOULD NOT BE CALLED FROM EXTERNAL SOURCES.
        // IF YOU WANT TO BE ABLE TO SET AFFINITY FROM AN OUTSIDE THREAD, PLEASE MAKE YOUR OWN OBSERVABLE THAT YOU INJECT AND CHECK
        // COOPERATIVELY WITHIN THE EXECUTION CONTEXT YOU SUPPLY.
        // Lastly, keep an eye on this method usage, rapid affinity repinning could potentialy blow up the heap if you are careless.
        private void SetAffinity(Affinity affinity)
        {
            Native.SetAffinity(affinity.affinityMask, out ulong aam);

            Volatile.Write(ref this.Affinity, affinity);
            Volatile.Write(ref this.AppliedAffinity, new Affinity(aam));
        }

        public Affinity GetAppliedAffinity() { return Volatile.Read(ref this.AppliedAffinity); }
      
        public CancellationTokenSource GetCancellationTokenSource() { return Volatile.Read(ref this.CoreCts); }

        public System.Threading.Thread? GetDotnetThread() { return Volatile.Read(ref this.DotnetThread); }

        public bool GetIsDisposed() { return Volatile.Read(ref this.IsDisposed); }

        public Task AwaitIsDisposedAsync(CancellationToken ct, decimal TimeoutMs = 0m)
        {
            return Task.Run(async () =>
            {
                long startTime = TimeoutMs > 0m ? Stopwatch.GetTimestamp() : 0l;
                decimal msToSeconds = TimeoutMs > 0m ? this.TimeoutMs / 1000m : 0m;
                long timeoutInTicks = TimeoutMs > 0m ? (long)(msToSeconds * ((decimal)Stopwatch.Frequency)) : 0l;

                while (true)
                {
                    if (TimeoutMs > 0m && (Stopwatch.GetTimestamp() - startTime) >= timeoutInTicks) throw new TimedOutException();

                    ct.ThrowIfCancellationRequested();

                    if (Volatile.Read(ref this.IsDisposed)) return;

                    // so that the thread as part of the TPL ThreadPool can work on other stuff instead of this leading to a deadlock. Here, we can utilize
                    // Task.Yield instead of System.Threading.Thread.Yield since AwaitIsDisposedAsync has softer requirements for propagating the change of
                    // this state to the user. At the same time, CLR visibility here allows this method to have many multiple Tasks it produces concurrently running
                    // with reasonable (but not super real-time) resolution latency. 
                    await Task.Yield();
                }
            }, ct);
        }

        public bool GetIsDisposing() { return Volatile.Read(ref this.IsDisposing); }

        private void ThrowIfShouldExit(CancellationToken ct)
        {
            if (this.GetIsDisposing()) throw new DisposingException();

            ct.ThrowIfCancellationRequested();
        }

        // Checks current state of the given Thread instance it's a part of to ensure that no existing running thread routine is happening.
        // Create a bootstrap state to be used to mediate shared memory flags between the thread that is to be created and teh consumer thread calling Start().
        // Within the body of the new thread created, the thread is pinned, which includes an unpin saga to ensure the underlying thread is reset to a valid state.
        // If bootstrap is successful, the thread will be pinned and begin executing the supplied routine with the supplied state.
        public void Start(StartState state, Action<StartState, ThreadAPIProxy<StartState>, CancellationToken> executionContext)
        {
            CancellationToken ct = this.CoreCts.Token;

            try
            {
                    this.ThrowIfShouldExit(ct);

                if (this.GetDotnetThread() != null) throw new ThreadStateException($"Thread already exists.");

                    this.ThrowIfShouldExit(ct);

                BootstrapState bss = new(); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                    this.ThrowIfShouldExit(ct);

                System.Threading.Thread DotnetThread = new(() =>
                {
                    System.Threading.Thread? thread = this.GetDotnetThread();

                    try
                    {
                        if (thread == null) throw new ThreadNotFoundException();

                            this.ThrowIfShouldExit(ct);

                        this.SetAffinity(this.Affinity);

                            this.ThrowIfShouldExit(ct);

                        bss.SetIsBootstrapped(true);

                            this.ThrowIfShouldExit(ct);
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity(out ulong _); // doesn't need any additional handling since this native wrapper should throw when the aam doesn't match the max value of ulong 

                        if (e is DisposingException || e is OperationCanceledException) return;

                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                            this.ThrowIfShouldExit(ct);

                        ThreadAPIProxy<StartState> proxy = new(this.GetDotnetThread, this.GetAffinity, this.SetAffinity);

                            this.ThrowIfShouldExit(ct);

                        executionContext(state, proxy, ct);

                            this.ThrowIfShouldExit(ct);

                        Native.ResetAffinity(out ulong _); // reset the affinity once the work is done. the code can exit out normally now without lingering side effects.
                    }
                    catch (Exception e)
                    {
                        Native.ResetAffinity(out ulong _); // doesn't need any additional handling since this native wrapper should throw when the aam doesn't match the max value of ulong 

                        if (e is DisposingException || e is OperationCanceledException) return;

                        throw new ThreadRuntimeException(null, e);
                    }
                });

                    this.ThrowIfShouldExit(ct);

                Volatile.Write(ref this.DotnetThread, DotnetThread);

                    this.ThrowIfShouldExit(ct);

                // need to make it a background thread, since its a foreground thread by default. 
                // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                // if this called method returns without exceptions.
                DotnetThread.IsBackground = true;

                    this.ThrowIfShouldExit(ct);

                DotnetThread.Start();

                    this.ThrowIfShouldExit(ct);

                // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                // the underlying thread, but not the CLR. The CFS is your friend.

                long startTime = Stopwatch.GetTimestamp();
                decimal msToSeconds = this.TimeoutMs / 1000m;
                long timeoutInTicks = (long)(msToSeconds * ((decimal)Stopwatch.Frequency));

                // so that the thread as part of the TPL ThreadPool can work on other stuff instead of this leading to a deadlock.
                // Uses thread yield instead of task yield because on its own, this isn't a task but can be the executing body of a task. This will
                // allow the OS to allow any other tasks on the same core of the underlying kernel thread executing this chain to run. In the managed side of things,
                // this keeps the CLR from preempting the loop itself. This ensures that CPU-bound noisy neighbors be it in the same process or in the same OS context
                // don't hang this thread up. However, since this isn't CLR-visible, in situations where you create many of these threads at once, it can potentially 
                // hit the threadpool max. however, since the happy path should resolve in microseconds, with millisecond-level timeouts, these should be very deterministic
                // even though they're async. 
                while ((Stopwatch.GetTimestamp() - startTime) < timeoutInTicks)
                {
                        this.ThrowIfShouldExit(ct);

                    if (bss.GetIsBootstrapped()) return;

                    System.Threading.Thread.Yield();
                }

                this.CoreCts.Cancel();

                throw new ThreadBootstrapException("Timed out.");
            }
            catch (Exception e)
            {
                if (e is DisposingException || e is OperationCanceledException) throw;

                throw new StartException(null, e);
            }
        }

        // for encapsulating a synchronous operation to bootstrap a new pinned thread executing a particular routine.
        // using a task to encapsulate the entire routine, so that the given task thread can retain its context on the particular bootstrapping routine.
        public Task StartAsync(StartState state, Action<StartState, ThreadAPIProxy<StartState>, CancellationToken> executionContext)
        {
            CancellationToken ct = this.CoreCts.Token;

            return Task.Run(() =>
            {
                try
                {
                        this.ThrowIfShouldExit(ct);

                    this.Start(state, executionContext);
                }
                catch (Exception e)
                {
                    if (e is DisposingException) Volatile.Write(ref this.IsDisposed, true);

                    if (e is OperationCanceledException) ct.ThrowIfCancellationRequested(); // so the surrounding task can exit in the Cancelled state.

                    throw new StartAsyncException(null, e);
                }
            }, ct);
        }

        public void Dispose()
        {
            Volatile.Write(ref this.IsDisposing, true); // important so that both the sync and async Start() APIs can handle disposal gracefully/allow you to implement proper cleanup routines.

            // reusing the ct is simpler, reduces how much is needed to be injected into the execution context supplied
            // while also being able to reuse the safe-exit logic that's made in the supplied execution context.
            this.CoreCts.Cancel();
        }
    }

    public class AppliedAffinityMaskDoesNotMatchException : Exception
    {
        public AppliedAffinityMaskDoesNotMatchException() { }
        public AppliedAffinityMaskDoesNotMatchException(string message) : base(message) { }
        public AppliedAffinityMaskDoesNotMatchException(string? message, Exception? innerException) : base(message, innerException) { }
    }
}
