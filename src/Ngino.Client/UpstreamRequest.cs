using System.Net.Http.Headers;
using System.Threading.Channels;
using Ngino.Protocol;

namespace Ngino.Client;

internal sealed class UpstreamRequest
{
    private static readonly HashSet<string> HeadersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Expect",
        "Host",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        ProtocolConstants.TokenHeader,
        ProtocolConstants.ModelHeader
    };

    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Channel<byte[]> _requestBody = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly HttpClient _httpClient;
    private readonly TunnelMessage _initialMessage;
    private readonly Action<string> _onComplete;
    private readonly Uri _upstream;
    private readonly Func<TunnelMessage, CancellationToken, Task> _sendAsync;
    private readonly Func<HttpResponseMessage, CancellationToken, Task>? _responseHandler;
    private readonly Func<string, string?>? _pathTransform;
    private readonly Func<byte[], byte[]>? _bodyTransform;
    private readonly List<byte[]> _bufferedBody = [];

    public UpstreamRequest(
        ClientOptions options,
        HttpClient httpClient,
        TunnelMessage initialMessage,
        Func<TunnelMessage, CancellationToken, Task> sendAsync,
        Action<string> onComplete,
        CancellationToken cancellationToken,
        Uri? effectiveUpstream = null,
        Func<HttpResponseMessage, CancellationToken, Task>? responseHandler = null,
        Func<string, string?>? pathTransform = null,
        Func<byte[], byte[]>? bodyTransform = null)
    {
        _httpClient = httpClient;
        _initialMessage = initialMessage;
        _sendAsync = sendAsync;
        _onComplete = onComplete;
        _upstream = effectiveUpstream ?? options.Upstream;
        _responseHandler = responseHandler;
        _pathTransform = pathTransform;
        _bodyTransform = bodyTransform;
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!initialMessage.HasBody)
        {
            _requestBody.Writer.TryComplete();
        }
    }

    public void AddBody(byte[] body)
    {
        if (body.Length > 0)
        {
            if (_bodyTransform is not null)
            {
                _bufferedBody.Add(body);
            }
            else
            {
                _requestBody.Writer.TryWrite(body);
            }
        }
    }

    public void CompleteBody()
    {
        if (_bodyTransform is not null && _bufferedBody.Count > 0)
        {
            var totalLength = _bufferedBody.Sum(b => b.Length);
            var concatenated = new byte[totalLength];
            var offset = 0;
            foreach (var chunk in _bufferedBody)
            {
                chunk.CopyTo(concatenated, offset);
                offset += chunk.Length;
            }

            var transformed = _bodyTransform(concatenated);
            _requestBody.Writer.TryWrite(transformed);
        }

        _requestBody.Writer.TryComplete();
    }

    public void Cancel()
    {
        _requestBody.Writer.TryComplete();
        _cancellationTokenSource.Cancel();
    }

    public async Task RunAsync()
    {
        try
        {
            using var request = BuildHttpRequest();
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                _cancellationTokenSource.Token);

            if (_responseHandler is not null)
            {
                await _responseHandler(response, _cancellationTokenSource.Token);
            }
            else
            {
                await SendResponseHeadersAsync(response);
                await SendResponseBodyAsync(response);

                await _sendAsync(
                    new TunnelMessage
                    {
                        Type = TunnelMessageTypes.HttpResponseComplete,
                        RequestId = _initialMessage.RequestId
                    },
                    _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SendErrorAsync(exception);
        }
        finally
        {
            _requestBody.Writer.TryComplete();
            _onComplete(_initialMessage.RequestId);
            _cancellationTokenSource.Dispose();
        }
    }

    public static string? ExtractModelName(TunnelMessage message)
    {
        if (message.Headers is null)
        {
            return null;
        }

        foreach (var header in message.Headers)
        {
            if (string.Equals(header.Name, ProtocolConstants.ModelHeader, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(header.Value))
            {
                return header.Value;
            }
        }

        return null;
    }

    private HttpRequestMessage BuildHttpRequest()
    {
        var path = _initialMessage.PathAndQuery ?? "/";
        if (_pathTransform is not null)
        {
            var transformed = _pathTransform(path);
            if (transformed is not null)
            {
                path = transformed;
            }
        }

        var method = new HttpMethod(_initialMessage.Method ?? HttpMethod.Get.Method);
        var request = new HttpRequestMessage(method, BuildUpstreamUri(_upstream, path));

        if (_initialMessage.HasBody)
        {
            request.Content = new ChannelHttpContent(_requestBody.Reader, _cancellationTokenSource.Token);
        }

        foreach (var header in _initialMessage.Headers)
        {
            if (HeadersToSkip.Contains(header.Name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value)
                && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return request;
    }

    internal static Uri BuildUpstreamUri(Uri upstream, string? pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery))
        {
            return upstream;
        }

        if (!IsOriginPathAndQuery(pathAndQuery))
        {
            throw new InvalidOperationException("Tunnel request path must be an origin-form path.");
        }

        var uri = new Uri(upstream, pathAndQuery);
        if (!HasSameOrigin(uri, upstream))
        {
            throw new InvalidOperationException("Tunnel request path resolved outside the configured upstream origin.");
        }

        return uri;
    }

    private static bool IsOriginPathAndQuery(string pathAndQuery) =>
        pathAndQuery.StartsWith("/", StringComparison.Ordinal)
        && !pathAndQuery.StartsWith("//", StringComparison.Ordinal)
        && !pathAndQuery.Contains('\\');

    private static bool HasSameOrigin(Uri uri, Uri upstream) =>
        string.Equals(uri.Scheme, upstream.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.IdnHost, upstream.IdnHost, StringComparison.OrdinalIgnoreCase)
        && uri.Port == upstream.Port;

    private async Task SendResponseHeadersAsync(HttpResponseMessage response)
    {
        await _sendAsync(
            new TunnelMessage
            {
                Type = TunnelMessageTypes.HttpResponseHeaders,
                RequestId = _initialMessage.RequestId,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = CollectResponseHeaders(response)
            },
            _cancellationTokenSource.Token);
    }

    private async Task SendResponseBodyAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(_cancellationTokenSource.Token);
        var buffer = new byte[_upstream switch { _ => 64 * 1024 }];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, _cancellationTokenSource.Token);
            if (bytesRead == 0)
            {
                break;
            }

            await _sendAsync(
                new TunnelMessage
                {
                    Type = TunnelMessageTypes.HttpResponseBody,
                    RequestId = _initialMessage.RequestId,
                    Body = buffer.AsSpan(0, bytesRead).ToArray()
                },
                _cancellationTokenSource.Token);
        }
    }

    private async Task SendErrorAsync(Exception exception)
    {
        try
        {
            await _sendAsync(
                new TunnelMessage
                {
                    Type = TunnelMessageTypes.Error,
                    RequestId = _initialMessage.RequestId,
                    Error = exception.Message
                },
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private static List<HeaderPair> CollectResponseHeaders(HttpResponseMessage response)
    {
        var headers = new List<HeaderPair>();
        AddHeaders(headers, response.Headers);
        AddHeaders(headers, response.Content.Headers);
        return headers;
    }

    private static void AddHeaders(List<HeaderPair> target, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            foreach (var value in header.Value)
            {
                target.Add(new HeaderPair(header.Key, value));
            }
        }
    }
}
