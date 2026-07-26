using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// Encodes a preset into a single-line share code and back again.
    ///
    /// Wire format:  "PFP1" + Base64Url( checksum[4] || gzip(utf8 json) )
    ///
    /// The "PFP1" magic makes it cheap to reject clipboard content that isn't one of our codes
    /// (the common case when someone hits "Import from clipboard" with a Discord message or a URL
    /// on the clipboard), and the checksum catches codes truncated by a chat client's line limit.
    /// Base64Url avoids '+' and '/' so the code survives being pasted into a URL or a code block.
    /// </summary>
    public static class PresetShare
    {
        /// <summary>Magic prefix identifying a v1 share code. Bump the digit if the payload shape changes.</summary>
        public const string Magic = "PFP1";

        /// <summary>
        /// Upper bound on the decompressed JSON. A preset is well under 4 KB; this only exists so a
        /// hand-crafted code can't make us allocate an unbounded buffer.
        /// </summary>
        private const int MaxDecompressedBytes = 64 * 1024;

        /// <summary>Deserialization is locked down because share codes come from strangers: no type
        /// names off the wire (so the payload can't pick which classes get instantiated) and a
        /// shallow depth limit.</summary>
        private static readonly JsonSerializerSettings ImportSettings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
        };

        // ══════════════════════════════════════════════════════════
        //  EXPORT
        // ══════════════════════════════════════════════════════════

        /// <summary>Builds the one-line share code for a preset.</summary>
        public static string Export(PfPresetData preset)
        {
            // Identity and usage history are local bookkeeping - the importer mints its own.
            var payload = preset.Duplicate();
            payload.Id = string.Empty;
            payload.Name = preset.Name;
            payload.CreatedAt = DateTime.MinValue;
            payload.LastUsedAt = DateTime.MinValue;

            string json = JsonConvert.SerializeObject(payload, Formatting.None);
            byte[] compressed = Compress(Encoding.UTF8.GetBytes(json));

            byte[] framed = new byte[4 + compressed.Length];
            BitConverter.TryWriteBytes(framed.AsSpan(0, 4), Checksum(compressed));
            compressed.CopyTo(framed, 4);

            return Magic + ToBase64Url(framed);
        }

        // ══════════════════════════════════════════════════════════
        //  IMPORT
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Parses a share code. Returns false with a user-facing <paramref name="error"/> for
        /// anything that isn't one of our codes, so pasting arbitrary clipboard content fails
        /// cleanly instead of half-importing. On success the preset is ready to add: it has a fresh
        /// id and timestamps, and every field has been clamped to a sane range.
        /// </summary>
        public static bool TryImport(string? code, out PfPresetData preset, out string error)
        {
            preset = null!;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(code))
            {
                error = "Nothing to import - the code is empty.";
                return false;
            }

            // Chat clients love to wrap, indent and backtick-quote pasted text.
            string cleaned = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray()).Trim('`', '"', '\'');

            if (!cleaned.StartsWith(Magic, StringComparison.Ordinal))
            {
                error = "That isn't a PF Presets code - it should start with \"PFP1\".";
                return false;
            }

            byte[] framed;
            try
            {
                framed = FromBase64Url(cleaned.Substring(Magic.Length));
            }
            catch (Exception)
            {
                error = "This code is damaged - it contains characters that don't belong.";
                return false;
            }

            if (framed.Length <= 4)
            {
                error = "This code is incomplete. Make sure you copied the whole thing.";
                return false;
            }

            byte[] compressed = framed.AsSpan(4).ToArray();
            if (BitConverter.ToUInt32(framed, 0) != Checksum(compressed))
            {
                error = "This code is incomplete or was altered. Make sure you copied the whole thing.";
                return false;
            }

            string json;
            try
            {
                json = Encoding.UTF8.GetString(Decompress(compressed));
            }
            catch (Exception)
            {
                error = "This code is damaged and could not be read.";
                return false;
            }

            PfPresetData? parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<PfPresetData>(json, ImportSettings);
            }
            catch (Exception)
            {
                error = "This code is from a newer version of PF Presets, or it's damaged.";
                return false;
            }

            if (parsed == null)
            {
                error = "This code doesn't contain a preset.";
                return false;
            }

            preset = Sanitize(parsed);
            return true;
        }

        /// <summary>
        /// Forces an imported preset into a state the rest of the plugin can trust: fresh identity,
        /// eight slots, and every numeric field inside the range its UI allows. A share code is just
        /// bytes someone pasted, so nothing in it is assumed to be in range.
        /// </summary>
        private static PfPresetData Sanitize(PfPresetData p)
        {
            p.Id = Guid.NewGuid().ToString("N");
            p.CreatedAt = DateTime.UtcNow;
            p.LastUsedAt = DateTime.MinValue;

            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Imported Preset" : Truncate(p.Name.Trim(), 64);
            p.Comment = Truncate(p.Comment ?? string.Empty, PfAutomation.MaxCommentLength);

            p.DutyCategoryId = Math.Clamp(p.DutyCategoryId, 0, DutyCategories.Names.Length - 1);
            p.DutyCategoryName = string.IsNullOrWhiteSpace(p.DutyCategoryName)
                ? DutyCategories.Names[p.DutyCategoryId]
                : Truncate(p.DutyCategoryName, 64);
            p.DutyName = string.IsNullOrWhiteSpace(p.DutyName) ? "None" : Truncate(p.DutyName, 128);

            p.ObjectiveId = Math.Clamp(p.ObjectiveId, 0, 3);
            p.CompletionStatusType = Math.Clamp(p.CompletionStatusType, 0, 2);
            p.LootRules = Math.Clamp(p.LootRules, 0, 2);
            p.AvgItemLv = Math.Clamp(p.AvgItemLv, 1, 9999);

            // Password is a 4-digit numeric field in game; anything else means "no password".
            p.PrivatePartyPassword = int.TryParse(p.PrivatePartyPassword, out int pw) && pw is >= 0 and <= 9999
                ? pw.ToString()
                : string.Empty;
            if (string.IsNullOrEmpty(p.PrivatePartyPassword))
                p.FormPrivateParty = false;

            // Exactly eight slots, each with a valid role and no bits set for jobs we don't know.
            var slots = p.Slots ?? new();
            while (slots.Count < 8)
                slots.Add(new RoleSlot { SlotIndex = slots.Count, Role = RoleType.Free });
            if (slots.Count > 8)
                slots = slots.Take(8).ToList();
            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].SlotIndex = i;
                if (!Enum.IsDefined(typeof(RoleType), slots[i].Role))
                    slots[i].Role = RoleType.Free;
                slots[i].AcceptedJobFlags &= JobMasks.AllJobsMask;
            }
            p.Slots = slots;

            // At least one language must be accepted or the listing can't post.
            if (!p.LangJapanese && !p.LangEnglish && !p.LangGerman && !p.LangFrench)
                p.LangJapanese = p.LangEnglish = p.LangGerman = p.LangFrench = true;

            return p;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        // ══════════════════════════════════════════════════════════
        //  ENCODING PRIMITIVES
        // ══════════════════════════════════════════════════════════

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(data, 0, data.Length);
            return output.ToArray();
        }

        /// <summary>Inflates a payload, refusing to grow past <see cref="MaxDecompressedBytes"/>.</summary>
        private static byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            byte[] buffer = new byte[8192];
            int total = 0, read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxDecompressedBytes)
                    throw new InvalidDataException("Share payload is unreasonably large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        /// <summary>Base64 with the URL-safe alphabet and no '=' padding, so the code stays a single
        /// clean token wherever it gets pasted.</summary>
        private static string ToBase64Url(byte[] data)
            => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string s)
        {
            string b64 = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
        }

        /// <summary>FNV-1a 32-bit. Not cryptographic - it exists to catch truncation and typos, and
        /// the format carries no trust beyond "this decodes to a preset".</summary>
        private static uint Checksum(byte[] data)
        {
            uint hash = 2166136261u;
            foreach (byte b in data)
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
