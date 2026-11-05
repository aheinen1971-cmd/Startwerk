using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Startwerk;

public partial class MainWindow
{
    CancellationTokenSource? capacityCancellation;

    void CancelCapacityClick(object sender, RoutedEventArgs e) => RequestCapacityCancellation();

    void RequestCapacityCancellation()
    {
        if (capacityCancellation is null || capacityCancellation.IsCancellationRequested) return;
        CancelCapacityButton.IsEnabled = false;
        Status.Text = "Abbruch angefordert. Bitte warte, bis der Stick freigegeben ist …";
        capacityCancellation.Cancel();
    }

    void UpdateCapacityProgress(CapacityProgress state) => Dispatcher.Invoke(() =>
    {
        if (capacityCancellation is null || capacityCancellation.IsCancellationRequested) return;
        Progress.IsIndeterminate = false;
        Progress.Value = state.OverallPercent;
        string phase = state.Phase == "write" ? "Schreiben" : "Zurücklesen";
        string remaining = state.Remaining is { } duration
            ? $"ca. {(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : "wird berechnet …";
        Status.Text = $"{phase} {state.PhasePercent:F0} % · Restzeit dieser Phase {remaining}";
    });

    async void ManageStick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (Targets.SelectedItem is not Target target)
        {
            MessageBox.Show(this, "Bitte einen USB-Stick auswählen.");
            return;
        }
        bool test = Equals(((Button)sender).Tag, "Test");
        string action = test ? "Kapazität und Speicher prüfen" : "Als exFAT-Datenspeicher wiederherstellen";
        string serial = string.IsNullOrWhiteSpace(target.Serial) ? "nicht gemeldet" : target.Serial;
        string summary = $"{action}\n\nZiel: {target.Label}\nDatenträgernummer: {target.Number}\nSeriennummer: {serial}\n\nAlle Partitionen und Dateien auf diesem Stick werden gelöscht.";
        if (test) summary += " Der Test beschreibt und liest die gesamte Kapazität. Nach Test oder Abbruch kannst du den Stick wiederherstellen.";
        if (!ConfirmUsb(target, summary, test ? "Löschen und prüfen" : "Löschen und formatieren")) return;

        SetBusy(true);
        string result;
        try
        {
            if (test)
            {
                using var cancellation = new CancellationTokenSource();
                capacityCancellation = cancellation;
                CancelCapacityButton.IsEnabled = true;
                CancelCapacityButton.Visibility = Visibility.Visible;
                try
                {
                    bool complete = await Backend.TestUsb(target, cancellation.Token, UpdateCapacityProgress, AddLog);
                    result = complete ? "Kapazitätstest bestanden" : "Kapazitätstest abgebrochen";
                    if (complete) Progress.Value = 100;
                    else AddLog("Kapazitätstest abgebrochen. Die USB-Zugriffe sind beendet. Wenn Testdaten geschrieben wurden, kannst du den Stick mit „Stick wiederherstellen“ neu einrichten.");
                }
                finally
                {
                    capacityCancellation = null;
                    CancelCapacityButton.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                await Backend.RestoreUsb(target, AddLog);
                result = "Datenspeicher wiederhergestellt";
                Progress.Value = 100;
            }
        }
        catch (Exception ex)
        {
            result = "USB-Vorgang fehlgeschlagen";
            AddLog(ex.Message);
            MessageBox.Show(this, ex.Message, result, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
        await Refresh();
        Status.Text = result;
    }

    async void CheckImageHash(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var file = new OpenFileDialog
        {
            Title = "Datei für SHA-256 auswählen",
            Filter = "Abbilder (*.iso;*.img;*.img.xz)|*.iso;*.img;*.img.xz|Alle Dateien|*.*",
            CheckFileExists = true,
            FileName = ImagePath.Text
        };
        if (file.ShowDialog(this) != true) return;

        var dialog = new Window
        {
            Owner = this, Title = "Datei-Prüfsumme", Width = 620,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "SHA-256 des Anbieters zum Vergleich eingeben. Das Feld kann leer bleiben.",
            TextWrapping = TextWrapping.Wrap
        });
        var input = new TextBox { Margin = new Thickness(0, 12, 0, 12), MaxLength = 256 };
        panel.Children.Add(input);
        var calculate = new Button { Content = "SHA-256 berechnen", IsDefault = true };
        input.TextChanged += (_, _) =>
        {
            string value = input.Text.Trim();
            calculate.IsEnabled = value.Length == 0 || value.Length == 64 && value.All(Uri.IsHexDigit);
        };
        calculate.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(calculate);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;

        string expected = input.Text.Trim();
        SetBusy(true);
        try
        {
            Status.Text = "SHA-256 wird berechnet …";
            var (digest, matches) = await Backend.CheckFileHash(file.FileName, expected);
            Status.Text = matches switch
            {
                true => "Prüfsumme stimmt überein",
                false => "Prüfsumme weicht ab",
                null => "SHA-256 berechnet"
            };
            AddLog($"{Path.GetFileName(file.FileName)} · SHA-256: {digest}");
            if (matches.HasValue) AddLog(Status.Text + " · Anbieter: " + expected);
        }
        catch (Exception ex)
        {
            Status.Text = "Prüfsumme fehlgeschlagen";
            AddLog(ex.Message);
        }
        finally { SetBusy(false); }
    }
}
