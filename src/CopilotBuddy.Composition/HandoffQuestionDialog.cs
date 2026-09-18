using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal sealed class HandoffQuestionDialog : Form
{
    private static readonly Color PanelColor = Color.FromArgb(42, 39, 51);
    private static readonly Color HoverColor = Color.FromArgb(63, 83, 96);
    private static readonly Color EdgeColor = Color.FromArgb(75, 71, 88);
    private static readonly Color AccentColor = Color.FromArgb(91, 216, 211);
    private static readonly Color HighlightColor = Color.FromArgb(255, 203, 77);
    private readonly PrivateFontCollection fonts = new();
    private readonly RadioButton specification = new()
        { Text = "1. Specification - requirements and constraints" };
    private readonly RadioButton research = new()
        { Text = "2. Research - questions and sources" };
    private readonly RadioButton implementation = new()
        { Text = "3. Implementation - files and steps" };
    private readonly RadioButton custom = new() { Text = "4. Something else" };
    private readonly TextBox customText = new() { Multiline = true, Enabled = false };
    private readonly Button continueButton = new() { Text = "Continue" };

    private HandoffQuestionDialog(float dpiScale)
    {
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf"));
        float scale = Math.Max(1, dpiScale);
        Font = new Font(fonts.Families[0], 21 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "Handoff";
        AccessibleName = "Handoff output question";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(31, 29, 38);
        ForeColor = Color.FromArgb(247, 244, 231);
        ClientSize = new Size((int)(590 * scale), (int)(430 * scale));

        Label question = new()
        {
            Text = "HANDOFF OUTPUT",
            ForeColor = AccentColor,
            Bounds = Scale(new Rectangle(14, 12, 562, 38), scale)
        };
        ConfigureOption(specification, new Rectangle(12, 61, 566, 50), scale);
        ConfigureOption(research, new Rectangle(12, 115, 566, 50), scale);
        ConfigureOption(implementation, new Rectangle(12, 169, 566, 50), scale);
        ConfigureOption(custom, new Rectangle(12, 223, 566, 50), scale);
        customText.Bounds = Scale(new Rectangle(28, 281, 550, 80), scale);
        customText.BackColor = PanelColor;
        customText.ForeColor = ForeColor;
        customText.BorderStyle = BorderStyle.FixedSingle;
        ConfigureButton(continueButton, new Rectangle(350, 379, 120, 38), scale);
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        ConfigureButton(cancel, new Rectangle(478, 379, 100, 38), scale);
        foreach (RadioButton option in new[] { specification, research, implementation, custom })
        {
            option.CheckedChanged += (_, _) => UpdateOptionStyle(option);
        }
        specification.Checked = true;
        custom.CheckedChanged += (_, _) =>
        {
            customText.Enabled = custom.Checked;
            if (custom.Checked)
            {
                customText.Focus();
            }
            ValidateChoice();
        };
        customText.TextChanged += (_, _) => ValidateChoice();
        continueButton.Click += (_, _) =>
        {
            ValidateChoice();
            if (continueButton.Enabled)
            {
                DialogResult = DialogResult.OK;
            }
        };
        AcceptButton = continueButton;
        CancelButton = cancel;
        Controls.AddRange([question, specification, research, implementation, custom, customText, continueButton, cancel]);
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (customText.Focused)
        {
            return;
        }

        RadioButton? choice = args.KeyCode switch
        {
            Keys.D1 or Keys.NumPad1 => specification,
            Keys.D2 or Keys.NumPad2 => research,
            Keys.D3 or Keys.NumPad3 => implementation,
            Keys.D4 or Keys.NumPad4 => custom,
            _ => null
        };
        if (choice is null)
        {
            return;
        }

        choice.Checked = true;
        choice.Focus();
        if (choice == custom)
        {
            customText.Focus();
        }
        args.Handled = true;
        args.SuppressKeyPress = true;
    }

    public static HandoffRequest? Ask(IWin32Window owner, float dpiScale)
    {
        using HandoffQuestionDialog dialog = new(dpiScale);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }
        if (dialog.specification.Checked) return new(HandoffOutput.Specification);
        if (dialog.research.Checked) return new(HandoffOutput.ResearchInstructions);
        if (dialog.implementation.Checked) return new(HandoffOutput.ImplementationInstructions);
        return new(HandoffOutput.Custom, dialog.customText.Text);
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        SetForegroundWindow(Handle);
        Activate();
        specification.Select();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.None;
        float scale = DeviceDpi / 96f;
        using Pen shadow = new(Color.FromArgb(17, 16, 22), Math.Max(2, 4 * scale));
        using Pen edge = new(AccentColor, Math.Max(1, 3 * scale));
        args.Graphics.DrawRectangle(shadow, (int)(4 * scale), (int)(4 * scale),
            ClientSize.Width - (int)(8 * scale), ClientSize.Height - (int)(8 * scale));
        args.Graphics.DrawRectangle(edge, 1, 1, ClientSize.Width - 4, ClientSize.Height - 4);
        using Pen divider = new(HoverColor, Math.Max(1, 2 * scale));
        args.Graphics.DrawLine(divider, (int)(12 * scale), (int)(53 * scale),
            ClientSize.Width - (int)(13 * scale), (int)(53 * scale));
    }

    private void ValidateChoice() =>
        continueButton.Enabled = !custom.Checked || !string.IsNullOrWhiteSpace(customText.Text);

    private void ConfigureOption(RadioButton option, Rectangle bounds, float scale)
    {
        option.Appearance = Appearance.Button;
        option.AutoSize = false;
        option.Bounds = Scale(bounds, scale);
        option.Padding = new Padding((int)(14 * scale), 0, (int)(14 * scale), 0);
        option.TextAlign = ContentAlignment.MiddleLeft;
        option.FlatStyle = FlatStyle.Flat;
        option.FlatAppearance.BorderSize = Math.Max(1, (int)(2 * scale));
        option.FlatAppearance.MouseOverBackColor = HoverColor;
        option.FlatAppearance.MouseDownBackColor = HoverColor;
        option.FlatAppearance.CheckedBackColor = HoverColor;
        UpdateOptionStyle(option);
    }

    private void UpdateOptionStyle(RadioButton option)
    {
        option.BackColor = option.Checked ? HoverColor : PanelColor;
        option.FlatAppearance.BorderColor = option.Checked ? HighlightColor : EdgeColor;
        option.ForeColor = option.Checked ? HighlightColor : ForeColor;
    }

    private void ConfigureButton(Button button, Rectangle bounds, float scale)
    {
        button.Bounds = Scale(bounds, scale);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = Math.Max(1, (int)(2 * scale));
        button.FlatAppearance.BorderColor = EdgeColor;
        button.FlatAppearance.MouseOverBackColor = HoverColor;
        button.FlatAppearance.MouseDownBackColor = HoverColor;
        button.BackColor = PanelColor;
        button.ForeColor = ForeColor;
    }

    private static Rectangle Scale(Rectangle bounds, float scale) => new(
        (int)(bounds.X * scale), (int)(bounds.Y * scale),
        (int)(bounds.Width * scale), (int)(bounds.Height * scale));

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fonts.Dispose();
        }
        base.Dispose(disposing);
    }
}
