using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Windows.Gaming.Input;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>
    /// A virtual Xbox 360 controller created through the ViGEmBus driver (github.com/nefarius/ViGEmBus).
    /// Games see it through XInput, DirectInput and Windows.Gaming.Input, and it doesn't depend on the
    /// Windows "Xbox Accessory Management Service". Only available when ViGEmBus is installed.
    /// </summary>
    internal sealed class VirtualXbox360Output : IDisposable
    {
        private static readonly (GamepadButtons From, Xbox360Button To)[] ButtonMap =
        {
            (GamepadButtons.A, Xbox360Button.A),
            (GamepadButtons.B, Xbox360Button.B),
            (GamepadButtons.X, Xbox360Button.X),
            (GamepadButtons.Y, Xbox360Button.Y),
            (GamepadButtons.LeftShoulder, Xbox360Button.LeftShoulder),
            (GamepadButtons.RightShoulder, Xbox360Button.RightShoulder),
            (GamepadButtons.Menu, Xbox360Button.Start),
            (GamepadButtons.View, Xbox360Button.Back),
            (GamepadButtons.DPadUp, Xbox360Button.Up),
            (GamepadButtons.DPadDown, Xbox360Button.Down),
            (GamepadButtons.DPadLeft, Xbox360Button.Left),
            (GamepadButtons.DPadRight, Xbox360Button.Right),
            (GamepadButtons.LeftThumbstick, Xbox360Button.LeftThumb),
            (GamepadButtons.RightThumbstick, Xbox360Button.RightThumb),
        };

        private readonly ViGEmClient client;
        private readonly IXbox360Controller controller;

        /// <summary>Connects a new virtual controller. Throws VigemBusNotFoundException if ViGEmBus isn't installed.</summary>
        public VirtualXbox360Output()
        {
            client = new ViGEmClient();
            try
            {
                controller = client.CreateXbox360Controller();
                controller.AutoSubmitReport = false;
                controller.Connect();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void Send(GamepadReading reading)
        {
            foreach (var (from, to) in ButtonMap)
            {
                controller.SetButtonState(to, (reading.Buttons & from) != 0);
            }

            controller.SetSliderValue(Xbox360Slider.LeftTrigger, ToTrigger(reading.LeftTrigger));
            controller.SetSliderValue(Xbox360Slider.RightTrigger, ToTrigger(reading.RightTrigger));
            controller.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis(reading.LeftThumbstickX));
            controller.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis(reading.LeftThumbstickY));
            controller.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis(reading.RightThumbstickX));
            controller.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis(reading.RightThumbstickY));
            controller.SubmitReport();
        }

        private static byte ToTrigger(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

        private static short ToAxis(double value) => (short)Math.Round(Math.Clamp(value, -1, 1) * 32767);

        public void Dispose()
        {
            try { controller.Disconnect(); } catch { }
            client.Dispose();
        }
    }
}
