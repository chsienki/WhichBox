using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WhichBox;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Banner first so we always know the process started, even if the
        // very next call dies before any exception can be caught.
        Logger.LogStartupBanner();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception
                ?? new Exception($"Non-Exception thrown: {e.ExceptionObject}");
            Logger.Crash($"AppDomain.UnhandledException (terminating={e.IsTerminating})", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Crash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Logger.Info("AppDomain.ProcessExit -- process is terminating normally");
        };

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            Logger.Crash("Program.Main", ex);
            throw;
        }
    }
}

