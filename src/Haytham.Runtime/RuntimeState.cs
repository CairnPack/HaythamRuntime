namespace Haytham.Runtime;

public enum RuntimeState {
    Created,
    Starting,
    Running,
    PreStopping,
    StopRequested,
    Draining,
    Stopping,
    Unloading,
    Stopped,
    Hanging
}
