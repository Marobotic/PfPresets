#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>One player seen in a duty, as the local log records them.</summary>
    public sealed class EncounterMember
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public uint JobId { get; set; }

        /// <summary>Which alliance they were in (0-2), for splitting a 24-player prompt.</summary>
        public int AllianceIndex { get; set; }

        /// <summary>The friend/FC link detected while the party still existed. Captured here
        /// because it cannot be worked out after the duty ends - see
        /// <see cref="SocialLinkResolver"/>.</summary>
        public SocialLink Social { get; set; } = SocialLink.None;

        /// <summary>Set once this install has rated them for this encounter, so the prompt stops
        /// offering the same person twice for the same duty.</summary>
        public bool Rated { get; set; }

        [JsonIgnore]
        public CharacterIdentity Identity => new(Name, World);

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(World);
    }

    /// <summary>
    /// One person you have met, flattened out of the duty they were met in. This is what the
    /// Contacts tab lists, and it is a view over <see cref="EncounterStore"/> rather than
    /// something stored separately.
    /// </summary>
    public sealed class Contact
    {
        public EncounterMember Member { get; set; } = new();
        public string EncounterId { get; set; } = string.Empty;
        public string DutyName { get; set; } = string.Empty;
        public uint DutyRowId { get; set; }
        public DateTime MetUtc { get; set; }

        public CharacterIdentity Identity => Member.Identity;
    }

    /// <summary>
    /// A completed duty and who was in it.
    ///
    /// This is the only record the plugin keeps of other players, it never leaves the machine on
    /// its own, and it exists for exactly one purpose: so the post-duty prompt can offer a list of
    /// names rather than asking the user to type them. It is capped and aged out by
    /// <see cref="EncounterStore"/> rather than kept indefinitely.
    /// </summary>
    public sealed class DutyEncounter
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>ContentFinderCondition row id, as reported by Dalamud's duty state.</summary>
        public uint DutyRowId { get; set; }

        public string DutyName { get; set; } = string.Empty;

        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }

        /// <summary>Whether the duty was actually cleared, as opposed to recorded because it ran
        /// long enough to count. Context only; it doesn't affect who can be rated.</summary>
        public bool Cleared { get; set; }

        /// <summary>Everyone seen in the party over the duty's lifetime, excluding the local
        /// player. Late joiners are added as they appear, so a replacement mid-run is still
        /// rateable.</summary>
        public List<EncounterMember> Members { get; set; } = new();

        /// <summary>Set when the user dismisses the prompt for this duty, so it doesn't come back
        /// on the next login.</summary>
        public bool Dismissed { get; set; }

        /// <summary>True when there is still someone here worth prompting about.</summary>
        [JsonIgnore]
        public bool HasUnratedMembers
        {
            get
            {
                foreach (var m in Members)
                {
                    if (m.IsValid && !m.Rated)
                        return true;
                }
                return false;
            }
        }
    }
}
#endif
