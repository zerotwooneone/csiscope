using CsiHub.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace CsiHub.Ingestion.Pipelines.Handlers;

/// <summary>
/// Handles ACK frames that complete pending host-to-node commands.
/// </summary>
public sealed class AckHandler : IPayloadHandler
{
    public bool CanHandle(NodePayload payload) => payload.Type == "ack";

    public void Handle(NodePayload payload, ReadOnlySpan<byte> rawSpan, SerialPipelineContext context)
    {
        if (!payload.Seq.HasValue)
        {
            return;
        }

        int seq = payload.Seq.Value;
        string? cmd = payload.Cmd;
        bool success = payload.Success ?? false;
        string? reason = payload.Reason;
        string? state = payload.State;
        string? mac = payload.Mac;

        if (!string.IsNullOrEmpty(mac))
        {
            context.LastMac = mac;
        }

        if (!string.IsNullOrEmpty(state))
        {
            context.PublishStateFromString(state, receivedAt: payload.ReceivedAt);
        }

        var ack = new Ack(seq, cmd, success, reason, state);

        if (context.PendingAcks.TryRemove(seq, out var tcs))
        {
            tcs.TrySetResult(ack);
        }
        else
        {
            context.Logger.LogDebug(
                "Received unsolicited ACK for seq {Seq} on {Port}.",
                seq,
                context.PortName);
        }

        // A nacked command is a node-reported failure, not just an unanswered
        // waiter. Publish it so downstream consumers (state store) can surface
        // it even when the command was sent fire-and-forget.
        if (!success)
        {
            context.Logger.LogWarning(
                "Command {Cmd} nacked by {Mac} on {Port}: {Reason}.",
                cmd ?? "unknown",
                mac ?? "unknown",
                reason ?? "unknown");

            context.Channel.TryPublish(payload);
        }
    }
}
