namespace RepairAlert;

internal static class ReliableMessageLogic
{
    /// <summary>Экспоненциальная пауза между повторами отправки: 1, 2, 5, 10, 30 с, далее всегда 30 с.</summary>
    private static readonly int[] BackoffSeconds = { 1, 2, 5, 10, 30 };

    public static TimeSpan RetryDelay(int retryCount) =>
        TimeSpan.FromSeconds(BackoffSeconds[Math.Clamp(retryCount, 0, BackoffSeconds.Length - 1)]);

    public static bool ContainsMessage(IEnumerable<ChatEntry> history, string messageId) =>
        messageId.Length > 0 && history.Any(entry => entry.Id == messageId);

    public static bool NeedsRetry(OutgoingChatMessage item, DateTime utcNow) =>
        item.AwaitingAck && utcNow - item.LastSentAtUtc > RetryDelay(item.RetryCount);
}
