private int _tickCounter = 0;

    private void OnGatekeeperPulse(EventArgs args)
    {
        if (++_tickCounter < 60) return;
        _tickCounter = 0;

        if (_limboPlayers.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var kvp in _limboPlayers)
        {
            if (kvp.Value == DateTime.MinValue) continue; 

            if ((now - kvp.Value).TotalMinutes >= _config.VerificationTimeoutMinutes)
            {
                var player = TShock.Players[kvp.Key];
                if (player != null && player.Active)
                {
                    player.Disconnect($"Verification timed out. You have {_config.VerificationTimeoutMinutes} minutes to link via Discord.");
                }
                _limboPlayers.TryRemove(kvp.Key, out _);
            }
        }
    }
