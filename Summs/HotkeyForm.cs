namespace Summs;

/// <summary>Dialog to change the key of each role and the modifiers of each action.</summary>
sealed class HotkeyForm : Form
{
    readonly KeyBox[] _roleBoxes;
    readonly ModifierBox _spell1 = new();
    readonly ModifierBox _spell2 = new();
    readonly ModifierBox _remove1 = new();
    readonly ModifierBox _remove2 = new();
    readonly HotkeyBox _clearAll = new();
    readonly Label _example = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };

    public HotkeyConfig Config { get; private set; }

    public HotkeyForm(HotkeyConfig config, Icon? icon)
    {
        Config = config.Clone();
        Text = "Atajos de Summs";
        if (icon is not null)
            Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };

        AddHeader(layout, "Tecla de cada rol (haz clic y pulsa la tecla)");
        _roleBoxes = HotkeyConfig.RoleNames.Select(name =>
        {
            var box = new KeyBox();
            box.KeyChanged += (_, _) => UpdateExample();
            AddRow(layout, name, box);
            return box;
        }).ToArray();

        AddHeader(layout, "Modificadores (haz clic y pulsa Ctrl, Shift y/o Alt; Supr = ninguno)");
        AddRow(layout, "Flash", _spell1);
        AddRow(layout, "Otro hechizo", _spell2);
        AddRow(layout, "Borrar Flash", _remove1);
        AddRow(layout, "Borrar otro hechizo", _remove2);
        foreach (var box in new[] { _spell1, _spell2, _remove1, _remove2 })
            box.ModifiersChanged += (_, _) => UpdateExample();

        AddHeader(layout, "Borrar todos los timers (haz clic y pulsa la combinación)");
        AddRow(layout, "Atajo", _clearAll);
        _clearAll.ComboChanged += (_, _) => UpdateExample();

        layout.Controls.Add(_example);
        layout.SetColumnSpan(_example, 2);

        var defaults = new Button { Text = "Predeterminados", AutoSize = true };
        defaults.Click += (_, _) => Display(new HotkeyConfig());
        var ok = new Button { Text = "Aceptar", AutoSize = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.AddRange(new Control[] { cancel, ok, defaults });
        layout.Controls.Add(buttons);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Display(Config);
    }

    static void AddHeader(TableLayoutPanel layout, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold),
            Margin = new Padding(3, layout.Controls.Count == 0 ? 3 : 12, 3, 3),
        };
        layout.Controls.Add(label);
        layout.SetColumnSpan(label, 2);
    }

    static void AddRow(TableLayoutPanel layout, string text, Control control)
    {
        layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left });
        layout.Controls.Add(control);
    }

    void Display(HotkeyConfig config)
    {
        for (int i = 0; i < _roleBoxes.Length; i++)
            _roleBoxes[i].Key = config.RoleKeys[i];
        _spell1.Modifiers = config.Spell1;
        _spell2.Modifiers = config.Spell2;
        _remove1.Modifiers = config.Remove1;
        _remove2.Modifiers = config.Remove2;
        _clearAll.Set(config.ClearModifiers, config.ClearKey);
        UpdateExample();
    }

    HotkeyConfig Read() => new()
    {
        RoleKeys = _roleBoxes.Select(b => b.Key).ToArray(),
        Spell1 = _spell1.Modifiers,
        Spell2 = _spell2.Modifiers,
        Remove1 = _remove1.Modifiers,
        Remove2 = _remove2.Modifiers,
        ClearKey = _clearAll.Key,
        ClearModifiers = _clearAll.Modifiers,
    };

    void UpdateExample()
    {
        var config = Read();
        var key = config.RoleKeys[0];
        _example.Text = $"Ejemplo: Flash del TOP = {HotkeyConfig.ComboName(config.Spell1, key)}, "
            + $"borrarlo = {HotkeyConfig.ComboName(config.Remove1, key)}\n"
            + $"Borrar todo = {HotkeyConfig.ComboName(config.ClearModifiers, config.ClearKey)}";
    }

    void Accept()
    {
        var config = Read();
        string? error = config.Validate();
        if (error is not null)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Config = config;
        DialogResult = DialogResult.OK;
    }

    /// <summary>The key of a WM_KEYDOWN, normalized like the keyboard hook does.</summary>
    static Keys KeyOf(Message msg)
    {
        long lParam = msg.LParam.ToInt64();
        uint scanCode = (uint)(lParam >> 16) & 0xFF;
        bool extended = ((lParam >> 24) & 1) != 0;
        return HotkeyConfig.Normalize((uint)msg.WParam.ToInt64(), scanCode, extended);
    }

    /// <summary>Read-only box that captures the next key pressed while it has focus.</summary>
    sealed class KeyBox : TextBox
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        Keys _key;

        public event EventHandler? KeyChanged;

        public KeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Width = 140;
            Cursor = Cursors.Hand;
        }

        public Keys Key
        {
            get => _key;
            set
            {
                _key = value;
                Text = HotkeyConfig.KeyName(value);
                KeyChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var keyCode = keyData & Keys.KeyCode;
            // Tab, Enter and Escape keep navigating / accepting / closing the dialog; modifiers are chosen apart.
            if (msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN && keyCode is not (Keys.Tab or Keys.Escape or Keys.Enter)
                && !HotkeyConfig.IsModifierKey(keyCode))
            {
                Key = KeyOf(msg);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>
    /// Read-only box that captures a modifier combination: it accumulates the modifiers held
    /// down and keeps them once they're all released, or as soon as another key completes a
    /// combination (e.g. Ctrl+Alt+Num1 sets Ctrl+Alt). Delete or Backspace alone clear it.
    /// </summary>
    sealed class ModifierBox : TextBox
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetKeyState(Keys key);

        HotkeyModifiers _modifiers;
        HotkeyModifiers _pending; // Modifiers pressed during the current capture.

        public event EventHandler? ModifiersChanged;

        public ModifierBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Width = 140;
            Cursor = Cursors.Hand;
        }

        public HotkeyModifiers Modifiers
        {
            get => _modifiers;
            set
            {
                _modifiers = value;
                _pending = HotkeyModifiers.None;
                Text = HotkeyConfig.ModifiersName(value);
                ModifiersChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        static bool IsDown(Keys key) => GetKeyState(key) < 0;

        /// <summary>Modifiers held right now, as the keyboard hook sees them (left Alt only).</summary>
        public static HotkeyModifiers? HeldModifiers()
        {
            if (IsDown(Keys.RMenu)) // AltGr, which the hook ignores.
                return null;
            var held = HotkeyModifiers.None;
            if (IsDown(Keys.LControlKey) || IsDown(Keys.RControlKey)) held |= HotkeyModifiers.Ctrl;
            if (IsDown(Keys.LShiftKey) || IsDown(Keys.RShiftKey)) held |= HotkeyModifiers.Shift;
            if (IsDown(Keys.LMenu)) held |= HotkeyModifiers.Alt;
            return held;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var keyCode = keyData & Keys.KeyCode;
            if (msg.Msg is not (WM_KEYDOWN or WM_SYSKEYDOWN) || keyCode is Keys.Tab or Keys.Escape or Keys.Enter)
                return base.ProcessCmdKey(ref msg, keyData);

            var held = HeldModifiers();
            if (held is null)
                return true;

            if (HotkeyConfig.IsModifierKey(keyCode))
            {
                _pending |= held.Value;
                Text = HotkeyConfig.ModifiersName(_pending) + "...";
            }
            else if (held == HotkeyModifiers.None && keyCode is Keys.Delete or Keys.Back)
                Modifiers = HotkeyModifiers.None;
            else if (held != HotkeyModifiers.None)
                Modifiers = held.Value;
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            e.Handled = e.SuppressKeyPress = true;
            if (_pending != HotkeyModifiers.None && HeldModifiers() == HotkeyModifiers.None)
                Modifiers = _pending;
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Modifiers = _modifiers; // Drops an unfinished capture.
        }
    }

    /// <summary>
    /// Read-only box that captures a whole hotkey (modifiers + key, e.g. Ctrl+Alt+Num0) in one go:
    /// shows the modifiers while they're held and keeps the combination once a key is pressed.
    /// </summary>
    sealed class HotkeyBox : TextBox
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        public HotkeyModifiers Modifiers { get; private set; }
        public Keys Key { get; private set; }

        public event EventHandler? ComboChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Width = 140;
            Cursor = Cursors.Hand;
        }

        public void Set(HotkeyModifiers modifiers, Keys key)
        {
            Modifiers = modifiers;
            Key = key;
            Text = HotkeyConfig.ComboName(modifiers, key);
            ComboChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var keyCode = keyData & Keys.KeyCode;
            // Tab, Enter and Escape keep navigating / accepting / closing the dialog.
            if (msg.Msg is not (WM_KEYDOWN or WM_SYSKEYDOWN) || keyCode is Keys.Tab or Keys.Escape or Keys.Enter)
                return base.ProcessCmdKey(ref msg, keyData);

            var held = ModifierBox.HeldModifiers();
            if (held is null) // AltGr
                return true;
            if (HotkeyConfig.IsModifierKey(keyCode))
                Text = HotkeyConfig.ModifiersName(held.Value) + "+...";
            else
                Set(held.Value, KeyOf(msg));
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            // Handled, so releasing Alt doesn't open the window menu.
            e.Handled = e.SuppressKeyPress = true;
            if (ModifierBox.HeldModifiers() == HotkeyModifiers.None)
                Text = HotkeyConfig.ComboName(Modifiers, Key); // Modifiers released without a key.
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Text = HotkeyConfig.ComboName(Modifiers, Key);
        }
    }
}
