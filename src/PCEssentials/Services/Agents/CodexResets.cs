using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCEssentials.Core;

namespace PCEssentials.Services.Agents;

public sealed record CodexResetSummary(int Available, DateTime? NextExpiry, string? NextCreditId, DateTime CheckedAt);

/// Port of the Mac app's AgentCodexServer / AgentCodexResetService: Codex's banked rate-limit resets,
/// read and used through the official Codex app-server (JSON-RPC over stdio). Codex signs in with its
/// own account; PC Essentials never reads a token.
public sealed class CodexResetService : IDisposable
{
    public enum Outcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed, Failed }

    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);
    private readonly Timer _timer;
    private int _busy;
    /// A use that got no answer keeps its key, so trying again can never spend a second reset.
    private string? _pendingKey;

    public CodexResetSummary? Summary { get; private set; }
    /// "missing", "signIn", "outdated" or "failed" when the last check didn't work.
    public string? Failure { get; private set; }
    public bool Redeeming { get; private set; }
    public (Outcome Outcome, DateTime At)? Finished { get; private set; }
    public event Action? Changed;

    public CodexResetService() => _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(8), CheckEvery);

    public void RefreshIfStale()
    {
        if (Summary is null || DateTime.Now - Summary.CheckedAt > TimeSpan.FromMinutes(2))
            ThreadPool.QueueUserWorkItem(_ => Check());
    }

    private void Check()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var exe = FindCodex();
            if (exe is null) { Failure = "missing"; return; }
            using var rpc = Rpc.Start(exe);
            if (rpc is null) { Failure = "failed"; return; }
            var account = rpc.Ask("account/read", new JsonObject { ["refreshToken"] = false });
            if (account?["account"]?["type"]?.GetValue<string>() != "chatgpt") { Failure = account is null ? rpc.FailureKind : "signIn"; return; }
            var limits = rpc.Ask("account/rateLimits/read", new JsonObject());
            if (limits is null) { Failure = rpc.FailureKind; return; }
            Summary = Parse(limits);
            Failure = Summary is null ? "outdated" : null;
        }
        catch (Exception ex)
        {
            Failure = "failed";
            Log.Error("codex resets", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    /// Uses one reset (the next to expire), then reads the account again.
    public void Redeem()
    {
        if (Redeeming) return;
        Redeeming = true;
        Changed?.Invoke();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var outcome = Outcome.Failed;
            try
            {
                var exe = FindCodex();
                using var rpc = exe is null ? null : Rpc.Start(exe, TimeSpan.FromSeconds(30));
                if (rpc is not null)
                {
                    _pendingKey ??= Guid.NewGuid().ToString();
                    var args = new JsonObject { ["idempotencyKey"] = _pendingKey };
                    if (Summary?.NextCreditId is { } credit) args["creditId"] = credit;
                    var result = rpc.Ask("account/rateLimitResetCredit/consume", args);
                    if (result is not null)
                    {
                        _pendingKey = null;
                        outcome = result["outcome"]?.GetValue<string>() switch
                        {
                            "reset" => Outcome.Reset,
                            "nothingToReset" => Outcome.NothingToReset,
                            "noCredit" => Outcome.NoCredit,
                            "alreadyRedeemed" => Outcome.AlreadyRedeemed,
                            _ => Outcome.Failed,
                        };
                    }
                    if (rpc.Ask("account/rateLimits/read", new JsonObject()) is { } after) Summary = Parse(after) ?? Summary;
                }
                Log.Info($"codex reset used: {outcome}");
            }
            catch (Exception ex) { Log.Error("codex reset use", ex); }
            Redeeming = false;
            Finished = (outcome, DateTime.Now);
            Changed?.Invoke();
        });
    }

    private static CodexResetSummary? Parse(JsonNode result)
    {
        if (result["rateLimitResetCredits"] is not JsonObject credits) return null;
        var now = DateTime.Now;
        var listed = (credits["credits"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(c => (c["status"]?.GetValue<string>() ?? "available") == "available" && c["id"]?.GetValue<string>() is { Length: > 0 })
            .Select(c => (Id: c["id"]!.GetValue<string>(),
                          Expires: c["expiresAt"] is JsonValue v && v.TryGetValue<double>(out var s) ? DateTimeOffset.FromUnixTimeSeconds((long)s).LocalDateTime : (DateTime?)null))
            .Where(c => c.Expires is null || c.Expires > now)
            .OrderBy(c => c.Expires ?? DateTime.MaxValue)
            .ToList();
        var count = Math.Max(listed.Count, credits["availableCount"] is JsonValue n && n.TryGetValue<int>(out var a) ? a : 0);
        return new CodexResetSummary(count, listed.FirstOrDefault().Expires, listed.Count > 0 ? listed[0].Id : null, now);
    }

    public static string? FindCodex()
    {
        // The Codex desktop app keeps the CLI in a versioned folder; the newest wins.
        var appBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            if (Directory.Exists(appBin))
            {
                var bundled = Directory.GetFiles(appBin, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (bundled is not null) return bundled;
            }
        }
        catch (IOException) { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(exe)) return exe;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public void Dispose() => _timer.Dispose();

    /// One app-server conversation: initialize, the initialized notification, then questions by id.
    private sealed class Rpc : IDisposable
    {
        private readonly Process _process;
        private readonly BlockingCollection<string> _lines = new();
        private readonly TimeSpan _timeout;
        private int _lastId;
        public string FailureKind { get; private set; } = "failed";

        private Rpc(Process process, TimeSpan timeout)
        {
            _process = process;
            _timeout = timeout;
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) _lines.Add(line); };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public static Rpc? Start(string exe, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo(exe, "-c features.plugins=false app-server")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetTempPath(),
            };
            var p = Process.Start(psi);
            if (p is null) return null;
            var rpc = new Rpc(p, timeout ?? TimeSpan.FromSeconds(20));
            var client = new JsonObject { ["name"] = "pc-essentials", ["title"] = "PC Essentials", ["version"] = "0.1" };
            if (rpc.Ask("initialize", new JsonObject { ["clientInfo"] = client, ["capabilities"] = null }) is null) { rpc.Dispose(); return null; }
            rpc.Send(new JsonObject { ["method"] = "initialized" });
            return rpc;
        }

        private void Send(JsonObject message)
        {
            _process.StandardInput.WriteLine(message.ToJsonString());
            _process.StandardInput.Flush();
        }

        public JsonNode? Ask(string method, JsonObject args)
        {
            var id = ++_lastId;
            Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args });
            var deadline = DateTime.Now + _timeout;
            while (DateTime.Now < deadline)
            {
                if (!_lines.TryTake(out var line, TimeSpan.FromMilliseconds(250))) continue;
                JsonNode? message;
                try { message = JsonNode.Parse(line); } catch (JsonException) { continue; }
                // Notifications and the server's own requests are not the answer.
                if (message?["method"] is not null || message?["id"] is not JsonValue mid || !mid.TryGetValue<int>(out var got) || got != id) continue;
                if (message["result"] is JsonNode result) return result;
                var text = message["error"]?["message"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
                var code = message["error"]?["code"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0;
                FailureKind = code == -32601 || text.Contains("unknown variant") || text.Contains("method not found") ? "outdated"
                    : text.Contains("authentication") || text.Contains("not logged in") ? "signIn" : "failed";
                return null;
            }
            return null;
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); if (!_process.HasExited) _process.Kill(true); } catch { }
            _process.Dispose();
        }
    }
}
