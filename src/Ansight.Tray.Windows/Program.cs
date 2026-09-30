using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ansight.Tray.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string CommandSchema = "ansight.desktop-command/v1";
    private const string ActionSchema = "ansight.desktop-action/v1";
    private const string IconResourceName = "Ansight.Tray.Windows.Assets.ansight-icon.png";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Control dispatcher = new();
    private readonly Icon icon;
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem openItem;
    private readonly ToolStripMenuItem copyItem;
    private readonly ToolStripMenuItem runnerStatusItem;
    private readonly ToolStripMenuItem updateItem;
    private Uri? explorerUrl;
    private bool disposed;

    public TrayApplicationContext()
    {
        dispatcher.CreateControl();
        _ = dispatcher.Handle;
        icon = LoadTrayIcon();

        openItem = new ToolStripMenuItem("Open Ansight", null, (_, _) => OpenAnsight())
        {
            Enabled = false
        };
        copyItem = new ToolStripMenuItem("Copy Local URL", null, (_, _) => CopyLocalUrl())
        {
            Enabled = false
        };
        runnerStatusItem = new ToolStripMenuItem("Remote runner: Disabled")
        {
            Enabled = false
        };
        updateItem = new ToolStripMenuItem("Update Ansight", null, (_, _) => SendDesktopAction("update"))
        {
            Available = false
        };
        var restartItem = new ToolStripMenuItem("Restart Ansight", null, (_, _) => SendDesktopAction("restart"));
        var stopItem = new ToolStripMenuItem("Stop Ansight", null, (_, _) => SendDesktopAction("stop"));
        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(runnerStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(updateItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(stopItem);

        notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Ansight",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => OpenAnsight();
        notifyIcon.BalloonTipClicked += (_, _) => OpenAnsight();

        _ = Task.Run(ReadCommands);
    }

    private void ReadCommands()
    {
        try
        {
            while (Console.In.ReadLine() is { } line)
            {
                DesktopCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize<DesktopCommand>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (command is null
                    || !string.Equals(command.Schema, CommandSchema, StringComparison.Ordinal))
                {
                    continue;
                }

                dispatcher.BeginInvoke(() => HandleCommand(command));
                if (string.Equals(command.Kind, "shutdown", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
        finally
        {
            if (!disposed)
            {
                dispatcher.BeginInvoke(ExitThread);
            }
        }
    }

    private void HandleCommand(DesktopCommand command)
    {
        switch (command.Kind)
        {
            case "initialize":
                explorerUrl = Uri.TryCreate(command.Url, UriKind.Absolute, out var url) ? url : null;
                openItem.Enabled = explorerUrl is not null;
                copyItem.Enabled = explorerUrl is not null;
                updateItem.Available = command.UpdateAvailable == true;
                updateItem.Enabled = true;
                break;
            case "update-availability":
                updateItem.Available = command.UpdateAvailable == true;
                updateItem.Enabled = true;
                break;
            case "runner-status":
                runnerStatusItem.Text = $"Remote runner: {DisplayRunnerState(command.RunnerState)}";
                runnerStatusItem.ToolTipText = command.RunnerDetail;
                break;
            case "notify" when !string.IsNullOrWhiteSpace(command.Title)
                                && !string.IsNullOrWhiteSpace(command.Body):
                ShowNotification(command.Title, command.Body);
                break;
            case "shutdown":
                ExitThread();
                break;
        }
    }

    private static string DisplayRunnerState(string? state) => state?.ToLowerInvariant() switch
    {
        "starting" => "Starting",
        "idle" => "Ready",
        "busy" => "Running a job",
        "error" => "Needs attention",
        _ => "Disabled"
    };

    private void OpenAnsight()
    {
        if (explorerUrl is null)
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = explorerUrl.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void CopyLocalUrl()
    {
        if (explorerUrl is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(explorerUrl.AbsoluteUri);
        }
        catch (ExternalException)
        {
            // Another process may briefly own the clipboard. Keep the tray alive.
        }
    }

    private void ShowNotification(string title, string body)
    {
        try
        {
            notifyIcon.ShowBalloonTip(
                8_000,
                Truncate(title, 63),
                Truncate(body, 255),
                ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
            // Notification delivery must not end the resident host.
        }
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value[..maximumLength];
    }

    private static void SendDesktopAction(string action)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new DesktopAction(ActionSchema, action),
            JsonOptions));
        Console.Out.Flush();
    }

    private static Icon LoadTrayIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName)
                           ?? throw new InvalidOperationException("The Ansight tray icon resource is missing.");
        using var bitmap = new Bitmap(stream);
        var iconHandle = bitmap.GetHicon();
        try
        {
            using var borrowedIcon = Icon.FromHandle(iconHandle);
            return (Icon)borrowedIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }

    protected override void ExitThreadCore()
    {
        if (!disposed)
        {
            disposed = true;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            icon.Dispose();
            dispatcher.Dispose();
        }

        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            ExitThreadCore();
        }

        base.Dispose(disposing);
    }

    private sealed record DesktopCommand(
        string Schema,
        string Kind,
        string? Url,
        string? Identifier,
        string? Title,
        string? Body,
        string? SessionId,
        bool? UpdateAvailable,
        string? RunnerState,
        string? RunnerDetail);

    private sealed record DesktopAction(string Schema, string Action);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr iconHandle);
    }
}
