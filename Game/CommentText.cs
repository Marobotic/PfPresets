using System;
using System.Text;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace PfPresets
{
    /// <summary>
    /// The comment buffer is SeString, not text, and that is the whole reason this file exists.
    ///
    /// A listing's comment is 192 bytes of encoded SeString. Plain words are UTF-8 inside it, so
    /// reading the buffer as a UTF-8 string appears to work - right up until someone used the
    /// game's auto-translate. An auto-translate phrase is not characters at all: it is a payload,
    /// 0x02 0x2E &lt;len&gt; &lt;group&gt; &lt;key&gt; 0x03, which the client expands to the reader's own
    /// language at draw time. Run those bytes through Encoding.UTF8 and they decode to control
    /// codes and replacement characters, which is why such comments showed up in the plugin as
    /// blank or as junk. Worse, the damage is not reversible: once the bytes have become U+FFFD,
    /// re-encoding produces a comment the game cannot read either.
    ///
    /// So everything that reads a comment out of game memory goes through <see cref="Decode"/>,
    /// and anything that intends to write one back keeps the original bytes (see
    /// PfPresetData.CommentRaw) rather than reconstructing them from text.
    ///
    /// The decoded form marks each phrase with the game's own bracket glyphs,
    /// <see cref="AutoTranslateOpen"/> and <see cref="AutoTranslateClose"/> - the same two
    /// characters the client draws around auto-translate. That keeps a comment a plain string
    /// everywhere in the plugin (search, wrapping, JSON, length) while leaving the phrases
    /// findable for the UI, which colours the brackets green and red the way the game does.
    /// </summary>
    public static unsafe class CommentText
    {
        /// <summary>The game's opening auto-translate bracket, U+E040.</summary>
        public const char AutoTranslateOpen = (char)SeIconChar.AutoTranslateOpen;

        /// <summary>The game's closing auto-translate bracket, U+E041.</summary>
        public const char AutoTranslateClose = (char)SeIconChar.AutoTranslateClose;

        /// <summary>
        /// Turns a raw comment buffer into displayable text, expanding auto-translate payloads to
        /// the phrase they stand for and wrapping each one in the game's brackets.
        /// </summary>
        public static string Decode(ReadOnlySpan<byte> raw)
        {
            if (raw.Length == 0)
                return string.Empty;

            // Fixed-size buffers are zero-padded; everything past the terminator is not ours.
            int end = raw.IndexOf((byte)0);
            if (end >= 0)
                raw = raw[..end];

            if (raw.Length == 0)
                return string.Empty;

            try
            {
                var parsed = SeString.Parse(raw);
                var sb = new StringBuilder(raw.Length);

                foreach (var payload in parsed.Payloads)
                {
                    switch (payload)
                    {
                        // Ordered before ITextProvider deliberately: AutoTranslatePayload is one
                        // too, and its Text already carries brackets and padding spaces. We strip
                        // those and re-add our own so the markers are exactly one char each and
                        // the UI can split on them without guessing at the spacing.
                        case AutoTranslatePayload autoTranslate:
                            sb.Append(AutoTranslateOpen)
                              .Append((autoTranslate.Text ?? string.Empty)
                                  .Trim(AutoTranslateOpen, AutoTranslateClose, ' '))
                              .Append(AutoTranslateClose);
                            break;

                        case ITextProvider text:
                            sb.Append(text.Text);
                            break;

                        // Anything else (colour changes, icons we don't model) contributes no text.
                    }
                }

                return sb.ToString();
            }
            catch (Exception)
            {
                // A comment we cannot parse is still better shown as its readable parts than
                // dropped, and this must never take the caller down mid-frame.
                return Encoding.UTF8.GetString(raw);
            }
        }

        /// <summary>
        /// The bytes behind a game string. Utf8String.AsSpan gives chars, already decoded and so
        /// already too late; this is the buffer itself.
        /// </summary>
        public static ReadOnlySpan<byte> Bytes(in Utf8String text)
        {
            byte* ptr = text.StringPtr;
            if (ptr == null)
                return ReadOnlySpan<byte>.Empty;

            int length = (int)Math.Max(0, Math.Min(text.BufUsed, int.MaxValue));
            return new ReadOnlySpan<byte>(ptr, length);
        }

        /// <summary>Decodes a game string, expanding auto-translate phrases.</summary>
        public static string Decode(in Utf8String text) => Decode(Bytes(text));

        /// <summary>Decodes from a fixed-size buffer in game memory.</summary>
        public static string Decode(byte* buffer, int maxBytes)
        {
            if (buffer == null || maxBytes <= 0)
                return string.Empty;

            return Decode(new ReadOnlySpan<byte>(buffer, maxBytes));
        }

        /// <summary>Copies a fixed-size buffer's live bytes, up to but not including the terminator.</summary>
        public static byte[] RawBytes(byte* buffer, int maxBytes)
        {
            if (buffer == null || maxBytes <= 0)
                return Array.Empty<byte>();

            var span = new ReadOnlySpan<byte>(buffer, maxBytes);
            int end = span.IndexOf((byte)0);
            return (end >= 0 ? span[..end] : span).ToArray();
        }

        /// <summary>True when the text contains at least one auto-translate phrase.</summary>
        public static bool HasAutoTranslate(string? text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(AutoTranslateOpen) >= 0;

        /// <summary>
        /// What a comment costs in the game's buffer. The limit is 192 bytes, not 192 characters:
        /// every symbol in the game's set is three bytes in UTF-8, so counting characters
        /// overstates how much fits by a factor of three.
        /// </summary>
        public static int ByteLength(string? text) =>
            string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

        /// <summary>
        /// Cuts a string to fit a byte budget without splitting a character in half. Truncating by
        /// bytes alone can leave a partial UTF-8 sequence, which the game renders as a black
        /// diamond or drops the rest of the line over.
        /// </summary>
        public static string TruncateToBytes(string? text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text) || maxBytes <= 0)
                return string.Empty;

            if (ByteLength(text) <= maxBytes)
                return text;

            var encoder = Encoding.UTF8;
            int chars = Math.Min(text.Length, maxBytes);

            while (chars > 0 && encoder.GetByteCount(text.AsSpan(0, chars)) > maxBytes)
                chars--;

            // Never end on a lone surrogate - that is half a character, and encodes as U+FFFD.
            if (chars > 0 && char.IsHighSurrogate(text[chars - 1]))
                chars--;

            return text[..chars];
        }
    }
}
