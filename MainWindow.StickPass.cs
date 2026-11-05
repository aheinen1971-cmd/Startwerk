using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Startwerk;

public partial class MainWindow
{
    IsoDownloadResult? selectedDownload;
    string? selectedManualHash;
    StickPass? lastStickPass;
    string? probeQemu, probeFirmware;

    void InvalidateStickPass(Target target)
    {
        if (lastStickPass is { } pass && pass.Target.UniqueId == target.UniqueId) pass.CanProbe = false;
    }

    void OpenStickPass(object sender, RoutedEventArgs e)
    {
        if (busy || lastStickPass is not { } pass) return;
        var dialog = new Window
        {
            Owner = this, Title = "Startwerk-Stickpass", Width = 820, Height = 600,
            MinWidth = 620, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Add(controls);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);
        var report = new TextBox
        {
            IsReadOnly = true, Text = pass.Report(), TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        panel.Children.Add(report);
        dialog.Content = panel;

        Button AddButton(string label)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(8) };
            controls.Children.Add(button);
            return button;
        }

        var save = AddButton("Stickpass speichern …");
        var probe = AddButton("UEFI-Bootprobe …");
        var physical = AddButton("Ergebnis am echten PC …");
        var cancel = AddButton("Bootprobe abbrechen");
        cancel.Visibility = Visibility.Collapsed;
        probe.IsEnabled = pass.SupportsSecureBootProbe;
        status.Text = pass.SupportsSecureBootProbe
            ? "Die optionale Bootprobe benötigt QEMU, die geprüfte OVMF-Firmware und Platz für eine Kopie des Sticks."
            : "Die virtuelle Bootprobe ist für ein unverändertes Windows-x64-Medium mit UEFI-Startdatei verfügbar.";

        CancellationTokenSource? running = null;
        void RequestStop()
        {
            cancel.IsEnabled = false;
            status.Text = "Abbruch angefordert. Startwerk wartet auf die Freigabe …";
            running?.Cancel();
        }
        dialog.Closing += (_, args) =>
        {
            if (running is null) return;
            args.Cancel = true;
            RequestStop();
        };
        cancel.Click += (_, _) => RequestStop();
        save.Click += (_, _) =>
        {
            var file = new SaveFileDialog
            {
                Filter = "Textdatei (*.txt)|*.txt", DefaultExt = ".txt",
                FileName = $"Startwerk-Stickpass-{pass.WrittenAt:yyyy-MM-dd-HHmm}"
            };
            if (file.ShowDialog(dialog) != true) return;
            try
            {
                File.WriteAllText(file.FileName, pass.Report());
                status.Text = "Stickpass gespeichert.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        physical.Click += (_, _) =>
        {
            var answer = MessageBox.Show(dialog, "Hast du den Stick auf einem echten PC gestartet und das Setup oder Live-System gesehen?\n\nJa: erreicht. Nein: nicht erreicht. Abbrechen: keine Änderung.",
                "Praxistest dokumentieren", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel) return;
            pass.PhysicalResult = answer == MessageBoxResult.Yes ? "Setup oder Live-System erreicht (manuell bestätigt)" : "Setup oder Live-System nicht erreicht (manuell gemeldet)";
            report.Text = pass.Report();
        };
        probe.Click += async (_, _) =>
        {
            var answer = MessageBox.Show(dialog, "Startwerk erstellt eine temporäre Kopie des Sticks und startet sie mit QEMU ohne Netzwerk. Schließe das Testfenster, sobald du das Windows-Setup gesehen hast. Verwende eine QEMU-Installation, der du vertraust.",
                "UEFI-Bootprobe", MessageBoxButton.OKCancel);
            if (answer != MessageBoxResult.OK || !ChooseProbeTools(dialog)) return;

            using var cancellation = new CancellationTokenSource();
            running = cancellation;
            save.IsEnabled = probe.IsEnabled = physical.IsEnabled = false;
            cancel.Visibility = Visibility.Visible;
            cancel.IsEnabled = true;
            try
            {
                status.Text = "Bootprobe wird vorbereitet …";
                string snapshotHash = await BootProbe.Run(pass.Target, probeQemu!, probeFirmware!, message =>
                {
                    if (!cancellation.IsCancellationRequested) status.Text = message;
                }, cancellation.Token);
                answer = MessageBox.Show(dialog, "War im virtuellen Testfenster das Windows-Setup zu sehen?\n\nJa: erreicht. Nein: nicht erreicht. Abbrechen: keine Beobachtung.",
                    "Beobachtung zur Bootprobe", MessageBoxButton.YesNoCancel);
                string observed = answer switch
                {
                    MessageBoxResult.Yes => "Windows-Setup erreicht (manuell bestätigt)",
                    MessageBoxResult.No => "Windows-Setup nicht erreicht (manuell gemeldet)",
                    _ => "Fenster geschlossen. Kein Ergebnis bestätigt."
                };
                pass.VirtualResult = $"{DateTimeOffset.Now:dd.MM.yyyy HH:mm zzz} · UEFI-Secure-Boot-Profil · {observed}";
                pass.SnapshotSha256 = snapshotHash;
                status.Text = "Virtuelle Bootprobe beendet. Ein echter PC wird separat geprüft.";
            }
            catch (OperationCanceledException) { status.Text = "Bootprobe abgebrochen."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally
            {
                running = null;
                save.IsEnabled = physical.IsEnabled = true;
                probe.IsEnabled = pass.SupportsSecureBootProbe;
                cancel.Visibility = Visibility.Collapsed;
                report.Text = pass.Report();
            }
        };
        dialog.ShowDialog();
    }

    bool ChooseProbeTools(Window owner)
    {
        if (!File.Exists(probeQemu))
        {
            var file = new OpenFileDialog
            {
                Title = "Installiertes QEMU auswählen", Filter = "QEMU für x64|qemu-system-x86_64.exe;qemu-system-x86_64w.exe",
                CheckFileExists = true
            };
            if (file.ShowDialog(owner) != true) return false;
            if (Path.GetFileName(file.FileName) is not ("qemu-system-x86_64.exe" or "qemu-system-x86_64w.exe")) return false;
            probeQemu = file.FileName;
        }
        var folder = new OpenFolderDialog { Title = "Ordner mit der geprüften OVMF-Firmware auswählen" };
        if (Directory.Exists(probeFirmware)) folder.InitialDirectory = probeFirmware;
        if (folder.ShowDialog(owner) != true) return false;
        probeFirmware = folder.FolderName;
        return true;
    }
}
