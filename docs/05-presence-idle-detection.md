# 05 — Mandatory Presence & Idle Detection

This is a hard product requirement.

## Capability gate

At login / first-run for presence-monitored roles:

1. Frontend checks `window.IdleDetector`.
2. If absent: mark session incompatible and block working app.
3. User clicks a clear “Enable Required Idle Detection” action.
4. Frontend calls `IdleDetector.requestPermission()` from that user gesture.
5. If not `granted`: block working app and show remediation.
6. Start detector using approved threshold configuration (web API minimum is 60 seconds).
7. Send capability verification to backend.
8. Backend marks session `Verified`.
9. Working app becomes available.

## Server capability state

Per `user_sessions`:

- Unknown
- Unsupported
- PermissionDenied
- Starting
- Verified
- Stale
- Revoked/Error

Only `Verified` permits entering active monitored work state.

## Capability handshake

`POST /api/v1/auth/idle-capability/verify`

Example:

```json
{
  "supported": true,
  "permission": "granted",
  "detectorStarted": true,
  "thresholdSeconds": 60,
  "clientObservedAt": "..."
}
```

Server:
- ignores client user ID
- uses authenticated session
- records server receive time
- returns capability lease expiry and heartbeat cadence

## Heartbeat

The frontend sends:
- app visibility
- detector userState
- detector screenState
- last client transition time
- app version
- session/device ID

Server derives authoritative presence classification.

Do not accept client-supplied “I worked 3 hours” intervals.

## Presence evaluator

Inputs:
- assigned shift
- approved schedule exception
- time off
- active break
- technical grace
- DND/manual status
- IdleDetector user state
- IdleDetector screen state
- websocket/heartbeat connectivity
- app visibility as supplemental
- device offline state

Outputs:
- visible current presence
- normalized presence segment
- PRS flags when rules are violated

## Reconnect behavior

After connection loss:
- record last known server heartbeat
- mark connection degraded/offline according to thresholds
- on reconnect request a fresh IdleDetector state
- do not blindly backfill “active” time from client

## Browser/device compatibility

Because Idle Detection is mandatory:
- compatibility is a workforce device requirement
- frontend must maintain a tested browser matrix
- incompatible browsers/devices may still be able to reach a remediation/login shell but not the monitored working app

Do not silently switch to webpage-only event tracking.

## Privacy minimization

Store state transitions and work-relevant intervals, not raw input events.

Never store:
- keystrokes
- typed content from unrelated apps
- screenshots for presence
- detailed OS activity beyond the browser API’s coarse active/idle + locked/unlocked states

## Tests

Automate:
- supported/granted
- supported/denied
- unsupported
- permission revoked
- stale capability lease
- websocket disconnect
- screen locked
- user idle
- DND + idle
- break + idle
- technical grace
- approved time off
- shift grace boundaries across DST
