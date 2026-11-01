using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ClipClip.Engine;

namespace ClipClip.Services;

/// <summary>Tray icon and menu. The icon colour is the always-visible recording indicator.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly ClipController _controller;
    private readonly NotifyIcon _icon;
    private readonly Icon _recording = LoadIcon("tray-rec.ico");
    private readonly Icon _idle = LoadIcon("tray-idle.ico");
    private readonly Icon _paused = LoadIcon("tray-pause.ico");
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _clip;
    private readonly ToolStripMenuItem _pause;

    public TrayIcon(ClipController controller, Action open, Action exit)
    {
        _controller = controller;

        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        _status = new ToolStripMenuItem("ClipClip") { Enabled = false };
        _clip = new ToolStripMenuItem("Clip Now", null, (_, _) => controller.ClipNow(ClipTrigger.Tray));
        _pause = new ToolStripMenuItem("Pause Recording", null, (_, _) => controller.SetPaused(!controller.UserPaused));
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_clip);
        menu.Items.Add(new ToolStripMenuItem("Open ClipClip", null, (_, _) => open()));
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));
        menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon { Icon = _idle, Text = "ClipClip", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) open();
        };
        Refresh();
    }

    public void Refresh()
    {
        RecordingMode mode = _controller.Mode;
        (Icon icon, string text) = mode switch
        {
            RecordingMode.Recording => (_recording, "Recording"),
            RecordingMode.Starting => (_recording, "Starting"),
            RecordingMode.Paused => (_paused, "Paused"),
            RecordingMode.Error => (_paused, "Recording problem"),
            _ => (_idle, "Waiting for a game"),
        };
        _icon.Icon = icon;
        _icon.Text = "ClipClip: " + text;
        _status.Text = text;
        _clip.Enabled = _controller.CanClip;
        _pause.Text = _controller.UserPaused ? "Resume Recording" : "Pause Recording";
    }

    public void Hint(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);

    private static Icon LoadIcon(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                              ?? throw new FileNotFoundException(name);
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _recording.Dispose();
        _idle.Dispose();
        _paused.Dispose();
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.FromArgb(26, 29, 38);
        private static readonly Color Hover = Color.FromArgb(35, 39, 51);
        private static readonly Color Line = Color.FromArgb(42, 47, 60);

        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Back);

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using var brush = new SolidBrush(Hover);
            e.Graphics.FillRectangle(brush, new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(243, 244, 246) : Color.FromArgb(120, 126, 142);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(42, 47, 60);
        public override Color ToolStripDropDownBackground => Color.FromArgb(26, 29, 38);
    }
}
