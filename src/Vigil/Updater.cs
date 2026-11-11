using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Vigil;

record Release(Version Version, string Tag, string PageUrl, string AssetName, string AssetUrl, long Size, string? Sha256);

/// <summary>
/// "Check for updates": the only network call Vigil makes, and only when the user clicks it.
/// Reads the latest GitHub release, downloads the matching exe next to the running one, verifies
/// size + SHA-256, swaps the files (Windows allows renaming a running exe) and restarts.
/// </summary>
static class Updater
{
    const string Repo = "lohith-pras/vigil";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    static string ExePath => Environment.ProcessPath!;

    // ponytail: the standalone exe embeds the runtime (~69 MB) and the small one is ~0.3 MB,
    // so size tells them apart. Update to the same flavour that's running.
    static bool IsStandalone => new FileInfo(ExePath).Length > 20 * 1024 * 1024;

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Vigil/{Current}"); // GitHub API requires a User-Agent
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Latest published release, or null if there is none / it has no matching exe.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var http = NewClient();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        return Parse(doc.RootElement, IsStandalone ? "Vigil-standalone.exe" : "Vigil.exe");
    }

    internal static Release? Parse(JsonElement root, string assetName)
    {
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != assetName) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:")
                ? s["sha256:".Length..] : null;
            return new Release(version, tag, root.GetProperty("html_url").GetString() ?? ReleasesPage, assetName,
                a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    /// <summary>Download, verify, swap, start the new exe. Caller quits right after.</summary>
    public static async Task InstallAsync(Release r)
    {
        string exe = ExePath, fresh = exe + ".new", old = exe + ".old";
        TryDelete(fresh);

        using (var http = NewClient())
        await using (var src = await http.GetStreamAsync(r.AssetUrl))
        await using (var dst = File.Create(fresh))
            await src.CopyToAsync(dst);

        var info = new FileInfo(fresh);
        if (info.Length != r.Size)
        {
            TryDelete(fresh);
            throw new IOException($"Download incomplete ({info.Length} of {r.Size} bytes).");
        }
        if (r.Sha256 is { } expected)
        {
            await using var f = File.OpenRead(fresh);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                f.Close();
                TryDelete(fresh);
                throw new IOException("Downloaded file doesn't match the release checksum.");
            }
        }

        // Swap: running exe -> .old (allowed while running), new -> exe. Roll back if the second step fails.
        TryDelete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch { File.Move(old, exe); throw; }

        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
    }

    /// <summary>On start-up: remove leftovers from a previous update.</summary>
    public static void CleanUp()
    {
        TryDelete(ExePath + ".old"); // the previous version; it has exited by now
        TryDelete(ExePath + ".new"); // an interrupted download
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* still in use: next start */ }
    }

    public static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
}
