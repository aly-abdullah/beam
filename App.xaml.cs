using System.Threading;
using System.Windows;

namespace Beam;

public partial class App : Application
{
    const string InstanceName = "Beam.SingleInstance.7F3A";
    Mutex? _mutex;
    EventWaitHandle? _signal;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            try
            {
                EventWaitHandle.OpenExisting(InstanceName + ".Show").Set();
            }
            catch
            {
            }
            Shutdown();
            return;
        }

        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        base.OnStartup(e);

        var window = new MainWindow();
        window.Show();

        var listener = new Thread(() =>
        {
            while (_signal.WaitOne())
                Dispatcher.Invoke(window.Reveal);
        }) { IsBackground = true };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _signal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
