using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Windows.Data;
using VoSharp.Kernel.Pool;
using VoWin.Helpers;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.ViewModels.Pages
{
    public class EgressOptionModel
    {
        public string? NodeId { get; }
        public string DisplayName { get; }
        public bool IsDirect => string.IsNullOrEmpty(NodeId);

        public EgressOptionModel(string? nodeId, string displayName)
        {
            NodeId = nodeId;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }

    public partial class ProxyViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;

        public ProxyViewModel ViewModel => this;
        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;
        public ObservableCollection<ProxyNodeModel> ProxyPresets => _kernelService.ProxyPresets;
        public ObservableCollection<ProxyNodeModel> Presets => ProxyPresets;
        public ObservableCollection<CountryRouteModel> CountryRoutes => _kernelService.CountryRoutes;

        private readonly List<CountryMeta> _allKnownCountries;
        public ObservableCollection<CountryMeta> FilteredAvailableCountries { get; } = new();
        public ObservableCollection<EgressOptionModel> AvailableEgressOptions { get; } = new();

        private readonly ICollectionView? _filteredCountryRoutesView;
        public ICollectionView? FilteredCountryRoutes => _filteredCountryRoutesView;

        [ObservableProperty]
        private CountryMeta? _selectedCountryToAdd;

        [ObservableProperty]
        private string _countrySearchQuery = string.Empty;

        [ObservableProperty]
        private EgressOptionModel? _quickAddSelectedEgress;

        [ObservableProperty]
        private string _quickAddHintText = "请在上方下拉列表中选择国家/地区，或直接输入国家名/代码进行搜索。";

        [ObservableProperty]
        private string _rulesSearchQuery = string.Empty;

        // ================= Modal Edit Dialog State =================
        [ObservableProperty]
        private bool _isEditingRoute;

        [ObservableProperty]
        private CountryRouteModel? _editingRoute;

        [ObservableProperty]
        private bool _editDialogIsDirect = true;

        [ObservableProperty]
        private bool _editDialogIsProxy;

        [ObservableProperty]
        private ProxyNodeModel? _editDialogSelectedNode;

        [ObservableProperty]
        private CountryRouteModel? _selectedCountryRoute;

        [ObservableProperty]
        private ProxyNodeModel? _selectedNodeForCountry;

        [ObservableProperty]
        private bool _isDirectRoute = true;

        [ObservableProperty]
        private bool _isProxyRoute;

        [ObservableProperty]
        private bool _hasRouteChanges;

        [ObservableProperty]
        private string _newCountryCode = string.Empty;

        [ObservableProperty]
        private string _detectedCountryHint = "请输入 2 位 ISO 国家代码 (例如: DE, FR, JP, US, CA, AU)";

        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        [ObservableProperty]
        private ModemSlot? _selectedSlotForBind;

        [ObservableProperty]
        private ProxyNodeModel? _selectedPreset;

        [ObservableProperty]
        private string _newPresetUrl = "socks5://127.0.0.1:1080";

        [ObservableProperty]
        private string _newPresetName = "新代理节点";

        [ObservableProperty]
        private string _newPresetProtocol = "socks5";

        [ObservableProperty]
        private string _newPresetHost = "127.0.0.1";

        [ObservableProperty]
        private int _newPresetPort = 1080;

        [ObservableProperty]
        private string _newPresetUsername = string.Empty;

        [ObservableProperty]
        private string _newPresetPassword = string.Empty;

        [ObservableProperty]
        private string _customProxyUrl = string.Empty;

        [ObservableProperty]
        private bool _isTesting;

        [ObservableProperty]
        private string _statusMessage = "代理配置就绪";

        public string RulesSummaryText
        {
            get
            {
                int total = CountryRoutes.Count;
                int direct = CountryRoutes.Count(r => r.IsDirect);
                int proxy = total - direct;
                return $"{total} 条分流规则 · {direct} 条直连 · {proxy} 条节点代理";
            }
        }

        public string NodePoolSummaryText
        {
            get
            {
                int total = ProxyPresets.Count;
                int online = ProxyPresets.Count(p => p.LatencyMs.HasValue && p.LatencyMs.Value > 0);
                int untested = ProxyPresets.Count(p => !p.LatencyMs.HasValue);
                return $"已配置 {total} 个代理节点 · {online} 个已连通 · {untested} 个待测速";
            }
        }

        public ProxyViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();
            SelectedPreset = ProxyPresets.FirstOrDefault();
            SelectedCountryRoute = CountryRoutes.FirstOrDefault();

            if (SelectedSlot != null && !string.IsNullOrEmpty(SelectedSlot.ProxyUrl))
            {
                CustomProxyUrl = SelectedSlot.ProxyUrl;
            }

            SyncCountryRouteState(SelectedCountryRoute);

            _allKnownCountries = MccCountryHelper.GetAllKnownCountries().ToList();
            UpdateFilteredCountries(string.Empty);
            SelectedCountryToAdd = FilteredAvailableCountries.FirstOrDefault();

            _filteredCountryRoutesView = CollectionViewSource.GetDefaultView(CountryRoutes);
            if (_filteredCountryRoutesView != null)
            {
                _filteredCountryRoutesView.Filter = FilterCountryRoute;
            }

            RefreshEgressOptions();
            QuickAddSelectedEgress = AvailableEgressOptions.FirstOrDefault();

            CountryRoutes.CollectionChanged += (s, e) =>
            {
                UpdateSummaries();
                _filteredCountryRoutesView?.Refresh();
            };

            ProxyPresets.CollectionChanged += (s, e) =>
            {
                RefreshEgressOptions();
                UpdateSummaries();
            };
        }

        public void UpdateSummaries()
        {
            OnPropertyChanged(nameof(RulesSummaryText));
            OnPropertyChanged(nameof(NodePoolSummaryText));
        }

        private bool _isSyncingRouteState;

        partial void OnSelectedCountryRouteChanged(CountryRouteModel? value)
        {
            SyncCountryRouteState(value);
        }

        private void SyncCountryRouteState(CountryRouteModel? route)
        {
            _isSyncingRouteState = true;
            try
            {
                if (route == null)
                {
                    IsDirectRoute = true;
                    IsProxyRoute = false;
                    SelectedNodeForCountry = null;
                }
                else
                {
                    if (route.IsDirect)
                    {
                        IsDirectRoute = true;
                        IsProxyRoute = false;
                        SelectedNodeForCountry = ProxyPresets.FirstOrDefault();
                    }
                    else
                    {
                        IsDirectRoute = false;
                        IsProxyRoute = true;
                        SelectedNodeForCountry = ProxyPresets.FirstOrDefault(p => p.Id == route.ProxyNodeId) ?? ProxyPresets.FirstOrDefault();
                    }
                }
                HasRouteChanges = false;
            }
            finally
            {
                _isSyncingRouteState = false;
            }
        }

        partial void OnIsDirectRouteChanged(bool value)
        {
            if (_isSyncingRouteState) return;
            if (value)
            {
                IsProxyRoute = false;
            }
            CheckRouteChanges();
        }

        partial void OnIsProxyRouteChanged(bool value)
        {
            if (_isSyncingRouteState) return;
            if (value)
            {
                IsDirectRoute = false;
                if (SelectedNodeForCountry == null)
                {
                    SelectedNodeForCountry = ProxyPresets.FirstOrDefault();
                }
            }
            CheckRouteChanges();
        }

        partial void OnSelectedNodeForCountryChanged(ProxyNodeModel? value)
        {
            if (_isSyncingRouteState) return;
            CheckRouteChanges();
        }

        private void CheckRouteChanges()
        {
            if (SelectedCountryRoute == null)
            {
                HasRouteChanges = false;
                return;
            }

            if (IsDirectRoute)
            {
                HasRouteChanges = !SelectedCountryRoute.IsDirect;
            }
            else
            {
                HasRouteChanges = SelectedCountryRoute.IsDirect || SelectedCountryRoute.ProxyNodeId != SelectedNodeForCountry?.Id;
            }
        }

        partial void OnNewCountryCodeChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                DetectedCountryHint = "请输入 2 位 ISO 国家代码 (例如: DE, FR, JP, US, CA, AU)";
                return;
            }

            var code = value.Trim().ToUpperInvariant();
            var meta = Helpers.MccCountryHelper.FindByCode(code);
            if (meta == null)
            {
                DetectedCountryHint = $"未在数据库中找到代码 [{code}] 对应的国家";
            }
            else
            {
                bool exists = CountryRoutes.Any(r => string.Equals(r.CountryCode, meta.Code, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    DetectedCountryHint = $"{meta.Flag} {meta.Name} ({meta.Code}) · 已存在分流规则";
                }
                else
                {
                    DetectedCountryHint = $"{meta.Flag} {meta.Name} ({meta.Code}) · MCC: {string.Join(", ", meta.Mccs)} · 可添加";
                }
            }
        }

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            if (value != null && !string.IsNullOrEmpty(value.ProxyUrl))
            {
                CustomProxyUrl = value.ProxyUrl;
            }
        }

        [RelayCommand]
        private void SaveCountryRouteChanges()
        {
            if (SelectedCountryRoute == null) return;

            if (IsDirectRoute || SelectedNodeForCountry == null)
            {
                _kernelService.SaveCountryRoute(SelectedCountryRoute.CountryCode, null);
                SelectedCountryRoute.ProxyNodeId = null;
                SelectedCountryRoute.ProxyNodeName = "直连模式 (Direct)";
                StatusMessage = $"已将 [{SelectedCountryRoute.CountryName}] 设置为直连模式。";
            }
            else
            {
                _kernelService.SaveCountryRoute(SelectedCountryRoute.CountryCode, SelectedNodeForCountry.Id);
                SelectedCountryRoute.ProxyNodeId = SelectedNodeForCountry.Id;
                SelectedCountryRoute.ProxyNodeName = SelectedNodeForCountry.Name;
                StatusMessage = $"已将 [{SelectedCountryRoute.CountryName}] 绑定出站代理至 [{SelectedNodeForCountry.Name}]。";
            }

            HasRouteChanges = false;
            UpdateSummaries();
        }

        [RelayCommand]
        private void AddCountryRule()
        {
            if (string.IsNullOrWhiteSpace(NewCountryCode))
            {
                StatusMessage = "请输入 2 位国家代码 (例如: DE, FR, AU, CA, MY)...";
                return;
            }

            var meta = Helpers.MccCountryHelper.FindByCode(NewCountryCode.Trim());
            if (meta == null)
            {
                StatusMessage = $"未找到代码 [{NewCountryCode}] 的国家电信元数据。";
                return;
            }

            if (CountryRoutes.Any(r => string.Equals(r.CountryCode, meta.Code, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"规则 [{meta.Name}] 已存在，请直接在列表中查看。";
                SelectedCountryRoute = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, meta.Code, StringComparison.OrdinalIgnoreCase));
                return;
            }

            var targetNodeId = IsProxyRoute ? SelectedNodeForCountry?.Id : null;
            _kernelService.SaveCountryRoute(meta.Code, targetNodeId);
            NewCountryCode = string.Empty;
            SelectedCountryRoute = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, meta.Code, StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"成功添加 [{meta.Name}] 国家分流规则！";
            UpdateSummaries();
        }

        public void RefreshEgressOptions()
        {
            var prevSelectedId = QuickAddSelectedEgress?.NodeId;
            AvailableEgressOptions.Clear();
            AvailableEgressOptions.Add(new EgressOptionModel(null, "直连模式 (DIRECT)"));
            foreach (var preset in ProxyPresets)
            {
                AvailableEgressOptions.Add(new EgressOptionModel(preset.Id, $"代理 · {preset.Name}"));
            }

            QuickAddSelectedEgress = AvailableEgressOptions.FirstOrDefault(o => o.NodeId == prevSelectedId)
                                     ?? AvailableEgressOptions.FirstOrDefault();
        }

        private bool FilterCountryRoute(object item)
        {
            if (item is not CountryRouteModel route) return false;
            if (string.IsNullOrWhiteSpace(RulesSearchQuery)) return true;

            var q = RulesSearchQuery.Trim();
            return route.CountryName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   route.CountryCode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   route.ProxyNodeName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   route.MccDisplay.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        partial void OnRulesSearchQueryChanged(string value)
        {
            _filteredCountryRoutesView?.Refresh();
            OnPropertyChanged(nameof(RulesSummaryText));
        }

        partial void OnCountrySearchQueryChanged(string value)
        {
            UpdateFilteredCountries(value);
        }

        private void UpdateFilteredCountries(string? query)
        {
            FilteredAvailableCountries.Clear();
            var q = query?.Trim().ToLowerInvariant() ?? string.Empty;
            foreach (var country in _allKnownCountries)
            {
                if (string.IsNullOrEmpty(q) ||
                    country.Name.ToLowerInvariant().Contains(q) ||
                    country.Code.ToLowerInvariant().Contains(q) ||
                    country.Mccs.Any(m => m.Contains(q)))
                {
                    FilteredAvailableCountries.Add(country);
                }
            }

            if (!string.IsNullOrEmpty(q))
            {
                var exact = FilteredAvailableCountries.FirstOrDefault(c =>
                    string.Equals(c.Code, q, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    SelectedCountryToAdd = exact;
                }
            }

            UpdateQuickAddHint();
        }

        partial void OnSelectedCountryToAddChanged(CountryMeta? value)
        {
            UpdateQuickAddHint();
        }

        private void UpdateQuickAddHint()
        {
            if (SelectedCountryToAdd == null)
            {
                QuickAddHintText = "请在上方下拉列表中选择国家/地区，或直接输入国家名/代码进行搜索。";
                return;
            }

            bool exists = CountryRoutes.Any(r => string.Equals(r.CountryCode, SelectedCountryToAdd.Code, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                QuickAddHintText = $"{SelectedCountryToAdd.Flag} {SelectedCountryToAdd.Name} ({SelectedCountryToAdd.Code}) · MCC: {string.Join(", ", SelectedCountryToAdd.Mccs)} · ⚠️ 该国家规则已存在 (可直接在列表中编辑)";
            }
            else
            {
                QuickAddHintText = $"{SelectedCountryToAdd.Flag} {SelectedCountryToAdd.Name} ({SelectedCountryToAdd.Code}) · 自动关联 MCC: {string.Join(", ", SelectedCountryToAdd.Mccs)} · ✓ 点击右侧按钮即可添加";
            }
        }

        partial void OnEditDialogIsDirectChanged(bool value)
        {
            if (_isSyncingRouteState) return;
            if (value)
            {
                EditDialogIsProxy = false;
            }
        }

        partial void OnEditDialogIsProxyChanged(bool value)
        {
            if (_isSyncingRouteState) return;
            if (value)
            {
                EditDialogIsDirect = false;
                if (EditDialogSelectedNode == null)
                {
                    EditDialogSelectedNode = ProxyPresets.FirstOrDefault();
                }
            }
        }

        [RelayCommand]
        private void OpenEditDialog(CountryRouteModel? route)
        {
            var target = route ?? SelectedCountryRoute;
            if (target == null) return;

            EditingRoute = target;
            SelectedCountryRoute = target;

            _isSyncingRouteState = true;
            try
            {
                if (target.IsDirect)
                {
                    EditDialogIsDirect = true;
                    EditDialogIsProxy = false;
                    EditDialogSelectedNode = ProxyPresets.FirstOrDefault();
                }
                else
                {
                    EditDialogIsDirect = false;
                    EditDialogIsProxy = true;
                    EditDialogSelectedNode = ProxyPresets.FirstOrDefault(p => p.Id == target.ProxyNodeId) ?? ProxyPresets.FirstOrDefault();
                }
            }
            finally
            {
                _isSyncingRouteState = false;
            }

            IsEditingRoute = true;
        }

        [RelayCommand]
        private void CloseEditDialog()
        {
            IsEditingRoute = false;
            EditingRoute = null;
        }

        [RelayCommand]
        private void SaveEditDialog()
        {
            if (EditingRoute == null)
            {
                IsEditingRoute = false;
                return;
            }

            if (EditDialogIsDirect || EditDialogSelectedNode == null)
            {
                _kernelService.SaveCountryRoute(EditingRoute.CountryCode, null);
                EditingRoute.ProxyNodeId = null;
                EditingRoute.ProxyNodeName = "直连模式 (Direct)";
                StatusMessage = $"已将 [{EditingRoute.CountryName}] 设置为直连模式。";
            }
            else
            {
                _kernelService.SaveCountryRoute(EditingRoute.CountryCode, EditDialogSelectedNode.Id);
                EditingRoute.ProxyNodeId = EditDialogSelectedNode.Id;
                EditingRoute.ProxyNodeName = EditDialogSelectedNode.Name;
                StatusMessage = $"已将 [{EditingRoute.CountryName}] 绑定出站代理至 [{EditDialogSelectedNode.Name}]。";
            }

            IsEditingRoute = false;
            UpdateSummaries();
            _filteredCountryRoutesView?.Refresh();
        }

        [RelayCommand]
        private void QuickAddCountryRule()
        {
            var target = SelectedCountryToAdd;
            if (target == null && !string.IsNullOrWhiteSpace(CountrySearchQuery))
            {
                target = MccCountryHelper.FindByCode(CountrySearchQuery.Trim()) ??
                         _allKnownCountries.FirstOrDefault(c => c.Name.Equals(CountrySearchQuery.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (target == null)
            {
                StatusMessage = "请先选择或搜索要添加的国家/地区。";
                return;
            }

            if (CountryRoutes.Any(r => string.Equals(r.CountryCode, target.Code, StringComparison.OrdinalIgnoreCase)))
            {
                var existing = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, target.Code, StringComparison.OrdinalIgnoreCase));
                SelectedCountryRoute = existing;
                StatusMessage = $"[{target.Name}] 分流规则已存在，已为您高亮定位。";
                return;
            }

            string? targetNodeId = QuickAddSelectedEgress?.IsDirect == false ? QuickAddSelectedEgress.NodeId : null;
            _kernelService.SaveCountryRoute(target.Code, targetNodeId);

            var newRoute = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, target.Code, StringComparison.OrdinalIgnoreCase));
            if (newRoute != null)
            {
                SelectedCountryRoute = newRoute;
            }

            string egressTitle = QuickAddSelectedEgress?.DisplayName ?? "直连模式";
            StatusMessage = $"已成功添加 [{target.Flag} {target.Name} ({target.Code})] 出站分流规则（{egressTitle}）！";

            CountrySearchQuery = string.Empty;
            UpdateFilteredCountries(string.Empty);
            UpdateSummaries();
            _filteredCountryRoutesView?.Refresh();
        }

        [RelayCommand]
        private async Task DeleteCountryRuleAsync(CountryRouteModel? route = null)
        {
            var target = route ?? EditingRoute ?? SelectedCountryRoute;
            if (target == null)
            {
                StatusMessage = "请先在列表中选择要删除的国家分流规则。";
                return;
            }

            var uiMessageBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "确认删除分流规则",
                Content = $"确定要删除国家分流规则 [{target.CountryName} ({target.CountryCode})] 吗？\n删除后该国家/地区出站将自动回退到直连模式。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消"
            };

            var result = await uiMessageBox.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                return;
            }

            _kernelService.RemoveCountryRoute(target.CountryCode);
            StatusMessage = $"已删除国家分流规则 [{target.CountryName}] ({target.CountryCode})。";

            if (EditingRoute == target)
            {
                IsEditingRoute = false;
                EditingRoute = null;
            }

            if (SelectedCountryRoute == target)
            {
                SelectedCountryRoute = CountryRoutes.FirstOrDefault();
            }

            UpdateSummaries();
            _filteredCountryRoutesView?.Refresh();
        }

        // Compatibility aliases
        [RelayCommand]
        private void BindNodeToCountry(CountryRouteModel? route) => SaveCountryRouteChanges();

        [RelayCommand]
        private void SetCountryDirect(CountryRouteModel? route)
        {
            IsDirectRoute = true;
            SaveCountryRouteChanges();
        }

        [RelayCommand]
        private void ApplyPresetToSlot(ProxyNodeModel? preset)
        {
            var p = preset ?? SelectedPreset;
            if (SelectedSlot == null || p == null)
            {
                StatusMessage = "请先选择目标卡槽和代理节点。";
                return;
            }

            var url = p.ToProxyUrl();
            bool ok = _kernelService.SetSlotProxy(SelectedSlot.Id, url);
            if (ok)
            {
                SelectedSlot.ProxyUrl = url;
                CustomProxyUrl = url;
                StatusMessage = $"已将代理 [{p.Name}] 应用至卡槽 [{SelectedSlot.Name}]。";
            }
        }

        [RelayCommand]
        private void ApplyPresetToAllSlots(ProxyNodeModel? preset)
        {
            var p = preset ?? SelectedPreset;
            if (p == null) return;

            var url = p.ToProxyUrl();
            foreach (var slot in Slots)
            {
                _kernelService.SetSlotProxy(slot.Id, url);
                slot.ProxyUrl = url;
            }
            StatusMessage = $"已将代理 [{p.Name}] 应用至全部已登记卡槽。";
        }

        [RelayCommand]
        private void ClearSlotProxy(ModemSlot? slot)
        {
            var target = slot ?? SelectedSlot;
            if (target == null) return;

            bool ok = _kernelService.SetSlotProxy(target.Id, null);
            if (ok)
            {
                target.ProxyUrl = null;
                CustomProxyUrl = string.Empty;
                StatusMessage = $"卡槽 [{target.Name}] 代理已清空，恢复直连。";
            }
        }

        [RelayCommand]
        private void SaveCustomProxy()
        {
            if (SelectedSlot == null) return;

            var url = string.IsNullOrWhiteSpace(CustomProxyUrl) ? null : CustomProxyUrl.Trim();
            bool ok = _kernelService.SetSlotProxy(SelectedSlot.Id, url);
            if (ok)
            {
                SelectedSlot.ProxyUrl = url;
                StatusMessage = $"卡槽 [{SelectedSlot.Name}] 代理设置已更新。";
            }
        }

        [RelayCommand]
        private void AddPreset()
        {
            if (string.IsNullOrWhiteSpace(NewPresetUrl)) return;
            var node = ProxyNodeModel.FromUrl(NewPresetUrl.Trim());
            _kernelService.AddProxyPreset(node);
            NewPresetUrl = string.Empty;
            SelectedPreset = node;
            StatusMessage = $"已添加节点 [{node.Name}]。";
            UpdateSummaries();
        }

        [RelayCommand]
        private async Task DeletePresetAsync(ProxyNodeModel? preset)
        {
            var p = preset ?? SelectedPreset;
            if (p == null) return;

            var uiMessageBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "确认移除代理节点",
                Content = $"确定要从代理池中移除节点 [{p.Name}] ({p.Url}) 吗？\n若有国家规则引用此节点，将自动回退为直连。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消"
            };

            var result = await uiMessageBox.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            _kernelService.RemoveProxyPreset(p.Id);
            SelectedPreset = ProxyPresets.FirstOrDefault();
            StatusMessage = $"已删除代理节点 [{p.Name}]。";
            UpdateSummaries();
        }

        [RelayCommand]
        private void BindPresetToSlot()
        {
            var slot = SelectedSlotForBind ?? SelectedSlot;
            if (slot == null || SelectedPreset == null)
            {
                StatusMessage = "请先选择目标卡槽和代理预设。";
                return;
            }
            ApplyPresetToSlot(SelectedPreset);
        }

        [RelayCommand]
        private async Task TestSlotProxyAsync(ModemSlot? slot)
        {
            var targetSlot = slot ?? SelectedSlot;
            if (IsTesting || targetSlot == null || string.IsNullOrWhiteSpace(targetSlot.ProxyUrl))
            {
                StatusMessage = "未指定代理或卡槽。";
                return;
            }

            IsTesting = true;
            StatusMessage = $"正在测速卡槽 [{targetSlot.Name}] 的代理...";
            try
            {
                var fakeNode = ProxyNodeModel.FromUrl(targetSlot.ProxyUrl);
                var rtt = await _kernelService.TestProxyConnectivityAsync(fakeNode);
                targetSlot.LastTestResult = rtt > 0 ? $"{rtt} ms" : "超时/不可达";
                StatusMessage = $"卡槽 [{targetSlot.Name}] 代理测试完成: {targetSlot.LastTestResult}";
            }
            catch (Exception ex)
            {
                targetSlot.LastTestResult = "失败";
                StatusMessage = $"测速失败: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        }

        [RelayCommand]
        private async Task TestPresetLatencyAsync(ProxyNodeModel? preset)
        {
            var p = preset ?? SelectedPreset;
            if (p == null || IsTesting) return;

            IsTesting = true;
            StatusMessage = $"正在测试代理 {p.Name} ({p.Host}:{p.Port}) 连通性...";

            try
            {
                int ms = await _kernelService.TestProxyConnectivityAsync(p);
                StatusMessage = ms >= 0
                    ? $"代理 [{p.Name}] 连通正常，握手延迟: {ms} ms。"
                    : $"代理 [{p.Name}] 连接超时或失败。";
                UpdateSummaries();
            }
            catch (Exception ex)
            {
                StatusMessage = $"代理测试失败: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        }

        [RelayCommand]
        private async Task TestAllPresetsAsync()
        {
            if (IsTesting) return;
            IsTesting = true;
            StatusMessage = "正在批量测速所有预设代理节点...";

            try
            {
                var presets = ProxyPresets.ToArray();
                using var concurrency = new SemaphoreSlim(4);
                await Task.WhenAll(presets.Select(async preset =>
                {
                    await concurrency.WaitAsync();
                    try
                    {
                        await _kernelService.TestProxyConnectivityAsync(preset);
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }));
                StatusMessage = "全部预设节点测速完毕。";
                UpdateSummaries();
            }
            catch (Exception ex)
            {
                StatusMessage = $"批量测速未完成: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        }

        [RelayCommand]
        private void AddNewPreset()
        {
            if (string.IsNullOrWhiteSpace(NewPresetHost) || NewPresetPort <= 0)
            {
                StatusMessage = "请填写完整的主机地址与有效端口。";
                return;
            }

            var node = new ProxyNodeModel
            {
                Name = string.IsNullOrWhiteSpace(NewPresetName) ? $"{NewPresetHost}:{NewPresetPort}" : NewPresetName.Trim(),
                Protocol = NewPresetProtocol,
                Host = NewPresetHost.Trim(),
                Port = NewPresetPort,
                Username = string.IsNullOrWhiteSpace(NewPresetUsername) ? null : NewPresetUsername.Trim(),
                Password = string.IsNullOrWhiteSpace(NewPresetPassword) ? null : NewPresetPassword.Trim()
            };

            _kernelService.AddProxyPreset(node);
            SelectedPreset = node;
            StatusMessage = $"新代理预设 [{node.Name}] 已添加。";
            UpdateSummaries();
        }

        [RelayCommand]
        private async Task RemovePresetAsync(ProxyNodeModel? preset) => await DeletePresetAsync(preset);
    }
}
