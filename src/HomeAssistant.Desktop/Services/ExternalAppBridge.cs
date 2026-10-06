using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Home Assistant's external app bridge - the same one the Android and iOS companion
/// apps speak.
///
/// The frontend decides it is running inside a native app purely by looking for
/// <c>window.externalApp</c> (src/data/external.ts). Once it finds it, two things
/// change:
///
/// 1. Authentication stops being the page's business. Instead of loading tokens from
///    its own storage, the frontend asks the host for one every time it needs it
///    (src/entrypoints/core.ts picks createExternalAuth over getAuth). That is what
///    lets this app move between its internal and external addresses without the
///    dashboard having to log in again - those are different web origins, and the
///    page's own session does not cross them. The host's token does.
///
/// 2. The frontend asks what the app can do, and renders accordingly. Answering
///    <c>config/get</c> with hasSettingsScreen is what puts the "Companion App" row
///    in Settings and in the sidebar.
///
/// The one hard requirement: <c>config/get</c> must always be answered. The frontend
/// awaits it inside createExternalAuth, so a missing reply leaves the dashboard
/// permanently blank rather than merely missing a feature.
/// </summary>
public sealed class ExternalAppBridge
{
    /// <summary>
    /// Defines window.externalApp before any page script runs, and forwards each call
    /// to the host. Confined to the top-level document: Home Assistant hosts add-on
    /// UIs in iframes, and they have no business claiming to be the companion app.
    /// </summary>
    private const string InjectedScript = """
        (function () {
          if (window.top !== window) { return; }
          var send = function (kind, payload) {
            window.chrome.webview.postMessage({ type: 'ha-bridge', kind: kind, payload: payload });
          };
          window.externalApp = {
            getExternalAuth: function (p) { send('getExternalAuth', p); },
            revokeExternalAuth: function (p) { send('revokeExternalAuth', p); },
            externalBus: function (p) { send('externalBus', p); },
          };
        })();
        """;

    private readonly Func<bool, CancellationToken, Task<(string Token, int ExpiresIn)?>> _tokenProvider;
    private readonly Func<CancellationToken, Task> _revoke;
    private readonly Action _showSettings;
    private readonly Func<string, Task> _runScript;

    public ExternalAppBridge(
        Func<bool, CancellationToken, Task<(string Token, int ExpiresIn)?>> tokenProvider,
        Func<CancellationToken, Task> revoke,
        Action showSettings,
        Func<string, Task> runScript)
    {
        _tokenProvider = tokenProvider;
        _revoke = revoke;
        _showSettings = showSettings;
        _runScript = runScript;
    }

    public static Task InstallAsync(CoreWebView2 core) =>
        core.AddScriptToExecuteOnDocumentCreatedAsync(InjectedScript).AsTask();

    /// <summary>
    /// Handles one message from the injected script. Returns false if it was not ours,
    /// so the caller can go on to its own messages.
    /// </summary>
    public bool TryHandle(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "ha-bridge")
        {
            return false;
        }

        var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var payload = root.TryGetProperty("payload", out var p) ? p.GetString() : null;

        switch (kind)
        {
            case "getExternalAuth":
                _ = SupplyTokenAsync(payload);
                return true;
            case "revokeExternalAuth":
                _ = RevokeAsync(payload);
                return true;
            case "externalBus":
                _ = HandleBusAsync(payload);
                return true;
            default:
                return true;
        }
    }

    private async Task SupplyTokenAsync(string? payload)
    {
        // { "callback": "externalAuthSetToken", "force": true }. The callback name is
        // taken from the message rather than assumed: the frontend's own comment calls
        // these constants a contract, and reading it back is free.
        var callback = ReadString(payload, "callback") ?? "externalAuthSetToken";
        var force = ReadBool(payload, "force");

        try
        {
            if (await _tokenProvider(force, CancellationToken.None) is { } token)
            {
                var json = JsonSerializer.Serialize(new
                {
                    access_token = token.Token,
                    expires_in = token.ExpiresIn,
                });

                await _runScript($"window.{callback}(true, {json});");
                return;
            }

            Log.Warn("bridge", "the dashboard asked for a token and the app has none");
            await _runScript($"window.{callback}(false, {{\"message\":\"not signed in\"}});");
        }
        catch (Exception ex)
        {
            Log.Error("bridge", "could not hand a token to the dashboard", ex);
            await _runScript($"window.{callback}(false, {{\"message\":\"token request failed\"}});");
        }
    }

    private async Task RevokeAsync(string? payload)
    {
        var callback = ReadString(payload, "callback") ?? "externalAuthRevokeToken";

        try
        {
            await _revoke(CancellationToken.None);
            await _runScript($"window.{callback}(true);");
        }
        catch (Exception ex)
        {
            Log.Error("bridge", "could not revoke the token for the dashboard", ex);
            await _runScript($"window.{callback}(false);");
        }
    }

    private async Task HandleBusAsync(string? payload)
    {
        if (payload is null)
        {
            return;
        }

        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(payload);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
        var id = message.TryGetProperty("id", out var i) && i.TryGetInt32(out var parsed) ? parsed : (int?)null;

        switch (type)
        {
            case "config/get":
                // Everything this app can do that the frontend cares about. Claiming a
                // capability it does not have would put a button on screen that does
                // nothing, so the list stays honest.
                Log.Info("bridge", "the dashboard asked what this app can do; offering a settings screen");
                await ReplyAsync(id, success: true, new { hasSettingsScreen = true });
                break;

            case "config_screen/show":
                Log.Info("bridge", "the dashboard asked for the app's settings screen");
                _showSettings();
                break;

            case "connection-status":
            case "frontend/loaded":
            case "theme-update":
            case "exoplayer/stop":
                // Notifications, not requests. The frontend only asks questions about
                // capabilities this app advertised, and the only one it advertises is
                // the settings screen, so config/get is the sole message awaiting a
                // reply in practice.
                break;

            default:
                // Anything awaited must be answered or the frontend waits forever. A
                // refusal is recoverable; silence is not.
                Log.Info("bridge", $"declining unsupported bridge message '{type}'");
                await ReplyAsync(id, success: false, result: null, code: "not_supported",
                    error: $"{type} is not supported by this app");
                break;
        }
    }

    private async Task ReplyAsync(
        int? id, bool success, object? result, string? code = null, string? error = null)
    {
        if (id is null)
        {
            return;
        }

        object message = success
            ? new { id, type = "result", success = true, result }
            : new { id, type = "result", success = false, error = new { code, message = error } };

        await _runScript($"window.externalBus({JsonSerializer.Serialize(message)});");
    }

    private static string? ReadString(string? payload, string name)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadBool(string? payload, string name)
    {
        if (payload is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
