using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using ChipL.Core;

namespace ChipL;

public sealed class ChipItemViewModel : INotifyPropertyChanged
{
    private readonly ChipModule _module;
    private ImageSource _image;
    private bool _imageRequested;

    public ChipItemViewModel(ChipRecord record)
    {
        Record = record;
        _module = record.Module;
    }

    public ChipRecord Record { get; }
    public string Name => Record.Name;
    public string SerieName => Record.SerieName;

    public string Brand => Record.BrandFamily;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(string key, string unit = "")
            {
                var v = Record.Get(key);
                if (string.IsNullOrWhiteSpace(v) || v == "-" || v == "None") return;
                // 统一走 ValueFormat（4.0 → 4 万/千分位），与详情页保持一致
                var spec = new FieldSpec { Key = key, Label = key, Unit = unit };
                var pretty = ValueFormat.Pretty(spec, v);
                if (!string.IsNullOrWhiteSpace(pretty)) parts.Add(pretty);
            }

            switch (_module)
            {
                case ChipModule.Cpu:
                    Add("cores");
                    if (parts.Count > 0) parts[^1] += " 核";
                    Add("core_clk", "MHz");
                    Add("tdp", "W");
                    break;
                case ChipModule.Gpu:
                    Add("cores");
                    if (parts.Count > 0) parts[^1] += " 单元";
                    Add("core_clk", "MHz");
                    Add("memory_size", "MB");
                    break;
                case ChipModule.Soc:
                    Add("core_clk_max", "MHz");
                    Add("graphics_name");
                    Add("tdp", "W");
                    break;
            }
            return string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public string Badge => Record.BrandFamily;

    /// <summary>未确认型号（官方标 * 的传闻产品）在列表里加个标记。</summary>
    public bool IsUnconfirmed => Record.IsUnconfirmed;
    public string NameSuffix => Record.IsUnconfirmed ? "  · 未确认" : "";

    public string Initials
    {
        get
        {
            var parts = Record.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Length > 0 ? parts[0][..Math.Min(2, parts[0].Length)] : "?";
            return (parts[0][..Math.Min(1, parts[0].Length)] + parts[1][..Math.Min(1, parts[1].Length)]).ToUpperInvariant();
        }
    }

    /// <summary>远程缩略图（与安卓版同一图源），失败时退回文字占位。</summary>
    public ImageSource Image
    {
        get
        {
            if (!_imageRequested)
            {
                _imageRequested = true;
                _ = LoadImageAsync();
            }
            return _image;
        }
    }

    private async Task LoadImageAsync()
    {
        var bmp = await ImageLoader.LoadAsync(Record, true, 220);
        if (bmp != null)
        {
            _image = bmp;
            OnPropertyChanged(nameof(Image));
            OnPropertyChanged(nameof(HasImage));
        }
    }

    public bool HasImage => _image != null;

    /// <summary>收藏标记（由 MainWindow 维护的集合驱动）。</summary>
    private bool _favorite;
    public bool IsFavorite => _favorite;
    public string FavoriteIcon => _favorite ? "★" : "☆";

    /// <summary>已收藏时图标用高亮橙色，未收藏用次要色（与收藏面板保持一致）。</summary>
    public string FavoriteIconColor => _favorite ? "#FFF2A33C" : "#FF95A3B8";

    public void SetFavorite(bool on)
    {
        if (_favorite == on) return;
        _favorite = on;
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteIcon));
        OnPropertyChanged(nameof(FavoriteIconColor));
    }

    public void RefreshFavorite()
    {
        var win = System.Windows.Application.Current?.MainWindow as MainWindow;
        SetFavorite(win?.IsFavorite(Record) ?? false);
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ImageLoader
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string ImageId(ChipRecord r) => r.Get("image_" + (r.Module switch
    {
        ChipModule.Cpu => "cpu",
        ChipModule.Gpu => "gpu",
        _ => "soc"
    }));

    private static bool HasImageId(string img) =>
        !string.IsNullOrWhiteSpace(img) && img != "0" && img != "-" && img != "None";

    private static string ImageFolder(ChipRecord r) => r.Module switch
    {
        ChipModule.Cpu => "cpus",
        ChipModule.Gpu => "gpus",
        _ => "socs"
    };

    /// <summary>
    /// 服务端图库按 id_serie 分目录，但历史数据存在目录错位（同一张图只存在于 0 或 1 目录）。
    /// 因此按「系列目录 → 0 → 1 → 根目录」依次尝试，命中后写入 PrefCache 复用。
    /// </summary>
    private static readonly Dictionary<string, string> PrefCache = new(StringComparer.Ordinal);
    private const string ImageRoot = "https://www.ijrsoftware.com/images/";

    public static IEnumerable<string> CandidateUrls(ChipRecord r, bool thumb)
    {
        var img = ImageId(r);
        if (!HasImageId(img)) yield break;
        var folder = ImageFolder(r);
        var dirs = new List<string>();
        if (r.Module != ChipModule.Soc)
        {
            var serie = r.Get("id_serie");
            if (!string.IsNullOrWhiteSpace(serie) && serie != "0") dirs.Add($"{folder}/{serie}");
        }
        dirs.Add(folder + "/0");
        dirs.Add(folder + "/1");
        dirs.Add(folder);
        var middle = thumb ? "/thumbnails/" : "/";
        foreach (var d in dirs)
            yield return $"{ImageRoot}{d}{middle}{img}.jpg";
    }

    public static async Task<BitmapImage> LoadAsync(ChipRecord r, bool thumb, int decodeWidth)
    {
        var key = (r.Module + ":" + r.Id);
        string preferred = null;
        lock (PrefCache)
        {
            PrefCache.TryGetValue(key, out preferred);
            if (Failed.Contains(key)) return null;
        }
        if (preferred != null)
        {
            var b = await GetAsync(preferred, decodeWidth).ConfigureAwait(false);
            if (b != null) return b;
        }

        foreach (var url in CandidateUrls(r, thumb))
        {
            var bmp = await GetAsync(url, decodeWidth).ConfigureAwait(false);
            if (bmp != null)
            {
                lock (PrefCache) PrefCache[key] = url;
                return bmp;
            }
        }
        lock (Failed) Failed.Add(key);
        return null;
    }

    public static async Task<BitmapImage> GetAsync(string url, int decodeWidth = 220)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var cached)) return cached;
        }
        lock (Failed)
        {
            if (Failed.Contains(url)) return null;
        }
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length < 100) return null;
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
            }
            lock (Cache) Cache[url] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            if (ex is System.Net.Http.HttpRequestException)
            {
                lock (Failed) Failed.Add(url);
            }
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
