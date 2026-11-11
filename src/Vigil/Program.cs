using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Vigil;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One copy only: a second launch just exits.
        using var mutex = new Mutex(true, @"Local\Vigil.SingleInstance", out bool first);
        if (!first) return;

        // No main window: WPF runs until Quit calls Shutdown().
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var tray = new Tray(app);
        app.Run();
    }
}

sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;

    public Tray(Application app)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Quit", null, (_, _) => app.Shutdown());

        _icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application, // ponytail: placeholder until step 4 draws CPU % into the icon
            Text = "Vigil",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    // Hide + dispose removes the icon at once instead of leaving a "ghost" until hovered.
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
