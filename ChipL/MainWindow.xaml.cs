using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChipL.Core;

namespace ChipL;

public partial class MainWindow : Window
{
    private readonly DatabaseStore _store = new();
    private readonly Dictionary<ChipModule, ChipDatabase> _databases = new();
    private readonly ObservableCollection<ChipItemViewModel> _items = new();
    private ChipModule _module = ChipModule.Cpu;
    private ChipItemViewModel _selected;
    private bool _initialized;
    private bool _navReady;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var c in new[] { "按名称排序", "按发布时间（新→旧）", "按发布时间（旧→新）" })
            SortBox.Items.Add(c);
        SortBox.SelectedIndex = 0;
        ChipList.ItemsSource = _items;
        Nav.FilterChanged += OnNavFilterChanged;
        Closed += (_, _) => StopAgentServer();
        SearchBox.TextChanged += (_, _) => UpdateSearchHint();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var m in new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc })
                _databases[m] = _store.LoadLocal(m);
        }
        catch (Exception ex)
        {
            MessageBox.Show("数据库载入失败：\n" + ex.Message, "Chip-L 桌面版",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        CpuCountText.Text = $"{_databases[ChipModule.Cpu].Count:N0} 款处理器";
        GpuCountText.Text = $"{_databases[ChipModule.Gpu].Count:N0} 款显卡";
        SocCountText.Text = $"{_databases[ChipModule.Soc].Count:N0} 款 SoC";

        _initialized = true;
        RefreshModule(selectFirst: true);
        UpdateDbStatusText();

        LoadFavorites();
        InitFavoritesPanel();
        StartAgentServer();

        // 启动后静默检查一次热更新（不阻塞界面）
        _ = AutoCheckAsync();
    }

    private async Task AutoCheckAsync()
    {
        try
        {
            await Task.Delay(1200);
            await CheckUpdatesAsync(interactive: false);
        }
        catch
        {
            // 后台静默检查失败不打扰用户
        }
    }

    private void UpdateDbStatusText()
    {
        var db = _databases[_module];
        var mod = SpecSchema.ModuleTitle(_module);
        var release = _store.LocalRelease(_module);
        var origin = db.Source;
        DbStatusText.Text = release.HasValue
            ? $"{mod} 数据库 v{db.Version} · 热更新于 {release:yyyy-MM-dd HH:mm}"
            : $"{mod} 数据库 v{db.Version} · {origin}";
        if (Environment.GetEnvironmentVariable("CHIPL_SELFTEST") == "1")
            Console.WriteLine($"[selftest] status={DbStatusText.Text}");
    }

    private void OnModuleChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var m = sender == NavGpu ? ChipModule.Gpu : sender == NavSoc ? ChipModule.Soc : ChipModule.Cpu;
        if (m == _module) return;
        _module = m;
        RefreshModule(selectFirst: true);
        UpdateDbStatusText();
    }

    private void RefreshModule(bool selectFirst)
    {
        ModuleTitle.Text = _module switch
        {
            ChipModule.Cpu => "CPU 处理器",
            ChipModule.Gpu => "GPU 显卡",
            _ => "SoC 芯片"
        };
        ModuleSlogan.Text = SpecSchema.ModuleSlogan(_module);
        _navReady = false;
        Nav.Load(_databases[_module]);
        _navReady = true;
        UpdateCompareButton();
        RefreshCompareWindow();
        ApplyFilter(selectFirst);
    }

    private void OnNavFilterChanged(object sender, EventArgs e)
    {
        if (!_navReady) return;
        ApplyFilter(selectFirst: true);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        ApplyFilter(selectFirst: false);
    }

    private void UpdateSearchHint()
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        ApplyFilter(selectFirst: false);
    }

    private void ApplyFilter(bool selectFirst)
    {
        if (!_databases.TryGetValue(_module, out var db)) return;

        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ChipRecord> rows = db.Records;

        // 依次应用：系列 → 产品线 → 类别 → 品牌（取最具体的一层）
        if (Nav.Serie != null) rows = Nav.Serie.Records;
        else if (Nav.ProductFilter != null) rows = rows.Where(r =>
            r.BrandFamily == Nav.Brand.Name &&
            (string.IsNullOrEmpty(Nav.Category?.Key) ? true : r.Category == Nav.Category.Key) &&
            r.ProductFamily == Nav.ProductFilter.Name);
        else if (Nav.Category != null) rows = rows.Where(r =>
            r.BrandFamily == Nav.Brand.Name && r.Category == Nav.Category.Key);
        else if (Nav.Brand != null) rows = rows.Where(r => r.BrandFamily == Nav.Brand.Name);

        if (!string.IsNullOrEmpty(query))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rows = rows.Where(r => tokens.All(t => Matches(r, t)));
        }

        rows = SortBox.SelectedIndex switch
        {
            1 => rows.OrderByDescending(r => r.LaunchOrder).ThenBy(r => r.Name),
            2 => rows.OrderBy(r => r.LaunchOrder).ThenBy(r => r.Name),
            _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        };

        var previousId = _selected?.Record.Id;
        _items.Clear();
        int count = 0;
        foreach (var r in rows)
        {
            var vm = new ChipItemViewModel(r);
            vm.SetFavorite(IsFavorite(r));
            _items.Add(vm);
            count++;
        }

        ResultCount.Text = $"{count:N0} 条";
        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_items.Count > 0)
        {
            var restore = previousId == null ? null : _items.FirstOrDefault(v => v.Record.Id == previousId);
            if (selectFirst || _selected == null)
                ChipList.SelectedIndex = 0;
            else if (restore != null)
                ChipList.SelectedItem = restore;
            else
                ChipList.SelectedIndex = 0;
        }
        else
        {
            _selected = null;
            ShowDetail(null);
        }
    }

    private static bool Matches(ChipRecord r, string token)
    {
        if (r.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        if ((r.SerieName ?? "").Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        if ((r.BrandFamily ?? "").Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var key in new[] { "code_name", "codename", "chip", "model", "part_number", "sspec", "core_arch", "variant", "arch" })
        {
            var v = r.Get(key);
            if (!string.IsNullOrEmpty(v) && v.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void OnChipSelected(object sender, SelectionChangedEventArgs e)
    {
        // 列表支持 Ctrl/Shift 多选；详情区展示最后点击的那一款
        if (ChipList.SelectedItem is ChipItemViewModel vm)
        {
            _selected = vm;
            ShowDetail(vm.Record);
            CompareButton.Content = ChipList.SelectedItems.Count > 1
                ? $"⇄  对比 ({ChipList.SelectedItems.Count})"
                : "⇄  对比";
            return;
        }

        if (ChipList.SelectedItems.Count > 0)
        {
            var last = ChipList.SelectedItems[ChipList.SelectedItems.Count - 1] as ChipItemViewModel;
            if (last != null) { _selected = last; ShowDetail(last.Record); }
        }
        CompareButton.Content = ChipList.SelectedItems.Count > 1
            ? $"⇄  对比 ({ChipList.SelectedItems.Count})"
            : "⇄  对比";
    }

    private void ShowDetail(ChipRecord record)
    {
        DetailPanel.Children.Clear();
        if (record == null)
        {
            DetailPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        DetailPlaceholder.Visibility = Visibility.Collapsed;

        DetailPanel.Children.Add(BuildHeader(record));

        var specs = SpecSchema.For(record.Module);
        var sections = new List<string>();
        foreach (var spec in specs)
        {
            if (!sections.Contains(spec.Section)) sections.Add(spec.Section);
        }

        foreach (var section in sections)
        {
            var fields = specs.Where(s => s.Section == section)
                .Select(s => (spec: s, value: ValueFormat.Pretty(s, record.Get(s.Key))))
                .Where(t => !string.IsNullOrWhiteSpace(t.value))
                .ToList();
            if (fields.Count == 0) continue;

            var card = new Border { Style = (Style)FindResource("SectionCard"), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = section, Style = (Style)FindResource("SectionTitle") });

            // 两列布局：每个单元格只占自己需要的高度，互不拉伸。
            // （用 UniformGrid 会强制整行等高，个别长字段会把相邻单元格撑得很松。）
            var grid = new Grid { Margin = new Thickness(0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            int col = 0;
            foreach (var (spec, value) in fields)
            {
                if (spec.IsLarge)
                {
                    // 长文本独占一行，跨两列
                    if (col != 0) { row++; col = 0; }
                    var big = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
                    big.Children.Add(new TextBlock { Text = spec.Label, Style = (Style)FindResource("FieldLabel") });
                    big.Children.Add(new TextBlock
                    {
                        Text = value,
                        Style = (Style)FindResource("FieldValue"),
                        Margin = new Thickness(0)
                    });
                    Grid.SetRow(big, row);
                    Grid.SetColumn(big, 0);
                    Grid.SetColumnSpan(big, 2);
                    EnsureRow(grid, row);
                    grid.Children.Add(big);
                    row++; col = 0;
                    continue;
                }

                var cell = new StackPanel
                {
                    Margin = new Thickness(0, 0, 12, 8),
                    VerticalAlignment = VerticalAlignment.Top
                };
                cell.Children.Add(new TextBlock { Text = spec.Label, Style = (Style)FindResource("FieldLabel") });
                cell.Children.Add(new TextBlock
                {
                    Text = spec.Key == "launch" ? ValueFormat.Launch(value) : value,
                    Style = (Style)FindResource("FieldValue"),
                    Margin = new Thickness(0)
                });

                EnsureRow(grid, row);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);

                col++;
                if (col >= 2) { col = 0; row++; }
            }
            if (grid.Children.Count > 0) stack.Children.Add(grid);
            card.Child = stack;
            DetailPanel.Children.Add(card);
        }
    }

    /// <summary>保证 Grid 有足够行，且每行按内容自适应高度。</summary>
    private static void EnsureRow(Grid grid, int row)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    private FrameworkElement BuildHeader(ChipRecord record)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("PanelAltBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 缩略图
        var imgBox = new Border
        {
            Width = 108,
            Height = 108,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x24, 0x32)),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 16, 0),
            ClipToBounds = true
        };
        var img = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        imgBox.Child = img;
        _ = ImageLoader.LoadAsync(record, false, 480).ContinueWith(t =>
        {
            var bmp = t.Result;
            if (bmp != null) Dispatcher.Invoke(() => img.Source = bmp);
        });
        outer.Children.Add(imgBox);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = record.Name,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextBrush")
        };
        info.Children.Add(title);

        var sub = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.BrandFamily)) sub.Add(record.BrandFamily);
        if (!string.IsNullOrWhiteSpace(record.SerieName)) sub.Add(record.SerieName);
        var launch = ValueFormat.Launch(record.Get("launch"));
        if (!string.IsNullOrWhiteSpace(launch)) sub.Add(launch + " 发布");
        info.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", sub),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("SubTextBrush"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var quick = new List<(string label, string value)>();
        void Q(string key, string label, string unit = "")
        {
            var v = record.Get(key);
            if (string.IsNullOrWhiteSpace(v) || v == "-" || v == "None") return;
            // 与详情区共用同一套格式化（4.0 → 4、2400 → 2,400），避免两处显示不一致
            var spec = new FieldSpec { Key = key, Label = label, Unit = unit };
            var pretty = ValueFormat.Pretty(spec, v);
            if (!string.IsNullOrWhiteSpace(pretty)) quick.Add((label, pretty));
        }
        if (record.Module == ChipModule.Cpu)
        {
            Q("cores", "核心");
            Q("threads", "线程");
            Q("core_clk", "基准", "MHz");
            Q("core_clk_boost", "睿频", "MHz");
            Q("tdp", "TDP", "W");
        }
        else if (record.Module == ChipModule.Gpu)
        {
            Q("cores", "着色单元");
            Q("core_clk", "基准", "MHz");
            Q("core_clk_boost", "加速", "MHz");
            Q("memory_size", "显存", "MB");
            Q("tdp", "TDP", "W");
        }
        else
        {
            Q("num_cores", "核心");
            Q("core_clk_max", "最高", "MHz");
            Q("graphics_name", "GPU");
            Q("tdp", "TDP", "W");
        }

        if (quick.Count > 0)
        {
            var rail = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var (label, value) in quick)
            {
                var pill = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x31, 0x45)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                var ps = new StackPanel { Orientation = Orientation.Horizontal };
                ps.Children.Add(new TextBlock { Text = label + " ", FontSize = 10.5, Foreground = (Brush)FindResource("SubTextBrush") });
                ps.Children.Add(new TextBlock { Text = value, FontSize = 11.5, Foreground = (Brush)FindResource("TextBrush") });
                pill.Child = ps;
                rail.Children.Add(pill);
            }
            info.Children.Add(rail);
        }

        Grid.SetColumn(info, 1);
        outer.Children.Add(info);
        card.Child = outer;
        return card;
    }

    private void OnOpenCompare(object sender, RoutedEventArgs e)
    {
        // 对比清单按模块分开保存，但入口是全局的：
        // 只要任意模块凑够 2 款就能打开对比窗，不再被当前所在模块钳制。
        var perModule = new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc }
            .Select(m => (Module: m, Count: _compareBasket[m].Count))
            .Where(x => x.Count > 0)
            .ToList();

        int total = perModule.Sum(x => x.Count);

        if (total == 0)
        {
            SetAgentBanner("对比清单为空：点列表每行右侧的 ⚖ 图标加入对比");
            MessageBox.Show(this,
                "对比清单为空。" + Environment.NewLine + Environment.NewLine +
                "请在列表每行右侧点 ⚖ 图标，把要对比的型号加入清单（至少 2 款），再点此按钮。",
                "对比", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (total < 2)
        {
            var where = SpecSchema.ModuleTitle(perModule[0].Module);
            SetAgentBanner($"对比清单只有 1 款（{where}），至少需要 2 款");
            MessageBox.Show(this,
                $"当前对比清单只有 1 款（{where}），至少需要 2 款才能对比。",
                "对比", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 优先切到「有最多对比项」的模块，让对比窗一打开就有内容
        var target = perModule.OrderByDescending(x => x.Count).First().Module;
        if (target != _module)
        {
            _module = target;
            NavCpu.IsChecked = target == ChipModule.Cpu;
            NavGpu.IsChecked = target == ChipModule.Gpu;
            NavSoc.IsChecked = target == ChipModule.Soc;
            RefreshModule(selectFirst: false);
        }

        ShowCompareWindow();
    }

    private async void OnUpdateDatabase(object sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync(interactive: true);
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        if (UpdateOverlay.Visibility == Visibility.Visible) return;
        UpdateButton.IsEnabled = false;
        if (interactive)
        {
            UpdateOverlay.Visibility = Visibility.Visible;
            UpdateProgress.Value = 0;
            UpdateStatusText.Text = "正在检查更新…";
        }

        var progress = new Progress<(int percent, string status)>(p =>
        {
            UpdateProgress.Value = p.percent;
            UpdateStatusText.Text = p.status;
        });

        var diffs = new List<DbDiff>();
        var messages = new List<string>();
        bool anyUpdated = false;
        try
        {
            foreach (var m in new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc })
            {
                var name = SpecSchema.ModuleTitle(m);
                if (!interactive) UpdateStatusText.Text = $"检查 {name} 数据库…";
                var r = await _store.UpdateAsync(m, interactive ? progress : null);
                if (r.Updated)
                {
                    anyUpdated = true;
                    messages.Add($"{name}：{r.Message}");
                    var fresh = _store.LoadLocal(m);
                    _databases[m] = fresh;

                    // 计算本次变更，供「更新说明」窗口展示
                    try
                    {
                        diffs.Add(DbDiffBuilder.Compare(m, r.OldDatabase, fresh, r.OldVersion, r.NewVersion));
                    }
                    catch (Exception ex)
                    {
                        LogUiError("diff", ex);
                    }
                }
                else if (interactive)
                {
                    messages.Add($"{name}：{r.Message}");
                }
            }

            if (anyUpdated)
            {
                CpuCountText.Text = $"{_databases[ChipModule.Cpu].Count:N0} 款处理器";
                GpuCountText.Text = $"{_databases[ChipModule.Gpu].Count:N0} 款显卡";
                SocCountText.Text = $"{_databases[ChipModule.Soc].Count:N0} 款 SoC";
                RefreshModule(selectFirst: false);
                UpdateDbStatusText();
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            UpdateOverlay.Visibility = Visibility.Collapsed;

            // 有更新内容时展示变更说明（静默检查也展示，避免用户错过）
            if (anyUpdated && diffs.Count > 0)
            {
                try { ShowUpdateSummary(diffs); }
                catch (Exception ex) { LogUiError("summary", ex); }
            }
            else if (interactive)
            {
                MessageBox.Show(
                    messages.Count > 0 ? string.Join(Environment.NewLine, messages) : "检查完成",
                    "数据库检查", MessageBoxButton.OK, MessageBoxImage.None);
            }
        }
    }

    private void LogUiError(string tag, Exception ex)
    {
        try
        {
            var logPath = System.IO.Path.Combine(_store.RootDirectory, "ui-error.log");
            System.IO.File.AppendAllText(logPath,
                $"{DateTime.Now:HH:mm:ss} [{tag}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>展示「本次更新了什么」，并支持跳转到新增型号。</summary>
    private void ShowUpdateSummary(List<DbDiff> diffs)
    {
        // 静默检查结束时窗口可能不在前台，先激活，避免模态窗口被系统立即关闭
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        var win = new UpdateSummaryWindow(diffs)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        // 用非模态方式展示：不阻塞更新流程，也不会因前台切换被系统关掉
        win.ShowInTaskbar = true;
        win.JumpRequested += (_, record) =>
        {
            try { NavigateToRecord(win.JumpModule, record); }
            catch (Exception ex) { LogUiError("navigate", ex); }
        };
        win.Show();
        win.Activate();
    }

    /// <summary>切换到指定模块并定位到目标型号。</summary>
    private void NavigateToRecord(ChipModule module, ChipRecord target)
    {
        if (module != _module)
        {
            _module = module;
            NavCpu.IsChecked = module == ChipModule.Cpu;
            NavGpu.IsChecked = module == ChipModule.Gpu;
            NavSoc.IsChecked = module == ChipModule.Soc;
            RefreshModule(selectFirst: false);
        }
        RevealRecord(target);
    }

    /// <summary>清空筛选与搜索，确保目标型号一定出现在列表中。</summary>
    private void RevealRecord(ChipRecord target)
    {
        _navReady = false;
        Nav.Load(_databases[_module]);
        _navReady = true;
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";

        ApplyFilter(selectFirst: false);
        var vm = _items.FirstOrDefault(v => v.Record.Id == target.Id);
        if (vm != null)
        {
            ChipList.SelectedItem = vm;
            ChipList.ScrollIntoView(vm);
        }
    }
}
