using CsiScope.Domain.Model;
using CsiScope.Domain.Strategy;
using FluentAssertions;
using Xunit;

namespace CsiScope.Domain.Tests;

/// <summary>
/// Table-driven verification of every SensingPolicy transition — pure
/// SensingContext snapshots in, SensingDecision out. No timers, no streams.
/// </summary>
public class SensingPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly WifiChannel Ch6 = new(6);
    private static readonly WifiChannel Ch7 = new(7);
    private static readonly WifiChannel Ch11 = new(11);
    private static readonly MacAddress Target = MacAddress.Parse("08:E9:F6:63:9A:CC");

    private static SensingContext Ctx(
        CampaignMode mode,
        ConfidenceScore? confidence = null,
        ChannelLiveness? liveness = null,
        bool converged = false,
        TimeSpan? inMode = null,
        DateTimeOffset? lastAudit = null,
        WifiChannel? locked = null,
        params ChannelCandidate[] candidates) => new()
        {
            Mode = mode,
            Now = Now,
            ModeEnteredAt = Now - (inMode ?? TimeSpan.Zero),
            LockedChannel = locked,
            Confidence = confidence ?? ConfidenceScore.Full,
            Liveness = liveness,
            AllNodesConverged = converged,
            LastAuditAt = lastAudit,
            Candidates = candidates,
            MacFilter = new[] { Target },
        };

    #region Surveying

    [Fact]
    public void Surveying_with_active_candidate_begins_acquisition()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Surveying,
            candidates: new[] { new ChannelCandidate(Ch7, Target, 12.5) });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert — contents, not record identity (list fields compare by reference).
        var acquisition = decision.Should().BeOfType<SensingDecision.BeginAcquisition>().Which;
        acquisition.Channel.Should().Be(Ch7);
        acquisition.MacFilter.Should().Equal(Target);
    }

    [Fact]
    public void Surveying_picks_highest_activity_candidate()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Surveying,
            candidates: new[]
            {
                new ChannelCandidate(Ch6, Target, 4.0),
                new ChannelCandidate(Ch11, Target, 9.0),
            });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.BeginAcquisition>()
            .Which.Channel.Should().Be(Ch11);
    }

    [Fact]
    public void Surveying_without_candidates_holds()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Surveying);

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    [Fact]
    public void Surveying_ignores_candidates_below_activity_floor()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Surveying,
            candidates: new[] { new ChannelCandidate(Ch6, Target, 0.2) });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    #endregion

    #region Acquiring

    [Fact]
    public void Acquiring_when_all_nodes_converged_enters_detecting()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Acquiring, converged: true, locked: Ch6,
            liveness: new ChannelLiveness(Ch6, 500, Now));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.EnterDetecting>();
    }

    [Fact]
    public void Acquiring_dead_air_past_dead_channel_timeout_resumes_surveying()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Acquiring, locked: Ch6,
            liveness: ChannelLiveness.Dead(Ch6),
            inMode: TimeSpan.FromSeconds(9));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().Be(new SensingDecision.ResumeSurveying(SurveyReason.DeadAir));
    }

    [Fact]
    public void Acquiring_dead_air_within_dead_channel_timeout_holds()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Acquiring, locked: Ch6,
            liveness: ChannelLiveness.Dead(Ch6),
            inMode: TimeSpan.FromSeconds(3));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    [Fact]
    public void Acquiring_missing_liveness_is_treated_as_dead_air()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Acquiring, locked: Ch6, liveness: null,
            inMode: TimeSpan.FromSeconds(9));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().Be(new SensingDecision.ResumeSurveying(SurveyReason.DeadAir));
    }

    [Fact]
    public void Acquiring_live_channel_still_filling_holds()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Acquiring, locked: Ch6,
            liveness: new ChannelLiveness(Ch6, 40, Now),
            inMode: TimeSpan.FromSeconds(30));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    [Fact]
    public void Acquiring_live_channel_past_acquisition_timeout_gives_up()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Acquiring, locked: Ch6,
            liveness: new ChannelLiveness(Ch6, 40, Now),
            inMode: TimeSpan.FromSeconds(95));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().Be(new SensingDecision.ResumeSurveying(SurveyReason.AcquisitionTimeout));
    }

    #endregion

    #region Detecting

    [Fact]
    public void Detecting_high_confidence_recent_audit_holds()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: ConfidenceScore.Full,
            liveness: new ChannelLiveness(Ch6, 100, Now),
            lastAudit: Now - TimeSpan.FromSeconds(10),
            candidates: new[] { new ChannelCandidate(Ch7, Target, 5.0) });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    [Fact]
    public void Detecting_issues_audit_when_interval_elapsed()
    {
        // Arrange — full confidence -> audit interval = MaxAuditInterval (60s).
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: ConfidenceScore.Full,
            liveness: new ChannelLiveness(Ch6, 100, Now),
            lastAudit: Now - TimeSpan.FromSeconds(70),
            candidates: new[]
            {
                new ChannelCandidate(Ch6, Target, 9.0),  // locked — excluded
                new ChannelCandidate(Ch7, Target, 5.0),
                new ChannelCandidate(Ch11, Target, 3.0),
            });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        var audit = decision.Should().BeOfType<SensingDecision.AuditChannels>().Which;
        audit.ReturnTo.Should().Be(Ch6);
        audit.Plan.Channels.Should().BeEquivalentTo(new[] { Ch7, Ch11 });
    }

    [Fact]
    public void Detecting_full_confidence_waits_max_interval_before_audit()
    {
        // Arrange — conf=1.0 -> interval = MaxAuditInterval (60s); only 45s elapsed.
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: ConfidenceScore.Full,
            liveness: new ChannelLiveness(Ch6, 100, Now),
            lastAudit: Now - TimeSpan.FromSeconds(45),
            candidates: new[] { new ChannelCandidate(Ch7, Target, 5.0) });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    [Fact]
    public void Detecting_lower_confidence_audits_sooner()
    {
        // Arrange — Value ~0.52 stays above the reacquire gate but shortens
        // the audit interval to ~36s; same 45s elapsed -> due.
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: new ConfidenceScore(0.8, 0.8, 0.9, 0.9),
            liveness: new ChannelLiveness(Ch6, 100, Now),
            lastAudit: Now - TimeSpan.FromSeconds(45),
            candidates: new[] { new ChannelCandidate(Ch7, Target, 5.0) });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.AuditChannels>();
    }

    [Fact]
    public void Detecting_collapsed_confidence_dead_air_fast_skips()
    {
        // Arrange
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: ConfidenceScore.Zero,
            liveness: ChannelLiveness.Dead(Ch6));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().Be(new SensingDecision.ResumeSurveying(SurveyReason.DeadAir));
    }

    [Fact]
    public void Detecting_collapsed_confidence_live_channel_hunts_shifted_target()
    {
        // Arrange — channel still producing frames from other MACs but the
        // primary went silent -> target moved, resume surveying to relocate it.
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: ConfidenceScore.Zero,
            liveness: new ChannelLiveness(Ch6, 55, Now));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().Be(new SensingDecision.ResumeSurveying(SurveyReason.TargetShifted));
    }

    [Fact]
    public void Detecting_degraded_confidence_reacquires()
    {
        // Arrange — Value = 0.7^4 = 0.24 -> above abandon (0.15), below reacquire (0.5).
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: new ConfidenceScore(0.7, 0.7, 0.7, 0.7),
            liveness: new ChannelLiveness(Ch6, 80, Now));

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Reacquire>();
    }

    [Fact]
    public void Detecting_audit_due_but_no_other_channels_holds()
    {
        // Arrange — only the locked channel was surveyed; nothing to audit.
        var ctx = Ctx(CampaignMode.Detecting, locked: Ch6,
            confidence: ConfidenceScore.Full,
            liveness: new ChannelLiveness(Ch6, 100, Now),
            lastAudit: Now - TimeSpan.FromSeconds(120),
            candidates: new[] { new ChannelCandidate(Ch6, Target, 9.0) });

        // Act
        var decision = SensingPolicy.Decide(ctx);

        // Assert
        decision.Should().BeOfType<SensingDecision.Hold>();
    }

    #endregion
}
