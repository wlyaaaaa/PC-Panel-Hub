using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class OverlayPolicyTests
{
    [Fact]
    public void PhoneBattery_IsPersistentDirectOverlay()
    {
        var policy = OverlayPolicies.For(OverlayKind.PhoneBattery);

        Assert.Equal(OverlayVisualTier.Direct, policy.VisualTier);
        Assert.Equal(OverlayLifetime.WhileActive, policy.Lifetime);
        Assert.Null(policy.Duration);
        Assert.True(policy.Typography.TitlePx >= 48);
    }

    [Fact]
    public void OrdinaryPhoneNotification_IsLargeAndFinite()
    {
        var policy = OverlayPolicies.For(OverlayKind.PhoneNotification);

        Assert.Equal(OverlayVisualTier.Crystal, policy.VisualTier);
        Assert.Equal(OverlayLifetime.Timed, policy.Lifetime);
        Assert.Equal(TimeSpan.FromSeconds(18), policy.Duration);
        Assert.True(policy.Typography.TitlePx >= 56);
        Assert.True(policy.Typography.BodyPx >= 42);
        Assert.Equal(2, policy.Typography.MaxBodyLines);
    }

    [Theory]
    [InlineData(OverlayKind.MediaTrackChange, 8)]
    [InlineData(OverlayKind.GameAchievement, 12)]
    [InlineData(OverlayKind.GameSummary, 20)]
    [InlineData(OverlayKind.SystemOperation, 6)]
    [InlineData(OverlayKind.DeviceOrNetwork, 12)]
    [InlineData(OverlayKind.ImportantTaskComplete, 15)]
    [InlineData(OverlayKind.HardwareResolved, 10)]
    [InlineData(OverlayKind.PhoneConnection, 20)]
    public void TimedPolicies_HaveApprovedDurations(OverlayKind kind, int seconds)
    {
        var policy = OverlayPolicies.For(kind);

        Assert.Equal(OverlayLifetime.Timed, policy.Lifetime);
        Assert.Equal(TimeSpan.FromSeconds(seconds), policy.Duration);
    }

    [Theory]
    [InlineData(OverlayKind.MediaActive)]
    [InlineData(OverlayKind.GameActive)]
    [InlineData(OverlayKind.ImportantTask)]
    [InlineData(OverlayKind.PhoneDynamic)]
    [InlineData(OverlayKind.PhoneCall)]
    [InlineData(OverlayKind.PhoneTransfer)]
    [InlineData(OverlayKind.HardwareAlert)]
    public void ActiveStates_DoNotInventAnExpiry(OverlayKind kind)
    {
        var policy = OverlayPolicies.For(kind);

        Assert.Equal(OverlayLifetime.WhileActive, policy.Lifetime);
        Assert.Null(policy.Duration);
    }

    [Fact]
    public void SteamAndOtherGamesShareTheSameGamePolicy()
    {
        var steam = OverlayRequest.Active(
            "steam-730",
            OverlayKind.GameActive,
            OverlaySource.Steam,
            "Counter-Strike 2");
        var other = OverlayRequest.Active(
            "game-local",
            OverlayKind.GameActive,
            OverlaySource.Game,
            "Local Game");

        Assert.Equal(OverlayPolicies.For(steam.Kind), OverlayPolicies.For(other.Kind));
    }
}
