using System;
using System.Linq;
using System.Threading;
using ClipClip.Engine.Core;
using Velopack;

namespace ClipClip;

internal static class Program
{
    private const string InstanceName = "ClipClip.SingleInstance.v1";
    private const string ShowSignalName = "ClipClip.ShowWindow.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        // Runs installer hooks and exits during install or uninstall.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => WindowsStartup.Set(false, ""))
            .Run();

        // A copy with its own data folder is a separate app, so test runs cannot touch a real install.
        string scope = Environment.GetEnvironmentVariable("CLIPCLIP_DATA_DIR") is { Length: > 0 } dir
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToUpperInvariant())))[..12]
            : "";
        using var instance = new Mutex(true, InstanceName + scope, out bool isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName + scope);
        if (!isFirst)
        {
            showSignal.Set();
            return 0;
        }

        var app = new App(showSignal, args.Contains("--minimized"));
        app.InitializeComponent();
        return app.Run();
    }
}
