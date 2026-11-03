using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Messages;

/// <param name="Url">Original file, relative to the server.</param>
/// <param name="ThumbnailUrl">Image scaled to at most 600 px; null for files that are not images.</param>
public sealed record AttachmentDto(
    string Id, string FileName, string ContentType, long Size, int? Width, int? Height, string Url, string? ThumbnailUrl);

/// <remarks>Headers and body stay on the server: they may hold keys of the target.</remarks>
public sealed record ActionDto(int Index, string Label, string Method, string Url, bool Clear, int? LastStatus, DateTime? LastRunAt);

/// <summary>A message as the API returns it, to senders and readers alike.</summary>
public sealed record MessageDto(
    long Id,
    string Topic,
    string? Title,
    string Message,
    int Priority,
    List<string> Tags,
    string? Click,
    string Source,
    DateTime CreatedAt,
    DateTime? ReadAt,
    List<AttachmentDto> Attachments,
    List<ActionDto> Actions)
{
    public static MessageDto From(Message message) => new(
        message.Id,
        message.Topic,
        message.Title,
        message.Body,
        message.Priority,
        message.Tags,
        message.ClickUrl,
        message.Source,
        message.CreatedAt,
        message.ReadAt,
        message.Attachments.OrderBy(a => a.Path, StringComparer.Ordinal).Select(From).ToList(),
        message.Actions.OrderBy(a => a.Index).Select(From).ToList());

    public static AttachmentDto From(Attachment attachment) => new(
        attachment.Id,
        attachment.FileName,
        attachment.ContentType,
        attachment.Size,
        attachment.Width,
        attachment.Height,
        $"/api/v1/attachments/{attachment.Id}",
        attachment.ThumbnailPath is null ? null : $"/api/v1/attachments/{attachment.Id}/thumb");

    public static ActionDto From(MessageAction action) =>
        new(action.Index, action.Label, action.Method, action.Url, action.Clear, action.LastStatus, action.LastRunAt);
}
