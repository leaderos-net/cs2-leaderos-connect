using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace LeaderosConnect;

/// <summary>
/// LeaderosConnect - CounterStrikeSharp plugin for real-time server communication
/// via WebSocket (Pusher protocol). Handles command delivery, player queue management,
/// and automatic reconnection.
/// </summary>
public class LeaderosConnect : BasePlugin, IPluginConfig<LeaderosConfig>
{
    public override string ModuleName    => "LeaderosConnect";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor  => "LeaderOS";
    public override string ModuleDescription => "Connect your CS2 server to LeaderOS, enabling you to execute commands on your server directly from the website.";

    #region Constants

    private const string AppKey               = "leaderos-connect";
    private const string Host                 = "connect-socket.leaderos.net:6001";
    private const string AuthEndpoint         = "https://connect-api.leaderos.net/broadcasting/auth";
    private const int    PingInterval         = 30;  // seconds between keepalive pings
    private const int    PongTimeout          = 10;  // seconds to wait for pong before reconnecting
    private const int    ReconnectDelay       = 5;   // base reconnect delay in seconds
    private const int    MaxReconnectAttempts = 10;
    private const string QueueFile            = "queue.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Configuration

    public LeaderosConfig Config { get; set; } = new();

    public void OnConfigParsed(LeaderosConfig config)
    {
        Config = config;
    }

    #endregion

    #region Fields

    private ClientWebSocket?         _webSocket;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _pingCts;
    private CancellationTokenSource? _pongTimeoutCts;
    private CancellationTokenSource? _reconnectCts;

    private string _socketId    = string.Empty;
    private string _channelName = string.Empty;
    private string _socketUrl   = string.Empty;

    private bool     _isConnected      = false;
    private bool     _isAuthenticated  = false;
    private bool     _shouldReconnect  = true;
    private bool     _waitingForPong   = false;
    private int      _reconnectAttempts = 0;
    private DateTime _lastPongReceived  = DateTime.UtcNow;

    private readonly HttpClient _httpClient = new();

    // Offline command queue: SteamID64 -> pending commands
    private Dictionary<string, List<string>> _commandQueue = new();
    private readonly object _queueLock = new();

    #endregion

    #region Plugin Lifecycle

    public override void Load(bool hotReload)
    {
        if (!ValidateConfiguration())
        {
            Console.WriteLine("[LeaderosConnect] ❌ Configuration validation failed. Plugin will not connect.");
            return;
        }

        _channelName = $"private-servers.{Config.ServerToken}";
        _socketUrl   = $"ws://{Host}/app/{AppKey}?protocol=7&client=cs-cssharp&version=1.0";

        LoadQueue();
        RegisterAdminCommands();

        // Process queued commands when a player connects
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        // Connect after a short delay so the server finishes initializing
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            await ConnectToWebSocket();
        });

        Console.WriteLine("[LeaderosConnect] 🚀 Plugin loaded successfully!");
    }

    public override void Unload(bool hotReload)
    {
        _shouldReconnect = false;
        SaveQueue();
        DisconnectWebSocket();
        _httpClient.Dispose();
        Console.WriteLine("[LeaderosConnect] 🔌 Plugin unloaded.");
    }

    #endregion

    #region Configuration Validation

    /// <summary>
    /// Ensures all required config fields are present and valid before connecting.
    /// </summary>
    private bool ValidateConfiguration()
    {
        bool valid = true;

        if (string.IsNullOrWhiteSpace(Config.WebsiteUrl))
        {
            Console.WriteLine("[LeaderosConnect] ❌ WebsiteUrl is not set in configuration.");
            valid = false;
        }
        else if (Config.WebsiteUrl.StartsWith("http://"))
        {
            Console.WriteLine("[LeaderosConnect] ❌ WebsiteUrl must use HTTPS.");
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            Console.WriteLine("[LeaderosConnect] ❌ ApiKey is not set in configuration.");
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(Config.ServerToken))
        {
            Console.WriteLine("[LeaderosConnect] ❌ ServerToken is not set in configuration.");
            valid = false;
        }

        return valid;
    }

    #endregion

    #region Queue Management

    /// <summary>
    /// Loads the offline command queue from disk on plugin startup.
    /// </summary>
    private void LoadQueue()
    {
        try
        {
            string path = GetQueuePath();
            if (!File.Exists(path)) return;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;

            _commandQueue = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, JsonOptions)
                            ?? new Dictionary<string, List<string>>();

            Console.WriteLine($"[LeaderosConnect] 📋 Loaded {_commandQueue.Count} queued entries from disk.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error loading queue: {ex.Message}");
            _commandQueue = new Dictionary<string, List<string>>();
        }
    }

    /// <summary>
    /// Saves the offline command queue to disk so it survives plugin reloads.
    /// </summary>
    private void SaveQueue()
    {
        try
        {
            lock (_queueLock)
            {
                string dir = Path.GetDirectoryName(GetQueuePath())!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(GetQueuePath(), JsonSerializer.Serialize(_commandQueue, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error saving queue: {ex.Message}");
        }
    }

    private string GetQueuePath() => Path.Combine(ModuleDirectory, "data", QueueFile);

    /// <summary>
    /// Adds commands to the offline queue for a player who is not currently connected.
    /// </summary>
    private void AddToQueue(string steamId, List<string> commands)
    {
        try
        {
            lock (_queueLock)
            {
                if (_commandQueue.ContainsKey(steamId))
                    _commandQueue[steamId].AddRange(commands);
                else
                    _commandQueue[steamId] = new List<string>(commands);

                SaveQueue();
                Console.WriteLine($"[LeaderosConnect] 📝 Queued {commands.Count} command(s) for SteamID: {steamId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error adding to queue: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes and removes all queued commands for a player who just connected.
    /// </summary>
    private void ProcessQueueForPlayer(string steamId)
    {
        try
        {
            lock (_queueLock)
            {
                if (!_commandQueue.ContainsKey(steamId)) return;

                var commands = _commandQueue[steamId];
                Console.WriteLine($"[LeaderosConnect] 🎯 Processing {commands.Count} queued command(s) for: {steamId}");

                ExecuteCommands(commands, steamId);

                _commandQueue.Remove(steamId);
                SaveQueue();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error processing queue for {steamId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if a player with the given SteamID64 is currently in the server.
    /// </summary>
    private bool IsPlayerOnline(string steamId)
    {
        try
        {
            foreach (var p in Utilities.GetPlayers())
                if (p is { IsValid: true, IsBot: false } && p.SteamID.ToString() == steamId)
                    return true;
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error checking player status: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Player Events

    /// <summary>
    /// When a player fully connects, check if they have queued commands and run them.
    /// </summary>
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        try
        {
            var player = @event.Userid;
            if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

            string steamId = player.SteamID.ToString();

            if (Config.DebugMode)
                Console.WriteLine($"[LeaderosConnect] 👋 Player connected: {player.PlayerName} ({steamId})");

            if (_commandQueue.ContainsKey(steamId))
            {
                // Small delay to ensure the player is fully spawned before running commands
                Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    ProcessQueueForPlayer(steamId);
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error in OnPlayerConnectFull: {ex.Message}");
        }

        return HookResult.Continue;
    }

    #endregion

    #region Command Processing

    /// <summary>
    /// Handles the "send-commands" WebSocket event by extracting command IDs
    /// and forwarding them to the validation API.
    /// </summary>
    private void HandleSendCommandsEvent(JsonElement? data)
    {
        try
        {
            if (data == null)
            {
                Console.WriteLine("[LeaderosConnect] ⚠️ Null data in send-commands event.");
                return;
            }

            // Pusher may wrap event data as a JSON string — unwrap if needed
            string dataStr = data.Value.ValueKind == JsonValueKind.String
                ? data.Value.GetString()!
                : data.Value.GetRawText();

            if (Config.DebugMode)
                Console.WriteLine($"[LeaderosConnect] 📋 send-commands data: {dataStr}");

            var payload = JsonSerializer.Deserialize<CommandEventData>(dataStr, JsonOptions);

            if (payload?.commands == null || payload.commands.Length == 0)
            {
                Console.WriteLine("[LeaderosConnect] ⚠️ No command IDs received.");
                return;
            }

            // Fire-and-forget async validation
            _ = ValidateAndExecuteCommandsAsync(payload.commands);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error in HandleSendCommandsEvent: {ex.Message}");
        }
    }

    /// <summary>
    /// POSTs command IDs to the LeaderOS validation endpoint.
    /// Runs on a background thread — does NOT touch game objects directly.
    /// </summary>
    private async Task ValidateAndExecuteCommandsAsync(string[] commandIds)
    {
        try
        {
            // Build application/x-www-form-urlencoded body
            var form = new List<string> { $"token={Uri.EscapeDataString(Config.ServerToken)}" };
            for (int i = 0; i < commandIds.Length; i++)
                form.Add($"{Uri.EscapeDataString($"commands[{i}]")}={Uri.EscapeDataString(commandIds[i])}");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Config.WebsiteUrl}/api/command-logs/validate");
            request.Headers.Add("X-API-Key", Config.ApiKey);
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(string.Join("&", form), Encoding.UTF8, "application/x-www-form-urlencoded");

            if (Config.DebugMode)
                Console.WriteLine($"[LeaderosConnect] 🔍 Validating commands: {string.Join(", ", commandIds)}");

            var response = await _httpClient.SendAsync(request);
            string body  = await response.Content.ReadAsStringAsync();

            if (Config.DebugMode)
                Console.WriteLine($"[LeaderosConnect] 🔍 Validation response ({(int)response.StatusCode}): {body}");

            if (response.IsSuccessStatusCode)
            {
                // HandleValidationResponse only parses JSON and calls AddTimer — safe from any thread
                HandleValidationResponse(body);
            }
            else
            {
                Console.WriteLine($"[LeaderosConnect] ❌ Validation failed. HTTP {(int)response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Exception in ValidateAndExecuteCommandsAsync: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses validated commands and either executes them immediately or queues them
    /// if the target player is offline (and CheckPlayerOnline is enabled).
    /// Safe to call from any thread — uses AddTimer for actual command execution.
    /// </summary>
    private void HandleValidationResponse(string response)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                Console.WriteLine("[LeaderosConnect] ⚠️ Empty validation response.");
                return;
            }

            var result = JsonSerializer.Deserialize<ValidationResponse>(response, JsonOptions);

            if (result?.commands == null || result.commands.Length == 0)
            {
                Console.WriteLine("[LeaderosConnect] ⚠️ No commands in validation response.");
                return;
            }

            string steamId      = string.Empty;
            var    commandsList = new List<string>();

            foreach (var item in result.commands)
            {
                if (string.IsNullOrWhiteSpace(item.command)) continue;
                commandsList.Add(item.command);

                // The "username" field contains the SteamID64 string
                if (string.IsNullOrEmpty(steamId) && !string.IsNullOrEmpty(item.username))
                    steamId = item.username;
            }

            if (commandsList.Count == 0 || string.IsNullOrEmpty(steamId))
            {
                Console.WriteLine("[LeaderosConnect] ⚠️ No executable commands or missing SteamID.");
                return;
            }

            if (Config.CheckPlayerOnline)
            {
                if (IsPlayerOnline(steamId))
                {
                    Console.WriteLine($"[LeaderosConnect] 👤 Player {steamId} is online — executing now.");
                    ExecuteCommands(commandsList, steamId);
                }
                else
                {
                    Console.WriteLine($"[LeaderosConnect] 💤 Player {steamId} is offline — queuing commands.");
                    AddToQueue(steamId, commandsList);
                }
            }
            else
            {
                // CheckPlayerOnline disabled: always execute immediately
                ExecuteCommands(commandsList, steamId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error in HandleValidationResponse: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedules each command to run on the main game thread via Server.NextFrame.
    /// Server.NextFrame is safe to call from background threads.
    /// </summary>
    private void ExecuteCommands(List<string> commands, string identifier)
    {
        Console.WriteLine($"[LeaderosConnect] ⚡ Executing {commands.Count} command(s) for: {identifier}");

        foreach (string cmd in commands)
        {
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            string currentCmd = cmd;
            Server.NextFrame(() =>
            {
                try
                {
                    Server.ExecuteCommand(currentCmd);
                    Console.WriteLine($"[LeaderosConnect] ✅ Executed: {currentCmd}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LeaderosConnect] ❌ Failed to execute '{currentCmd}': {ex.Message}");
                }
            });
        }

        Console.WriteLine($"[LeaderosConnect] 🎉 All commands dispatched for: {identifier}");
    }

    #endregion

    #region WebSocket Connection

    /// <summary>
    /// Connects to the LeaderOS WebSocket server.
    /// On failure, retries with exponential back-off up to MaxReconnectAttempts.
    /// </summary>
    private async Task ConnectToWebSocket()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Max reconnect attempts ({MaxReconnectAttempts}) reached.");
            return;
        }

        try
        {
            if (_webSocket != null && _webSocket.State != WebSocketState.Closed)
                await CleanupWebSocket();

            _webSocket = new ClientWebSocket();
            _cts       = new CancellationTokenSource();

            Console.WriteLine($"[LeaderosConnect] 🔌 Connecting... (attempt {_reconnectAttempts + 1}/{MaxReconnectAttempts})");

            var connectTask = _webSocket.ConnectAsync(new Uri(_socketUrl), _cts.Token);
            var timeoutTask = Task.Delay(10_000, _cts.Token);

            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                throw new TimeoutException("Connection timed out.");

            await connectTask;

            _isConnected       = true;
            _reconnectAttempts = 0;

            Console.WriteLine("[LeaderosConnect] ✅ WebSocket connected!");

            _ = Task.Run(() => ReceiveMessages(_cts.Token));
            StartKeepAlive();
        }
        catch (Exception ex)
        {
            _reconnectAttempts++;
            Console.WriteLine($"[LeaderosConnect] ❌ Connection failed: {ex.Message}");

            if (_shouldReconnect && _reconnectAttempts < MaxReconnectAttempts)
            {
                int delay = Math.Min(ReconnectDelay * _reconnectAttempts, 60);
                Console.WriteLine($"[LeaderosConnect] ⏳ Retrying in {delay}s...");
                ScheduleReconnect(delay * 1000);
            }
        }
    }

    /// <summary>
    /// Gracefully closes and disposes the current WebSocket instance.
    /// </summary>
    private async Task CleanupWebSocket()
    {
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
                await Task.WhenAny(
                    _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None),
                    Task.Delay(3000));

            _webSocket?.Dispose();
            _webSocket = null;
        }
        catch (Exception ex)
        {
            if (Config.DebugMode)
                Console.WriteLine($"[LeaderosConnect] 🔧 Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels all background tasks and closes the WebSocket cleanly on unload.
    /// </summary>
    private void DisconnectWebSocket()
    {
        try
        {
            StopKeepAlive();

            _reconnectCts?.Cancel();
            _reconnectCts = null;

            _isConnected     = false;
            _isAuthenticated = false;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open)
                    Task.WhenAny(
                        _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin unloaded", CancellationToken.None),
                        Task.Delay(3000)).Wait(3000);

                _webSocket.Dispose();
                _webSocket = null;
            }

            Console.WriteLine("[LeaderosConnect] 🔌 WebSocket disconnected.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error during disconnect: {ex.Message}");
        }
    }

    #endregion

    #region Keep-Alive

    /// <summary>
    /// Starts a background loop that sends a Pusher ping every PingInterval seconds.
    /// </summary>
    private void StartKeepAlive()
    {
        _lastPongReceived = DateTime.UtcNow;
        _waitingForPong   = false;

        _pingCts = new CancellationTokenSource();
        var token = _pingCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PingInterval * 1000, token);
                if (!token.IsCancellationRequested)
                    SendKeepAlivePing();
            }
        }, token);
    }

    private void StopKeepAlive()
    {
        _pingCts?.Cancel();
        _pongTimeoutCts?.Cancel();
        _waitingForPong = false;
    }

    /// <summary>
    /// Sends a Pusher ping and arms a pong timeout watchdog.
    /// If no pong arrives within PongTimeout seconds, reconnection is triggered.
    /// </summary>
    private void SendKeepAlivePing()
    {
        if (!_isConnected || _webSocket?.State != WebSocketState.Open) return;

        // If still waiting from the previous ping, the connection is dead
        if (_waitingForPong && (DateTime.UtcNow - _lastPongReceived).TotalSeconds > PongTimeout)
        {
            Console.WriteLine("[LeaderosConnect] ⚠️ Pong timeout — reconnecting...");
            HandleConnectionLost();
            return;
        }

        _waitingForPong = true;
        _ = SendRawJsonAsync("{\"event\":\"pusher:ping\",\"data\":{}}");

        // Arm pong timeout watchdog
        _pongTimeoutCts?.Cancel();
        _pongTimeoutCts = new CancellationTokenSource();
        var token = _pongTimeoutCts.Token;

        Task.Run(async () =>
        {
            await Task.Delay(PongTimeout * 1000, token);
            if (!token.IsCancellationRequested && _waitingForPong)
            {
                Console.WriteLine("[LeaderosConnect] ⚠️ No pong — reconnecting...");
                HandleConnectionLost();
            }
        }, token);
    }

    private void HandlePongReceived()
    {
        _waitingForPong   = false;
        _lastPongReceived = DateTime.UtcNow;
        _pongTimeoutCts?.Cancel();

        if (Config.DebugMode)
            Console.WriteLine("[LeaderosConnect] 💓 Pong received — connection alive.");
    }

    /// <summary>
    /// Marks the connection as lost and schedules a reconnect attempt.
    /// Safe to call from any thread.
    /// </summary>
    private void HandleConnectionLost()
    {
        _isConnected     = false;
        _isAuthenticated = false;

        Console.WriteLine("[LeaderosConnect] 🔄 Connection lost — reconnecting...");

        if (_shouldReconnect)
            ScheduleReconnect(ReconnectDelay * 1000);
    }

    private void ScheduleReconnect(int delayMs)
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        Task.Run(async () =>
        {
            await Task.Delay(delayMs, token);
            if (!token.IsCancellationRequested)
                await ConnectToWebSocket();
        }, token);
    }

    #endregion

    #region Message Receiving

    /// <summary>
    /// Background loop that reads WebSocket frames and dispatches them for processing.
    /// HandleMessage is called directly (no NextFrame) — safe because it only parses
    /// JSON and schedules game operations via AddTimer.
    /// </summary>
    private async Task ReceiveMessages(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var sb     = new StringBuilder();

        try
        {
            while (_webSocket!.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        string message = sb.ToString();
                        sb.Clear();
                        HandleMessage(message);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("[LeaderosConnect] 🔌 Server closed the WebSocket.");
                    HandleConnectionLost();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (Config.DebugMode)
                Console.WriteLine("[LeaderosConnect] 🔧 Receive loop cancelled.");
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[LeaderosConnect] ⚠️ WebSocket error: {ex.Message}");
            HandleConnectionLost();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Unexpected receive error: {ex.Message}");
            HandleConnectionLost();
        }
    }

    /// <summary>
    /// Parses a raw WebSocket message and routes it to the correct handler.
    /// </summary>
    private void HandleMessage(string raw)
    {
        try
        {
            if (Config.DebugMode)
                Console.WriteLine($"[LeaderosConnect] 📨 Message: {raw}");

            if (string.IsNullOrWhiteSpace(raw)) return;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            string eventName = root.TryGetProperty("event",   out var ev) ? ev.GetString() ?? "" : "";
            string channel   = root.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "";
            JsonElement? data = root.TryGetProperty("data",   out var d)  ? d : null;

            switch (eventName)
            {
                case "pusher:connection_established":
                    HandleConnectionEstablished(data);
                    break;

                case "pusher:subscription_succeeded":
                    _isAuthenticated = true;
                    Console.WriteLine($"[LeaderosConnect] 🎉 Subscribed to channel: {channel}");
                    break;

                case "pusher:subscription_error":
                    Console.WriteLine($"[LeaderosConnect] ❌ Subscription error on: {channel}");
                    break;

                case "pusher:pong":
                    HandlePongReceived();
                    break;

                case "ping":
                    Console.WriteLine("[LeaderosConnect] ✅ Ping received from server.");
                    break;

                case "send-commands":
                    HandleSendCommandsEvent(data);
                    break;

                default:
                    if (Config.DebugMode)
                        Console.WriteLine($"[LeaderosConnect] 🎯 Unknown event: {eventName}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error handling message: {ex.Message}");
        }
    }

    #endregion

    #region Authentication

    /// <summary>
    /// Handles pusher:connection_established by extracting socket_id
    /// and starting the authentication flow.
    /// </summary>
    private void HandleConnectionEstablished(JsonElement? data)
    {
        try
        {
            if (data == null) return;

            // Pusher sends connection data as a JSON string inside the data field
            string dataStr = data.Value.ValueKind == JsonValueKind.String
                ? data.Value.GetString()!
                : data.Value.GetRawText();

            using var doc = JsonDocument.Parse(dataStr);
            _socketId = doc.RootElement.TryGetProperty("socket_id", out var sid)
                ? sid.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrEmpty(_socketId))
            {
                Console.WriteLine($"[LeaderosConnect] 🆔 Socket ID: {_socketId}");
                _ = AuthenticateAndSubscribeAsync();
            }
            else
            {
                Console.WriteLine("[LeaderosConnect] ❌ No socket_id in connection_established.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error in HandleConnectionEstablished: {ex.Message}");
        }
    }

    /// <summary>
    /// POSTs socket_id and channel_name to the LeaderOS auth endpoint to get
    /// a Pusher auth token, then subscribes to the private channel.
    /// Falls back to direct subscription if auth fails.
    /// </summary>
    private async Task AuthenticateAndSubscribeAsync()
    {
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                socket_id    = _socketId,
                channel_name = _channelName
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, AuthEndpoint);
            request.Headers.Add("X-API-Key", Config.ApiKey);
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            if (Config.DebugMode)
                Console.WriteLine("[LeaderosConnect] 🔐 Authenticating...");

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                Console.WriteLine("[LeaderosConnect] ✅ Authentication successful!");

                if (Config.DebugMode)
                    Console.WriteLine($"[LeaderosConnect] 🔐 Auth response: {body}");

                using var doc = JsonDocument.Parse(body);

                string auth = doc.RootElement.TryGetProperty("auth", out var authProp)
                    ? authProp.GetString() ?? string.Empty
                    : string.Empty;

                string? channelData = doc.RootElement.TryGetProperty("channel_data", out var cdProp)
                    ? cdProp.GetString()
                    : null;

                if (!string.IsNullOrEmpty(auth))
                    await SubscribeAsync(auth, channelData);
                else
                    await SubscribeDirectAsync();
            }
            else
            {
                Console.WriteLine($"[LeaderosConnect] ❌ Auth failed. HTTP {(int)response.StatusCode}");
                await SubscribeDirectAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Error during authentication: {ex.Message}");
            await SubscribeDirectAsync();
        }
    }

    private async Task SubscribeAsync(string auth, string? channelData = null)
    {
        string json = channelData != null
            ? $"{{\"event\":\"pusher:subscribe\",\"data\":{{\"auth\":\"{auth}\",\"channel\":\"{_channelName}\",\"channel_data\":\"{channelData}\"}}}}"
            : $"{{\"event\":\"pusher:subscribe\",\"data\":{{\"auth\":\"{auth}\",\"channel\":\"{_channelName}\"}}}}";

        await SendRawJsonAsync(json);
    }

    private async Task SubscribeDirectAsync()
    {
        Console.WriteLine("[LeaderosConnect] 🔄 Attempting direct channel subscription...");
        await SendRawJsonAsync($"{{\"event\":\"pusher:subscribe\",\"data\":{{\"channel\":\"{_channelName}\"}}}}");
    }

    #endregion

    #region Message Sending

    /// <summary>
    /// Sends a pre-serialized JSON string over the WebSocket.
    /// On timeout or error, triggers reconnection.
    /// </summary>
    private async Task SendRawJsonAsync(string json)
    {
        try
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                Console.WriteLine("[LeaderosConnect] ❌ Cannot send: WebSocket is not open.");
                return;
            }

            byte[] bytes    = Encoding.UTF8.GetBytes(json);
            var sendTask    = _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            var timeoutTask = Task.Delay(5000);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine("[LeaderosConnect] ❌ Send timeout — reconnecting...");
                HandleConnectionLost();
                return;
            }

            await sendTask;

            if (Config.DebugMode)
                Console.WriteLine("[LeaderosConnect] 📤 Message sent.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LeaderosConnect] ❌ Send error: {ex.Message}");
            HandleConnectionLost();
        }
    }

    #endregion

    #region Admin Commands

    /// <summary>
    /// Registers admin console commands using AddCommand (works from any thread context).
    /// All commands require @css/root permission when called by a player.
    /// </summary>
    private void RegisterAdminCommands()
    {
        // Shows current WebSocket connection status
        AddCommand("leaderos_status", "Show LeaderosConnect status", (player, command) =>
        {
            if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root"))
            {
                command.ReplyToCommand("[LeaderosConnect] No permission.");
                return;
            }

            string conn     = _isConnected ? "Connected" : "Disconnected";
            string lastPong = _isConnected ? $"{(DateTime.UtcNow - _lastPongReceived).TotalSeconds:F1}s ago" : "N/A";

            command.ReplyToCommand("[LeaderosConnect] === Status ===");
            command.ReplyToCommand($"  Connection   : {conn}");
            command.ReplyToCommand($"  Authenticated: {(_isAuthenticated ? "Yes" : "No")}");
            command.ReplyToCommand($"  Socket ID    : {(string.IsNullOrEmpty(_socketId) ? "None" : _socketId)}");
            command.ReplyToCommand($"  Channel      : {_channelName}");
            command.ReplyToCommand($"  Last Pong    : {lastPong}");
            command.ReplyToCommand($"  Reconnects   : {_reconnectAttempts}");
            command.ReplyToCommand($"  Queue entries: {_commandQueue.Count}");
        });

        // Forces an immediate WebSocket reconnection
        AddCommand("leaderos_reconnect", "Force WebSocket reconnection", (player, command) =>
        {
            if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root"))
            {
                command.ReplyToCommand("[LeaderosConnect] No permission.");
                return;
            }

            _reconnectAttempts = 0;
            DisconnectWebSocket();
            command.ReplyToCommand("[LeaderosConnect] 🔄 Reconnecting...");

            Task.Run(async () =>
            {
                await Task.Delay(1000);
                await ConnectToWebSocket();
            });
        });

        // Toggles verbose debug logging on/off at runtime
        AddCommand("leaderos_debug", "Toggle debug mode", (player, command) =>
        {
            if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root"))
            {
                command.ReplyToCommand("[LeaderosConnect] No permission.");
                return;
            }

            Config.DebugMode = !Config.DebugMode;
            command.ReplyToCommand($"[LeaderosConnect] 🔧 Debug mode: {(Config.DebugMode ? "Enabled" : "Disabled")}");
        });
    }

    #endregion

    #region Data Classes

    public class CommandEventData
    {
        [JsonPropertyName("commands")]
        public string[] commands { get; set; } = Array.Empty<string>();
    }

    public class ValidationResponse
    {
        [JsonPropertyName("commands")]
        public ValidatedCommand[] commands { get; set; } = Array.Empty<ValidatedCommand>();
    }

    public class ValidatedCommand
    {
        [JsonPropertyName("command")]
        public string command { get; set; } = string.Empty;

        /// <summary>SteamID64 string of the target player.</summary>
        [JsonPropertyName("username")]
        public string username { get; set; } = string.Empty;
    }

    #endregion
}

/// <summary>
/// Plugin configuration — auto-generated at:
/// addons/counterstrikesharp/configs/plugins/LeaderosConnect/LeaderosConnect.json
/// </summary>
public class LeaderosConfig : BasePluginConfig
{
    [JsonPropertyName("WebsiteUrl")]
    public string WebsiteUrl { get; set; } = "";

    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("ServerToken")]
    public string ServerToken { get; set; } = "";

    [JsonPropertyName("DebugMode")]
    public bool DebugMode { get; set; } = false;

    [JsonPropertyName("CheckPlayerOnline")]
    public bool CheckPlayerOnline { get; set; } = true;
}