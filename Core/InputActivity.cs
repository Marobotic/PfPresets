using System;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// When the player last touched anything: a key, a mouse button, or the mouse itself.
    ///
    /// "Inactive" has to mean away from the machine, not just not typing - somebody reading a
    /// wiki with the mouse is here. Keys and buttons come from Dalamud's key state; the cursor
    /// from the system's own position, compared each second. Framework thread, once a second.
    /// </summary>
    internal sealed class InputActivity
    {
        private readonly IKeyState keyState;
        private readonly IPluginLog log;

        private DateTime lastCheck = DateTime.MinValue;
        private (int X, int Y) lastCursor;

        public InputActivity(IKeyState keyState, IPluginLog log)
        {
            this.keyState = keyState;
            this.log = log;
        }

        public DateTime LastInput { get; private set; } = DateTime.UtcNow;

        public TimeSpan IdleFor => DateTime.UtcNow - LastInput;

        public void Tick()
        {
            var now = DateTime.UtcNow;
            // A quarter second: a Party Finder read stops the moment the player is back, and a
            // second's lag is a second of them unable to open the window.
            if (now - lastCheck < TimeSpan.FromMilliseconds(250))
                return;
            lastCheck = now;

            try
            {
                if (GetCursorPos(out var point) && (point.X, point.Y) != lastCursor)
                {
                    lastCursor = (point.X, point.Y);
                    LastInput = now;
                    return;
                }

                foreach (var key in keyState.GetValidVirtualKeys())
                {
                    if (key != VirtualKey.NO_KEY && keyState[key])
                    {
                        LastInput = now;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Activity] Input check failed: {ex.Message}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);
    }
}
