using System.IO;

namespace Startwerk;
public static class WebsiteMonitor
{
    public static string ReportPath=>Path.Combine(Updates.StateDirectory,"websites.json");
    static readonly object Sync=new();
    static readonly CancellationTokenSource Stop=new();
    static Task<WebsiteReport>? current;
    static DateTime lastAttempt;
    public static bool IsDue()=>WebsiteSafety.Due(WebsiteSafety.Load(ReportPath),DateTime.UtcNow)&&DateTime.UtcNow-lastAttempt>=WebsiteSafety.Interval;
    public static Task<WebsiteReport> Refresh(bool force=false)
    {
        lock(Sync)
        {
            if(current is {IsCompleted:false})return current;
            var cached=WebsiteSafety.Load(ReportPath);
            if(!force&&!WebsiteSafety.Due(cached,DateTime.UtcNow))return Task.FromResult(cached!);
            if(!force&&current is {IsCompletedSuccessfully:true}&&!WebsiteSafety.Due(current.Result,DateTime.UtcNow))
            {
                WebsiteSafety.Save(ReportPath,current.Result);return current;
            }
            lastAttempt=DateTime.UtcNow;return current=Run();
        }
    }
    static async Task<WebsiteReport> Run()
    {
        var report=await WebsiteSafety.CheckAll(Stop.Token);WebsiteSafety.Save(ReportPath,report);return report;
    }
    public static void Cancel()=>Stop.Cancel();
}
