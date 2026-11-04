using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Auth;

/// <summary>
/// Manages publish tokens and the access key from the command line, e.g.
/// <c>docker compose exec onkel-bummel dotnet OnkelBummel.Api.dll --create-token grafana alarme</c>.
/// </summary>
public static class TokenCommands
{
    public const string Create = "--create-token";
    public const string List = "--list-tokens";
    public const string Delete = "--delete-token";
    public const string NewAccessKey = "--new-access-key";

    private const string Usage = """
        Aufruf:
          --create-token <name> <topics>   Token anlegen; topics kommagetrennt, * für alle
          --list-tokens                    Tokens anzeigen
          --delete-token <id>              Token löschen
          --new-access-key                 neuer Zugangsschlüssel (nur ohne Identity-Server), meldet alle Browser ab
        """;

    public static bool IsCommand(string[] args) => args is [Create or List or Delete or NewAccessKey, ..];

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OnkelBummelDbContext>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        switch (args)
        {
            case [Create, var name, var topicList]:
            {
                var (topics, problem) = PublishTokens.Validate(name, topicList.Split(','));
                if (topics is null)
                {
                    await error.WriteLineAsync(problem);
                    return 1;
                }
                var created = await PublishTokens.CreateAsync(db, name, topics, time, CancellationToken.None);
                await output.WriteLineAsync($"Token „{created.Name}“ ({created.Id}) für {string.Join(", ", created.Topics)}:");
                await output.WriteLineAsync(created.Token);
                await output.WriteLineAsync("Er wird nur dieses eine Mal angezeigt.");
                return 0;
            }
            case [List]:
                foreach (var token in await PublishTokens.ListAsync(db, CancellationToken.None))
                {
                    var used = token.LastUsedAt is { } at ? $"zuletzt {at:yyyy-MM-dd HH:mm} UTC" : "nie benutzt";
                    await output.WriteLineAsync($"{token.Id}  {token.Name}  [{string.Join(", ", token.Topics)}]  {used}");
                }
                return 0;
            case [Delete, var id]:
                if (await PublishTokens.DeleteAsync(db, id, CancellationToken.None))
                {
                    await output.WriteLineAsync($"Token {id} gelöscht.");
                    return 0;
                }
                await error.WriteLineAsync($"Token {id} gibt es nicht.");
                return 1;
            case [NewAccessKey]:
            {
                var keys = scope.ServiceProvider.GetRequiredService<AccessKeyStore>();
                if (!keys.IsActive)
                {
                    await error.WriteLineAsync("Dieser Server meldet über den Identity-Server an (Oidc:Issuer), ein Zugangsschlüssel gilt hier nicht.");
                    return 1;
                }
                await output.WriteLineAsync("Neuer Zugangsschlüssel, der alte gilt nicht mehr:");
                await output.WriteLineAsync(keys.Create());
                await output.WriteLineAsync("Er wird nur dieses eine Mal angezeigt.");
                return 0;
            }
            default:
                await error.WriteLineAsync(Usage);
                return 2;
        }
    }
}
