namespace CsiScope.Domain.Model;

/// <summary>Why a campaign abandoned its lock and returned to surveying.</summary>
public enum SurveyReason
{
    /// <summary>No frames from any filtered MAC — the channel is dead air.</summary>
    DeadAir,

    /// <summary>Channel is live but the primary target went silent — it moved.</summary>
    TargetShifted,

    /// <summary>Composite confidence collapsed for other reasons.</summary>
    ConfidenceCollapsed,

    /// <summary>Acquisition dwell exceeded the timeout without converging.</summary>
    AcquisitionTimeout,
}
