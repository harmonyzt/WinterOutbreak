using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WinterOutbreak
{
    public sealed partial class WinterOutbreakPlugin
    {
        private const string InventoryTemperatureName = "Winter Outbreak Inventory Temperature";
        private const float InventoryTemperatureGap = 2f;

        [HarmonyPatch(typeof(ItemsPanel), nameof(ItemsPanel.Show))]
        private static class InventoryTemperaturePatch
        {
            private static void Prefix(ItemsPanel __instance)
            {
                var summary = __instance._healthParametersPanel;
                var healthPanel = __instance._healthPanel;
                var healthStats = healthPanel != null ? healthPanel._parametersPanel : null;
                if (summary == null || summary == healthStats)
                {
                    return;
                }

                if (summary._temperature == null && healthStats != null && healthStats._temperature != null
                    && summary._energy != null && summary._energy.transform is RectTransform energy)
                {
                    // A sibling of the vanilla row, outside its layout
                    var temperature = Instantiate(healthStats._temperature, energy.parent, false);
                    temperature.name = InventoryTemperatureName;
                    var element = temperature.GetComponent<LayoutElement>()
                        ?? temperature.gameObject.AddComponent<LayoutElement>();
                    element.ignoreLayout = true;
                    temperature.transform.SetAsLastSibling();
                    summary._temperature = temperature;
                }

                var shown = summary._temperature;
                if (shown == null || shown.name != InventoryTemperatureName)
                {
                    return;
                }

                shown.gameObject.SetActive(true);
                PlaceAboveRow(summary, (RectTransform)shown.transform);
            }

            private static void PlaceAboveRow(EFT.UI.Health.HealthParametersPanel summary, RectTransform rect)
            {
                if (summary._energy == null || !(summary._energy.transform is RectTransform energy)
                    || !(energy.parent is RectTransform row))
                {
                    return;
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(row);

                // Line up with the indicator
                RectTransform first = energy;
                foreach (var candidate in new EFT.UI.Health.HealthParameterPanel[] { summary._weight, summary._overallHealth })
                {
                    if (candidate != null && candidate.transform is RectTransform rect2 && rect2.parent == row
                        && rect2.gameObject.activeSelf && rect2.localPosition.x < first.localPosition.x)
                    {
                        first = rect2;
                    }
                }
                Vector2 firstCenter = (Vector2)first.localPosition + first.rect.center;
                float energyCenterY = energy.localPosition.y + energy.rect.center.y;
                float height = Mathf.Max(energy.rect.height, rect.rect.height);

                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(Mathf.Max(rect.rect.width, first.rect.width), energy.rect.height);
                rect.anchoredPosition = new Vector2(
                    firstCenter.x - first.rect.width * 0.5f + rect.sizeDelta.x * 0.5f - row.rect.center.x,
                    energyCenterY + height + InventoryTemperatureGap - row.rect.center.y);
            }
        }
    }
}
