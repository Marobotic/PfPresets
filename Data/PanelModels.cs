#if PFP_RATINGS
using System.Collections.Generic;

namespace PfPresets
{
    // ══════════════════════════════════════════════════════════════
    //  MODERATOR PANEL
    //
    //  The panel is server-driven: the client asks for a screen and renders whatever tabs, rows,
    //  columns and controls come back, then posts actions against it. These models are the wire
    //  format for that exchange. They ship in every build - see Core/PanelAccess.cs.
    // ══════════════════════════════════════════════════════════════

    internal sealed class ScreenTab
    {
        public string Id { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;
    }

    internal sealed class ScreenOpen
    {
        public string Name { get; set; } = string.Empty;

        public string World { get; set; } = string.Empty;
    }

    internal sealed class ScreenResponse
    {
        public string Screen { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public List<ScreenTab> Tabs { get; set; } = new List<ScreenTab>();

        public List<ScreenControl> Controls { get; set; } = new List<ScreenControl>();

        public List<ScreenColumn> Columns { get; set; } = new List<ScreenColumn>();

        public List<ScreenRow> Rows { get; set; } = new List<ScreenRow>();

        public string Note { get; set; } = string.Empty;

        public string Empty { get; set; } = string.Empty;
    }

    internal sealed class ScreenRow
    {
        public List<ScreenCell> Cells { get; set; } = new List<ScreenCell>();

        public List<ScreenAction> Actions { get; set; } = new List<ScreenAction>();
    }

    internal sealed class ScreenCell
    {
        public string Text { get; set; } = string.Empty;

        public string? Tone { get; set; }

        public string? Tip { get; set; }

        public ScreenOpen? Open { get; set; }
    }

    internal sealed class ScreenColumn
    {
        public string Label { get; set; } = string.Empty;

        public float Width { get; set; }

        public float Stretch { get; set; }

        public string? Align { get; set; }
    }

    internal sealed class ScreenControl
    {
        public string Kind { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public int Value { get; set; }

        public bool On { get; set; }

        public List<string>? Options { get; set; }
    }

    internal sealed class ScreenInput
    {
        public string Id { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public int Value { get; set; }
    }

    internal sealed class ScreenAction
    {
        public string Label { get; set; } = string.Empty;

        public string? Tone { get; set; }

        public string? Icon { get; set; }

        public string Token { get; set; } = string.Empty;

        public ScreenConfirm? Confirm { get; set; }

        public List<ScreenInput>? Inputs { get; set; }
    }

    internal sealed class ScreenActionResponse
    {
        public bool Ok { get; set; }

        public string? Note { get; set; }
    }

    internal sealed class ScreenConfirm
    {
        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public string Ok { get; set; } = string.Empty;

        public string? Detail { get; set; }
    }

    internal sealed class SubjectActions
    {
        public List<ScreenAction> Actions { get; set; } = new List<ScreenAction>();
    }

    internal sealed class ResolvedName
    {
        public string Name { get; set; } = string.Empty;

        public string World { get; set; } = string.Empty;

        public string Target { get; set; } = string.Empty;
    }

    internal sealed class ResolveResponse
    {
        public List<ResolvedName> Players { get; set; } = new List<ResolvedName>();
    }

    internal sealed class ReleaseResponse
    {
        public bool Ok { get; set; }

        public int Released { get; set; }
    }
}
#endif
