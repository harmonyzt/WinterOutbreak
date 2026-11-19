using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Audio.AudioWeatherSystem;
using CommonAssets.Scripts.Audio;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private Fixed<float> _snowFootstepVolume;
        private readonly AudioClip[] _snowSteps = new AudioClip[5];
        private readonly AudioClip[] _snowRuns = new AudioClip[9];
        private Coroutine _snowStepLoad;
        private bool _snowStepLoadStarted;
        private int _previousSnowStep = -1;
        private int _previousSnowRun = -1;

        // Steps, runs and a slide on bare ice (frozen water), from the mod's own sound folder
        private readonly AudioClip[] _iceSteps = new AudioClip[5];
        private readonly AudioClip[] _iceRuns = new AudioClip[5];
        private readonly AudioClip[] _iceSlide = new AudioClip[1];
        private int _previousIceStep = -1;
        private int _previousIceRun = -1;

        private void BindSnowStepSettings()
        {
            _snowFootstepVolume = new Fixed<float>(30f);
        }

        private static string ResourcePath(string relativePath)
        {
            return Path.Combine(Path.GetDirectoryName(typeof(WinterOutbreakPlugin).Assembly.Location),
                "Resources", relativePath);
        }

        private void EnsureSnowStepAudio()
        {
            if (!_snowStepLoadStarted)
            {
                _snowStepLoadStarted = true;
                _snowStepLoad = StartCoroutine(LoadSnowStepAudio());
            }
        }

        private IEnumerator LoadSnowStepAudio()
        {
            yield return LoadSnowClips(_snowSteps, "snow_footstep");
            yield return LoadSnowClips(_snowRuns, "snow_run");
            yield return LoadSnowClips(_iceSteps, "ice_step");
            yield return LoadSnowClips(_iceRuns, "ice_run");
            yield return LoadSoundClip(_iceSlide, "ice_stop_slide");
            _snowStepLoad = null;
        }

        private IEnumerator LoadSnowClips(AudioClip[] clips, string name)
        {
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] != null)
                {
                    continue;
                }

                string path = ResourcePath($"sound/{name}{i + 1}.mp3");
                using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    clips[i] = DownloadHandlerAudioClip.GetContent(request);
                }
                else
                {
                    Logger.LogWarning($"Could not load {name}{i + 1}: {request.error}");
                }
            }
        }

        // One clip by its plain name (no number)
        private IEnumerator LoadSoundClip(AudioClip[] slot, string name)
        {
            if (slot[0] != null)
            {
                yield break;
            }

            string path = ResourcePath($"sound/{name}.mp3");
            using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                slot[0] = DownloadHandlerAudioClip.GetContent(request);
            }
            else
            {
                Logger.LogWarning($"Could not load {name}: {request.error}");
            }
        }

        private void ClearSnowStepAudio()
        {
            if (_snowStepLoad != null)
            {
                StopCoroutine(_snowStepLoad);
                _snowStepLoad = null;
            }

            DestroySnowClips(_snowSteps);
            DestroySnowClips(_snowRuns);
            DestroySnowClips(_iceSteps);
            DestroySnowClips(_iceRuns);
            DestroySnowClips(_iceSlide);
            _previousSnowStep = -1;
            _previousSnowRun = -1;
            _previousIceStep = -1;
            _previousIceRun = -1;
            _snowStepLoadStarted = false;
        }

        private static void DestroySnowClips(AudioClip[] clips)
        {
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] != null)
                {
                    Destroy(clips[i]);
                }

                clips[i] = null;
            }
        }

        private AudioClip PickSnowStep(Player player, bool running)
        {
            if ((!player.IsYourPlayer && !player.IsAI) || player.MovementContext?.IsGrounded != true)
            {
                return null;
            }

            // Bare ice gets ice steps, with or without voxel snow on the ground around it
            if (FrozenIce.IsBareIce(player.Position))
            {
                var ice = running ? PickSnowClip(_iceRuns, ref _previousIceRun) : null;
                return ice ?? PickSnowClip(_iceSteps, ref _previousIceStep);
            }

            if (_voxelSnow == null || !_voxelSnow.HasFootstepSnow(player.Position))
            {
                return null;
            }

            if (running)
            {
                var run = PickSnowClip(_snowRuns, ref _previousSnowRun);
                if (run != null)
                {
                    return run;
                }
            }

            return PickSnowClip(_snowSteps, ref _previousSnowStep);
        }

        // Stopping on bare ice: the slide
        private AudioClip PickIceStop(Player player)
        {
            if ((!player.IsYourPlayer && !player.IsAI) || player.MovementContext?.IsGrounded != true
                || !FrozenIce.IsBareIce(player.Position))
            {
                return null;
            }

            return _iceSlide[0];
        }

        private AudioClip PickSnowClip(AudioClip[] clips, ref int previous)
        {
            int first = _random.Next(clips.Length);
            for (int i = 0; i < clips.Length; i++)
            {
                int index = (first + i) % clips.Length;
                if (index != previous && clips[index] != null)
                {
                    previous = index;
                    return clips[index];
                }
            }

            return previous >= 0 ? clips[previous] : null;
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        private static class SnowStepRaidStartPatch
        {
            private static void Postfix(GameWorld __instance)
            {
                var plugin = _plugin;
                var season = __instance.SeasonsController;
                if (plugin == null || __instance is HideoutGameWorld || __instance is NarrateGameWorld
                    || season?.Season != ESeason.Winter || season.Status != ESeasonStatus.Winter)
                {
                    return;
                }

                plugin.TrackRaidWorld(__instance);
                plugin.EnsureSnowStepAudio();
            }
        }

        [HarmonyPatch]
        private static class SnowStepPlaybackPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Player), nameof(Player.PlayStepSound));
                yield return AccessTools.Method(typeof(Player), "DefaultPlay");
                var sprint = AccessTools.Inner(typeof(Player), "CG_Sprint");
                yield return AccessTools.Method(sprint, "MoveNext");

                // Fika replaces the sprint coroutine with its own per-frame tick that plays the sprint bank directly,
                // so without this target sprint steps never reach the snow sounds
                var fikaPlayer = AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer");
                var fikaSprint = fikaPlayer == null ? null : AccessTools.DeclaredMethod(fikaPlayer, "TickSprintState");
                if (fikaSprint != null)
                {
                    yield return fikaSprint;
                }
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
                ILGenerator generator, MethodBase original)
            {
                var code = instructions.ToList();
                var bankPlay = AccessTools.Method(typeof(SoundBank), nameof(SoundBank.Play), new[]
                {
                    typeof(BetterSource), typeof(EnvironmentType), typeof(float), typeof(float),
                    typeof(float), typeof(bool), typeof(bool)
                });
                var weatherPlay = AccessTools.Method(typeof(IPlayerStepAudioController),
                    nameof(IPlayerStepAudioController.Play));
                if (code.Count(instruction => instruction.Calls(bankPlay)) != 1
                    || code.Count(instruction => instruction.Calls(weatherPlay)) != 1)
                {
                    _plugin.Logger.LogWarning("Snow footsteps could not find both EFT playback calls.");
                    return code;
                }

                var clip = generator.DeclareLocal(typeof(AudioClip));
                var loadClip = AccessTools.Method(typeof(SnowStepPlaybackPatch), nameof(GetClip));
                var loadSprintClip = AccessTools.Method(typeof(SnowStepPlaybackPatch), nameof(GetSprintStartClip));
                var playSurface = AccessTools.Method(typeof(SnowStepPlaybackPatch), nameof(PlaySurface));
                var playWeather = AccessTools.Method(typeof(SnowStepPlaybackPatch), nameof(PlayWeather));

                // The EFT sprint coroutine holds its player in a field
                bool sprintCoroutine = !typeof(Player).IsAssignableFrom(original.DeclaringType);
                bool movementStateArgument = original.Name == "DefaultPlay";
                var result = new List<CodeInstruction>(code.Count + 8);

                foreach (var instruction in code)
                {
                    if (instruction.Calls(bankPlay))
                    {
                        instruction.opcode = OpCodes.Ldarg_0;
                        instruction.operand = null;
                        result.Add(instruction);

                        if (sprintCoroutine)
                        {
                            result.Add(new CodeInstruction(OpCodes.Ldfld,
                                AccessTools.Field(original.DeclaringType, "player_0")));
                        }
                        else if (movementStateArgument)
                        {
                            result.Add(new CodeInstruction(OpCodes.Ldarg_3));
                        }

                        result.Add(new CodeInstruction(OpCodes.Call, movementStateArgument ? loadSprintClip : loadClip));
                        result.Add(new CodeInstruction(OpCodes.Dup));
                        result.Add(new CodeInstruction(OpCodes.Stloc, clip));
                        result.Add(new CodeInstruction(OpCodes.Call, playSurface));
                    }
                    else if (instruction.Calls(weatherPlay))
                    {
                        instruction.opcode = OpCodes.Ldloc;
                        instruction.operand = clip;
                        result.Add(instruction);
                        result.Add(new CodeInstruction(OpCodes.Call, playWeather));
                    }
                    else
                    {
                        result.Add(instruction);
                    }
                }

                return result;
            }

            private static AudioClip GetClip(Player player)
            {
                _plugin?.OnSnowStepSound(player);
                return _plugin?.PickSnowStep(player, player.IsSprintEnabled);
            }

            private static AudioClip GetSprintStartClip(Player player, EAudioMovementState movement)
            {
                if (movement == EAudioMovementState.Stop)
                {
                    return _plugin?.PickIceStop(player);
                }

                return movement == EAudioMovementState.Sprint ? _plugin?.PickSnowStep(player, true) : null;
            }

            private static void PlaySurface(SoundBank bank, BetterSource source, EnvironmentType environment,
                float distance, float volume, float blend, bool stereo, bool oneShot, AudioClip clip)
            {
                if (clip == null)
                {
                    bank.Play(source, environment, distance, volume, blend, stereo, oneShot);
                }
                else if (_plugin != null && _plugin._snowFootstepVolume.Value > 0f
                    && source != null && distance <= source.MaxDistance)
                {
                    source.SetPitch(bank.RandomPitch);
                    float gain = _plugin._snowFootstepVolume.Value / 100f;
                    source.Play(clip, null, 1f, Mathf.Clamp01(volume) * gain, stereo, oneShot);
                }
            }

            private static void PlayWeather(IPlayerStepAudioController controller, EAudioMovementState movement,
                EnvironmentType environment, float distance, float volume, float blend, bool firstPerson,
                AudioClip clip)
            {
                if (clip == null)
                {
                    controller.Play(movement, environment, distance, volume, blend, firstPerson);
                }
            }
        }
    }
}
