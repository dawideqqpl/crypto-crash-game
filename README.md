# Crash Crypto Game

A multiplayer **Crash-style betting game** built in Unity, integrated with the **XRP Ledger** for real cryptocurrency transactions and **PlayFab** for player data management.

Players place bets before each round, watch a multiplier grow in real time, and must cash out before the graph crashes — or lose their bet.

## Demo

https://github.com/user-attachments/assets/443b5d80-d50d-427e-9273-998c1ad392a9

---

## Features

- **Real-time multiplier graph** — smooth client-side interpolation between server sync ticks
- **XRP Ledger integration** — deposits and withdrawals via the [Xaman](https://xaman.app/) wallet app (QR code login flow)
- **JWT session management** — secure session tokens linked to PlayFab player accounts
- **Auto cash-out** — set a target multiplier and the game cashes out automatically
- **Provably fair** — server seed hash displayed before each round for result verification
- **Game history** — last rounds displayed with color-coded crash points
- **PlayFab backend** — player balances stored as virtual currency (NAMI)

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Game engine | Unity (C#) |
| Player data / auth | [PlayFab](https://playfab.com/) |
| Crypto wallet | XRP Ledger + [Xaman app](https://xaman.app/) |
| Backend API | Node.js (hosted on Render) |
| QR code generation | ZXing.Net |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                    Unity Client                     │
│                                                     │
│  XrpWalletConnector  ──►  Backend API  ──►  Xaman  │
│         │                     │                     │
│         ▼                     ▼                     │
│  CrashGameManager  ◄──►  PlayFab SDK               │
│         │                                           │
│         ▼                                           │
│  CrashAnimator (graph + interpolation)             │
└─────────────────────────────────────────────────────┘
```

**Login flow:**
1. Player scans a QR code with the Xaman app
2. Backend receives the signed payload and issues a JWT session token
3. Session token is linked to a PlayFab account via the player's XRP wallet address
4. Balance is read from PlayFab virtual currency; deposits/withdrawals go through XRP Ledger

---

## Project Structure

```
Assets/
├── Scripts/
│   ├── CrashGameManager.cs   # Core game logic, betting, server sync, UI
│   ├── CrashAnimator.cs      # Real-time graph animation and line renderer
│   ├── XrpWalletConnector.cs # Xaman login, JWT sessions, deposit flow
│   └── GameConfig.cs         # Runtime config loader (reads config.json)
└── Resources/
    └── config.example.json   # Configuration template
```

---

## Getting Started

### Prerequisites

- Unity 2021.3 LTS or newer
- [PlayFab Unity SDK](https://github.com/PlayFab/UnitySDK)
- [ZXing.Net](https://github.com/micjahn/ZXing.Net) (for QR code generation)
- A running instance of the backend API
- A [PlayFab](https://developer.playfab.com/) title

### Configuration

1. Copy the example config file and fill in your values:

```bash
cp Assets/Resources/config.example.json Assets/Resources/config.json
```

2. Edit `config.json`:

```json
{
  "playfabTitleId": "YOUR_PLAYFAB_TITLE_ID",
  "backendUrl": "https://your-backend-url.example.com"
}
```

> `config.json` is excluded from version control via `.gitignore` — never commit it.

### Running the Game

1. Open the project in Unity
2. Ensure `config.json` is in `Assets/Resources/`
3. Press **Play** in the Unity Editor

---

## Key Scripts

### `CrashGameManager.cs`
Central controller that manages the full game loop:
- Polls `/api/game-state` every 250ms and interpolates the multiplier client-side between ticks
- Handles bet placement, cash-out, and auto cash-out logic
- Communicates with PlayFab for balance updates and crash history

### `XrpWalletConnector.cs`
Manages the Xaman wallet authentication flow:
- Requests a login payload from the backend and renders it as a QR code
- Polls `/api/login-status/{uuid}` until the player signs the payload
- Stores the JWT session token in `PlayerPrefs` for auto-login on next session

### `CrashAnimator.cs`
Drives the visual graph:
- Maps `(timeElapsed, multiplier)` to screen coordinates within a configurable graph area
- Accumulates trail points for the `LineRenderer` and moves the wave indicator
- Changes line color to red on crash

### `GameConfig.cs`
Loads `Assets/Resources/config.json` at runtime using `Resources.Load`. Provides a singleton `GameConfig.Instance` accessible from any script.

---

## License

This project is for portfolio and educational purposes.
