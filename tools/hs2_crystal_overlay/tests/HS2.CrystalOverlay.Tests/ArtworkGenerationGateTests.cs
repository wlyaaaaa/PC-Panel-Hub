using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class ArtworkGenerationGateTests
{
    [Fact]
    public void Begin_MakesOnlyTheNewestArtworkPublicationCurrent()
    {
        var gate = new ArtworkGenerationGate();

        var first = gate.Begin();
        var second = gate.Begin();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void Invalidate_RejectsOutstandingArtworkPublication()
    {
        var gate = new ArtworkGenerationGate();
        var generation = gate.Begin();

        gate.Invalidate();

        Assert.False(gate.IsCurrent(generation));
    }
}
