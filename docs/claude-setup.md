# Connecting Claude

Build a self-contained executable first. `dotnet run` isn't recommended: it can print build messages to stdout, which corrupts the MCP stream, and it starts slowly.

```powershell
dotnet publish src/Meeza.MarketingMcp -c Release -o publish
```

The executable is `publish/meeza-marketing-mcp.exe` (on macOS and Linux it's `publish/meeza-marketing-mcp`).

## Claude Code

```powershell
claude mcp add meeza-marketing -- "C:\path\to\meeza-marketing-mcp\publish\meeza-marketing-mcp.exe"
```

Check that the server is connected with `/mcp` inside Claude Code.

## Claude Desktop

Edit `claude_desktop_config.json`:

- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "meeza-marketing": {
      "command": "C:\\path\\to\\meeza-marketing-mcp\\publish\\meeza-marketing-mcp.exe",
      "args": [],
      "env": {
        "META_AD_ACCOUNT_IDS": "act_1234567890",
        "PLAY_PACKAGE_NAMES": "com.example.game"
      }
    }
  }
}
```

Restart Claude Desktop after editing the file.

**Don't put tokens in this file.** Store secrets with `meeza-marketing-mcp secrets set …`. On Windows they are encrypted with DPAPI for your user account, and the server reads them automatically. Only non-secret settings, such as account IDs and package names, belong in `env`. On macOS and Linux, inject secrets from your keychain or secrets manager into the environment Claude starts the server with.

## First prompt

> Which marketing accounts are connected?

Claude calls `get_connected_accounts` and shows what is configured, along with setup hints for anything that isn't.

## Live dashboard

While Claude is connected, the dashboard runs at `http://127.0.0.1:5057`. Ask Claude to *"open the marketing dashboard"* and it opens in your browser with your private access link. See [dashboard.md](dashboard.md).
