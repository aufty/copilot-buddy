using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal sealed class HandoffQuestionDialog : Form
{
    private readonly PrivateFontCollection fonts = new();
    private readonly RadioButton specification = new() { Text = "A spec", AutoSize = true };
    private readonly RadioButton research = new() { Text = "Research instructions", AutoSize = true };
    private readonly RadioButton implementation = new()
    {
        Text = "Concrete implementation instructions with details, line numbers, and breakdown",
        AutoSize = true
    };
    private readonly RadioButton custom = new() { Text = "Something else", AutoSize = true };
    private readonly TextBox customText = new() { Multiline = true, Enabled = false };
    private readonly Button continueButton = new() { Text = "Continue" };

    private HandoffQuestionDialog(float dpiScale)
    {
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf"));
        float scale = Math.Max(1, dpiScale);
        Font = new Font(fonts.Families[0], 21 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "Handoff";
        AccessibleName = "Handoff output question";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(31, 29, 38);
        ForeColor = Color.FromArgb(247, 244, 231);
        ClientSize = new Size((int)(590 * scale), (int)(390 * scale));

        Label question = new()
        {
            Text = "What should be the output of this handoff?",
            ForeColor = Color.FromArgb(91, 216, 211),
            Bounds = Scale(new Rectangle(20, 18, 550, 42), scale)
        };
        specification.Bounds = Scale(new Rectangle(28, 74, 520, 30), scale);
        research.Bounds = Scale(new Rectangle(28, 112, 520, 30), scale);
        implementation.Bounds = Scale(new Rectangle(28, 150, 545, 58), scale);
        custom.Bounds = Scale(new Rectangle(28, 216, 520, 30), scale);
        customText.Bounds = Scale(new Rectangle(48, 252, 514, 72), scale);
        customText.BackColor = Color.FromArgb(42, 39, 51);
        customText.ForeColor = ForeColor;
        customText.BorderStyle = BorderStyle.FixedSingle;
        continueButton.Bounds = Scale(new Rectangle(382, 340, 110, 34), scale);
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = Scale(new Rectangle(500, 340, 70, 34), scale)
        };
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

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.None;
        using Pen border = new(Color.FromArgb(91, 216, 211), Math.Max(2, DeviceDpi / 48f));
        args.Graphics.DrawRectangle(border, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
    }

    private void ValidateChoice() =>
        continueButton.Enabled = !custom.Checked || !string.IsNullOrWhiteSpace(customText.Text);

    private static Rectangle Scale(Rectangle bounds, float scale) => new(
        (int)(bounds.X * scale), (int)(bounds.Y * scale),
        (int)(bounds.Width * scale), (int)(bounds.Height * scale));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fonts.Dispose();
        }
        base.Dispose(disposing);
    }
}
