using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;

namespace Leo.Service.Health;

/// <summary>What the probe needs to know about one adapter.</summary>
public sealed record ProbeTargetAdapter(string Id, int InterfaceIndex, IPAddress LocalAddress, IReadOnlyList<IPAddress> DnsServers);

/// <summary>
/// One internet check through one specific adapter (LEO.md §5.7): DNS via that network, then an HTTP GET
/// on a socket pinned to it. First target that answers decides; a redirect or unexpected body = captive portal.
/// </summary>
public static class InterfaceProbe
{
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);

    public static async Task<HealthReport> RunAsync(ProbeTargetAdapter adapter, IReadOnlyList<ProbeTarget> targets,
        int slowLatencyMs, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var target in targets)
        {
            if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri)) continue;
            try
            {
                var (status, body, latencyMs, redirected) = await GetAsync(adapter, uri, ct);
                var expect204 = target.Expect == "204";
                var ok = expect204
                    ? status == HttpStatusCode.NoContent
                    : status == HttpStatusCode.OK && body.Trim() == target.Expect;

                if (ok)
                {
                    var state = latencyMs > slowLatencyMs ? HealthState.Degraded : HealthState.Up;
                    return new HealthReport(state, latencyMs, DateTimeOffset.Now, uri.Host);
                }

                // An answer, but not the one we expect: someone is intercepting HTTP (sign-in page).
                if (redirected || (int)status is >= 200 and < 400)
                {
                    return new HealthReport(HealthState.Captive, latencyMs, DateTimeOffset.Now,
                        $"{uri.Host} answered {(int)status}{(redirected ? " (redirect)" : "")}");
                }
                lastError = $"{uri.Host}: HTTP {(int)status}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsAddressGone(ex))
            {
                // The adapter's IP changed under us (switching networks, DHCP renew). That's not "no internet":
                // report Unknown, which never triggers failover, and let the next scan pick up the new address.
                return new HealthReport(HealthState.Unknown, null, DateTimeOffset.Now, "Network address is changing");
            }
            catch (Exception ex)
            {
                lastError = $"{uri.Host}: {Flatten(ex)}";
            }
        }
        return new HealthReport(HealthState.NoInternet, null, DateTimeOffset.Now, lastError);
    }

    private static async Task<(HttpStatusCode Status, string Body, int LatencyMs, bool Redirected)> GetAsync(
        ProbeTargetAdapter adapter, Uri uri, CancellationToken ct)
    {
        var addresses = await InterfaceDns.ResolveAsync(uri.Host, adapter.LocalAddress, adapter.InterfaceIndex,
            adapter.DnsServers, DnsTimeout, ct);

        var connectMs = -1;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                var socket = BoundSockets.Create(SocketType.Stream, ProtocolType.Tcp, adapter.LocalAddress, adapter.InterfaceIndex);
                try
                {
                    var sw = Stopwatch.StartNew();
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                    connectMs = (int)sw.ElapsedMilliseconds;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = HttpTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LEO-HealthProbe/1.0");
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        var redirected = (int)response.StatusCode is >= 300 and < 400;
        var body = response.Content.Headers.ContentLength is > 4096 ? "" : await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body, connectMs, redirected);
    }

    private static bool IsAddressGone(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressNotAvailable }) return true;
            if (e.Message.Contains("not valid in its context", StringComparison.OrdinalIgnoreCase)) return true; // WSAEADDRNOTAVAIL text
        }
        return false;
    }

    private static string Flatten(Exception ex)
    {
        while (ex.InnerException is not null && ex is HttpRequestException or AggregateException) ex = ex.InnerException;
        return ex.Message;
    }
}
