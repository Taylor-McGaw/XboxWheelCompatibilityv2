using System.Globalization;

namespace XboxWheelCompatibility.CommunicationInterface
{
    /// <summary>Problems the service can report to the status window.</summary>
    public static class WheelProblem
    {
        public const int None = 0;

        /// <summary>Windows' "Xbox Accessory Management Service" (XboxGipSvc) is disabled, so no virtual controller can be created.</summary>
        public const int XboxAccessoryServiceDisabled = 1;

        /// <summary>Sending input to Windows failed for another reason (details in Event Viewer).</summary>
        public const int InjectionFailed = 2;

        /// <summary>The Xbox Accessory Management Service is enabled but not responding (usually stuck starting; a restart fixes it).</summary>
        public const int XboxAccessoryServiceNotResponding = 3;
    }

    /// <summary>
    /// A snapshot of the wheel, sent from the service to the status window.
    /// Sent as a plain text string so the connection only ever carries simple data.
    /// </summary>
    /// <param name="Wheel">Steering, -1 (full left) to 1 (full right).</param>
    /// <param name="Throttle">0 to 1.</param>
    /// <param name="Brake">0 to 1.</param>
    /// <param name="Buttons">Windows.Gaming.Input.RacingWheelButtons flags.</param>
    /// <param name="Problem">One of <see cref="WheelProblem"/>.</param>
    /// <param name="VirtualXbox360">True when games see a virtual Xbox 360 controller (ViGEmBus) rather than the Windows method.</param>
    public readonly record struct LiveState(bool WheelConnected, bool Enabled, double Wheel, double Throttle, double Brake, long Buttons, int Problem = WheelProblem.None, bool VirtualXbox360 = false)
    {
        public string Format() => string.Join(';',
            WheelConnected ? "1" : "0",
            Enabled ? "1" : "0",
            Wheel.ToString("R", CultureInfo.InvariantCulture),
            Throttle.ToString("R", CultureInfo.InvariantCulture),
            Brake.ToString("R", CultureInfo.InvariantCulture),
            Buttons.ToString(CultureInfo.InvariantCulture),
            Problem.ToString(CultureInfo.InvariantCulture),
            VirtualXbox360 ? "1" : "0");

        public static LiveState Parse(string text)
        {
            string[] parts = text.Split(';');
            return new LiveState(
                parts[0] == "1",
                parts[1] == "1",
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture),
                double.Parse(parts[4], CultureInfo.InvariantCulture),
                long.Parse(parts[5], CultureInfo.InvariantCulture),
                parts.Length > 6 ? int.Parse(parts[6], CultureInfo.InvariantCulture) : WheelProblem.None,
                parts.Length > 7 && parts[7] == "1");
        }
    }
}
