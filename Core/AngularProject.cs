using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beam.Core;

public sealed record AngularInfo(string Root, string Name, string Version, int Port, bool HostsReady, bool HasNodeModules);

public static class AngularProject
{
    public const string TunnelHost = ".trycloudflare.com";

    static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AngularInfo? Inspect(string root)
    {
        var file = Path.Combine(root, "angular.json");
        if (!File.Exists(file))
            return null;

        var doc = JsonNode.Parse(File.ReadAllText(file), documentOptions: ReadOptions);
        var (name, serve) = FindServe(doc);
        var options = serve?["options"] as JsonObject;

        return new AngularInfo(
            root,
            name ?? Path.GetFileName(root),
            ReadVersion(root),
            ReadPort(options) ?? 4200,
            HostsAllowed(options),
            Directory.Exists(Path.Combine(root, "node_modules")));
    }

    public static bool AllowTunnelHosts(string root)
    {
        var file = Path.Combine(root, "angular.json");
        var doc = JsonNode.Parse(File.ReadAllText(file), documentOptions: ReadOptions);
        var (_, serve) = FindServe(doc);
        if (serve is null)
            throw new InvalidOperationException("angular.json has no serve target");

        if (serve["options"] is not JsonObject options)
        {
            options = new JsonObject();
            serve["options"] = options;
        }

        if (HostsAllowed(options))
            return false;

        if (options["allowedHosts"] is JsonArray hosts)
            hosts.Add(TunnelHost);
        else
            options["allowedHosts"] = new JsonArray(TunnelHost);

        var original = Path.Combine(
            Path.GetDirectoryName(file)!,
            Path.GetFileNameWithoutExtension(file) + "_original" + Path.GetExtension(file));
        if (!File.Exists(original))
            File.Move(file, original);

        File.WriteAllText(file, doc!.ToJsonString(WriteOptions) + Environment.NewLine);
        return true;
    }

    public static int FreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 100; port++)
        {
            if (IsFree(port))
                return port;
        }
        return preferred;
    }

    static bool IsFree(int port)
    {
        if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == port))
            return false;

        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Any, IPAddress.IPv6Any })
        {
            try
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                listener.Stop();
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }

    static (string? Name, JsonObject? Serve) FindServe(JsonNode? doc)
    {
        if (doc?["projects"] is not JsonObject projects)
            return (null, null);

        var preferred = doc["defaultProject"]?.GetValue<string>();
        if (preferred is not null && projects[preferred]?["architect"]?["serve"] is JsonObject chosen)
            return (preferred, chosen);

        foreach (var (key, project) in projects)
        {
            if (project?["projectType"]?.GetValue<string>() == "application" && project["architect"]?["serve"] is JsonObject serve)
                return (key, serve);
        }

        foreach (var (key, project) in projects)
        {
            if (project?["architect"]?["serve"] is JsonObject serve)
                return (key, serve);
        }

        return (null, null);
    }

    static bool HostsAllowed(JsonObject? options)
    {
        var node = options?["allowedHosts"];
        if (node is JsonValue value && value.TryGetValue<bool>(out var all))
            return all;
        if (node is JsonArray hosts)
        {
            return hosts.Any(h =>
            {
                var host = h?.GetValue<string>() ?? "";
                return host == "all" || host == TunnelHost || host == "trycloudflare.com";
            });
        }
        return false;
    }

    static int? ReadPort(JsonObject? options)
    {
        var node = options?["port"];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var port))
                return port;
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out port))
                return port;
        }
        return null;
    }

    static string ReadVersion(string root)
    {
        try
        {
            var package = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "package.json")), documentOptions: ReadOptions);
            var version = package?["dependencies"]?["@angular/core"]?.GetValue<string>();
            return version?.TrimStart('^', '~', '=', ' ') ?? "";
        }
        catch
        {
            return "";
        }
    }
}
