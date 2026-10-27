using System.Collections.ObjectModel;
using ServiceLib.Handler.CloudflareBestIP;

namespace ServiceLib.ViewModels;

public partial class CloudflareBestIpViewModel : MyReactiveObject
{
    private CancellationTokenSource? _runCancellation;
    private CfProbeRunResult? _run;

    [Reactive] public partial string SniText { get; set; }
    [Reactive] public partial string TestPath { get; set; }
    [Reactive] public partial string SpeedPath { get; set; }
    [Reactive] public partial string DomainsUrl { get; set; }
    [Reactive] public partial string HistoricalUrls { get; set; }
    [Reactive] public partial string IpSetUrls { get; set; }
    [Reactive] public partial string PostUrls { get; set; }
    [Reactive] public partial string ProbeMode { get; set; }
    [Reactive] public partial int TopN { get; set; }
    [Reactive] public partial int LowestSpeed { get; set; }
    [Reactive] public partial int Timeout { get; set; }
    [Reactive] public partial int ProbeRepeat { get; set; }
    [Reactive] public partial int SpeedTestDuration { get; set; }
    [Reactive] public partial int SleepInterval { get; set; }
    [Reactive] public partial double WeightLatency { get; set; }
    [Reactive] public partial double WeightLoss { get; set; }
    [Reactive] public partial bool VerifyCertificate { get; set; }
    [Reactive] public partial string SelectedProtocol { get; set; }
    [Reactive] public partial string Credential { get; set; }
    [Reactive] public partial string WsPath { get; set; }
    [Reactive] public partial CfProfileTemplate? SelectedTemplate { get; set; }
    [Reactive] public partial bool IsRunning { get; set; }
    [Reactive] public partial bool IsBusy { get; set; }
    [Reactive] public partial bool Imported { get; set; }
    [Reactive] public partial double ProgressPercent { get; set; }
    [Reactive] public partial string StatusText { get; set; }

    public ObservableCollection<CfProfileTemplate> Templates { get; } = [];
    public ObservableCollection<CfProbeResult> Results { get; } = [];
    public string[] Modes { get; } = ["full", "edge"];
    public string[] Protocols { get; } = ["vless", "vmess", "trojan"];

    public CloudflareBestIpViewModel() : this(AppManager.Instance.Config) { }

    public CloudflareBestIpViewModel(Config config)
    {
        _config = config;
        var c = config.CfBestIpItem ?? new CfBestIpItem();
        SniText = string.Join(Environment.NewLine, c.OriginSniList ?? []);
        TestPath = c.OriginTestPath ?? "";
        SpeedPath = c.OriginSpeedTestPath ?? "";
        DomainsUrl = c.DomainsSetUrl ?? "";
        HistoricalUrls = string.Join(Environment.NewLine, c.HavePostRes ?? []);
        IpSetUrls = string.Join(Environment.NewLine, c.IpSetUrls ?? []);
        PostUrls = string.Join(Environment.NewLine, c.PostUrls ?? []);
        ProbeMode = c.ProbeMode;
        TopN = c.TopN;
        LowestSpeed = c.LowestSpeed;
        Timeout = c.Timeout;
        ProbeRepeat = c.ProbeRepeat;
        SpeedTestDuration = c.SpeedTestDuration;
        SleepInterval = c.SleepInterval;
        WeightLatency = c.WeightLatency;
        WeightLoss = c.WeightLoss;
        VerifyCertificate = c.OriginVerifyCert;
        SelectedProtocol = c.SelectedProtocol;
        Credential = c.Uuid;
        WsPath = c.WsPath;
        StatusText = CfUi.InitialStatus;
        Templates.Add(new CfProfileTemplate { DisplayName = CfUi.ManualTemplate, IndexId = "" });
        SelectedTemplate = Templates[0];
        _ = LoadTemplatesAsync(c.TemplateIndexId);
    }

    private async Task LoadTemplatesAsync(string? preferred)
    {
        try
        {
            var nodes = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
            foreach (var node in nodes.Where(n => n.StreamSecurity == Global.StreamSecurity
                && n.ConfigType is EConfigType.VLESS or EConfigType.VMess or EConfigType.Trojan && n.IsValid()))
                Templates.Add(new CfProfileTemplate { IndexId = node.IndexId,
                    DisplayName = $"{node.Remarks} · {node.ConfigType} · {node.Sni}" });
            SelectedTemplate = Templates.FirstOrDefault(t => t.IndexId == preferred) ?? Templates[0];
        }
        catch { SelectedTemplate = Templates[0]; }
    }

    private CfBestIpItem Snapshot()
    {
        var c = JsonUtils.DeepCopy(_config.CfBestIpItem) ?? new CfBestIpItem();
        c.OriginSniList = CfProbeValidation.Lines(SniText);
        c.OriginTestPath = TestPath.Trim();
        c.OriginSpeedTestPath = SpeedPath.Trim();
        c.DomainsSetUrl = DomainsUrl.Trim();
        c.HavePostRes = CfProbeValidation.Lines(HistoricalUrls);
        c.IpSetUrls = CfProbeValidation.Lines(IpSetUrls);
        c.PostUrls = CfProbeValidation.Lines(PostUrls);
        c.ProbeMode = ProbeMode;
        c.TopN = TopN;
        c.LowestSpeed = LowestSpeed;
        c.Timeout = Timeout;
        c.ProbeRepeat = ProbeRepeat;
        c.SpeedTestDuration = SpeedTestDuration;
        c.SleepInterval = SleepInterval;
        c.WeightLatency = WeightLatency;
        c.WeightLoss = WeightLoss;
        c.OriginVerifyCert = VerifyCertificate;
        c.SelectedProtocol = SelectedProtocol;
        c.Uuid = Credential.Trim();
        c.WsPath = WsPath.Trim();
        c.TemplateIndexId = SelectedTemplate?.IndexId ?? "";
        return c;
    }

    public async Task SaveAsync()
    {
        if (IsBusy) return;
        var snapshot = Snapshot();
        if (snapshot.HavePostRes.Concat(snapshot.IpSetUrls).Concat(snapshot.PostUrls)
            .Any(u => !CfProbeValidation.IsHttpUrl(u))
            || (!string.IsNullOrWhiteSpace(snapshot.DomainsSetUrl)
                && !CfProbeValidation.IsHttpUrl(snapshot.DomainsSetUrl)))
        {
            StatusText = CfUi.T("请检查数据源 URL；只接受 HTTP(S) 地址。", "Check source URLs; only HTTP(S) is accepted.");
            return;
        }
        _config.CfBestIpItem = snapshot;
        StatusText = await ConfigHandler.SaveConfig(_config) == 0
            ? CfUi.T("配置已保存。", "Settings saved.") : CfUi.T("配置保存失败。", "Could not save settings.");
    }

    public async Task StartAsync()
    {
        if (IsBusy) return;
        var settings = Snapshot();
        var error = CfProbeValidation.Validate(settings);
        if (error is not null) { StatusText = error; return; }
        _config.CfBestIpItem = settings;
        if (await ConfigHandler.SaveConfig(_config) != 0) { StatusText = CfUi.T("配置保存失败。", "Could not save settings."); return; }
        IsRunning = true;
        IsBusy = true;
        Imported = false;
        _run = null;
        Results.Clear();
        ProgressPercent = 0;
        _runCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CfProbeProgress>(p =>
            {
                StatusText = $"{p.Message} · {p.Completed}/{p.Total} · {CfUi.T("达标", "qualified")} {p.Qualified}";
                ProgressPercent = p.Percent;
            });
            _run = await CfBestIpHandler.Instance.RunAsync(_config, progress, _runCancellation.Token);
            foreach (var row in _run.Results.OrderByDescending(r => r.DownloadSpeedKBs)
                .ThenBy(r => r.AvgLatencyMs)) Results.Add(row);
            StatusText = CfUi.T($"测速完成：{_run.TopResults.Count} 个达标，共 {Results.Count} 个有效响应。",
                $"Probe complete: {_run.TopResults.Count} qualified, {Results.Count} valid responses.");
            ProgressPercent = 100;
        }
        catch (OperationCanceledException) { StatusText = CfUi.T("测速已停止。", "Probe stopped."); }
        catch (Exception ex) { StatusText = CfUi.T($"测速失败：{ex.Message}", $"Probe failed: {ex.Message}"); Logging.SaveLog("CloudflareBestIp", ex); }
        finally { _runCancellation?.Dispose(); _runCancellation = null; IsRunning = false; IsBusy = false; }
    }

    public void Stop() => _runCancellation?.Cancel();

    public async Task ImportAsync()
    {
        if (IsBusy || _run is null || _run.TopResults.Count == 0) { StatusText = CfUi.T("请先完成测速并取得达标结果。", "Complete a probe with qualified results first."); return; }
        IsBusy = true;
        try
        {
            var settings = Snapshot();
            ProfileItem? template = null;
            if (!string.IsNullOrWhiteSpace(SelectedTemplate?.IndexId))
                template = await AppManager.Instance.GetProfileItem(SelectedTemplate.IndexId);
            if (!string.IsNullOrWhiteSpace(SelectedTemplate?.IndexId) && template is null)
                throw new ArgumentException(CfUi.T("选中的模板节点已不存在。", "The selected template no longer exists."));
            var added = await new CfResultExporter(settings).ImportAsync(_config, _run, template);
            Imported = true;
            StatusText = CfUi.T($"已导入 {added} 个新节点；重复节点已跳过。",
                $"Imported {added} new profiles; duplicates were skipped.");
        }
        catch (Exception ex) { StatusText = CfUi.T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    public async Task PostAsync()
    {
        if (IsBusy || _run is null || _run.TopResults.Count == 0) { StatusText = CfUi.T("请先完成测速。", "Complete a probe first."); return; }
        var settings = Snapshot();
        if (settings.PostUrls.Count == 0) { StatusText = CfUi.T("请先填写自己的上报地址。", "Enter your own report URL first."); return; }
        IsBusy = true;
        try
        {
            await CfBestIpHandler.Instance.PostResultsAsync(_run, settings);
            StatusText = CfUi.T($"已上报到 {settings.PostUrls.Count} 个地址。", $"Reported to {settings.PostUrls.Count} URL(s).");
        }
        catch (Exception ex) { StatusText = CfUi.T($"上报失败：{ex.Message}", $"Report failed: {ex.Message}"); }
        finally { IsBusy = false; }
    }
}
