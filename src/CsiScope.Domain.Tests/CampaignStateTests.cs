using System.Collections.Immutable;
using CsiScope.Domain.Events;
using CsiScope.Domain.Model;
using CsiScope.Domain.Strategy;
using FluentAssertions;
using Xunit;

namespace CsiScope.Domain.Tests;

public class CampaignStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly WifiChannel Ch7 = new(7);
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");

    private static SensingContext Ctx(CampaignMode mode, ConfidenceScore? confidence = null) => new()
    {
        Mode = mode,
        Now = Now,
        ModeEnteredAt = Now,
        Confidence = confidence ?? ConfidenceScore.Full,
        MacFilter = ImmutableArray.Create(Target),
    };

    #region Transitions

    [Fact]
    public void BeginAcquisition_locks_channel_and_emits_lock_event()
    {
        // Arrange
        var campaign = new CampaignState();
        var ctx = Ctx(CampaignMode.Surveying);

        // Act
        campaign.Apply(new SensingDecision.BeginAcquisition(Ch6, ImmutableArray.Create(Target)), ctx);

        // Assert
        campaign.Mode.Should().Be(CampaignMode.Acquiring);
        campaign.LockedChannel.Should().Be(Ch6);
        campaign.PrimaryTarget.Should().Be(Target);
        campaign.ModeEnteredAt.Should().Be(Now);
        campaign.DrainEvents().Should().ContainSingle()
            .Which.Should().Be(new ChannelLockAcquired(Ch6, Target, Now));
    }

    [Fact]
    public void ResumeSurveying_clears_lock_and_emits_degraded_event()
    {
        // Arrange
        var campaign = new CampaignState();
        var ctx = Ctx(CampaignMode.Detecting, ConfidenceScore.Zero);
        campaign.Apply(new SensingDecision.BeginAcquisition(Ch6, ImmutableArray.Create(Target)), ctx);
        campaign.Apply(new SensingDecision.EnterDetecting(), ctx);
        campaign.DrainEvents();

        // Act
        campaign.Apply(new SensingDecision.ResumeSurveying(SurveyReason.TargetShifted), ctx);

        // Assert
        campaign.Mode.Should().Be(CampaignMode.Surveying);
        campaign.LockedChannel.Should().BeNull();
        campaign.PrimaryTarget.Should().BeNull();
        campaign.DrainEvents().Should().ContainSingle()
            .Which.Should().Be(new ConfidenceDegraded(Ch6, ConfidenceScore.Zero, SurveyReason.TargetShifted, Now));
    }

    [Fact]
    public void Reacquire_keeps_lock_but_reenters_acquiring()
    {
        // Arrange
        var campaign = new CampaignState();
        var ctx = Ctx(CampaignMode.Detecting);
        campaign.Apply(new SensingDecision.BeginAcquisition(Ch6, ImmutableArray.Create(Target)), ctx);
        campaign.Apply(new SensingDecision.EnterDetecting(), ctx);

        // Act
        campaign.Apply(new SensingDecision.Reacquire(), ctx);

        // Assert
        campaign.Mode.Should().Be(CampaignMode.Acquiring);
        campaign.LockedChannel.Should().Be(Ch6);
        campaign.MacFilter.Should().Contain(Target);
    }

    #endregion

    #region Audit Directive

    [Fact]
    public void AuditChannels_stays_detecting_and_stamps_audit_time()
    {
        // Arrange
        var campaign = new CampaignState();
        var ctx = Ctx(CampaignMode.Detecting);
        campaign.Apply(new SensingDecision.BeginAcquisition(Ch6, ImmutableArray.Create(Target)), ctx);
        campaign.Apply(new SensingDecision.EnterDetecting(), ctx);
        campaign.DrainEvents();

        // Act
        var plan = new ScanPlan(ImmutableArray.Create(Ch7), TimeSpan.FromMilliseconds(250));
        campaign.Apply(new SensingDecision.AuditChannels(plan, Ch6), ctx);

        // Assert
        campaign.Mode.Should().Be(CampaignMode.Detecting);
        campaign.LockedChannel.Should().Be(Ch6);
        campaign.LastAuditAt.Should().Be(Now);
        campaign.DrainEvents().Should().BeEmpty("audits are directives, not transitions");
    }

    #endregion

    #region Event Drain

    [Fact]
    public void DrainEvents_clears_pending_events()
    {
        // Arrange
        var campaign = new CampaignState();
        var ctx = Ctx(CampaignMode.Surveying);
        campaign.Apply(new SensingDecision.BeginAcquisition(Ch6, ImmutableArray.Create(Target)), ctx);

        // Act & Assert — events are delivered once, never replayed.
        campaign.DrainEvents().Should().HaveCount(1);
        campaign.DrainEvents().Should().BeEmpty();
    }

    #endregion
}
