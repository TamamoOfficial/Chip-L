using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChipL.Agent;

/// <summary>一条 agent 指令（GET 查询参数或 POST JSON 扁平化）。</summary>
public sealed class AgentRequest
{
    public string Path { get; init; } = "/";
    public string Cmd { get; init; } = "";
    public Dictionary<string, string> Args { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key, string fallback = null)
        => Args.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var v) ? v : fallback;
}

/// <summary>执行结果：Data 给 agent，Ui 给用户看（会显示在窗口顶部状态条）。</summary>
public sealed class AgentResult
{
    public bool Ok { get; init; } = true;
    public string Ui { get; init; } = "";
    public object Data { get; init; }
    public string Error { get; init; }

    public static AgentResult Good(object data, string ui) => new() { Data = data, Ui = ui };
    public static AgentResult Fail(string error) => new() { Ok = false, Error = error, Ui = "✕ " + error };
}

/// <summary>
/// 本地调试接口：只监听 127.0.0.1 的随机端口，需 token 鉴权。
/// 用裸 TcpListener 实现极简 HTTP/1.1，避免 HttpListener 的 URL ACL 需要管理员权限。
/// 启动后把 port/token 写入 %LOCALAPPDATA%\ChipL\agent.json 供外部发现。
/// </summary>
public sealed class AgentServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<AgentRequest, AgentResult> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public int Port { get; }
    public string Token { get; }
    public string EndpointFile { get; }

    public AgentServer(Func<AgentRequest, AgentResult> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        for (int attempt = 0; ; attempt++)
        {
            try { _listener.Start(); break; }
            catch (SocketException) when (attempt < 5)
            {
                Thread.Sleep(120);
            }
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChipL");
        Directory.CreateDirectory(dir);
        EndpointFile = Path.Combine(dir, "agent.json");

        // 进程被强杀时 Closed 不会触发，旧文件会残留；启动时先清掉
        try { if (File.Exists(EndpointFile)) File.Delete(EndpointFile); } catch { }

        File.WriteAllText(EndpointFile, JsonSerializer.Serialize(new
        {
            url = $"http://127.0.0.1:{Port}/",
            token = Token,
            pid = Environment.ProcessId,
            startedAt = DateTime.Now.ToString("s"),
            hint = "请求需带 &token=…；进程异常退出时本文件可能残留，以 pid 是否存活为准"
        }, new JsonSerializerOptions { WriteIndented = true }));

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            try
            {
                var (method, path, query, body) = await ReadRequestAsync(stream);
                if (path == null) return;

                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in ParseQuery(query)) args[kv.Key] = kv.Value;
                if (!string.IsNullOrWhiteSpace(body)) ParseJsonBody(body, args);

                if (!args.TryGetValue("token", out var token) || token != Token)
                {
                    await WriteJsonAsync(stream, 401, new { ok = false, error = "token 无效" });
                    return;
                }

                var cmd = args.TryGetValue("cmd", out var c) && !string.IsNullOrEmpty(c)
                    ? c
                    : path.Trim('/').Split('/')[0];

                AgentResult result;
                try
                {
                    result = _handler(new AgentRequest { Path = path, Cmd = cmd, Args = args });
                }
                catch (Exception ex)
                {
                    result = AgentResult.Fail(ex.GetType().Name + ": " + ex.Message);
                }

                await WriteJsonAsync(stream, result.Ok ? 200 : 400, new
                {
                    ok = result.Ok,
                    ui = result.Ui,
                    data = result.Data,
                    error = result.Error
                });
            }
            catch
            {
                // 单个请求失败不影响服务
            }
        }
    }

    private static async Task<(string method, string path, string query, string body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        int headerEnd = -1;
        int total = 0;

        // 读头
        while (total < 64 * 1024)
        {
            int n = await stream.ReadAsync(buffer);
            if (n <= 0) return (null, null, null, null);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            total += n;
            var text = sb.ToString();
            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return (null, null, null, null);

        var head = sb.ToString()[..headerEnd];
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return (null, null, null, null);

        string method = first[0];
        string target = first[1];
        string path = target;
        string query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }

        int contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
        }

        var body = "";
        if (contentLength > 0)
        {
            var alreadyRead = sb.ToString()[(headerEnd + 4)..];
            var bodyBytes = new List<byte>(alreadyRead.Length + contentLength);
            bodyBytes.AddRange(Encoding.UTF8.GetBytes(alreadyRead));
            while (bodyBytes.Count < contentLength)
            {
                int n = await stream.ReadAsync(buffer);
                if (n <= 0) break;
                bodyBytes.AddRange(buffer.Take(n));
            }
            body = Encoding.UTF8.GetString(bodyBytes.Take(contentLength).ToArray());
        }

        return (method, path, query, body);
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            yield return new KeyValuePair<string, string>(
                Uri.UnescapeDataString(part[..eq]), Uri.UnescapeDataString(part[(eq + 1)..]));
        }
    }

    private static void ParseJsonBody(string body, Dictionary<string, string> args)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            args[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.Value.GetRawText()
            };
        }
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {bodyBytes.Length}\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { if (File.Exists(EndpointFile)) File.Delete(EndpointFile); } catch { }
    }
}
