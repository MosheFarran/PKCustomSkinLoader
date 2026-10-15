using PrehistoricKingdom;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PKSkinLoader
{
    /// <summary>
    /// Lets other mods add their own sections to the skin panel (F6). Sections show in Animal mode, under the
    /// skin loader's own settings, for the animal that is selected in the park.
    ///
    /// Optional-dependency pattern for other mods: check that the "PK Custom Skin Loader" melon is loaded, then call
    /// <see cref="AddAnimalSection"/> from a method that is only run in that case, so your mod still works without it.
    /// </summary>
    public static class PanelApi
    {
        /// <summary>Raised when the API gains features, so other mods can check what they can use.</summary>
        public const int Version = 3;   // 2: skin sections + nursery preview, 3: Row + RowButton

        internal class Ext { public string Id; public Action<PanelBuilder, Animal> Build; }
        internal static readonly List<Ext> AnimalSections = new List<Ext>();
        internal class SkinExt { public string Id; public Action<PanelBuilder, BaseAnimalData, int> Build; }
        internal static readonly List<SkinExt> SkinSections = new List<SkinExt>();

        /// <summary>
        /// Adds (or replaces, by id) a section under a skin's settings (Browse mode, also what the panel shows while the
        /// nursery is open). Gets the species and the skin's slot number (-1 if unknown, e.g. a switched-off skin).
        /// </summary>
        public static void AddSkinSection(string id, Action<PanelBuilder, BaseAnimalData, int> build)
        {
            if (string.IsNullOrEmpty(id) || build == null) return;
            SkinSections.RemoveAll(x => x.Id == id);
            SkinSections.Add(new SkinExt { Id = id, Build = build });
            SkinModBase.Log.Msg("[Panel] Skin section added by another mod: " + id);
            if (SkinPanel.IsOpen) SkinPanel.RedrawSkin();
        }

        /// <summary>The animal previewed in the nursery right now (null when the nursery is closed).</summary>
        public static VirtualAnimal NurseryPreview
        {
            get
            {
                try
                {
                    var n = SkinApplier.Nursery;
                    return n != null && n.isActiveAndEnabled ? n.CurrentPreviewVirtualAnimalSafe() : null;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Adds (or replaces, by id) a section in Animal mode. <paramref name="build"/> is called every time the panel
        /// redraws the selected animal; use the builder to add titles, text, sliders and buttons.
        /// </summary>
        public static void AddAnimalSection(string id, Action<PanelBuilder, Animal> build)
        {
            if (string.IsNullOrEmpty(id) || build == null) return;
            AnimalSections.RemoveAll(x => x.Id == id);
            AnimalSections.Add(new Ext { Id = id, Build = build });
            SkinModBase.Log.Msg("[Panel] Section added by another mod: " + id);
            if (SkinPanel.IsOpen) SkinPanel.RedrawAnimal();
        }

        public static void RemoveAnimalSection(string id)
        {
            AnimalSections.RemoveAll(x => x.Id == id);
            SkinSections.RemoveAll(x => x.Id == id);
            if (SkinPanel.IsOpen) SkinPanel.RedrawAnimal();
        }

        /// <summary>Redraws the Animal view or the skin details (e.g. after your values changed).</summary>
        public static void Redraw() { if (SkinPanel.IsOpen) { SkinPanel.RedrawAnimal(); SkinPanel.RedrawSkin(); } }

        /// <summary>Shows a short message at the bottom of the panel.</summary>
        public static void Toast(string message, bool warning = false)
        {
            SkinPanel.ExtToast(message, warning ? Theme.Amber : Theme.GreenLight);
        }

        /// <summary>Re-applies an animal's visuals the way the skin loader does (keeps custom skins and looks).</summary>
        public static void RefreshAnimal(IAnimal animal)
        {
            if (animal != null) SkinApplier.Refresh(animal);
        }

        /// <summary>True while the panel is open.</summary>
        public static bool IsOpen { get { return SkinPanel.IsOpen; } }
    }

    /// <summary>Adds controls to a panel section, in the skin panel's own style.</summary>
    public sealed class PanelBuilder
    {
        private readonly Transform _c;
        internal PanelBuilder(Transform c) { _c = c; }

        /// <summary>A section title with a divider line and an optional grey hint below it.</summary>
        public void Section(string title, string hint = null)
        {
            var gap = UI.Rect("Gap", _c); UI.Size(gap, 4);
            var line = UI.Box(_c, "Line", new Color(1, 1, 1, 0.08f), null); UI.Size(line, 2);
            UI.Para(_c, title, 18, Theme.Text, true);
            if (!string.IsNullOrEmpty(hint)) UI.Para(_c, hint, 14, Theme.TextMuted);
        }

        /// <summary>A paragraph (supports TextMeshPro rich text such as &lt;color=#f0b75a&gt;).</summary>
        public void Text(string text, bool muted = false, float size = 15)
        {
            UI.Para(_c, text, size, muted ? Theme.TextMuted : Theme.TextDim);
        }

        /// <summary>
        /// A labelled slider. <paramref name="onDrag"/> runs while dragging (keep it cheap), <paramref name="onRelease"/>
        /// when the mouse is let go. <paramref name="label"/> turns the slider value into the text on the handle.
        /// </summary>
        public Slider Slider(string name, float min, float max, float value, Func<float, string> label,
                             Action<float> onDrag, Action<float> onRelease)
        {
            var row = UI.Rect("Row", _c);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childForceExpandWidth = false; h.childForceExpandHeight = true;
            h.childControlWidth = true; h.childControlHeight = true;
            UI.Size(row, 32);
            var t = UI.Text(row, name, 15, Theme.TextDim);
            UI.Size(t, -1, 130);
            var s = UI.Slider(row, min, max, value, "0.00", Safe(onDrag), Safe(onRelease), label);
            s.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1;
            return s;
        }

        /// <summary>A full-width button.</summary>
        public Button Button(string label, Action onClick, bool danger = false)
        {
            var b = UI.Button(_c, label, danger ? Theme.Red : Theme.Button, Safe(onClick), 14);
            UI.Size(b, 32);
            return b;
        }

        /// <summary>Buttons side by side; <paramref name="onClick"/> gets the index of the clicked one.</summary>
        public void Buttons(string[] labels, Action<int> onClick)
        {
            var row = UI.Rect("Row", _c);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childForceExpandWidth = true; h.childForceExpandHeight = true;
            h.childControlWidth = true; h.childControlHeight = true;
            UI.Size(row, 32);
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                UI.Button(row, labels[i], Theme.Button, () => { try { onClick(idx); } catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); } }, 14);
            }
        }

        /// <summary>A row of options where the selected one is blue.</summary>
        public void Choice(string[] labels, int selected, Action<int> onPick)
        {
            UI.Segmented(_c, labels, selected, i => { try { onPick(i); } catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); } }, 32);
        }

        /// <summary>
        /// A row like a slider row: a label on the left (same width as slider labels) and an optional dim text filling
        /// the rest. Add small buttons to its right end with <see cref="RowButton"/>.
        /// </summary>
        public RectTransform Row(string label, string middle = null)
        {
            var row = UI.Rect("Row", _c);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childForceExpandWidth = false; h.childForceExpandHeight = true;
            h.childControlWidth = true; h.childControlHeight = true;
            UI.Size(row, 32);
            var t = UI.Text(row, label ?? "", 15, Theme.TextDim);
            UI.Size(t, -1, 130);
            var m = UI.Text(row, middle ?? "", 13, Theme.TextMuted);
            m.enableWordWrapping = false;
            m.overflowMode = TextOverflowModes.Ellipsis;
            m.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            return row;
        }

        /// <summary>
        /// A small pill button at the right end of a row: a slider (as returned by <see cref="Slider"/>) or a
        /// <see cref="Row"/>. The label supports rich text (e.g. &lt;color=#f0b75a&gt;).
        /// </summary>
        public Button RowButton(Component sliderOrRow, string text, Action onClick, float width = 48)
        {
            if (sliderOrRow == null) return null;
            Transform row = sliderOrRow is Slider ? sliderOrRow.transform.parent : sliderOrRow.transform;
            var b = UI.Button(row, "", Theme.Button, Safe(onClick), 13);
            var img = b.targetGraphic as Image;
            if (img != null) img.sprite = Theme.Pill;
            var t = UI.Text(b.transform, text, 13, Theme.Text, true, TextAlignmentOptions.Center);
            UI.Fill(t.rectTransform, 4, 0, 4, 0);
            t.enableWordWrapping = false;
            var le = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.flexibleWidth = 0;
            return b;
        }

        /// <summary>Empty space.</summary>
        public void Gap(float height = 6) { UI.Size(UI.Rect("Gap", _c), height); }

        private static Action<float> Safe(Action<float> a)
        {
            if (a == null) return null;
            return v => { try { a(v); } catch (Exception ex) { SkinModBase.ErrorOnce("ext-slider", "[Panel] " + ex); } };
        }

        private static Action Safe(Action a)
        {
            if (a == null) return null;
            return () => { try { a(); } catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); } };
        }
    }
}
