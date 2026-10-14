using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PKSkinLoader
{
    /// <summary>Colours and shapes taken from the game's own panels (nursery "Edit Skin").</summary>
    internal static class Theme
    {
        public static readonly Color Header = Hex("1c2733");
        public static readonly Color Body = Hex("3a3f47");
        public static readonly Color Inset = Hex("2e3239");
        public static readonly Color Row = Hex("33373e");
        public static readonly Color Track = Hex("1f2227");
        public static readonly Color Green = Hex("5aa832");
        public static readonly Color GreenLight = Hex("79c94a");
        public static readonly Color Blue = Hex("3a98e3");
        public static readonly Color Red = Hex("a23b3b");
        public static readonly Color Amber = Hex("d99a2b");
        public static readonly Color Button = Hex("4a5059");
        public static readonly Color Text = Color.white;
        public static readonly Color TextDim = Hex("b9bec5");
        public static readonly Color TextMuted = Hex("8b9199");
        public static readonly Color Shade = new Color(0f, 0f, 0f, 0.35f);

        public static Color Hex(string h)
        {
            Color c;
            ColorUtility.TryParseHtmlString("#" + h, out c);
            return c;
        }

        private static Sprite _round, _pill;
        public static Sprite Round { get { return _round ?? (_round = MakeRounded(64, 12, "PKSkin round")); } }
        public static Sprite Pill { get { return _pill ?? (_pill = MakeRounded(64, 31, "PKSkin pill")); } }

        /// <summary>A white rounded rectangle for 9-slicing (anti-aliased edges).</summary>
        private static Sprite MakeRounded(int size, int radius, string name)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.name = name;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            float b = Mathf.Min(radius + 1, size / 2 - 1);
            return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }
    }

    /// <summary>Small helpers to build Unity UI from code.</summary>
    internal static class UI
    {
        public static TMP_FontAsset Font;

        /// <summary>The font most of the game's own labels use.</summary>
        public static void FindGameFont()
        {
            if (Font != null) return;
            try
            {
                var counts = new Dictionary<TMP_FontAsset, int>();
                foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                {
                    if (t == null || t.font == null) continue;
                    int n; counts.TryGetValue(t.font, out n); counts[t.font] = n + 1;
                }
                if (counts.Count > 0) Font = counts.OrderByDescending(kv => kv.Value).First().Key;
            }
            catch { }
            if (Font == null) Font = TMP_Settings.defaultFontAsset;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            var rt = (RectTransform)go.transform;
            if (parent != null) rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Top-left anchored box at (x, y) with size (w, h).</summary>
        public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        /// <summary>Stretches to the parent with the given margins.</summary>
        public static RectTransform Fill(RectTransform rt, float left, float top, float right, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        public static Image Box(Transform parent, string name, Color c, Sprite shape)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            if (shape != null) { img.sprite = shape; img.type = Image.Type.Sliced; }
            return img;
        }

        public static LayoutElement Size(Component c, float height, float width = -1)
        {
            var le = c.gameObject.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
            if (height >= 0) { le.preferredHeight = height; le.minHeight = height; }
            if (width >= 0) { le.preferredWidth = width; le.minWidth = width; }
            return le;
        }

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color c, bool bold = false,
                                           TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var rt = Rect("Text", parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = c;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.alignment = align;
            t.richText = true;
            t.raycastTarget = false;
            t.enableWordWrapping = true;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        /// <summary>A text line inside a vertical layout (height grows with wrapped text).</summary>
        public static TextMeshProUGUI Para(Transform parent, string text, float size, Color c, bool bold = false)
        {
            var t = Text(parent, text, size, c, bold, TextAlignmentOptions.TopLeft);
            t.overflowMode = TextOverflowModes.Overflow;   // height comes from the parent layout (text's preferred height)
            return t;
        }

        public static void Tint(Button b, Color bg)
        {
            var img = b.targetGraphic as Image;
            if (img != null) img.color = bg;
        }

        public static Button Button(Transform parent, string label, Color bg, Action onClick, float fontSize = 17)
        {
            var img = Box(parent, "Button " + label, bg, Theme.Round);
            var b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var cb = b.colors;
            cb.normalColor = new Color(0.9f, 0.9f, 0.9f);
            cb.highlightedColor = Color.white;
            cb.pressedColor = new Color(0.72f, 0.72f, 0.72f);
            cb.selectedColor = cb.normalColor;
            cb.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
            if (onClick != null) b.onClick.AddListener(() =>
            {
                try { onClick(); }
                catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); }
            });
            if (!string.IsNullOrEmpty(label))
            {
                var t = Text(img.transform, label, fontSize, Theme.Text, true, TextAlignmentOptions.Center);
                Fill(t.rectTransform, 8, 0, 8, 0);
                t.enableWordWrapping = false;
            }
            return b;
        }

        public static void SetLabel(Button b, string label)
        {
            var t = b.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) t.text = label;
        }

        /// <summary>Row of buttons where one is selected (blue). Returns the row.</summary>
        public static RectTransform Segmented(Transform parent, string[] labels, int selected, Action<int> onPick, float height = 34)
        {
            var row = Rect("Segmented", parent);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6; h.childForceExpandWidth = true; h.childForceExpandHeight = true;
            h.childControlWidth = true; h.childControlHeight = true;
            Size(row, height);
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var b = Button(row, labels[i], i == selected ? Theme.Blue : Theme.Button, () => { if (onPick != null) onPick(idx); }, 15);
                if (onPick == null) b.interactable = false;
            }
            return row;
        }

        /// <summary>Game-style slider: dark track, white fill, pill handle showing the value.</summary>
        public static Slider Slider(Transform parent, float min, float max, float value, string format,
                                    Action<float> onDrag, Action<float> onRelease)
        {
            var root = Rect("Slider", parent);
            Size(root, 30);

            var bg = Box(root, "Track", Theme.Track, Theme.Pill);
            var bgRt = bg.rectTransform;
            bgRt.anchorMin = new Vector2(0, 0.5f); bgRt.anchorMax = new Vector2(1, 0.5f);
            bgRt.sizeDelta = new Vector2(0, 8); bgRt.anchoredPosition = Vector2.zero;

            // The fill ends under the handle's centre; the handle (60 px) travels between 30 px from each end.
            var fillArea = Rect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0, 0.5f); fillArea.anchorMax = new Vector2(1, 0.5f);
            fillArea.offsetMin = new Vector2(30, -4); fillArea.offsetMax = new Vector2(-30, 4);
            var fill = Box(fillArea, "Fill", new Color(0.92f, 0.93f, 0.95f), Theme.Pill);
            fill.rectTransform.offsetMin = new Vector2(-30, 0); fill.rectTransform.offsetMax = Vector2.zero;

            var handleArea = Rect("Handle Area", root);
            Fill(handleArea, 30, 0, 30, 0);
            var handle = Box(handleArea, "Handle", Theme.Header, Theme.Pill);
            handle.rectTransform.sizeDelta = new Vector2(60, -6);
            var label = Text(handle.transform, "", 13, Theme.Text, true, TextAlignmentOptions.Center);
            Fill(label.rectTransform, 2, 0, 2, 0);
            label.enableWordWrapping = false;

            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = min; s.maxValue = max;
            var nav = s.navigation; nav.mode = Navigation.Mode.None; s.navigation = nav;
            s.SetValueWithoutNotify(value);
            label.text = value.ToString(format);
            s.onValueChanged.AddListener(v =>
            {
                label.text = v.ToString(format);
                if (onDrag != null) onDrag(v);
            });

            // Only save when the mouse is released (saving on every step would reload the skin 60x a second).
            var trigger = root.gameObject.AddComponent<EventTrigger>();
            var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            up.callback.AddListener(_ =>
            {
                try { if (onRelease != null) onRelease(s.value); }
                catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); }
            });
            trigger.triggers.Add(up);
            return s;
        }

        public static TMP_InputField Input(Transform parent, string placeholder, string value, Action<string> onChange)
        {
            var bg = Box(parent, "Input", Theme.Track, Theme.Round);
            bg.gameObject.SetActive(false);   // set everything up before the field wakes up
            Size(bg, 36);

            var area = Rect("Text Area", bg.transform);
            Fill(area, 12, 4, 12, 4);
            area.gameObject.AddComponent<RectMask2D>();

            var ph = Text(area, placeholder, 16, Theme.TextMuted);
            ph.fontStyle = FontStyles.Italic;
            Fill(ph.rectTransform, 0, 0, 0, 0);
            ph.enableWordWrapping = false;

            var txt = Text(area, "", 16, Theme.Text);
            Fill(txt.rectTransform, 0, 0, 0, 0);
            txt.enableWordWrapping = false;
            txt.overflowMode = TextOverflowModes.Overflow;

            var f = bg.gameObject.AddComponent<TMP_InputField>();
            f.textViewport = area;
            f.textComponent = txt;
            f.placeholder = ph;
            if (Font != null) f.fontAsset = Font;
            f.pointSize = 16;
            f.lineType = TMP_InputField.LineType.SingleLine;
            f.characterLimit = 60;
            f.targetGraphic = bg;
            var nav = f.navigation; nav.mode = Navigation.Mode.None; f.navigation = nav;
            f.text = value ?? "";
            if (onChange != null) f.onValueChanged.AddListener(v => onChange(v));
            bg.gameObject.SetActive(true);
            return f;
        }

        /// <summary>A vertically scrolling list. Returns the content to add children to.</summary>
        public static RectTransform Scroll(RectTransform area, float spacing, RectOffset padding, out ScrollRect sr)
        {
            var hit = area.gameObject.GetComponent<Image>() ?? area.gameObject.AddComponent<Image>();
            if (hit.sprite == null) hit.color = new Color(0, 0, 0, 0);   // invisible, but catches the mouse wheel

            var viewport = Rect("Viewport", area);
            Fill(viewport, 0, 0, 0, 0);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = Vector2.zero; content.anchoredPosition = Vector2.zero;
            var v = content.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing; v.padding = padding;
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            var fit = content.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr = area.gameObject.GetComponent<ScrollRect>() ?? area.gameObject.AddComponent<ScrollRect>();
            sr.viewport = viewport;
            sr.content = content;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 45;
            sr.inertia = false;
            return content;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                var c = t.GetChild(i).gameObject;
                c.SetActive(false);
                UnityEngine.Object.Destroy(c);
            }
        }

        /// <summary>Thumbnail image filling its box (keeps proportions, crops nothing).</summary>
        public static Image Picture(Transform parent, Sprite sprite, Color bg)
        {
            var frame = Box(parent, "Picture", bg, Theme.Round);
            if (sprite != null)
            {
                var img = Box(frame.transform, "Image", Color.white, null);
                img.sprite = sprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                Fill(img.rectTransform, 4, 4, 4, 4);
            }
            return frame;
        }
    }
}
