#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;

namespace PfPresets
{
    /// <summary>
    /// The pictures on the clears cards - the faces and the fights - downloaded once per machine,
    /// then read off disk forever.
    ///
    /// ONE CACHE FOR BOTH, because they are the same problem: an image named by the hash of its own
    /// bytes, fetched from our own server, which can therefore never be stale. The fight art used to
    /// be eight jpgs compiled into the DLL, which covered the Ultimates and one savage boss - so
    /// when savage began posting every floor, most of the feed had no picture and the remedy was a
    /// plugin release per tier. It comes down the same pipe as the portraits now.
    ///
    /// WHY THE FEED NEEDED THESE. A clear is something one PERSON did, and the card leads with them
    /// - and the picture beside their name was the fight. Eight reclears of UCOB in an evening drew
    /// the same Bahamut eight times, which is a column of identical rows telling the reader nothing
    /// about the eight different people in it. The face is the part that differs, so the face is
    /// what goes in the slot; the fight's art moved behind the card, where it belongs as a mood
    /// rather than as the subject. See DrawCardBody.
    ///
    /// ── Why this is not just EmbeddedTexture with a URL ──────────────────────
    ///
    /// The site icons are five kilobytes baked into the DLL. These arrive over the network, there
    /// are as many of them as there are raiders, and every one of the differences matters:
    ///
    ///   THE ADDRESS IS THE CONTENT. The server names a portrait by the SHA-256 of its own bytes, so
    ///   the image at a path can never change. That is what makes the disk cache correct with no
    ///   invalidation on either side: a file we have is a file we never need to check. Somebody
    ///   changing their glamour is a different hash, which is a different path, which we do not
    ///   have and fetch.
    ///
    ///   THE DISK IS THE CACHE, NOT MEMORY. A texture lives for the session; the file lives for the
    ///   install. Somebody who has scrolled the feed once has the portraits of everybody on it, and
    ///   every subsequent launch draws them without a single request.
    ///
    ///   IT ONLY EVER TALKS TO OUR OWN SERVER. The portrait originates from the Lodestone, and it is
    ///   fetched THERE by the rating server rather than here - so a plugin user reading the feed
    ///   does not quietly emit a column of requests to Square Enix's CDN from their own address for
    ///   images they never asked for. This class knows one host: the one the plugin already uses.
    ///
    /// A miss is remembered as a null, so a portrait that fails once is not retried on every frame
    /// for the rest of the session - the card draws the fight's art instead, which is the layout it
    /// had before portraits existed and is a perfectly good card.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// The most portraits one session will hold textures for.
        ///
        /// Not a cache eviction policy - it is a ceiling, and it is never expected to be reached.
        /// The whole feed is a few hundred posts by rather fewer people, and a 96px portrait is
        /// about forty kilobytes of video memory, so the realistic worst case is a handful of
        /// megabytes. The number exists because "one texture per distinct string the server sends"
        /// is the shape of an unbounded resource, and an unbounded resource in a plugin that runs
        /// for eight hours is a leak whether or not anybody has managed to trip it yet.
        ///
        /// Past it, cards draw the fight's art. Nothing breaks and nothing is disposed mid-frame.
        /// </summary>
        private const int MaxPortraits = 1024;

        /// <summary>Concurrent because the decode lands on a worker thread while the frame thread is
        /// reading: a plain dictionary can rehash under the read.</summary>
        private readonly ConcurrentDictionary<string, IDalamudTextureWrap?> portraits = new(StringComparer.Ordinal);

        /// <summary>Paths already being fetched, so a card drawn sixty times a second asks
        /// once.</summary>
        private readonly ConcurrentDictionary<string, byte> portraitsLoading = new(StringComparer.Ordinal);

        private string? portraitDir;

        /// <summary>
        /// Where the files live, made on first use.
        ///
        /// Under the plugin's own config directory, beside the vote queue and the encounter store,
        /// so uninstalling takes them with it. Null if the directory cannot be made, which turns
        /// the disk half off and leaves the rest working - portraits are then fetched once per
        /// session instead of once per install, which is a slower version of the same thing rather
        /// than a broken one.
        /// </summary>
        private string? PortraitDir()
        {
            if (portraitDir != null)
                return portraitDir.Length > 0 ? portraitDir : null;

            try
            {
                string dir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "portraits");
                Directory.CreateDirectory(dir);
                portraitDir = dir;
                return dir;
            }
            catch (Exception)
            {
                // Remembered as "no directory" rather than retried: whatever stopped it - a
                // read-only profile, a full disk - is not going to be different in sixteen
                // milliseconds' time.
                portraitDir = string.Empty;
                return null;
            }
        }

        /// <summary>
        /// One character's portrait, or null while it is on its way - or for good, if there is not
        /// one.
        ///
        /// Safe to call every frame, from the frame thread only. Callers draw the fight's art when
        /// this is null, so a slow first fetch costs a moment of the old layout rather than a gap.
        /// </summary>
        private IDalamudTextureWrap? CachedImage(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (portraits.TryGetValue(path, out var loaded))
                return loaded;

            // Validated before it is used to name a FILE, because this arrives from the server. The
            // API client checks the same shape before it will fetch one; this is the check that
            // stands between a server response and a path on somebody's disk, and it has to exist
            // here whether or not the other one does.
            if (!IsImagePath(path))
                return null;

            if (portraits.Count >= MaxPortraits)
                return null;

            if (!portraitsLoading.TryAdd(path, 0))
                return null;

            _ = Task.Run(async () =>
            {
                try
                {
                    byte[]? bytes = ReadPortraitFile(path);

                    if (bytes == null)
                    {
                        var ratings = Ratings;
                        if (ratings != null)
                            bytes = await ratings.GetPortraitAsync(path).ConfigureAwait(false);

                        if (bytes != null)
                            WritePortraitFile(path, bytes);
                    }

                    if (bytes == null)
                    {
                        // Remembered as a miss. The card falls back to the fight's art, which is
                        // what it drew before any of this existed.
                        portraits[path] = null;
                        return;
                    }

                    // THE PLACEHOLDER GOES IN BEFORE THE DECODE IS STARTED, and the ordering is the
                    // whole of a bug worth remembering - see the same note in EmbeddedTexture. A
                    // warm texture provider can finish inside the next two statements, and when it
                    // does, storing null afterwards overwrites the finished texture permanently.
                    portraits[path] = null;

                    _ = textureProvider.CreateFromImageAsync(bytes).ContinueWith(t =>
                    {
                        portraits[path] = t.IsCompletedSuccessfully ? t.Result : null;
                    });
                }
                catch (Exception)
                {
                    portraits[path] = null;
                }
                finally
                {
                    portraitsLoading.TryRemove(path, out _);
                }
            });

            return null;
        }

        /// <summary>The two shapes the server issues: <c>portrait/</c> or <c>art/</c> and
        /// sixty-four hex digits. It is about to become a filename, so nothing else is entertained -
        /// no separators, no dots, nothing to walk out of the directory with.</summary>
        private static bool IsImagePath(string path)
        {
            int slash = path.IndexOf('/');
            if (slash < 0)
                return false;

            if (path[..slash] is not ("portrait" or "art"))
                return false;

            if (path.Length != slash + 1 + 64)
                return false;

            for (int i = slash + 1; i < path.Length; i++)
            {
                char c = path[i];
                if (!char.IsAsciiDigit(c) && (c < 'a' || c > 'f'))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The filename for a cached image: its kind, then its hash.
        ///
        /// The kind is kept in the name rather than dropped, even though the hash alone is already
        /// unique. Two reasons, and the second is the real one: a directory of nothing but
        /// sixty-four-character names is impossible to make sense of by hand, and if the two ever
        /// need separating - a "forget every portrait" without touching the art - it has to be
        /// possible to tell them apart from the filename alone.
        ///
        /// Only ever called with a path that has already passed <see cref="IsImagePath"/>.
        /// </summary>
        private static string PortraitFile(string path) => path.Replace('/', '-') + ".img";

        private byte[]? ReadPortraitFile(string path)
        {
            string? dir = PortraitDir();
            if (dir == null)
                return null;

            try
            {
                string file = Path.Combine(dir, PortraitFile(path));
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void WritePortraitFile(string path, byte[] bytes)
        {
            string? dir = PortraitDir();
            if (dir == null)
                return;

            try
            {
                // Written beside and moved into place, so a launch that dies mid-write cannot leave
                // a truncated file that every subsequent launch reads, decodes into nothing and
                // never re-fetches - the file being present IS the cache hit.
                string file = Path.Combine(dir, PortraitFile(path));
                string temp = file + ".tmp";

                File.WriteAllBytes(temp, bytes);
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception)
            {
                // A portrait that cannot be written is fetched again next session. Not worth a word
                // to anybody.
            }
        }

        private void DisposePortraits()
        {
            foreach (var portrait in portraits.Values)
                portrait?.Dispose();

            portraits.Clear();
            portraitsLoading.Clear();
        }
    }
}
#endif
