using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChipL.Core;

namespace ChipL;

/// <summary>
/// 分级导航（逐级下钻）：先选制造商，再选定位（类别），再选产品线，最后选系列。
/// 同一时间只展示「当前一层」的选项；已选的上级出现在面包屑里，可点击回退。
/// </summary>
public partial class NavigationBar : UserControl
{
    public event EventHandler FilterChanged;

    private ChipDatabase _db;
    private BrandNode _brand;
    private CategoryNode _category;
    private ProductNode _product;
    private SerieNode _serie;

    /// <summary>当前正在选择第几层：0=制造商 1=定位 2=产品线 3=系列</summary>
    private int _level;

    public NavigationBar()
    {
        InitializeComponent();
    }

    public BrandNode Brand => _brand;
    public CategoryNode Category => _category;
    public ProductNode ProductFilter => _product;
    public SerieNode Serie => _serie;

    public IReadOnlyList<ChipRecord> SerieRecords => _serie?.Records;

    public void Load(ChipDatabase db)
    {
        _db = db;
        _brand = null; _category = null; _product = null; _serie = null;
        _level = 0;
        Rebuild();
    }

    /// <summary>按名称定位（供调试接口使用）。</summary>
    public void SelectByNames(string brand, string category, string product, string serie)
    {
        if (_db == null) return;

        if (!string.IsNullOrEmpty(brand))
        {
            var b = _db.Brands.FirstOrDefault(x => x.Name.Equals(brand, StringComparison.OrdinalIgnoreCase));
            if (b != null) { _brand = b; _category = null; _product = null; _serie = null; }
        }

        if (!string.IsNullOrEmpty(category) && _brand != null)
        {
            var c = _brand.Categories.FirstOrDefault(x =>
                        x.Key.Equals(category, StringComparison.OrdinalIgnoreCase) ||
                        x.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    ?? _brand.Categories.FirstOrDefault(x =>
                        x.Name.Contains(category, StringComparison.OrdinalIgnoreCase));
            if (c != null) { _category = c; _product = null; _serie = null; }
        }

        if (!string.IsNullOrEmpty(product) && _category != null)
        {
            var p = _category.Products.FirstOrDefault(x =>
                        x.Name.Equals(product, StringComparison.OrdinalIgnoreCase))
                    ?? _category.Products.FirstOrDefault(x =>
                        x.Name.Contains(product, StringComparison.OrdinalIgnoreCase));
            if (p != null) { _product = p; _serie = null; }
        }

        if (!string.IsNullOrEmpty(serie) && _product != null)
        {
            var s = _product.Series.FirstOrDefault(x =>
                        x.Name.Equals(serie, StringComparison.OrdinalIgnoreCase))
                    ?? _product.Series.FirstOrDefault(x =>
                        x.Name.Contains(serie, StringComparison.OrdinalIgnoreCase));
            if (s != null) _serie = s;
        }

        // 同步当前层：已选越深，展示越深一层
        _level = _serie != null ? 3 : _product != null ? 2 : _category != null ? 1 : 0;
        Rebuild();
    }

    /// <summary>
    /// 温和定位：如果目标型号所在的「品牌/类别/产品线」能对上，就只切到那一层，
    /// 不清空用户当前的搜索与其他筛选。返回 true 表示定位成功。
    /// </summary>
    public bool TryFocusRecord(ChipRecord rec)
    {
        if (_db == null || rec == null) return false;

        var brand = _db.Brands.FirstOrDefault(b => b.Name == rec.BrandFamily);
        if (brand == null) return false;

        // 目标所在类别 / 产品线
        var category = brand.Categories.FirstOrDefault(c => c.Key == rec.Category)
                       ?? (brand.Categories.Count == 1 ? brand.Categories[0] : null);
        var product = category?.Products.FirstOrDefault(p => p.Name == rec.ProductFamily);
        var serie = product?.Series.FirstOrDefault(s => s.Name == rec.SerieName);

        _brand = brand;
        _category = category;
        _product = product;
        _serie = serie;

        _level = serie != null ? 3 : product != null ? 2 : category != null ? 1 : 0;
        Rebuild();
        return true;
    }

    private void Rebuild()
    {
        TabsPanel.Children.Clear();
        CrumbPanel.Children.Clear();

        if (_db == null || _db.Brands.Count == 0)
        {
            LevelLabel.Text = "";
            return;
        }

        _brand ??= _db.Brands[0];

        BuildCrumbs();

        // 归一化层级
        if (_level > 3) _level = 3;
        if (_brand == null) _level = 0;

        switch (_level)
        {
            case 0: BuildBrandLevel(); break;
            case 1: BuildCategoryLevel(); break;
            case 2: BuildProductLevel(); break;
            default: BuildSeriesLevel(); break;
        }

        var total = _serie?.Records.Count ?? _product?.Count ?? _category?.Count ?? _brand?.Count ?? 0;
        CountText.Text = total.ToString("N0");

        BackButton.Visibility = _level > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = (_category != null || _product != null || _serie != null)
            ? Visibility.Visible : Visibility.Collapsed;

        NextHintText.Text = _level switch
        {
            0 => "选择制造商后进入下一步",
            1 => "选择定位（类别）",
            2 => "选择产品线",
            _ => "选择系列（可选，选中后精确筛选）"
        };
    }

    /// <summary>面包屑：已选的上级，点击可回退到该层。</summary>
    private void BuildCrumbs()
    {
        void AddCrumb(string text, int gotoLevel, bool current)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = current
                    ? new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF5))
                    : new SolidColorBrush(Color.FromRgb(0x7F, 0xA8, 0xD8)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
                Cursor = current ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand
            };
            if (!current)
            {
                tb.MouseLeftButtonUp += (_, _) =>
                {
                    _level = gotoLevel;
                    // 回退时清空更深层选择
                    if (gotoLevel <= 0) { _category = null; _product = null; _serie = null; }
                    else if (gotoLevel == 1) { _product = null; _serie = null; }
                    else if (gotoLevel == 2) { _serie = null; }
                    Rebuild();
                    FilterChanged?.Invoke(this, EventArgs.Empty);
                };
                tb.MouseEnter += (_, _) => tb.TextDecorations = TextDecorations.Underline;
                tb.MouseLeave += (_, _) => tb.TextDecorations = null;
            }
            CrumbPanel.Children.Add(tb);

            var sep = new TextBlock
            {
                Text = " › ",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x70, 0x89)),
                VerticalAlignment = VerticalAlignment.Center
            };
            CrumbPanel.Children.Add(sep);
        }

        if (_brand != null) AddCrumb(_brand.Name, 0, _level == 0 && _category == null);
        if (_category != null) AddCrumb(_category.Name, 1, _level == 1 && _product == null);
        if (_product != null) AddCrumb(_product.Name, 2, _level == 2 && _serie == null);
        if (_serie != null) AddCrumb(_serie.Name, 3, true);
    }

    private void BuildBrandLevel()
    {
        LevelLabel.Text = "制造商";
        foreach (var b in _db.Brands)
        {
            bool sel = ReferenceEquals(_brand, b);
            TabsPanel.Children.Add(MakeTab($"{b.Name}", b.Count, sel, () =>
            {
                _brand = b;
                _category = null; _product = null; _serie = null;
                _level = b.Categories.Count > 1 ? 1 : 2;   // 只有一个类别时直接进产品线
                if (_level == 2) _category = b.Categories.FirstOrDefault();
                Rebuild();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    private void BuildCategoryLevel()
    {
        LevelLabel.Text = "定位（" + _brand.Name + "）";

        // 只有一个类别（如 SoC）时不必让用户再点一次，直接进产品线
        if (_brand.Categories.Count == 1)
        {
            _category = _brand.Categories[0];
            var only = _category.Products.Count == 1 ? _category.Products[0] : null;
            _product = only;
            _level = only != null ? 3 : 2;
            BuildProductLevel();
            return;
        }

        TabsPanel.Children.Add(MakeTab("全部", _brand.Count, _category == null, () =>
        {
            _category = null; _product = null; _serie = null;
            _level = 2;
            Rebuild();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }));

        foreach (var c in _brand.Categories)
        {
            bool sel = ReferenceEquals(_category, c);
            bool single = c.Products.Count == 1;
            TabsPanel.Children.Add(MakeTab(c.Name, c.Count, sel, () =>
            {
                _category = c;
                _product = single ? c.Products[0] : null;
                _serie = null;
                _level = single ? 3 : 2;
                Rebuild();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    private void BuildProductLevel()
    {
        LevelLabel.Text = "产品线（" + (_category?.Name ?? _brand.Name) + "）";
        if (_category == null) { BuildCategoryLevel(); return; }

        TabsPanel.Children.Add(MakeTab("全部", _category.Count, _product == null, () =>
        {
            _product = null; _serie = null;
            _level = 2;
            Rebuild();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }));

        foreach (var p in _category.Products)
        {
            bool sel = ReferenceEquals(_product, p);
            TabsPanel.Children.Add(MakeTab(p.Name, p.Count, sel, () =>
            {
                _product = p;
                _serie = null;
                _level = 3;
                Rebuild();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    private void BuildSeriesLevel()
    {
        LevelLabel.Text = "系列（" + (_product?.Name ?? "") + "）";
        if (_product == null) { BuildProductLevel(); return; }

        TabsPanel.Children.Add(MakeTab("全部", _product.Count, _serie == null, () =>
        {
            _serie = null;
            _level = 3;
            Rebuild();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }));

        foreach (var s in _product.Series)
        {
            bool sel = ReferenceEquals(_serie, s);
            TabsPanel.Children.Add(MakeTab(s.Name, s.Records.Count, sel, () =>
            {
                _serie = ReferenceEquals(_serie, s) ? null : s;
                Rebuild();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    private Border MakeTab(string text, int count, bool selected, Action onClick)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = selected
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF5))
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"  {count:N0}",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(selected
                ? Color.FromRgb(0xD8, 0xE6, 0xF8)
                : Color.FromRgb(0x7F, 0x92, 0xA8)),
            VerticalAlignment = VerticalAlignment.Center
        });

        var border = new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 5, 5),
            CornerRadius = new CornerRadius(6),
            Background = selected
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x6F, 0xBF))
                : new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x31)),
            BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x46)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = stack
        };

        if (!selected)
        {
            border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x31, 0x3F));
            border.MouseLeave += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x31));
        }
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private void OnBackLevel(object sender, RoutedEventArgs e)
    {
        if (_level == 0) return;
        _level--;
        if (_level < 3) _serie = null;
        if (_level < 2) _product = null;
        if (_level < 1) _category = null;
        Rebuild();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _category = null; _product = null; _serie = null;
        _level = 1;
        Rebuild();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }
}
