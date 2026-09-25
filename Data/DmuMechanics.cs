using System;

namespace PfPresets
{
    /// <summary>
    /// Dancing Mad (Ultimate): which mechanic a phase and boss-HP reading most likely means.
    ///
    /// From the fight research (Dancing_Mad_Ultimate_Research.md): approximate HP ranges, not hard
    /// triggers - gear, uptime, deaths and Limit Breaks move them. Only a few are hard gates (P1
    /// below 15%, P4 below 25%, the kills). So this names where a pull most likely ended, and the
    /// hover says it is an approximation.
    ///
    /// The percentage is the boss's remaining HP in that phase, the same number "P4 58%" shows, so
    /// lower is further in. P3 has two bosses; its percentage is overall phase progress.
    /// </summary>
    public static class DmuMechanics
    {
        /// <summary>ContentFinderCondition row for Dancing Mad (Ultimate).</summary>
        public const uint DutyRowId = 1094;

        public static bool IsDancingMad(uint dutyRowId, string? dutyName)
            => dutyRowId == DutyRowId
               || (dutyName?.Contains("Dancing Mad", StringComparison.OrdinalIgnoreCase) ?? false);

        private readonly record struct Range(double High, double Low, string Name, string Detail);

        private static readonly (string Boss, Range[] Ranges)[] Phases =
        {
            // P1
            ("Kefka", new Range[]
            {
                new(100, 95, "Graven Image 1", "Pull, Revolting Ruin III, Graven Image 1"),
                new(95, 85, "Mystery Magic 1", "First Mystery Magic, laser towers, Double-Trouble Trap"),
                new(85, 70, "Hyperdrive", "Second Mystery Magic, platform transition, Light of Judgement, Hyperdrive, Graven Image 2"),
                new(70, 55, "Gravity III", "Second Double-Trouble Trap, Gravity III bubbles, first part of Tele-Trouncing"),
                new(55, 40, "Tele-Trouncing", "Tele-Trouncing arrows, Confused, Graven Image 3"),
                new(40, 15, "Final Graven Image", "Final Graven Image sequence, final Mystery Magic, real/fake gaze"),
                new(15, 0, "Transition", "Hard gate: below 15% Kefka moves to Phase 2 (above it, Light of Judgement wipes)"),
            }),
            // P2
            ("God Kefka", new Range[]
            {
                new(100, 90, "Ultimate Embrace", "Ultimate Embrace, Forsaken starts, first tower assignment"),
                new(90, 75, "Forsaken towers", "Forsaken towers 1-2, cone and AoE assignments"),
                new(75, 60, "Future's / Past's End", "Future's End or Past's End, Kefka clones, All Things Ending"),
                new(60, 45, "Middle towers", "Middle Forsaken towers, Future's End or Past's End, Light of Judgement check"),
                new(45, 30, "Final towers", "Final Forsaken towers and last clone sequence"),
                new(30, 15, "Trine", "Trine, Wings of Destruction, final tankbuster positioning"),
                new(15, 0, "Final Ultimate Embrace", "Hard gate: kill Kefka after the final Ultimate Embrace, or he enrages"),
            }),
            // P3
            ("Chaos & Exdeath", new Range[]
            {
                new(100, 85, "Definition of Insanity", "Definition of Insanity, The Decisive Battle, Bowels of Agony, first crystals"),
                new(85, 70, "Entropy / Dynamic Fluid", "First Entropy and Dynamic Fluid resolves, Thunder III, first Implosion"),
                new(70, 60, "Eight dashes", "Second debuffs, Umbra Smash, Kefka's eight dashes, Vacuum Wave"),
                new(60, 50, "Ultima Blaster", "Ultima Blaster, second The Decisive Battle, Thunder III"),
                new(50, 40, "Slap Happy", "Max, Earthquake, giant Kefka, Slap Happy"),
                new(40, 30, "Blackhole", "Blackhole begins, tethers, Damning Edict"),
                new(30, 20, "White Hole", "Look Upon Me and Despair, White Hole, remaining tethers"),
                new(20, 10, "Stomp-a-Mole", "Stomp-a-Mole, Knock Down, Blizzard puddles, pair stacks"),
                new(10, 0, "Big Bang", "Big Bang, Blizzard III, kill Chaos first, then Exdeath"),
            }),
            // P4
            ("Kefka Says", new Range[]
            {
                new(100, 90, "Kefka Says", "Kefka Says, Chaos and Neo-Exdeath appear, first Mystery Magic"),
                new(90, 80, "Grand Cross 1", "Grand Cross 1, first Compressed Water, Forked Lightning, Acceleration Bomb, Cursed Shriek"),
                new(80, 70, "Ultima Upsurge 1", "First Chaos debuffs, first Entropy and Dynamic Fluid, first Ultima Upsurge"),
                new(70, 60, "Grand Cross 2", "Grand Cross 2, second stack or spread, second Acceleration Bomb and Cursed Shriek"),
                new(60, 50, "Grand Cross 3", "Second debuff resolves, Grand Cross 3, White Wound or Black Wound, Allagan Field or Beyond Death"),
                new(50, 40, "Flood of Naught", "Flood of Naught, Thrumming Thunder III, first gaze, Dynamic Fluid or Entropy"),
                new(40, 30, "Blizzard III Blowout", "Second gaze, Blizzard III Blowout, second Chaos debuff resolve"),
                new(30, 25, "Mana Release", "Mana Release, final Ultima Upsurge"),
                new(25, 0, "Final Ultima Upsurge", "Hard gate: below 25% before the final Ultima Upsurge (above it, Light of Judgement wipes)"),
            }),
            // P5
            ("Ultima Kefka", new Range[]
            {
                new(100, 90, "Ultima Repeater", "Ultima Repeater, first Fell Forces"),
                new(90, 80, "Chaotic Flood", "Chaotic Flood, first Maddening Orchestra"),
                new(80, 65, "Celestriad", "Celestriad, element towers, Earth and Wind effects"),
                new(65, 55, "Ultima Repeater 2", "Second Ultima Repeater, Fell Forces"),
                new(55, 45, "Stray Apocalypse", "Stray Apocalypse, Exaflares"),
                new(45, 35, "Stray Entropy", "Stray Entropy, second Maddening Orchestra"),
                new(35, 25, "Forsaken", "Fell Forces, start of Forsaken"),
                new(25, 10, "Forsaken blackholes", "Forsaken blackholes and orange AOEs"),
                new(10, 0, "Final blackholes", "Final blackholes, defeat Kefka before Forsaken Null"),
            }),
        };

        /// <summary>The mechanic a pull that ended at this phase and boss HP was most likely on, or
        /// null outside the table.</summary>
        public static (string Boss, string Mechanic, string Detail)? Lookup(int phase, double percent)
        {
            if (phase < 1 || phase > Phases.Length)
                return null;

            var (boss, ranges) = Phases[phase - 1];
            double hp = Math.Clamp(percent, 0, 100);
            foreach (var r in ranges)
            {
                if (hp <= r.High && hp > r.Low)
                    return (boss, r.Name, r.Detail);
            }

            var last = ranges[^1];
            return (boss, last.Name, last.Detail);
        }
    }
}
