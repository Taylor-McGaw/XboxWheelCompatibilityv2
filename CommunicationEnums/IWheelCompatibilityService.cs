namespace XboxWheelCompatibility.CommunicationInterface
{
    /// <summary>What the status window can ask the background service.</summary>
    public interface IWheelCompatibilityService
    {
        /// <summary>Only listens on this PC (127.0.0.1), never on the network.</summary>
        public const int Port = 16581;

        /// <summary>Everything the status window shows, in one call. Format: see <see cref="LiveState"/>.</summary>
        public string GetLiveState();

        /// <summary>Pause (false) or resume (true) sending wheel input.</summary>
        public void SetEnabled(bool enabled);
    }
}
