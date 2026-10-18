# Stonewards Session Visibility (v0.1.0 test build)

Host-only BepInEx 5 plugin for **switching Public / Private / Friends Only visibility of the same existing Steam lobby**, including during an active Stonewards match. No session restart and no client installation are intended to be needed.

## Usage

1. Host a Steam lobby using a r2modman profile that has BepInEx 5 enabled.
2. Open **Esc → Session**. The mod adds one native-styled **VISIBILITY: PUBLIC / PRIVATE** button inside the expanded native Session title header, directly beneath the session name (above the player list). It shows the current mode; click to toggle. In a Friends Only session, the first click switches to Public.
3. Or press **F7** to toggle directly between Public and Private. Rebind or disable the key in `BepInEx/config/dev.stonewards.sessionvisibility.cfg` after first launch.
4. Visibility does **not** change the game's existing `Joinable` flag. If the game has locked the lobby, switching to Public does not override the lock and new joins may remain blocked.

## Build & install (PowerShell)

Extract this archive into `X:\projects\stonewards-session-visibility` (or another folder) and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-and-install.ps1 `
  -GameDir "F:\Steam\steamapps\common\Stonewards" `
  -ProfileDir "C:\Users\user\AppData\Roaming\r2modmanPlus-local\Stonewards\profiles\stonewards-session-visibility"
```

The r2modman profile name should match the repository name (`stonewards-session-visibility`). The script builds the DLL against **your installed game's managed assemblies** and copies it to the profile's `BepInEx/plugins` folder.

## How it works

- Checks for an active host via Mirror and verifies the local Steam user owns the active lobby.
- Calls `Steamworks.SteamMatchmaking.SetLobbyType` on the current lobby (without creating a new lobby).
- On success, updates `StoneWardsNetworkManager.HostedLobbyVisibility` via the game's `SetHostedLobbyVisibility` method.
- Intentionally **does not use** `SteamLobby.UnlockLobby`, which is restricted by the game's network state in the inspected build.
- Leaves all connected players, the game scene, and the `Joinable` flag alone.

## Known limits / verification

- **Source code not yet rebuilt or playtested after the UI change.** Compile and send build output or logs if the Session button does not appear or overlaps at your resolution.
- The Steam API returns success/failure for a type change; Stonewards provides no public getter for current Steam lobby type. The UI reads the game's stored visibility setting, updated only after Steam accepts the change.
- Verify visibility from a *second Steam account* in the public lobby browser and test that existing members remain connected.
- Steam Private visibility does not inherently imply "all connections blocked"; game's separate join lock is preserved.

## Testing checklist

- Create Public lobby; switch to Private mid-run; check it disappears from public discovery.
- Invite a friend and test joining private (only when game `Joinable` permits).
- Switch back to Public; check discovery and successful join.
- Switch between hub and gameplay; verify UI mode, connections and state.
- As non-host, verify no change of lobby visibility is possible.
