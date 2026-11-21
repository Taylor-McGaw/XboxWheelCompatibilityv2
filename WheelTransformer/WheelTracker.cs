using Windows.Gaming.Input;

namespace XboxWheelCompatibility.WheelTransformer
{
    /// <summary>
    /// Keeps track of connected racing wheels. Windows tells us when a wheel is added or removed,
    /// so the wheel list is only re-read when something changed (plus a cheap safety check once a
    /// second), instead of twice every loop tick like before.
    /// The most recently connected wheel is the "main" wheel, same as the original behaviour.
    /// </summary>
    internal sealed class WheelTracker : IDisposable
    {
        private readonly object sync = new();
        private readonly List<RacingWheel> wheels = new();
        private RacingWheel? mainWheel;
        private volatile bool dirty = true;

        public WheelTracker()
        {
            RacingWheel.RacingWheelAdded += OnWheelsChanged;
            RacingWheel.RacingWheelRemoved += OnWheelsChanged;
        }

        public RacingWheel? MainWheel
        {
            get { lock (sync) return mainWheel; }
        }

        /// <summary>Safety net in case an add/remove event was missed.</summary>
        public void CheckForMissedChanges()
        {
            int knownCount;
            lock (sync) knownCount = wheels.Count;

            if (RacingWheel.RacingWheels.Count != knownCount)
            {
                dirty = true;
            }
        }

        /// <summary>Re-reads the wheel list if needed. Returns true when the main wheel changed.</summary>
        public bool Refresh()
        {
            if (!dirty) return false;

            // Clear the flag before reading, so a change that happens while we read is not lost.
            dirty = false;
            IReadOnlyList<RacingWheel> current = RacingWheel.RacingWheels;

            lock (sync)
            {
                wheels.RemoveAll(wheel => !current.Contains(wheel));

                foreach (RacingWheel wheel in current)
                {
                    if (!wheels.Contains(wheel)) wheels.Add(wheel);
                }

                RacingWheel? newMain = wheels.Count > 0 ? wheels[^1] : null;
                bool changed = !ReferenceEquals(newMain, mainWheel);
                mainWheel = newMain;
                return changed;
            }
        }

        private void OnWheelsChanged(object? sender, RacingWheel wheel) => dirty = true;

        public void Dispose()
        {
            RacingWheel.RacingWheelAdded -= OnWheelsChanged;
            RacingWheel.RacingWheelRemoved -= OnWheelsChanged;
        }
    }
}
