using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Manager;

public class WindowDialog : IWindowDialog
{
    public async Task<bool> ShowDialogAsync<TViewModel>(TViewModel vm)
        where TViewModel : class
    {
        var owner = TryGetOwnerWindow();

        var view = SimpleViewLocator.Instance.Build(vm);

        if (view is not WindowBase<TViewModel> window)
        {
            // A false return here is indistinguishable from "the user closed the dialog
            // without saving", so the only way this failure is ever diagnosable is if it
            // names the type it could not build.
            Logging.SaveLog($"WindowDialog: no window registered for {vm.GetType().Name}, got {view?.GetType().Name ?? "null"}");
            return false;
        }

        window.ViewModel = vm;

        if (vm is ServiceLib.Base.ICloseable closeable)
        {
            closeable.RequestClose += (_, _) => Dispatch(() => window.Close(true));
        }

        var result = await window.ShowDialog<bool>(owner);
        return result;
    }

    public static Window TryGetOwnerWindow()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            ?? throw new InvalidOperationException("Application lifetime is not of type IClassicDesktopStyleApplicationLifetime.");
        var openWindows = desktop.Windows
            .Where(w => w.IsVisible)
            .ToArray();
        if (openWindows.Length == 0)
        {
            return desktop.MainWindow
                ?? throw new InvalidOperationException("No open windows and no main window found.");
        }

        if (openWindows.Length == 1)
        {
            return openWindows[0];
        }

        try
        {
            Window.SortWindowsByZOrder(openWindows);

            var activeTopmost = openWindows.Reverse().FirstOrDefault(w => w.IsActive);
            return activeTopmost ?? openWindows[^1];
        }
        catch
        {
            return desktop.Windows.FirstOrDefault(w => w.IsActive)
                   ?? desktop.MainWindow
                   ?? openWindows[0];
        }
    }

    private static void Dispatch(Action action)
    {
        Dispatcher.UIThread.InvokeAsync(action);
    }
}
