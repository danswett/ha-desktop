using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using static HomeAssistant.Desktop.Services.NativeMethods;

namespace HomeAssistant.Desktop;

public static class Program
{
    private const string InstanceKey = "HomeAssistantDesktop.SingleInstance";

    private const uint CWMO_DEFAULT = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // One dashboard, one browser profile: a second WebView2 against the same user data
        // folder would fail anyway, so a second launch hands off to the running instance.
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            RedirectActivationTo(mainInstance, AppInstance.GetCurrent().GetActivatedEventArgs());
            return;
        }

        Application.Start(_ =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));

            // The Application instance registers itself; the reference is intentionally dropped.
            new App(mainInstance, args);
        });
    }

    /// <summary>
    /// RedirectActivationToAsync must complete before this process exits, but awaiting it on
    /// the STA deadlocks. Run it on a worker and pump COM here until it signals.
    /// </summary>
    private static void RedirectActivationTo(AppInstance target, AppActivationArguments args)
    {
        var redirected = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);

        var worker = new Thread(() =>
        {
            try
            {
                target.RedirectActivationToAsync(args).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // The other instance may have exited between the key lookup and the redirect.
                // Nothing useful to do here; this process is going away regardless.
            }
            finally
            {
                SetEvent(redirected);
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();
        CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [redirected], out _);
        CloseHandle(redirected);
    }
}
