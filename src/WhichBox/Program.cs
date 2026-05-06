using System.Diagnostics;
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

        // Self-restart handshake: if launched with --wait-for-pid <PID>,
        // wait for the previous instance to fully exit before initializing.
        // Used by SelfRestart on session change to avoid the new and old
        // processes racing for shared WinUI/COM endpoints.
        if (args.Length >= 2 && args[0] == "--wait-for-pid" && int.TryParse(args[1], out var prevPid))
        {
            try
            {
                var prev = Process.GetProcessById(prevPid);
                Logger.Info($"--wait-for-pid: waiting for previous instance PID={prevPid} to exit (timeout 10s)...");
                if (prev.WaitForExit(10000))
                    Logger.Info($"--wait-for-pid: previous instance PID={prevPid} exited");
                else
                    Logger.Warn($"--wait-for-pid: previous instance PID={prevPid} did not exit within 10s, proceeding anyway");
            }
            catch (ArgumentException)
            {
                Logger.Info($"--wait-for-pid: previous instance PID={prevPid} already gone");
            }
            catch (Exception ex)
            {
                Logger.Warn($"--wait-for-pid failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Install the native unhandled-exception filter as early as possible
        // so we can capture a minidump for crashes during WinUI startup.
        // Re-installed from MainWindow.ctor in case WinUI overrode us.
        NativeCrashHandler.Install();

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


