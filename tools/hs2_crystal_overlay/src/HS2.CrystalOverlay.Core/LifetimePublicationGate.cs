namespace HS2.CrystalOverlay.Core;

public sealed class LifetimePublicationGate
{
    private readonly object sync = new();
    private bool closed;

    public bool IsClosed
    {
        get
        {
            lock (sync)
            {
                return closed;
            }
        }
    }

    public bool TryPublish(Func<bool> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (sync)
        {
            return !closed && publish();
        }
    }

    public bool Close()
    {
        lock (sync)
        {
            if (closed)
            {
                return false;
            }

            closed = true;
            return true;
        }
    }
}
