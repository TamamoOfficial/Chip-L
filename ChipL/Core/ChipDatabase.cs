using System.IO;
using System.Text.Json;

namespace ChipL.Core;

/// <summary>一条芯片记录：宽表 JSON 的原始字段 + 分级定位索引。</summary>
public sealed class ChipRecord
{
    public ChipModule Module { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string BrandFamily { get; set; } = "";

    /// <summary>产品线/家族（如 Radeon、GeForce、Xeon、Ryzen 9）。</summary>
    public string ProductFamily { get; set; } = "";

    /// <summary>平台类别（桌面/移动/工作站…）。</summary>
    public string Category { get; set; } = "";

    public string SerieId { get; set; } = "";
    public string SerieName { get; set; } = "";

    /// <summary>
    /// 官方把系列标成 -1 的型号（名称带 *），表示"尚未确认/传闻中的产品"。
    /// 这类记录不能丢，单独归入「未确认型号」分组。
    /// </summary>
    public bool IsUnconfirmed { get; set; }

    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key) => Fields.TryGetValue(key, out var v) ? v : null;

    public long LaunchOrder
    {
        get
        {
            var s = Get("launch");
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return 0;
            return long.TryParse(digits.Length > 6 ? digits[..6] : digits, out var v) ? v : 0;
        }
    }
}

/// <summary>系列节点：某一产品线下的具体系列（如 Radeon RX 7000 Series）。</summary>
public sealed class SerieNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Category { get; init; } = "";
    public int Order { get; init; }
    public List<ChipRecord> Records { get; } = new();
}

/// <summary>产品线节点：如 amd 桌面系列的 Radeon。</summary>
public sealed class ProductNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public int Order { get; init; }
    public List<SerieNode> Series { get; } = new();
    public int Count => Series.Sum(s => s.Records.Count);
}

/// <summary>类别节点：如 AMD › 桌面系列。</summary>
public sealed class CategoryNode
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int Order { get; init; }
    public List<ProductNode> Products { get; } = new();
    public int Count => Products.Sum(p => p.Count);
}

/// <summary>品牌节点：如 AMD / NVIDIA / Intel / Qualcomm。</summary>
public sealed class BrandNode
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public int Order { get; init; }
    public List<CategoryNode> Categories { get; } = new();
    public int Count => Categories.Sum(c => c.Count);
}

/// <summary>某个模块解析后的完整数据库：扁平记录 + 分级导航树。</summary>
public sealed class ChipDatabase
{
    public ChipModule Module { get; init; }
    public string Version { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime LoadedAt { get; init; } = DateTime.Now;

    public List<ChipRecord> Records { get; } = new();
    public List<BrandNode> Brands { get; } = new();

    public int Count => Records.Count;

    /// <summary>平台类别代码 → 中文名（与原版 Android 版 strings 一致）。</summary>
    public static readonly Dictionary<string, (string Cpu, string Gpu, int Order)> CategoryNames = new()
    {
        ["dk"] = ("桌面处理器", "桌面系列", 1),
        ["mb"] = ("移动处理器", "移动系列", 2),
        ["dw"] = ("工作站处理器", "工作站系列", 3),
        ["mw"] = ("移动工作站处理器", "移动工作站系列", 4),
        ["sv"] = ("服务器处理器", "服务器系列", 5),
        ["sc"] = ("超级计算机处理器", "超级计算机系列", 6),
        ["ed"] = ("嵌入式处理器", "嵌入式系列", 7),
        ["cs"] = ("游戏机处理器", "游戏机系列", 8),
        ["cm"] = ("", "矿卡系列", 9),
    };

    public const string UnconfirmedKey = "__unconfirmed";

    public static string CategoryLabel(string code, ChipModule module)
    {
        if (code == UnconfirmedKey) return "未确认型号";
        if (string.IsNullOrEmpty(code)) return module == ChipModule.Soc ? "全部分类" : "未分类";
        if (!CategoryNames.TryGetValue(code, out var t)) return code;
        return module == ChipModule.Gpu ? t.Gpu : t.Cpu;
    }

    public static int CategoryOrder(string code)
    {
        // 「未确认型号」固定排在最末
        if (code == UnconfirmedKey) return 100;
        return CategoryNames.TryGetValue(code ?? "", out var t) ? t.Order : 99;
    }

    private static readonly Dictionary<string, string> SerieOrder = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ProductOrder = new(StringComparer.Ordinal);

    public static ChipDatabase Parse(ChipModule module, string json, string source, string version)
    {
        var db = new ChipDatabase { Module = module, Source = source, Version = version };
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("数据库根节点不是 JSON 对象");

        var tables = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<Dictionary<string, string>>(prop.Value.GetArrayLength());
            foreach (var row in prop.Value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in row.EnumerateObject())
                {
                    dict[f.Name] = f.Value.ValueKind switch
                    {
                        JsonValueKind.String => f.Value.GetString(),
                        JsonValueKind.Number => f.Value.GetRawText(),
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        _ => null
                    };
                    if (dict[f.Name] != null) dict[f.Name] = dict[f.Name].Trim();
                }
                list.Add(dict);
            }
            tables[prop.Name] = list;
        }

        switch (module)
        {
            case ChipModule.Cpu: BuildCpu(db, tables); break;
            case ChipModule.Gpu: BuildGpu(db, tables); break;
            case ChipModule.Soc: BuildSoc(db, tables); break;
        }

        db.Records.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return db;
    }

    private static List<Dictionary<string, string>> Table(
        Dictionary<string, List<Dictionary<string, string>>> t, string name)
        => t.TryGetValue(name, out var v) ? v : new List<Dictionary<string, string>>();

    private static Dictionary<string, Dictionary<string, string>> Index(
        List<Dictionary<string, string>> rows, string keyField = "id")
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!row.TryGetValue(keyField, out var k) || string.IsNullOrEmpty(k)) continue;
            map[k] = row;
        }
        return map;
    }

    private static void Merge(ChipRecord rec, Dictionary<string, string> detail)
    {
        foreach (var kv in detail)
            if (!rec.Fields.ContainsKey(kv.Key) || string.IsNullOrEmpty(rec.Fields[kv.Key]))
                rec.Fields[kv.Key] = kv.Value;
    }

    private static void BuildCpu(ChipDatabase db, Dictionary<string, List<Dictionary<string, string>>> t)
    {
        foreach (var (prefix, brand) in new[] { ("amd", "AMD"), ("intel", "Intel") })
        {
            var products = Index(Table(t, prefix + "_products"));
            var series = Index(Table(t, prefix + "_series"));
            var data = Index(Table(t, prefix + "_cpu_data"), "id_cpu");

            foreach (var row in Table(t, prefix + "_cpu"))
            {
                var id = row.GetValueOrDefault("id") ?? "";
                var rec = new ChipRecord
                {
                    Module = ChipModule.Cpu,
                    Id = id,
                    Name = row.GetValueOrDefault("name") ?? $"#{id}",
                    BrandFamily = brand,
                    SerieId = row.GetValueOrDefault("id_serie") ?? ""
                };
                foreach (var kv in row) rec.Fields[kv.Key] = kv.Value;
                if (data.TryGetValue(id, out var detail)) Merge(rec, detail);

                if (series.TryGetValue(rec.SerieId, out var srow))
                {
                    rec.SerieName = srow.GetValueOrDefault("serie_name") ?? "";
                    rec.Fields["serie_order"] = srow.GetValueOrDefault("order_family") ?? "0";
                    var pid = srow.GetValueOrDefault("id_product_family") ?? "";
                    if (products.TryGetValue(pid, out var prow))
                    {
                        rec.ProductFamily = prow.GetValueOrDefault("product_name") ?? "";
                        rec.Category = prow.GetValueOrDefault("type") ?? "";
                        rec.Fields["product_order"] = prow.GetValueOrDefault("order_product") ?? "0";
                    }
                }
                if (rec.SerieId == "-1" || string.IsNullOrWhiteSpace(rec.SerieName))
                {
                    rec.IsUnconfirmed = true;
                    rec.SerieName = "未确认型号";
                    rec.ProductFamily = "未确认型号";
                    rec.Category = "__unconfirmed";
                }
                else if (string.IsNullOrEmpty(rec.ProductFamily)) rec.ProductFamily = brand;
                rec.Fields["brand"] = brand;
                rec.Fields["serie_name"] = rec.SerieName;
                rec.Fields["product"] = rec.ProductFamily;
                rec.Fields["category"] = CategoryLabel(rec.Category, ChipModule.Cpu);
                db.Records.Add(rec);
            }
        }
        db.Brands.AddRange(BuildTree(db, new[] { "AMD", "Intel" }));
    }

    private static void BuildGpu(ChipDatabase db, Dictionary<string, List<Dictionary<string, string>>> t)
    {
        foreach (var (prefix, brand) in new[] { ("amd", "AMD"), ("intel", "Intel"), ("nvidia", "NVIDIA") })
        {
            var products = Index(Table(t, prefix + "_products"));
            var series = Index(Table(t, prefix + "_series"));
            var data = Index(Table(t, prefix + "_gpu_data"), "id_gpu");

            foreach (var row in Table(t, prefix + "_gpu"))
            {
                var id = row.GetValueOrDefault("id") ?? "";
                var rec = new ChipRecord
                {
                    Module = ChipModule.Gpu,
                    Id = id,
                    Name = row.GetValueOrDefault("name") ?? $"#{id}",
                    BrandFamily = brand,
                    SerieId = row.GetValueOrDefault("id_serie") ?? ""
                };
                foreach (var kv in row) rec.Fields[kv.Key] = kv.Value;
                if (data.TryGetValue(id, out var detail)) Merge(rec, detail);

                if (series.TryGetValue(rec.SerieId, out var srow))
                {
                    rec.SerieName = srow.GetValueOrDefault("serie_name") ?? "";
                    rec.Fields["serie_order"] = srow.GetValueOrDefault("order_family") ?? "0";
                    var pid = srow.GetValueOrDefault("id_product_family") ?? "";
                    if (products.TryGetValue(pid, out var prow))
                    {
                        rec.ProductFamily = prow.GetValueOrDefault("product_name") ?? "";
                        rec.Category = prow.GetValueOrDefault("type") ?? "";
                        rec.Fields["product_order"] = prow.GetValueOrDefault("order_product") ?? "0";
                    }
                }
                if (rec.SerieId == "-1" || string.IsNullOrWhiteSpace(rec.SerieName))
                {
                    rec.IsUnconfirmed = true;
                    rec.SerieName = "未确认型号";
                    rec.ProductFamily = "未确认型号";
                    rec.Category = "__unconfirmed";
                }
                else if (string.IsNullOrEmpty(rec.ProductFamily)) rec.ProductFamily = brand;
                rec.Fields["brand"] = brand;
                rec.Fields["serie_name"] = rec.SerieName;
                rec.Fields["product"] = rec.ProductFamily;
                rec.Fields["category"] = CategoryLabel(rec.Category, ChipModule.Gpu);
                db.Records.Add(rec);
            }
        }
        db.Brands.AddRange(BuildTree(db, new[] { "AMD", "Intel", "NVIDIA" }));
    }

    private static readonly Dictionary<string, string> SocBrandNames = new()
    {
        ["0"] = "Apple", ["1"] = "HiSilicon", ["2"] = "MediaTek", ["3"] = "NVIDIA",
        ["4"] = "Qualcomm", ["5"] = "Samsung", ["6"] = "Xiaomi", ["7"] = "Allwinner",
        ["8"] = "Broadcom", ["9"] = "Freescale / NXP", ["10"] = "Rockchip", ["11"] = "Unisoc / Spreadtrum",
        ["12"] = "ST-Ericsson", ["13"] = "Texas Instruments", ["14"] = "Amlogic", ["15"] = "Leadcore",
        ["16"] = "LG Nuclun", ["17"] = "Marvell / Cavium", ["18"] = "Intel", ["19"] = "Ampere",
        ["20"] = "Google"
    };

    /// <summary>
    /// 品牌排序：与原版 Android 版一致——纯字母序（Allwinner 打头，不是 Apple）。
    /// </summary>
    private static int SocBrandRank(string name)
    {
        var ordered = SocBrandNames.Values
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int i = ordered.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 999 : i;
    }

    private static void BuildSoc(ChipDatabase db, Dictionary<string, List<Dictionary<string, string>>> t)
    {
        var series = Index(Table(t, "soc_series"));
        var data = Index(Table(t, "soc_data"), "id_soc");

        foreach (var row in Table(t, "soc_list"))
        {
            var id = row.GetValueOrDefault("id") ?? "";
            var brandId = row.GetValueOrDefault("id_brand") ?? "";
            var rec = new ChipRecord
            {
                Module = ChipModule.Soc,
                Id = id,
                Name = row.GetValueOrDefault("name") ?? $"#{id}",
                BrandFamily = SocBrandNames.GetValueOrDefault(brandId, "其他"),
                SerieId = row.GetValueOrDefault("id_serie") ?? ""
            };
            foreach (var kv in row) rec.Fields[kv.Key] = kv.Value;
            if (data.TryGetValue(id, out var detail)) Merge(rec, detail);

            // SoC 的三层：品牌 → 产品线(family) → 系列(serie_name) → 型号
            // 例：HiSilicon → kirin → Kirin 9xxx → 具体型号
            if (series.TryGetValue(rec.SerieId, out var srow))
            {
                rec.SerieName = srow.GetValueOrDefault("serie_name") ?? "";
                rec.Fields["serie_order"] = srow.GetValueOrDefault("order_family") ?? "0";
                var fam = srow.GetValueOrDefault("family") ?? "";
                rec.ProductFamily = string.IsNullOrWhiteSpace(fam) ? rec.BrandFamily : SocProductLineLabel(fam);
            }
            if (rec.SerieId == "-1" || string.IsNullOrWhiteSpace(rec.SerieName))
            {
                rec.IsUnconfirmed = true;
                rec.SerieName = "未确认型号";
                rec.ProductFamily = "未确认型号";
                rec.Category = "__unconfirmed";
            }
            else if (string.IsNullOrEmpty(rec.ProductFamily)) rec.ProductFamily = rec.BrandFamily;
            rec.Fields["brand"] = rec.BrandFamily;
            rec.Fields["serie_name"] = rec.SerieName;
            rec.Fields["product"] = rec.ProductFamily;
            rec.Fields["category"] = "全部分类";
            db.Records.Add(rec);
        }

        // 品牌按字母序（与原版一致：Allwinner → Amlogic → Ampere → Apple → …）
        var ordered = db.Records.Select(r => r.BrandFamily)
            .Distinct()
            .OrderBy(SocBrandRank)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        db.Brands.AddRange(BuildTree(db, ordered));
    }

    /// <summary>SoC 产品线（family 代码）转展示名。</summary>
    private static string SocProductLineLabel(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return "";
        return family.ToLowerInvariant() switch
        {
            "a-series" => "A 系列",
            "s-series" => "S 系列",
            "m-series" => "M 系列",
            "h-series" => "H 系列",
            "r-series" => "R 系列",
            "v-series" => "V 系列",
            "vr-series" => "VR 系列",
            "f-series" => "F 系列",
            "kirin" => "Kirin 麒麟",
            "k3" => "K3",
            "hisilicon" => "Hi 服务器",
            "snapdragon" => "Snapdragon 骁龙",
            "homehub" => "Home Hub",
            "smartaudio" => "Smart Audio",
            "custom" => "半定制",
            "mt" => "MT / Helio / Dimensity",
            "tegra" => "Tegra",
            "n1" => "N1",
            "exynos" => "Exynos",
            "surge" => "Surge 澎湃",
            "xring" => "XRING",
            "bcm" => "BCM",
            "imx" => "i.MX",
            "rk" => "RK",
            "sc" => "SC",
            "unisoct" => "T 系列",
            "nt" => "NovaThor",
            "omap" => "OMAP",
            "sitara" => "Sitara",
            "amlogic" => "AML",
            "leadcore" => "LC",
            "nuclun" => "Nuclun",
            "thunder" => "ThunderX",
            "atom" => "Atom",
            "ampere" => "Ampere",
            "tensor" => "Tensor",
            _ => family
        };
    }


    private static int ParseOrder(string s)
        => int.TryParse(s, out var v) ? v : 0;

    /// <summary>由扁平记录构建 品牌 → 类别 → 产品线 → 系列 → 型号 树。</summary>
    private static IEnumerable<BrandNode> BuildTree(ChipDatabase db, string[] brandOrder)
    {
        int brandRank = 0;
        foreach (var brand in brandOrder)
        {
            var brandRecs = db.Records.Where(r => r.BrandFamily == brand).ToList();
            if (brandRecs.Count == 0) continue;

            int catRank = 0;
            var bnode = new BrandNode { Key = brand, Name = brand, Order = brandRank++ };

            foreach (var catGroup in brandRecs
                         .GroupBy(r => string.IsNullOrEmpty(r.Category) ? "" : r.Category)
                         .OrderBy(g => CategoryOrder(g.Key)))
            {
                var cnode = new CategoryNode
                {
                    Key = catGroup.Key,
                    Name = CategoryLabel(catGroup.Key, db.Module),
                    Order = CategoryOrder(catGroup.Key) + catRank++
                };

                int prodRank = 0;
                foreach (var prodGroup in catGroup
                             .GroupBy(r => string.IsNullOrEmpty(r.ProductFamily) ? brand : r.ProductFamily)
                             .OrderByDescending(g => g.Max(r => ParseOrder(r.Get("product_order"))))
                             .ThenByDescending(g => g.Max(r => ParseOrder(r.Get("serie_order"))))
                             .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var pnode = new ProductNode
                    {
                        Id = prodGroup.Key,
                        Name = prodGroup.Key,
                        Category = cnode.Key,
                        Order = prodRank++
                    };

                    int serieRank = 0;
                    foreach (var serGroup in prodGroup
                                 .GroupBy(r => string.IsNullOrEmpty(r.SerieName) ? "其他" : r.SerieName)
                                 .OrderByDescending(g => ParseOrder(g.First().Get("serie_order")))
                                 .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var snode = new SerieNode
                        {
                            Id = serGroup.First().SerieId,
                            Name = serGroup.Key,
                            ProductId = pnode.Id,
                            Category = cnode.Key,
                            Order = serieRank++
                        };
                        // 系列内型号：新的在前（与原版列表一致）
                        foreach (var r in serGroup
                                     .OrderByDescending(r => r.LaunchOrder)
                                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                            snode.Records.Add(r);
                        pnode.Series.Add(snode);
                    }
                    cnode.Products.Add(pnode);
                }
                bnode.Categories.Add(cnode);
            }
            yield return bnode;
        }
    }
}
