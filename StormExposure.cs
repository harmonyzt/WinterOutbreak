using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFTEffectManager;
using HarmonyLib;
using UnityEngine;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const float ColdThreshold = 34f;
        private const float NormalTemperature = 36.6f;
        private const float FireTemperature = 36.9f;
        private const float LowestTemperature = 30f;
        private const float ExposureInterval = 0.5f;

        private const float MaxSnowSlowdown = 0.25f;

        // Snow slows you whether you walk or stand in it:
        private const float SnowSlowdownWalkingSeconds = 8f;
        private const float SnowSlowdownStandingSeconds = 60f;
        private const float SnowRecoverySeconds = 45f;
        private const float ShallowerSnowRecoverySeconds = 20f;
        private const float MinimumSlowingSnow = 0.02f;

        private const float FullySlowingSnow = 0.25f;

        // Warm gear
        private const float GearCoolingReduction = 0.45f;
        private const float GearWarmingBoost = 0.6f;
        private const float GearBuffShown = 0.38f;
        private const string SnowSpeedPenalty = PluginId + ".SnowSlowdown";

        private readonly RaidFires _fires = new RaidFires();
        private ConfigEntry<bool> _frostbiteEnabled;
        private ConfigEntry<bool> _gradualFrostbite;
        private Fixed<float> _frostbiteBuildUp;
        private Fixed<float> _frostbiteDelay;
        private Fixed<float> _outdoorCooling;
        private Fixed<float> _indoorWarming;
        private Fixed<float> _fireWarming;

        private EffectRegistration _lowTemperature;
        private EffectRegistration _customFrostbite;
        private EffectRegistration _warmingUp;
        private EffectRegistration _snowSlowed;
        private EffectRegistration _gearWarmth;
        private float _gearWarmthScore;
        private float _snowSlowdown;
        private float _appliedSnowSlowdown;
        private Vector3 _lastSlowdownPosition;
        private bool _hasSlowdownPosition;
        private ActiveHealthController _exposedHealth;
        private EBodyPart? _healingLimb;
        private ActiveHealthController _temperatureHealth;
        private float _environmentTemperatureChange;
        private float _lastExposureTick = -1f;
        private float _timeBesideFire;
        private float _timeAtLowTemperature;
        private float _frostbiteSeverity;

        private void BindFrostbiteSettings()
        {
            _frostbiteEnabled = Config.Bind("Frostbite", "Enabled", true,
                "Your body cools down outdoors in winter raids: Low Temperature, then Frostbite, which slowly costs "
                + "health (up to 25 HP a minute) until you warm up indoors or by a fire.");

            _gradualFrostbite = Config.Bind("Frostbite", "Gradual Build Up", true, "The frost on the screen builds up slowly after Frostbite starts, instead of all at once.");

            _frostbiteDelay = new Fixed<float>(120f);
            _frostbiteBuildUp = new Fixed<float>(60f);
            _outdoorCooling = new Fixed<float>(0.9f);
            _indoorWarming = new Fixed<float>(0.45f);
            _fireWarming = new Fixed<float>(2.4f);
        }

        private void RegisterWinterEffects()
        {
            EffectsManager.Init();

            _lowTemperature = RegisterEffect<WinterLowTemperature>("Low Temperature", "effect_lowtemp.png");
            _customFrostbite = RegisterEffect<WinterFrostbite>("Frostbite", "effect_frozen.png");
            _warmingUp = RegisterEffect<WinterWarmingUp>("Warming Up", "effect_warm.png");
            _snowSlowed = RegisterEffect<WinterSnowSlowed>("Slowed by Snow", "effect_slowed.png");
            _gearWarmth = RegisterEffect<WinterGearWarmth>("Dressed for the Cold", "effect_warmgear.png");
            EffectsManager.ErgonomicsPenalties.Add(PluginId, health => _lowTemperature.Find(health, EBodyPart.Head) != null ? -0.15f : 0f);
            EffectsManager.MovementSpeedPenalties.Add(SnowSpeedPenalty,
                health => _appliedSnowSlowdown > 0f && _snowSlowed.FindExisting(health, EBodyPart.Head) != null ? 1f - MaxSnowSlowdown * _appliedSnowSlowdown : (float?)null);
            _fires.FireFound = position => _voxelSnow?.AddFire(position, false);
        }

        private static void RemoveWinterPenalties()
        {
            EffectsManager.ErgonomicsPenalties.Remove(PluginId);
            EffectsManager.MovementSpeedPenalties.Remove(SnowSpeedPenalty);
        }

        private static EffectRegistration RegisterEffect<T>(string name, string icon) where T : ActiveHealthController.Effect, new()
        {
            return EffectsManager.RegisterEffect<T>(typeof(WinterOutbreakPlugin).Assembly, new EffectOptions
            {
                DisplayName = name,
                IconFilePath = "Resources/effects/" + icon,
                DelayTime = 0f,
                WorkTime = float.PositiveInfinity,
                ResidueTime = 0f,
                RemoveOnBodyPartDestroyed = false,
                PersistAcrossRaids = false
            });
        }

        private void UpdateWinterExposure(Player player)
        {
            var health = player != null ? player.ActiveHealthController : null;
            if (health == null || !health.IsAlive)
            {
                ResetExposure();
                return;
            }

            float now = Time.time;
            if (_exposedHealth != health)
            {
                ResetExposure();
                _exposedHealth = health;
                _lastExposureTick = now - ExposureInterval;
            }

            float elapsed = now - _lastExposureTick;
            if (elapsed < ExposureInterval)
            {
                return;
            }

            _lastExposureTick = now;
            bool nearFire = _fires.IsNearFire(player, now);
            bool indoors = player.Environment == EnvironmentType.Indoor;

            UpdateBarrelHealing(health, nearFire, elapsed);
            UpdateFrozenFood(player, nearFire, indoors, elapsed);

            if (_frostbiteEnabled.Value && player.Side != EPlayerSide.Savage)
            {
                _gearWarmthScore = GearWarmth(player);
                SetEffect(_gearWarmth, health, _gearWarmthScore >= GearBuffShown);
                UpdateBodyTemperature(health, nearFire, indoors, elapsed);
                UpdateSnowSlowdown(player, health, elapsed);
            }
            else
            {
                ClearColdEffects();
            }
        }

        private void UpdateSnowSlowdown(Player player, ActiveHealthController health, float elapsed)
        {
            Vector3 position = player.Position;
            Vector3 travel = _hasSlowdownPosition ? position - _lastSlowdownPosition : Vector3.zero;
            travel.y = 0f;
            _lastSlowdownPosition = position;
            _hasSlowdownPosition = true;

            bool grounded = player.MovementContext != null && player.MovementContext.IsGrounded;
            bool walking = grounded && travel.sqrMagnitude >= 0.08f * 0.08f * elapsed / ExposureInterval;
            float depth = 0f;
            bool inSnow = grounded && TryGetVoxelSnowDepth(position, out depth) && depth >= MinimumSlowingSnow;

            // Deeper snow slows more, up to the full penalty at 25 cm. The level moves toward that: fast while walking
            // through it, slowly while standing in it, and it wears off slowly once out of it.
            float target = inSnow ? Mathf.InverseLerp(MinimumSlowingSnow, FullySlowingSnow, depth) : 0f;
            if (_snowSlowdown < target)
            {
                float seconds = walking ? SnowSlowdownWalkingSeconds : SnowSlowdownStandingSeconds;
                _snowSlowdown = Mathf.Min(target, _snowSlowdown + elapsed / seconds);
            }
            else
            {
                float seconds = inSnow ? ShallowerSnowRecoverySeconds : SnowRecoverySeconds;
                _snowSlowdown = Mathf.Max(target, _snowSlowdown - elapsed / seconds);
            }

            SetEffect(_snowSlowed, health, _snowSlowdown > 0.02f);
            ApplySnowSlowdown(player);
        }

        private void ApplySnowSlowdown(Player player)
        {
            // Two-percent steps keep the speed cap from being rebuilt on every tick.
            float applied = Mathf.Ceil(_snowSlowdown * 50f) / 50f;
            if (Mathf.Approximately(applied, _appliedSnowSlowdown))
            {
                return;
            }

            _appliedSnowSlowdown = applied;
            if (player != null)
            {
                EffectsManager.RefreshPenalties(player);
            }
        }

        private void ClearSnowSlowdown()
        {
            if (_exposedHealth != null)
            {
                SetEffect(_snowSlowed, _exposedHealth, false);
            }

            _snowSlowdown = 0f;
            _hasSlowdownPosition = false;
            if (_appliedSnowSlowdown > 0f)
            {
                _appliedSnowSlowdown = 0f;
                var player = EffectsManager.RaidPlayer;
                if (player != null)
                {
                    EffectsManager.RefreshPenalties(player);
                }
            }
        }

        private void UpdateBarrelHealing(ActiveHealthController health, bool nearFire, float elapsed)
        {
            SetEffect(_warmingUp, health, nearFire);
            if (!nearFire)
            {
                _timeBesideFire = 0f;
                _healingLimb = null;
                return;
            }

            float previousTime = _timeBesideFire;
            _timeBesideFire += elapsed;
            float healingTime = _timeBesideFire - Mathf.Max(previousTime, BarrelSettings.DelayBeforeHealing.Value);
            var warmingUp = _warmingUp.Find(health, EBodyPart.Head) as WinterWarmingUp;
            if (healingTime <= 0f || warmingUp == null)
            {
                return;
            }

            if (!_healingLimb.HasValue || health.IsBodyPartDestroyed(_healingLimb.Value) || health.GetBodyPartHealth(_healingLimb.Value).AtMaximum)
            {
                _healingLimb = LimbPriority.PickNextLimb(health);
            }

            float rate = _healingLimb.HasValue ? BarrelSettings.HealRatePerMinute.Value / 60f : 0f;
            warmingUp.SetHealingRate(rate);

            if (_healingLimb.HasValue)
            {
                EffectsManager.HealPart(health, _healingLimb.Value, healingTime * rate);
            }
        }

        private void UpdateBodyTemperature(ActiveHealthController health, bool nearFire, bool indoors, float elapsed)
        {
            if (_temperatureHealth != health)
            {
                _temperatureHealth = health;
                _environmentTemperatureChange = 0f;
            }

            float current = health.Temperature.Current;
            float target = nearFire ? FireTemperature : indoors ? NormalTemperature : LowestTemperature;
            float rate = nearFire ? _fireWarming.Value : indoors ? _indoorWarming.Value : _outdoorCooling.Value;
            // Warm gear keeps body heat in: slower cooling outside, faster warming inside and by a fire.
            rate *= nearFire || indoors ? 1f + GearWarmingBoost * _gearWarmthScore : 1f - GearCoolingReduction * _gearWarmthScore;
            float change = rate * elapsed / 60f;
            float delta = target - current;
            if (nearFire)
            {
                delta = Mathf.Max(0f, delta);
            }
            else if (!indoors)
            {
                delta = Mathf.Min(0f, delta);
            }

            delta = Mathf.Clamp(delta, -change, change);
            if (delta != 0f)
            {
                health.ChangeTemperature(delta);
                _environmentTemperatureChange += health.Temperature.Current - current;
            }

            if (health.Temperature.Current > ColdThreshold)
            {
                ClearTemperatureEffects();
                return;
            }

            SetEffect(_lowTemperature, health, true);
            _timeAtLowTemperature += elapsed;

            float frostbiteTime = _timeAtLowTemperature - _frostbiteDelay.Value;
            if (frostbiteTime >= 0f)
            {
                _frostbiteSeverity = _gradualFrostbite.Value ? Mathf.Lerp(0.25f, 1f, frostbiteTime / _frostbiteBuildUp.Value) : 1f;
                SetEffect(_customFrostbite, health, true);
                ApplyColdDamage(health, elapsed);
            }
        }

        // Once Frostbite has set in, the body loses up to 25 HP a minute, scaled by how far the frostbite has built up
        // and how close the body is to the lowest temperature. The loss goes straight off the limbs, extremities first,
        // without the pain, bleed or fracture rolls of a hit; a limb at zero blacks out, and the head or chest at zero
        // is death.
        private const float MaxColdDamagePerMinute = 25f;

        private static readonly EBodyPart[] ColdDamageOrder =
        {
            EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg, EBodyPart.Stomach, EBodyPart.Chest, EBodyPart.Head
        };

        private void ApplyColdDamage(ActiveHealthController health, float elapsed)
        {
            float coldness = Mathf.InverseLerp(ColdThreshold, LowestTemperature, health.Temperature.Current);
            float amount = MaxColdDamagePerMinute * _frostbiteSeverity * coldness * elapsed / 60f;
            if (amount <= 0f)
            {
                return;
            }

            // A random extremity among those that still have health, then the torso and head only when none is left.
            int start = Random.Range(0, 4);
            for (int i = 0; i < ColdDamageOrder.Length; i++)
            {
                EBodyPart part = i < 4 ? ColdDamageOrder[(start + i) % 4] : ColdDamageOrder[i];
                if (health.IsBodyPartDestroyed(part) || health.GetBodyPartHealth(part).AtMinimum)
                {
                    continue;
                }

                var damage = new DamageInfo
                {
                    DamageType = EDamageType.Stimulator,
                    Damage = amount
                };
                health.ChangeHealth(part, -amount, damage);
                if (health.GetBodyPartHealth(part).AtMinimum)
                {
                    health.DestroyBodyPart(part, EDamageType.Stimulator);
                }

                return;
            }
        }

        private static void SetEffect(EffectRegistration effect, ActiveHealthController health, bool active)
        {
            bool exists = effect.FindExisting(health, EBodyPart.Head) != null;
            if (active && !exists)
            {
                effect.Add(health, EBodyPart.Head);
            }
            else if (!active && exists)
            {
                effect.Remove(health, EBodyPart.Head);
            }
        }

        private void ClearColdEffects()
        {
            ClearTemperatureEffects();
            ClearSnowSlowdown();
            if (_exposedHealth != null)
            {
                SetEffect(_gearWarmth, _exposedHealth, false);
            }

            _gearWarmthScore = 0f;
        }

        private void ClearTemperatureEffects()
        {
            if (_exposedHealth != null)
            {
                SetEffect(_lowTemperature, _exposedHealth, false);
                SetEffect(_customFrostbite, _exposedHealth, false);
            }

            _timeAtLowTemperature = 0f;
            _frostbiteSeverity = 0f;
        }

        /// <summary>
        /// 0 to 1: how well the worn gear keeps body heat in. Each piece adds a little; body armour and a backpack
        /// (about 0.4 together) are enough to show the buff. A rig counts less than armour, an armoured rig nearly as
        /// much. Fully kitted (helmet, armour, rig, backpack, headset, face cover) is about 0.9.
        /// </summary>
        private static float GearWarmth(Player player)
        {
            var equipment = player.Equipment;
            if (equipment == null)
            {
                return 0f;
            }

            float score = 0f;
            score += Worn(equipment, EquipmentSlot.ArmorVest) ? 0.25f : 0f;
            Item rig = equipment.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem;
            if (rig != null)
            {
                score += rig.GetItemComponent<ArmorComponent>() != null ? 0.22f : 0.12f;
            }

            score += Worn(equipment, EquipmentSlot.Backpack) ? 0.15f : 0f;
            score += Worn(equipment, EquipmentSlot.Headwear) ? 0.15f : 0f;
            score += Worn(equipment, EquipmentSlot.FaceCover) ? 0.12f : 0f;
            score += Worn(equipment, EquipmentSlot.Earpiece) ? 0.08f : 0f;
            score += Worn(equipment, EquipmentSlot.Eyewear) ? 0.03f : 0f;
            return Mathf.Clamp01(score);
        }

        private static bool Worn(InventoryEquipment equipment, EquipmentSlot slot)
        {
            return equipment.GetSlot(slot)?.ContainedItem != null;
        }

        private void ResetExposure()
        {
            ClearColdEffects();
            if (_exposedHealth != null)
            {
                SetEffect(_warmingUp, _exposedHealth, false);
                _fires.Clear();
            }

            _hasSlowdownPosition = false;

            _exposedHealth = null;
            _healingLimb = null;
            _lastExposureTick = -1f;
            _timeBesideFire = 0f;
        }

        [HarmonyPatch]
        private static class RaidTemperatureStorePatch
        {
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Store), new[] { typeof(Profile.HealthInfo) });
                yield return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Store),
                    new[] { typeof(GlobalConfiguration.TransitGlobalSettings), typeof(Profile.HealthInfo) });
            }

            private static void Postfix(ActiveHealthController __instance, Profile.HealthInfo __result)
            {
                var plugin = _plugin;
                var temperature = __result?.Temperature;
                if (plugin?._temperatureHealth == __instance && temperature != null)
                {
                    temperature.Current = Mathf.Clamp(__instance.Temperature.Current - plugin._environmentTemperatureChange, temperature.Minimum, temperature.Maximum);
                }
            }
        }

        [HarmonyPatch(typeof(EffectsController.FrostbiteAccumulator), MethodType.Constructor, typeof(EffectsController), typeof(FrostbiteEffect), typeof(System.Type[]))]
        private static class FrostbiteVisualTypesPatch
        {
            private static void Prefix(ref System.Type[] __2)
            {
                int count = __2.Length;
                System.Array.Resize(ref __2, count + 2);
                __2[count] = typeof(IWinterLowTemperature);
                __2[count + 1] = typeof(IWinterFrostbite);
            }
        }

        [HarmonyPatch(typeof(EffectsController.FrostbiteAccumulator), "UpdateAmount")]
        private static class FrostbiteVisualAmountPatch
        {
            private static void Prefix(System.Collections.Generic.List<IHealthEffect> ___ActiveEffects, ref float ___MaxEffectValue, out float __state)
            {
                __state = ___MaxEffectValue;
                bool lowTemperature = false;
                bool frostbite = false;
                foreach (var effect in ___ActiveEffects)
                {
                    if (effect is IWinterFrostbite)
                    {
                        frostbite = true;
                    }
                    else if (effect is IWinterLowTemperature)
                    {
                        lowTemperature = true;
                    }
                    else
                    {
                        return;
                    }
                }

                if (frostbite || lowTemperature)
                {
                    float severity = frostbite ? _plugin?._frostbiteSeverity ?? 1f : 0.15f;
                    ___MaxEffectValue = Mathf.Lerp(1f, __state, severity);
                }
            }

            private static void Postfix(ref float ___MaxEffectValue, float __state)
            {
                ___MaxEffectValue = __state;
            }
        }
    }

    public interface IWinterLowTemperature : IHealthEffect
    {
    }

    public interface IWinterFrostbite : IHealthEffect
    {
    }

    public interface IWinterWarmingUp : IHealthEffect
    {
    }

    public interface IWinterSnowSlowed : IHealthEffect
    {
    }

    public sealed class WinterSnowSlowed : ActiveHealthController.Effect, IWinterSnowSlowed, IEffectTriggersUIPanel
    {
    }

    public sealed class WinterLowTemperature : ActiveHealthController.Effect, IWinterLowTemperature, IEffectTriggersUIPanel
    {
    }

    public sealed class WinterFrostbite : ActiveHealthController.Effect, IWinterFrostbite, IEffectTriggersUIPanel
    {
    }

    public interface IWinterGearWarmth : IHealthEffect
    {
    }

    public sealed class WinterGearWarmth : ActiveHealthController.Effect, IWinterGearWarmth, IEffectTriggersUIPanel
    {
    }

    public sealed class WinterWarmingUp : ActiveHealthController.Effect, IWinterWarmingUp, IEffectTriggersUIPanel
    {
        internal void SetHealingRate(float rate)
        {
            SetHealthRatesPerSecond(rate, 0f, 0f, 0f);
        }
    }
}
