using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Beam.Core;

public enum Step { Cloudflared, Hosts, Server, Tunnel }

public enum StepState { Pending, Active, Done, Failed }

public sealed class StepUpdate(Step step, StepState state, string detail)
{
    public Step Step { get; } = step;
    public StepState State { get; } = state;
    public string Detail { get; } = detail;
}

public sealed partial class TunnelSession : IDisposable
{
    static readonly HashSet<int> Claimed = [];

    readonly List<Process> _processes = [];
    readonly Queue<string> _tail = new();
    Process? _server;
    Process? _tunnel;

    public event Action<string>? Log;
    public event Action<StepUpdate>? StepChanged;
    public event Action<string>? Ended;

    public int Port { get; private set; }
    public string? PublicUrl { get; private set; }
    public bool Stopping { get; private set; }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelUrl();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    public async Task<string> StartAsync(AngularInfo info, CancellationToken ct)
    {
        var loc = Loc.I;

        Report(Step.Cloudflared, StepState.Active, "");
        var download = new Progress<int>(p => Report(Step.Cloudflared, StepState.Active, loc.F("Downloading", p)));
        var cloudflared = await Cloudflared.EnsureAsync(download, ct);
        Write($"cloudflared: {cloudflared}");
        Report(Step.Cloudflared, StepState.Done, loc["Ready"]);

        Report(Step.Hosts, StepState.Active, "");
        var patched = AngularProject.AllowTunnelHosts(info.Root);
        Report(Step.Hosts, StepState.Done, patched ? loc["Patched"] : loc["AlreadyOk"]);

        if (!Directory.Exists(Path.Combine(info.Root, "node_modules")))
        {
            Report(Step.Server, StepState.Active, loc["Installing"]);
            var install = Spawn("cmd.exe", "/d /s /c \"npm install\"", info.Root);
            await install.WaitForExitAsync(ct);
            if (install.ExitCode != 0)
                throw new InvalidOperationException(loc["NpmFailed"]);
        }

        Port = ClaimPort(info.Port);
        Report(Step.Server, StepState.Active, loc.F("Building", Port));
        var localCli = Path.Combine(info.Root, "node_modules", ".bin", "ng.cmd");
        var cli = File.Exists(localCli) ? $"\"{localCli}\"" : "npx --yes ng";
        _server = Spawn("cmd.exe", $"/d /s /c \"{cli} serve --port {Port}\"", info.Root);
        await WaitForServerAsync(Port, ct);
        Report(Step.Server, StepState.Done, $"localhost:{Port}");

        Report(Step.Tunnel, StepState.Active, loc["Connecting"]);
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tunnel = Spawn(cloudflared, $"tunnel --no-autoupdate --url http://localhost:{Port}", info.Root, line =>
        {
            var match = TunnelUrl().Match(line);
            if (match.Success && !match.Value.Contains("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                found.TrySetResult(match.Value);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            PublicUrl = await found.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(loc["TunnelTimeout"]);
        }

        Report(Step.Tunnel, StepState.Done, loc["Live"]);
        WatchForExit(_server);
        WatchForExit(_tunnel);
        return PublicUrl;
    }

    static int ClaimPort(int preferred)
    {
        lock (Claimed)
        {
            var start = preferred;
            while (true)
            {
                var port = AngularProject.FreePort(start);
                if (Claimed.Add(port))
                    return port;
                start = port + 1;
            }
        }
    }

    async Task WaitForServerAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_server is { HasExited: true })
                throw new InvalidOperationException(Loc.I["ServerExited"] + Environment.NewLine + string.Join(Environment.NewLine, _tail));

            if (await IsListeningAsync(port))
                return;

            await Task.Delay(800, ct);
        }
        throw new TimeoutException(Loc.I["ServerTimeout"]);
    }

    static async Task<bool> IsListeningAsync(int port)
    {
        foreach (var host in new[] { "127.0.0.1", "::1" })
        {
            try
            {
                using var client = new TcpClient(host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                using var cts = new CancellationTokenSource(600);
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch
            {
            }
        }
        return false;
    }

    void WatchForExit(Process? process)
    {
        if (process is null)
            return;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (!Stopping)
                Ended?.Invoke(Loc.I["ServerExited"]);
        };
    }

    Process Spawn(string file, string args, string workDir, Action<string>? onLine = null)
    {
        var info = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["NG_CLI_ANALYTICS"] = "false";
        info.Environment["NO_COLOR"] = "1";
        info.Environment["FORCE_COLOR"] = "0";

        var process = new Process { StartInfo = info };
        DataReceivedEventHandler handler = (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            var line = Ansi().Replace(e.Data, "").TrimEnd();
            Write(line);
            onLine?.Invoke(line);
        };
        process.OutputDataReceived += handler;
        process.ErrorDataReceived += handler;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_processes)
            _processes.Add(process);
        Write($"> {Path.GetFileName(file)} {args}");
        return process;
    }

    void Write(string line)
    {
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 12)
                _tail.Dequeue();
        }
        Log?.Invoke(line);
    }

    void Report(Step step, StepState state, string detail) => StepChanged?.Invoke(new StepUpdate(step, state, detail));

    public void Stop()
    {
        Stopping = true;
        List<Process> all;
        lock (_processes)
            all = [.. _processes];

        foreach (var process in all)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_processes)
        {
            foreach (var process in _processes)
                process.Dispose();
            _processes.Clear();
        }
        if (Port > 0)
        {
            lock (Claimed)
                Claimed.Remove(Port);
        }
    }
}
