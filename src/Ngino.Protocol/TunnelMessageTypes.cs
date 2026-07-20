namespace ReverseLlama.Protocol;

public static class TunnelMessageTypes
{
    public const string HttpRequest = "http.request";
    public const string HttpRequestBody = "http.request.body";
    public const string HttpRequestComplete = "http.request.complete";
    public const string HttpResponseHeaders = "http.response.headers";
    public const string HttpResponseBody = "http.response.body";
    public const string HttpResponseComplete = "http.response.complete";
    public const string ModelSnapshot = "models.snapshot";
    public const string ModelCommand = "models.command";
    public const string ModelCommandResult = "models.command.result";
    public const string Cancel = "cancel";
    public const string Error = "error";
}
