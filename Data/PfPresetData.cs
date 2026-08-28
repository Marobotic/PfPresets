using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// Represents a single saved Party Finder recruitment preset.
    /// All fields mirror the in-game Recruitment Criteria window.
    /// </summary>
    [Serializable]
    public class PfPresetData
    {
        // ── Identity ──────────────────────────────────────────────
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New Preset";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUsedAt { get; set; } = DateTime.MinValue;

        // ── Duty ──────────────────────────────────────────────────
        /// <summary>Duty category index matching the in-game dropdown; see
        /// <see cref="DutyCategories.Names"/> (0 = None).</summary>
        public int DutyCategoryId { get; set; } = 0;

        /// <summary>
        /// ContentFinderCondition row id of the selected duty - the authoritative reference.
        /// 0 means "no specific duty" (or a synthetic entry, see <see cref="DutyDataHelper"/>),
        /// in which case <see cref="DutyName"/> is used as the fallback. Presets saved before
        /// 3.0.0.1 have 0 here and are back-filled from the name on load.
        /// </summary>
        public uint DutyRowId { get; set; } = 0;

        /// <summary>Cached display name for the duty (so we don't need Lumina to show it).</summary>
        public string DutyName { get; set; } = "None";

        /// <summary>Cached display name for the duty category.</summary>
        public string DutyCategoryName { get; set; } = "None";

        // ── Objective ─────────────────────────────────────────────
        /// <summary>0=None, 1=Duty Completion, 2=Practice, 3=Loot</summary>
        public int ObjectiveId { get; set; } = 0;

        /// <summary>
        /// The sprout beside the objective: "Beginners/first-timers welcome".
        ///
        /// Lives next to ObjectiveId because that is where the game puts it - a small round toggle
        /// on the Objective row rather than anything in the Conditions column - and a preset that
        /// mirrors the game's own window is easier to check against it.
        ///
        /// It was not merely missing before: the apply path wrote a hard-coded false into the
        /// checkbox on every application, so a listing posted through a preset had the sprout
        /// stripped off it even when the player had ticked it in the game's window first.
        /// </summary>
        public bool BeginnersWelcome { get; set; } = false;

        // ── Comment ───────────────────────────────────────────────
        /// <summary>
        /// The comment as text. Auto-translate phrases appear here expanded, wrapped in the game's
        /// bracket glyphs - readable, searchable, and what the UI draws.
        ///
        /// The budget is 192 <em>bytes</em> in game memory, not 192 characters: the game's symbols
        /// are three bytes each in UTF-8, so this can be well under 191 characters and still be full.
        /// </summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>
        /// The comment's original SeString bytes, base64, when this preset was built from a real
        /// listing. Null for anything typed in the plugin.
        ///
        /// An auto-translate phrase is a payload, not text, and the phrase text alone cannot be
        /// turned back into one - so re-posting a preset saved off such a listing would have
        /// replaced a real, self-translating phrase with literal bracket characters. Keeping the
        /// bytes means the listing goes back up exactly as it was found. They are used only while
        /// <see cref="Comment"/> still matches what they decode to; edit the text and the plugin
        /// falls back to posting the text.
        /// </summary>
        public string? CommentRaw { get; set; } = null;

        // ── Role Slots ────────────────────────────────────────────
        /// <summary>
        /// The seats this listing is recruiting for - as many as the party has, no more.
        ///
        /// Not always eight. A dungeon preset carries four, Crystalline Conflict carries two, and
        /// selecting a duty reshapes the list through <see cref="DutyComposition"/>. Anything
        /// reading this must go by <c>Slots.Count</c> rather than assuming.
        ///
        /// A preset that has never had a duty picked starts as a full party rather than eight open
        /// seats: "two tanks, two healers, four damage" is at worst a shape you edit, whereas eight
        /// free slots is a listing that asks for nothing in particular.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<RoleSlot> Slots { get; set; } = DutyComposition.FullParty();

        // ── Role Options ──────────────────────────────────────────
        /// <summary>Mirrors the in-game "Seek Job Distributions" option. On by default: it's what
        /// most listings want, and it fills around whoever is already in your party. Existing
        /// presets keep whatever they were saved with - this only affects newly created ones.</summary>
        public bool AutoAdjustRoles { get; set; } = true;

        /// <summary>
        /// Whether this preset actually hands its composition to the game client.
        ///
        /// <see cref="AutoAdjustRoles"/> alone is not enough: the option only means anything for
        /// the three categories in <see cref="DutyComposition.SupportsAutoAdjust"/>, and presets
        /// saved before that restriction existed still carry it set on dungeons and roulettes.
        /// Reading the stored flag through here rather than directly means such a preset behaves
        /// the same as one made today, instead of quietly ignoring its own slots.
        /// </summary>
        [JsonIgnore]
        public bool UsesAutoAdjust =>
            AutoAdjustRoles && DutyComposition.SupportsAutoAdjust(DutyCategoryId);

        /// <summary>
        /// Widens one melee slot to accept casters as well - the "fake melee" seat that a double
        /// caster comp fills.
        ///
        /// Only meaningful alongside <see cref="AutoAdjustRoles"/>: without it you'd set the slot
        /// by hand instead, which the job selector already does.
        /// </summary>
        public bool AllowDoubleCaster { get; set; } = false;

        /// <summary>Whether to add a short note to the end of the comment saying double caster is
        /// welcome. The slot alone allows it; this is what tells people scrolling the list.</summary>
        public bool NoteDoubleCasterInComment { get; set; } = false;

        /// <summary>The note appended when <see cref="NoteDoubleCasterInComment"/> is on.</summary>
        public const string DoubleCasterNote = "2 caster ok";

        /// <summary>
        /// The comment as it will actually be posted, with the double-caster note appended when
        /// that's switched on.
        ///
        /// Skips the note if it wouldn't fit rather than truncating it to nonsense, and skips it if
        /// the comment already mentions it - people often type it themselves.
        /// </summary>
        public string ResolveComment(int maxBytes)
        {
            string comment = CommentText.TruncateToBytes(Comment ?? string.Empty, maxBytes);

            if (!AllowDoubleCaster || !NoteDoubleCasterInComment)
                return comment;

            if (comment.Contains("caster", StringComparison.OrdinalIgnoreCase))
                return comment;

            string separator = comment.Length > 0 ? " || " : string.Empty;
            string withNote = comment + separator + DoubleCasterNote;

            return CommentText.ByteLength(withNote) <= maxBytes ? withNote : comment;
        }

        /// <summary>
        /// The comment as bytes for the game's buffer, preserving auto-translate payloads when the
        /// preset came from a real listing and the text has not been edited since.
        ///
        /// This is what the apply path writes. <see cref="ResolveComment"/> remains the text form,
        /// for display and for the places that can only take a string.
        /// </summary>
        public byte[] ResolveCommentBytes(int maxBytes)
        {
            byte[] body = OriginalCommentBytes(maxBytes)
                ?? System.Text.Encoding.UTF8.GetBytes(
                       CommentText.TruncateToBytes(Comment ?? string.Empty, maxBytes));

            if (!AllowDoubleCaster || !NoteDoubleCasterInComment)
                return body;

            if ((Comment ?? string.Empty).Contains("caster", StringComparison.OrdinalIgnoreCase))
                return body;

            byte[] note = System.Text.Encoding.UTF8.GetBytes(
                (body.Length > 0 ? " || " : string.Empty) + DoubleCasterNote);

            // Skip the note rather than truncate it to nonsense - and never at the cost of cutting
            // the comment, which here could mean slicing a payload in half.
            if (body.Length + note.Length > maxBytes)
                return body;

            var combined = new byte[body.Length + note.Length];
            body.CopyTo(combined, 0);
            note.CopyTo(combined, body.Length);
            return combined;
        }

        /// <summary>
        /// The saved listing bytes, but only while they still describe <see cref="Comment"/>.
        /// Any edit to the text makes them stale, and posting stale bytes would silently ignore
        /// what the user just typed.
        /// </summary>
        private byte[]? OriginalCommentBytes(int maxBytes)
        {
            if (string.IsNullOrEmpty(CommentRaw))
                return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(CommentRaw);
                if (bytes.Length == 0 || bytes.Length > maxBytes)
                    return null;

                return CommentText.Decode(bytes) == (Comment ?? string.Empty) ? bytes : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        public bool RemoveRoleRestrictions { get; set; } = false;
        public bool OnePlayerPerJob { get; set; } = false;

        // ── Search Area ───────────────────────────────────────────
        public bool LimitRecruitingToWorld { get; set; } = false;
        public bool FormPrivateParty { get; set; } = false;

        /// <summary>Numeric password 0-9999. Always displayed as 4 zero-padded digits (e.g. 0001, 0056).</summary>
        public string PrivatePartyPassword { get; set; } = string.Empty;

        /// <summary>Returns the password as a 4-digit zero-padded string.</summary>
        [JsonIgnore]
        public string PasswordDisplay => int.TryParse(PrivatePartyPassword, out int val) ? val.ToString("D4") : "0000";

        // ── Conditions ────────────────────────────────────────────
        public bool CompletionStatusEnabled { get; set; } = false;

        /// <summary>0 = Duty Complete, 1 = Duty Complete (Weekly Reward Unclaimed), 2 = Duty Incomplete</summary>
        public int CompletionStatusType { get; set; } = 0;

        public bool AvgItemLvEnabled { get; set; } = false;
        public int AvgItemLv { get; set; } = 1;

        // ── Duty Finder Settings ──────────────────────────────────
        public bool UnrestrictedParty { get; set; } = false;
        public bool MinimumIL { get; set; } = false;
        public bool SilenceEcho { get; set; } = false;

        // ── Loot Rules ────────────────────────────────────────────
        /// <summary>0 = Normal, 1 = Greed Only, 2 = Lootmaster</summary>
        public int LootRules { get; set; } = 0;

        // ── Language ──────────────────────────────────────────────
        public bool LangJapanese { get; set; } = true;
        public bool LangEnglish { get; set; } = true;
        public bool LangGerman { get; set; } = true;
        public bool LangFrench { get; set; } = true;

        /// <summary>Creates a deep copy of this preset with a new ID.</summary>
        public PfPresetData Duplicate()
        {
            var json = JsonConvert.SerializeObject(this);
            var copy = JsonConvert.DeserializeObject<PfPresetData>(json)!;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = this.Name + " (Copy)";
            copy.CreatedAt = DateTime.UtcNow;
            copy.LastUsedAt = DateTime.MinValue;
            return copy;
        }
    }

    /// <summary>
    /// Represents a single role slot in a party recruitment preset.
    /// </summary>
    [Serializable]
    public class RoleSlot
    {
        public int SlotIndex { get; set; } = 0;
        public RoleType Role { get; set; } = RoleType.Free;

        /// <summary>
        /// Bitfield of accepted job IDs for this slot.
        /// Each bit corresponds to a job's <see cref="JobInfo.BitIndex"/>.
        /// 0 = accept all jobs for the role (no restriction).
        /// </summary>
        public ulong AcceptedJobFlags { get; set; } = 0;
    }

    /// <summary>
    /// Role types matching the in-game Party Finder slot categories.
    /// </summary>
    public enum RoleType
    {
        Free = 0,
        Tank = 1,
        Healer = 2,
        MeleeDPS = 3,
        PhysRangedDPS = 4,
        MagicRangedDPS = 5,
        Omit = 6,
    }
}
