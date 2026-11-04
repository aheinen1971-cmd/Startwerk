using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Startwerk;
public sealed record MicrosoftOption(string Value,string Label);
public sealed record MicrosoftPageData(string[] Links,string[] Rows,string? Error=null);
// Drives the public form shown on Microsoft's page. No private API, stored link or remote script.
public sealed class MicrosoftWizard(Func<string,Task<string>> execute,Func<string> currentUrl)
{
    public static string Page(bool arm64)=>IsoDownloads.MicrosoftPage+(arm64?"arm64":"");
    async Task<T?> Evaluate<T>(string script)
    {
        if(!Uri.TryCreate(currentUrl(),UriKind.Absolute,out var uri)||!WebsiteSafety.MicrosoftPage(uri))throw new IOException("Der Assistent wartet auf die freigegebene Microsoft-Downloadseite.");
        return JsonSerializer.Deserialize<T>(await execute(script));
    }
    public async Task<MicrosoftOption[]> Options(string id,CancellationToken token)
    {
        if(id is not "product-edition" and not "product-languages")throw new ArgumentException(nameof(id));
        var until=DateTime.UtcNow.AddSeconds(45);
        while(DateTime.UtcNow<until)
        {
            token.ThrowIfCancellationRequested();
            var items=await Evaluate<MicrosoftOption[]>($"Array.from(document.getElementById({JsonSerializer.Serialize(id)})?.options || []).filter(o=>o.value && o.value!=='null').map(o=>({{Value:o.value,Label:o.textContent.trim()}}))");
            if(items?.Length>0)return items;
            await Task.Delay(250,token);
        }
        throw new IOException("Microsoft hat keine Auswahl geliefert. Bitte die Microsoft-Seite als Alternative verwenden oder später erneut versuchen.");
    }
    public async Task Submit(string id,string button,MicrosoftOption option,CancellationToken token)
    {
        if((id,button) is not ("product-edition","submit-product-edition") and not ("product-languages","submit-sku"))throw new ArgumentException(nameof(id));
        token.ThrowIfCancellationRequested();
        bool ok=await Evaluate<bool>($"(()=>{{const s=document.getElementById({JsonSerializer.Serialize(id)}),b=document.getElementById({JsonSerializer.Serialize(button)});const value={JsonSerializer.Serialize(option.Value)};if(!s||!b||b.disabled||!Array.from(s.options).some(o=>o.value===value))return false;s.value=value;s.dispatchEvent(new Event('change',{{bubbles:true}}));b.click();return true;}})()");
        if(!ok)throw new IOException("Die Microsoft-Auswahl hat sich geändert. Bitte den Assistenten neu laden oder die Microsoft-Seite öffnen.");
    }
    public static IsoSource Source(MicrosoftPageData data,string language)
    {
        language=Regex.Replace(language,@"\s+"," ").Trim();
        // Microsoft's German form names US English differently from its checksum table.
        if(language=="Englisch (Vereinigte Staaten)")language="Englisch";
        var links=data.Links.Distinct().Where(x=>Uri.TryCreate(x,UriKind.Absolute,out var u)&&IsoDownloads.TrustedUri("Windows",u)&&u.AbsolutePath.EndsWith(".iso",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(links.Length!=1)throw new IOException("Microsoft hat keinen eindeutigen offiziellen ISO-Link geliefert.");
        var rows=data.Rows.Where(x=>Regex.Replace(x,@"\s+"," ").Trim().StartsWith(language+" 64-bit ",StringComparison.OrdinalIgnoreCase)).ToArray();
        var hashes=rows.Select(x=>Regex.Match(x,@"\b[a-fA-F0-9]{64}\b").Value).Where(x=>x.Length==64).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(hashes.Length!=1)throw new IOException("Die offizielle Microsoft-Prüfsumme für diese Sprache fehlt. Bitte die Microsoft-Seite als Alternative verwenden.");
        return IsoDownloads.MicrosoftSource(links[0],hashes[0]);
    }
    public async Task<string[]> CurrentLinks(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();return await Evaluate<string[]>("Array.from(document.querySelectorAll('a[href]')).map(a=>a.href)")??[];
    }
    public async Task<IsoSource> Link(string language,CancellationToken token,IReadOnlySet<string>? previousLinks=null)
    {
        var until=DateTime.UtcNow.AddSeconds(60);
        string reason="";
        while(DateTime.UtcNow<until)
        {
            token.ThrowIfCancellationRequested();
            var data=await Evaluate<MicrosoftPageData>("({Links:Array.from(document.querySelectorAll('a[href]')).map(a=>a.href),Rows:Array.from(document.querySelectorAll('tr')).map(r=>Array.from(r.cells).map(c=>c.textContent.trim()).join(' ')),Error:Array.from(document.querySelectorAll('#errorModalMessage,#product-edition-error,#product-languages-error')).filter(e=>e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden').map(e=>e.textContent.trim()).filter(Boolean).join(' ')})");
            if(data!=null)
            {
                if(!string.IsNullOrWhiteSpace(data.Error))throw new IOException("Microsoft meldet: "+data.Error[..Math.Min(data.Error.Length,600)]);
                if(previousLinks!=null)data=data with{Links=data.Links.Where(x=>!previousLinks.Contains(x)).ToArray()};
                try{return Source(data,language);}catch(IOException ex){reason=ex.Message;}
            }
            await Task.Delay(300,token);
        }
        throw new IOException("Microsoft hat keinen frischen ISO-Link mit passender Prüfsumme geliefert. "+reason+" Die Microsoft-Seite bleibt als Alternative verfügbar.");
    }
}
