# Xbox Wheel Compatibility v2
_An updated fork of [the original Xbox Wheel Compatibility](https://github.com/camren-m/XboxWheelCompatibility) by Camren Mumme._

A simple tool to make unsupported Xbox racing wheels work on Windows 10/11. It runs in the background and makes your wheel show up as an Xbox controller.

## Notice
Please ensure you have all Xbox drivers that are not included in Windows by default **uninstalled** before installing this software.

## Usage
Download the latest installer from the [Releases page](https://github.com/Taylor-McGaw/XboxWheelCompatibilityv2/releases) and run `WheelCompatibilityInstaller.exe`. After it finishes, your Xbox-exclusive racing wheel should work on your PC. You don't need to install .NET separately.

Open **Xbox Wheel Compatibility** from the Start menu to check and test your wheel:
- **Steering**: the on-screen wheel turns with yours.
- **Pedals**: live throttle and brake bars.
- **Buttons**: each button lights up when you press it, labeled with the Xbox button it sends. Buttons that aren't mapped yet are listed underneath.
- **Pause / Resume**: use Pause for games that already support your wheel directly, so they don't get doubled input. Pausing lasts until you press Resume or restart the PC.
- If the background service isn't running, the window says so and offers a **Start service** button.

If something goes wrong, check **Event Viewer > Windows Logs > Application** for entries from `WheelCompatibilityService`.

## Button layout
| Wheel | Xbox controller |
|---|---|
| Steering | Left stick (left/right) |
| Throttle | Right trigger |
| Brake | Left trigger |
| Right / left paddle | RB / LB |
| D-pad | D-pad |
| Button1 / Button2 | Menu / View |
| Button3-6 | A / B / X / Y |

## Requirements
- **Recommended: [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) (free driver).** When it's installed, your wheel appears as a virtual Xbox 360 controller that every game can see, including F1 25. Without it, the app uses Windows' own method, which only some games see and which needs the Windows "Xbox Accessory Management Service".
- Windows 11, or Windows 10 version 22H2 or above (64-bit)

## Tested with
- Thrustmaster Ferrari 458 Spider Racing Wheel (Windows 10 22H2)
- Thrustmaster Ferrari 458 Spider Racing Wheel (Windows 11 22H2)

## Building
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish WheelCompatibilityService -c Release
dotnet publish WheelCompatibilityConfigurator -c Release
dotnet build WheelCompatibilityInstaller -c Release
```

The two programs end up in `<project>\bin\Release\publish\`. The installer is in `WheelCompatibilityInstaller\bin\Release\`.

## Changes in 2.0.0
- **Works with F1 25 and other games:** when [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) is installed, the wheel shows up as a virtual Xbox 360 controller that games detect through XInput. Tested with F1 25. Without ViGEmBus, the app falls back to Windows' own method.
- A stuck Windows controller service can no longer freeze the app. The wheel keeps showing live, and the window explains what's wrong.
- The status window warns when the Windows "Xbox Accessory Management Service" is disabled or stuck, with a **Fix it** button for the disabled case.
- Clearer event log messages, and automatic retries once a problem is fixed.
- The status window shows whether games see an Xbox 360 controller (ViGEmBus) or the Windows controller.

## Changes in 1.3.0
- The wheel is now read about 1000 times per second instead of about 64, which removes up to ~16 ms of input lag.
- Fixed Stop never working and Start running the loop twice.
- Errors no longer stop the wheel silently. They are logged and the loop keeps running.
- Buttons and pedals are released when the wheel is unplugged or the service stops.
- The status connection now only accepts connections from this PC, not from your network.
- New status window with a live test panel (steering, pedals, buttons), a Pause button, and a Start service button. It no longer crashes when the service isn't running.
- Upgraded from .NET 6 (no longer supported) to .NET 10. The runtime is built in, so the installer no longer downloads .NET.
- The installer is now 64-bit.
