using System.Text;

namespace PoniExp.Core.Protocol;

/// <summary>
/// What makes a message well formed. The sender checks before signing and the recipient checks again,
/// because the router can't: from phase 2 the content is encrypted.
/// </summary>
public static class MessageRules
{
    /// <summary>Null if the content is well formed, otherwise why not.</summary>
    public static string? CheckContent(MessageContent content)
    {
        if (!Limits.IsValidEndpoint(content.FromEndpoint) || !Limits.IsValidEndpoint(content.ToEndpoint))
        {
            return $"an endpoint must be {Limits.EndpointRule}";
        }

        if (!Enum.IsDefined(content.Type))
        {
            return "unknown message type";
        }

        if (content.Status is { } status && (!Enum.IsDefined(status) || content.ReplyTo is null))
        {
            return "a status is only for replies (set ReplyTo)";
        }

        if (content.ConversationId == Guid.Empty || content.OperationId == Guid.Empty || content.ReplyTo == Guid.Empty)
        {
            return "conversation, operation and reply-to ids can't be empty";
        }

        if (content.Subject is { Length: 0 or > Limits.MaxSubjectLength })
        {
            return $"a subject is 1-{Limits.MaxSubjectLength} characters (or none)";
        }

        if (!Limits.IsValidContentType(content.ContentType))
        {
            return "the content type must look like 'text/plain'";
        }

        if (content.Metadata is null || content.Metadata.Count > Limits.MaxMetadataEntries
            || content.Metadata.Any(m => !Limits.IsValidMetadataKey(m.Key) || m.Value is null || m.Value.Length > Limits.MaxMetadataValueLength))
        {
            return $"metadata is at most {Limits.MaxMetadataEntries} entries, keys [A-Za-z0-9._:-] up to {Limits.MaxMetadataKeyLength}, values up to {Limits.MaxMetadataValueLength} characters";
        }

        if (content.Body is null || Encoding.UTF8.GetByteCount(content.Body) > Limits.MaxBodyBytes)
        {
            return $"the body is at most {Limits.MaxBodyBytes} bytes";
        }

        return null;
    }
}
