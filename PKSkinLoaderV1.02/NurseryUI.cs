using HarmonyLib;
using PrehistoricKingdom;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PKSkinLoader
{
    /// <summary>Nursery skin row: hides stand-in slots, makes the row scrollable, paints the preview model.</summary>
    internal static class NurseryUI
    {
        private static bool _warnedMismatch;

        public static void Refresh(NurseryMenuV2 nursery)
        {
            if (nursery == null) return;
            SkinApplier.Nursery = nursery;
            HideStandIns(nursery);
            try { FixSkinCardSizing(nursery); }
            catch (Exception ex) { SkinModBase.ErrorOnce("cards", "[Nursery] Skin row layout failed: " + ex.Message); }
        }

        private static void HideStandIns(NurseryMenuV2 nursery)
        {
            try
            {
                var data = Traverse.Create(nursery).Field("currentSelectedAnimalData").GetValue<BaseAnimalData>();
                var toggles = Traverse.Create(nursery).Field("skinToggles").GetValue<List<Toggle>>();
                if (data == null || data.skinsData == null || toggles == null) return;

                var list = data.skinsData;
                if (toggles.Count < list.Count)
                {
                    if (!_warnedMismatch && SkinModBase.Verbose)
                        SkinModBase.Log.Warning("[Nursery] " + toggles.Count + " skin cards for " + list.Count + " skins - not hiding stand-ins.");
                    _warnedMismatch = true;
                    return;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    var t = toggles[i];
                    if (t != null && (SkinInjector.IsStandIn(list[i]) || UninstallPrep.HideInNursery(list[i])) && t.gameObject.activeSelf) t.gameObject.SetActive(false);
                }
            }
            catch (Exception ex)
            {
                if (SkinModBase.Verbose) SkinModBase.Log.Warning("[Nursery] Hiding stand-ins failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- preview model

        public static void PaintPreview(AnimalPreviewComponent previewComp, VirtualAnimal virtualAnimal)
        {
            if (previewComp == null || virtualAnimal == null) return;
            if (SkinInjector.DefFor(virtualAnimal.OwnAD, virtualAnimal.skinIdx) == null
                && VanillaTweaks.For(virtualAnimal.OwnAD, virtualAnimal.skinIdx) == null) return;
            MelonLoader.MelonCoroutines.Start(PaintPreviewRoutine(previewComp, virtualAnimal));
        }

        /// <summary>
        /// Waits (max ~3s) until the preview model has its real skin material (not the loading wireframe),
        /// then paints ONLY the preview model.
        /// </summary>
        private static IEnumerator PaintPreviewRoutine(AnimalPreviewComponent previewComp, VirtualAnimal virtualAnimal)
        {
            BaseAnimalData data = virtualAnimal.OwnAD;
            int skinIdx = virtualAnimal.skinIdx;

            for (int i = 0; i < 30; i++)
            {
                if (previewComp == null) yield break;
                IAnimal preview = previewComp.currentAnimalPreview;
                if (!previewComp.IsLoadingAnimal && preview != null)
                {
                    if (preview.BaseData != data) yield break;   // user picked something else meanwhile

                    bool ready = false;
                    foreach (var r in SkinApplier.GetAnimalRenderers(preview))
                    {
                        var sm = r != null ? r.sharedMaterial : null;
                        if (sm != null && sm.HasProperty("_AdultDiffuse")) { ready = true; break; }
                    }
                    if (ready)
                    {
                        var def = SkinInjector.DefFor(data, skinIdx);
                        bool male = !virtualAnimal.Sex.ToString().Equals("Female", StringComparison.OrdinalIgnoreCase);
                        if (def != null) SkinApplier.ApplyDef(preview, def, male);
                        else SkinApplier.ApplyVanillaTweak(preview, data, skinIdx, male);
                        yield break;
                    }
                }
                yield return new WaitForSeconds(0.1f);
            }
        }

        // ---------------------------------------------------------------- scrollable skin row (from v3)

        public static void FixSkinCardSizing(NurseryMenuV2 nurseryInstance)
        {
            if (nurseryInstance == null) return;

            List<Toggle> skinToggles = null;
            try
            {
                skinToggles = Traverse.Create(nurseryInstance).Field("skinToggles").GetValue<List<Toggle>>();
            }
            catch { }

            if (skinToggles == null || skinToggles.Count == 0) return;

            RectTransform cardContainer = skinToggles[0]?.transform?.parent as RectTransform;
            if (cardContainer == null) return;

            if (cardContainer.name == "ModdedScrollContent")
            {
                cardContainer = cardContainer.parent as RectTransform;
                if (cardContainer == null) return;
            }

            LayoutElement containerLe = cardContainer.GetComponent<LayoutElement>();
            if (containerLe == null) containerLe = cardContainer.gameObject.AddComponent<LayoutElement>();
            containerLe.flexibleWidth = 1f;
            containerLe.minWidth = 150f;

            Image img = cardContainer.GetComponent<Image>();
            if (img == null)
            {
                img = cardContainer.gameObject.AddComponent<Image>();
                img.color = new Color(0, 0, 0, 0);
            }
            img.raycastTarget = true;

            Transform scrollContentTr = cardContainer.Find("ModdedScrollContent");
            RectTransform scrollContent = scrollContentTr as RectTransform;

            if (scrollContent == null)
            {
                GameObject contentObj = new GameObject("ModdedScrollContent", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
                scrollContent = contentObj.GetComponent<RectTransform>();
                scrollContent.SetParent(cardContainer, false);

                scrollContent.anchorMin = new Vector2(0f, 0f);
                scrollContent.anchorMax = new Vector2(0f, 1f);
                scrollContent.pivot = new Vector2(0f, 0.5f);
                scrollContent.anchoredPosition = Vector2.zero;
                scrollContent.sizeDelta = Vector2.zero;

                HorizontalLayoutGroup origHlg = cardContainer.GetComponent<HorizontalLayoutGroup>();
                HorizontalLayoutGroup newHlg = scrollContent.GetComponent<HorizontalLayoutGroup>();

                var alignProp = typeof(HorizontalLayoutGroup).GetProperty("childAlignment");

                if (origHlg != null)
                {
                    newHlg.spacing = origHlg.spacing > 0 ? origHlg.spacing : 6f;
                    newHlg.padding = origHlg.padding;

                    try
                    {
                        if (alignProp != null)
                        {
                            var val = alignProp.GetValue(origHlg);
                            alignProp.SetValue(newHlg, val);
                        }
                    }
                    catch { }

                    UnityEngine.Object.DestroyImmediate(origHlg);
                }
                else
                {
                    newHlg.spacing = 6f;
                    try
                    {
                        if (alignProp != null)
                        {
                            alignProp.SetValue(newHlg, 3);
                        }
                    }
                    catch { }
                }

                newHlg.childControlWidth = false;
                newHlg.childControlHeight = true;
                newHlg.childForceExpandWidth = false;
                newHlg.childForceExpandHeight = true;

                ContentSizeFitter fitter = scrollContent.GetComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

                if (cardContainer.GetComponent<RectMask2D>() == null)
                {
                    cardContainer.gameObject.AddComponent<RectMask2D>();
                }

                ScrollRect scroll = cardContainer.GetComponent<ScrollRect>();
                if (scroll == null)
                {
                    scroll = cardContainer.gameObject.AddComponent<ScrollRect>();
                }
                scroll.content = scrollContent;
                scroll.viewport = cardContainer;
                scroll.horizontal = true;
                scroll.vertical = false;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 35f;
            }

            List<Transform> rawChildren = new List<Transform>();
            for (int i = 0; i < cardContainer.childCount; i++)
            {
                Transform child = cardContainer.GetChild(i);
                if (child != scrollContent)
                {
                    rawChildren.Add(child);
                }
            }

            foreach (var child in rawChildren)
            {
                child.SetParent(scrollContent, false);
            }

            for (int i = 0; i < skinToggles.Count; i++)
            {
                Toggle toggle = skinToggles[i];
                if (toggle != null && toggle.transform != null)
                {
                    toggle.transform.SetParent(scrollContent, false);
                    toggle.transform.SetAsLastSibling();

                    LayoutElement le = toggle.GetComponent<LayoutElement>();
                    if (le == null) le = toggle.gameObject.AddComponent<LayoutElement>();

                    le.minWidth = 52f;
                    le.preferredWidth = 52f;
                    le.flexibleWidth = 0f;
                }
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContent);
            LayoutRebuilder.ForceRebuildLayoutImmediate(cardContainer);
        }
    }
}
