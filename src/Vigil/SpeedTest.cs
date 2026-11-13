using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Vigil;

/// <summary>
/// Network speed test against Cloudflare's public endpoints (no account, no key). Second and last
/// network exception after the updater: runs only when the user clicks it, and can be cancelled.
/// </summary>
static class SpeedTest
{
    const string Base = "https://speed.cloudflare.com";

    /// <summary>Transfer sizes tried in order. A phase stops early once the next size wouldn't fit its time budget.</summary>
    public record Plan(int Pings, long[] Down, long[] Up, double PhaseSeconds)
    {
        public long MaxBytes => Down.Sum() + Up.Sum();
    }

    // ~67 MB worst case on a fast line; a slow line stops after the small sizes and uses far less.
    public static readonly Plan Full = new(8,
        new[] { 100_000L, 1_000_000L, 10_000_000L, 25_000_000L },
        new[] { 100_000L, 1_000_000L, 10_000_000L, 20_000_000L }, 6);

    public record Result(double PingMs, double DownMbps, double UpMbps);

    /// <summary>Progress: phase name ("Ping", "Download", "Upload") and the value so far (ms or Mbps).</summary>
    public static async Task<Result> RunAsync(Plan plan, IProgress<(string Phase, double Value)>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Vigil/{Updater.Current}");

        // Latency: time to response headers of empty downloads. The first one pays for DNS + TLS, so it's dropped.
        var pings = new List<double>();
        for (int i = 0; i <= plan.Pings; i++)
        {
            var sw = Stopwatch.StartNew();
            using (var r = await http.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                r.EnsureSuccessStatusCode();
            if (i == 0) continue;
            pings.Add(sw.Elapsed.TotalMilliseconds);
            progress?.Report(("Ping", Median(pings)));
        }
        double ping = Median(pings);

        double down = await Phase(plan.Down, plan.PhaseSeconds, "Download", n => Download(http, n, progress, ct), progress, ct).ConfigureAwait(false);
        double up = await Phase(plan.Up, plan.PhaseSeconds, "Upload", n => Upload(http, n, ct), progress, ct).ConfigureAwait(false);
        return new Result(ping, down, up);
    }

    /// <summary>Runs sizes smallest first; result is the best Mbps seen (small transfers include latency, so they only under-read).</summary>
    static async Task<double> Phase(long[] sizes, double budget, string name, Func<long, Task<double>> transfer,
                                    IProgress<(string, double)>? progress, CancellationToken ct)
    {
        double best = 0, spent = 0;
        foreach (long n in sizes)
        {
            // Skip a size that would blow the phase's time budget at the speed seen so far.
            if (best > 0 && spent + n * 8 / (best * 1e6) > budget) break;
            ct.ThrowIfCancellationRequested();
            double secs = await transfer(n).ConfigureAwait(false);
            spent += secs;
            best = Math.Max(best, n * 8 / secs / 1e6);
            progress?.Report((name, best));
        }
        return best;
    }

    static async Task<double> Download(HttpClient http, long bytes, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var r = await http.GetAsync($"{Base}/__down?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using var s = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[64 * 1024];
        long got = 0;
        double lastReport = 0;
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            got += n;
            double t = sw.Elapsed.TotalSeconds;
            if (t - lastReport > 0.25 && t > 0.5) // live speed during the big downloads
            {
                lastReport = t;
                progress?.Report(("Download", got * 8 / t / 1e6));
            }
        }
        return Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    }

    static async Task<double> Upload(HttpClient http, long bytes, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var body = new ZeroContent(bytes);
        using var r = await http.PostAsync($"{Base}/__up", body, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        return Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    }

    /// <summary>Upload body of N zero bytes streamed from a 64 KB buffer, so a 20 MB upload never holds 20 MB in RAM.</summary>
    sealed class ZeroContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
        {
            var buf = new byte[64 * 1024];
            for (long left = length; left > 0; left -= buf.Length)
                await stream.WriteAsync(buf.AsMemory(0, (int)Math.Min(left, buf.Length))).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long len) { len = length; return true; }
    }

    static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }
}
