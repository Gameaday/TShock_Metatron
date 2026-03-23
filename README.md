🌌 Project Metatron: The Celestial Scribe

Project Metatron is an enterprise-grade, all-in-one community management engine for TShock (Terraria) servers. It replaces the "three-headed beast" of fragmented plugins with a singular, high-performance "Celestial Scribe" that bridges the gap between your Discord community and your game server.

Designed with forensic-grade security and a "frictionless" user experience in mind, Metatron handles Discord-Gated Access, Automatic Account Provisioning, and Smart Broadcasting within a single, asynchronous .dll.
💎 The Three Pillars of Metatron
1. The Gate (Cerberus Protocol)

Metatron acts as a strict but fair bouncer. It ensures that only members of your Discord community (and specifically those with the roles you choose) can set foot in your world.

    Slash Command Integration: Users use /link in Discord to receive a cryptographically secure, temporary PIN.

    Quarantine Protocol: Unverified users are placed in "Limbo"—they are webbed (frozen), muted, and unable to interact with the world until they type /verify <pin>.

    Instant Smite: If a user leaves your Discord or loses their required role, the Scribe instantly revokes their access and kicks them from the game server in real-time.

2. The Scribe (Frictionless Auto-Reg)

Once a user is verified via Discord, Metatron handles the "paperwork."

    Invisible Registration: Accounts are generated automatically. Users never have to type /register.

    Forensic Auto-Login: Metatron uses IP-Pinning and UUID validation. If a user joins from their known device and IP, they are logged in instantly. No passwords, no prompts, no friction.

    Secure Fallback: If a user’s IP changes (e.g., using a VPN), Metatron gracefully falls back to TShock’s native password prompt to prevent unauthorized access.

3. The Voice (Universal Broadcaster)

A smarter way to communicate with your players and your Discord channel.

    Environmental Triggers: Broadcast messages based on Dawn/Dusk transitions, Blood Moons, or specific Boss kills.

    Discord Webhooks: Mirror in-game events to Discord with "Rich Embeds," dynamic bot names, and role pings (e.g., !lfg pings the @Terraria role).

    Streamer Aware: Respects your schedule. Use AllowedDays to ensure stream promotions only fire when you are actually live (e.g., automatically silencing promos on your off-days).

🚀 Installation

    Requirements: TShock 6.1.0+ and .NET 9.0.

    Download: Place Metatron.dll and the required dependencies (Discord.Net, Microsoft.Data.Sqlite) into your ServerPlugins folder.

    Initial Run: Start the server. Metatron will generate a default Metatron.json in your tshock/ folder.

    Configuration: * Open Metatron.json.

        Input your Discord Bot Token and Global Webhook URL.

        Flip EnableDiscordGate to true.

    Reload: Type /meta reload in-game or in the console.

⚙️ Configuration (Master Toggles)

Metatron is designed to be lean. You can disable entire modules to save resources:
Toggle	Description
EnableDiscordGate	Activates the Discord role-verification and /verify system.
EnableFrictionlessAuth	Activates auto-registration and UUID/IP-based auto-login.
EnableBroadcaster	Activates the smart message engine and Discord webhooks.
UpdateDiscordStatus	The bot will show "Playing with X/Max players" in Discord.
🛠️ Commands
For Users

    Discord: /link — Generates a unique 6-digit PIN to bind your account.

    In-Game: /verify <pin> — Consumes the PIN to forge your "Celestial Seal."

For Admins

    /meta reload — Hot-reloads the configuration and restarts the Scribe.

    /user password <name> <pass> — (Native TShock) Used for manual account recovery if a user loses access to their Discord.

🛡️ Security & Performance

    SQLite WAL Mode: Uses Write-Ahead Logging for the Metatron.sqlite archive, allowing simultaneous reads and writes without thread-locking the game.

    Asynchronous Scribe: The Discord bot runs on an isolated background thread. Discord API lag will never cause your Terraria server to skip a frame.

    Anti-Squatting: The VerificationTimeoutMinutes setting ensures that unverified "Limbo" players are kicked after a few minutes to free up slots for active community members.

    Memory Pruning: Temporary PINs are stored in volatile memory with a self-cleaning expiration timer to prevent memory bloat.

Project Metatron is maintained by HistoryLabs. Sit at the threshold. Guard the Ledger. Protect the Realm.