using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace ChipL.Core;

public sealed class AddedItem
{
    public string Name { get; init; } = "";
    public string SerieName { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Category { get; init; } = "";
    public string Predecessor { get; init; } = "";
    public ChipRecord Record { get; init; }
}

public sealed class DbDiff
{
    public ChipModule Module { get; init; }
    public int OldCount { get; init; }
    public int NewCount { get; init; }
    public int OldVersion { get; init; }
    public int NewVersion { get; init; }
    public List<AddedItem> Added { get; } = new();
    public List<string> NewSeries { get; } = new();
    public int RemovedCount { get; set; }

    public int Delta => NewCount - OldCount;
    public bool HasChanges => Added.Count > 0 || RemovedCount > 0 || NewSeries.Count > 0;
}

/// <summary>
/// 计算两次数据库快照的差异，用于热更新后展示「本次更新了什么」。
/// 只在更新成功后调用；对比对象是更新前的当前库与刚落地的新库。
/// </summary>
public static class DbDiffBuilder
{
    public static DbDiff Compare(ChipModule module, ChipDatabase oldDb, ChipDatabase newDb, int oldVersion, int newVersion)
    {
        var diff = new DbDiff
        {
            Module = module,
            OldCount = oldDb?.Count ?? 0,
            NewCount = newDb?.Count ?? 0,
            OldVersion = oldVersion,
            NewVersion = newVersion
        };

        var oldIds = new HashSet<string>(oldDb?.Records.Select(r => r.Id) ?? Enumerable.Empty<string>());
        var addedRecords = newDb.Records.Where(r => !oldIds.Contains(r.Id)).ToList();

        // 新增型号按「系列 → 名称」归组，并推断同系列中上一代产品
        foreach (var r in addedRecords
                     .OrderBy(r => r.BrandFamily)
                     .ThenByDescending(r => r.LaunchOrder)
                     .ThenBy(r => r.SerieName))
        {
            diff.Added.Add(new AddedItem
            {
                Name = r.Name,
                SerieName = string.IsNullOrEmpty(r.SerieName) ? "其他" : r.SerieName,
                Brand = r.BrandFamily,
                Category = ChipDatabase.CategoryLabel(r.Category, module),
                Predecessor = FindPredecessor(oldDb, r),
                Record = r
            });
        }

        // 新增系列
        var oldSeries = new HashSet<string>(
            oldDb?.Records.Select(r => r.SerieName).Where(s => !string.IsNullOrEmpty(s)) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var s in newDb.Records.Select(r => r.SerieName)
                     .Where(s => !string.IsNullOrEmpty(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!oldSeries.Contains(s)) diff.NewSeries.Add(s);
        }

        // 被移除的条目（服务端偶尔也会删数据）
        var newIds = new HashSet<string>(newDb.Records.Select(r => r.Id));
        diff.RemovedCount = (oldDb?.Records.Count(r => !newIds.Contains(r.Id)) ?? 0);
        return diff;
    }

    /// <summary>在同一系列里找发布最早、且早于新品的记录作为“上代产品”。</summary>
    private static string FindPredecessor(ChipDatabase oldDb, ChipRecord newRecord)
    {
        if (oldDb == null) return "";
        var launch = newRecord.LaunchOrder;
        var candidates = oldDb.Records
            .Where(r => string.Equals(r.SerieName, newRecord.SerieName, StringComparison.OrdinalIgnoreCase)
                        && r.BrandFamily == newRecord.BrandFamily)
            .Where(r => launch == 0 || r.LaunchOrder == 0 || r.LaunchOrder < launch)
            .OrderByDescending(r => r.LaunchOrder)
            .ToList();
        return candidates.FirstOrDefault()?.Name ?? "";
    }
}
