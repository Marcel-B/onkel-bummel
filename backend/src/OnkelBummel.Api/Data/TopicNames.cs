using System.Text.RegularExpressions;

namespace OnkelBummel.Api.Data;

public static partial class TopicNames
{
    /// <summary>Stands for every topic in subscriptions and token rights.</summary>
    public const string All = "*";

    // Would clash with routes or mean "all topics".
    private static readonly HashSet<string> Reserved = ["api", "auth", "static", "health", "metrics"];

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name) && !Reserved.Contains(name);

    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex Pattern();
}
