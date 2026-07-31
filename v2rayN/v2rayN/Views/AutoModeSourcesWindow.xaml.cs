namespace v2rayN.Views;

public partial class AutoModeSourcesWindow
{
    public AutoModeSourcesWindow()
    {
        InitializeComponent();

        btnClose.Click += btnClose_Click;

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.SourceText, v => v.txtSources.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Summary, v => v.txtSummary.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Stats, v => v.lstStats.ItemsSource).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportFileCmd, v => v.btnImportFile).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ResetStatsCmd, v => v.btnResetStats).DisposeWith(disposables);

            ViewModel.ShowYesNoInteraction.RegisterHandler(interaction =>
            {
                interaction.SetOutput(UI.ShowYesNo(interaction.Input) != MessageBoxResult.No);
            }).DisposeWith(disposables);

            ViewModel.BrowseFileInteraction.RegisterHandler(interaction =>
            {
                interaction.SetOutput(UI.OpenFileDialog(out var fileName, "Text|*.txt|All|*.*") == true ? fileName : null);
            }).DisposeWith(disposables);
        });

        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void btnClose_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsModified == true)
        {
            DialogResult = true;
        }
        else
        {
            Close();
        }
    }
}
