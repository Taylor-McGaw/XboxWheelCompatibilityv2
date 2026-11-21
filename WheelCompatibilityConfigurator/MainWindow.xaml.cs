using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Gaming.Input;
using XboxWheelCompatibility.CommunicationInterface;

namespace WheelCompatibilityConfigurator
{
    public partial class MainWindow : Window
    {
        private const string ServiceName = "WheelCompatibilityService";

        // How far the on-screen wheel turns at full lock. The Ferrari 458 Spider turns 240° lock to lock.
        private const double VisualMaxAngle = 120;

        private static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(33);   // ~30 updates/sec while connected
        private static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(1);

        private readonly Communicator communicator = new();
        private readonly DispatcherTimer refreshTimer = new() { Interval = LiveInterval };
        private readonly List<ButtonChip> chips = new();
        private readonly DemoSource? demo;

        private LiveState? lastState;
        private DateTime nextOfflineAttempt = DateTime.MinValue;
        private bool refreshing;

        public MainWindow()
        {
            InitializeComponent();
            BuildButtonChips();

            // Preview modes: "--demo" fake wheel data, "--demo-offline" service not running,
            // "--demo-problem" the Xbox Accessory Management Service warning.
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("--demo")) demo = new DemoSource(offline: false, problem: WheelProblem.None);
            if (args.Contains("--demo-offline")) demo = new DemoSource(offline: true, problem: WheelProblem.None);
            if (args.Contains("--demo-problem") || args.Contains("--demo-stuck")) demo = new DemoSource(offline: false, problem: WheelProblem.XboxAccessoryServiceDisabled);

            SourceInitialized += (_, _) => UseDarkTitleBar();
            refreshTimer.Tick += async (_, _) => await RefreshAsync();
            Loaded += async (_, _) =>
            {
                await RefreshAsync();
                refreshTimer.Start();
            };
            Closed += (_, _) =>
            {
                refreshTimer.Stop();
                communicator.Dispose();
            };
        }

        // ---------------- Refreshing ----------------

        private async Task RefreshAsync()
        {
            if (refreshing) return;

            // Don't hammer the connection while the service is down.
            if (lastState == null && DateTime.UtcNow < nextOfflineAttempt) return;

            refreshing = true;
            try
            {
                LiveState? state = demo != null ? demo.Next() : await Task.Run(communicator.GetLiveState);
                lastState = state;
                if (state == null) nextOfflineAttempt = DateTime.UtcNow + OfflineRetry;
                Render(state);
            }
            finally
            {
                refreshing = false;
            }
        }

        private void Render(LiveState? state)
        {
            if (state is not LiveState s)
            {
                OfflinePanel.Visibility = Visibility.Visible;
                TestPanel.Visibility = Visibility.Hidden; // keep its size so the window doesn't jump
                NoWheelOverlay.Visibility = Visibility.Collapsed;
                SetStatus("Service off", (Brush)FindResource("BrakeBrush"));
                FooterTitle.Text = "Not running";
                FooterText.Text = "Games can't see your wheel until the service is running.";
                ToggleButton.IsEnabled = false;
                ToggleButton.Content = "Pause";
                return;
            }

            OfflinePanel.Visibility = Visibility.Collapsed;
            TestPanel.Visibility = Visibility.Visible;
            NoWheelOverlay.Visibility = s.WheelConnected ? Visibility.Collapsed : Visibility.Visible;

            bool hasProblem = s.WheelConnected && s.Problem != WheelProblem.None;
            RenderProblem(hasProblem ? s.Problem : WheelProblem.None);

            if (!s.WheelConnected) SetStatus("No wheel", (Brush)FindResource("SubtleTextBrush"));
            else if (!s.Enabled) SetStatus("Paused", (Brush)FindResource("WarningBrush"));
            else if (hasProblem) SetStatus("Not reaching games", (Brush)FindResource("WarningBrush"));
            else SetStatus("Connected", (Brush)FindResource("AccentBrush"));

            FooterTitle.Text = !s.Enabled ? "Paused" : hasProblem ? "Not reaching games" : "Sending to games";
            FooterText.Text = !s.Enabled
                ? "Games don't get controller input from your wheel. Use this for games with built-in wheel support."
                : hasProblem
                ? "Your wheel is detected, but Windows won't create the Xbox controller. See the warning above."
                : s.VirtualXbox360
                ? "Games see your wheel as an Xbox 360 controller (via ViGEmBus)."
                : "Games see your wheel as an Xbox controller.";
            ToggleButton.IsEnabled = true;
            ToggleButton.Content = s.Enabled ? "Pause" : "Resume";

            RenderSteering(s.Wheel);
            RenderPedal(ThrottleFill, ThrottleText, s.Throttle);
            RenderPedal(BrakeFill, BrakeText, s.Brake);
            RenderButtons((RacingWheelButtons)s.Buttons);
        }

        private int shownProblem = -1;
        private DateTime? fixAppliedAt;
        private bool restartHintShown;
        private static readonly TimeSpan FixGracePeriod = TimeSpan.FromSeconds(15);

        private void RenderProblem(int problem)
        {
            if (problem == WheelProblem.None)
            {
                fixAppliedAt = null;
                restartHintShown = false;
            }
            else if (problem == WheelProblem.XboxAccessoryServiceDisabled && fixAppliedAt is DateTime appliedAt
                     && !restartHintShown && DateTime.UtcNow - appliedAt > FixGracePeriod)
            {
                // The setting was changed, but the Windows service got stuck while starting. A restart clears that.
                restartHintShown = true;
                shownProblem = problem;
                ProblemText.Text = "The service was turned on, but Windows hasn't finished starting it. Restart your PC to finish the fix, then open your game.";
                FixButton.Visibility = Visibility.Collapsed;
                ProblemBanner.Visibility = Visibility.Visible;
                return;
            }

            if (problem == shownProblem) return;
            shownProblem = problem;

            switch (problem)
            {
                case WheelProblem.XboxAccessoryServiceDisabled:
                    ProblemText.Text = "The Windows \"Xbox Accessory Management Service\" is turned off (often by tweak or debloat tools). Click Fix it to turn it back on, then restart your game.";
                    FixButton.Visibility = Visibility.Visible;
                    ProblemBanner.Visibility = Visibility.Visible;
                    break;

                case WheelProblem.XboxAccessoryServiceNotResponding:
                    ProblemText.Text = "Windows isn't responding: its \"Xbox Accessory Management Service\" is stuck starting. Restart your PC. If that doesn't help, run Windows Update and the Windows repair tools (sfc /scannow).";
                    FixButton.Visibility = Visibility.Collapsed;
                    ProblemBanner.Visibility = Visibility.Visible;
                    break;

                case WheelProblem.InjectionFailed:
                    ProblemText.Text = "Windows refused the virtual Xbox controller. Check Event Viewer > Windows Logs > Application for WheelCompatibilityService errors.";
                    FixButton.Visibility = Visibility.Collapsed;
                    ProblemBanner.Visibility = Visibility.Visible;
                    break;

                default:
                    ProblemBanner.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private async void FixButton_Click(object sender, RoutedEventArgs e)
        {
            FixButton.IsEnabled = false;
            ProblemText.Text = "Turning the service on...";

            int exitCode;
            try
            {
                // Set the Xbox Accessory Management Service back to its Windows default (Manual) and start it.
                // Needs admin rights, so Windows shows a permission prompt.
                exitCode = await Task.Run(() =>
                {
                    using Process? process = Process.Start(new ProcessStartInfo("cmd.exe", "/c sc config XboxGipSvc start= demand && sc start XboxGipSvc")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    process?.WaitForExit();
                    return process?.ExitCode ?? -1;
                });
            }
            catch (Win32Exception)
            {
                exitCode = -2; // permission prompt was cancelled
            }

            FixButton.IsEnabled = true;
            shownProblem = -1; // let the next refresh redraw the banner

            if (exitCode is 0 or 1056)
            {
                fixAppliedAt = DateTime.UtcNow;
                if (demo != null && !Environment.GetCommandLineArgs().Contains("--demo-stuck")) demo.ClearProblem();
                ProblemText.Text = "Done. Checking again... Restart your game once this warning disappears.";
                shownProblem = WheelProblem.XboxAccessoryServiceDisabled; // keep this message until the service reports success
                return;
            }

            ProblemText.Text = exitCode == -2
                ? "Permission was not given. Click Fix it and choose Yes."
                : $"Couldn't turn the service on (error {exitCode}). Open services.msc, find \"Xbox Accessory Management Service\", set Startup type to Manual and click Start.";
            shownProblem = WheelProblem.XboxAccessoryServiceDisabled;
        }

        private void SetStatus(string text, Brush color)
        {
            StatusText.Text = text;
            StatusDot.Fill = color;
        }

        private void RenderSteering(double wheel)
        {
            wheel = Math.Clamp(wheel, -1, 1);
            WheelRotation.Angle = wheel * VisualMaxAngle;

            int percent = (int)Math.Round(Math.Abs(wheel) * 100);
            SteeringText.Text = percent == 0 ? "Centered" : $"{(wheel < 0 ? "Left" : "Right")} {percent}%";

            double half = SteeringTrack.ActualWidth / 2;
            double width = Math.Abs(wheel) * half;
            SteeringFill.Width = width;
            SteeringFill.Margin = new Thickness(wheel >= 0 ? half : half - width, 0, 0, 0);
        }

        private static void RenderPedal(Border fill, TextBlock label, double value)
        {
            value = Math.Clamp(value, 0, 1);
            double trackHeight = ((FrameworkElement)fill.Parent).Height;
            fill.Height = value * trackHeight;
            label.Text = $"{Math.Round(value * 100)}%";
        }

        private void RenderButtons(RacingWheelButtons pressed)
        {
            RacingWheelButtons mapped = RacingWheelButtons.None;
            foreach (ButtonChip chip in chips)
            {
                chip.SetLit((pressed & chip.Flag) != 0);
                mapped |= chip.Flag;
            }

            RacingWheelButtons extra = pressed & ~mapped;
            ExtraButtonsText.Text = extra != RacingWheelButtons.None
                ? $"Also pressed (not sent to games yet): {extra}"
                : "Press any button on your wheel to test it.";
            ExtraButtonsText.Foreground = (Brush)FindResource(extra != RacingWheelButtons.None ? "WarningBrush" : "SubtleTextBrush");
        }

        // ---------------- Button chips ----------------

        private void BuildButtonChips()
        {
            var green = Brush("#2EA043");
            var red = Brush("#E5534B");
            var blue = Brush("#3D8BFF");
            var yellow = Brush("#D4A72C");
            var neutral = Brush("#5B6372");

            (string Label, string Sub, RacingWheelButtons Flag, Brush Lit)[] layout =
            {
                ("◀", "Paddle · LB", RacingWheelButtons.PreviousGear, neutral),
                ("▶", "Paddle · RB", RacingWheelButtons.NextGear, neutral),
                ("⧉", "View", RacingWheelButtons.Button2, neutral),
                ("≡", "Menu", RacingWheelButtons.Button1, neutral),
                ("↑", "D-pad", RacingWheelButtons.DPadUp, neutral),
                ("↓", "D-pad", RacingWheelButtons.DPadDown, neutral),
                ("←", "D-pad", RacingWheelButtons.DPadLeft, neutral),
                ("→", "D-pad", RacingWheelButtons.DPadRight, neutral),
                ("A", "Button 3", RacingWheelButtons.Button3, green),
                ("B", "Button 4", RacingWheelButtons.Button4, red),
                ("X", "Button 5", RacingWheelButtons.Button5, blue),
                ("Y", "Button 6", RacingWheelButtons.Button6, yellow),
            };

            foreach (var (label, sub, flag, lit) in layout)
            {
                var chip = new ButtonChip(label, sub, flag, lit, (Brush)FindResource("TrackBrush"), (Brush)FindResource("SubtleTextBrush"));
                chips.Add(chip);
                ButtonGrid.Children.Add(chip.Element);
            }
        }

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private sealed class ButtonChip
        {
            private readonly Border border;
            private readonly TextBlock labelText;
            private readonly TextBlock subText;
            private readonly Brush litBrush;
            private readonly Brush idleBrush;
            private readonly Brush subtleBrush;
            private bool lit;

            public ButtonChip(string label, string sub, RacingWheelButtons flag, Brush litBrush, Brush idleBrush, Brush subtleBrush)
            {
                Flag = flag;
                this.litBrush = litBrush;
                this.idleBrush = idleBrush;
                this.subtleBrush = subtleBrush;

                labelText = new TextBlock { Text = label, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
                subText = new TextBlock { Text = sub, FontSize = 9.5, Foreground = subtleBrush, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

                border = new Border
                {
                    Background = idleBrush,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(3),
                    Padding = new Thickness(2, 6, 2, 6),
                    Child = new StackPanel { Children = { labelText, subText } },
                    ToolTip = $"Wheel: {flag}",
                };
            }

            public RacingWheelButtons Flag { get; }
            public UIElement Element => border;

            public void SetLit(bool value)
            {
                if (lit == value) return;
                lit = value;
                border.Background = value ? litBrush : idleBrush;
                subText.Foreground = value ? Brushes.White : subtleBrush;
            }
        }

        // ---------------- Actions ----------------

        private async void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (lastState is not LiveState s) return;
            bool enable = !s.Enabled;
            ToggleButton.IsEnabled = false;

            bool ok = demo != null ? demo.SetEnabled(enable) : await Task.Run(() => communicator.TrySetEnabled(enable));
            if (!ok)
            {
                MessageBox.Show(this, "Couldn't reach the Wheel Compatibility service.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            await RefreshAsync();
        }

        private async void StartServiceButton_Click(object sender, RoutedEventArgs e)
        {
            StartServiceButton.IsEnabled = false;
            OfflineHintText.Text = "Starting...";

            int exitCode;
            try
            {
                // Starting a service needs admin rights, so Windows shows a permission prompt.
                exitCode = await Task.Run(() =>
                {
                    using Process? process = Process.Start(new ProcessStartInfo("sc.exe", $"start {ServiceName}")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    process?.WaitForExit();
                    return process?.ExitCode ?? -1;
                });
            }
            catch (Win32Exception)
            {
                exitCode = -2; // permission prompt was cancelled
            }

            OfflineHintText.Text = exitCode switch
            {
                0 or 1056 => "Service started. Connecting...",
                1060 => "The service isn't installed. Please run the Xbox Wheel Compatibility installer again.",
                -2 => "Permission was not given. Click Start service and choose Yes.",
                _ => $"Couldn't start the service (error {exitCode}). Try restarting your PC or reinstalling.",
            };

            StartServiceButton.IsEnabled = true;
            nextOfflineAttempt = DateTime.MinValue;
            await Task.Delay(1000);
            await RefreshAsync();
        }

        // ---------------- Window chrome ----------------

        private void UseDarkTitleBar()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int enabled = 1;
                DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref enabled, sizeof(int));
            }
            catch
            {
                // Older Windows: keep the default title bar.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // ---------------- Demo data (for previewing the window without a wheel) ----------------

        private sealed class DemoSource
        {
            private readonly bool offline;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private bool enabled = true;
            private int problem;

            public DemoSource(bool offline, int problem)
            {
                this.offline = offline;
                this.problem = problem;
            }

            public void ClearProblem() => problem = WheelProblem.None;

            public bool SetEnabled(bool value)
            {
                enabled = value;
                return true;
            }

            public LiveState? Next()
            {
                if (offline) return null;

                double t = clock.Elapsed.TotalSeconds;
                double wheel = Math.Sin(t * 0.9) * 0.65;
                double throttle = Math.Max(0, Math.Sin(t * 1.3));
                double brake = Math.Max(0, -Math.Sin(t * 1.3)) * 0.8;

                RacingWheelButtons buttons = RacingWheelButtons.None;
                int step = (int)(t * 1.5) % 6;
                if (step == 1) buttons |= RacingWheelButtons.NextGear;
                if (step == 2) buttons |= RacingWheelButtons.Button3;
                if (step == 3) buttons |= RacingWheelButtons.DPadUp | RacingWheelButtons.Button6;
                if (step == 4) buttons |= RacingWheelButtons.Button7;

                return new LiveState(true, enabled, wheel, throttle, brake, (long)buttons, problem);
            }
        }
    }
}
