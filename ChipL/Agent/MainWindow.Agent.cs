using System.Text.Json;
using System.Windows;
using ChipL.Core;
using ChipL.Agent;

namespace ChipL;

/// <summary>
/// 把 AgentRequest 翻译成对主窗口的操作。
/// 每个指令都同时产出：Data（给 agent 读的结构化结果）+ Ui（用户在窗口上看到的一句话）。
/// </summary>
public sealed partial class MainWindow
{
    private AgentServer _agent;

    private void StartAgentServer()
    {
        try
        {
            _agent = new AgentServer(HandleAgentRequest);
            AgentBadge.Visibility = System.Windows.Visibility.Visible;
            AgentBadgeText.Text = $"调试接口 {_agent.Port}";
        }
        catch (Exception ex)
        {
            // 端口/权限异常不应影响正常使用，但必须留痕便于排查
            LogUiError("agent-start", ex);
            try
            {
                var p = System.IO.Path.Combine(_store.RootDirectory, "agent-error.log");
                System.IO.File.WriteAllText(p, DateTime.Now + " " + ex);
            }
            catch { }
        }
    }

    private void StopAgentServer()
    {
        try { _agent?.Dispose(); } catch { }
        _agent = null;
    }

    private AgentResult HandleAgentRequest(AgentRequest req)
    {
        // 接口可能从非 UI 线程进来，统一切到 UI 线程执行
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => HandleAgentRequest(req));

        try
        {
            switch (req.Cmd.ToLowerInvariant())
            {
                case "ping": return AgentPing();
                case "state": return AgentState();
                case "modules": return Wrap(AgentModules());
                case "tree": return Wrap(AgentTree(req));
                case "basket": return Wrap(AgentBasket(req));
                case "favorite": return Wrap(AgentFavorite(req));
                case "favtab": return Wrap(AgentFavTab(req));
                case "opendetail": return Wrap(AgentOpenDetail(req));
                case "pick": return Wrap(AgentPickRandom(req));
                case "module": return Wrap(AgentModule(req));
                case "sort": return Wrap(AgentSort(req));
                case "filter": return Wrap(AgentFilter(req));
                case "cmpwin": return Wrap(AgentCompareWindow(req));
                case "clearcache": return Wrap(AgentClearCache(req));
                case "status": return Wrap(AgentStatus());
                case "nav": return AgentNav(req);
                case "search": return AgentSearch(req);
                case "select": return AgentSelect(req);
                case "detail": return AgentDetail(req);
                case "compare": return AgentCompare(req);
                case "update": return AgentUpdate(req);
                case "history": return Wrap(AgentHistory());
                case "selftest": return Wrap(AgentSelfTest());
                case "help": return Wrap(AgentHelp());
                default:
                    return Wrap(AgentResult.Fail($"未知指令 '{req.Cmd}'，可用：help"));
            }
        }
        catch (Exception ex)
        {
            return Wrap(AgentResult.Fail(ex.GetType().Name + ": " + ex.Message));
        }
    }

    // ---------- 具体指令 ----------

    /// <summary>统一收口：把结果里的 Ui 文案推送到窗口横幅。</summary>
    private AgentResult Wrap(AgentResult r)
    {
        if (!string.IsNullOrEmpty(r.Ui)) SetAgentBanner(r.Ui);
        return r;
    }

    private AgentResult AgentPing()
        => AgentResult.Good(new { pid = Environment.ProcessId, version = "1.0.0" }, "调试接口连通");

    private AgentResult AgentState()
    {
        var db = _databases[_module];
        var data = new
        {
            module = _module.ToString(),
            brand = Nav.Brand?.Name,
            category = Nav.Category?.Name,
            product = Nav.ProductFilter?.Name,
            serie = Nav.Serie?.Name,
            categoryKey = Nav.Category?.Key,
            search = SearchBox.Text,
            filtered = _items.Count,
            total = db.Count,
            dbVersion = db.Version,
            dbSource = db.Source,
            selected = _selected?.Record?.Name,
            selectedId = _selected?.Record?.Id,
            counts = new
            {
                cpu = _databases[ChipModule.Cpu].Count,
                gpu = _databases[ChipModule.Gpu].Count,
                soc = _databases[ChipModule.Soc].Count
            }
        };
        var ui = $"检查状态：{SpecSchema.ModuleTitle(_module)} / {(Nav.Brand?.Name ?? "-")}" +
                 (Nav.Category != null ? $" › {Nav.Category.Name}" : "") +
                 (Nav.ProductFilter != null ? $" › {Nav.ProductFilter.Name}" : "") +
                 (Nav.Serie != null ? $" › {Nav.Serie.Name}" : "") +
                 $"（{_items.Count:N0} 条）";
        return Wrap(AgentResult.Good(data, ui));
    }

    /// <summary>导出某品牌下的完整导航树（产品线 → 系列 → 条数），用于核对层级与顺序。</summary>
    private AgentResult AgentTree(AgentRequest req)
    {
        var bn = req.Get("brand");
        var brands = _databases[_module].Brands
            .Where(b => string.IsNullOrEmpty(bn) || b.Name.Equals(bn, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (brands.Count == 0) return AgentResult.Fail("未找到品牌 " + bn);

        var data = brands.Select(b => new
        {
            brand = b.Name,
            products = b.Categories.SelectMany(c => c.Products).Select(p => new
            {
                product = p.Name,
                count = p.Count,
                series = p.Series.Select(s => new { serie = s.Name, count = s.Records.Count }).ToArray()
            }).ToArray()
        }).ToArray();

        var first = brands[0];
        var pls = first.Categories.SelectMany(c => c.Products).ToList();
        var ui = $"{first.Name} 下 {pls.Count} 条产品线：" +
                 string.Join("、", pls.Take(6).Select(p => $"{p.Name}({p.Count})"));
        return AgentResult.Good(data, ui);
    }

    /// <summary>
    /// 收藏（与对比是两回事）：
    ///   /favorite?action=list
    ///   /favorite?action=toggle&amp;id=3922
    ///   /favorite?action=clear
    /// </summary>
    private AgentResult AgentFavorite(AgentRequest req)
    {
        var action = (req.Get("action") ?? "list").ToLowerInvariant();

        if (action == "toggle")
        {
            var id = req.Get("id");
            var module = (req.Get("module") ?? _module.ToString()).ToLowerInvariant() switch
            {
                "gpu" => ChipModule.Gpu,
                "soc" => ChipModule.Soc,
                _ => ChipModule.Cpu
            };
            var rec = _databases[module].Records.FirstOrDefault(r => r.Id == id);
            if (rec == null) return Wrap(AgentResult.Fail("未找到型号"));

            bool on = ToggleFavorite(rec);
            RefreshFavoritePanel();
            return Wrap(AgentResult.Good(
                new { id = rec.Id, name = rec.Name, favorite = on, total = _favorites.Count },
                $"{(on ? "已收藏" : "已取消收藏")}：{rec.Name}"));
        }

        if (action == "clear")
        {
            _favorites.Clear();
            SaveFavorites();
            RefreshFavoritePanel();
            return Wrap(AgentResult.Good(new { total = 0 }, "收藏已清空"));
        }

        var moduleFilter = req.Get("module");
        var all = FavoriteRecords().ToList();
        IEnumerable<ChipRecord> shown = all;
        if (!string.IsNullOrEmpty(moduleFilter))
        {
            var fm = moduleFilter.ToLowerInvariant() switch
            {
                "gpu" => ChipModule.Gpu,
                "soc" => ChipModule.Soc,
                _ => ChipModule.Cpu
            };
            shown = all.Where(r => r.Module == fm);
        }
        var items = shown.Select(r => new { id = r.Id, name = r.Name, module = r.Module.ToString() }).ToArray();
        return Wrap(AgentResult.Good(
            new { total = all.Count, shown = items.Length, items },
            $"收藏共 {all.Count} 款" + (string.IsNullOrEmpty(moduleFilter) ? "" : $"，{moduleFilter} 显示 {items.Length} 款")));
    }

    /// <summary>
    /// 随机挑一款（与骰子按钮同一实现）：/pick  或  /pick?module=gpu
    /// </summary>

    /// <summary>切换芯片类别：/module?module=gpu</summary>
    private AgentResult AgentModule(AgentRequest req)
    {
        var arg = (req.Get("module") ?? "").ToLowerInvariant();
        var m = arg switch
        {
            "gpu" => ChipModule.Gpu,
            "soc" => ChipModule.Soc,
            "cpu" => ChipModule.Cpu,
            _ => (ChipModule?)null
        };
        if (m == null) return Wrap(AgentResult.Fail("module 参数需为 cpu / gpu / soc"));

        if (m.Value != _module)
        {
            _module = m.Value;
            NavCpu.IsChecked = m.Value == ChipModule.Cpu;
            NavGpu.IsChecked = m.Value == ChipModule.Gpu;
            NavSoc.IsChecked = m.Value == ChipModule.Soc;
            RefreshModule(selectFirst: true);
        }

        return Wrap(AgentResult.Good(
            new { module = _module.ToString(), total = _databases[_module].Count, filtered = _items.Count },
            $"已切换到 {SpecSchema.ModuleTitle(_module)}（{_items.Count:N0} 条）"));
    }

    /// <summary>排序：/sort?by=name|newest|oldest</summary>
    private AgentResult AgentSort(AgentRequest req)
    {
        var by = (req.Get("by") ?? "name").ToLowerInvariant();
        int idx = by switch
        {
            "newest" or "desc" => 1,
            "oldest" or "asc" => 2,
            _ => 0
        };
        SortBox.SelectedIndex = idx;
        ApplyFilter(selectFirst: false);

        var name = idx switch { 1 => "按发布时间（新→旧）", 2 => "按发布时间（旧→新）", _ => "按名称排序" };
        return Wrap(AgentResult.Good(
            new { sort = name, filtered = _items.Count },
            $"已切换排序：{name}"));
    }

    /// <summary>设置搜索词并返回当前筛选状态：/filter?search=xxx</summary>
    private AgentResult AgentFilter(AgentRequest req)
    {
        var search = req.Get("search");
        if (search != null)
        {
            SearchBox.Text = search;
            ApplyFilter(selectFirst: false);
        }
        return Wrap(AgentResult.Good(
            new
            {
                module = _module.ToString(),
                search = SearchBox.Text ?? "",
                brand = Nav.Brand?.Name,
                category = Nav.Category?.Name,
                product = Nav.ProductFilter?.Name,
                serie = Nav.Serie?.Name,
                filtered = _items.Count
            },
            $"当前筛选：{Nav.Brand?.Name ?? "-"} / 搜索「{SearchBox.Text}」→ {_items.Count:N0} 条"));
    }

    /// <summary>对比窗口控制：/cmpwin?action=open|close</summary>
    private AgentResult AgentCompareWindow(AgentRequest req)
    {
        var action = (req.Get("action") ?? "open").ToLowerInvariant();
        if (action == "close")
        {
            if (_compareWindow != null) { _compareWindow.Close(); _compareWindow = null; }
            return Wrap(AgentResult.Good(new { open = false }, "已关闭对比窗口"));
        }
        if (action == "open")
        {
            ShowCompareWindow();
            return Wrap(AgentResult.Good(new { open = _compareWindow != null }, "已打开对比窗口"));
        }
        return Wrap(AgentResult.Good(
            new { open = _compareWindow != null },
            _compareWindow != null ? "对比窗口已打开" : "对比窗口未打开"));
    }

    /// <summary>清空本地热更新缓存回内置库：/clearcache[?module=cpu|gpu|soc]</summary>
    private AgentResult AgentClearCache(AgentRequest req)
    {
        var arg = (req.Get("module") ?? "all").ToLowerInvariant();
        var targets = arg switch
        {
            "cpu" => new[] { ChipModule.Cpu },
            "gpu" => new[] { ChipModule.Gpu },
            "soc" => new[] { ChipModule.Soc },
            _ => new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc }
        };

        foreach (var m in targets)
        {
            _store.ClearCache(m);
            _databases[m] = _store.LoadLocal(m);
        }
        RefreshModule(selectFirst: false);
        UpdateDbStatusText();

        return Wrap(AgentResult.Good(
            new { cleared = targets.Select(m => m.ToString()).ToArray() },
            $"已清除缓存并回到内置库：{string.Join("、", targets.Select(SpecSchema.ModuleTitle))}"));
    }

    /// <summary>运行时概览：/status</summary>
    private AgentResult AgentStatus()
    {
        int bTotal = _compareBasket[ChipModule.Cpu].Count
                   + _compareBasket[ChipModule.Gpu].Count
                   + _compareBasket[ChipModule.Soc].Count;

        var data = new
        {
            pid = Environment.ProcessId,
            agentPort = _agent?.Port,
            currentModule = _module.ToString(),
            selected = _selected?.Record?.Name,
            filtered = _items.Count,
            compareWindowOpen = _compareWindow != null,
            basket = new
            {
                cpu = _compareBasket[ChipModule.Cpu].Count,
                gpu = _compareBasket[ChipModule.Gpu].Count,
                soc = _compareBasket[ChipModule.Soc].Count,
                total = bTotal
            },
            favorites = _favorites.Count,
            databases = new[]
            {
                new { module = "Cpu", count = _databases[ChipModule.Cpu].Count, version = _databases[ChipModule.Cpu].Version, source = _databases[ChipModule.Cpu].Source },
                new { module = "Gpu", count = _databases[ChipModule.Gpu].Count, version = _databases[ChipModule.Gpu].Version, source = _databases[ChipModule.Gpu].Source },
                new { module = "Soc", count = _databases[ChipModule.Soc].Count, version = _databases[ChipModule.Soc].Version, source = _databases[ChipModule.Soc].Source }
            }
        };
        return Wrap(AgentResult.Good(data,
            $"{SpecSchema.ModuleTitle(_module)} {_items.Count:N0} 条 · 对比 {bTotal} 款 · 收藏 {data.favorites} 款"));
    }

    private AgentResult AgentPickRandom(AgentRequest req)
    {
        var moduleArg = req.Get("module");
        if (!string.IsNullOrEmpty(moduleArg))
        {
            var m = moduleArg.ToLowerInvariant() switch
            {
                "gpu" => ChipModule.Gpu,
                "soc" => ChipModule.Soc,
                _ => ChipModule.Cpu
            };
            if (m != _module)
            {
                _module = m;
                NavCpu.IsChecked = m == ChipModule.Cpu;
                NavGpu.IsChecked = m == ChipModule.Gpu;
                NavSoc.IsChecked = m == ChipModule.Soc;
                RefreshModule(selectFirst: false);
            }
        }

        OnPickRandom(this, new RoutedEventArgs());

        var sel = _selected?.Record;
        if (sel == null) return Wrap(AgentResult.Fail("随机失败"));

        return Wrap(AgentResult.Good(new
        {
            module = _module.ToString(),
            id = sel.Id,
            name = sel.Name,
            brand = sel.BrandFamily,
            category = Nav.Category?.Name,
            product = Nav.ProductFilter?.Name,
            serie = Nav.Serie?.Name,
            selectedInList = _items.Any(v => v.Record.Id == sel.Id)
        }, $"随机抽到：{sel.Name}"));
    }

    /// <summary>
    /// 打开某型号详情（与点击收藏行/列表项走同一路径）：
    ///   /opendetail?id=1419
    ///   /opendetail?name=Ryzen 3 PRO 4350GE
    /// </summary>
    private AgentResult AgentOpenDetail(AgentRequest req)
    {
        var id = req.Get("id");
        var name = req.Get("name");
        var moduleArg = req.Get("module");

        ChipModule? module = null;
        if (!string.IsNullOrEmpty(moduleArg))
        {
            module = moduleArg.ToLowerInvariant() switch
            {
                "gpu" => ChipModule.Gpu,
                "soc" => ChipModule.Soc,
                "cpu" => ChipModule.Cpu,
                _ => (ChipModule?)null
            };
        }

        ChipRecord rec = null;
        if (module != null)
        {
            rec = FindRec(InModule(module.Value), id, name);
        }
        else
        {
            foreach (var m in new[] { _module, ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc })
            {
                rec = FindRec(InModule(m), id, name);
                if (rec != null) break;
            }
        }

        if (rec == null) return Wrap(AgentResult.Fail("未找到该型号"));

        // 复用与 UI 点击完全相同的实现
        ShowRecordDetail(rec);

        var selected = _selected?.Record;
        return Wrap(AgentResult.Good(new
        {
            requested = new { id = rec.Id, name = rec.Name, module = rec.Module.ToString() },
            moduleAfter = _module.ToString(),
            selectedId = selected?.Id,
            selectedName = selected?.Name,
            detailShown = selected != null && selected.Id == rec.Id
        }, $"已打开详情：{rec.Name}"));
    }

    private IEnumerable<ChipRecord> InModule(ChipModule m)
        => _databases.TryGetValue(m, out var db) ? db.Records : Enumerable.Empty<ChipRecord>();

    private static ChipRecord FindRec(IEnumerable<ChipRecord> rows, string id, string name)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var byId = rows.FirstOrDefault(r => r.Id == id);
            if (byId != null) return byId;
        }
        if (!string.IsNullOrEmpty(name))
            return rows.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>切换左侧收藏面板的页签：/favtab?module=gpu</summary>
    private AgentResult AgentFavTab(AgentRequest req)
    {
        var m = (req.Get("module") ?? "cpu").ToLowerInvariant() switch
        {
            "gpu" => ChipModule.Gpu,
            "soc" => ChipModule.Soc,
            _ => ChipModule.Cpu
        };
        _favModule = m;
        FavTabCpu.IsChecked = m == ChipModule.Cpu;
        FavTabGpu.IsChecked = m == ChipModule.Gpu;
        FavTabSoc.IsChecked = m == ChipModule.Soc;
        RefreshFavoritePanel();
        return Wrap(AgentResult.Good(
            new { module = m.ToString() },
            $"收藏面板切到 {SpecSchema.ModuleTitle(m)}"));
    }

    /// <summary>
    /// 对比清单操作：
    ///   /basket?action=add&amp;id=3922   加入
    ///   /basket?action=remove&amp;id=3922 移除
    ///   /basket?action=clear           清空当前模块
    ///   /basket?action=list            查看
    ///   /basket?action=open            打开对比窗
    /// </summary>
    private AgentResult AgentBasket(AgentRequest req)
    {
        var action = (req.Get("action") ?? "list").ToLowerInvariant();
        var basket = _compareBasket[_module];

        switch (action)
        {
            case "add":
            {
                var id = req.Get("id");
                var name = req.Get("name");
                var rec = _databases[_module].Records.FirstOrDefault(r => r.Id == id)
                          ?? _databases[_module].Records.FirstOrDefault(r =>
                              r.Name.Contains(name ?? " ", StringComparison.OrdinalIgnoreCase));
                if (rec == null) return Wrap(AgentResult.Fail("未找到型号"));
                if (basket.All(r => r.Id != rec.Id)) basket.Add(rec);
                UpdateCompareButton();
                RefreshCompareWindow();
                return Wrap(AgentResult.Good(
                    new { count = basket.Count, module = _module.ToString() },
                    $"已加入对比：{rec.Name}（共 {basket.Count} 款）"));
            }
            case "remove":
            {
                var id = req.Get("id");
                basket.RemoveAll(r => r.Id == id);
                UpdateCompareButton();
                RefreshCompareWindow();
                return Wrap(AgentResult.Good(
                    new { count = basket.Count }, $"已移出对比（共 {basket.Count} 款）"));
            }
            case "clear":
            {
                ClearCompareBasket(_module);
                UpdateCompareButton();
                RefreshCompareWindow();
                return Wrap(AgentResult.Good(new { count = 0 }, "对比清单已清空"));
            }
            case "open":
            {
                // 与 UI 按钮一致：任意模块凑够 2 款即可打开，不被当前模块钳制
                var perModule = new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc }
                    .Select(m => (Module: m, Count: _compareBasket[m].Count))
                    .Where(x => x.Count > 0)
                    .ToList();
                int total = perModule.Sum(x => x.Count);

                if (total < 2)
                    return Wrap(AgentResult.Fail($"对比清单不足 2 款（当前共 {total} 款）"));

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
                return Wrap(AgentResult.Good(
                    new
                    {
                        total,
                        perModule = perModule.Select(x => new { module = x.Module.ToString(), count = x.Count })
                    },
                    $"已打开对比窗（共 {total} 款：{string.Join("、", perModule.Select(x => SpecSchema.ModuleTitle(x.Module) + " " + x.Count))}）"));
            }
            default:
            {
                var items = basket.Select(r => new { id = r.Id, name = r.Name }).ToArray();
                return Wrap(AgentResult.Good(
                    new { module = _module.ToString(), count = basket.Count, items },
                    $"对比清单：{_module} 共 {basket.Count} 款"));
            }
        }
    }

    private AgentResult AgentModules()
    {
        var list = new object[]
        {
            new { key = "cpu", title = SpecSchema.ModuleTitle(ChipModule.Cpu),
                  count = _databases[ChipModule.Cpu].Count,
                  brands = _databases[ChipModule.Cpu].Brands.Select(b => b.Name).ToArray() },
            new { key = "gpu", title = SpecSchema.ModuleTitle(ChipModule.Gpu),
                  count = _databases[ChipModule.Gpu].Count,
                  brands = _databases[ChipModule.Gpu].Brands.Select(b => b.Name).ToArray() },
            new { key = "soc", title = SpecSchema.ModuleTitle(ChipModule.Soc),
                  count = _databases[ChipModule.Soc].Count,
                  brands = _databases[ChipModule.Soc].Brands.Select(b => b.Name).ToArray() }
        };
        return Wrap(AgentResult.Good(list, "读取模块列表"));
    }

    /// <summary>
    /// 分级导航：nav?module=gpu&amp;brand=NVIDIA&amp;category=dk&amp;product=GeForce&amp;serie=...
    /// 逐级下钻，参数可选；同时把界面切到对应位置。
    /// </summary>
    private AgentResult AgentNav(AgentRequest req)
    {
        var moduleArg = req.Get("module");
        if (!string.IsNullOrEmpty(moduleArg))
        {
            var m = moduleArg.ToLowerInvariant() switch
            {
                "gpu" => ChipModule.Gpu,
                "soc" => ChipModule.Soc,
                _ => ChipModule.Cpu
            };
            if (m != _module)
            {
                _module = m;
                NavCpu.IsChecked = m == ChipModule.Cpu;
                NavGpu.IsChecked = m == ChipModule.Gpu;
                NavSoc.IsChecked = m == ChipModule.Soc;
                RefreshModule(selectFirst: false);
            }
        }

        var brandName = req.Get("brand");
        if (!string.IsNullOrEmpty(brandName)) Nav.SelectByNames(brandName, null, null, null);

        var catArg = req.Get("category");
        if (!string.IsNullOrEmpty(catArg)) Nav.SelectByNames(null, catArg, null, null);

        var prodArg = req.Get("product");
        if (!string.IsNullOrEmpty(prodArg)) Nav.SelectByNames(null, null, prodArg, null);

        var serieArg = req.Get("serie");
        if (!string.IsNullOrEmpty(serieArg)) Nav.SelectByNames(null, null, null, serieArg);

        ApplyFilter(selectFirst: false);

        var db = _databases[_module];
        var data = new
        {
            module = _module.ToString(),
            brand = Nav.Brand?.Name,
            category = Nav.Category?.Name,
            product = Nav.ProductFilter?.Name,
            serie = Nav.Serie?.Name,
            matched = _items.Count,
            items = _items.Take(30).Select(v => new { id = v.Record.Id, name = v.Record.Name }).ToArray()
        };

        var crumbs = new[] { Nav.Brand?.Name, Nav.Category?.Name, Nav.ProductFilter?.Name, Nav.Serie?.Name }
            .Where(s => !string.IsNullOrEmpty(s));
        var ui = $"导航到 {SpecSchema.ModuleTitle(_module)} › {string.Join(" › ", crumbs)}（{_items.Count:N0} 条）";
        return Wrap(AgentResult.Good(data, ui));
    }

    private AgentResult AgentSearch(AgentRequest req)
    {
        var q = req.Get("q", "");
        SearchBox.Text = q;
        ApplyFilter(selectFirst: false);
        var data = new
        {
            query = q,
            module = _module.ToString(),
            matched = _items.Count,
            items = _items.Take(30).Select(v => new { id = v.Record.Id, name = v.Record.Name }).ToArray()
        };
        return Wrap(AgentResult.Good(data, $"搜索“{q}” → {_items.Count:N0} 条结果"));
    }

    /// <summary>选择某条记录：select?id=1234 或 select?name=EPYC 9966</summary>
    private AgentResult AgentSelect(AgentRequest req)
    {
        var id = req.Get("id");
        var name = req.Get("name");
        ChipItemViewModel target = null;

        if (!string.IsNullOrEmpty(id))
            target = _items.FirstOrDefault(v => v.Record.Id == id);
        if (target == null && !string.IsNullOrEmpty(name))
        {
            target = _items.FirstOrDefault(v =>
                v.Record.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        if (target == null)
        {
            // 当前筛选里没有，退一步到全库找
            var db = _databases[_module];
            var rec = !string.IsNullOrEmpty(id)
                ? db.Records.FirstOrDefault(r => r.Id == id)
                : db.Records.FirstOrDefault(r => r.Name.Contains(name ?? "", StringComparison.OrdinalIgnoreCase));
            if (rec != null)
            {
                _navReady = false;
                Nav.Load(_databases[_module]);
                _navReady = true;
                SearchBox.Text = "";
                ApplyFilter(selectFirst: false);
                target = _items.FirstOrDefault(v => v.Record.Id == rec.Id);
            }
        }

        if (target == null) return Wrap(AgentResult.Fail("未找到匹配的芯片"));

        ChipList.SelectedItem = target;
        ChipList.ScrollIntoView(target);
        return Wrap(AgentResult.Good(RecordPayload(target.Record), $"查看 {target.Record.Name}"));
    }

    private AgentResult AgentDetail(AgentRequest req)
    {
        var sel = AgentSelect(req);
        if (!sel.Ok) return sel;
        if (_selected == null) return Wrap(AgentResult.Fail("没有选中的芯片"));
        return Wrap(AgentResult.Good(RecordPayload(_selected.Record), $"详情：{_selected.Record.Name}"));
    }

    private AgentResult AgentCompare(AgentRequest req)
    {
        var a = req.Get("a");
        var b = req.Get("b");
        var dbAll = _databases.Values.SelectMany(d => d.Records).ToList();
        ChipRecord ra = dbAll.FirstOrDefault(r => r.Id == a)
                        ?? dbAll.FirstOrDefault(r => r.Name.Contains(a ?? "\0", StringComparison.OrdinalIgnoreCase));
        ChipRecord rb = dbAll.FirstOrDefault(r => r.Id == b)
                        ?? dbAll.FirstOrDefault(r => r.Name.Contains(b ?? "\0", StringComparison.OrdinalIgnoreCase));
        if (ra == null || rb == null) return Wrap(AgentResult.Fail("对比双方未找到"));

        var specs = SpecSchema.For(ra.Module);
        var rows = new List<object>();
        foreach (var sp in specs)
        {
            var lv = ValueFormat.Pretty(sp, ra.Get(sp.Key));
            var rv = ValueFormat.Pretty(sp, rb.Get(sp.Key));
            if (sp.Key == "launch") { lv = ValueFormat.Launch(lv); rv = ValueFormat.Launch(rv); }
            if (string.IsNullOrWhiteSpace(lv) && string.IsNullOrWhiteSpace(rv)) continue;
            rows.Add(new { label = sp.Label, left = lv, right = rv, different = lv != rv });
        }

        if (_compareBasket.TryGetValue(ra.Module, out var bk))
        {
            if (bk.All(r => r.Id != ra.Id)) bk.Add(ra);
            if (bk.All(r => r.Id != rb.Id)) bk.Add(rb);
        }
        ShowCompareWindow();
        return Wrap(AgentResult.Good(new
        {
            left = new { name = ra.Name, module = ra.Module.ToString() },
            right = new { name = rb.Name, module = rb.Module.ToString() },
            differences = rows
        }, $"对比 {ra.Name} ↔ {rb.Name}"));
    }

    /// <summary>触发一次真实的数据库热更新检查。</summary>
    private async void AgentUpdateAsync(AgentRequest req) { }

    private AgentResult AgentUpdate(AgentRequest req)
    {
        // 异步执行，先回执再更新，避免管道超时
        var interact = req.Get("interactive", "0") == "1";
        SetAgentBanner($"触发热更新检查…");
        _ = Task.Run(async () =>
        {
            try { await CheckUpdatesAsync(interactive: interact); }
            catch (Exception ex) { LogUiError("agent-update", ex); }
        });
        return Wrap(AgentResult.Good(new { started = true }, "触发热更新检查…"));
    }

    private AgentResult AgentHistory()
    {
        var items = _agentHistory.Select(h => new { at = h.At.ToString("HH:mm:ss"), text = h.Text }).ToArray();
        return Wrap(AgentResult.Good(items, $"最近 {items.Length} 次自动检查"));
    }

    /// <summary>界面内自检：校验三库解析与导航树结构，不写文件。</summary>
    private AgentResult AgentSelfTest()
    {
        var report = new List<object>();
        var problems = new List<string>();

        foreach (var m in new[] { ChipModule.Cpu, ChipModule.Gpu, ChipModule.Soc })
        {
            var db = _databases[m];
            int brands = db.Brands.Count;
            int cats = db.Brands.Sum(b => b.Categories.Count);
            int prods = db.Brands.Sum(b => b.Categories.Sum(c => c.Products.Count));
            int sers = db.Brands.Sum(b => b.Categories.Sum(c => c.Products.Sum(p => p.Series.Count)));
            int inTree = db.Brands.Sum(b => b.Categories.Sum(c => c.Products.Sum(p => p.Series.Sum(x => x.Records.Count))));
            int unconfirmed = db.Records.Count(r => r.IsUnconfirmed);

            if (inTree != db.Count)
                problems.Add($"{m}: 树内 {inTree} 条 ≠ 平铺 {db.Count} 条");

            var emptyName = db.Records.Count(r => string.IsNullOrWhiteSpace(r.Name));
            if (emptyName > 0) problems.Add($"{m}: {emptyName} 条无名称");

            report.Add(new
            {
                module = m.ToString(),
                records = db.Count,
                inTree,
                brands,
                categories = cats,
                products = prods,
                series = sers,
                unconfirmed,
                version = db.Version,
                source = db.Source
            });
        }

        return AgentResult.Good(new { passed = problems.Count == 0, problems, report },
            problems.Count == 0 ? "自检通过：三库结构完整" : $"自检发现 {problems.Count} 个问题");
    }

    private AgentResult AgentHelp()
    {
        var cmds = new[]
        {
            "GET /state                当前模块/筛选/选中项/计数",
            "GET /modules              三个库与品牌列表",
            "GET /nav?module=gpu&brand=NVIDIA&category=dk&product=GeForce&serie=...  逐级下钻",
            "GET /search?q=9966        搜索",
            "GET /select?id=1234      按 id 选中；或 select?name=EPYC 9966",
            "GET /detail?id=1234      选中并返回完整规格",
            "GET /compare?a=..&b=..   返回逐项差异（同时弹出对比窗）",
            "GET /update[&interactive=1]  触发热更新",
            "GET /history             自动检查记录",
            "GET /pick[?module=gpu]   骰子随机抽一款并定位导航",
            "GET /module?module=gpu   切换芯片类别",
            "GET /sort?by=name|newest|oldest  切换排序",
            "GET /filter?search=xxx   设置搜索词并返回筛选状态",
            "GET /cmpwin?action=open|close    控制对比窗口",
            "GET /clearcache[?module=cpu]     清空热更新缓存回内置库",
            "GET /status              当前模块/清单/收藏/数据库来源",
            "GET /selftest            运行内置自检",
            "所有请求需带 &token=<agent.json 里的 token>"
        };
        return Wrap(AgentResult.Good(cmds, "调试接口用法"));
    }

    private static object RecordPayload(ChipRecord r)
    {
        var specs = SpecSchema.For(r.Module);
        var fields = new List<object>();
        foreach (var sp in specs)
        {
            var v = ValueFormat.Pretty(sp, r.Get(sp.Key));
            if (string.IsNullOrWhiteSpace(v)) continue;
            // 发布时间做人类可读化（202602 → 2026 年 2 月）
            if (sp.Key == "launch") v = ValueFormat.Launch(v);
            fields.Add(new { key = sp.Key, label = sp.Label, value = v, section = sp.Section });
        }
        return new
        {
            id = r.Id,
            name = r.Name,
            module = r.Module.ToString(),
            brand = r.BrandFamily,
            category = ChipDatabase.CategoryLabel(r.Category, r.Module),
            product = r.ProductFamily,
            serie = r.SerieName,
            unconfirmed = r.IsUnconfirmed,
            fields
        };
    }

    // ---------- 界面上的可见反馈 ----------

    private readonly List<(DateTime At, string Text)> _agentHistory = new();

    private void SetAgentBanner(string text)
    {
        AgentBanner.Visibility = System.Windows.Visibility.Visible;
        AgentBannerText.Text = text;
        _agentHistory.Insert(0, (DateTime.Now, text));
        if (_agentHistory.Count > 50) _agentHistory.RemoveAt(_agentHistory.Count - 1);
    }
}
