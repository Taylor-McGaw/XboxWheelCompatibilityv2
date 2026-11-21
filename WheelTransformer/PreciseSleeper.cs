using System.Runtime.InteropServices;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>
    /// Sleeps for short intervals (about 1 ms) accurately.
    /// Thread.Sleep(1) normally sleeps ~15.6 ms on Windows because of the default timer resolution,
    /// which limited the old loop to ~64 updates per second. This uses a high-resolution waitable
    /// timer (Windows 10 1803+), which is also not throttled for windowless processes like services.
    /// Falls back to timeBeginPeriod(1) + Thread.Sleep if the high-resolution timer is unavailable.
    /// </summary>
    internal sealed class PreciseSleeper : IDisposable
    {
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;
        private const uint INFINITE = 0xFFFFFFFF;

        private IntPtr timerHandle;
        private bool usingTimeBeginPeriod;

        public PreciseSleeper()
        {
            timerHandle = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

            if (timerHandle == IntPtr.Zero)
            {
                usingTimeBeginPeriod = timeBeginPeriod(1) == 0;
            }
        }

        public bool IsHighResolution => timerHandle != IntPtr.Zero;

        public void Sleep(TimeSpan duration)
        {
            if (timerHandle != IntPtr.Zero)
            {
                // Negative due time = relative, in 100 ns units (same unit as TimeSpan.Ticks).
                long dueTime = -duration.Ticks;
                if (SetWaitableTimer(timerHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(timerHandle, INFINITE);
                    return;
                }
            }

            Thread.Sleep(duration);
        }

        public void Dispose()
        {
            if (timerHandle != IntPtr.Zero)
            {
                CloseHandle(timerHandle);
                timerHandle = IntPtr.Zero;
            }

            if (usingTimeBeginPeriod)
            {
                timeEndPeriod(1);
                usingTimeBeginPeriod = false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);
    }
}
