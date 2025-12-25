using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamPunk.Threading.Windows
{
    internal partial struct Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        [LibraryImport("PinThreadWindows.dll")]
        public static partial int PinThread(ulong affinityMask);
    }
    public struct Affinity
    {
        // The handle (thread ID) will be fetched from within the execution context within the lib function being called

        // Windows double word for a bit mask
        public readonly ulong affinityMask;
        public Affinity(ulong affinityMask)
        {
            this.affinityMask = affinityMask;
        }
    }
    internal class Thread
    {
    }
}
