using System.Collections;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;
using EFT.Communications;
using UnityEngine.Rendering;

namespace WinterOutbreak
{
    /// <summary>
    /// Lets the eyes adjust back after walking away from a bonfire.
    /// </summary>
    public sealed partial class WinterOutbreakPlugin
    {
        private const float BonfireGlareDistance = 8f;
        private const float BonfireClearDistance = 12f;
        private const float ExposureSettleSeconds = 2.5f;

        // Blend alpha during the settle
        private const float SettleAlphaStart = 0.04f;
        private const float SettleAlphaEnd = 0.25f;

        private static float _exposureSpeedOverride;

        private static readonly AccessTools.FieldRef<PrismEffects, bool> PrismFirstRun =
            AccessTools.FieldRefAccess<PrismEffects, bool>("firstRun");

        private readonly Fixed<KeyboardShortcut> _exposureTestKey = new Fixed<KeyboardShortcut>(new KeyboardShortcut(KeyCode.F8));
        private Coroutine _exposureTest;

        private bool _besideBonfire;
        private float _exposureSettleStarted = -1f;
        private float _nextExposureSample;
        private int _exposureSamplesLeft;
        private int _bonfiresSeen;

        private void UpdateBonfireExposure(Player player)
        {
            _bonfiresSeen = _bonfires.Count;

            if (_exposureTest == null && _bonfires.Count > 0 && _exposureTestKey.Value.IsDown())
            {
                _exposureTest = StartCoroutine(RunExposureTest(player));
            }

            float nearest = float.MaxValue;
            foreach (var bonfire in _bonfires)
            {
                if (bonfire.Root != null)
                {
                    nearest = Mathf.Min(nearest, Vector3.Distance(player.Position, bonfire.Root.transform.position));
                }
            }

            if (nearest <= BonfireGlareDistance)
            {
                _besideBonfire = true;
                _exposureSettleStarted = -1f;
                _exposureSpeedOverride = 0f;
            }
            else if (_besideBonfire && nearest >= BonfireClearDistance)
            {
                _besideBonfire = false;
                _exposureSettleStarted = Time.time;
                _exposureSamplesLeft = 0;
            }

            if (_exposureSettleStarted >= 0f)
            {
                float progress = (Time.time - _exposureSettleStarted) / ExposureSettleSeconds;
                if (progress >= 1f)
                {
                    FinishExposureSettle();
                }
                else
                {
                    float alpha = Mathf.Lerp(SettleAlphaStart, SettleAlphaEnd, progress * progress);
                    _exposureSpeedOverride = alpha / 0.0125f;
                }
            }

            if (_exposureSamplesLeft > 0 && Time.time >= _nextExposureSample)
            {
                _exposureSamplesLeft--;
                _nextExposureSample = Time.time + 2f;
            }
        }

        // The ramp gets most of the way back
        private void FinishExposureSettle()
        {
            var prism = FindExposurePrism();

            _exposureSettleStarted = -1f;
            _exposureSpeedOverride = 0f;
            if (prism != null)
            {
                PrismFirstRun(prism) = true;
            }

            _exposureSamplesLeft = 3;
            _nextExposureSample = Time.time + 0.3f;
        }

        // F8 test
        private IEnumerator RunExposureTest(Player player)
        {
            float nearest = float.MaxValue;
            foreach (var bonfire in _bonfires)
            {
                if (bonfire.Root != null)
                {
                    nearest = Mathf.Min(nearest, Vector3.Distance(player.Position, bonfire.Root.transform.position));
                }
            }

            var readings = new float[3];
            for (int step = 0; step < 3; step++)
            {
                SetBonfiresShown(step != 1);
                yield return null;
                yield return null;

                var prism = FindExposurePrism();
                if (prism == null)
                {
                    SetBonfiresShown(true);
                    _exposureTest = null;
                    yield break;
                }

                PrismFirstRun(prism) = true;
                yield return null;
                yield return null;

                var request = AsyncGPUReadback.Request(prism.AdaptationTexture, 0);
                yield return new WaitUntil(() => request.done);
                readings[step] = request.hasError ? -1f : request.GetData<Color32>()[0].r / 255f;
            }

            SetBonfiresShown(true);
            NotificationManager.DisplayMessageNotification(
                $"Exposure test: with {readings[0]:0.000}, hidden {readings[1]:0.000}, again {readings[2]:0.000}");
            _exposureTest = null;
        }

        private void SetBonfiresShown(bool shown)
        {
            foreach (var bonfire in _bonfires)
            {
                if (bonfire.Root != null)
                {
                    bonfire.Root.SetActive(shown);
                }
            }
        }

        private static PrismEffects FindExposurePrism()
        {
            var camera = Camera.main;
            var prism = camera != null ? camera.GetComponent<PrismEffects>() : null;
            return prism != null && prism.useExposure ? prism : null;
        }

        private void ClearBonfireExposure()
        {
            if (_exposureTest != null)
            {
                StopCoroutine(_exposureTest);
                _exposureTest = null;
                SetBonfiresShown(true);
            }

            _besideBonfire = false;
            _exposureSettleStarted = -1f;
            _exposureSpeedOverride = 0f;
            _exposureSamplesLeft = 0;
            _bonfiresSeen = 0;
        }

        [HarmonyPatch(typeof(PrismEffects), nameof(PrismEffects.UpdateShaderValues))]
        private static class BonfireExposurePatch
        {
            private static readonly int ExposureSpeed = Shader.PropertyToID("_ExposureSpeed");

            private static void Postfix(PrismEffects __instance)
            {
                if (_exposureSpeedOverride <= 0f || !__instance.useExposure)
                {
                    return;
                }

                // m_Material2 runs the adaptation pass
                var material = __instance.m_Material2;
                if (material != null)
                {
                    material.SetFloat(ExposureSpeed, Mathf.Max(__instance.exposureSpeed, _exposureSpeedOverride));
                }
            }
        }
    }
}
