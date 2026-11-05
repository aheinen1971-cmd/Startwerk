using System.IO;
using SharpCompress.Compressors.Xz;

namespace Startwerk;

public sealed class CompressedImage : IDisposable
{
    readonly FileStream source;
    public string Path { get; }

    CompressedImage(string path, FileStream source)
    {
        Path = path;
        this.source = source;
    }

    public static async Task<CompressedImage> Expand(string path)
    {
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Startwerk-{Guid.NewGuid():N}.img");
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        bool created = false;
        try
        {
            long free = new DriveInfo(System.IO.Path.GetPathRoot(temp)!).AvailableFreeSpace;
            long limit = Math.Min(64L * 1024 * 1024 * 1024, free - 256L * 1024 * 1024);
            if (limit <= 0) throw new IOException("Zu wenig Platz zum Entpacken des Abbilds.");
            var checks = XzChecks.Read(source, limit);
            var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            created = true;
            await using (output)
            using (var decoder = new XZStream(source))
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;
                int count;
                while ((count = await decoder.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
                {
                    written += count;
                    if (written > checks.Size) throw new IOException("Das entpackte Abbild ist größer als im XZ-Index angegeben.");
                    await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                }
                if (decoder.Footer is null || source.Position != checks.StreamEnd)
                    throw new IOException("Das XZ-Abbild hat keinen vollständigen Abschluss.");
                await output.FlushAsync().ConfigureAwait(false);
                checks.Verify(source, output, buffer);
            }
            return new CompressedImage(temp, source);
        }
        catch (Exception ex)
        {
            source.Dispose();
            if (created) TryDelete(temp);
            if (ex.GetType().Assembly == typeof(XZStream).Assembly)
                throw new IOException("Das XZ-Abbild ist beschädigt oder verwendet ein unbekanntes Format.", ex);
            throw;
        }
    }

    public void Dispose()
    {
        source.Dispose();
        TryDelete(Path);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
