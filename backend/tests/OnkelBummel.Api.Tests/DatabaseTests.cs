using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Endpoints;

namespace OnkelBummel.Api.Tests;

public sealed class MessageSearchTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Finds_words_by_prefix_ignoring_case_and_accents()
    {
        await using var db = _factory.CreateDbContext();
        var printed = await AddAsync(db, "Druck fertig", "Benchy ist fertig.", ["white_check_mark"]);
        var door = await AddAsync(db, "Garage", "Die Tür ist offen.", []);

        Assert.Equal([printed], await SearchAsync(db, "druck fert"));
        Assert.Equal([door], await SearchAsync(db, "TUR"));
        Assert.Equal([printed], await SearchAsync(db, "white_check"));
        Assert.Empty(await SearchAsync(db, "druck offen"));
    }

    [Fact]
    public async Task Follows_changes_and_deletions()
    {
        await using var db = _factory.CreateDbContext();
        var id = await AddAsync(db, "Backup", "läuft", []);
        var message = await db.Messages.SingleAsync(m => m.Id == id, TestContext.Current.CancellationToken);

        message.Body = "fehlgeschlagen";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await SearchAsync(db, "läuft"));
        Assert.Equal([id], await SearchAsync(db, "fehl"));

        db.Messages.Remove(message);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await SearchAsync(db, "backup"));
    }

    [Theory]
    [InlineData("drucker fertig", "\"drucker\"* \"fertig\"*")]
    [InlineData("  a \"b\"  ", "\"a\"* \"b\"*")]
    [InlineData("NOT OR -x", "\"NOT\"* \"OR\"* \"-x\"*")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Quotes_every_word_so_fts_syntax_is_taken_literally(string? input, string? query) =>
        Assert.Equal(query, MessageSearch.ToQuery(input));

    [Fact]
    public async Task Operators_in_the_input_do_not_break_the_query()
    {
        await using var db = _factory.CreateDbContext();
        await AddAsync(db, null, "a AND b", []);

        Assert.Empty(await SearchAsync(db, "NEAR( \" * ^"));
    }

    private static async Task<long> AddAsync(OnkelBummelDbContext db, string? title, string body, List<string> tags)
    {
        if (await db.Topics.FindAsync(["test"], TestContext.Current.CancellationToken) is null)
            db.Topics.Add(new Topic { Name = "test" });
        var message = new Message { Topic = "test", Title = title, Body = body, Tags = tags, Source = "api", CreatedAt = DateTime.UtcNow };
        db.Messages.Add(message);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return message.Id;
    }

    private static Task<List<long>> SearchAsync(OnkelBummelDbContext db, string input) =>
        db.Database.SqlQuery<long>($"""SELECT rowid AS "Value" FROM "MessagesFts" WHERE "MessagesFts" MATCH {MessageSearch.ToQuery(input)} ORDER BY rowid""")
            .ToListAsync(TestContext.Current.CancellationToken);
}

public sealed class DatabaseSetupTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Uses_write_ahead_logging_and_deletes_messages_with_their_topic()
    {
        await using var db = _factory.CreateDbContext();
        var mode = await db.Database.SqlQueryRaw<string>("""SELECT journal_mode AS "Value" FROM pragma_journal_mode""").SingleAsync(TestContext.Current.CancellationToken);
        db.Topics.Add(new Topic { Name = "alt" });
        db.Messages.Add(new Message { Topic = "alt", Body = "x", Source = "api", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Topics.Remove(await db.Topics.SingleAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("wal", mode);
        Assert.Equal(0, await db.Messages.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_second_message_with_the_same_idempotency_key()
    {
        await using var db = _factory.CreateDbContext();
        db.Topics.Add(new Topic { Name = "t" });
        db.Messages.Add(new Message { Topic = "t", Body = "1", Source = "api", IdempotencyKey = "k", CreatedAt = DateTime.UtcNow });
        db.Messages.Add(new Message { Topic = "t", Body = "2", Source = "api", CreatedAt = DateTime.UtcNow });
        db.Messages.Add(new Message { Topic = "t", Body = "3", Source = "api", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Messages.Add(new Message { Topic = "t", Body = "4", Source = "api", IdempotencyKey = "k", CreatedAt = DateTime.UtcNow });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task Stores_action_headers_and_tags_as_json()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Topics.Add(new Topic { Name = "filomat" });
            var message = new Message { Topic = "filomat", Body = "fertig", Source = "api", Tags = ["tada", "drucker"], CreatedAt = DateTime.UtcNow };
            message.Actions.Add(new MessageAction { Index = 0, Label = "Weiter", Url = "http://fil-o-mat/next", Headers = new() { ["X-Key"] = "1" } });
            db.Messages.Add(message);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = _factory.CreateDbContext();
        var stored = await read.Messages.Include(m => m.Actions).SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["tada", "drucker"], stored.Tags);
        Assert.Equal("1", Assert.Single(stored.Actions).Headers["X-Key"]);
    }
}

public class TopicNameTests
{
    [Theory]
    [InlineData("garage", true)]
    [InlineData("fil-o-mat_2", true)]
    [InlineData("Garage", false)]
    [InlineData("haus/garage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("api", false)]
    [InlineData("*", false)]
    public void Accepts_short_lower_case_names_except_reserved_ones(string? name, bool valid) =>
        Assert.Equal(valid, TopicNames.IsValid(name));

    [Fact]
    public void Rejects_names_longer_than_64_characters()
    {
        Assert.True(TopicNames.IsValid(new string('a', 64)));
        Assert.False(TopicNames.IsValid(new string('a', 65)));
    }
}

public sealed class ConfigEndpointTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Tells_the_app_where_to_sign_in_without_login()
    {
        var config = await _factory.CreateClient().GetFromJsonAsync<ClientConfig>("/api/v1/config", TestContext.Current.CancellationToken);

        Assert.NotNull(config);
        Assert.Equal(new Uri("https://identity.example.ts.net"), config.Issuer);
        Assert.Equal("onkel-bummel-ios", config.ClientId);
        Assert.Contains("onkel-bummel-api", config.Scopes);
        Assert.Contains("offline_access", config.Scopes);
    }
}
