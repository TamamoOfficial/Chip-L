using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChipL.Core;

namespace ChipL;

/// <summary>
/// 主窗口的「对比清单 / 收藏」逻辑（与原版 Android 的交互一致）：
/// 列表每一行右侧有 ⚖对比 与 ☆收藏 两个按钮，
/// 点对比把该型号加入对比清单，顶栏按钮显示已选数量，再点顶栏进入对比页。
/// 三个模块的对比清单彼此隔离。
/// </summary>
public partial class MainWindow
{
    /// <summary>每个模块独立的对比清单（保持加入顺序）。</summary>
    private readonly Dictionary<ChipModule, List<ChipRecord>> _compareBasket = new()
    {
        [ChipModule.Cpu] = new List<ChipRecord>(),
        [ChipModule.Gpu] = new List<ChipRecord>(),
        [ChipModule.Soc] = new List<ChipRecord>()
    };

    /// <summary>收藏（跨模块，持久化）。</summary>
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
    private string FavoritesPath => Path.Combine(_store.RootDirectory, "favorites.json");

    private void LoadFavorites()
    {
        try
        {
            if (!File.Exists(FavoritesPath)) return;
            var arr = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavoritesPath));
            if (arr == null) return;
            foreach (var k in arr) _favorites.Add(k);
        }
        catch { }
    }

    private void SaveFavorites()
    {
        try
        {
            var dir = Path.GetDirectoryName(FavoritesPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favorites.ToList()));
        }
        catch { }
    }

    private static string FavKey(ChipRecord r) => r.Module + ":" + r.Id;

    public bool IsFavorite(ChipRecord r) => r != null && _favorites.Contains(FavKey(r));

    /// <summary>行内「⚖ 对比」：加入/移出当前模块的对比清单。</summary>
    private void OnRowCompare(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChipItemViewModel vm) return;
        var rec = vm.Record;
        var basket = _compareBasket[rec.Module];

        var exist = basket.FirstOrDefault(r => r.Id == rec.Id);
        if (exist != null)
        {
            basket.Remove(exist);
            SetAgentBanner($"已从对比清单移除：{rec.Name}（共 {basket.Count} 款）");
        }
        else
        {
            basket.Add(rec);
            SetAgentBanner($"已加入对比：{rec.Name}（共 {basket.Count} 款）");
        }
        UpdateCompareButton();
        RefreshCompareWindow();
    }

    /// <summary>行内「☆ 收藏」。</summary>
    private void OnRowFavorite(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChipItemViewModel vm) return;
        var key = FavKey(vm.Record);

        if (_favorites.Contains(key))
        {
            _favorites.Remove(key);
            SetAgentBanner($"已取消收藏：{vm.Record.Name}");
        }
        else
        {
            _favorites.Add(key);
            SetAgentBanner($"已收藏：{vm.Record.Name}");
        }
        SaveFavorites();
        vm.RefreshFavorite();
        RefreshFavoritePanel();
    }

    /// <summary>收藏列表（供调试接口读取）。</summary>
    public IReadOnlyCollection<string> FavoriteKeys => _favorites;

    /// <summary>按 id 切换收藏状态，返回新状态。</summary>
    public bool ToggleFavorite(ChipRecord r)
    {
        var key = FavKey(r);
        bool on;
        if (_favorites.Contains(key)) { _favorites.Remove(key); on = false; }
        else { _favorites.Add(key); on = true; }
        SaveFavorites();
        return on;
    }

    /// <summary>按关键字查找收藏的芯片。</summary>
    public IEnumerable<ChipRecord> FavoriteRecords()
    {
        foreach (var key in _favorites)
        {
            var parts = key.Split(':');
            if (parts.Length != 2) continue;
            var m = parts[0] switch
            {
                "Cpu" => ChipModule.Cpu,
                "Gpu" => ChipModule.Gpu,
                "Soc" => ChipModule.Soc,
                _ => (ChipModule?)null
            };
            if (m == null) continue;
            var rec = _databases[m.Value].Records.FirstOrDefault(r => r.Id == parts[1]);
            if (rec != null) yield return rec;
        }
    }

    // ===== 对比窗口单例：关窗只隐藏，清单保留到程序退出 =====
    private CompareWindow _compareWindow;

    /// <summary>
    /// 打开（或激活）唯一的对比窗口。
    /// 不设 Owner，避免挡住主窗口导致无法继续加对比项；
    /// 关闭窗口只是隐藏，对比清单仍在内存里，直到退出程序才清空。
    /// </summary>
    public void ShowCompareWindow()
    {
        if (_compareWindow == null || !_compareWindow.IsLoaded)
        {
            // 窗口本身可重建，但 _compareBasket 是字段，内容一直保留到程序退出
            _compareWindow = new CompareWindow(_databases, _compareBasket, _module);
            _compareWindow.Closed += (_, _) => _compareWindow = null;
            _compareWindow.Show();
        }
        else
        {
            if (_compareWindow.WindowState == WindowState.Minimized)
                _compareWindow.WindowState = WindowState.Normal;
            _compareWindow.SwitchTo(_module);
            _compareWindow.Activate();
        }
    }

    /// <summary>通知对比窗热更新（它开着才有效）。</summary>
    private void RefreshCompareWindow()
    {
        if (_compareWindow == null || !_compareWindow.IsLoaded) return;
        _compareWindow.RefreshFromOutside();
    }

    /// <summary>顶栏按钮文案：显示当前模块已加入对比的数量。</summary>
    private void UpdateCompareButton()
    {
        if (CompareButtonText == null) return;

        // 入口是全局的：显示三个模块合计数量
        int total = _compareBasket.Values.Sum(list => list.Count);
        if (total == 0)
        {
            CompareButtonText.Text = "⇄  对比";
            return;
        }

        var parts = new List<string>();
        foreach (var m in new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc })
        {
            int c = _compareBasket[m].Count;
            if (c > 0) parts.Add($"{SpecSchema.ModuleTitle(m)} {c}");
        }
        CompareButtonText.Text = $"⇄  对比 ({total})";
        CompareButton.ToolTip = "已加入对比：" + string.Join("、", parts);
    }

    /// <summary>对比窗内改动清单后，主窗口刷新按钮计数。</summary>
    public void OnBasketChangedExternally()
    {
        UpdateCompareButton();
        RefreshCompareWindow();
    }

    /// <summary>清空当前模块的对比清单。</summary>
    private void ClearCompareBasket(ChipModule module)
    {
        _compareBasket[module].Clear();
        RefreshCompareWindow();
    }
}


// ===================== 左侧收藏列表 =====================

public partial class MainWindow
{
    private ChipModule _favModule = ChipModule.Cpu;
    private bool _favReady;

    /// <summary>初始化收藏页签（Loaded 时调用一次）。</summary>
    private void InitFavoritesPanel()
    {
        _favReady = false;
        FavTabCpu.IsChecked = true;
        _favModule = ChipModule.Cpu;
        _favReady = true;
        RefreshFavoritePanel();
    }

    private void OnFavTab(object sender, RoutedEventArgs e)
    {
        if (!_favReady) return;
        var m = sender == FavTabGpu ? ChipModule.Gpu
              : sender == FavTabSoc ? ChipModule.Soc
              : ChipModule.Cpu;
        if (m == _favModule) return;
        _favModule = m;
        RefreshFavoritePanel();
    }

    /// <summary>重绘当前模块的收藏列表：每行带对比 与 取消收藏。</summary>
    private void RefreshFavoritePanel()
    {
        if (FavPanel == null) return;

        var module = _favModule;
        var items = _databases.TryGetValue(module, out var db)
            ? db.Records.Where(r => IsFavorite(r)).ToList()
            : new List<ChipRecord>();

        FavPanel.Children.Clear();
        FavCountText.Text = items.Count.ToString();
        FavEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var rec in items)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x31)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 4, 5),
                Margin = new Thickness(0, 0, 0, 3)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = rec.Name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(rec.SerieName) ? rec.BrandFamily : rec.SerieName,
                FontSize = 9.5,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            grid.Children.Add(info);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var cmp = new Button
            {
                Style = (Style)FindResource("RowIconButton"),
                Content = new TextBlock { Text = "⚖", FontSize = 12 },
                ToolTip = "加入对比",
                Tag = rec
            };
            cmp.Click += (s, e) =>
            {
                if ((s as FrameworkElement)?.Tag is not ChipRecord r) return;
                ToggleBasket(r);
            };
            btns.Children.Add(cmp);

            var unfav = new Button
            {
                Style = (Style)FindResource("RowIconButton"),
                Content = new TextBlock { Text = "★", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA3, 0x3C)) },
                ToolTip = "取消收藏",
                Tag = rec
            };
            unfav.Click += (s, e) =>
            {
                if ((s as FrameworkElement)?.Tag is not ChipRecord r) return;
                if (_favorites.Remove(FavKey(r)))
                {
                    SaveFavorites();
                    SetAgentBanner($"已取消收藏：{r.Name}");
                }
                RefreshFavoritePanel();
            };
            btns.Children.Add(unfav);

            Grid.SetColumn(btns, 1);
            grid.Children.Add(btns);

            row.Child = grid;

            // 点击行内任意空白处：切到该芯片所属模块，定位并显示详情
            var captured = rec;
            row.Cursor = System.Windows.Input.Cursors.Hand;
            var normalBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x31));
            var hoverBg = new SolidColorBrush(Color.FromRgb(0x20, 0x29, 0x36));
            row.Background = normalBg;
            row.BorderThickness = new Thickness(1);
            row.BorderBrush = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, args) =>
            {
                // 点在 ⚖ / ★ 按钮上时交给按钮自己处理
                if (args.OriginalSource is DependencyObject src && FindAncestorButton(src) != null) return;
                ShowRecordDetail(captured);
            };
            // 与主列表 ChipListItem 同样的悬停高亮：背景提亮 + 边框
            row.MouseEnter += (_, _) =>
            {
                row.Background = hoverBg;
                row.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x53, 0x72));
            };
            row.MouseLeave += (_, _) =>
            {
                row.Background = normalBg;
                row.BorderBrush = Brushes.Transparent;
            };

            FavPanel.Children.Add(row);
        }
    }


    /// <summary>向上找 Button 祖先，用于区分「点按钮」和「点行空白」。</summary>
    private static System.Windows.Controls.Button FindAncestorButton(DependencyObject node)
    {
        while (node != null)
        {
            if (node is System.Windows.Controls.Button b) return b;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// 从收藏等入口直接打开某型号的详情：
    /// 若不在当前模块就先切模块，再清空筛选确保它出现在列表里，最后选中并滚动到可见。
    /// </summary>
    private static readonly Random _rng = new Random();

    /// <summary>
    /// 骰子按钮：在当前芯片类别的全库中随机挑一款，
    /// 把导航切到它所属的分支（制造商 → 定位 → 产品线 → 系列），并显示详情。
    /// </summary>
    private void OnPickRandom(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_databases.TryGetValue(_module, out var db) || db.Count == 0) return;

            var rec = db.Records[_rng.Next(db.Count)];

            // 导航精确定位到该芯片所在分支
            _navReady = false;
            Nav.TryFocusRecord(rec);
            _navReady = true;
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";

            ApplyFilter(selectFirst: false);

            var vm = _items.FirstOrDefault(v => v.Record.Id == rec.Id);
            if (vm != null)
            {
                ChipList.SelectedItem = vm;
                _selected = vm;
                ShowDetail(vm.Record);
                ChipList.ScrollIntoView(vm);
            }
            else
            {
                // 极端情况（筛选未覆盖到）：直接渲染详情
                _selected = new ChipItemViewModel(rec);
                ShowDetail(rec);
                DetailScroll.ScrollToTop();
            }

            UpdateDbStatusText();
            SetAgentBanner($"随机抽到：{rec.Name}");
        }
        catch (Exception ex)
        {
            LogUiError("random", ex);
        }
    }

    /// <summary>
    /// 从收藏等入口直接查看某型号详情。
    /// 只更新右侧详情区，不改动导航筛选、搜索词与列表选中状态。
    /// 若该芯片属于别的模块，临时按其模块的字段集渲染详情（不切换当前模块）。
    /// </summary>
    private void ShowRecordDetail(ChipRecord rec)
    {
        if (rec == null) return;

        try
        {
            _selected = new ChipItemViewModel(rec);
            ShowDetail(rec);
            DetailScroll.ScrollToTop();
            UpdateDetailPlaceholder();

            var where = rec.Module == _module
                ? ""
                : $"（{SpecSchema.ModuleTitle(rec.Module)}）";
            SetAgentBanner($"查看详情：{rec.Name}{where}");
        }
        catch (Exception ex)
        {
            LogUiError("show-detail", ex);
        }
    }

    /// <summary>按当前是否有选中项刷新详情占位提示。</summary>
    private void UpdateDetailPlaceholder()
    {
        if (DetailPlaceholder == null) return;
        DetailPlaceholder.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把某型号加入/移出它所属模块的对比清单（收藏列表也用它）。</summary>
    private void ToggleBasket(ChipRecord rec)
    {
        var basket = _compareBasket[rec.Module];
        var exist = basket.FirstOrDefault(r => r.Id == rec.Id);
        if (exist != null)
        {
            basket.Remove(exist);
            SetAgentBanner($"已从对比清单移除：{rec.Name}（共 {basket.Count} 款）");
        }
        else
        {
            basket.Add(rec);
            SetAgentBanner($"已加入对比：{rec.Name}（共 {basket.Count} 款）");
        }
        UpdateCompareButton();
        RefreshCompareWindow();
    }
}
