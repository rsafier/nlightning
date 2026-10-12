namespace NLightning.Domain.Protocol.OnionMessages;

using Enums;

/// <summary>
/// The result of <c>IOnionMessageService.SendAsync</c> or <c>SendAndWaitForReplyAsync</c>.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Reply">The reply when <paramref name="Status"/> is <see cref="OnionMessageSendStatus.Replied"/>, else
/// null.</param>
public sealed record OnionMessageSendResult(OnionMessageSendStatus Status, ReceivedOnionMessage? Reply = null);