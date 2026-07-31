using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class AutoModeSourcesWindow : WindowBase<AutoModeSourcesViewModel>
{
    public AutoModeSourcesWindow()
    {
        InitializeComponent();

        ViewModel ??= new AutoModeSourcesViewModel();

        btnClose.Click += BtnClose_Click;
        btnGuide.Click += (_, _) => guideOverlay.IsVisible = true;
        btnGuideClose.Click += (_, _) => guideOverlay.IsVisible = false;
        KeyDown += AutoModeSourcesWindow_KeyDown;

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.SourceText, v => v.txtSources.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ProtocolChoices, v => v.lstProtocolChoices.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CountryChoices, v => v.lstCountryChoices.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ProtocolSummary, v => v.txtProtocolSummary.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CountrySummary, v => v.txtCountrySummary.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Summary, v => v.txtSummary.Text).DisposeWith(disposables);
            // NumericUpDown carries a nullable decimal, so the two directions need
            // explicit conversions rather than the plain two-way bind.
            this.Bind(ViewModel, vm => vm.TopCount, v => v.numTopCount.Value,
                    count => (decimal?)count,
                    value => (int)(value ?? 10))
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportFileCmd, v => v.btnImportFile).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ResetStatsCmd, v => v.btnResetStats).DisposeWith(disposables);

            ViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
            {
                var result = await UI.ShowYesNo(interaction.Input);
                interaction.SetOutput(result == ButtonResult.Yes);
            }).DisposeWith(disposables);

            ViewModel.BrowseFileInteraction.RegisterHandler(async interaction =>
            {
                var file = await UI.OpenFileDialog(null);
                interaction.SetOutput(file);
            }).DisposeWith(disposables);
        });
    }

    private async void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        // The Use tick boxes edit the sources in place, so flush them on the way out.
        if (ViewModel is not null)
        {
            await ViewModel.PersistEnabledFlags();
        }
        Close(ViewModel?.IsModified);
    }

    private void AutoModeSourcesWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // Escape should dismiss the guide first, not the whole window under it.
        if (guideOverlay.IsVisible)
        {
            guideOverlay.IsVisible = false;
            return;
        }

        Close(ViewModel?.IsModified);
    }
}
