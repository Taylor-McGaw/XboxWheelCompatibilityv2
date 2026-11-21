using System.Net;
using ServiceWire.TcpIp;
using XboxWheelCompatibility.CommunicationInterface;

namespace WheelCompatibilityConfigurator
{
    /// <summary>
    /// Talks to the background service. Never throws: if the service is not running,
    /// it returns null and reconnects automatically once the service is back.
    /// </summary>
    internal sealed class Communicator : IDisposable
    {
        private readonly object sync = new();
        private TcpClient<IWheelCompatibilityService>? client;

        /// <summary>The current wheel state, or null if the service can't be reached.</summary>
        public LiveState? GetLiveState()
        {
            lock (sync)
            {
                try
                {
                    return LiveState.Parse(Connect().GetLiveState());
                }
                catch
                {
                    Disconnect();
                    return null;
                }
            }
        }

        public bool TrySetEnabled(bool enabled)
        {
            lock (sync)
            {
                try
                {
                    Connect().SetEnabled(enabled);
                    return true;
                }
                catch
                {
                    Disconnect();
                    return false;
                }
            }
        }

        private IWheelCompatibilityService Connect()
        {
            client ??= new TcpClient<IWheelCompatibilityService>(
                new TcpEndPoint(new IPEndPoint(IPAddress.Loopback, IWheelCompatibilityService.Port)));
            return client.Proxy;
        }

        private void Disconnect()
        {
            try { client?.Dispose(); } catch { }
            client = null;
        }

        public void Dispose()
        {
            lock (sync) Disconnect();
        }
    }
}
