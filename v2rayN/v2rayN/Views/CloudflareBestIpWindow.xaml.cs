namespace v2rayN.Views;

public partial class CloudflareBestIpWindow : v2rayN.Base.WindowBase<CloudflareBestIpViewModel>
{
    public CloudflareBestIpWindow()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
        btnSave.Click += async (_, _) => { if (ViewModel is not null) await ViewModel.SaveAsync(); };
        btnStart.Click += async (_, _) => { if (ViewModel is not null) await ViewModel.StartAsync(); };
        btnStop.Click += (_, _) => ViewModel?.Stop();
        btnPost.Click += async (_, _) => { if (ViewModel is not null) await ViewModel.PostAsync(); };
        btnClose.Click += (_, _) => Close();
        Closed += (_, _) => ViewModel?.Stop();
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }
}
