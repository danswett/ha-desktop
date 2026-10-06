<#
.SYNOPSIS
    Sends a test toast to this machine and waits for its action button to come back.

.DESCRIPTION
    Verifies the half of the notification path that nothing else covers: pressing a
    button on a Windows toast should raise mobile_app_notification_action in Home
    Assistant, which is what automations listen for.

    This cannot be automated. Windows renders toasts in an isolated accessibility tree
    that an ordinary process is not allowed to traverse or click, so the press has to be
    a real one. The script sends the toast, then watches Home Assistant's event bus and
    reports what arrives.

    It is addressed to this machine alone. Never test with ticker.notify - that fans out
    to every phone in the house.

.PARAMETER HaUrl
    Home Assistant's address. Defaults to the one the app is configured with.

.PARAMETER Token
    A long-lived access token. Defaults to the agent-ha-bridge token if present.

.PARAMETER TimeoutSeconds
    How long to wait for the button press.

.EXAMPLE
    pwsh -File tools\Test-ToastAction.ps1
#>
[CmdletBinding()]
param(
    [string]$HaUrl,
    [string]$Token,
    [string]$NotifyService,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

if (-not $HaUrl) {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'
    if (Test-Path $settingsPath) {
        $HaUrl = (Get-Content $settingsPath -Raw | ConvertFrom-Json).HomeUrl
    }
}
if (-not $HaUrl) { throw 'Could not work out the Home Assistant address. Pass -HaUrl.' }

if (-not $Token) {
    $bridgeConfig = Join-Path $env:USERPROFILE '.agent-ha-bridge\config.json'
    if (Test-Path $bridgeConfig) {
        $Token = (Get-Content $bridgeConfig -Raw | ConvertFrom-Json).homeAssistant.token
    }
}
if (-not $Token) { throw 'Could not find an access token. Pass -Token.' }

if (-not $NotifyService) {
    $NotifyService = 'mobile_app_' + ($env:COMPUTERNAME -replace '[^a-zA-Z0-9]', '_').ToLower()
}

$HaUrl = $HaUrl.TrimEnd('/')
$wsUrl = ($HaUrl -replace '^http', 'ws') + '/api/websocket'
$action = 'TOAST_TEST_' + (Get-Random -Maximum 999999)

Write-Host "Home Assistant : $HaUrl"
Write-Host "notify service : notify.$NotifyService"
Write-Host "action id      : $action"
Write-Host ''

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds + 30))
$buffer = [byte[]]::new(262144)

function Receive-Json {
    $builder = [System.Text.StringBuilder]::new()
    do {
        $segment = [ArraySegment[byte]]::new($buffer)
        $result = $socket.ReceiveAsync($segment, $cts.Token).GetAwaiter().GetResult()
        [void]$builder.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
    } while (-not $result.EndOfMessage)
    $builder.ToString() | ConvertFrom-Json
}

function Send-Json($payload) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 10 -Compress))
    $socket.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, $cts.Token).GetAwaiter().GetResult() | Out-Null
}

try {
    $socket.ConnectAsync([Uri]$wsUrl, $cts.Token).GetAwaiter().GetResult() | Out-Null
    Receive-Json | Out-Null
    Send-Json @{ type = 'auth'; access_token = $Token }
    if ((Receive-Json).type -ne 'auth_ok') { throw 'Home Assistant rejected the token.' }

    Send-Json @{ id = 1; type = 'subscribe_events'; event_type = 'mobile_app_notification_action' }
    Receive-Json | Out-Null

    $body = @{
        message = 'Press Acknowledge to confirm action buttons work'
        title   = 'Toast action test'
        data    = @{
            tag     = 'toast-action-test'
            actions = @(@{ action = $action; title = 'Acknowledge' })
        }
    } | ConvertTo-Json -Depth 6

    Invoke-RestMethod -Uri "$HaUrl/api/services/notify/$NotifyService" `
        -Headers @{ Authorization = "Bearer $Token" } -Method Post `
        -Body $body -ContentType 'application/json' -TimeoutSec 30 | Out-Null

    Write-Host 'Toast sent. Press "Acknowledge" on it now.' -ForegroundColor Cyan
    Write-Host 'If the banner has already faded, press Win+N - the buttons are still there.'
    Write-Host "Waiting up to $TimeoutSeconds seconds..."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $message = Receive-Json
        if ($message.type -eq 'event' -and $message.event.event_type -eq 'mobile_app_notification_action') {
            $data = $message.event.data
            if ($data.action -eq $action) {
                Write-Host ''
                Write-Host 'PASS - the button press reached Home Assistant.' -ForegroundColor Green
                $data | ConvertTo-Json -Depth 6
                return
            }
            Write-Host "  (ignoring an action from elsewhere: $($data.action))" -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    Write-Host "FAIL - nothing arrived within $TimeoutSeconds seconds." -ForegroundColor Red
    Write-Host 'Check %LOCALAPPDATA%\HomeAssistantDesktop\app.log to see whether the toast was shown.'
}
finally {
    $socket.Dispose()
    $cts.Dispose()
}
