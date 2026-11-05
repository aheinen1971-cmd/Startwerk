using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Startwerk;
public sealed record Target(string Id, string Label, int Number = -1, string UniqueId = "", long Size = 0, string Serial = "");
public sealed record CapacityProgress(string Phase, long Done, long Total, double ElapsedSeconds)
{
    public double PhasePercent => 100.0 * Done / Total;
    public double OverallPercent => (Phase == "read" ? 50 : 0) + PhasePercent / 2;
    public TimeSpan? Remaining => Done == Total ? TimeSpan.Zero
        : Done > 0 && ElapsedSeconds >= 3
            ? TimeSpan.FromSeconds(Math.Min(315360000, Math.Ceiling(ElapsedSeconds * (Total - Done) / Done)))
            : null;

    internal const string Prefix = "CAPACITY_PROGRESS ";

    internal static CapacityProgress Parse(string line)
    {
        var fields = line.Split(' ');
        if (fields.Length != 5 || fields[1] is not ("write" or "read")
            || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long done)
            || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long total)
            || total <= 0 || done > total
            || !double.TryParse(fields[4], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds))
            throw new IOException("Ungültiger Fortschrittswert des Kapazitätstests.");
        return new CapacityProgress(fields[1], done, total, seconds);
    }
}
public static class Backend
{
    public static List<Target> ListUsb()
    {
        const string command = "@(Get-Disk | Where-Object { $_.BusType -eq 'USB' -and -not $_.IsBoot -and -not $_.IsSystem -and -not $_.IsReadOnly -and -not $_.IsOffline } | Select-Object Number,FriendlyName,UniqueId,SerialNumber,Size) | ConvertTo-Json -Compress";
        var output = RunPs(command);
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var json = JsonDocument.Parse(output);
        IEnumerable<JsonElement> rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
        return rows.Select(x => new Target("usb", $"USB {x.GetProperty("Number").GetInt32()} · {x.GetProperty("FriendlyName").GetString()} · {x.GetProperty("Size").GetInt64()/1073741824.0:F1} GiB", x.GetProperty("Number").GetInt32(), x.GetProperty("UniqueId").GetString() ?? "", x.GetProperty("Size").GetInt64(), x.GetProperty("SerialNumber").GetString() ?? "")).ToList();
    }
    static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    public static string RunPs(string command)
    {
        var psi = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        psi.Environment["PSModulePath"]=Path.Combine(Path.GetDirectoryName(PowerShell)!,"Modules");
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ErrorActionPreference='Stop'; " + command)));
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync(); string result = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(stderr.GetAwaiter().GetResult()); return result;
    }
    public static Task WriteUsb(string path, Target target, bool windows, bool verify, Action<string> log, string bootMode = "UEFI", bool windows11Compatibility=false, string? sourceOrigin=null)
    {
        if(windows11Compatibility&&!windows)throw new IOException("Windows-11-Kompatibilität ist nur im Windows-USB-Modus verfügbar.");
        return RunEmbeddedUsb(false, new { Path = path, SourceOrigin=sourceOrigin, target.Number, target.UniqueId, target.Size, target.Serial, Windows = windows, Verify = verify, BootMode = windows ? bootMode : "Image", Windows11Compatibility=windows11Compatibility }, log);
    }
    public static Task RestoreUsb(Target target, Action<string> log)
        => RunEmbeddedUsb(false, new { target.Number, target.UniqueId, target.Size, target.Serial, DeviceAction="Restore" }, log);

    public static async Task<bool> TestUsb(Target target, CancellationToken token, Action<CapacityProgress> progress, Action<string> log)
    {
        string cancelEvent = $@"Local\Startwerk-Capacity-{Guid.NewGuid():N}";
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, cancelEvent);
        using var registration = token.Register(() => signal.Set());
        Exception? callbackError = null;
        int exit = await RunEmbeddedUsb(false,
            new { target.Number, target.UniqueId, target.Size, target.Serial, DeviceAction="Test", CancelEvent=cancelEvent },
            line =>
            {
                if (callbackError is not null) return;
                try
                {
                    if (line.StartsWith(CapacityProgress.Prefix, StringComparison.Ordinal)) progress(CapacityProgress.Parse(line));
                    else log(line);
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                    signal.Set();
                }
            }, allowCancellation: true);
        if (callbackError is not null) throw new IOException(callbackError.Message, callbackError);
        if (exit == 2 && !token.IsCancellationRequested) throw new IOException("Kapazitätstest unerwartet abgebrochen.");
        return exit == 0;
    }
    public static async Task<(string Digest, bool? Matches)> CheckFileHash(string path, string expected)
    {
        expected = expected.Trim();
        if (expected.Length != 0 && (expected.Length != 64 || !expected.All(Uri.IsHexDigit)))
            throw new ArgumentException("SHA-256 muss aus genau 64 Hexadezimalzeichen bestehen.", nameof(expected));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
        return (digest, expected.Length == 0 ? null : digest.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }
    public static async Task<IsoFacts> InspectIso(string path)
    {
        var lines = new List<string>();
        await RunEmbeddedUsb(false, new { Path = path, InspectOnly = true }, lines.Add);
        return JsonSerializer.Deserialize<IsoFacts>(string.Join("\n", lines)) ?? throw new IOException("Abbild-Erkennung lieferte kein Ergebnis.");
    }
    public static Task TestEmbeddedUsb(Action<string> log) => RunEmbeddedUsb(true, null, log);
    static async Task<int> RunEmbeddedUsb(bool selfTest, object? request, Action<string> log, bool allowCancellation = false)
    {
        using var resource = typeof(Backend).Assembly.GetManifestResourceStream("Startwerk.UsbScript") ?? throw new IOException("Eingebautes USB-Modul fehlt.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        string script = await reader.ReadToEndAsync();
        // The script and request travel as separate data records over standard input.
        // Only this short fixed bootstrap is on the command line; no script file is needed.
        const string bootstrap = "[Console]::InputEncoding=New-Object System.Text.UTF8Encoding($false); [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false); $ErrorActionPreference='Stop'; try { $config=([Console]::ReadLine() | ConvertFrom-Json); $requestJson=if (!$config.SelfTest) { [Console]::ReadLine() }; $body=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($config.Script)); & ([ScriptBlock]::Create($body)) -SelfTest:([bool]$config.SelfTest) -ApplicationPath $config.ApplicationPath -RequestJson $requestJson } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var psi = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        psi.Environment["PSModulePath"]=Path.Combine(Path.GetDirectoryName(PowerShell)!,"Modules");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        string applicationPath = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? Path.Combine(AppContext.BaseDirectory, "Startwerk.dll") : Environment.ProcessPath!;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Script = Convert.ToBase64String(Encoding.UTF8.GetBytes(script)), SelfTest = selfTest, ApplicationPath = applicationPath }));
        if (!selfTest) await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync();
        while (await process.StandardOutput.ReadLineAsync() is { } line) log(line);
        await process.WaitForExitAsync(); string error = await errors;
        if (process.ExitCode != 0 && !(allowCancellation && process.ExitCode == 2))
            throw new IOException(string.IsNullOrWhiteSpace(error) ? "USB-Vorgang fehlgeschlagen. Siehe Protokoll." : error.Trim());
        return process.ExitCode;
    }
}
