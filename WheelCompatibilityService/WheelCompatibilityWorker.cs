using System.Net;
using ServiceWire.TcpIp;
using Windows.Gaming.Input;
using XboxWheelCompatibility.CommunicationInterface;
using XboxWheelCompatibility.WheelTransformer;

namespace XboxWheelCompatibility.WheelCompatibilityService
{
    public sealed class WheelCompatibilityWorker : BackgroundService, IWheelCompatibilityService
    {
        private readonly ILogger<WheelCompatibilityWorker> logger;
        private readonly WheelInputTransformer transformer;
        private TcpHost? tcpHost;

        public WheelCompatibilityWorker(ILogger<WheelCompatibilityWorker> logger, WheelInputTransformer transformer)
        {
            this.logger = logger;
            this.transformer = transformer;
        }

        // ---- Calls from the status window ----

        public string GetLiveState()
        {
            RacingWheelReading reading = transformer.LatestReading;
            return new LiveState(
                transformer.IsWheelConnected,
                transformer.InjectionEnabled,
                reading.Wheel,
                reading.Throttle,
                reading.Brake,
                (long)reading.Buttons,
                transformer.Problem switch
                {
                    InjectionProblem.XboxAccessoryServiceDisabled => WheelProblem.XboxAccessoryServiceDisabled,
                    InjectionProblem.Failed => WheelProblem.InjectionFailed,
                    InjectionProblem.XboxAccessoryServiceNotResponding => WheelProblem.XboxAccessoryServiceNotResponding,
                    _ => WheelProblem.None,
                },
                transformer.UsingVirtualXbox360
            ).Format();
        }

        public void SetEnabled(bool enabled) => transformer.InjectionEnabled = enabled;

        // ---- Service lifetime ----

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            transformer.Start();

            try
            {
                // Loopback only: the old code listened on every network interface, so other devices
                // on your network could control this service (which runs with full system rights).
                tcpHost = new TcpHost(new IPEndPoint(IPAddress.Loopback, IWheelCompatibilityService.Port));
                tcpHost.AddService<IWheelCompatibilityService>(this);
                tcpHost.Open();
            }
            catch (Exception ex)
            {
                // The wheel still works without this; only the status window can't connect.
                logger.LogError(ex, "Could not open the status connection on port {Port}. The wheel still works, but the status window won't.", IWheelCompatibilityService.Port);
                tcpHost = null;
            }

            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                tcpHost?.Close();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error closing the status connection.");
            }

            transformer.Stop();

            await base.StopAsync(cancellationToken);
        }
    }
}
