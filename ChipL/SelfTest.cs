using System.IO;
using System.Text;
using ChipL.Core;

namespace ChipL;

/// <summary>无界面自检：验证内置库、缓存库解密解析与在线热更新全链路，结果写入 stdout。</summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        // WinExe 没有控制台句柄，重定向到文件时用 UTF-8 写出，避免中文乱码
        StreamWriter outw = null;
        try { outw = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }; } catch { }
        var log = new StringBuilder();
        void W(string line)
        {
            log.AppendLine(line);
            try { if (outw != null) outw.WriteLine(line); else Console.WriteLine(line); } catch { }
        }
        var store = new DatabaseStore();
        int failures = 0;
        W("== Chip-L 桌面版 自检 ==");
        W("缓存目录: " + store.RootDirectory);

        bool doUpdate = args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase));

        // --server <url> 可指向镜像站，用于离线验证热更新链路
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--server", StringComparison.OrdinalIgnoreCase))
            {
                DatabaseStore.ServerBase = args[i + 1];
                W("镜像服务器: " + DatabaseStore.ServerBase);
            }
        }
        bool clearCache = args.Any(a => a.Equals("--clear", StringComparison.OrdinalIgnoreCase));
        if (clearCache)
        {
            foreach (var m in new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc }) store.ClearCache(m);
            W("已清除本地热更新缓存");
        }

        foreach (var m in new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc })
        {
            var name = SpecSchema.ModuleTitle(m);
            Console.WriteLine($"\n----- {name} -----");
            W($"密钥(包名MD5): {DbCrypto.KeyHex(m)}");

            ChipDatabase embedded = null, local = null;
            try
            {
                embedded = store.LoadEmbedded(m);
                W($"内置库: {embedded.Count:N0} 条 / {embedded.Brands.Count} 品牌 / {embedded.Brands.Sum(b => b.Categories.Count)} 类别 / {embedded.Brands.Sum(b => b.Categories.Sum(c => c.Products.Count))} 产品线 / {embedded.Brands.Sum(b => b.Categories.Sum(c => c.Products.Sum(p => p.Series.Count)))} 系列 / 版本 {embedded.Version}  [OK]");
                foreach (var b in embedded.Brands)
                    foreach (var c in b.Categories)
                        W($"  {b.Name} › {c.Name}: {c.Products.Count} 产品线 / {c.Count:N0} 款");
            }
            catch (Exception ex)
            {
                failures++;
                W($"内置库: 失败 {ex.GetType().Name}: {ex.Message}  [FAIL]");
            }

            try
            {
                local = store.LoadLocal(m);
                W($"当前库: {local.Count:N0} 条 / 来源「{local.Source}」/ 版本 {local.Version}  [OK]");
                var sample = local.Records.FirstOrDefault();
                if (sample != null)
                    W($"  样例: {sample.Name} | {sample.BrandFamily} | {sample.SerieName} | 字段 {sample.Fields.Count} 个");
                var broken = local.Records.Count(r => string.IsNullOrWhiteSpace(r.Name));
                if (broken > 0) { failures++; W($"  存在 {broken} 条无名记录  [FAIL]"); }
            }
            catch (Exception ex)
            {
                failures++;
                W($"当前库: 失败 {ex.GetType().Name}: {ex.Message}  [FAIL]");
            }

            var info = await store.FetchRemoteInfoAsync(m);
            if (info == null)
            {
                W("远程版本接口: 无法连接（离线环境可忽略）");
            }
            else
            {
                W($"远程版本: v{info.Version} release={info.Release} size={info.Size} url={info.Url}");
            }

            if (doUpdate)
            {
                var progress = new Progress<(int percent, string status)>(p =>
                {
                    if (p.percent % 20 == 0) W($"  进度 {p.percent}% {p.status}");
                });
                var r = await store.UpdateAsync(m, progress);
                W($"热更新: {r.Message}  [{(r.Updated || r.Skipped ? "OK" : "WARN")}]");
                if (r.Updated)
                {
                    var reloaded = store.LoadLocal(m);
                    W($"  更新后重载: {reloaded.Count:N0} 条 / 来源「{reloaded.Source}」/ 版本 {reloaded.Version}");
                    if (reloaded.Source != "本地热更新缓存") { failures++; W("  重载来源异常  [FAIL]"); }
                }
            }
        }

        Console.WriteLine($"\n== 自检结束：{(failures == 0 ? "全部通过" : failures + " 项失败")} ==");
        return failures == 0 ? 0 : 1;
    }
}
