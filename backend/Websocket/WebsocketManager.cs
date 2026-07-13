using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Websocket;

public class WebsocketManager
{
    // each socket gets a send-lock: WebSocket.SendAsync forbids overlapping sends,
    // and broadcasts can be triggered concurrently from many threads.
    private readonly Dictionary<WebSocket, SemaphoreSlim> _authenticatedSockets = [];
    private readonly Dictionary<WebsocketTopic, string> _lastMessage = new();

    public async Task HandleRoute(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            if (!await Authenticate(webSocket).ConfigureAwait(false))
            {
                Log.Warning($"Closing unauthenticated websocket connection from {context.Connection.RemoteIpAddress}");
                await CloseUnauthorizedConnection(webSocket).ConfigureAwait(false);
                return;
            }

            // mark the socket as authenticated
            var sendLock = new SemaphoreSlim(1, 1);
            lock (_authenticatedSockets)
                _authenticatedSockets.Add(webSocket, sendLock);

            // send current state for all topics
            List<KeyValuePair<WebsocketTopic, string>>? lastMessage;
            lock (_lastMessage) lastMessage = _lastMessage.ToList();
            foreach (var message in lastMessage)
                if (message.Key.Type == WebsocketTopic.TopicType.State)
                    await SendMessage(webSocket, sendLock, message.Key, message.Value).ConfigureAwait(false);

            // wait for the socket to disconnect
            await WaitForDisconnected(webSocket).ConfigureAwait(false);
            lock (_authenticatedSockets)
                _authenticatedSockets.Remove(webSocket);
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }

    /// <summary>
    /// Send a message to all authenticated websockets.
    /// </summary>
    /// <param name="topic">The topic of the message to send</param>
    /// <param name="message">The message to send</param>
    public Task SendMessage(WebsocketTopic topic, string message)
    {
        lock (_lastMessage) _lastMessage[topic] = message;
        List<KeyValuePair<WebSocket, SemaphoreSlim>> authenticatedSockets;
        lock (_authenticatedSockets)
        {
            // don't pay for serialization when no ui client is listening
            if (_authenticatedSockets.Count == 0) return Task.CompletedTask;
            authenticatedSockets = _authenticatedSockets.ToList();
        }

        var topicMessage = new TopicMessage(topic, message);
        var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(topicMessage.ToJson()));
        return Task.WhenAll(authenticatedSockets.Select(x => SendMessage(x.Key, x.Value, bytes)));
    }

    /// <summary>
    /// Ensure a websocket sends a valid api key.
    /// </summary>
    /// <param name="socket">The websocket to authenticate.</param>
    /// <returns>True if authenticated, False otherwise.</returns>
    private static async Task<bool> Authenticate(WebSocket socket)
    {
        var apiKey = await ReceiveAuthToken(socket).ConfigureAwait(false);
        return apiKey == EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    /// <summary>
    /// Ignore all messages from the websocket and
    /// wait for it to disconnect.
    /// </summary>
    /// <param name="socket">The websocket to wait for disconnect.</param>
    private static async Task WaitForDisconnected(WebSocket socket)
    {
        try
        {
            var buffer = new byte[1024];
            WebSocketReceiveResult? result = null;
            while (result is not { CloseStatus: not null })
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            await socket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down - send a proper close frame
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warning(e.Message);
        }
    }

    /// <summary>
    /// Send a message to a connected websocket.
    /// </summary>
    /// <param name="socket">The websocket to send the message to.</param>
    /// <param name="sendLock">The socket's send-lock, ensuring sends never overlap.</param>
    /// <param name="topic">The topic of the message to send</param>
    /// <param name="message">The message to send</param>
    private static async Task SendMessage(WebSocket socket, SemaphoreSlim sendLock, WebsocketTopic topic, string message)
    {
        var topicMessage = new TopicMessage(topic, message);
        var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(topicMessage.ToJson()));
        await SendMessage(socket, sendLock, bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Send a message to a connected websocket.
    /// </summary>
    /// <param name="socket">The websocket to send the message to.</param>
    /// <param name="sendLock">The socket's send-lock, ensuring sends never overlap.</param>
    /// <param name="message">The message to send.</param>
    private static async Task SendMessage(WebSocket socket, SemaphoreSlim sendLock, ArraySegment<byte> message)
    {
        try
        {
            await sendLock.WaitAsync(SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, true, SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (Exception e)
        {
            Log.Debug($"Failed to send message to websocket. {e.Message}");
        }
    }

    /// <summary>
    /// Receive an authentication token from a connected websocket.
    /// With timeout after five seconds.
    /// </summary>
    /// <param name="socket">The websocket to receive from.</param>
    /// <returns>The authentication token. Or null if none provided.</returns>
    private static async Task<string?> ReceiveAuthToken(WebSocket socket)
    {
        try
        {
            var buffer = new byte[1024];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
            return result.MessageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Close a websocket connection as unauthorized.
    /// </summary>
    /// <param name="socket">The websocket whose connection to close.</param>
    private static async Task CloseUnauthorizedConnection(WebSocket socket)
    {
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class TopicMessage(WebsocketTopic topic, string message)
    {
        public string Topic { get; } = topic.Name;
        public string Message { get; } = message;
    }
}