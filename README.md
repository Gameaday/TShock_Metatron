# Project Metatron: The Celestial Scribe

Project Metatron is an enterprise-grade community management and security engine for TShock (Terraria) servers. Designed with forensic-grade security and a frictionless user experience, Metatron provides strict Discord-Gated Access and Automatic Account Provisioning within a single, highly optimized plugin.

By natively embedding its dependencies and utilizing a Service-Oriented Architecture, Metatron bridges the gap between your Discord community and your game server without bloated file structures or compiler conflicts.

## The Two Pillars of Metatron

### 1. The Gate (Cerberus Protocol)
Metatron acts as a strict but fair bouncer. It ensures that only members of your Discord community (and specifically those with the roles you choose) can set foot in your world.

* **Discord Integration:** Users type `!link` in a designated Discord channel to receive a cryptographically secure, temporary PIN via Direct Message.
* **Ironclad Quarantine:** Unverified users are placed in "Limbo." At the network layer, Metatron intercepts and rejects all movement, combat, and inventory packets from unverified clients. They are webbed, muted, and completely neutralized until they verify.
* **Role Enforcement:** Validates Discord server membership and specific role IDs before granting access.

### 2. The Scribe (Frictionless Auto-Reg)
Once a user is verified via Discord, Metatron handles the "paperwork" silently in the background.

* **Invisible Registration:** Accounts are generated automatically using BCrypt hashed temporary passwords. Users never have to type `/register`.
* **Forensic Auto-Login:** Metatron binds the Discord ID to the TShock Account and the client's UUID. If a user joins from their known device, they bypass the password screen entirely. No passwords, no prompts, no friction.
* **Secure Fallback:** If a user loses their UUID or connects from a new machine, Metatron gracefully falls back to TShock's native password prompt to prevent unauthorized access, utilizing an internal strike system to prevent brute-forcing.

## Installation

**Requirements:** TShock 6.1.0+ and .NET 9.0.

1.  Download the latest `Metatron.dll`. (Dependencies like Discord.Net and BCrypt are natively embedded inside this single file).
2.  Place `Metatron.dll` into your `ServerPlugins` folder.
3.  Start the server. Metatron will generate its configuration in `tshock/Metatron/Core.json`.
4.  Open `Core.json` and input your Discord Bot Token, Guild ID, and target Channel ID.
5.  Type `/meta reload` in the server console or in-game.

## Configuration (Core.json)

Metatron is designed to be lean. You can customize its exact behavior via the `Core.json` file.

| Setting | Description |
| :--- | :--- |
| `EnableDiscordGate` | Activates the Discord role-verification, Limbo quarantine, and `/verify` system. |
| `EnableFrictionlessAuth` | Activates invisible account registration and UUID-
