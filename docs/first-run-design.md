# First-run setup: design

Status: proposal. Nothing here is implemented yet.

## The question

Today the app is configured by editing defaults in source and running a script named
after one household's custom integration. This proposes a first-run experience that
asks the user three things and asks Home Assistant for the rest.

It also answers a narrower question that turns out to simplify the whole design:

> Does the app need to know about Ticker at all, or is registering as a device and
> attaching to the authenticating user good enough?

**Registering is good enough.** Home Assistant already does the attaching, and it does
it better than the current script does. The rest of this document follows from that.

---

## 1. What is embedded today

| What | Where | Count |
|---|---|---|
| Home Assistant address `192.168.1.188:8123` | `AppSettings.cs` (2), `MainWindow.xaml.cs` placeholder, `Register-TickerTarget.ps1` default, `bench/` (2), README | 7 |
| `person.copilot` | `Register-TickerTarget.ps1` default | 1 |
| Machine name `dswett_home` | README examples | 1 |
| Token read from `~/.agent-ha-bridge/config.json` | `Register-TickerTarget.ps1`, `Test-ToastAction.ps1` | 2 |
| Ticker in names and prose | script filename, 10 refs in it, 3 in README, 5 comments in `src/` | 19 |
| Microsoft package proxy | `nuget.config` | 2 |

Two of these are worth separating out, because they are not the same kind of problem.

**The Ticker coupling is cosmetic.** Every one of the five hits in `src/` is a comment
or a log string that names the script. The app's actual contract is
`mobile_app_notification_action`, which is core Home Assistant, raised by the phone apps
too. There is no Ticker-specific code to remove — only names.

**The NuGet proxy is environment, not configuration.** It belongs to the machines this
is built on, not to the user's house, and CI already overrides it. It should stay, with
its existing comment explaining why.

Everything else is one user's house leaking into defaults, and is what first-run setup
replaces.

---

## 2. Why registration is sufficient

`mobile_app`'s registration flow does the person wiring itself:

```python
# homeassistant/components/mobile_app/config_flow.py, async_step_registration
devt_entry = entity_registry.async_get_or_create(
    "device_tracker", DOMAIN, user_input[ATTR_DEVICE_ID],
    object_id_base=user_input[ATTR_DEVICE_NAME],
)
await person.async_add_user_device_tracker(
    self.hass, user_input[CONF_USER_ID], devt_entry.entity_id
)
```

and `CONF_USER_ID` is whoever's token made the call:

```python
# homeassistant/components/mobile_app/http_api.py, RegistrationsView.post
user = request["hass_user"]
...
data[CONF_USER_ID] = user.id
```

So a single authenticated `POST /api/mobile_app/registrations` creates the device, the
`device_tracker` entity, the `notify.mobile_app_<name>` service and the person link —
addressed to the account that authenticated, exactly as proposed.

This is strictly better than what `Register-TickerTarget.ps1` does by hand:

| | Script today | Registration |
|---|---|---|
| Creates `device_tracker` | Manually, as a separate step | Automatically |
| Finds the person | Name matching against `person/list` | Not needed — uses the token's user |
| Attaches the tracker | `person/update` | `async_add_user_device_tracker` |
| Needs an admin token | **Yes** — `person/update` is admin-only ([1]) | **No** |
| Can attach to the wrong person | Yes, name matching is fuzzy | No |

The admin requirement is the important row. The manual path needs an administrator
token purely to do something the automatic path does with any account.

[1]: `person/update_own_profile`'s own docstring — "Unlike the admin-only
`person/update`, this only touches the name and picture of the calling user" — and that
command covers name and picture only, never `device_trackers`. There is no non-admin
way to attach a tracker by hand.

### What this means for Ticker

Nothing in the app, and nothing to detect.

Ticker resolves a recipient by walking person → `device_tracker` → device → notify
service. That walk succeeds because of the person link registration already made. A
desktop appears among its targets by existing, not by cooperating.

Without Ticker, the same registration leaves a `notify.mobile_app_<name>` service that
any automation can call directly. Both paths end at the same websocket push channel.

So "use Ticker if present, otherwise fall back" is not a fallback and not a branch — it
is one mechanism seen from two directions. **No capability detection, no Ticker-aware
code, no second code path.**

### The one real precondition

`async_add_user_device_tracker` loops over people looking for one whose `user_id`
matches, and silently does nothing when there is none. A Home Assistant account with no
person linked to it gets a working `notify.mobile_app_<name>` and no person routing.

That is a genuine state worth reporting rather than leaving to be discovered when a
notification does not arrive. It is detectable without admin, because person entities
publish both facts the app needs as ordinary state attributes:

```python
# homeassistant/components/person/__init__.py, _update_extra_state_attributes
data: dict[str, Any] = {
    PersonEntityStateAttribute.DEVICE_TRACKERS: self.device_trackers,
    ...
}
if (user_id := self._config.get(CONF_USER_ID)) is not None:
    data[PersonEntityStateAttribute.USER_ID] = user_id
```

`auth/current_user` returns the signed-in user's id, so over its existing websocket the
app can confirm not merely that a person exists, but that *this device's tracker* is
listed on the person belonging to *this account*. That is the whole chain, verified
from the client, with no admin rights and no guessing by name.

---

## 3. Proposed first run

Four steps. The app already hosts a WebView2 and a websocket client, so none of this
needs new infrastructure.

### Step 1 — Find Home Assistant

Home Assistant advertises itself over zeroconf as `_home-assistant._tcp.local.`
(`components/zeroconf/const.py`), with `location_name`, `version`, `internal_url` and
`external_url` in its TXT record. Offer what is found as a list, with manual entry
always available and always the fallback.

Prefer `internal_url` when present. Resolve nothing silently: show the address that will
be used before using it.

### Step 2 — Sign in

See section 4. The outcome is an access token and a refresh token belonging to a real
Home Assistant account.

### Step 3 — Register this device

```http
POST /api/mobile_app/registrations
Authorization: Bearer <access token>

{
  "app_id": "io.github.danswett.ha_desktop",
  "app_name": "Home Assistant Desktop",
  "app_version": "<assembly version>",
  "device_name": "<computer name, editable>",
  "manufacturer": "<system manufacturer>",
  "model": "<system model>",
  "os_name": "Windows",
  "os_version": "<build>",
  "supports_encryption": false,
  "app_data": { "push_websocket_channel": true }
}
```

Returns `webhook_id`, which is what the push channel needs.

`device_name` is the only field worth letting the user edit: it becomes the entity id
and the notify service name, and it is how they will recognise this machine in Home
Assistant. Default it to `%COMPUTERNAME%`, which is already what people call their
machines.

`supports_encryption: false` is deliberate. The payload travels inside the websocket
the app already holds; adding a second layer of encryption to the same channel buys
nothing and adds a key to manage.

### Step 4 — Confirm, concretely

Show what now exists, named:

- the device tracker: `device_tracker.<slug>`
- the notify service: `notify.mobile_app_<slug>`
- the person it was attached to, **or** a clear note that the account has no linked
  person and what that costs

Offer one test notification. This is the only point where the user can see the whole
chain working, and it costs one button.

---

## 4. Authentication

### What happens today

`GetAccessTokenAsync` reads `hassTokens` out of the dashboard's `localStorage` through
WebView2. It is neat — the app holds no credential of its own — but it is not a basis
for setup:

- there is no token until someone has signed into the dashboard, so registration cannot
  be part of a first run that precedes it
- it breaks when the user signs out of the dashboard
- it depends on an undocumented frontend storage key

### Proposed: OAuth2 in the window we already have

Home Assistant implements IndieAuth. Two facts make this clean for a desktop app:

```python
# homeassistant/components/auth/indieauth.py, verify_redirect_uri
is_valid = (
    client_id_parts.scheme == redirect_parts.scheme
    and client_id_parts.netloc == redirect_parts.netloc
)
if is_valid:
    return True
```

A redirect URI sharing scheme and host with the client id is accepted outright — **no
network fetch of the client id**. And `_parse_client_id` explicitly permits `http` and
loopback hosts.

So:

- `client_id` = `http://localhost/ha-desktop`
- `redirect_uri` = `http://localhost/ha-desktop/callback`

Navigate the hosted WebView2 to `/auth/authorize`, and handle the redirect with
`CoreWebView2.NavigationStarting`: when the target starts with the redirect URI, cancel
the navigation and read `code` from the query string. Exchange it at `/auth/token` for
an access token and a refresh token.

**Nothing ever listens on that port.** The redirect is intercepted before it leaves the
control, so the app keeps its property of opening no socket — which is also why the
websocket push channel was chosen over HTTP push in the first place.

The user sees Home Assistant's own login page, including whatever MFA they have
configured. The app never handles a password.

### Storing it

| Secret | Where |
|---|---|
| Refresh token | DPAPI, `CurrentUser` scope |
| `webhook_id` | DPAPI — it addresses this device and should not sit in a readable file |
| Everything else | `settings.json` as today |

`settings.json` is currently plain and contains `PushWebhookId`. That should move.

---

## 5. What happens to the script

`tools/Register-TickerTarget.ps1` becomes unnecessary for setup: the app does
registration itself, and the person wiring the script exists to perform is done by Home
Assistant.

It should be deleted rather than renamed. Keeping it would leave two ways to register a
machine, one of which needs an admin token and can attach to the wrong person.

`tools/Test-ToastAction.ps1` stays — it tests the one thing that cannot be automated —
but should take `-HaUrl` and `-Token` rather than reading the bridge's config, and
should derive the notify service from the registered device name rather than guessing
from `%COMPUTERNAME%`.

---

## 6. Failure modes

| Situation | Behaviour |
|---|---|
| Account has no linked person | Register anyway; say so in step 4. `notify.mobile_app_<name>` still works |
| Already registered | Detect a stored `webhook_id` and skip setup. Offer "register again", which deletes the old device first rather than leaving a duplicate |
| Registration succeeds, app can't reach HA later | Existing retry with backoff; unchanged |
| Refresh token revoked | `auth_invalid` on the websocket. Re-run step 2 only — keep the registration |
| Zeroconf finds nothing | Manual entry, which is always on screen anyway |
| HA reachable, wrong credentials | Home Assistant's own login UI handles it; the app shows nothing of its own |
| User cancels setup | App opens with no dashboard and an obvious way back into setup. It must not exit |

The duplicate-device case deserves care: registering twice silently creates a second
device, a second tracker and a second notify service, and the first keeps receiving.

---

## 7. Migrating this machine

The existing install already has `HomeUrl` and `PushWebhookId`, and a device registered
under `dswett_home` that Home Assistant already linked to a person.

Treat a present `PushWebhookId` as "setup complete" and skip first run. The only gap is
the refresh token, which does not exist yet; until the user signs in again, token
borrowing remains as a fallback for exactly this case. That is the one place where
keeping the old path has a purpose, and it can be removed a version later.

---

## 8. What this does not change

The app's behaviour once configured is untouched: occlusion and lock/sleep suspension,
the push channel, toasts, action buttons, the badges, single instance, tray behaviour.
Those are verified by hand against a real desktop and a real Home Assistant, and this
proposal does not move any of them.

---

## 9. Decisions still open

1. **Does setup run in the main window or its own?** A separate window is cleaner but
   duplicates the title bar and tray work the main window already does.
2. **Should the dashboard address and the API address ever differ?** They are the same
   today. Supporting a split would complicate every screen for a case that may not
   exist.
3. **How much should the app say about Ticker?** Nothing is required. A sentence in the
   README explaining why it needs no integration-specific support may still be worth
   keeping, since the absence of that code is itself surprising.
