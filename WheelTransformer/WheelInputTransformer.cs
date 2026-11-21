using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Gaming.Input;
using Windows.UI.Input.Preview.Injection;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>
    /// Reads the racing wheel about 1000 times a second and sends it to Windows as a virtual Xbox controller.
    /// Start/Stop control the background loop (used by the service). InjectionEnabled pauses/resumes
    /// sending input while the loop keeps tracking the wheel (used by the status window's Pause button).
    /// </summary>
    public sealed class WheelInputTransformer : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMilliseconds(100);
        private const long KeepAliveMs = 50;            // resend unchanged input this often
        private const long WheelSafetyCheckMs = 1000;   // re-check wheel count this often
        private const long InjectorRetryMs = 5000;      // retry creating the injector this often
        private const long ErrorLogIntervalMs = 10000;  // don't flood the event log
        private const int ErrorsBeforeInjectorReset = 50;

        private readonly ILogger logger;
        private readonly object lifecycleLock = new();
        private Thread? loopThread;
        private CancellationTokenSource? cancellation;

        private volatile bool injectionEnabled = true;
        private volatile bool wheelConnected;
        private readonly object readingLock = new();
        private RacingWheelReading latestReading;

        public WheelInputTransformer(ILogger<WheelInputTransformer> logger)
        {
            this.logger = logger;
        }

        public bool IsRunning
        {
            get { lock (lifecycleLock) return loopThread != null; }
        }

        public bool InjectionEnabled
        {
            get => injectionEnabled;
            set
            {
                if (injectionEnabled == value) return;
                injectionEnabled = value;
                logger.LogInformation("Wheel input {State}.", value ? "resumed" : "paused");
            }
        }

        public bool IsWheelConnected => wheelConnected;

        /// <summary>The most recent wheel reading (all zero when no wheel is connected).</summary>
        public RacingWheelReading LatestReading
        {
            get { lock (readingLock) return latestReading; }
        }

        private void SetLatestReading(RacingWheelReading reading)
        {
            lock (readingLock) latestReading = reading;
        }

        /// <summary>Starts the loop. Calling it again while running does nothing.</summary>
        public void Start()
        {
            lock (lifecycleLock)
            {
                if (loopThread != null) return;

                cancellation = new CancellationTokenSource();
                CancellationToken token = cancellation.Token;

                loopThread = new Thread(() => Loop(token))
                {
                    IsBackground = true,
                    Name = "Wheel input loop",
                    Priority = ThreadPriority.AboveNormal,
                };
                loopThread.Start();
            }
        }

        /// <summary>Stops the loop and releases all controller input. Can be started again later.</summary>
        public void Stop()
        {
            Thread? thread;
            lock (lifecycleLock)
            {
                if (loopThread == null) return;

                cancellation!.Cancel();
                thread = loopThread;
                loopThread = null;
            }

            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                logger.LogWarning("Wheel input loop did not stop within 2 seconds.");
            }

            lock (lifecycleLock)
            {
                cancellation?.Dispose();
                cancellation = null;
            }
        }

        public void Dispose() => Stop();

        private void Loop(CancellationToken token)
        {
            using var sleeper = new PreciseSleeper();
            using var tracker = new WheelTracker();

            logger.LogInformation("Wheel input loop started ({Timer} timer).", sleeper.IsHighResolution ? "high-resolution" : "fallback");

            var clock = Stopwatch.StartNew();
            InputInjector? injector = null;
            long nextInjectorAttempt = 0;
            long nextSafetyCheck = 0;
            long lastInjectTime = 0;
            long lastErrorLog = -ErrorLogIntervalMs;
            int consecutiveErrors = 0;
            GamepadReading lastSent = default;
            bool controllerActive = false; // true when we have sent non-neutral input

            void SendNeutral()
            {
                if (injector == null || !controllerActive) return;
                injector.InjectGamepadInput(new InjectedInputGamepadInfo(GamepadMapper.Neutral()));
                controllerActive = false;
            }

            while (!token.IsCancellationRequested)
            {
                long now = clock.ElapsedMilliseconds;

                try
                {
                    if (now >= nextSafetyCheck)
                    {
                        tracker.CheckForMissedChanges();
                        nextSafetyCheck = now + WheelSafetyCheckMs;
                    }

                    if (tracker.Refresh())
                    {
                        RacingWheel? main = tracker.MainWheel;
                        logger.LogInformation(main == null ? "Racing wheel disconnected." : "Racing wheel connected.");

                        // Release everything so no button or pedal gets stuck when a wheel is unplugged or swapped.
                        SendNeutral();
                    }

                    if (injector == null && now >= nextInjectorAttempt)
                    {
                        injector = InputInjector.TryCreate();
                        if (injector == null)
                        {
                            logger.LogWarning("Could not create the Windows input injector. Retrying in {Seconds} seconds.", InjectorRetryMs / 1000);
                            nextInjectorAttempt = now + InjectorRetryMs;
                        }
                    }

                    RacingWheel? wheel = tracker.MainWheel;
                    wheelConnected = wheel != null;

                    if (wheel == null)
                    {
                        SetLatestReading(default);
                    }
                    else
                    {
                        RacingWheelReading reading = wheel.GetCurrentReading();
                        SetLatestReading(reading);

                        if (!injectionEnabled)
                        {
                            SendNeutral();
                        }
                        else if (injector != null)
                        {
                            GamepadReading pad = GamepadMapper.ToGamepad(reading);

                            // Only send when something changed, plus a regular keep-alive.
                            if (!controllerActive || !GamepadMapper.SameInput(pad, lastSent) || now - lastInjectTime >= KeepAliveMs)
                            {
                                injector.InjectGamepadInput(new InjectedInputGamepadInfo(pad));
                                lastSent = pad;
                                lastInjectTime = now;
                                controllerActive = true;
                            }
                        }
                    }

                    consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    // Previously any error here silently killed the loop forever. Now we log and keep going.
                    consecutiveErrors++;

                    if (now - lastErrorLog >= ErrorLogIntervalMs)
                    {
                        logger.LogError(ex, "Error in wheel input loop ({Count} in a row).", consecutiveErrors);
                        lastErrorLog = now;
                    }

                    if (consecutiveErrors == ErrorsBeforeInjectorReset)
                    {
                        logger.LogWarning("Recreating the input injector after repeated errors.");
                        injector = null;
                        nextInjectorAttempt = now;
                        controllerActive = false;
                    }

                    sleeper.Sleep(ErrorBackoff);
                    continue;
                }

                sleeper.Sleep(PollInterval);
            }

            try
            {
                SendNeutral();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not release controller input while stopping.");
            }

            wheelConnected = false;
            SetLatestReading(default);
            logger.LogInformation("Wheel input loop stopped.");
        }
    }
}
