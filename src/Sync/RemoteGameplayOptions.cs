using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// The OTHER player's Options → Gameplay settings, exchanged in the handshake.
    ///
    /// A family of gameplay options is read as <c>if (IsPlayerObject &amp;&amp; option)</c>,
    /// so each one applies only to your OWN units on YOUR machine. In PvP the other
    /// player's fleet is the local enemy taskforce on the machine that simulates it, so
    /// <c>IsPlayerObject</c> is false for every one of their units and the option is
    /// never consulted - while their own machine, where the option IS set, does not run
    /// that sim. The settings simply evaporate: the owner's choice never reaches their
    /// ships, and the host's does not stand in for it either.
    ///
    /// Playtest 28 caught the air half in the host's log: four of the guest's aircraft
    /// entering ReturnToBase within nine seconds of each other, unordered, right after
    /// spending their air-to-air load. The guest reads that as "planes RTB without my
    /// orders" and "they will not break out of it and will not engage even on weapons
    /// free" - because CheckWinchester does three things at once, and the same verdict
    /// also clears <c>_objectToDestroy</c> and ceases fire.
    ///
    /// EVERY ONE OF THESE OPTIONS IS AN ESCAPE HATCH. Read the game's own code and they
    /// only ever turn a verdict OFF: CheckWinchester's player branches are all
    /// <c>return false</c> ("stay airborne"), and AutoAttackSurface only REMOVES surface
    /// contacts from a target list. That is what makes them safe to apply from a
    /// postfix - the parity pass can decline what the engine decided and can never
    /// invent a decision the engine did not reach.
    ///
    /// Carried in the handshake because that is where the pairing already exchanges
    /// per-machine facts, and it costs one byte each way. KNOWN LIMIT: a player who
    /// changes these options mid-battle does not re-advertise them - the values are
    /// whatever they held at connect. Reconnecting picks them up.
    /// </summary>
    internal static class RemoteGameplayOptions
    {
        /// <summary>False until the handshake has completed. Nothing consults the
        /// values before then, so a parity pass with no data simply does not run and
        /// the engine's own verdict stands.</summary>
        internal static bool Known { get; private set; }

        // Names match the game's own fields so the two can be read side by side.
        internal static bool PlanesWinchester            { get; private set; } = true;
        internal static bool BombersStaysWithAAM         { get; private set; }
        internal static bool FighterBombersStaysWithAAM  { get; private set; }
        internal static bool InterceptorsStaysWithAAM    { get; private set; }
        internal static bool SRFightersStaysWithGuns     { get; private set; }
        internal static bool ASWBombersStaysWithSonobuoys{ get; private set; }
        internal static bool AutoAttackSurface           { get; private set; }

        /// <summary>This machine's own values, packed for the handshake. Read live
        /// rather than cached: the player may have changed them between launching the
        /// game and connecting.</summary>
        internal static byte PackLocal()
        {
            var opts = Singleton<OptionsManager>.InstanceExists(false)
                ? Singleton<OptionsManager>.Instance : null;

            int b = 0;
            if (Globals._playerPlanesWinchester)              b |= 1 << 0;
            if (Globals._playerBombersStaysWithAAM)           b |= 1 << 1;
            if (Globals._playerFighterBombersStaysWithAAM)    b |= 1 << 2;
            if (Globals._playerInterceptorsStaysWithAAM)      b |= 1 << 3;
            if (Globals._playerSRFightersStaysWithGuns)       b |= 1 << 4;
            if (Globals._playerASWBombersStaysWithSonobuoys)  b |= 1 << 5;
            if (opts != null && opts.PlayerAutoAttackSurface) b |= 1 << 6;
            return (byte)b;
        }

        internal static void Apply(byte packed)
        {
            PlanesWinchester             = (packed & (1 << 0)) != 0;
            BombersStaysWithAAM          = (packed & (1 << 1)) != 0;
            FighterBombersStaysWithAAM   = (packed & (1 << 2)) != 0;
            InterceptorsStaysWithAAM     = (packed & (1 << 3)) != 0;
            SRFightersStaysWithGuns      = (packed & (1 << 4)) != 0;
            ASWBombersStaysWithSonobuoys = (packed & (1 << 5)) != 0;
            AutoAttackSurface            = (packed & (1 << 6)) != 0;
            Known = true;

            Plugin.Log.LogInfo($"[Options] Remote player's gameplay options: " +
                $"planesWinchester={PlanesWinchester} bombersAAM={BombersStaysWithAAM} " +
                $"fighterBombersAAM={FighterBombersStaysWithAAM} interceptorsAAM={InterceptorsStaysWithAAM} " +
                $"srFightersGuns={SRFightersStaysWithGuns} aswSonobuoys={ASWBombersStaysWithSonobuoys} " +
                $"autoAttackSurface={AutoAttackSurface}");
        }

        internal static void Reset() => Known = false;
    }
}
