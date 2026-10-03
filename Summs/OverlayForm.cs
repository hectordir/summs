using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Summs;

/// <summary>How each overlay row is laid out.</summary>
enum RowStyle
{
    /// <summary>[champion icon] Flash en 17:20</summary>
    ChampionText,

    /// <summary>ADC [champion icon][spell icon] 17:20</summary>
    RoleIcons,
}

/// <summary>Whether a timer counts the Cosmic Insight rune.</summary>
enum CosmicInsight
{
    /// <summary>Not counted: no Inspiration tree, or the user said they don't have it.</summary>
    None,

    /// <summary>Counted because they run Inspiration; the API doesn't say which runes, so it's unconfirmed.</summary>
    Assumed,

    /// <summary>Counted; the user confirmed it (e.g. from the TAB scoreboard).</summary>
    Confirmed,
}

/// <summary>
/// A spell timer for one enemy: which role/slot it belongs to and when it's back. Lane orders
/// the overlay groups: 1 TOP, 2 JG, 3 MID, 4 ADC, 5 SUP. PressedAt is the game time the hotkey
/// was pressed and ItemHaste the summoner spell haste from items, so the timer can be
/// recalculated when Cosmic Insight is confirmed or ruled out.
/// </summary>
sealed record SpellTimer(string Role, int Lane, int Slot, string ChampionKey, string SpellId, string SpellName,
    double PressedAt, int BaseCooldown, int ItemHaste, CosmicInsight CosmicInsight)
{
    [JsonIgnore]
    public int Haste => ItemHaste + (CosmicInsight == CosmicInsight.None ? 0 : DataDragon.CosmicInsightHaste);

    [JsonIgnore]
    public int ReadyAt => Spells.ReadyAt(PressedAt, BaseCooldown, Haste);
}

/// <summary>
/// Always-on-top overlay listing the active summoner spell timers, styled after the Blitz.gg overlay.
/// Drawn with per-pixel alpha (UpdateLayeredWindow) so the background is translucent but the
/// text is opaque. Anchored at its bottom edge so it grows upwards. Draggable with the mouse
/// at any time (like Blitz) without taking focus from the game. Only visible over the game in
/// Borderless or Windowed mode.
/// </summary>
sealed class OverlayForm : Form
{
    const int ReadyDisplaySeconds = 10;
    const int WarningSeconds = 60;
    const int BlinkDurationMs = 2000;
    const int BlinkIntervalMs = 250;
    const int MessageDurationMs = 3000;
    const int NoGameNoteDurationMs = 4000;
    const int DimAlpha = 60;
    const int WheelStepSeconds = 5;
    const string InspirationLabel = " insp.";
    const string InspirationHint = "¿Lleva Perspicacia cósmica? Míralo en el TAB\nClic: sí  ·  Clic derecho: no";

    // Layout, in pixels.
    const int PadX = 10, PadY = 7, RowHeight = 18, ColumnGap = 14, CornerRadius = 5;
    const int IconSize = 16, IconGap = 3, SeparatorGap = 7;

    static readonly Color Background = Color.FromArgb(165, 16, 20, 24);
    static readonly Color LabelColor = Color.FromArgb(168, 174, 178);
    static readonly Color TextColor = Color.FromArgb(205, 210, 214);
    static readonly Color ReadyColor = Color.FromArgb(76, 206, 120);
    static readonly Color InspirationColor = Color.FromArgb(110, 205, 195); // teal, like the rune tree
    static readonly Color InspirationHoverColor = Color.FromArgb(170, 245, 235);
    static readonly Color HintColor = Color.FromArgb(240, 200, 80);
    static readonly Color ErrorColor = Color.FromArgb(232, 72, 85);
    static readonly Color IconPlaceholder = Color.FromArgb(60, 66, 72);
    static readonly Color SeparatorColor = Color.FromArgb(55, 255, 255, 255);
    static readonly Color RowHoverColor = Color.FromArgb(28, 255, 255, 255);

    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 0x2;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    const int ULW_ALPHA = 0x2;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    readonly Font _font = new("Arial", 8.5f, FontStyle.Bold);
    readonly Font _smallFont = new("Arial", 7f);
    readonly Dictionary<(string Role, int Slot), TimerEntry> _timers = new();
    readonly Dictionary<string, Image?> _icons = new(); // "kind/key" -> icon, null while loading or missing
    readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = BlinkIntervalMs };
    readonly System.Windows.Forms.Timer _messageTimer = new() { Interval = MessageDurationMs };
    readonly System.Windows.Forms.Timer _noGameTimer = new() { Interval = NoGameNoteDurationMs };
    readonly HintPopup _hint = new();
    readonly WheelHook _wheelHook;
    // Where each "insp." marker was last drawn, in client coordinates, and whose it is.
    readonly List<(Rectangle Bounds, string ChampionKey)> _inspirationMarkers = new();
    // Where each timer row and its delete button were last drawn, in client coordinates.
    readonly List<(Rectangle Row, Rectangle Delete, (string Role, int Slot) Key)> _timerRows = new();
    string? _hoveredChampion;
    (string Role, int Slot)? _hoveredRow;
    bool _hoveredDelete;
    int _wheelDelta; // Wheel movement not yet applied, for fine-grained wheels and touchpads.
    bool _noGame;
    bool _noGameNoteVisible; // the "sin partida" note shows only briefly after losing the game
    double _gameTime;
    bool _showPlaceholder;
    (string Text, Color Color)? _message;
    RowStyle _style;
    int _left, _bottom;

    sealed class TimerEntry(SpellTimer timer)
    {
        public SpellTimer Timer { get; } = timer;
        public bool WarningShown { get; set; }
        public bool ReadyShown { get; set; }
        public DateTime BlinkUntil { get; set; }
    }

    /// <summary>
    /// Left part (label + icons) is left-aligned; segments are right-aligned. Separator draws a
    /// line above the row, between enemy groups.
    /// </summary>
    sealed record Row(string Label, string[] IconKeys, (string Text, Color Color)[] Segments,
        bool Dim = false, bool Separator = false, string? InspirationChampion = null,
        (string Role, int Slot)? TimerKey = null);

    public OverlayForm(int left, int bottom, RowStyle style)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _left = left;
        _bottom = bottom;
        _style = style;
        // Any wheel over the visible overlay is ours, even off the rows, so it never zooms the game.
        _wheelHook = new WheelHook(() => Visible && Bounds.Contains(Cursor.Position), OnWheel);
        _blinkTimer.Tick += (_, _) => Render();
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            _message = null;
            Render();
        };
        _noGameTimer.Tick += (_, _) =>
        {
            _noGameTimer.Stop();
            _noGameNoteVisible = false;
            Render();
        };
    }

    public bool HasTimers => _timers.Count > 0;

    /// <summary>Raised after the user finishes dragging the overlay.</summary>
    public event EventHandler? Moved;

    /// <summary>Raised when the user answers whether an enemy (by champion key) has Cosmic Insight.</summary>
    public event EventHandler<(string ChampionKey, bool HasIt)>? CosmicInsightAnswered;

    /// <summary>Raised when the user moves a timer with the wheel; carries the moved timer.</summary>
    public event EventHandler<SpellTimer>? TimerAdjusted;

    /// <summary>Raised when the user deletes a timer with its button.</summary>
    public event EventHandler<(string Role, int Slot)>? TimerDeleted;

    public IEnumerable<SpellTimer> Timers => _timers.Values.Select(e => e.Timer);

    /// <summary>Left edge and bottom edge of the overlay; the bottom stays fixed as it grows.</summary>
    public (int Left, int Bottom) AnchorPoint => (_left, _bottom);

    public RowStyle Style
    {
        get => _style;
        set
        {
            _style = value;
            Render();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            return cp;
        }
    }

    /// <summary>
    /// Adds or replaces a timer. Quietly, a replaced timer doesn't blink as if it had just reached
    /// the warning or come back (e.g. a teammate moved it with the wheel).
    /// </summary>
    public void SetTimer(SpellTimer timer, double gameTime, bool quiet = false)
    {
        var key = (timer.Role, timer.Slot);
        double remaining = timer.ReadyAt - gameTime;
        _timers[key] = quiet && _timers.ContainsKey(key)
            ? new TimerEntry(timer) { WarningShown = remaining <= WarningSeconds, ReadyShown = remaining <= 0 }
            : new TimerEntry(timer);
        Tick(gameTime);
    }

    public void RemoveTimer(string role, int slot)
    {
        if (_timers.Remove((role, slot)))
            Render();
    }

    /// <summary>Recalculates the unconfirmed timers of an enemy once Cosmic Insight is confirmed or ruled out.</summary>
    public void SetCosmicInsight(string championKey, bool hasIt)
    {
        var state = hasIt ? CosmicInsight.Confirmed : CosmicInsight.None;
        foreach (var (key, entry) in _timers.Where(t => t.Value.Timer.ChampionKey == championKey
                     && t.Value.Timer.CosmicInsight == CosmicInsight.Assumed).ToList())
            _timers[key] = new TimerEntry(entry.Timer with { CosmicInsight = state });
        Tick(_gameTime);
    }

    /// <summary>
    /// With no timers, shows a tiny "sin partida" note for a few seconds when no game is detected
    /// (at startup or when a game ends), then hides the overlay.
    /// </summary>
    public void SetNoGame(bool noGame)
    {
        if (noGame == _noGame)
            return;
        _noGame = noGame;
        _noGameNoteVisible = noGame;
        _noGameTimer.Stop();
        if (noGame)
            _noGameTimer.Start();
        Render();
    }

    public void Clear()
    {
        _timers.Clear();
        Render();
    }

    /// <summary>Shows a short-lived message row, e.g. when a hotkey can't be resolved.</summary>
    public void ShowMessage(string message) => ShowMessage(message, ErrorColor);

    /// <summary>Like <see cref="ShowMessage(string)"/>, for news rather than errors.</summary>
    public void ShowInfo(string message) => ShowMessage(message, HintColor);

    void ShowMessage(string message, Color color)
    {
        _message = (message, color);
        _messageTimer.Stop();
        _messageTimer.Start();
        Render();
    }

    /// <summary>Shows a placeholder while there are no timers, so the overlay can be positioned.</summary>
    public void SetPlaceholder(bool enabled)
    {
        _showPlaceholder = enabled;
        Render();
    }

    /// <summary>
    /// Updates the display for the current game time, starts a blink when a timer reaches the
    /// warning threshold or comes back, and drops finished timers.
    /// </summary>
    public void Tick(double gameTime)
    {
        _gameTime = gameTime;
        foreach (var key in _timers.Where(t => gameTime >= t.Value.Timer.ReadyAt + ReadyDisplaySeconds).Select(t => t.Key).ToList())
            _timers.Remove(key);

        var blinkUntil = DateTime.Now.AddMilliseconds(BlinkDurationMs);
        foreach (var entry in _timers.Values)
        {
            double remaining = entry.Timer.ReadyAt - gameTime;
            if (!entry.WarningShown && remaining <= WarningSeconds && remaining > 0)
            {
                entry.WarningShown = true;
                entry.BlinkUntil = blinkUntil;
            }
            if (!entry.ReadyShown && remaining <= 0)
            {
                entry.ReadyShown = true;
                entry.BlinkUntil = blinkUntil;
            }
        }
        Render();
    }

    List<Row> BuildRows()
    {
        var now = DateTime.Now;
        var rows = new List<Row>();

        // One group per enemy (role): the champion is shown only on its first row and groups are
        // separated by a line. Groups are ordered by lane (TOP..SUP), spells by slot (D, F).
        var groups = _timers.Values
            .GroupBy(e => e.Timer.Lane)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(e => e.Timer.Slot).ToList());

        foreach (var group in groups)
        foreach (var entry in group)
        {
            var t = entry.Timer;
            bool first = entry == group[0];
            bool separator = first && rows.Count > 0;
            // While blinking, every other interval the row is drawn dimmed.
            bool dim = now < entry.BlinkUntil
                && (int)((entry.BlinkUntil - now).TotalMilliseconds / BlinkIntervalMs) % 2 == 1;
            double remaining = t.ReadyAt - _gameTime;
            var color = Spells.ColorOf(t.SpellId);

            // Status: "17:20", "17:20 (0:59)" in the last minute, or "listo (+0:03)" once back.
            var status = new List<(string, Color)>();
            if (remaining <= 0)
            {
                status.Add(("listo", ReadyColor));
                status.Add(($" (+{Spells.FormatTime((int)-remaining)})", ReadyColor));
            }
            else
            {
                status.Add((Spells.FormatTime(t.ReadyAt), color));
                if (remaining <= WarningSeconds)
                    status.Add(($" ({Spells.FormatTime((int)Math.Ceiling(remaining))})", color));

                // Warns that the time assumes Cosmic Insight: it may come back later. Hovering
                // it asks the user to confirm it (see OnMouseMove).
                if (t.CosmicInsight == CosmicInsight.Assumed)
                    status.Add((InspirationLabel, _hoveredChampion == t.ChampionKey ? InspirationHoverColor : InspirationColor));
            }

            // An empty icon key leaves the champion's column blank on its later rows.
            string championIcon = first ? $"champion/{t.ChampionKey}" : "";
            string? inspiration = remaining > 0 && t.CosmicInsight == CosmicInsight.Assumed ? t.ChampionKey : null;
            if (_style == RowStyle.RoleIcons)
            {
                rows.Add(new Row(first ? t.Role : "", new[] { championIcon, $"spell/{t.SpellId}" },
                    status.ToArray(), dim, separator, inspiration, (t.Role, t.Slot)));
            }
            else
            {
                string connector = remaining <= 0 ? " " : " en ";
                status.Insert(0, (t.SpellName + connector, TextColor));
                rows.Add(new Row("", new[] { championIcon }, status.ToArray(), dim, separator, inspiration, (t.Role, t.Slot)));
            }
        }

        if (_showPlaceholder && rows.Count == 0)
            rows.Add(new Row("", Array.Empty<string>(), new[] { ("Arrastra para mover", HintColor) }));
        if (_message is (string messageText, Color messageColor))
            rows.Add(new Row("", Array.Empty<string>(), new[] { (messageText, messageColor) }, Separator: rows.Count > 0));
        return rows;
    }

    /// <summary>Returns the icon if loaded; otherwise starts loading it and re-renders when ready.</summary>
    Image? GetIcon(string iconKey)
    {
        if (_icons.TryGetValue(iconKey, out var icon))
            return icon;

        _icons[iconKey] = null;
        string[] parts = iconKey.Split('/', 2);
        _ = LoadIconAsync(iconKey, parts[0], parts[1]);
        return null;
    }

    async Task LoadIconAsync(string iconKey, string kind, string key)
    {
        var image = await DataDragon.LoadIconAsync(kind, key);
        if (image is null)
            return;
        _icons[iconKey] = image;
        Render();
    }

    void Render()
    {
        // Keep the fast blink timer running only while some row is blinking.
        bool blinking = _timers.Values.Any(t => DateTime.Now < t.BlinkUntil);
        if (blinking != _blinkTimer.Enabled)
            _blinkTimer.Enabled = blinking;

        var rows = BuildRows();
        _inspirationMarkers.Clear();
        _timerRows.Clear();
        if (rows.Count == 0)
        {
            SetHoveredChampion(null);
            SetHoveredRow(null, false);
            if (_noGame && _noGameNoteVisible)
                RenderNoGame();
            else
                Hide();
            return;
        }

        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        float Measure(string text) => measure.MeasureString(text, _font, PointF.Empty, format).Width;

        // Timer rows (with label or icons) share columns; message rows span the full width
        // and don't widen the columns, so the times stay next to the icons.
        static bool IsTimerRow(Row r) => r.Label.Length > 0 || r.IconKeys.Length > 0;
        var timerRows = rows.Where(IsTimerRow).ToList();
        float SegmentsWidth(Row r) => r.Segments.Sum(seg => Measure(seg.Text));

        int labelWidth = (int)Math.Ceiling(timerRows.Select(r => Measure(r.Label)).DefaultIfEmpty(0).Max());
        int iconCount = timerRows.Select(r => r.IconKeys.Length).DefaultIfEmpty(0).Max();
        int iconsWidth = iconCount * IconSize + Math.Max(0, iconCount - 1) * IconGap;
        int leftWidth = labelWidth + (labelWidth > 0 && iconsWidth > 0 ? IconGap * 2 : 0) + iconsWidth;
        int rightWidth = (int)Math.Ceiling(timerRows.Select(SegmentsWidth).DefaultIfEmpty(0).Max());
        int gap = leftWidth > 0 ? ColumnGap : 0;
        int timerWidth = leftWidth + gap + rightWidth;
        int messageWidth = (int)Math.Ceiling(rows.Where(r => !IsTimerRow(r)).Select(SegmentsWidth).DefaultIfEmpty(0).Max());

        int width = PadX * 2 + Math.Max(timerWidth, messageWidth);
        // Row tops, leaving room for a separator line between groups.
        var rowTops = new int[rows.Count];
        int top = PadY;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Separator)
                top += SeparatorGap;
            rowTops[i] = top;
            top += RowHeight;
        }
        int height = top + PadY;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            using (var path = RoundedRect(new Rectangle(0, 0, width - 1, height - 1), CornerRadius))
            using (var brush = new SolidBrush(Background))
            {
                g.FillPath(brush, path);
                if (_showPlaceholder)
                {
                    using var pen = new Pen(HintColor) { DashStyle = DashStyle.Dash };
                    g.DrawPath(pen, path);
                }
            }

            float textOffset = (RowHeight - _font.GetHeight(g)) / 2;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                int rowTop = rowTops[i];
                float y = rowTop + textOffset;
                Color Shade(Color c) => row.Dim ? Color.FromArgb(DimAlpha, c) : c;

                if (row.Separator)
                {
                    int lineY = rowTop - SeparatorGap / 2 - 1;
                    using var pen = new Pen(SeparatorColor);
                    g.DrawLine(pen, PadX, lineY, width - PadX, lineY);
                }

                int iconX = PadX + labelWidth + (labelWidth > 0 ? IconGap * 2 : 0);
                int iconY = rowTop + (RowHeight - IconSize) / 2;
                // Hovering a timer row offers to delete it with a button over its first icon.
                bool hovered = row.TimerKey is not null && row.TimerKey == _hoveredRow;
                var deleteBounds = new Rectangle(iconX, iconY, IconSize, IconSize);
                if (row.TimerKey is { } timerKey)
                    _timerRows.Add((new Rectangle(PadX / 2, rowTop, width - PadX, RowHeight), deleteBounds, timerKey));
                if (hovered)
                {
                    using var brush = new SolidBrush(RowHoverColor);
                    g.FillRectangle(brush, PadX / 2, rowTop, width - PadX, RowHeight);
                }

                using (var labelBrush = new SolidBrush(Shade(LabelColor)))
                    g.DrawString(row.Label, _font, labelBrush, PadX, y, format);

                for (int k = 0; k < row.IconKeys.Length; k++)
                {
                    string iconKey = row.IconKeys[k];
                    if (k == 0 && hovered)
                        DrawDeleteButton(g, deleteBounds, _hoveredDelete);
                    else if (iconKey.Length > 0)
                        DrawIcon(g, GetIcon(iconKey), new Rectangle(iconX, iconY, IconSize, IconSize), row.Dim);
                    iconX += IconSize + IconGap;
                }

                // Timer status is right-aligned to its column, the time highlighted like Blitz's
                // stats; message rows are left-aligned.
                float x = IsTimerRow(row) ? PadX + timerWidth - SegmentsWidth(row) : PadX;
                foreach (var (text, color) in row.Segments)
                {
                    using var brush = new SolidBrush(Shade(color));
                    g.DrawString(text, _font, brush, x, y, format);
                    float textWidth = Measure(text);
                    if (row.InspirationChampion is string champion && text == InspirationLabel)
                    {
                        // The leading space isn't part of the hover target.
                        float space = Measure(" ");
                        _inspirationMarkers.Add((Rectangle.FromLTRB((int)(x + space), rowTop, (int)Math.Ceiling(x + textWidth), rowTop + RowHeight), champion));
                        if (_hoveredChampion == champion)
                        {
                            using var pen = new Pen(Shade(color));
                            float underline = y + _font.GetHeight(g);
                            g.DrawLine(pen, x + space, underline, x + textWidth, underline);
                        }
                    }
                    x += textWidth;
                }
            }
        }

        // Shift left if the overlay grew past the right edge of the screen it's on.
        Present(bitmap);

        // Rows and markers may have moved or gone; keep the hover in sync with where the mouse is now.
        var mouse = PointToClient(Cursor.Position);
        if (_hoveredChampion is not null)
            SetHoveredChampion(MarkerAt(mouse)?.ChampionKey);
        if (_hoveredRow is not null)
            UpdateHoveredRow(mouse);
    }

    /// <summary>Tiny, faint "● sin partida" pill at the overlay's spot (it can be dragged too).</summary>
    void RenderNoGame()
    {
        const string text = "sin partida";
        const int padX = 5, padY = 2, dot = 4, dotGap = 4;
        var textSize = TextRenderer.MeasureText(text, _smallFont, Size.Empty, TextFormatFlags.NoPadding);
        int width = padX * 2 + dot + dotGap + textSize.Width;
        int height = padY * 2 + textSize.Height;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var path = RoundedRect(new Rectangle(0, 0, width - 1, height - 1), 3))
            using (var background = new SolidBrush(Color.FromArgb(110, Background)))
                g.FillPath(background, path);
            using (var dotBrush = new SolidBrush(Color.FromArgb(170, HintColor)))
                g.FillEllipse(dotBrush, padX, (height - dot) / 2f, dot, dot);
            using var textBrush = new SolidBrush(Color.FromArgb(150, LabelColor));
            g.DrawString(text, _smallFont, textBrush, padX + dot + dotGap - 2, padY);
        }
        Present(bitmap);
    }

    /// <summary>Shows the bitmap with its bottom-left at the anchor, kept inside the screen.</summary>
    void Present(Bitmap bitmap)
    {
        // Shift left if the overlay grew past the right edge of the screen it's on.
        var screen = Screen.FromPoint(new Point(_left, _bottom)).Bounds;
        var location = new Point(Math.Max(screen.Left, Math.Min(_left, screen.Right - bitmap.Width)), _bottom - bitmap.Height);
        SetBounds(location.X, location.Y, bitmap.Width, bitmap.Height);
        if (!Visible)
            Show();

        ApplyBitmap(bitmap, location);
        // The game may push itself above us; reassert topmost without stealing focus.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Red square with an "x", brighter while the mouse is over it.</summary>
    static void DrawDeleteButton(Graphics g, Rectangle rect, bool hot)
    {
        using (var path = RoundedRect(new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), 3))
        using (var brush = new SolidBrush(Color.FromArgb(hot ? 255 : 170, ErrorColor)))
            g.FillPath(brush, path);
        const int inset = 4;
        using var pen = new Pen(Color.White, 1.6f);
        g.DrawLine(pen, rect.Left + inset, rect.Top + inset, rect.Right - inset - 1, rect.Bottom - inset - 1);
        g.DrawLine(pen, rect.Right - inset - 1, rect.Top + inset, rect.Left + inset, rect.Bottom - inset - 1);
    }

    static void DrawIcon(Graphics g, Image? icon, Rectangle rect, bool dim)
    {
        if (icon is null)
        {
            using var brush = new SolidBrush(dim ? Color.FromArgb(DimAlpha, IconPlaceholder) : IconPlaceholder);
            g.FillRectangle(brush, rect);
            return;
        }

        if (!dim)
        {
            g.DrawImage(icon, rect);
            return;
        }

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = DimAlpha / 255f });
        g.DrawImage(icon, rect, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
    }

    void ApplyBitmap(Bitmap bitmap, Point location)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = bitmap.Size;
            var source = Point.Empty;
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref location, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    (Rectangle Bounds, string ChampionKey)? MarkerAt(Point point)
    {
        foreach (var marker in _inspirationMarkers)
            if (marker.Bounds.Contains(point))
                return marker;
        return null;
    }

    /// <summary>Highlights an enemy's "insp." markers and shows the hint next to the one under the mouse.</summary>
    void SetHoveredChampion(string? championKey)
    {
        if (championKey == _hoveredChampion)
            return;
        _hoveredChampion = championKey;
        UpdateCursor();

        var marker = championKey is null ? null : MarkerAt(PointToClient(Cursor.Position));
        if (marker is null)
            _hint.Hide();
        else
            _hint.ShowNear(InspirationHint, RectangleToScreen(marker.Value.Bounds), _font);
        if (Visible)
            Render();
    }

    (Rectangle Row, Rectangle Delete, (string Role, int Slot) Key)? TimerRowAt(Point point)
    {
        foreach (var row in _timerRows)
            if (row.Row.Contains(point))
                return row;
        return null;
    }

    void UpdateHoveredRow(Point point)
    {
        var row = TimerRowAt(point);
        SetHoveredRow(row?.Key, row?.Delete.Contains(point) == true);
    }

    /// <summary>Highlights the timer row under the mouse and shows its delete button.</summary>
    void SetHoveredRow((string Role, int Slot)? key, bool onDelete)
    {
        if (key == _hoveredRow && onDelete == _hoveredDelete)
            return;
        _hoveredRow = key;
        _hoveredDelete = onDelete;
        UpdateCursor();
        if (Visible)
            Render();
    }

    void UpdateCursor() =>
        Cursor = _hoveredChampion is not null || _hoveredDelete ? Cursors.Hand : Cursors.Default;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHoveredChampion(MarkerAt(e.Location)?.ChampionKey);
        UpdateHoveredRow(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoveredChampion(null);
        SetHoveredRow(null, false);
    }

    /// <summary>
    /// The wheel over a timer row moves it by 5s per notch: up, the spell comes back later; down,
    /// sooner (e.g. it was used a while before the hotkey was pressed). It arrives through
    /// <see cref="WheelHook"/>, which keeps it from the game so the camera doesn't zoom.
    /// </summary>
    void OnWheel(int delta)
    {
        if (TimerRowAt(PointToClient(Cursor.Position)) is not { } row || !_timers.TryGetValue(row.Key, out var entry))
        {
            _wheelDelta = 0;
            return;
        }

        _wheelDelta += delta;
        int steps = _wheelDelta / SystemInformation.MouseWheelScrollDelta;
        if (steps == 0)
            return;
        _wheelDelta -= steps * SystemInformation.MouseWheelScrollDelta;

        var timer = entry.Timer with { PressedAt = entry.Timer.PressedAt + steps * WheelStepSeconds };
        // Moving a timer shouldn't make it blink as if it had just reached the warning or come back.
        SetTimer(timer, _gameTime, quiet: true);
        Log.Write($"{row.Key.Role} hechizo {row.Key.Slot} movido {steps * WheelStepSeconds:+0;-0}s: {Spells.FormatTime(timer.ReadyAt)}");
        TimerAdjusted?.Invoke(this, timer);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Left click on "insp." confirms Cosmic Insight, right click rules it out.
        if (MarkerAt(e.Location) is { } marker && e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            SetHoveredChampion(null);
            CosmicInsightAnswered?.Invoke(this, (marker.ChampionKey, e.Button == MouseButtons.Left));
            return;
        }

        // Left click on a row's delete button removes that timer.
        if (e.Button == MouseButtons.Left && TimerRowAt(e.Location) is { } row && row.Delete.Contains(e.Location))
        {
            Log.Write($"{row.Key.Role} hechizo {row.Key.Slot} eliminado desde la superposición");
            RemoveTimer(row.Key.Role, row.Key.Slot);
            TimerDeleted?.Invoke(this, row.Key);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            // The drag loop above returns once the mouse is released.
            _left = Left;
            _bottom = Bottom;
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _blinkTimer.Dispose();
            _messageTimer.Dispose();
            _noGameTimer.Dispose();
            _hint.Dispose();
            _wheelHook.Dispose();
            _font.Dispose();
            _smallFont.Dispose();
            foreach (var icon in _icons.Values)
                icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Small tooltip shown above a point of the overlay. It never takes focus from the game and
    /// lets the mouse through, so it doesn't steal the hover from the overlay.
    /// </summary>
    sealed class HintPopup : Form
    {
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOPMOST = 0x8;
        static readonly Padding TextPadding = new(8, 5, 8, 5);

        string _text = "";

        public HintPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(16, 20, 24);
            ForeColor = TextColor;
            Opacity = 0.95; // Makes it layered, which WS_EX_TRANSPARENT needs to let clicks through.
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>Shows the text just above the anchor (in screen coordinates), kept on its screen.</summary>
        public void ShowNear(string text, Rectangle anchor, Font font)
        {
            _text = text;
            Font = font;
            var textSize = TextRenderer.MeasureText(text, font);
            var size = new Size(textSize.Width + TextPadding.Horizontal, textSize.Height + TextPadding.Vertical);
            var screen = Screen.FromRectangle(anchor).WorkingArea;
            int x = Math.Max(screen.Left, Math.Min(anchor.Left, screen.Right - size.Width));
            int y = anchor.Top - size.Height - 4;
            if (y < screen.Top)
                y = anchor.Bottom + 4;
            SetBounds(x, y, size.Width, size.Height);
            Invalidate();
            if (!Visible)
                Show();
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var border = new Pen(InspirationColor);
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, _text, Font, new Point(TextPadding.Left, TextPadding.Top), ForeColor);
        }
    }
}
