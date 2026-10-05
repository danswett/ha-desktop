# Registers this machine with Home Assistant as a mobile_app device, so Ticker can
# deliver to it like any phone.
#
# Ticker resolves recipients by tracing person -> device_tracker -> device -> notify
# service (custom_components/ticker/discovery.py). A plain notify service is therefore
# not enough; the device needs a device_tracker on it and that tracker needs to be on
# a person. This script creates all three.
#
# Registration asks for `push_websocket_channel`, not `push_url`. Home Assistant's
# mobile_app supports either (see homeassistant/components/mobile_app/util.py,
# supports_push), and the websocket channel means notifications arrive over the
# connection the app already holds - no listening port on the desktop, no inbound
# firewall rule, and nothing to break when DHCP moves the machine.
#
#   pwsh -File tools\Register-TickerTarget.ps1
#   pwsh -File tools\Register-TickerTarget.ps1 -WhatIf      (show, change nothing)
#   pwsh -File tools\Register-TickerTarget.ps1 -Unregister  (undo)

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$HaUrl = 'http://192.168.1.188:8123',
    [string]$PersonEntity = 'person.copilot',
    [string]$DeviceName = $env:COMPUTERNAME,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

function Get-HaToken {
    # Read-only reuse of the token the bridge already holds. Never printed.
    $cfg = Join-Path $env:USERPROFILE '.agent-ha-bridge\config.json'
    if (-not (Test-Path $cfg)) { throw "No Home Assistant token available at $cfg" }
    (Get-Content $cfg -Raw | ConvertFrom-Json).homeAssistant.token
}

$token = Get-HaToken
$headers = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }

function Invoke-HaWebSocket {
    <#
      Runs a sequence of websocket commands against Home Assistant and returns the
      results. The REST API cannot read or write person entities; person/list and
      person/update exist only on the websocket API.
    #>
    param([Parameter(Mandatory)][object[]]$Commands)

    Add-Type -AssemblyName System.Net.WebSockets.Client -ErrorAction SilentlyContinue
    $wsUri = [Uri](($HaUrl -replace '^http', 'ws') + '/api/websocket')
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))

    try {
        $ws.ConnectAsync($wsUri, $cts.Token).GetAwaiter().GetResult()

        $buffer = [byte[]]::new(131072)
        function Receive-Json {
            $sb = [System.Text.StringBuilder]::new()
            do {
                $seg = [ArraySegment[byte]]::new($buffer)
                $r = $ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
                [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
            } while (-not $r.EndOfMessage)
            $sb.ToString() | ConvertFrom-Json
        }
        function Send-Json {
            param($Obj)
            $json = $Obj | ConvertTo-Json -Depth 10 -Compress
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
            $ws.SendAsync([ArraySegment[byte]]::new($bytes),
                [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult()
        }

        $hello = Receive-Json
        if ($hello.type -ne 'auth_required') { throw "Unexpected greeting: $($hello.type)" }
        Send-Json @{ type = 'auth'; access_token = $token }
        $authResult = Receive-Json
        if ($authResult.type -ne 'auth_ok') { throw "Home Assistant rejected the token: $($authResult.type)" }

        $results = @()
        $id = 1
        foreach ($cmd in $Commands) {
            $id++
            $payload = @{ id = $id } + $cmd
            Send-Json $payload
            do { $msg = Receive-Json } while ($msg.id -ne $id)
            if (-not $msg.success) { throw "Command '$($cmd.type)' failed: $($msg.error.message)" }
            $results += , $msg.result
        }
        return $results
    }
    finally {
        if ($ws.State -eq 'Open') {
            $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $ws.Dispose(); $cts.Dispose()
    }
}

# ---------------------------------------------------------------------------
if ($Unregister) {
    Write-Host 'Removing the desktop registration...' -ForegroundColor Cyan
    $entries = Invoke-RestMethod -Uri "$HaUrl/api/config/config_entries/entry" -Headers $headers -TimeoutSec 30
    $mine = $entries | Where-Object { $_.domain -eq 'mobile_app' -and $_.title -eq $DeviceName }
    foreach ($e in $mine) {
        if ($PSCmdlet.ShouldProcess($e.title, 'delete mobile_app config entry')) {
            Invoke-RestMethod -Uri "$HaUrl/api/config/config_entries/entry/$($e.entry_id)" -Headers $headers -Method Delete -TimeoutSec 30 | Out-Null
            Write-Host "  removed config entry $($e.entry_id) ($($e.title))" -ForegroundColor Green
        }
    }
    if (-not $mine) { Write-Host '  nothing registered under that device name.' }

    if (Test-Path $settingsPath) {
        $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $s.PSObject.Properties.Remove('PushWebhookId')
        $s | ConvertTo-Json | Set-Content $settingsPath
        Write-Host '  cleared PushWebhookId from the app settings.' -ForegroundColor Green
    }
    return
}

Write-Host "Registering '$DeviceName' with Home Assistant..." -ForegroundColor Cyan

$existing = $null
if (Test-Path $settingsPath) {
    $existing = (Get-Content $settingsPath -Raw | ConvertFrom-Json).PushWebhookId
}
if ($existing) {
    Write-Host "  already registered (webhook id ends ...$($existing.Substring($existing.Length - 6)))" -ForegroundColor Yellow
    Write-Host '  re-run with -Unregister first to start over.'
    return
}

$body = @{
    device_id          = [guid]::NewGuid().ToString()
    app_id             = 'home_assistant_desktop'
    app_name           = 'Home Assistant Desktop'
    app_version        = '1.0.0'
    device_name        = $DeviceName
    manufacturer       = (Get-CimInstance Win32_ComputerSystem).Manufacturer
    model              = (Get-CimInstance Win32_ComputerSystem).Model
    os_name            = 'Windows'
    os_version         = [string][System.Environment]::OSVersion.Version
    supports_encryption = $false
    app_data           = @{ push_websocket_channel = $true }
} | ConvertTo-Json -Depth 5

if (-not $PSCmdlet.ShouldProcess($DeviceName, 'register mobile_app device')) { return }

$reg = Invoke-RestMethod -Uri "$HaUrl/api/mobile_app/registrations" -Headers $headers -Method Post -Body $body -TimeoutSec 30
$webhookId = $reg.webhook_id
Write-Host "  registered. webhook id ends ...$($webhookId.Substring($webhookId.Length - 6))" -ForegroundColor Green

# A notify service alone is not discoverable by Ticker; it needs a device_tracker on
# the same device. update_location with a location_name creates one without inventing
# GPS coordinates for a machine that does not move.
Invoke-RestMethod -Uri "$HaUrl/api/webhook/$webhookId" -Method Post -TimeoutSec 30 `
    -Body (@{ type = 'update_location'; data = @{ location_name = 'home' } } | ConvertTo-Json -Depth 5) `
    -ContentType 'application/json' | Out-Null
Start-Sleep -Seconds 3

$slug = ($DeviceName -replace '[^a-zA-Z0-9]', '_').ToLower()
$tracker = "device_tracker.$slug"
$notify = "notify.mobile_app_$slug"
Write-Host "  expecting $tracker and $notify" -ForegroundColor Green

$state = try { Invoke-RestMethod -Uri "$HaUrl/api/states/$tracker" -Headers $headers -TimeoutSec 20 } catch { $null }
if (-not $state) {
    Write-Host "  WARNING: $tracker did not appear; Ticker will not discover this device." -ForegroundColor Red
}

if (Test-Path $settingsPath) {
    $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $s | Add-Member -NotePropertyName PushWebhookId -NotePropertyValue $webhookId -Force
    $s | ConvertTo-Json | Set-Content $settingsPath
    Write-Host '  stored the webhook id in the app settings.' -ForegroundColor Green
}

# Attaching the tracker to a person is what lets Ticker resolve that person to this
# machine. It is deliberately the last step, and deliberately non-fatal: the webhook id
# above is what the app needs to connect, and losing it because a person lookup failed
# would leave the app unable to receive anything at all.
$people = (Invoke-HaWebSocket -Commands @(@{ type = 'person/list' }))[0]
$wanted = $PersonEntity -replace '^person\.', ''
$person = @($people.storage) | Where-Object {
    $name = $_.name
    ($name -and "person.$($name.ToLower() -replace '[^a-z0-9]','_')" -eq $PersonEntity) -or $_.id -eq $PersonEntity
}
if (-not $person) {
    $person = @($people.storage) | Where-Object { $_.name -and $_.name -match [regex]::Escape($wanted) }
}
if (-not $person) {
    Write-Host "  WARNING: could not find $PersonEntity in the person registry; attach $tracker manually." -ForegroundColor Red
    return
}

$person = $person[0]
$trackers = @($person.device_trackers) + $tracker | Select-Object -Unique
if ($PSCmdlet.ShouldProcess($PersonEntity, "attach $tracker")) {
    Invoke-HaWebSocket -Commands @(@{
        type            = 'person/update'
        person_id       = $person.id
        name            = $person.name
        device_trackers = @($trackers)
    }) | Out-Null
    Write-Host "  attached $tracker to $($person.name)" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done. Ticker should now list this machine among the delivery targets.' -ForegroundColor Cyan
