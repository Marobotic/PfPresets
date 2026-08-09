using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// Drawing a comment the way the game draws one.
    ///
    /// After <see cref="CommentText.Decode"/> an auto-translate phrase arrives as its words with a
    /// bracket glyph on each end, and those brackets are the whole visual signal that the phrase
    /// is auto-translate rather than something the leader typed: the game tints them, opening
    /// green and closing red, and everyone reading Party Finder knows the shape. Printing the
    /// string in one colour would show the phrase but lose what it is, so every place the plugin
    /// draws a comment splits it into runs and tints the brackets.
    ///
    /// Deliberately drawlist-based. The comment sites already draw that way (clipped rows in the
    /// status card, a wrapped block in the preset row), and a run of ImGui.TextColored calls
    /// stitched with SameLine cannot wrap in the middle of a phrase without leaving gaps.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// The brackets' tints. Data colours, like the role and vote colours: they mean
        /// "auto-translate" and follow the game rather than the configured accent. Not the theme's
        /// Positive/Negative either - nothing here is passing or failing, and red in this plugin is
        /// otherwise reserved for destructive actions.
        /// </summary>
        private static readonly Vector4 AutoTranslateOpenTint = ColorFromHex("#7fbf5f");
        private static readonly Vector4 AutoTranslateCloseTint = ColorFromHex("#c1584f");

        /// <summary>One stretch of a comment drawn in a single colour.</summary>
        private readonly struct CommentRun
        {
            public readonly string Text;
            public readonly Vector4 Color;

            public CommentRun(string text, Vector4 color)
            {
                Text = text;
                Color = color;
            }
        }

        /// <summary>
        /// Splits a comment into runs, giving each bracket glyph its own so it can be tinted.
        /// Text with no auto-translate in it comes back as a single run, which is the common case
        /// and costs one scan.
        /// </summary>
        private static List<CommentRun> SplitCommentRuns(string text, Vector4 baseColor)
        {
            var runs = new List<CommentRun>();
            if (string.IsNullOrEmpty(text))
                return runs;

            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != CommentText.AutoTranslateOpen && c != CommentText.AutoTranslateClose)
                    continue;

                if (i > start)
                    runs.Add(new CommentRun(text[start..i], baseColor));

                runs.Add(new CommentRun(
                    c.ToString(),
                    c == CommentText.AutoTranslateOpen ? AutoTranslateOpenTint : AutoTranslateCloseTint));

                start = i + 1;
            }

            if (start < text.Length)
                runs.Add(new CommentRun(text[start..], baseColor));

            return runs;
        }

        /// <summary>
        /// Draws one line of comment at a position, tinting any auto-translate brackets.
        /// Returns the width drawn, so callers can lay out around it.
        /// </summary>
        private static float DrawCommentLine(ImDrawListPtr dl, Vector2 pos, string text, Vector4 baseColor)
        {
            if (string.IsNullOrEmpty(text))
                return 0f;

            // The overwhelmingly common case: no phrases, so no reason to walk runs at all.
            if (!CommentText.HasAutoTranslate(text))
            {
                dl.AddText(pos, ImGui.ColorConvertFloat4ToU32(baseColor), text);
                return ImGui.CalcTextSize(text).X;
            }

            float x = pos.X;
            foreach (var run in SplitCommentRuns(text, baseColor))
            {
                dl.AddText(new Vector2(x, pos.Y), ImGui.ColorConvertFloat4ToU32(run.Color), run.Text);
                x += ImGui.CalcTextSize(run.Text).X;
            }

            return x - pos.X;
        }

        /// <summary>
        /// <see cref="DrawCommentLine"/> clipped to a horizontal band, for the status card's rows.
        /// </summary>
        private static void ClippedCommentLine(ImDrawListPtr dl, string text,
            float left, float right, float y, float line, Vector4 baseColor)
        {
            dl.PushClipRect(new Vector2(left, y), new Vector2(right, y + line), true);
            DrawCommentLine(dl, new Vector2(left, y), text, baseColor);
            dl.PopClipRect();
        }

        /// <summary>
        /// Draws a comment at the ImGui cursor, wrapped to a width and capped at a line count, and
        /// advances the cursor past it. For the flow-layout sites that used ImGui.TextColored.
        /// </summary>
        private static void DrawWrappedComment(string text, float width, int maxLines, Vector4 baseColor) =>
            DrawCommentLines(WrapCommentToLines(text, width, maxLines), baseColor, width);

        /// <summary>
        /// Draws already-wrapped comment lines at the ImGui cursor and advances past them, for
        /// callers that wrap by their own rule (the editor's preview mimics the game's listing
        /// width in characters, not pixels).
        /// </summary>
        private static void DrawCommentLines(IReadOnlyList<string> lines, Vector4 baseColor, float width = 0f)
        {
            if (lines.Count == 0)
                return;

            var dl = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();
            float lineHeight = ImGui.GetTextLineHeight();
            float drawn = 0f;

            for (int i = 0; i < lines.Count; i++)
                drawn = MathF.Max(drawn,
                    DrawCommentLine(dl, new Vector2(origin.X, origin.Y + i * lineHeight), lines[i], baseColor));

            ImGui.Dummy(new Vector2(width > 0f ? width : drawn, lines.Count * lineHeight));
        }
    }
}
