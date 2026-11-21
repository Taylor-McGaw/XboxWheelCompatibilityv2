using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Gaming.Input;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>Why wheel input isn't reaching games, if it isn't.</summary>
    public enum InjectionProblem
    {
        None,

        /// <summary>Windows' "Xbox Accessory Management Service" (XboxGipSvc) is disabled.</summary>
        XboxAccessoryServiceDisabled,

        /// <summary>Some other error while sending input (see the event log).</summary>
        Failed,

        /// <summary>XboxGipSvc is enabled but not responding (CO_E_SERVEREXEC_FAILURE), usually stuck while starting.</summary>
        XboxAccessoryServiceNotResponding,
    }

    /// <summary>
    /// Reads the racing wheel about 1000 times a second and sends it to Windows as a virtual Xbox controller.
    /// Start/Stop control the background loop (used by the service). InjectionEnabled pauses/resumes
    /// sending input while the loop keeps tracking the wheel (used by the status window's Pause button).
    /// </summary>
    public sealed class WheelInputTransformer : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(2); // a Windows call this slow counts as stuck
        private const long KeepAliveMs = 50;            // resend unchanged input this often
        private const long WheelSafetyCheckMs = 1000;   // re-check wheel count this often
        private const long ErrorLogIntervalMs = 10000;  // don't flood the event log

        private readonly ILogger logger;
        private readonly object lifecycleLock = new();
        private Thread? loopThread;
        private CancellationTokenSource? cancellation;

        private volatile bool injectionEnabled = true;
        private volatile bool wheelConnected;
        private readonly object readingLock = new();
        private RacingWheelReading latestReading;
        private volatile InjectionWorker? worker;

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

        /// <summary>Why input isn't reaching games right now, or None if it is (or nothing has been sent yet).</summary>
        public InjectionProblem Problem
        {
            get
            {
                InjectionWorker? current = worker;
                if (current == null || !wheelConnected) return InjectionProblem.None;
                if (current.CurrentCallDuration > HangThreshold) return InjectionProblem.XboxAccessoryServiceNotResponding;
                return current.Problem;
            }
        }

        /// <summary>True when games see the wheel as a virtual Xbox 360 controller (ViGEmBus).</summary>
        public bool UsingVirtualXbox360 => worker?.UsingVirtualXbox360 ?? false;

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
            var sender = new InjectionWorker(logger);
            worker = sender;

            logger.LogInformation("Wheel input loop started ({Timer} timer).", sleeper.IsHighResolution ? "high-resolution" : "fallback");

            var clock = Stopwatch.StartNew();
            long nextSafetyCheck = 0;
            long lastPostTime = 0;
            long lastErrorLog = -ErrorLogIntervalMs;
            bool hangLogged = false;
            GamepadReading lastPosted = default;
            bool controllerActive = false; // true when we have sent non-neutral input

            void SendNeutral()
            {
                if (!controllerActive) return;
                sender.Post(GamepadMapper.Neutral());
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

                        // Windows calls happen on the sender thread, so a stuck call can't freeze this loop.
                        bool hung = sender.CurrentCallDuration > HangThreshold;
                        if (hung && !hangLogged)
                        {
                            logger.LogError("Windows is not responding to controller input (the \"Xbox Accessory Management Service\" may be stuck starting). " +
                                "The wheel is still read, but games can't see it. Restarting the PC usually fixes this.");
                        }
                        hangLogged = hung;
                    }

                    if (tracker.Refresh())
                    {
                        RacingWheel? main = tracker.MainWheel;
                        logger.LogInformation(main == null ? "Racing wheel disconnected." : "Racing wheel connected.");

                        // Release everything so no button or pedal gets stuck when a wheel is unplugged or swapped.
                        SendNeutral();
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
                        else
                        {
                            GamepadReading pad = GamepadMapper.ToGamepad(reading);

                            // Only send when something changed, plus a regular keep-alive.
                            if (!controllerActive || !GamepadMapper.SameInput(pad, lastPosted) || now - lastPostTime >= KeepAliveMs)
                            {
                                sender.Post(pad);
                                lastPosted = pad;
                                lastPostTime = now;
                                controllerActive = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Errors reading the wheel: log now and then and keep going.
                    if (now - lastErrorLog >= ErrorLogIntervalMs)
                    {
                        logger.LogError(ex, "Error reading the racing wheel.");
                        lastErrorLog = now;
                    }

                    sleeper.Sleep(ErrorBackoff);
                    continue;
                }

                sleeper.Sleep(PollInterval);
            }

            // Release all input, then stop the sender (it gives up quickly if Windows is stuck).
            sender.Post(GamepadMapper.Neutral());
            worker = null;
            sender.Dispose();

            wheelConnected = false;
            SetLatestReading(default);
            logger.LogInformation("Wheel input loop stopped.");
        }
    }
}
