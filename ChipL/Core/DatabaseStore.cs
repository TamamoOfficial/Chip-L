using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ChipL.Core;

public sealed class UpdateResult
{
    public bool Updated { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; } = "";
    public int NewVersion { get; set; }
    public int OldVersion { get; set; }

    /// <summary>更新前的数据库快照（仅 Updated=true 时有值），用于计算变更详情。</summary>
    public ChipDatabase OldDatabase { get; set; }

    /// <summary>刚落盘的新数据库。</summary>
    public ChipDatabase NewDatabase { get; set; }
}

/// <summary>
/// 数据库仓库：内置快照 → 本地热更新缓存 → 在线检查（与安卓版 /json/db_*.json + /db/* 协议一致）。
/// 缓存目录：%LOCALAPPDATA%\ChipL\db\，写入采用 .tmp + 原子替换，避免半包损坏。
/// 离线或服务端未更新时完全不影响启动。
/// </summary>
public sealed class DatabaseStore
{
    public const string DefaultServer = "https://www.ijrsoftware.com";

    /// <summary>
    /// 服务端根地址。可用环境变量 CHIPL_SERVER 指向镜像站做离线验证；
    /// 未设置时始终使用官方域名。
    /// </summary>
    public static string ServerBase { get; set; } =
        Environment.GetEnvironmentVariable("CHIPL_SERVER") is { Length: > 0 } custom
            ? custom : DefaultServer;

    public static string VersionApiUrl(string tag)
        => $"{ServerBase.TrimEnd('/')}/json/db_{tag}.json";

    public static string FallbackUrl(string tag)
        => $"{ServerBase.TrimEnd('/')}/db/{tag}";

    private readonly string _root;
    private static readonly HttpClient Http = CreateClient();

    public DatabaseStore()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChipL", "db");
        Directory.CreateDirectory(_root);
    }

    public string RootDirectory => _root;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ChipL-Desktop/1.0 (Windows)");
        return c;
    }

    private static string Tag(ChipModule m) => m switch
    {
        ChipModule.Cpu => "cpus",
        ChipModule.Gpu => "gpus",
        ChipModule.Soc => "socs",
        _ => "cpus"
    };

    private static string ResourceName(ChipModule m) => m switch
    {
        ChipModule.Cpu => "db.cpus.gz",
        ChipModule.Gpu => "db.gpus.gz",
        ChipModule.Soc => "db.socs.gz",
        _ => "db.cpus.gz"
    };

    private string CachePath(ChipModule m) => Path.Combine(_root, Tag(m) + ".json.gz");
    private string MetaPath(ChipModule m) => Path.Combine(_root, Tag(m) + ".meta.json");

    /// <summary>本地缓存 + 内置快照组合加载，永远返回可用数据。</summary>
    public ChipDatabase LoadLocal(ChipModule m)
    {
        var cache = CachePath(m);
        if (File.Exists(cache))
        {
            try
            {
                using var fs = File.OpenRead(cache);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);
                var meta = ReadMeta(m);
                return ChipDatabase.Parse(m, reader.ReadToEnd(), "本地热更新缓存",
                    meta?.Version.ToString() ?? "?");
            }
            catch
            {
                try { File.Delete(cache); } catch { }
            }
        }
        return LoadEmbedded(m);
    }

    public ChipDatabase LoadEmbedded(ChipModule m)
    {
        var asm = typeof(DatabaseStore).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName(m))
            ?? throw new InvalidOperationException("内置数据库缺失：" + ResourceName(m));
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);
        return ChipDatabase.Parse(m, reader.ReadToEnd(), "内置数据库", EmbeddedVersion(m));
    }

    /// <summary>
    /// 内置快照对应的服务端版本号。
    /// CPU 用线上 v14（比 APK 内置的旧库多出 Xeon 6+ Series、EPYC 9006 Series 等 23 个系列）。
    /// </summary>
    private static string EmbeddedVersion(ChipModule m) => m switch
    {
        ChipModule.Cpu => "14",   // 内置快照 = 线上 v14（含 Xeon 6+ / EPYC 9006）
        ChipModule.Gpu => "6",
        ChipModule.Soc => "10",
        _ => "?"
    };

    private DbInfo ReadMeta(ChipModule m)
    {
        try
        {
            var p = MetaPath(m);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<DbInfo>(File.ReadAllText(p));
        }
        catch { return null; }
    }

    public int LocalVersion(ChipModule m)
    {
        var meta = ReadMeta(m);
        if (meta != null) return meta.Version;
        return int.TryParse(EmbeddedVersion(m), out var v) ? v : 0;
    }

    public DateTime? LocalRelease(ChipModule m)
    {
        var cache = CachePath(m);
        return File.Exists(cache) ? File.GetLastWriteTime(cache) : null;
    }

    /// <summary>查询服务端最新版本（失败返回 null，不抛异常）。</summary>
    public async Task<DbInfo> FetchRemoteInfoAsync(ChipModule m, CancellationToken ct = default)
    {
        try
        {
            var url = VersionApiUrl(Tag(m));
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<DbInfo>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (info == null) return null;
            if (string.IsNullOrWhiteSpace(info.Url))
                info.Url = FallbackUrl(Tag(m));
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查并下载更新。就地校验：解密 → 解压 → JSON 解析成功且条目数合理才落盘，
    /// 任何一步失败都保留旧数据（与安卓版「更新失败不影响使用」一致）。
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(
        ChipModule m,
        IProgress<(int percent, string status)> progress = null,
        CancellationToken ct = default)
    {
        var result = new UpdateResult();
        DbInfo info;
        try
        {
            info = await FetchRemoteInfoAsync(m, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Message = "检查更新失败：" + ex.Message;
            return result;
        }

        if (info == null)
        {
            result.Message = "无法连接更新服务器";
            return result;
        }

        int local = LocalVersion(m);
        result.OldVersion = local;
        if (info.Version <= local)
        {
            result.Skipped = true;
            result.NewVersion = info.Version;
            result.Message = $"已是最新（版本 {local}）";
            return result;
        }

        progress?.Report((2, $"发现新版本 {info.Version}，开始下载…"));

        byte[] payload;
        try
        {
            payload = await DownloadAsync(info.Url, info.Size, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Message = "下载失败：" + ex.Message;
            return result;
        }

        progress?.Report((85, "校验并解析数据…"));

        string json;
        string zipName;
        try
        {
            json = DbCrypto.Unpack(m, payload, out zipName);
        }
        catch (Exception ex)
        {
            result.Message = "解密校验失败（数据可能被截断）：" + ex.Message;
            return result;
        }

        ChipDatabase parsed;
        try
        {
            parsed = ChipDatabase.Parse(m, json, "在线更新", info.Version.ToString());
        }
        catch (Exception ex)
        {
            result.Message = "数据解析失败：" + ex.Message;
            return result;
        }

        if (parsed.Records.Count < 10)
        {
            result.Message = $"数据异常（仅 {parsed.Records.Count} 条），已放弃本次更新";
            return result;
        }

        progress?.Report((94, $"写入本地缓存（{parsed.Records.Count} 条）…"));
        // 保留更新前的快照，供界面计算「本次更新了什么」
        try { result.OldDatabase = LoadLocal(m); } catch { }
        WriteAtomic(m, json, info);
        result.NewDatabase = parsed;
        result.Updated = true;
        result.NewVersion = info.Version;
        result.Message = $"已更新到版本 {info.Version}（{parsed.Records.Count} 条记录）";
        progress?.Report((100, result.Message));
        return result;
    }

    private static async Task<byte[]> DownloadAsync(
        string url, int expectedSize, IProgress<(int, string)> progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream(expectedSize > 0 ? expectedSize : 1 << 20);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        int lastPct = -1;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, n);
            read += n;
            if (total > 0)
            {
                int pct = (int)(5 + read * 75 / total);
                if (pct != lastPct) { lastPct = pct; progress?.Report((pct, $"下载中 {read * 100 / total}%")); }
            }
        }
        return ms.ToArray();
    }

    private void WriteAtomic(ChipModule m, string json, DbInfo info)
    {
        var cache = CachePath(m);
        var tmp = cache + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
        using (var w = new StreamWriter(gz, new System.Text.UTF8Encoding(false)))
        {
            w.Write(json);
        }

        // 再读回验证一次，确认落盘内容完整
        using (var fs = File.OpenRead(tmp))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var r = new StreamReader(gz, System.Text.Encoding.UTF8))
        {
            var check = r.ReadToEnd();
            if (check.Length != json.Length) throw new InvalidDataException("写入校验失败");
        }

        var meta = new
        {
            info.Release,
            info.Version,
            info.Size,
            info.Url,
            UpdatedAt = DateTime.Now.ToString("s")
        };
        var metaTmp = MetaPath(m) + ".tmp";
        File.WriteAllText(metaTmp, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        if (File.Exists(cache)) File.Replace(tmp, cache, null);
        else File.Move(tmp, cache);
        if (File.Exists(MetaPath(m))) File.Replace(metaTmp, MetaPath(m), null);
        else File.Move(metaTmp, MetaPath(m));
    }

    /// <summary>清除本机热更新缓存，回到内置数据。</summary>
    public void ClearCache(ChipModule m)
    {
        try { if (File.Exists(CachePath(m))) File.Delete(CachePath(m)); } catch { }
        try { if (File.Exists(MetaPath(m))) File.Delete(MetaPath(m)); } catch { }
    }
}
