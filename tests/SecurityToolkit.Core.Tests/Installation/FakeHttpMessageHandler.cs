namespace SecurityToolkit.Core.Tests.Installation;

/// <summary>
/// Deterministic <see cref="HttpMessageHandler"/> test double: returns a scripted response (or
/// throws) for every request, and records every request URI so tests can assert what was
/// actually requested. Never performs a real network call.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public List<Uri> RequestedUris { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!);
        return Task.FromResult(_respond(request));
    }
}
