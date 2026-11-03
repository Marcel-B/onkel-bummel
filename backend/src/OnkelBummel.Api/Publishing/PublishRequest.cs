namespace OnkelBummel.Api.Publishing;

/// <summary>A message as senders send it, as JSON or as the fields of a form.</summary>
public sealed class PublishRequest
{
    public string? Topic { get; set; }

    public string? Title { get; set; }

    public string? Message { get; set; }

    public int? Priority { get; set; }

    public List<string>? Tags { get; set; }

    public string? Click { get; set; }

    public List<ActionRequest>? Actions { get; set; }

    public List<ImageRequest>? Images { get; set; }
}

public sealed class ActionRequest
{
    public string? Label { get; set; }

    public string? Url { get; set; }

    public string? Method { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Body { get; set; }

    public bool Clear { get; set; }
}

/// <summary>An attachment in JSON: either loaded from <see cref="Url"/> or given as Base64 in <see cref="Data"/>.</summary>
public sealed class ImageRequest
{
    public string? Url { get; set; }

    public string? Data { get; set; }

    public string? Filename { get; set; }
}

/// <summary>An attachment on its way in.</summary>
public abstract record IncomingFile;

public sealed record UploadedFile(IFormFile File) : IncomingFile;

public sealed record InlineFile(string Base64, string? FileName) : IncomingFile;

public sealed record RemoteFile(Uri Url, string? FileName) : IncomingFile;

/// <summary>Turns a request away; the endpoint answers with problem JSON and counts it under <see cref="Result"/>.</summary>
/// <param name="Result">Value of the <c>result</c> label of <c>onkelbummel_messages_received_total</c>.</param>
public sealed class PublishException(int status, string result, string title, string? detail = null) : Exception(title)
{
    public int Status { get; } = status;

    public string Result { get; } = result;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    public static PublishException Invalid(string detail) =>
        new(StatusCodes.Status400BadRequest, "invalid", "Ungültige Nachricht", detail);

    public static PublishException Forbidden(string topic) =>
        new(StatusCodes.Status403Forbidden, "forbidden", "Topic nicht erlaubt", $"Dieses Token darf nicht an „{topic}“ senden.");

    public static PublishException TooLarge(string detail) =>
        new(StatusCodes.Status413PayloadTooLarge, "too_large", "Zu groß", detail);

    public static PublishException UnsupportedType(string detail) =>
        new(StatusCodes.Status415UnsupportedMediaType, "invalid", "Falscher Inhaltstyp", detail);
}
