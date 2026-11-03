using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Actions;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Monitoring;

namespace OnkelBummel.Api.Tests;

public sealed class ActionTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();
    private HttpClient _sender = null!;
    private HttpClient _user = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _sender = await _factory.CreateSenderClientAsync("filomat");
        _user = _factory.CreateUserClient();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Calls_the_target_with_method_headers_and_body_and_stores_the_result()
    {
        _factory.Remote.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        var before = OnkelBummelMetrics.ActionsExecuted.WithLabels("ok").Value;
        var id = await SendAsync(new
        {
            label = "Nächster Druck",
            url = "http://fil-o-mat:8080/api/jobs/next?printer=1",
            method = "PUT",
            headers = new Dictionary<string, string> { ["X-Api-Key"] = "geheim" },
            body = """{"queue":"main"}""",
        });

        var response = await _user.PostAsync($"/api/v1/messages/{id}/actions/0", null, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ActionRunResult>(CancellationToken))!;
        Assert.Equal((202, true, false), (result.Status, result.Ok, result.MarkedRead));
        var call = Assert.Single(_factory.Remote.Requests);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal("http://fil-o-mat:8080/api/jobs/next?printer=1", call.Uri.ToString());
        Assert.Equal("geheim", call.Headers["X-Api-Key"]);
        Assert.Equal("""{"queue":"main"}""", call.Body);
        Assert.StartsWith("application/json", call.ContentType);
        Assert.True(OnkelBummelMetrics.ActionsExecuted.WithLabels("ok").Value >= before + 1);

        var message = await _user.GetFromJsonAsync<MessageDto>($"/api/v1/messages/{id}", CancellationToken);
        var action = Assert.Single(message!.Actions);
        Assert.Equal(202, action.LastStatus);
        Assert.NotNull(action.LastRunAt);
        Assert.Null(message.ReadAt);
        // Headers and body may hold keys of the target; readers do not get them.
        Assert.DoesNotContain("geheim", await _user.GetStringAsync($"/api/v1/messages/{id}", CancellationToken));
    }

    [Fact]
    public async Task Clear_marks_the_message_read_once_the_call_succeeded()
    {
        var id = await SendAsync(new { label = "Erledigt", url = "http://haus/ok", clear = true });

        _factory.Remote.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var failed = await (await _user.PostAsync($"/api/v1/messages/{id}/actions/0", null, CancellationToken)).Content.ReadFromJsonAsync<ActionRunResult>(CancellationToken);
        var stillUnread = await ReadAtAsync(id);
        _factory.Remote.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        var ok = await (await _user.PostAsync($"/api/v1/messages/{id}/actions/0", null, CancellationToken)).Content.ReadFromJsonAsync<ActionRunResult>(CancellationToken);

        Assert.Equal((500, false, false), (failed!.Status, failed.Ok, failed.MarkedRead));
        Assert.Null(stillUnread);
        Assert.Equal((200, true, true), (ok!.Status, ok.Ok, ok.MarkedRead));
        Assert.NotNull(await ReadAtAsync(id));
        Assert.Equal("POST", _factory.Remote.Requests.Last().Method.Method);
    }

    [Fact]
    public async Task Stores_status_0_when_the_target_does_not_answer()
    {
        _factory.Remote.Respond = _ => throw new HttpRequestException("Connection refused");
        var before = OnkelBummelMetrics.ActionsExecuted.WithLabels("failed").Value;
        var id = await SendAsync(new { label = "Tor auf", url = "http://garage/tor" });

        var result = await (await _user.PostAsync($"/api/v1/messages/{id}/actions/0", null, CancellationToken)).Content.ReadFromJsonAsync<ActionRunResult>(CancellationToken);

        Assert.Equal((0, false), (result!.Status, result.Ok));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, (await db.MessageActions.SingleAsync(CancellationToken)).LastStatus);
        Assert.True(OnkelBummelMetrics.ActionsExecuted.WithLabels("failed").Value >= before + 1);
    }

    [Fact]
    public async Task Unknown_actions_are_404_and_senders_cannot_run_them()
    {
        var id = await SendAsync(new { label = "A", url = "http://haus/a" });

        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostAsync($"/api/v1/messages/{id}/actions/1", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostAsync("/api/v1/messages/999/actions/0", null, CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _sender.PostAsync($"/api/v1/messages/{id}/actions/0", null, CancellationToken)).StatusCode);
        Assert.Empty(_factory.Remote.Requests);
    }

    private async Task<long> SendAsync(object action)
    {
        var response = await _sender.PostAsJsonAsync("/api/v1/messages", new { topic = "filomat", message = "Druck fertig", actions = new[] { action } }, CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!.Id;
    }

    private async Task<DateTime?> ReadAtAsync(long id)
    {
        await using var db = _factory.CreateDbContext();
        return (await db.Messages.SingleAsync(m => m.Id == id, CancellationToken)).ReadAt;
    }
}
