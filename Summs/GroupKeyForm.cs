namespace Summs;

/// <summary>Dialog to set the optional group key; empty means automatic rooms with no key.</summary>
sealed class GroupKeyForm : Form
{
    readonly TextBox _key = new() { Width = 260, MaxLength = 100 };

    public string GroupKey => _key.Text.Trim();

    public GroupKeyForm(string groupKey, Icon? icon)
    {
        Text = "Clave de grupo";
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

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Text = "Sin clave, Summs comparte los timers con los compañeros de la partida que lo usen. "
                + "Con clave, solo con quienes pongan la misma. Déjala vacía para no usarla.",
        });
        _key.Text = groupKey;
        layout.Controls.Add(_key);

        var ok = new Button { Text = "Aceptar", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(layout);
    }
}
