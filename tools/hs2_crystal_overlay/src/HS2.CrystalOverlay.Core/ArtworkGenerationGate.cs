namespace HS2.CrystalOverlay.Core;

public readonly record struct ArtworkGeneration(long Value);

/// <summary>
/// Invalidates asynchronous artwork work when its owning media session changes.
/// </summary>
public sealed class ArtworkGenerationGate
{
    private readonly object sync = new();
    private long current;

    public ArtworkGeneration Begin()
    {
        lock (sync)
        {
            return new ArtworkGeneration(++current);
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            current++;
        }
    }

    public bool IsCurrent(ArtworkGeneration generation)
    {
        lock (sync)
        {
            return generation.Value != 0 &&
                   generation.Value == current;
        }
    }
}
