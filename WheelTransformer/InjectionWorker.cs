using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Gaming.Input;
using Windows.UI.Input.Preview.Injection;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>
    /// Sends controller input to Windows on its own thread.
    /// On some PCs the Windows call never returns (for example when the "Xbox Accessory Management
    /// Service" is stuck starting). Doing it here means that can never freeze wheel reading or the
    /// status window; we just report that Windows isn't responding.
    /// Only the newest reading is kept: if Windows is slow, older readings are skipped, not queued.
    /// </summary>
    internal sealed class InjectionWorker : IDisposable
    {
        private const long InjectorRetryMs = 5000;
        private const long ErrorLogIntervalMs = 10000;
        private const long ServiceDisabledLogIntervalMs = 5 * 60 * 1000;
        private const int ErrorsBeforeInjectorReset = 50;
        private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan ServiceDisabledBackoff = TimeSpan.FromSeconds(1);

        // HRESULT_FROM_WIN32(ERROR_SERVICE_DISABLED): XboxGipSvc is disabled.
        private const int ServiceDisabledHResult = unchecked((int)0x80070422);
        // CO_E_SERVEREXEC_FAILURE: XboxGipSvc is enabled but stuck starting.
        private const int ServerExecFailedHResult = unchecked((int)0x80080005);

        private readonly ILogger logger;
        private readonly Thread thread;
        private readonly AutoResetEvent signal = new(false);
        private readonly object sync = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private GamepadReading? pending;
        private volatile bool disposed;
        private volatile InjectionProblem problem = InjectionProblem.None;
        private long callStartedMs = -1; // -1 = no call in progress; read/written with Interlocked

        private const long ViGEmRetryMs = 30000; // look for ViGEmBus again this often (so installing it later just works)

        private VirtualXbox360Output? virtualPad;
        private long nextViGEmAttempt;
        private bool loggedViGEmMissing;

        private InputInjector? injector;
        private long nextInjectorAttempt;
        private long lastErrorLog = -ErrorLogIntervalMs;
        private int consecutiveErrors;

        public InjectionWorker(ILogger logger)
        {
            this.logger = logger;
            thread = new Thread(Run) { IsBackground = true, Name = "Wheel input sender", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        /// <summary>True when input goes to a virtual Xbox 360 controller through ViGEmBus.</summary>
        public bool UsingVirtualXbox360 => virtualPad != null;

        /// <summary>Result of the most recent finished send.</summary>
        public InjectionProblem Problem => problem;

        /// <summary>How long the current Windows call has been running, or zero if none is running.</summary>
        public TimeSpan CurrentCallDuration
        {
            get
            {
                long started = Interlocked.Read(ref callStartedMs);
                return started < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds - started);
            }
        }

        /// <summary>Queue a reading to send. Replaces any reading that hasn't been sent yet.</summary>
        public void Post(GamepadReading reading)
        {
            lock (sync) pending = reading;
            signal.Set();
        }

        private void Run()
        {
            while (true)
            {
                signal.WaitOne();

                GamepadReading? next;
                lock (sync)
                {
                    next = pending;
                    pending = null;
                }

                if (next is GamepadReading reading && !Send(reading))
                {
                    Thread.Sleep(problem == InjectionProblem.XboxAccessoryServiceDisabled ? ServiceDisabledBackoff : ErrorBackoff);
                    signal.Set(); // try again with whatever is newest
                }

                if (disposed) return;
            }
        }

        private bool Send(GamepadReading reading)
        {
            long now = clock.ElapsedMilliseconds;

            // Preferred: a virtual Xbox 360 controller through ViGEmBus, which every game's controller
            // system (XInput, DirectInput, Windows.Gaming.Input) can see.
            if (TrySendViaViGEm(reading, now)) return true;

            if (injector == null)
            {
                if (now < nextInjectorAttempt) return false;

                injector = InputInjector.TryCreate();
                if (injector == null)
                {
                    logger.LogWarning("Could not create the Windows input injector. Retrying in {Seconds} seconds.", InjectorRetryMs / 1000);
                    nextInjectorAttempt = now + InjectorRetryMs;
                    problem = InjectionProblem.Failed;
                    return false;
                }
            }

            Interlocked.Exchange(ref callStartedMs, now);
            try
            {
                injector.InjectGamepadInput(new InjectedInputGamepadInfo(reading));

                if (consecutiveErrors > 0 || problem != InjectionProblem.None)
                {
                    logger.LogInformation("Wheel input is reaching Windows again.");
                }

                problem = InjectionProblem.None;
                consecutiveErrors = 0;
                return true;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                bool serviceDisabled = ex.HResult == ServiceDisabledHResult;
                problem = serviceDisabled ? InjectionProblem.XboxAccessoryServiceDisabled
                    : ex.HResult == ServerExecFailedHResult ? InjectionProblem.XboxAccessoryServiceNotResponding
                    : InjectionProblem.Failed;

                if (serviceDisabled)
                {
                    if (consecutiveErrors == 1 || now - lastErrorLog >= ServiceDisabledLogIntervalMs)
                    {
                        logger.LogError("Games can't see the wheel: the Windows \"Xbox Accessory Management Service\" (XboxGipSvc) is disabled. " +
                            "Open the Xbox Wheel Compatibility window and click Fix, or set that service to Manual in services.msc.");
                        lastErrorLog = now;
                    }
                }
                else if (now - lastErrorLog >= ErrorLogIntervalMs)
                {
                    logger.LogError(ex, "Could not send wheel input to Windows ({Count} in a row).", consecutiveErrors);
                    lastErrorLog = now;
                }

                // Start over with a fresh injector now and then, in case the cause was fixed.
                if (serviceDisabled || consecutiveErrors % ErrorsBeforeInjectorReset == 0)
                {
                    injector = null;
                    nextInjectorAttempt = now;
                }

                return false;
            }
            finally
            {
                Interlocked.Exchange(ref callStartedMs, -1);
            }
        }

        private bool TrySendViaViGEm(GamepadReading reading, long now)
        {
            if (virtualPad == null)
            {
                if (now < nextViGEmAttempt) return false;

                try
                {
                    virtualPad = new VirtualXbox360Output();
                    logger.LogInformation("Using a virtual Xbox 360 controller (ViGEmBus).");
                    loggedViGEmMissing = false;
                }
                catch (Exception ex)
                {
                    nextViGEmAttempt = now + ViGEmRetryMs;
                    if (!loggedViGEmMissing)
                    {
                        logger.LogInformation("ViGEmBus is not available ({Reason}). Using the Windows controller method instead.", ex.GetType().Name);
                        loggedViGEmMissing = true;
                    }
                    return false;
                }
            }

            Interlocked.Exchange(ref callStartedMs, now);
            try
            {
                virtualPad.Send(reading);
                problem = InjectionProblem.None;
                consecutiveErrors = 0;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The virtual Xbox 360 controller stopped working. Trying the Windows controller method instead.");
                virtualPad.Dispose();
                virtualPad = null;
                nextViGEmAttempt = now + ViGEmRetryMs;
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref callStartedMs, -1);
            }
        }

        /// <summary>Stops the thread after it sends anything still waiting (gives up after half a second if Windows is stuck).</summary>
        public void Dispose()
        {
            disposed = true;
            signal.Set();
            if (thread.Join(TimeSpan.FromMilliseconds(500)))
            {
                // Remove the virtual controller so games don't keep seeing a dead one.
                virtualPad?.Dispose();
                virtualPad = null;
            }
        }
    }
}
