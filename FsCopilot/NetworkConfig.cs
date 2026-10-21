namespace FsCopilot;

using System.Net;
using System.Text.Json;

/// <summary>
/// Optional local configuration for the patched build.
/// Read from "fscopilot.config.json" next to the executable (or passed on the command line).
///
/// {
///     "server": "p2p.fscopilot.com",  // rendezvous + relay host (IP or DNS name)
///     "direct": true,                 // true = no server at all; dial "ip[:port]" typed in the Join box
///     "directPort": 38510,            // UDP port used in direct mode (both sides listen on it)
///     "directBind": "0.0.0.0"         // keep 0.0.0.0 so the routing table picks the n2n adapter
/// }
///
/// Command line overrides: --server &lt;host&gt;  |  --direct  |  --direct-port &lt;port&gt;
/// </summary>
public sealed class NetworkConfig
{
    public const string DefaultServer = "p2p.fscopilot.com";
    public const int DefaultDirectPort = 38510;

    public string Server { get; init; } = DefaultServer;
    public bool Direct { get; init; }
    public int DirectPort { get; init; } = DefaultDirectPort;
    public string DirectBind { get; init; } = "0.0.0.0";

    public static NetworkConfig Load(string[] args)
    {
        var server = DefaultServer;
        var direct = false;
        var port = DefaultDirectPort;
        var bind = "0.0.0.0";

        var path = Path.Combine(AppContext.BaseDirectory, "fscopilot.config.json");
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String)
                        server = s.GetString() ?? server;

                    if (root.TryGetProperty("direct", out var d) &&
                        (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False))
                        direct = d.GetBoolean();

                    if (root.TryGetProperty("directPort", out var p) && p.TryGetInt32(out var pv) && pv is > 0 and <= 65535)
                        port = pv;

                    if (root.TryGetProperty("directBind", out var b) && b.ValueKind == JsonValueKind.String)
                        bind = b.GetString() ?? bind;
                }

                Log.Information("[Config] Loaded {Path}", path);
            }
            catch (Exception e)
            {
                Log.Warning(e, "[Config] Failed to read {Path}, using defaults", path);
            }
        }

        // Simple command line overrides
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--direct", StringComparison.OrdinalIgnoreCase))
            {
                direct = true;
            }
            else if (arg.StartsWith("--server=", StringComparison.OrdinalIgnoreCase))
            {
                server = arg["--server=".Length..];
            }
            else if (arg.Equals("--server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                server = args[++i];
            }
            else if (arg.StartsWith("--direct-port=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(arg["--direct-port=".Length..], out var dp) && dp is > 0 and <= 65535)
            {
                port = dp;
            }
        }

        return new NetworkConfig
        {
            Server = string.IsNullOrWhiteSpace(server) ? DefaultServer : server.Trim(),
            Direct = direct,
            DirectPort = port,
            DirectBind = string.IsNullOrWhiteSpace(bind) ? "0.0.0.0" : bind.Trim()
        };
    }

    /// <summary>Loopback-free IPv4 addresses of this machine, used as a hint in the UI.</summary>
    public static string LocalAddresses()
    {
        try
        {
            var list = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(x => !IPAddress.IsLoopback(x))
                .Select(x => x.ToString())
                .ToArray();

            return list.Length > 0 ? string.Join(", ", list) : "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
