namespace PfPresets
{
    /// <summary>
    /// How much anonymous usage data the plugin sends.
    ///
    /// Numbered explicitly and starting at the fullest setting, so a config written before this
    /// existed - and any config where the value is missing or zero - reads back as the old
    /// behaviour rather than silently downgrading what a consenting install reports. Someone who
    /// had turned analytics off entirely is moved to <see cref="Off"/> by the config migration, not
    /// by the numbering.
    /// </summary>
    public enum AnalyticsMode
    {
        /// <summary>Everything the plugin measures about its own use - the default.</summary>
        Full = 0,

        /// <summary>Install id and plugin version only. Enough to count copies and see which
        /// versions are live; nothing about what anyone does with it.</summary>
        Basic = 1,

        /// <summary>Nothing leaves the machine.</summary>
        Off = 2,
    }

    /// <summary>Presentation for <see cref="AnalyticsMode"/>.</summary>
    public static class AnalyticsModeInfo
    {
        /// <summary>
        /// The modes left to right along the settings slider, least data first.
        ///
        /// Deliberately the reverse of the enum's own numbering, which starts at Full so an older
        /// config reading back as zero keeps the old behaviour. On screen that ordering would be
        /// backwards: a slider is read as a volume control, so pushing it right has to mean sending
        /// more, not less.
        /// </summary>
        public static AnalyticsMode[] Order { get; } = new[]
        {
            AnalyticsMode.Off,
            AnalyticsMode.Basic,
            AnalyticsMode.Full,
        };

        /// <summary>The label shown above the handle at each stop, in <see cref="Order"/>. One word
        /// each: it sits over a moving handle, and the line underneath carries the meaning.</summary>
        public static string[] Labels { get; } = new[]
        {
            "Off",
            "Basic",
            "Full",
        };

        /// <summary>Which stop along the slider a mode sits at.</summary>
        public static int IndexOf(AnalyticsMode mode)
        {
            int i = System.Array.IndexOf(Order, mode);
            return i < 0 ? Order.Length - 1 : i;
        }

        /// <summary>The mode at a stop, clamped so a bad index can never write a value the enum
        /// doesn't define.</summary>
        public static AnalyticsMode FromIndex(int index)
            => Order[System.Math.Clamp(index, 0, Order.Length - 1)];

        /// <summary>The line printed under the dropdown for the selected mode.</summary>
        public static string Explain(AnalyticsMode mode) => mode switch
        {
            AnalyticsMode.Basic =>
                "Sends a random install id and the plugin version, so copies and versions can be "
                + "counted. Nothing about what you do with it.",
            AnalyticsMode.Off =>
                "Nothing is sent. This install stops being counted.",
            _ =>
                "Adds counts of how often plugin features are used, which duties presets are applied "
                + "for, and your current character's home world, its data centre and its region "
                + "(NA/EU/JP/OCE), to the install id and version. The world is only ever counted - "
                + "\"20 characters on Gilgamesh\" - never listed against anyone. An alt elsewhere is "
                + "counted separately, via a one-way hash of its content id rather than anything that "
                + "identifies it. Still no names and nothing you type.",
        };
    }
}
