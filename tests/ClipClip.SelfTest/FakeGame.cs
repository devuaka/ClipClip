using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ClipClip.SelfTest;

/// <summary>A separate process with a window, standing in for a game launch.</summary>
internal static class FakeGame
{
    public static int Run(string[] args)
    {
        Color color = args.Length > 0 ? Color.FromName(args[0]) : Color.Red;
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;

        using var form = new Form { Text = "Fake Game", Width = 800, Height = 600, BackColor = color, StartPosition = FormStartPosition.CenterScreen };
        form.Show();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds && !form.IsDisposed)
        {
            Application.DoEvents();
            form.Invalidate();
            Thread.Sleep(16);
        }
        return 0;
    }
}
