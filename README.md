# Stonewards Session Visibility

**Change your Steam lobby between Friends and Public while playing Stonewards, without recreating the session.** The host can switch visibility directly from the game's Session window.

## ✨ Features

- **In-session switching** — change lobby visibility between **Friends** and **Public** during an active game.
- **Integrated controls** — use the **VISIBILITY** button in the Session window, directly beneath the session name.
- **Game-style interface** — a pixel-art button with stepped corners, an expanded header, and room for all four player slots.
- **Host-only access** — only the current Steam lobby owner can change visibility.

## 🎮 How to use

1. Host a Stonewards session.
2. Open the pause menu and select **Session**.
3. Click **VISIBILITY: FRIENDS** or **VISIBILITY: PUBLIC** in the session header to switch modes. The button displays the current mode.

The switch is available **only through the Session menu**; there is no keyboard shortcut.

## 📦 Installation

Once available on Thunderstore, install **SessionVisibility** with **r2modman** or another compatible Thunderstore mod manager, then launch Stonewards through your modded profile. **BepInExPack** is listed as a package dependency.

To build and install directly from this repository, see **Development** below.

## 🔧 Compatibility

- **Game:** Stonewards **0.2.3** (tested).
- **Mod loader:** BepInEx 5.
- **Multiplayer:** Host-only control. The button is shown only to the host; visibility changes act on the existing Steam lobby.

## ⚠️ Known limitations

- The selectable modes are **Friends** and **Public**, matching Stonewards' session creation options. The game's internal Private/locked state is not a separate option.
- Changing visibility **does not override** Stonewards' lobby join lock. If the game currently blocks new players, switching to Public will not automatically allow them to join.

## 🛠️ Development

### Requirements

- .NET SDK capable of building `netstandard2.1`
- A local Stonewards installation (for the game's managed assemblies)
- BepInEx 5 in the chosen r2modman profile

### Building

Clone the repository and run the included PowerShell build script. The r2modman profile name should match the repository name: `stonewards-session-visibility`.

```powershell
$gameDir = Read-Host "Stonewards installation folder"
$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\Stonewards\profiles\stonewards-session-visibility"

powershell -ExecutionPolicy Bypass -File .\build-and-install.ps1 `
  -GameDir $gameDir `
  -ProfileDir $profileDir
```

The script builds `StonewardsSessionVisibility.dll` and installs it into the selected profile's `BepInEx/plugins` folder.

To create the **v0.1.0 Thunderstore upload ZIP** from the release metadata, icon, and compiled DLL:

```powershell
powershell -ExecutionPolicy Bypass -File .\package-thunderstore.ps1 `
  -GameDir $gameDir `
  -ProfileDir $profileDir
```

The package script validates the manifest version, checks the 256×256 icon, and places the upload archive in `dist/`. It does **not** upload or publish the release.

### Implementation

The BepInEx plugin integrates with Stonewards' `UISessionSettings` via Harmony. It verifies that the local Steam user owns the active lobby, calls `SteamMatchmaking.SetLobbyType`, and updates the game's hosted visibility state. The game's separate join-lock state is left unchanged.

## 🔗 Links

[Report an issue](https://github.com/thereisnouser/stonewards-session-visibility/issues) · [Changelog](CHANGELOG.md)

---

*Made by thereisnouser*
