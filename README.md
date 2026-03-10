# LeaderosConnect Plugin for CS2

This plugin allows you to connect your CS2 server to LeaderOS, enabling you to send commands to the server through the LeaderOS platform.

## Requirements

This plugin requires [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) to be installed.

## Installation

### 1. Download the plugin

Download the latest release as a ZIP file from the link below and extract it:

[https://www.leaderos.net/plugin/cs](https://www.leaderos.net/plugin/cs)

### 2. Upload the plugin

Copy the `LeaderosConnect` folder from the extracted ZIP into your server's CounterStrikeSharp plugins directory:

```
game/csgo/addons/counterstrikesharp/plugins/LeaderosConnect/
```

The folder should contain:

```
LeaderosConnect/
├── LeaderosConnect.dll
```

### 3. Configure the plugin

Start your server once to auto-generate the config file, then open it:

```
game/csgo/addons/counterstrikesharp/configs/plugins/LeaderosConnect/LeaderosConnect.json
```

Fill in your credentials:

```json
{
    "WebsiteUrl":        "https://yourwebsite.com",
    "ApiKey":            "YOUR_API_KEY_HERE",
    "ServerToken":       "YOUR_SERVER_TOKEN_HERE",
    "DebugMode":         false,
    "CheckPlayerOnline": true,
    "ConfigVersion":     1
}
```

### 4. Required server configuration

Add this to your `game/csgo/cfg/server.cfg`:

```
sv_hibernate_when_empty 0
```

Without this, the server enters sleep mode when no players are connected and stops processing game ticks. This prevents commands from executing on an empty server.

### 5. Restart your server

Restart your server. The plugin is now active. Run `leaderos_status` in the server console to confirm everything is working.

## Configuration

| Option | Description |
|---|---|
| `WebsiteUrl` | The URL of your LeaderOS website (e.g., `https://yourwebsite.com`). Must start with `https://`. |
| `ApiKey` | Your LeaderOS API key. Find it on `Dashboard > Settings > API`. |
| `ServerToken` | Your server token. Find it on `Dashboard > Store > Servers > Your Server > Server Token`. |
| `DebugMode` | Set to `true` to enable verbose debug logging, or `false` to disable it. |
| `CheckPlayerOnline` | Set to `true` to queue commands for offline players and deliver them on next login. Set to `false` to execute commands regardless of whether the target player is online. |

## Console Commands

All commands require `@css/root` admin permission when run by an in-game player. They can be run freely from the server console (RCON).

| Command | Description |
|---|---|
| `leaderos_status` | Displays the current connection state, socket ID, channel, last pong time, and queue size. |
| `leaderos_reconnect` | Forces an immediate WebSocket disconnection and reconnection. |
| `leaderos_debug` | Toggles debug mode on or off at runtime. |

## Building from Source

1. Clone this repository:
   ```bash
   git clone https://github.com/leaderos-net/cs-leaderos-connect.git
   cd cs-leaderos-connect
   ```

2. Install the .NET 8 SDK from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).

3. Build the project:
   ```bash
   dotnet restore
   dotnet build -c Release
   ```

4. Copy the resulting `LeaderosConnect.dll` from `bin/Release/net8.0/` to your server's plugin directory.