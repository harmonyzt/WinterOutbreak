using System;
using System.Collections;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const string BlizzardWindFile = "sound/blizzard_wind.ogg";
        private const float BlizzardWindFadeIn = 2.5f;
        private const float BlizzardWindFadeOut = 1.5f;

        private ConfigEntry<int> _blizzardWindVolume;
        private AudioClip _blizzardWindClip;
        private AudioSource _blizzardWindSource;
        private Coroutine _blizzardWindLoad;
        private bool _blizzardWindLoadStarted;
        private float _blizzardWindFade;

        private void BindBlizzardWindSettings()
        {
            _blizzardWindVolume = Config.Bind("Blizzard Wind", "Volume", 100,
                new ConfigDescription("Volume of the wind around you when outdoors in a Blizzard.", new AcceptableValueRange<int>(0, 100)));
        }

        private void UpdateBlizzardWind(Player player)
        {
            bool wanted = player != null && _preset == Blizzard && player.HealthController?.IsAlive == true && player.Environment != EnvironmentType.Indoor
                          && _blizzardWindVolume.Value > 0;

            if (wanted && !_blizzardWindLoadStarted)
            {
                _blizzardWindLoadStarted = true;
                _blizzardWindLoad = StartCoroutine(LoadBlizzardWind());
            }

            if (_blizzardWindClip == null)
            {
                return;
            }

            if (wanted)
            {
                EnsureBlizzardWindSource();
            }

            var source = _blizzardWindSource;
            if (source == null)
            {
                return;
            }

            float fadeTime = wanted ? BlizzardWindFadeIn : BlizzardWindFadeOut;
            _blizzardWindFade = Mathf.MoveTowards(_blizzardWindFade, wanted ? 1f : 0f, Time.deltaTime / fadeTime);

            // An eased curve sounds smoother than a straight line, so the wind swells in and dies away.
            float eased = _blizzardWindFade * _blizzardWindFade * (3f - 2f * _blizzardWindFade);
            source.volume = eased * _blizzardWindVolume.Value / 100f;

            if (_blizzardWindFade > 0f && !source.isPlaying)
            {
                source.UnPause();
                if (!source.isPlaying)
                {
                    source.time = UnityEngine.Random.Range(0f, _blizzardWindClip.length);
                    source.Play();
                }
            }
            else if (_blizzardWindFade <= 0f && source.isPlaying)
            {
                source.Pause();
            }
        }

        private IEnumerator LoadBlizzardWind()
        {
            string path = ResourcePath(BlizzardWindFile);
            using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);

            // Keep it compressed in memory, the way EFT keeps its own ambience clips.
            ((DownloadHandlerAudioClip)request.downloadHandler).compressed = true;
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                _blizzardWindClip = DownloadHandlerAudioClip.GetContent(request);
                _blizzardWindClip.name = "winterstorms_blizzard_wind";
            }
            else
            {
                Logger.LogWarning($"Could not load {BlizzardWindFile}: {request.error}");
            }

            _blizzardWindLoad = null;
        }

        private void EnsureBlizzardWindSource()
        {
            if (_blizzardWindSource != null)
            {
                return;
            }

            var holder = new GameObject("WinterOutbreak Blizzard Wind");
            holder.transform.SetParent(transform, false);

            var source = holder.AddComponent<AudioSource>();
            source.clip = _blizzardWindClip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.bypassReverbZones = true;
            source.dopplerLevel = 0f;
            source.priority = 64;
            source.volume = 0f;
            source.outputAudioMixerGroup = WindMixerGroup();

            _blizzardWindSource = source;
            _blizzardWindFade = 0f;
        }

        // The same mixer route as EFT's wind, so the game's ambient volume and headsets apply to it too.
        private AudioMixerGroup WindMixerGroup()
        {
            if (_windBlender != null && _windBlender._outputMixerGroup != null)
            {
                return _windBlender._outputMixerGroup;
            }

            return Singleton<BetterAudio>.Instantiated ? Singleton<BetterAudio>.Instance.AmbientOutMixer : null;
        }

        private void StopBlizzardWind()
        {
            if (_blizzardWindSource != null)
            {
                Destroy(_blizzardWindSource.gameObject);
            }

            _blizzardWindSource = null;
            _blizzardWindFade = 0f;
        }

        private void ClearBlizzardWind()
        {
            StopBlizzardWind();

            if (_blizzardWindLoad != null)
            {
                StopCoroutine(_blizzardWindLoad);
                _blizzardWindLoad = null;
            }

            if (_blizzardWindClip != null)
            {
                Destroy(_blizzardWindClip);
            }

            _blizzardWindClip = null;
            _blizzardWindLoadStarted = false;
        }
    }
}
