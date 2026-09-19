using System.Drawing.Drawing2D;

namespace Shunshou.ScreenRecording.Tests;

/// <summary>All captured pixels are generated here. No desktop or user document is a fixture.</summary>
internal sealed class FixtureWindow : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 25 };
    private int frame;
    private readonly Font titleFont = new("Segoe UI", 25, FontStyle.Bold);
    private readonly Font detailFont = new("Segoe UI", 18);

    internal FixtureWindow()
    {
        Text = "Shunshou recording test - generated fixture only";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("An interactive display is required.");
        var width = Math.Min(960, screen.Bounds.Width - 32);
        var height = Math.Min(800, screen.Bounds.Height - 64);
        Bounds = new Rectangle(screen.Bounds.X + 16, screen.Bounds.Y + 16, width, height);
        TopMost = true;
        DoubleBuffered = true;
        timer.Tick += (_, _) => { frame++; Invalidate(); };
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.FromArgb(17, 30, 48));
        g.FillRectangle(Brushes.Teal, 0, 0, Width, 120);
        g.DrawString("GENERATED RECORDING FIXTURE", titleFont, Brushes.White, 24, 30);
        g.DrawString($"Frame {frame:D6}", detailFont, Brushes.White, 24, 150);
        g.FillRectangle(Brushes.OrangeRed, 24 + frame * 9 % Math.Max(1, Width - 140), 240, 100, 100);
        g.FillEllipse(Brushes.Gold, Width / 2 - 40, 370 + (int)(Math.Sin(frame / 9d) * 90), 80, 80);
        for (int i = 0; i < 12; i++)
        {
            using var brush = new SolidBrush(Color.FromArgb((frame * 11 + i * 23) % 255,
                (frame * 5 + i * 13) % 255, (frame * 3 + i * 41) % 255));
            g.FillRectangle(brush, i * Width / 12, Height - 100, Width / 12 + 1, 100);
        }
        g.DrawString("Synthetic shapes only - microphone disabled", detailFont, Brushes.White, 24, Height - 145);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); titleFont.Dispose(); detailFont.Dispose(); }
        base.Dispose(disposing);
    }
}
