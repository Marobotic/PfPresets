#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;

namespace PfPresets
{
    /// <summary>
    /// The clear announcement: one line across the middle of the screen when somebody clears.
    ///
    /// Borrowed wholesale from the MMO it is sitting inside. A world first in an older game was a
    /// line of text every player on the shard saw at once, and the reason that worked was never the
    /// typography - it was that everybody found out at the same moment, including the people who
    /// were not looking. The Clears feed already knows all of this; it just keeps it behind a tab,
    /// which means the only people who see a clear are the ones who went to check.
    ///
    /// WHAT KEEPS IT FROM BEING AN ADVERT. This draws on top of somebody's game, which is a thing a
    /// plugin gets to do roughly once before it is uninstalled, so every decision here is the quiet
    /// one:
    ///
    ///   one line              A sentence, and nothing else on it: who, where they are, what they
    ///                         cleared. No icon, no chip, no second line, no border, no art, no
    ///                         buttons - the feed is where a clear gets a card, and this is not the
    ///                         feed. Everything that was tried here and taken out again was a small
    ///                         good idea that added up to a card drawn over somebody's game.
    ///   a few seconds         Six by default, and it fades rather than blinking out.
    ///   one at a time         The queue is capped in RatingService.Announce.cs. A busy evening does
    ///                         not become a minute of somebody else's news.
    ///   never in the way      Positioned above centre, where the game puts its own notices and
    ///                         where nothing the player is aiming at lives.
    ///   never yours           You were there.
    ///   off in one click      And the switch is in Settings beside the one that decides whether
    ///                         your own clears go out, which is the other half of the same idea.
    ///
    /// The one thing it is allowed to be is clickable, because the whole point of hearing about a
    /// clear is being able to say congratulations - pressing it opens the feed at the post, where
    /// the heart is.
    /// </summary>
    public partial class PluginUI
    {
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  WHERE IT SITS
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        /// <summary>
        /// How far down the screen it rests, before the player's own offset.
        ///
        /// A little above the middle rather than at it. Dead centre is where the game puts its own
        /// full-screen messages and where a player's eye is during a fight; a shade above is close
        /// enough to be unmissable and far enough not to sit on top of a boss's cast bar.
        /// </summary>
        private const float ClearAnnounceTopFraction = 0.28f;

        /// <summary>
        /// The one text size, and there is no setting for it.
        ///
        /// There was one, and it was wrong: it made the announcement a thing to be tuned rather than
        /// a thing to be read, and the cost was real - a font is rasterised at the size it is built
        /// at and never scaled at draw time, so a size slider meant discarding and rebuilding font
        /// handles on the end of a drag. One size that is right needs no slider and no rebuild.
        ///
        /// 26 RATHER THAN 20. It is competing with a game that sets its own full-screen
        /// announcements at something like this, on a screen that may be 1440 or 4K, and at 20 it
        /// read as a plugin's tooltip that had wandered into the middle of the display rather than
        /// as the game saying something. Nothing else moves with it: the art squares itself off
        /// against the line height and the panel is measured from the text, so this is the only
        /// number, and the padding below is the only thing tuned to sit with it.
        /// </summary>
        private const float AnnounceTextPx = 26f;

        /// <summary>Enough air that the text is not touching the edge, and no more. The panel is a
        /// ground for a sentence, not a card - so these grew with the text rather than in
        /// proportion to it, which would have made a bar out of a line.</summary>
        private const float AnnouncePadX = 19f;
        private const float AnnouncePadY = 11f;

        /// <summary>
        /// Air between the fight's art and the fight's name.
        ///
        /// The art is not sized here: it is squared off against the line height in
        /// MeasureAnnouncement, so it grows with the text instead of needing a second number kept in
        /// step with AnnounceTextPx by hand. This is the only gap that needs a value, and it is the
        /// tighter of the two spacings in the sentence - the art belongs TO the name that follows
        /// it, and reads as attached to it rather than as a third thing in a list.
        /// </summary>
        private const float AnnounceFightIconGap = 7f;

        // â”€â”€ The motion â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // IT RISES IN AND DRIFTS OUT, rather than appearing and vanishing. A notice that simply
        // switches on is something the eye has to notice by luck; one that moves is caught by
        // peripheral vision, which is the entire reason a world-boss announcement works on somebody
        // who was looking at their hotbar.
        //
        // Translation and opacity only, never scale. Growing text means rasterising at a new size
        // every frame, and the alternative - stretching the glyphs it already has - is the blur this
        // codebase has a rule against (see PluginUI.Fonts.cs). Moving a laid-out line costs nothing
        // and reads better anyway.

        /// <summary>How long the entrance takes, and how far below its resting place it starts.
        /// Short and small: this is a lift into place, not a slide across the screen.</summary>
        private const float AnnounceFadeIn = 0.14f;
        private const float AnnounceRiseFrom = 14f;

        /// <summary>The exit, which is slower than the entrance and travels the other way. An
        /// announcement that leaves the way it arrived looks like it was cancelled; one that carries
        /// on upward looks like it finished.</summary>
        private const float AnnounceFadeOut = 0.28f;
        private const float AnnounceDriftTo = 10f;

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  WHAT IS ON SCREEN
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private AchievementPost? announcePost;

        /// <summary>
        /// Seconds this announcement has been up, accumulated rather than measured from a timestamp.
        ///
        /// Accumulated because it has to be able to STOP, and a subtraction from DateTime.UtcNow
        /// cannot. Hovering pauses the countdown - somebody reaching for the thing must not have it
        /// vanish from under the cursor, which is the single most infuriating way a transient
        /// notification can behave.
        /// </summary>
        private float announceElapsed;

        /// <summary>
        /// Whether what is on screen is the sample from the Preview button.
        ///
        /// THE SAMPLE RUNS THE REAL LIFECYCLE. It used to be a static shape that sat there for as
        /// long as the settings page was open, which showed the placement and nothing else - and
        /// placement is the least of what somebody wants to know before turning this on. What they
        /// want to know is what it will feel like: how it arrives, how long it stays, how it goes.
        /// So the button hands the sample to exactly the code path a real clear takes, and the only
        /// two differences are that it cannot be clicked through to a post that does not exist, and
        /// that it draws over the settings page rather than behind it.
        /// </summary>
        private bool announceIsSample;

        /// <summary>The post an announcement was pressed about, and how long the feed marks it for.
        /// The card wears a ring so the clear that was announced is findable in a list where every
        /// row looks alike.</summary>
        private string announceFocusId = string.Empty;
        private DateTime announceFocusUntil = DateTime.MinValue;

        /// <summary>The sample, built once. A real post shape so the preview measures and wraps
        /// exactly like the thing it is previewing.</summary>
        private static readonly AchievementPost AnnouncePreviewPost = new()
        {
            Id = "##preview",
            Name = "Sample Player",
            World = "Cerberus",
            FightName = "Futures Rewritten",
            FightLabel = "FRU",
            FightSlug = "fru",
            Kind = "ultimate_first",
        };

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  THE FACE
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //
        // Three choices, and the default is the plugin's own for one reason: it is the only one of
        // the three that is guaranteed to draw every name. Roboto is merged with the game's Axis
        // for everything Latin does not cover (see PluginUI.Fonts.cs), so a name built out of Greek
        // letters and fullwidth brackets - which is an ordinary FFXIV name - comes out right.
        //
        // Axis is the game's own UI face and covers everything too, so it is a free swap for anybody
        // who wants the overlay to read as part of the game rather than as part of a plugin.
        //
        // Jupiter is the display face the game sets its own big announcements in, and it is the one
        // that actually looks like a world-first banner. It is also a display face, which means its
        // coverage is Latin and little else - so a name it cannot draw falls back to the plugin's
        // face for that run alone rather than coming out as a row of boxes. See AnnounceNameFontFor.

        internal const int AnnounceFacePlugin = 0;
        internal const int AnnounceFaceAxis = 1;
        internal const int AnnounceFaceJupiter = 2;

        internal static readonly string[] AnnounceFaceLabels =
        {
            "Plugin (Roboto)",
            "Game (Axis)",
            "Game (Jupiter)",
        };

        /// <summary>
        /// One handle per face, and all of them kept for the life of the plugin.
        ///
        /// THIS WAS ONE SLOT THAT GOT THROWN AWAY AND REBUILT whenever the setting moved. Changing
        /// the typeface is a click, and every click disposed the lot - including the two that have
        /// nothing to do with which family is chosen: Roboto is the fallback whichever face wins,
        /// and the heart and the close are icons.
        ///
        /// A disposed handle is not a handle that draws differently, it is a handle that does not
        /// exist, and until the atlas has finished rebuilding it every Push falls through to
        /// Dalamud's own default at about 12px. That is the twitch - the preview was not restyling,
        /// it was collapsing to 12px and growing back a few frames later, three times over.
        ///
        /// Three text handles and an icon at one size is a few hundred kilobytes of atlas, paid
        /// once at load. The onboarding's preview of this very control has always worked this way -
        /// see OnbPreviewGameFace - and switching there has always been instant, which is what gave
        /// the game away.
        /// </summary>
        private IFontHandle? annTextPlugin, annTextAxis, annTextJupiter, annIcon;

        /// <summary>The heart and the close, at a size that sits with 18px text rather than at
        /// whatever Dalamud's shared icon handle happens to be.</summary>
        private IFontHandle AnnIconFont => IconFont(ref annIcon, AnnounceIconPx);
        private const float AnnounceIconPx = 13f;

        // ── Getting the face ready BEFORE it is needed ────────────
        //
        // THIS IS WHY THE TEXT USED TO CHANGE SHAPE A MOMENT AFTER APPEARING. A font handle is built
        // asynchronously: asking for one starts a job and hands back something not yet usable, and
        // until it is ready every draw falls through to the plugin's own face. Because the handles
        // were only ever asked for at the moment of drawing, the first announcement of a session
        // drew its first frames in Roboto and then swapped to Jupiter mid-flight - a notice that
        // changes typeface while you are reading it, which is about the most distracting thing a
        // one-line notice can do.
        //
        // Three parts to the fix, and all of them are needed:
        //
        //   warm   Every handle is asked for on every frame from the moment the plugin draws
        //          anything, so the build has long since finished by the time a clear lands.
        //   all    ALL THREE FACES, not the one the setting names. Nothing is discarded when the
        //          setting moves, so a switch is a different handle rather than a new build - see
        //          the note on the handles themselves for what that cost before.
        //   wait   An announcement does not START until they are ready. Warming makes that wait
        //          nothing in practice; the check is what guarantees the swap can never be seen,
        //          including on the very first frames after a plugin reload.
        //
        // With a give-up, because a handle that never builds must not silence the feature forever -
        // after a few seconds the fallback is accepted and the announcement goes ahead in Roboto.

        private DateTime annFontsAskedAt = DateTime.MinValue;
        private static readonly TimeSpan AnnounceFontGiveUp = TimeSpan.FromSeconds(6);

        /// <summary>
        /// Asks for every face the banner might need, so they are all building. Safe and nearly free
        /// on every frame after the first - a null check and a flag read.
        ///
        /// EVERY FACE, NOT THE ONE THE SETTING NAMES. The other two cost one build each, at load,
        /// and are the whole reason changing the setting later costs nothing at all.
        /// </summary>
        private bool WarmAnnounceFonts()
        {
            // Touching the properties is what creates the handles.
            bool ready = AnnPluginFont.Available && AnnIconFont.Available;

            var axis = AnnounceGameFace(AnnounceFaceAxis);
            var jupiter = AnnounceGameFace(AnnounceFaceJupiter);

            // Readiness is asked of the face that would actually draw, and through its own slot
            // rather than through AnnTextFont - which answers with the fallback while the game face
            // is still building, and would therefore always say yes.
            ready &= AnnounceFace switch
            {
                AnnounceFaceAxis => axis?.Available ?? false,
                AnnounceFaceJupiter => jupiter?.Available ?? false,
                _ => true,
            };

            if (ready)
                return true;

            if (annFontsAskedAt == DateTime.MinValue)
                annFontsAskedAt = DateTime.UtcNow;

            return DateTime.UtcNow - annFontsAskedAt >= AnnounceFontGiveUp;
        }

        private int AnnounceFace => Math.Clamp(config.ClearAnnouncementFont, 0, AnnounceFaceLabels.Length - 1);

        /// <summary>
        /// Teardown, and only teardown - the plugin is unloading.
        ///
        /// NOTHING DISPOSES THESE ON A SETTING CHANGE ANY MORE. That was the bug: see the note on
        /// the handles. A face the player might switch back to in five seconds is not worth the
        /// atlas rebuild it costs to throw away, and the rebuild was visible.
        /// </summary>
        private void DisposeAnnounceFonts()
        {
            annTextPlugin?.Dispose();
            annTextAxis?.Dispose();
            annTextJupiter?.Dispose();
            annIcon?.Dispose();
            annTextPlugin = annTextAxis = annTextJupiter = annIcon = null;
        }

        /// <summary>The plugin's own face at the announcement's size. Always available, and the
        /// fallback for everything the display face cannot draw.</summary>
        private IFontHandle AnnPluginFont
            => Font(ref annTextPlugin, AnnounceTextPx, FontWeight.SemiBold);

        /// <summary>One of the two game faces at the announcement's size, built on first ask and
        /// kept for good. Null when the atlas will not give us one, which every caller reads as
        /// "draw it in Roboto".</summary>
        private IFontHandle? AnnounceGameFace(int face)
        {
            ref IFontHandle? slot = ref face == AnnounceFaceJupiter
                ? ref annTextJupiter
                : ref annTextAxis;

            if (slot != null)
                return slot;

            try
            {
                var family = face == AnnounceFaceJupiter
                    ? GameFontFamily.Jupiter
                    : GameFontFamily.Axis;

                slot = pluginInterface.UiBuilder.FontAtlas
                    .NewGameFontHandle(new GameFontStyle(family, AnnounceTextPx));
            }
            catch (Exception)
            {
                // Nothing to report and nothing to do about it: the plugin's own face draws this
                // frame and every frame after, at the right size.
                return null;
            }

            return slot;
        }

        /// <summary>Asks for both game faces up front, so changing the typeface picks one that is
        /// already built instead of starting a build. Called by PreloadFonts.</summary>
        private void PreloadAnnounceGameFaces()
        {
            _ = AnnounceGameFace(AnnounceFaceAxis);
            _ = AnnounceGameFace(AnnounceFaceJupiter);
        }

        /// <summary>
        /// The face the setting names, or the plugin's if the atlas will not give us one.
        ///
        /// Guarded rather than trusted. A game font handle that is not ready does not draw at the
        /// size that was asked for - it falls back to Dalamud's own default at about 12px, which is
        /// how a 26px announcement comes out smaller than the words beside it. So a handle is only
        /// used once it says it is ready, and Roboto draws in the meantime.
        ///
        /// Reads the SETTING, not a copy of it taken when something was last built. There is
        /// nothing left to keep in step: all three handles exist from load, and this picks one.
        /// </summary>
        private IFontHandle AnnTextFont
        {
            get
            {
                int face = AnnounceFace;
                if (face == AnnounceFacePlugin)
                    return AnnPluginFont;

                var slot = AnnounceGameFace(face);
                return slot != null && slot.Available ? slot : AnnPluginFont;
            }
        }

        /// <summary>
        /// The face to set one particular run in.
        ///
        /// Jupiter is a display face: it has the Latin alphabet and the punctuation a title needs,
        /// and essentially nothing else. Plenty of FFXIV names are not that - Greek letters,
        /// Cyrillic, fullwidth capitals, CJK - and a face without those glyphs draws a row of empty
        /// boxes where somebody's name should be, which is the one failure this feature cannot ship
        /// with. So a string it cannot handle is set in the plugin's face instead, for that run
        /// only. Axis is the game's UI face and covers everything, so it is never fallen back from.
        /// </summary>
        private IFontHandle AnnounceFontFor(string text)
            => AnnounceFace == AnnounceFaceJupiter && NeedsFullCoverage(text)
                ? AnnPluginFont
                : AnnTextFont;

        /// <summary>True when the string contains anything past Latin Extended-A, which is the point
        /// a display face stops being able to help.</summary>
        private static bool NeedsFullCoverage(string text)
        {
            foreach (char c in text)
            {
                if (c > 0x017F)
                    return true;
            }
            return false;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  THE OVERLAY
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        /// <summary>
        /// Takes the next clear off the queue and draws whatever is on screen. Called every frame
        /// from Draw, and returns on a null check on nearly all of them.
        /// </summary>
        private void DrawClearAnnouncement()
        {
            var ratings = Ratings;

            if (!config.CommunityEnabled || !config.ClearAnnouncementsEnabled)
            {
                // Nothing parked behind a switch that has just been turned off. Somebody who says
                // they do not want this should not get one more.
                announcePost = null;
                announceIsSample = false;
                ratings?.ClearAnnouncements();
                return;
            }

            // EVERY FRAME, whether anything is on screen or not - this is what makes the face ready
            // before the first clear rather than during it. See the note above WarmAnnounceFonts.
            bool fontsReady = WarmAnnounceFonts();

            // ALSO EVERY FRAME, and deliberately not folded into the condition below. It keeps a
            // timer, and a timer only asked about on the frames where the screen happens to be free
            // is a timer that restarts after every banner - which would have quietly put a gap
            // between two clears that arrived together.
            bool held = UpdateAnnouncementHold();

            // A real clear takes the slot the moment one is free, and not before the face it will be
            // set in is built. The sample holds the slot too, and does not get shoved aside by a
            // clear landing mid-preview - it is a few seconds, and interrupting the thing somebody
            // pressed to watch would make the button a liar. The clear waits on the queue, which is
            // what the queue is for.
            if (announcePost == null && ratings != null && fontsReady && !held)
            {
                var next = ratings.TakeAnnouncement();
                if (next != null)
                    StartAnnouncement(next, sample: false);
            }

            var post = announcePost;
            if (post == null)
                return;

            var motion = AdvanceAnnouncement();

            if (motion.Alpha <= 0f)
            {
                announcePost = null;
                announceIsSample = false;
                return;
            }

            DrawAnnouncementBanner(post, motion, announceIsSample);
        }

        // ══════════════════════════════════════════════════════════
        //  WHEN NOT TO
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// How long combat and the duty have to have been over before one is allowed through.
        ///
        /// COMBAT IS NOT A STEADY SIGNAL. It drops between trash packs, between FATE mobs, between
        /// pulls - gaps of a few seconds that are not "back to normal" by any reading a player
        /// would recognise. Without a settle the notice would slip into one of them and be gone
        /// before the next pull started, which is the same as never having shown it.
        ///
        /// It also covers the other end of a duty: BoundByDuty clears while the screen is still
        /// black, and a banner that spends its whole life behind a loading screen was delivered to
        /// nobody. A second and a half is long enough to be out and looking at the world again, and
        /// short enough that it still reads as "the moment you finished".
        /// </summary>
        private const long AnnounceSettleMs = 1500;

        /// <summary>When combat and the duty last both stopped being true, or 0 while either is.</summary>
        private long announceQuietSinceTick;

        /// <summary>
        /// Whether now is the wrong moment to put a clear on screen.
        ///
        /// HELD, NOT DROPPED. The queue is what holds them - see TakeAnnouncement - so nothing is
        /// lost by refusing here, and the clears that landed while somebody was inside a fight are
        /// still waiting when they come out. That is the whole feature: an announcement is worth
        /// reading, and a fight is precisely when it will not be read and precisely when a rectangle
        /// appearing over the arena is most unwelcome. The cap on the queue is what keeps a long
        /// duty from turning into a stack of them afterwards.
        ///
        /// The Preview button does not come through here on purpose. It calls StartAnnouncement
        /// directly, because somebody who pressed a button to see the thing should see the thing,
        /// and a settings page that silently did nothing in a duty would read as broken.
        /// </summary>
        private bool UpdateAnnouncementHold()
        {
            if (pfAutomation.IsInCombat() || pfAutomation.IsInDuty())
            {
                announceQuietSinceTick = 0;
                return true;
            }

            if (announceQuietSinceTick == 0)
                announceQuietSinceTick = Environment.TickCount64;

            return Environment.TickCount64 - announceQuietSinceTick < AnnounceSettleMs;
        }

        /// <summary>Puts one on screen from the top of its entrance. The single place the timer is
        /// reset, so a sample and a real clear cannot start out of step with each other.</summary>
        private void StartAnnouncement(AchievementPost post, bool sample)
        {
            announcePost = post;
            announceIsSample = sample;
            announceElapsed = 0f;
            announceDismissing = false;
            announceActionFade = 0f;

            // The hover flag is a frame behind by design, and the frame it is behind is the previous
            // announcement's last one. Left standing it would freeze the new banner's countdown
            // until the cursor moved.
            announceHovered = false;
        }

        /// <summary>Where the banner is in its life: how opaque, and how far from its resting
        /// place.</summary>
        private readonly record struct AnnounceMotion(float Alpha, float OffsetY);

        /// <summary>
        /// Advances the clock one frame and works out what that means on screen.
        ///
        /// Three phases and they are not symmetrical. It RISES into place quickly, HOLDS still, then
        /// FADES while drifting on upward - an announcement that leaves the way it arrived reads as
        /// having been cancelled, and one that carries on in the direction it was already going
        /// reads as having finished.
        ///
        /// Hovering does not merely pause it - it winds a fade-out back to full, and puts it back at
        /// rest. Somebody whose cursor lands on a banner that had started to go has caught it, and
        /// making them watch it finish disappearing under their own mouse would be a worse answer
        /// than not being clickable at all.
        /// </summary>
        private AnnounceMotion AdvanceAnnouncement()
        {
            float life = Math.Clamp(config.ClearAnnouncementSeconds, 2, 30);

            // The sample pauses on hover exactly like the real thing, because it now has the same
            // hit target and is meant to feel identical. A banner that has been dismissed ignores
            // the hover, or pressing the close while the cursor is still on it would hold it there
            // forever - which is the one press that must always be obeyed.
            if (announceHovered && !announceDismissing)
            {
                announceElapsed = MathF.Min(announceElapsed, life);
                return new AnnounceMotion(1f, 0f);
            }

            announceElapsed += ImGui.GetIO().DeltaTime;

            // ── Rising in ──
            if (announceElapsed < AnnounceFadeIn)
            {
                float t = Ease(Math.Clamp(announceElapsed / AnnounceFadeIn, 0f, 1f));
                return new AnnounceMotion(t, AnnounceRiseFrom * (1f - t));
            }

            // ── Holding ──
            if (announceElapsed <= life)
                return new AnnounceMotion(1f, 0f);

            // ── Drifting out ──
            float gone = (announceElapsed - life) / AnnounceFadeOut;
            if (gone >= 1f)
                return new AnnounceMotion(0f, 0f);

            float e = Ease(gone);
            return new AnnounceMotion(1f - e, -AnnounceDriftTo * e);
        }

        /// <summary>Whether the cursor was over the banner last frame. Read by the countdown, which
        /// runs before the banner is drawn - so it is a frame behind, which is invisible and saves
        /// laying the whole thing out twice.</summary>
        private bool announceHovered;

        /// <summary>Set by the close button. Overrides the hover pause so the exit can finish under
        /// the cursor that asked for it.</summary>
        private bool announceDismissing;

        /// <summary>
        /// The whole announcement: one sentence, on a panel just dark enough to read it against.
        ///
        /// ONE LINE, and nothing else on it. This had a job icon, a second line carrying the world
        /// and the kind of clear, an accent edge for a first clear and a hairline border, and every
        /// one of those was a small good idea that added up to a card - which is the thing the feed
        /// already is. What belongs on top of somebody's game is a sentence: who, and what they
        /// cleared. Everything else is one click away on the post itself.
        /// </summary>
        /// <summary>
        /// Everything measured, so the paint pass can be handed one thing rather than fourteen.
        /// </summary>
        private readonly record struct AnnounceLayout(
            string Who, string Verb, string Fight,
            IFontHandle WhoFont, IFontHandle VerbFont, IFontHandle FightFont,
            float WhoW, float VerbW, float WhoH, float FightH, float LineH,
            IDalamudTextureWrap? FightArt, float IconSize, float IconAdvance,
            Vector2 Size);

        // ── The two things on the right ───────────────────────────
        //
        // A heart and a close, and they are only there while the cursor is. At rest this is a
        // sentence and nothing else, which is the whole look; the moment somebody puts the pointer
        // on it, it becomes a thing with two buttons, which is what they came for.
        //
        // THEIR SPACE IS RESERVED EVEN WHEN THEY ARE NOT DRAWN. The alternative is a panel that
        // grows on hover, and because it is centred it would grow from both edges - the sentence
        // would slide sideways under the cursor that was reaching for it. Forty-odd pixels of quiet
        // air at the right end is a much smaller price than a target that moves when you approach.

        private const float AnnounceActionSize = 22f;
        private const float AnnounceActionGap = 6f;

        /// <summary>Air between the end of the sentence and the first button.</summary>
        private const float AnnounceActionInset = 12f;

        /// <summary>
        /// The room the two buttons need, or nothing at all in click-through.
        ///
        /// Zero rather than reserved-but-unused: in click-through the buttons do not exist, and
        /// keeping their space would leave sixty pixels of dead air on the end of a sentence for no
        /// reason anybody looking at it could work out. The mode is meant to be the leaner of the
        /// two, and this is most of what makes it look that way.
        /// </summary>
        private float AnnounceActionsWidth
            => config.ClearAnnouncementClickThrough
                ? 0f
                : AnnounceActionInset + AnnounceActionSize * 2f + AnnounceActionGap;

        /// <summary>
        /// How far the two buttons have faded in, 0 to 1.
        ///
        /// Eased rather than switched, because a glyph that pops into existence under a moving
        /// cursor reads as a glitch. A tenth of a second is enough to make it a reveal.
        /// </summary>
        private float announceActionFade;
        private const float AnnounceActionFadeTime = 0.1f;

        private void DrawAnnouncementBanner(AchievementPost post, AnnounceMotion motion, bool sample)
        {
            var layout = MeasureAnnouncement(post);

            // ── Where ──
            //
            // Off the viewport rather than off the display: with viewports on, a window is placed in
            // virtual-desktop coordinates and the game's client area is not necessarily at the
            // desktop origin. See GameToScreen - this is the same trap the Party Finder buttons fell
            // into, and the reason a banner "centred" on one machine sat off-screen on another.
            //
            // The motion's offset rides on top of the resting place, so the entrance and the exit
            // are the same arithmetic as the player's own nudge - which is why neither can fight the
            // other, and why the banner always ends up exactly where the numbers say.
            // THE SENTENCE IS CENTRED, NOT THE PANEL. The reserved button space lives at the right
            // end, so a panel centred on the screen would put the words half that space left of
            // centre - about thirty pixels, which is not much and is exactly enough to look like a
            // mistake on the one line somebody is reading. Nudging the panel right by half the
            // reserve puts the text dead centre and lets the buttons hang off the end, which is
            // where they belong anyway.
            var vp = ImGui.GetMainViewport();
            var centre = new Vector2(
                vp.Pos.X + vp.Size.X * 0.5f + AnnounceActionsWidth * 0.5f
                    + config.ClearAnnouncementOffsetX,
                vp.Pos.Y + vp.Size.Y * ClearAnnounceTopFraction + config.ClearAnnouncementOffsetY
                    + motion.OffsetY);

            float dt = ImGui.GetIO().DeltaTime;

            // ══════════════════════════════════════════════════════
            //  WHAT DRAWS IT, AND WHAT CATCHES THE CLICK
            // ══════════════════════════════════════════════════════
            //
            // These are two separate jobs and they used to be one, which is the bug this shape
            // exists to close.
            //
            //   THE PAINT always goes on the FOREGROUND draw list, in every mode. The foreground
            //   list sits above every ImGui window there is, so nothing can ever hide the banner -
            //   not the settings page previewing it, not the plugin's own window open behind it.
            //
            //   THE HIT TARGET is a real, empty ImGui window submitted on top of the same rectangle.
            //   It paints nothing. Its entire job is to exist so that ImGui reports a hover and a
            //   click there, which is the only way to know a press was meant for us rather than for
            //   the game.
            //
            // Doing the paint on the foreground list ALONE is what made this click-through. A draw
            // list is a list of triangles; it has no hit-testing, no hover, no concept of a mouse.
            // The sample went that way when the banner became one line, so that it could be seen
            // over the settings page - and it silently lost every button it had, because there was
            // no longer a window there to catch anything. Splitting the two jobs gets both: always
            // visible, and always clickable.
            //
            // Click-through is then exactly one thing: skip the window. No window, nothing to
            // capture the mouse, and the press lands on the game.
            bool clickThrough = config.ClearAnnouncementClickThrough;

            Vector2 min = centre - layout.Size * 0.5f;
            bool hot = false, pressed = false, onHeart = false, onClose = false;

            if (!clickThrough)
            {
                ImGui.SetNextWindowPos(centre, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
                ImGui.SetNextWindowSize(layout.Size, ImGuiCond.Always);

                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                // NoBringToFrontOnFocus is deliberately ABSENT. It pins a window to the back of the
                // stack, which is what put the banner underneath the plugin's own window - and a
                // hit target underneath something else is a hit target that never fires. It still
                // takes no focus when it appears; it simply is not held down.
                var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoDocking;

                bool open = ImGui.Begin("##PfPresetsClearAnnouncement", flags);
                ImGui.PopStyleVar(2);

                if (open)
                {
                    // The window may have been clamped back onto the screen, so the rectangle that
                    // gets painted is the one ImGui actually settled on - never the one we asked
                    // for. Otherwise a banner nudged off the edge draws in one place and listens in
                    // another.
                    min = ImGui.GetWindowPos();

                    // ONE HIT TARGET FOR THE WHOLE PANEL, then the mouse position decides which
                    // part of it was pressed.
                    //
                    // Rather than three overlapping ImGui items, which is a fight with hover
                    // priority that this binding has only the old SetItemAllowOverlap to settle.
                    // The button establishes that the click is OURS; a plain rectangle test then
                    // says which part of the panel it landed in. Deterministic, nothing to order.
                    ImGui.SetCursorScreenPos(min);
                    ImGui.InvisibleButton("##announcehit", layout.Size);
                    hot = ImGui.IsItemHovered();
                    pressed = ImGui.IsItemClicked(ImGuiMouseButton.Left);

                    AnnounceActionRects(min, layout.Size, out var heartMin, out var heartMax,
                        out var closeMin, out var closeMax);

                    // Guarded on `hot`: IsMouseHoveringRect knows nothing about what else is on top
                    // of this window, and the button above has already answered that honestly.
                    onHeart = hot && ImGui.IsMouseHoveringRect(heartMin, heartMax);
                    onClose = hot && ImGui.IsMouseHoveringRect(closeMin, closeMax);

                    if (hot)
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }

                ImGui.End();
            }

            announceHovered = hot;

            // Eased both ways, so leaving fades them out rather than blinking them off. Pinned shut
            // in click-through, where there is nothing to reveal.
            announceActionFade = clickThrough
                ? 0f
                : Math.Clamp(announceActionFade + (hot ? 1f : -1f) * dt / AnnounceActionFadeTime,
                    0f, 1f);

            PaintAnnouncement(ImGui.GetForegroundDrawList(), post, min, layout,
                motion.Alpha, hot, announceActionFade, onHeart, onClose);

            if (!pressed)
                return;

            // The sample has no post behind it to open or heart, so its buttons do the honest local
            // equivalent: the heart fills so you can see what that looks like, and anything else
            // sends it away. Pressing through to a feed post that does not exist would be the one
            // thing a preview must not do.
            if (sample)
            {
                if (onHeart)
                    AnnouncePreviewPost.Hearted = !AnnouncePreviewPost.Hearted;
                else
                    DismissAnnouncement();

                return;
            }

            // Three targets, most specific first. Anywhere that is not a button is the post.
            if (onClose)
                DismissAnnouncement();
            else if (onHeart)
                ToggleAnnouncedHeart(post);
            else
                OpenAnnouncedPost(post);
        }

        private AnnounceLayout MeasureAnnouncement(AchievementPost post)
        {
            // ── The sentence ──
            //
            // The world in brackets after the name, which is how the game writes a stranger and how
            // the MMO announcements this is borrowed from word it. Three runs rather than one string
            // only so the name and the fight can carry the weight and the joining words can step
            // back - it is the same sentence either way.
            string who = $"{DisplayName(post.Name)} ({post.World})";
            string fight = string.IsNullOrWhiteSpace(post.Title) ? post.FightLabel : post.Title;
            const string verb = " cleared ";

            var whoFont = AnnounceFontFor(who);
            var verbFont = AnnTextFont;
            var fightFont = AnnounceFontFor(fight);

            float whoW, whoH, verbW, fightW, fightH;

            // Each run measured in the face it will be drawn in. A string measured in one font and
            // drawn in another is how text ends up running out of the box it was sized for.
            using (whoFont.Push())
            {
                whoW = ImGui.CalcTextSize(who).X;
                whoH = ImGui.GetTextLineHeight();
            }

            using (verbFont.Push())
                verbW = ImGui.CalcTextSize(verb).X;

            using (fightFont.Push())
            {
                fightW = ImGui.CalcTextSize(fight).X;
                fightH = ImGui.GetTextLineHeight();
            }

            float lineH = MathF.Max(whoH, fightH);
            float bodyH = MathF.Max(lineH, AnnounceActionSize);

            // ── The fight's art ──
            //
            // The same picture the feed puts on a clear, at the size of the line rather than the
            // size of a card - see FightArt. Squared off the line height so it stays in step with
            // the text on its own; a second constant here would be one more thing to remember to
            // move whenever AnnounceTextPx moves.
            //
            // ITS SPACE IS RESERVED WHETHER OR NOT THERE IS A PICTURE, because there is always
            // something to draw: a fight with no art of its own falls back to the same crown or
            // book the feed's cards fall back to. A sentence that shifted sideways depending on
            // whether the server happened to know a slug would be worse than either.
            var art = FightArt(post.FightSlug, post.FightLabel);
            float iconSize = lineH;
            float iconAdvance = iconSize + AnnounceFightIconGap;

            var size = new Vector2(
                whoW + verbW + iconAdvance + fightW + AnnounceActionsWidth + AnnouncePadX * 2f,
                bodyH + AnnouncePadY * 2f);

            return new AnnounceLayout(who, verb, fight, whoFont, verbFont, fightFont,
                whoW, verbW, whoH, fightH, lineH, art, iconSize, iconAdvance, size);
        }

        /// <summary>Where the two buttons sit inside the panel. One place, so the hit test and the
        /// paint cannot disagree about it - which is the classic way a button ends up looking
        /// pressable a few pixels from where it actually is.</summary>
        private static void AnnounceActionRects(Vector2 min, Vector2 size,
            out Vector2 heartMin, out Vector2 heartMax,
            out Vector2 closeMin, out Vector2 closeMax)
        {
            float right = min.X + size.X - AnnouncePadX;
            float centreY = min.Y + size.Y * 0.5f;
            float half = AnnounceActionSize * 0.5f;

            closeMax = new Vector2(right, centreY + half);
            closeMin = new Vector2(right - AnnounceActionSize, centreY - half);

            heartMax = new Vector2(closeMin.X - AnnounceActionGap, centreY + half);
            heartMin = new Vector2(heartMax.X - AnnounceActionSize, centreY - half);
        }

        /// <summary>
        /// Paints the panel, the sentence and the two buttons onto whichever draw list it is handed.
        ///
        /// Two callers with two different lists - a window's for the real thing, so it sits in the
        /// normal stacking order and can be clicked, and the foreground's for the sample, so it is
        /// visible over the settings page that is adjusting it. Everything about how it looks lives
        /// here once, so the sample cannot drift away from the thing it is a sample of.
        /// </summary>
        private void PaintAnnouncement(ImDrawListPtr dl, AchievementPost post,
            Vector2 min, AnnounceLayout l, float alpha, bool hot, float actionFade,
            bool onHeart, bool onClose)
        {
            // ── The ground ──
            //
            // NOT BLACK. The palette's Ground is true black and it read as a hole cut in the game -
            // a solid slab with a sentence on it. This is a soft dark grey instead, so the panel
            // reads as something laid over the game rather than punched into it.
            //
            // NEARLY OPAQUE, though, and that is the correction to how this first shipped. It sat at
            // just over half, which looked handsome over a quiet scene and became unreadable over a
            // bright one - and the scenes this appears over are boss fights, which is to say the
            // brightest and busiest the game ever gets. A notice that cannot be read in the one
            // situation it exists for is not a transparency, it is a bug. Legibility wins; the grey
            // is what keeps it from reading as a hole. Hovering closes the last of the gap, which is
            // the whole of the hover state on the panel itself: no border, no glow, no movement.
            var ground = new Vector4(0.10f, 0.10f, 0.12f, 1f);
            float bgAlpha = (hot ? 0.97f : 0.88f) * alpha;

            dl.AddRectFilled(min, min + l.Size,
                ImGui.ColorConvertFloat4ToU32(ground with { W = bgAlpha }), Radius.Control);

            float x = min.X + AnnouncePadX;
            float centreY = min.Y + l.Size.Y * 0.5f;

            using (l.WhoFont.Push())
            {
                dl.AddText(new Vector2(x, centreY - l.WhoH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink with { W = alpha }), l.Who);
                x += l.WhoW;
            }

            // The verb steps back so the two things worth reading - who, and what - are what the eye
            // lands on. It is joining text, not information.
            using (l.VerbFont.Push())
            {
                dl.AddText(new Vector2(x, centreY - l.WhoH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Dim with { W = alpha }), l.Verb);
                x += l.VerbW;
            }

            // ── The fight's art, immediately before its name ──
            //
            // Tinted with the announcement's own alpha rather than drawn flat. Everything else here
            // fades in and out together, and a picture that stayed solid while the sentence around
            // it faded would be the one thing on screen still arriving after the rest had gone.
            var iconMin = new Vector2(x, centreY - l.IconSize * 0.5f);
            var iconMax = iconMin + new Vector2(l.IconSize, l.IconSize);

            if (l.FightArt != null)
            {
                dl.AddImage(l.FightArt.Handle, iconMin, iconMax, Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha)));
            }
            else
            {
                // The feed's fallback, for the same reason it has one: the server names a fight long
                // before anybody draws it a picture.
                using (pluginInterface.UiBuilder.IconFontHandle.Push())
                {
                    string glyph = (post.Kind == "savage_tier"
                        ? FontAwesomeIcon.Book
                        : FontAwesomeIcon.Crown).ToIconString();

                    Vector2 gs = ImGui.CalcTextSize(glyph);
                    dl.AddText(iconMin + (new Vector2(l.IconSize, l.IconSize) - gs) * 0.5f,
                        ImGui.ColorConvertFloat4ToU32(Dim with { W = alpha }), glyph);
                }
            }

            x += l.IconAdvance;

            using (l.FightFont.Push())
                dl.AddText(new Vector2(x, centreY - l.FightH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink with { W = alpha }), l.Fight);

            if (actionFade <= 0f)
                return;

            AnnounceActionRects(min, l.Size, out var heartMin, out var heartMax,
                out var closeMin, out var closeMax);

            // ── The heart ──
            //
            // The same three states the feed's own button has, and for the same reasons - see
            // DrawCardActions. Locked is drawn as hearted because it IS hearted; what it is not is
            // yours to take back.
            bool locked = post.HeartLocked && !post.Hearted;
            bool filled = post.Hearted || locked;
            bool canPress = !locked && !IsSelf(post.Identity);

            Vector4 heartColour = filled
                ? Accent
                : onHeart && canPress ? Ink : Faint;

            AnnounceGlyph(dl, FontAwesomeIcon.Heart, heartMin, heartMax,
                heartColour with { W = alpha * actionFade });

            // ── The close ──
            AnnounceGlyph(dl, FontAwesomeIcon.Times, closeMin, closeMax,
                (onClose ? Ink : Faint) with { W = alpha * actionFade });
        }

        /// <summary>One icon, centred in its box, in the announcement's own icon face.</summary>
        private void AnnounceGlyph(ImDrawListPtr dl, FontAwesomeIcon icon,
            Vector2 boxMin, Vector2 boxMax, Vector4 colour)
        {
            string glyph = icon.ToIconString();

            using (AnnIconFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(glyph);
                dl.AddText(boxMin + (boxMax - boxMin - ts) * 0.5f,
                    ImGui.ColorConvertFloat4ToU32(colour), glyph);
            }
        }

        /// <summary>
        /// Hearts the announced clear, or takes the heart back.
        ///
        /// DOES NOT DISMISS. Hovering has already stopped the clock, so the banner stays put while
        /// somebody reads their own decision - the heart fills under the cursor and they move away
        /// when they are done, which is the moment the countdown resumes. Dismissing on the press
        /// would take the confirmation away at the instant it was earned.
        /// </summary>
        private void ToggleAnnouncedHeart(AchievementPost post)
        {
            if (post.HeartLocked && !post.Hearted)
                return;

            if (IsSelf(post.Identity))
                return;

            if (post.Hearted)
                Ratings?.Unheart(post);
            else
                Ratings?.Heart(post);
        }

        /// <summary>
        /// Sends it away now, without opening anything.
        ///
        /// Not an instant vanish. It is put at the head of its own exit, so it leaves the way it
        /// always leaves, only immediately - about a quarter of a second. A notice that blinks out
        /// of existence under the cursor reads as a crash; one that goes when told reads as
        /// obedient. The flag is what lets it finish while the cursor is still on it, since hovering
        /// otherwise holds the clock.
        /// </summary>
        private void DismissAnnouncement()
        {
            float life = Math.Clamp(config.ClearAnnouncementSeconds, 2, 30);
            announceElapsed = MathF.Max(announceElapsed, life);
            announceDismissing = true;
        }

        /// <summary>
        /// Opens the feed at the post the banner was announcing.
        ///
        /// The point of being told about a clear is being able to react to it, and the heart lives
        /// on the card. So this does the whole journey rather than half of it: the window, the tab,
        /// the broadcast half of that tab, a read that lands on screen instead of behind the pill,
        /// and a ring on the card so the clear is findable in a column where every row is the same
        /// shape.
        /// </summary>
        private void OpenAnnouncedPost(AchievementPost post)
        {
            announcePost = null;

            isMainWindowVisible = true;
            activeTab = MainTab.Achievements;
            clearsView = ClearsView.Broadcast;
            // ToTop beats the Restore that returning to the tab sets a frame later - see the note
            // in DrawPostList, where the three cases are ordered.
            broadcastScroll.ToTop = true;

            announceFocusId = post.Id;
            announceFocusUntil = DateTime.UtcNow.AddSeconds(12);

            Ratings?.RevealFeedTop();
        }

        /// <summary>Whether this card is the one an announcement was pressed about. Expires on its
        /// own: a ring that never goes away stops meaning "this one".</summary>
        private bool IsAnnouncedCard(AchievementPost post)
            => announceFocusId.Length > 0
               && DateTime.UtcNow <= announceFocusUntil
               && string.Equals(post.Id, announceFocusId, StringComparison.Ordinal);

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  SETTINGS
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        /// <summary>Set while a placement number is being dragged, so the file is written once when
        /// the gesture ends rather than once per frame of it.</summary>
        private bool announceSettingsDirty;

        /// <summary>
        /// The announcement's own section, under the switch that decides whether your clears go out.
        ///
        /// Beside it deliberately: broadcasting is "does the feed hear about me" and this is "do I
        /// hear about the feed", and somebody deciding one is usually deciding both. Putting the
        /// display half on the Appearance page would have split one question across two screens.
        /// </summary>
        private void DrawClearAnnounceSettings()
        {
            if (!BroadcastAvailable)
                return;

            BeginSettingsSection("Clear announcements");

            DrawSetting("Announce other players' clears",
                () => config.ClearAnnouncementsEnabled,
                v =>
                {
                    config.ClearAnnouncementsEnabled = v;
                    if (!v)
                    {
                        announcePost = null;
                        Ratings?.ClearAnnouncements();
                    }
                },
                "A line across the middle of your screen when somebody on the feed clears an "
                + "Ultimate or a savage tier, the way an MMO announces a world first. It fades "
                + "after a few seconds, and unless it is click-through, clicking it opens the feed "
                + "at that post so you can give it a heart. Your own clears are never announced - "
                + "you were there.",
                last: !config.ClearAnnouncementsEnabled);

            if (!config.ClearAnnouncementsEnabled)
            {
                EndSettingsSection();
                return;
            }

            DrawSetting("Click-through",
                () => config.ClearAnnouncementClickThrough,
                v => config.ClearAnnouncementClickThrough = v,
                "The banner ignores the mouse completely, so a click meant for the game reaches the "
                + "game instead of landing on it. It becomes a notice and nothing else: no heart, "
                + "no close button, and the countdown no longer pauses when you point at it, "
                + "because it cannot tell that you have. Worth it if you raid with the middle of "
                + "your screen busy; otherwise leave it off and keep the heart.");

            float width = SettingsContentWidth();

            DrawSettingLabelRow("Position",
                "Nudges the banner from where it sits by default, which is centred and a little "
                + "above the middle of the screen. Drag left and right across a number to move it, "
                + "or double-click to type one. A nudge rather than a position, so the default "
                + "stays right on a screen of any size.", width);

            int offsetX = config.ClearAnnouncementOffsetX;
            int offsetY = config.ClearAnnouncementOffsetY;
            int seconds = Math.Clamp(config.ClearAnnouncementSeconds, 2, 30);

            bool moved = DrawDragNumberRow(width,
                new DragEntry("Across", -4000, 4000, "px",
                    "Left and right from the middle of the screen. Negative moves it left."),
                new DragEntry("Down", -4000, 4000, "px",
                    "Up and down. Negative moves it towards the top of the screen."),
                ref offsetX, ref offsetY);

            if (moved)
            {
                config.ClearAnnouncementOffsetX = offsetX;
                config.ClearAnnouncementOffsetY = offsetY;
                announceSettingsDirty = true;
            }

            DrawSettingLabelRow("How long it stays",
                "Seconds at full before it fades out. The rise in and the drift out are on top of "
                + "this. The countdown pauses while your cursor is on it, so it cannot disappear as "
                + "you reach for it.", width);

            if (DrawDragNumberRow(width,
                    new DragEntry("Time", 2, 30, "s", "Seconds at full, before the fade begins."),
                    ref seconds))
            {
                config.ClearAnnouncementSeconds = seconds;
                announceSettingsDirty = true;
            }

            DrawChoiceSetting("Font", AnnounceFaceLabels,
                () => AnnounceFace,
                v => config.ClearAnnouncementFont = v,
                "Which face the announcement is set in. Jupiter is the display face the game sets "
                + "its own full-screen announcements in, which is why it is the default - an "
                + "announcement in it reads as something the game said. It carries Latin only, so a "
                + "name it cannot draw falls back to the plugin's own face rather than coming out "
                + "as boxes. Axis is the game's interface font and covers everything; the plugin's "
                + "own is what the rest of this window is set in.");

            DrawAnnouncePreviewRow();

            // ONE SAVE PER GESTURE. Dragging across forty pixels writes forty values, and the
            // config file is not something to rewrite forty times for one adjustment - the
            // in-memory value is what the banner reads anyway, so the disk can wait for the mouse.
            if (announceSettingsDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                announceSettingsDirty = false;
                config.Save();
            }

            EndSettingsSection();
        }

        /// <summary>
        /// The Preview button, and the one sentence explaining what it is for.
        ///
        /// A BUTTON RATHER THAN A PERMANENT SAMPLE. The sample used to sit on screen for as long as
        /// this page was open, which answered "where will it be" and nothing else - and where it
        /// will be is the least of what somebody wants to know before letting a plugin draw over
        /// their game. What they want is to see the whole thing happen: how it arrives, how long it
        /// is there, how it leaves. That is a performance, and a performance needs a start.
        ///
        /// Pressing it again while one is running restarts it, which is what anybody who missed the
        /// entrance is going to do.
        /// </summary>
        private void DrawAnnouncePreviewRow()
        {
            ImGui.Dummy(new Vector2(0, Space.Tight));

            // Gated on the same readiness the real ones are, so the sample cannot demonstrate a
            // typeface swap that a real announcement would never show. In practice this is always
            // true by the time anybody can reach the button: the faces have been warming since the
            // plugin's first frame.
            if (DrawAccentOutlineButton("Preview##announcepreview", new Vector2(120, ButtonHeight))
                && WarmAnnounceFonts())
                StartAnnouncement(AnnouncePreviewPost, sample: true);

            ImGui.SameLine(0, Space.Gutter);
            ImGui.AlignTextToFramePadding();

            using (UiHelpFont.Push())
                ImGui.TextColored(Faint,
                    "Plays a sample where a real one would appear, start to finish.");
        }

        /// <summary>One entry on a drag row: what it is called, its bounds, its unit, and the
        /// sentence behind its question mark.</summary>
        private readonly record struct DragEntry(
            string Label, int Min, int Max, string Suffix, string Help);

        private bool DrawDragNumberRow(float width, DragEntry a, ref int valueA)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 rowMin = ImGui.GetCursorScreenPos();
            float x = rowMin.X;

            bool changed = DrawDragEntry(dl, a, ref valueA, rowMin, ref x);

            EndDragRow(dl, rowMin, width);
            return changed;
        }

        /// <summary>
        /// Labelled numbers on one row, each of them draggable.
        ///
        /// The same shape as DrawInlinePairRow, which is where the stepper chips came from, with one
        /// difference that is the whole point: these are dragged. A pixel offset is not a number
        /// anybody knows in advance - it is a thing you find by moving it and looking - so the
        /// gesture that finds it has to be continuous. Typing still works, on a double-click, for
        /// the person who does know.
        /// </summary>
        private bool DrawDragNumberRow(float width, DragEntry a, DragEntry b,
            ref int valueA, ref int valueB)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 rowMin = ImGui.GetCursorScreenPos();
            float x = rowMin.X;

            bool changed = DrawDragEntry(dl, a, ref valueA, rowMin, ref x);
            changed |= DrawDragEntry(dl, b, ref valueB, rowMin, ref x);

            EndDragRow(dl, rowMin, width);
            return changed;
        }

        private const float DragChipWidth = 78f;
        private const float DragChipHeight = 26f;

        /// <summary>A label, its chip and its question mark, flowing from <paramref name="x"/> and
        /// leaving it past the last of them.</summary>
        private bool DrawDragEntry(ImDrawListPtr dl, DragEntry entry, ref int value,
            Vector2 rowMin, ref float x)
        {
            float centreY = rowMin.Y + SettingRowHeight * 0.5f;

            using (UiBodyFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(entry.Label);
                dl.AddText(new Vector2(x, centreY - ts.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Dim), entry.Label);
                x += ts.X + Space.Tight;
            }

            bool changed = DrawDragNumberChip($"drag{entry.Label}", ref value, entry.Suffix,
                entry.Min, entry.Max,
                new Vector2(x, centreY - DragChipHeight * 0.5f),
                new Vector2(DragChipWidth, DragChipHeight));

            x += DragChipWidth + Space.Tight;
            DrawRowHelpMark($"drag{entry.Label}", entry.Help, new Vector2(x, centreY));
            x += 18f + Space.Gutter;

            return changed;
        }

        private static void EndDragRow(ImDrawListPtr dl, Vector2 rowMin, float width)
        {
            DrawRowSeparator(dl, rowMin, SettingRowHeight, 0f, rowMin.X + width);
            ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMin.Y + SettingRowHeight));
        }

        /// <summary>Which chip is mid-drag, and the sub-pixel remainder carried between frames.
        /// One at a time, because a mouse is one thing.</summary>
        private string dragChipId = string.Empty;
        private float dragChipAccum;

        /// <summary>How far the mouse travels for one step. Two pixels of travel per pixel of
        /// offset: a one-to-one drag overshoots on anything with a fine value, and this is the
        /// figure every application that has one of these settles near.</summary>
        private const float DragChipPixelsPerStep = 2f;

        /// <summary>
        /// A number you change by holding the mouse on it and moving sideways.
        ///
        /// Double-click still opens a text field - the same one the stepper chips use, sharing its
        /// state, so only one chip on the page can ever be being typed into. The two gestures do not
        /// collide: a double-click is two presses with no travel between them, so the drag has
        /// nothing to accumulate and changes nothing before the field opens.
        /// </summary>
        private bool DrawDragNumberChip(
            string id, ref int value, string suffix, int min, int max, Vector2 pos, Vector2 size)
        {
            ImGui.SetCursorScreenPos(pos);

            if (chipEditingId == id)
            {
                ImGui.SetNextItemWidth(size.X);
                PushFramedInput();
                if (chipEditFocusPending)
                {
                    ImGui.SetKeyboardFocusHere();
                    chipEditFocusPending = false;
                }
                bool entered = ImGui.InputInt($"##dragedit_{id}", ref chipEditValue, 0, 0, "%d",
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                bool deactivated = ImGui.IsItemDeactivated();
                PopFramedInput();

                if (entered || deactivated)
                {
                    value = Math.Clamp(chipEditValue, min, max);
                    chipEditingId = string.Empty;
                    return true;
                }
                return false;
            }

            ImGui.SetCursorScreenPos(pos);
            ImGui.InvisibleButton($"##dragchip_{id}", size);

            bool hot = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();
            bool changed = false;

            if (ImGui.IsItemActivated())
            {
                dragChipId = id;
                dragChipAccum = 0f;
            }

            if (active && dragChipId == id)
            {
                dragChipAccum += ImGui.GetIO().MouseDelta.X / DragChipPixelsPerStep;

                // Truncated towards zero and the remainder carried, so a slow drag moves one step
                // at a time in the direction it is going rather than stalling at a rounding edge.
                int step = (int)dragChipAccum;
                if (step != 0)
                {
                    dragChipAccum -= step;
                    int next = Math.Clamp(value + step, min, max);
                    if (next != value)
                    {
                        value = next;
                        changed = true;
                    }
                }
            }

            // The east-west arrows are the whole affordance. Nothing about a chip with a number in
            // it says "drag me", and a control nobody discovers is a control that does not exist -
            // so the cursor says it the moment the pointer arrives.
            if (hot || active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);

            var dl = ImGui.GetWindowDrawList();
            var max2 = pos + size;

            dl.AddRectFilled(pos, max2,
                ImGui.ColorConvertFloat4ToU32(active ? BorderControl : hot ? Raised : BgCard),
                Radius.Control);
            dl.AddRect(pos, max2,
                ImGui.ColorConvertFloat4ToU32(hot || active ? BorderHover : BorderDefault),
                Radius.Control, ImDrawFlags.None, 1f);

            string label = $"{value} {suffix}";
            using (UiBodyFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(pos + (size - ts) * 0.5f,
                    ImGui.ColorConvertFloat4ToU32(AccentBlue), label);
            }

            if (hot)
            {
                PaddedTooltip("Drag sideways to change. Double-click to type a number.");

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    chipEditingId = id;
                    chipEditValue = value;
                    chipEditFocusPending = true;
                }
            }

            return changed;
        }
    }
}
#endif
