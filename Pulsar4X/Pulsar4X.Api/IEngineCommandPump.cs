namespace Pulsar4X.Api;

/// <summary>
/// Optional hook for in-process clients to drain pending engine work (command inbox)
/// while simulation time is paused.
/// </summary>
public interface IEngineCommandPump
{
    void PumpPendingCommands();
}
