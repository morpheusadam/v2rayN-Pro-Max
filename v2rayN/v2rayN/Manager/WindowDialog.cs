using v2rayN.Base;

namespace v2rayN.Manager;

public class WindowDialog : IWindowDialog
{
    public Task<bool> ShowDialogAsync<TViewModel>(TViewModel vm)
        where TViewModel : class
    {
        var owner = Application.Current.MainWindow;

        var viewFor = SimpleViewLocator.Instance.ResolveView(vm);
        //var viewFor = SimpleViewLocator.Instance.ResolveView<TViewModel>();

        if (viewFor is not WindowBase<TViewModel> window)
        {
            // Same false as "closed without saving", so it has to name itself in the log
            // or the failure is invisible.
            Logging.SaveLog($"WindowDialog: no window registered for {vm.GetType().Name}, got {viewFor?.GetType().Name ?? "null"}");
            return Task.FromResult(false);
        }

        window.Owner = owner;
        window.ViewModel = vm;

        if (vm is ServiceLib.Base.ICloseable closeable)
        {
            closeable.RequestClose += (_, _) => Dispatch(() => window.DialogResult = true);
        }

        var result = window.ShowDialog();

        return Task.FromResult(result ?? false);
    }

    private static void Dispatch(Action action)
    {
        Application.Current.Dispatcher.Invoke(action);
    }
}
