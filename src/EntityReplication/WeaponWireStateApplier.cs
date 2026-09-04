using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Guest-side applier for WeaponWireStateMessage. Updates the replica torpedo's
    /// guidance fields so the tactical map target line / aim indicator follows the
    /// host's authoritative torpedo.
    ///
    /// The guest's wire command forward patches return false so the local weapon
    /// fields never update locally; only the host's torpedo changes. This applier
    /// receives the host's broadcast and sets the display fields.
    ///
    /// Wrapped in OrderHandler.ApplyingFromNetwork to prevent the reactive property
    /// setters from re-forwarding the change back to the host.
    /// </summary>
    public static class WeaponWireStateApplier
    {
        public static void Apply(WeaponWireStateMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return; // host doesn't apply its own broadcasts

            var wb = StateSerializer.FindById(msg.WeaponId) as WeaponBase;
            if (wb == null)
            {
                Plugin.Log.LogWarning($"[WireState] Weapon {msg.WeaponId} not found - dropping (spawn may not have arrived yet)");
                return;
            }

            if (wb._taskforce == null)
            {
                Plugin.Log.LogWarning($"[WireState] Weapon {msg.WeaponId} has no taskforce - dropping");
                return;
            }

            OrderHandler.ApplyingFromNetwork = true;
            try
            {
                // Target entity
                ObjectBase? newTarget = null;
                if (msg.TargetEntityId > 0)
                    newTarget = StateSerializer.FindById(msg.TargetEntityId);
                wb.CurrentIntendedTargetObject = newTarget;

                // Aim points
                wb.AimPointGeoPosition = new GeoPosition
                {
                    _longitude = msg.AimLonDeg,
                    _latitude = msg.AimLatDeg,
                    _height = msg.AimHeight,
                };
                wb.InitialTargetGeoPosition = new GeoPosition
                {
                    _longitude = msg.InitLonDeg,
                    _latitude = msg.InitLatDeg,
                    _height = msg.InitHeight,
                };

                // Set CommandPosition so the tactical map shows the guidance line and range
                // (OnFixedUpdate is suppressed on guest, so this doesn't get set automatically)
                wb.CommandPosition = wb.AimPointGeoPosition;

                // Wire state flags - use reactive .Value setters so UI bindings re-fire
                bool onWire = (msg.Flags & WeaponWireStateMessage.FlagOnWire) != 0;
                bool connLost = (msg.Flags & WeaponWireStateMessage.FlagConnectionLost) != 0;
                bool connLostForever = (msg.Flags & WeaponWireStateMessage.FlagConnectionLostForever) != 0;

                // _onWire is a plain field, set via reflection
                var onWireField = AccessTools.Field(typeof(WeaponBase), "_onWire");
                if (onWireField != null) onWireField.SetValue(wb, onWire);

                wb.ConnectionLost.Value = connLost;
                wb.ConnectionLostForever.Value = connLostForever;

                // Wire speed/depth - call the real setters (inside the guard)
                if (wb is Torpedo torp)
                {
                    if (msg.WireSpeedIndex != WeaponWireStateMessage.WireSpeedUnchanged)
                        torp.SetWireSpeedSetting(msg.WireSpeedIndex);
                    if (msg.WireDepthFeet > 0f)
                        torp.OrderWireDepth(msg.WireDepthFeet);
                }

                Plugin.Log.LogInfo($"[WireState] Applied: weapon={msg.WeaponId} target={msg.TargetEntityId} " +
                    $"aim={msg.AimLonDeg:F3},{msg.AimLatDeg:F3} onWire={onWire} connLost={connLost}");
            }
            finally
            {
                OrderHandler.ApplyingFromNetwork = false;
            }
        }
    }
}
