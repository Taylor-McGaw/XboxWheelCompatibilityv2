using Windows.Gaming.Input;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>Turns a racing wheel reading into an Xbox controller reading.</summary>
    internal static class GamepadMapper
    {
        // Same button layout as the original version.
        private static readonly (RacingWheelButtons Wheel, GamepadButtons Gamepad)[] ButtonMap =
        {
            (RacingWheelButtons.DPadUp, GamepadButtons.DPadUp),
            (RacingWheelButtons.DPadDown, GamepadButtons.DPadDown),
            (RacingWheelButtons.DPadLeft, GamepadButtons.DPadLeft),
            (RacingWheelButtons.DPadRight, GamepadButtons.DPadRight),
            (RacingWheelButtons.NextGear, GamepadButtons.RightShoulder),
            (RacingWheelButtons.PreviousGear, GamepadButtons.LeftShoulder),
            (RacingWheelButtons.Button1, GamepadButtons.Menu),
            (RacingWheelButtons.Button2, GamepadButtons.View),
            (RacingWheelButtons.Button3, GamepadButtons.A),
            (RacingWheelButtons.Button4, GamepadButtons.B),
            (RacingWheelButtons.Button5, GamepadButtons.X),
            (RacingWheelButtons.Button6, GamepadButtons.Y),
        };

        public static GamepadReading ToGamepad(RacingWheelReading wheel)
        {
            GamepadButtons buttons = GamepadButtons.None;

            foreach (var (wheelButton, gamepadButton) in ButtonMap)
            {
                if ((wheel.Buttons & wheelButton) != 0) buttons |= gamepadButton;
            }

            return new GamepadReading(
                Timestamp(),
                buttons,
                wheel.Brake,      // left trigger
                wheel.Throttle,   // right trigger
                wheel.Wheel,      // left stick X (steering)
                0,
                0, 0
            );
        }

        public static GamepadReading Neutral() => new(Timestamp(), GamepadButtons.None, 0, 0, 0, 0, 0, 0);

        /// <summary>Compares everything except the timestamp.</summary>
        public static bool SameInput(GamepadReading a, GamepadReading b) =>
            a.Buttons == b.Buttons &&
            a.LeftTrigger == b.LeftTrigger &&
            a.RightTrigger == b.RightTrigger &&
            a.LeftThumbstickX == b.LeftThumbstickX &&
            a.LeftThumbstickY == b.LeftThumbstickY &&
            a.RightThumbstickX == b.RightThumbstickX &&
            a.RightThumbstickY == b.RightThumbstickY;

        private static ulong Timestamp() => (ulong)DateTime.UtcNow.Ticks;
    }
}
