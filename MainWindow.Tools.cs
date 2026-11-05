using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Startwerk;

public partial class MainWindow
{
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
        if (test) summary += " Der Test beschreibt und liest die gesamte Kapazität. Danach kannst du den Stick wiederherstellen.";
        if (!ConfirmUsb(target, summary, test ? "Löschen und prüfen" : "Löschen und formatieren")) return;

        SetBusy(true);
        string result;
        try
        {
            await Backend.ManageUsb(target, test, AddLog);
            result = test ? "Kapazitätstest bestanden" : "Datenspeicher wiederhergestellt";
            Progress.Value = 100;
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
