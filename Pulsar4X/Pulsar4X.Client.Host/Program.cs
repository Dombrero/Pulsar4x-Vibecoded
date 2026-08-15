using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Pulsar4X.Api;
using Pulsar4X.Client;
using Pulsar4X.Client.Host;

#if TRACE
Trace.Listeners.Add(new ConsoleTraceListener());
#endif

static void LogCrash(string source, object exception)
{
    try
    {
        DebugTraceLog.Error("Crash", $"{source}: {exception}");
    }
    catch
    {
        // last-chance logging must not throw
    }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("UnhandledException", e.ExceptionObject);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    LogCrash("UnobservedTaskException", e.Exception);
    e.SetObserved();
};

using (var pulsar = new PulsarMainWindow(args))
{
    pulsar.State.Lifecycle = new GameLifecycle(pulsar.State);
    DevToolWindows.Register(pulsar.State);
    pulsar.Run();
}
