using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChipL.Core;

namespace ChipL;

/// <summary>
/// 多芯片对比窗口。
/// 左侧只展示「已加入对比清单」的芯片（可直接移除），不含候选列表；
/// 右侧按清单顺序动态生成列，逐项标出差异。
/// CPU / GPU / SoC 三个清单彼此独立，用顶部标签切换查看。
/// </summary>
public partial class CompareWindow : Window
{
    private readonly Dictionary<ChipModule, ChipDatabase> _dbs;
    private readonly Dictionary<ChipModule, List<ChipRecord>> _baskets;
    private ChipModule _module;
    private bool _loading;

    /// <param name="baskets">主窗口传入的对比清单（引用同一份，改动实时生效）。</param>
    public CompareWindow(Dictionary<ChipModule, ChipDatabase> dbs,
                         Dictionary<ChipModule, List<ChipRecord>> baskets,
                         ChipModule module)
    {
        InitializeComponent();
        _dbs = dbs;
        _baskets = baskets;

        _loading = true;
        TabCpu.IsChecked = module == ChipModule.Cpu;
        TabGpu.IsChecked = module == ChipModule.Gpu;
        TabSoc.IsChecked = module == ChipModule.Soc;
        if (TabCpu.IsChecked != true && TabGpu.IsChecked != true && TabSoc.IsChecked != true)
            TabCpu.IsChecked = true;
        _module = module;
        _loading = false;

        Refresh();
    }

    /// <summary>主窗口清单变化时调用：内容热更新，不需要关窗重开。</summary>
    public void RefreshFromOutside()
    {
        if (!IsLoaded) return;
        Refresh();
    }

    /// <summary>切到指定模块并刷新（主窗口切模块时同步）。</summary>
    public void SwitchTo(ChipModule module)
    {
        if (module == _module) { RefreshFromOutside(); return; }
        _loading = true;
        TabCpu.IsChecked = module == ChipModule.Cpu;
        TabGpu.IsChecked = module == ChipModule.Gpu;
        TabSoc.IsChecked = module == ChipModule.Soc;
        _loading = false;
        _module = module;
        RefreshFromOutside();
    }

    private List<ChipRecord> Current => _baskets.TryGetValue(_module, out var l) ? l : new List<ChipRecord>();

    private void OnModuleTab(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var m = sender == TabGpu ? ChipModule.Gpu
              : sender == TabSoc ? ChipModule.Soc
              : ChipModule.Cpu;
        if (m == _module) return;
        _module = m;
        Refresh();
    }

    /// <summary>重建左侧已选列表与右侧对比表。</summary>
    private void Refresh()
    {
        var picks = Current;

        BasketTitle.Text = $"{SpecSchema.ModuleTitle(_module)} 已选对比项";
        CountText.Text = $"{picks.Count} 款";
        BasketEmpty.Visibility = picks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        BuildBasketList(picks);
        BuildDiffTable(picks);
    }

    private void BuildBasketList(List<ChipRecord> picks)
    {
        BasketPanel.Children.Clear();
        for (int i = 0; i < picks.Count; i++)
        {
            var rec = picks[i];
            var normalBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x31));
            var hoverBg = new SolidColorBrush(Color.FromRgb(0x20, 0x29, 0x36));
            var hoverBorder = new SolidColorBrush(Color.FromRgb(0x3D, 0x53, 0x72));

            var row = new Border
            {
                Background = normalBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 6, 6),
                Margin = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent
            };

            // 与主列表一致：悬浮时背景提亮 + 描边
            row.MouseEnter += (_, _) =>
            {
                row.Background = hoverBg;
                row.BorderBrush = hoverBorder;
            };
            row.MouseLeave += (_, _) =>
            {
                row.Background = normalBg;
                row.BorderBrush = Brushes.Transparent;
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var idx = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x70, 0x89)),
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(idx);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = rec.Name,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{rec.BrandFamily} · {rec.SerieName}",
                FontSize = 9.5,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var del = new Button
            {
                Style = (Style)FindResource("RowIconButton"),
                Content = new TextBlock { Text = "✕", FontSize = 11 },
                ToolTip = "从对比中移除",
                Tag = rec
            };
            del.Click += (s, e) =>
            {
                var target = (s as FrameworkElement)?.Tag as ChipRecord;
                if (target == null) return;
                Current.RemoveAll(r => r.Id == target.Id);
                NotifyBasketChanged();
                Refresh();
            };
            Grid.SetColumn(del, 2);
            grid.Children.Add(del);

            row.Child = grid;
            BasketPanel.Children.Add(row);
        }
    }

    private void BuildDiffTable(List<ChipRecord> picks)
    {
        DiffPanel.Children.Clear();

        HeaderText.Text = picks.Count < 2
            ? $"{SpecSchema.ModuleTitle(_module)} 对比"
            : $"{SpecSchema.ModuleTitle(_module)} 对比（{picks.Count} 款）";

        EmptyHint.Visibility = picks.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
        if (picks.Count < 2) return;

        DiffPanel.Children.Add(BuildHeaderRow(picks));

        foreach (var spec in SpecSchema.For(_module))
        {
            var values = picks.Select(r => ValueFormat.Pretty(spec, r.Get(spec.Key))).ToList();
            if (spec.Key == "launch") values = values.Select(ValueFormat.Launch).ToList();
            if (values.All(string.IsNullOrWhiteSpace)) continue;

            bool anyDiff = values.Select(v => (v ?? "").Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

            DiffPanel.Children.Add(BuildRow(spec.Label, values, anyDiff));
        }
    }

    private FrameworkElement BuildHeaderRow(List<ChipRecord> picks)
    {
        var grid = MakeGrid(picks.Count);
        for (int i = 0; i < picks.Count; i++)
        {
            var rec = picks[i];
            var st = new StackPanel { Margin = new Thickness(6, 6, 6, 8) };
            st.Children.Add(new TextBlock
            {
                Text = rec.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
            st.Children.Add(new TextBlock
            {
                Text = rec.SerieName,
                FontSize = 9,
                Foreground = (Brush)FindResource("SubTextBrush"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
            Grid.SetColumn(st, i + 1);
            grid.Children.Add(st);
        }

        var line = new Border
        {
            Height = 1,
            Background = (Brush)FindResource("BorderBrush2"),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetColumnSpan(line, picks.Count + 1);
        grid.Children.Add(line);
        return grid;
    }

    /// <summary>
    /// 列宽策略：所有对比列等分容器宽度，长文本自动换行。
    /// 不引入横向滚动——芯片再多也一起挤在可视区内，靠换行表达内容。
    /// </summary>
    private Grid MakeGrid(int columnCount)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        // 字段名列按内容自适应但设上限，其余列等分
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            MinWidth = 92,
            MaxWidth = 132
        });
        for (int i = 0; i < columnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 1
            });
        return grid;
    }

    private FrameworkElement BuildRow(string label, List<string> values, bool different)
    {
        var grid = MakeGrid(values.Count);

        var lb = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("SubTextBrush"),
            Margin = new Thickness(6, 6, 6, 6),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(lb, 0);
        grid.Children.Add(lb);

        var diffBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xA3, 0x3C));

        for (int i = 0; i < values.Count; i++)
        {
            var v = string.IsNullOrWhiteSpace(values[i]) ? "—" : values[i];
            var text = new TextBlock
            {
                Text = v,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Foreground = different ? diffBrush : (Brush)FindResource("TextBrush"),
                Padding = new Thickness(6, 6, 6, 6)
            };
            var cell = new Border
            {
                Background = different
                    ? new SolidColorBrush(Color.FromArgb(0x18, 0xF2, 0xA3, 0x3C))
                    : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Child = text
            };
            Grid.SetColumn(cell, i + 1);
            grid.Children.Add(cell);
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x2A, 0x35, 0x46)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    /// <summary>清单被本窗口改动时通知主窗口刷新按钮计数。</summary>
    private void NotifyBasketChanged()
    {
        if (Owner is MainWindow mw) mw.OnBasketChangedExternally();
    }

    private void OnClearBasket(object sender, RoutedEventArgs e)
    {
        Current.Clear();
        NotifyBasketChanged();
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
