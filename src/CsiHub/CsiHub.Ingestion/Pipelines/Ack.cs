namespace CsiHub.Ingestion.Pipelines;

/// <summary>
/// An ACK received from a CsiScope node in response to a host command.
/// </summary>
/// <param name="Seq">The sequence number matching the original command.</param>
/// <param name="Cmd">The command name being acknowledged.</param>
/// <param name="Success">Whether the command succeeded on the node.</param>
/// <param name="Reason">Optional reason for failure.</param>
/// <param name="State">Optional current node state.</param>
public sealed record Ack(int Seq, string? Cmd, bool Success, string? Reason, string? State);
