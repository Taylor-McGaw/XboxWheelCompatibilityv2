using System.Globalization;

namespace XboxWheelCompatibility.CommunicationInterface
{
    /// <summary>
    /// A snapshot of the wheel, sent from the service to the status window.
    /// Sent as a plain text string so the connection only ever carries simple data.
    /// </summary>
    /// <param name="Wheel">Steering, -1 (full left) to 1 (full right).</param>
    /// <param name="Throttle">0 to 1.</param>
    /// <param name="Brake">0 to 1.</param>
    /// <param name="Buttons">Windows.Gaming.Input.RacingWheelButtons flags.</param>
    public readonly record struct LiveState(bool WheelConnected, bool Enabled, double Wheel, double Throttle, double Brake, long Buttons)
    {
        public string Format() => string.Join(';',
            WheelConnected ? "1" : "0",
            Enabled ? "1" : "0",
            Wheel.ToString("R", CultureInfo.InvariantCulture),
            Throttle.ToString("R", CultureInfo.InvariantCulture),
            Brake.ToString("R", CultureInfo.InvariantCulture),
            Buttons.ToString(CultureInfo.InvariantCulture));

        public static LiveState Parse(string text)
        {
            string[] parts = text.Split(';');
            return new LiveState(
                parts[0] == "1",
                parts[1] == "1",
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture),
                double.Parse(parts[4], CultureInfo.InvariantCulture),
                long.Parse(parts[5], CultureInfo.InvariantCulture));
        }
    }
}
