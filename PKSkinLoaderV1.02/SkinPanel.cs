using MelonLoader;
using Newtonsoft.Json.Linq;
using PrehistoricKingdom;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PKSkinLoader
{
    /// <summary>
    /// The in-game skin panel (F6): browse custom skins, change their settings live, switch them on/off,
    /// and create new skins from any species' vanilla skin. Built with the game's own UI system and font.
    /// </summary>
    internal static class SkinPanel
    {
        private const float W = 1280, H = 780, Top = 72, BodyH = 660;
        private const float LeftX = 12, LeftW = 236, MidX = 258, MidW = 502, RightX = 770, RightW = 498;

        private enum Mode { Browse, Wizard, Problems, Animal }

        private static GameObject _canvasGo;
        private static RectTransform _window, _left, _mid, _right;
        private static TextMeshProUGUI _status, _toast;
        private static Button _problemsBtn, _newBtn, _reloadBtn, _compactBtn, _closeBtn, _animalBtn;
        private static Animal _animal;
        private static float _animalPollAt;
        private static RectTransform _head, _headBottom, _accent, _nav, _grip;
        private static TextMeshProUGUI _title;
        private static Canvas _canvas;

        private static Mode _mode = Mode.Browse;
        private static List<BaseAnimalData> _species = new List<BaseAnimalData>();
        private static List<PanelEntry> _entries = new List<PanelEntry>();
        private static BaseAnimalData _selSpecies;   // null = all species
        private static string _selId;
        private static readonly Dictionary<SkinSource, Sprite> _cards = new Dictionary<SkinSource, Sprite>();

        // new-skin wizard
        private static BaseAnimalData _wzSpecies;
        private static int _wzBase = -1;
        private static string _wzName = "", _wzPack = "", _wzAuthor, _wzSearch = "";
        private static RectTransform _wzList;

        private static bool _busy;
        private static float _reloadAt = -1, _toastUntil;
        private static ScrollRect _rightScroll;
        private static readonly Dictionary<RectTransform, string> _scrollKey = new Dictionary<RectTransform, string>();
        private static readonly Dictionary<RectTransform, float> _scrollRestore = new Dictionary<RectTransform, float>();
        private static string _wzDiffuse;
        private const float CompactW = RightW + 24;

        public static bool IsOpen { get { return _canvasGo != null && _canvasGo.activeSelf; } }

        // ================================================================== lifecycle

        public static void OnUpdate()
        {
            try
            {
                if (Config.PanelKey != null && Input.GetKeyDown(Config.PanelKey.Value) && !Typing()) Toggle();
                if (!IsOpen) return;

                if (_reloadAt > 0 && Time.unscaledTime >= _reloadAt) { _reloadAt = -1; SkinModBase.Reload(); }
                if (_toastUntil > 0 && Time.unscaledTime > _toastUntil && _toast != null) { _toast.text = ""; _toastUntil = 0; }
                ReleaseButtonFocus();
                if (_mode == Mode.Animal && Time.unscaledTime >= _animalPollAt)
                {
                    _animalPollAt = Time.unscaledTime + 0.3f;
                    var sel = SelectedAnimal();
                    if (!ReferenceEquals(sel, _animal)) { _animal = sel; RenderAnimal(); if (Config.PanelCompact.Value) RenderAnimalNav(); }
                }
                if (_scrollRestore.Count > 0)
                {
                    foreach (var kv in _scrollRestore)
                    {
                        var sr = kv.Key != null ? kv.Key.GetComponent<ScrollRect>() : null;
                        if (sr == null || sr.content == null) continue;
                        LayoutRebuilder.ForceRebuildLayoutImmediate(sr.content);
                        var p = sr.content.anchoredPosition;
                        sr.content.anchoredPosition = new Vector2(p.x, kv.Value);
                    }
                    _scrollRestore.Clear();
                }
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("panel-update", "[Panel] " + ex); }
        }

        public static void OnReloaded()
        {
            foreach (var s in _cards.Values) if (s != null) UnityEngine.Object.Destroy(s);
            _cards.Clear();
            if (IsOpen && !_busy) Refresh();
        }

        private static void Toggle()
        {
            if (IsOpen) { _canvasGo.SetActive(false); return; }
            if (_canvasGo == null) Build();
            _canvasGo.SetActive(true);
            try
            {
                var n = SkinApplier.Nursery;
                var va = n != null && n.isActiveAndEnabled ? n.CurrentPreviewVirtualAnimalSafe() : null;
                if (va != null && va.OwnAD != null) { _species = SkinInjector.AllSpecies(); FollowNursery(va.OwnAD, va.skinIdx); return; }
            }
            catch { }
            Refresh();
        }

        /// <summary>Select the skin of the animal previewed in the nursery (so you edit what you're looking at).</summary>
        public static void FollowNursery(BaseAnimalData sp, int idx)
        {
            if (!IsOpen || sp == null || _busy || _mode == Mode.Wizard) return;
            try
            {
                if (_species.Count == 0) _species = SkinInjector.AllSpecies();
                var match = _species.FirstOrDefault(x => x.name == sp.name) ?? sp;
                var def = SkinInjector.DefFor(sp, idx);
                string id = def != null ? sp.name + "|" + def.Source.Location : VId(match, idx);
                if (_selId == id && _selSpecies != null && _selSpecies.name == sp.name) return;
                _selSpecies = match;
                _selId = id;
                _mode = Mode.Browse;
                Refresh();
            }
            catch (Exception ex) { SkinModBase.ErrorOnce("panel-follow", "[Panel] " + ex.Message); }
        }

        /// <summary>The game ignores keys while a UI element is selected; buttons shouldn't keep that focus.</summary>
        private static void ReleaseButtonFocus()
        {
            var es = EventSystem.current;
            if (es == null || es.currentSelectedGameObject == null) return;
            var go = es.currentSelectedGameObject;
            if (go.transform.IsChildOf(_canvasGo.transform) && go.GetComponent<TMP_InputField>() == null)
                es.SetSelectedGameObject(null);
        }

        private static bool Typing()
        {
            var es = EventSystem.current;
            if (es == null || es.currentSelectedGameObject == null) return false;
            var f = es.currentSelectedGameObject.GetComponent<TMP_InputField>();
            return f != null && f.isFocused;
        }

        private static void Toast(string msg, Color c, float seconds = 4f)
        {
            if (_toast == null) return;
            _toast.text = msg;
            _toast.color = c;
            _toastUntil = Time.unscaledTime + seconds;
        }

        private static void Saved(string error)
        {
            if (error != null) { Toast(error, Theme.Amber, 7f); return; }
            Toast("Saved - updating your animals...", Theme.GreenLight, 2.5f);
            _reloadAt = Time.unscaledTime + 0.15f;
        }

        /// <summary>
        /// Call before rebuilding a scrolling area: if it shows the same thing as before (same key),
        /// its scroll position is put back after the rebuild instead of jumping to the top.
        /// </summary>
        private static void KeepScroll(RectTransform region, string key)
        {
            var sr = region.GetComponent<ScrollRect>();
            string old;
            if (sr != null && sr.content != null && _scrollKey.TryGetValue(region, out old) && old == key)
                _scrollRestore[region] = sr.content.anchoredPosition.y;
            else _scrollRestore.Remove(region);
            _scrollKey[region] = key;
        }

        // ================================================================== frame

        private static void Build()
        {
            UI.FindGameFont();

            _canvasGo = new GameObject("PKSkinPanel", typeof(RectTransform));
            _canvasGo.layer = 5;
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            // soft shadow + window
            var shadow = UI.Box(_canvasGo.transform, "Shadow", new Color(0, 0, 0, 0.45f), Theme.Round);
            var win = UI.Box(_canvasGo.transform, "Window", Theme.Body, Theme.Round);
            _window = win.rectTransform;
            _canvas = canvas;
            _window.anchorMin = _window.anchorMax = new Vector2(0.5f, 0.5f);
            _window.pivot = new Vector2(0, 1);   // top-left: resizing keeps the title bar where it is
            _window.sizeDelta = new Vector2(W, H);
            float sc = Mathf.Clamp(Config.PanelScale.Value, 0.5f, 1.3f);
            _window.localScale = new Vector3(sc, sc, 1);
            _window.anchoredPosition = new Vector2(-W * sc / 2, H * sc / 2);
            shadow.rectTransform.SetParent(_window, false);
            UI.Fill(shadow.rectTransform, -6, -4, -6, -10);
            shadow.transform.SetAsFirstSibling();
            shadow.raycastTarget = false;

            // header (rounded top, square bottom) - drag it to move the window
            var head = UI.Box(_window, "Header", Theme.Header, Theme.Round);
            _head = head.rectTransform;
            var headBottom = UI.Box(head.transform, "HeaderBottom", Theme.Header, null);
            _headBottom = headBottom.rectTransform;
            headBottom.raycastTarget = false;
            var accent = UI.Box(_window, "Accent", Theme.Green, null);
            _accent = accent.rectTransform;
            var drag = head.gameObject.AddComponent<EventTrigger>();
            var e = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            e.callback.AddListener(d =>
            {
                var pd = d as PointerEventData;
                if (pd != null) _window.anchoredPosition += pd.delta / Mathf.Max(0.01f, canvas.scaleFactor);
            });
            drag.triggers.Add(e);

            _title = UI.Text(head.transform, "", 24, Theme.Text, true);
            UI.Place(_title.rectTransform, 22, 0, 600, 60);
            _title.enableWordWrapping = false;

            _closeBtn = UI.Button(head.transform, "X", Theme.Red, () => _canvasGo.SetActive(false), 18);
            _animalBtn = UI.Button(head.transform, "Animal", Theme.Button, () =>
            {
                _mode = _mode == Mode.Animal ? Mode.Browse : Mode.Animal;
                _animal = SelectedAnimal();
                Render();
            }, 16);
            _problemsBtn = UI.Button(head.transform, "Problems", Theme.Button, () => Show(_mode == Mode.Problems ? Mode.Browse : Mode.Problems), 16);
            _newBtn = UI.Button(head.transform, "+ New skin", Theme.Green, () => Show(_mode == Mode.Wizard ? Mode.Browse : Mode.Wizard), 16);
            _reloadBtn = UI.Button(head.transform, "Reload (" + Config.ReloadKey.Value + ")", Theme.Button, () => SkinModBase.Reload(), 16);
            _compactBtn = UI.Button(head.transform, "Compact", Theme.Button, () =>
            {
                Config.PanelCompact.Value = !Config.PanelCompact.Value;
                if (Config.PanelCompact.Value && _mode != Mode.Animal) _mode = Mode.Browse;
                MelonPreferences.Save();
                Render();
            }, 16);

            _left = UI.Box(_window, "Left", Theme.Inset, Theme.Round).rectTransform;
            _mid = UI.Box(_window, "Middle", Theme.Inset, Theme.Round).rectTransform;
            _right = UI.Box(_window, "Right", Theme.Inset, Theme.Round).rectTransform;
            _nav = UI.Box(_window, "Nav", Theme.Inset, Theme.Round).rectTransform;

            // resize grip (bottom-right): drag to make the whole panel smaller or bigger
            var grip = UI.Box(_window, "Resize", Theme.Button, Theme.Round);
            _grip = grip.rectTransform;
            var gl = UI.Text(grip.transform, "=", 22, Theme.TextDim, true, TextAlignmentOptions.Center);
            UI.Fill(gl.rectTransform, 0, 0, 0, 0);
            gl.rectTransform.localRotation = Quaternion.Euler(0, 0, -45);
            var gt = grip.gameObject.AddComponent<EventTrigger>();
            var ge = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
            ge.callback.AddListener(d =>
            {
                var pd = d as PointerEventData;
                if (pd == null) return;
                float s = _window.localScale.x;
                float wNow = (Config.PanelCompact.Value ? CompactW : W);
                float grow = (pd.delta.x - pd.delta.y) / Mathf.Max(0.01f, _canvas.scaleFactor) / (wNow + H);
                s = Mathf.Clamp(s * (1f + grow), 0.5f, 1.3f);
                _window.localScale = new Vector3(s, s, 1);
            });
            gt.triggers.Add(ge);
            var gu = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            gu.callback.AddListener(d => { Config.PanelScale.Value = (float)Math.Round(_window.localScale.x, 2); MelonPreferences.Save(); });
            gt.triggers.Add(gu);

            _status = UI.Text(_window, "", 14, Theme.TextMuted);
            _status.enableWordWrapping = false;
            _toast = UI.Text(_window, "", 15, Theme.GreenLight, true, TextAlignmentOptions.MidlineRight);
            _toast.enableWordWrapping = false;
            LayoutFrame();
        }

        /// <summary>Positions the frame for full or compact view.</summary>
        private static void LayoutFrame()
        {
            bool compact = Config.PanelCompact.Value;
            float w = compact ? CompactW : W;
            _window.sizeDelta = new Vector2(w, H);
            UI.Place(_head, 0, 0, w, 60);
            UI.Place(_headBottom, 0, 30, w, 30);
            UI.Place(_accent, 0, 60, w, 3);
            _title.text = compact ? "Skins" : "Custom Skins  <size=15><color=#8b9199>PK Custom Skin Loader " + SkinVersion() + "</color></size>";

            float x = w - 16 - 44;
            UI.Place((RectTransform)_closeBtn.transform, x, 10, 44, 40);
            _problemsBtn.gameObject.SetActive(!compact);
            _newBtn.gameObject.SetActive(!compact);
            _reloadBtn.gameObject.SetActive(!compact);
            if (compact)
            {
                x -= 140;
                UI.Place((RectTransform)_compactBtn.transform, x, 10, 130, 40);
                UI.SetLabel(_compactBtn, "Full view");
                x -= 130; UI.Place((RectTransform)_animalBtn.transform, x, 10, 120, 40);
            }
            else
            {
                x -= 160; UI.Place((RectTransform)_problemsBtn.transform, x, 10, 150, 40);
                x -= 160; UI.Place((RectTransform)_newBtn.transform, x, 10, 150, 40);
                x -= 140; UI.Place((RectTransform)_reloadBtn.transform, x, 10, 130, 40);
                x -= 130; UI.Place((RectTransform)_compactBtn.transform, x, 10, 120, 40);
                UI.SetLabel(_compactBtn, "Compact");
                x -= 130; UI.Place((RectTransform)_animalBtn.transform, x, 10, 120, 40);
            }

            _left.gameObject.SetActive(!compact);
            _mid.gameObject.SetActive(!compact);
            _nav.gameObject.SetActive(compact);
            if (compact)
            {
                UI.Place(_nav, 12, Top, RightW, 44);
                UI.Place(_right, 12, Top + 52, RightW, BodyH - 52);
                _right.gameObject.SetActive(true);
                _status.gameObject.SetActive(false);
                UI.Place(_toast.rectTransform, 18, Top + BodyH + 8, w - 36 - 40, 34);
            }
            else
            {
                bool wide = _mode == Mode.Problems;
                UI.Place(_left, LeftX, Top, LeftW, BodyH);
                UI.Place(_mid, MidX, Top, wide ? W - MidX - 12 : MidW, BodyH);
                UI.Place(_right, RightX, Top, RightW, BodyH);
                _right.gameObject.SetActive(!wide);
                _status.gameObject.SetActive(true);
                UI.Place(_status.rectTransform, 18, Top + BodyH + 8, 700, 34);
                UI.Place(_toast.rectTransform, 560, Top + BodyH + 8, W - 560 - 18 - 40, 34);
            }
            UI.Place(_grip, w - 34, H - 34, 26, 26);
        }

        /// <summary>Compact view: arrows to step through the skins without the list.</summary>
        private class NavItem { public string Id, Name; public BaseAnimalData Species; }

        private static string VId(BaseAnimalData sp, int idx) { return "V|" + sp.name + "|" + idx; }

        private static bool ParseV(string id, out BaseAnimalData sp, out int idx)
        {
            sp = null; idx = -1;
            if (id == null || !id.StartsWith("V|")) return false;
            var parts = id.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[2], out idx)) return false;
            string n = parts[1];
            sp = _species.FirstOrDefault(x => x.name == n);
            return sp != null && idx >= 0 && idx < sp.skinsData.Count;
        }

        /// <summary>Custom skins in view, then (with a species picked) its vanilla skins.</summary>
        private static List<NavItem> NavItems()
        {
            var r = Visible.OrderBy(x => SkinFiles.SpeciesLabel(x.Species)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new NavItem { Id = x.Id, Name = x.Name, Species = x.Species }).ToList();
            if (_selSpecies != null)
                foreach (var kv in SkinFiles.VanillaSkins(_selSpecies))
                    r.Add(new NavItem { Id = VId(_selSpecies, kv.Key), Name = kv.Value.skinName + " (vanilla)", Species = _selSpecies });
            return r;
        }

        private static void RenderNav()
        {
            UI.Clear(_nav);
            var list = NavItems();
            if (list.Count == 0) return;
            if (_selId == null || !list.Any(x => x.Id == _selId)) _selId = list[0].Id;
            int i = list.FindIndex(x => x.Id == _selId);
            Action<int> go = d => { _selId = list[(i + d + list.Count) % list.Count].Id; RenderNav(); RenderDetails(); };
            var prev = UI.Button(_nav, "<", Theme.Button, () => go(-1), 20);
            UI.Place((RectTransform)prev.transform, 4, 4, 44, 36);
            var next = UI.Button(_nav, ">", Theme.Button, () => go(1), 20);
            UI.Place((RectTransform)next.transform, RightW - 48, 4, 44, 36);
            var e = list[i];
            var t = UI.Text(_nav, e.Name + "  <size=14><color=#8b9199>" + SkinFiles.SpeciesLabel(e.Species) + "  " + (i + 1) + "/" + list.Count + "</color></size>",
                18, Theme.Text, true, TextAlignmentOptions.Center);
            UI.Place(t.rectTransform, 56, 0, RightW - 112, 44);
            t.enableWordWrapping = false;
        }

        private static string SkinVersion()
        {
            try { return typeof(SkinPanel).Assembly.GetCustomAttributes(typeof(MelonInfoAttribute), false).Cast<MelonInfoAttribute>().First().Version; }
            catch { return ""; }
        }

        private static void Show(Mode m)
        {
            _mode = m;
            if (m == Mode.Wizard && _wzSpecies == null) _wzSpecies = _selSpecies ?? (_entries.Count > 0 ? _entries[0].Species : null);
            Render();
        }

        // ================================================================== data

        private static void Refresh()
        {
            try
            {
                _species = SkinInjector.AllSpecies()
                    .GroupBy(s => s.name).Select(g => g.First())
                    .OrderBy(s => SkinFiles.SpeciesLabel(s), StringComparer.OrdinalIgnoreCase).ToList();
                _entries = SkinFiles.BuildEntries(_species);
                if (_selSpecies != null) _selSpecies = _species.FirstOrDefault(s => s.name == _selSpecies.name);
                if (_wzSpecies != null) _wzSpecies = _species.FirstOrDefault(s => s.name == _wzSpecies.name);
            }
            catch (Exception ex) { SkinModBase.Log.Error("[Panel] Could not read skins: " + ex); }
            Render();
        }

        private static PanelEntry Selected
        {
            get { return _selId == null ? null : _entries.FirstOrDefault(x => x.Id == _selId); }
        }

        private static IEnumerable<PanelEntry> Visible
        {
            get { return _entries.Where(x => _selSpecies == null || x.Species == _selSpecies); }
        }

        private static Sprite CardOf(PanelEntry e)
        {
            Sprite s;
            if (_cards.TryGetValue(e.Src, out s)) return s;
            try { s = TextureStore.Thumbnail(e.Def); } catch { s = null; }
            _cards[e.Src] = s;
            return s;
        }

        private static List<string> Problems()
        {
            var r = new List<string>();
            foreach (var f in Compat.Failed) r.Add("Game update: the '" + f + "' hook could not be applied (that feature is off).");
            r.AddRange(SkinScanner.Warnings);
            r.AddRange(TextureStore.Problems);
            return r.Distinct().ToList();
        }

        // ================================================================== rendering

        private static void Render()
        {
            if (_canvasGo == null) return;
            try
            {
                int problems = Problems().Count;
                UI.SetLabel(_problemsBtn, _mode == Mode.Problems ? "Back to skins" : problems > 0 ? "Problems (" + problems + ")" : "No problems");
                UI.Tint(_problemsBtn, _mode != Mode.Problems && problems > 0 ? Theme.Amber : Theme.Button);
                UI.SetLabel(_newBtn, _mode == Mode.Wizard ? "Back to skins" : "+ New skin");

                int texCount;
                long bytes = TextureStore.MemoryBytes(out texCount);
                _status.text = _entries.Count(x => !x.Src.Disabled) + " custom skin(s)  |  ~" + (bytes / (1024 * 1024)) + " MB video memory  |  "
                    + Config.ReloadKey.Value + " reload  |  " + Config.PanelKey.Value + " close  |  drag the title bar to move";

                LayoutFrame();

                if (_species.Count == 0)
                {
                    UI.Clear(_left); UI.Clear(_mid); UI.Clear(_right);
                    ScrollRect sr;
                    var c = UI.Scroll(_mid, 8, new RectOffset(18, 18, 18, 18), out sr);
                    UI.Para(c, "Load a park first", 22, Theme.Text, true);
                    UI.Para(c, "The skin panel needs the game's animal list, which loads with a park.", 16, Theme.TextDim);
                    return;
                }

                UI.Tint(_animalBtn, _mode == Mode.Animal ? Theme.Blue : Theme.Button);
                if (Config.PanelCompact.Value)
                {
                    if (_mode == Mode.Animal) { RenderAnimalNav(); RenderAnimal(); }
                    else { RenderNav(); RenderDetails(); }
                    return;
                }
                switch (_mode)
                {
                    case Mode.Browse: RenderSpecies(); RenderGrid(); RenderDetails(); break;
                    case Mode.Wizard: RenderWizardSpecies(); RenderWizardBases(); RenderWizardForm(); break;
                    case Mode.Problems: RenderSpecies(); RenderProblems(); break;
                    case Mode.Animal: RenderSpecies(); RenderAnimalInfo(); RenderAnimal(); break;
                }
            }
            catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); }
        }

        // ------------------------------------------------------------------ left: species with custom skins

        private static void RenderSpecies()
        {
            UI.Clear(_left);
            ScrollRect sr;
            var c = UI.Scroll(_left, 4, new RectOffset(8, 8, 8, 8), out sr);
            SpeciesButton(c, "All species", _entries.Count, _selSpecies == null && _mode == Mode.Browse, () => { _selSpecies = null; _mode = Mode.Browse; Render(); });
            foreach (var sp in ListedSpecies())
            {
                var s = sp;
                int count = _entries.Count(x => x.Species == s);
                SpeciesButton(c, SkinFiles.SpeciesLabel(s), count, _selSpecies == s && _mode == Mode.Browse, () =>
                {
                    _selSpecies = s; _mode = Mode.Browse;
                    var items = NavItems();
                    if (!items.Any(x => x.Id == _selId) && items.Count > 0) _selId = items[0].Id;
                    Render();
                });
            }
        }

        /// <summary>Species with custom skins, animals in the park or nursery, or vanilla tweaks.</summary>
        private static List<BaseAnimalData> ListedSpecies()
        {
            var names = new HashSet<string>(_entries.Select(x => x.Species.name));
            try
            {
                foreach (var a in SkinApplier.Known.ToList())
                {
                    try { if (a.BaseData != null) names.Add(a.BaseData.name); } catch { }
                }
            }
            catch { }
            try
            {
                var va = SkinApplier.Nursery != null ? SkinApplier.Nursery.CurrentPreviewVirtualAnimalSafe() : null;
                if (va != null && va.OwnAD != null) names.Add(va.OwnAD.name);
            }
            catch { }
            foreach (var sp in _species)
            {
                foreach (var kv in SkinFiles.VanillaSkins(sp))
                    if (!VanillaTweaks.Get(sp, kv.Value).Empty) { names.Add(sp.name); break; }
            }
            if (_selSpecies != null) names.Add(_selSpecies.name);
            return _species.Where(x => names.Contains(x.name)).ToList();
        }

        private static void SpeciesButton(Transform parent, string label, int count, bool selected, Action click)
        {
            var b = UI.Button(parent, "", selected ? Theme.Blue : Theme.Row, click);
            UI.Size(b, 40);
            var name = UI.Text(b.transform, label, 16, Theme.Text, selected);
            UI.Fill(name.rectTransform, 12, 0, 40, 0);
            name.enableWordWrapping = false;
            var n = UI.Text(b.transform, count.ToString(), 15, selected ? Theme.Text : Theme.TextMuted, true, TextAlignmentOptions.MidlineRight);
            UI.Fill(n.rectTransform, 0, 0, 12, 0);
        }

        // ------------------------------------------------------------------ middle: skin cards

        private static void RenderGrid()
        {
            KeepScroll(_mid, "grid|" + (_selSpecies != null ? _selSpecies.name : "*"));
            UI.Clear(_mid);
            ScrollRect sr;
            var c = UI.Scroll(_mid, 10, new RectOffset(14, 14, 12, 14), out sr);
            var list = Visible.OrderBy(x => SkinFiles.SpeciesLabel(x.Species)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

            UI.Para(c, (_selSpecies == null ? "All species" : SkinFiles.SpeciesLabel(_selSpecies)) + "  <size=15><color=#8b9199>" + list.Count + " custom skin(s)</color></size>", 20, Theme.Text, true);
            var nav = NavItems();
            if (nav.Count > 0 && (_selId == null || !nav.Any(x => x.Id == _selId))) _selId = nav[0].Id;
            if (list.Count == 0)
                UI.Para(c, "No custom skins yet. Click \"+ New skin\" to make one.", 16, Theme.TextDim);
            else RenderCustomCards(c, list);

            if (_selSpecies == null)
            {
                UI.Para(c, "Pick a species on the left to change its vanilla skins too.", 14, Theme.TextMuted);
                return;
            }
            var gap = UI.Rect("Gap", c); UI.Size(gap, 6);
            UI.Para(c, "Vanilla skins", 20, Theme.Text, true);
            UI.Para(c, "Change their fur droop and iridescence. Only in your game; the game's files stay untouched.", 14, Theme.TextMuted);
            var vg = UI.Rect("Vanilla", c);
            var g2 = vg.gameObject.AddComponent<GridLayoutGroup>();
            g2.cellSize = new Vector2(148, 196);
            g2.spacing = new Vector2(10, 10);
            g2.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g2.constraintCount = 3;
            foreach (var kv in SkinFiles.VanillaSkins(_selSpecies))
            {
                var sp = _selSpecies; int idx = kv.Key; var skin = kv.Value;
                string id = VId(sp, idx);
                bool sel = id == _selId;
                var b = UI.Button(vg, "", sel ? Theme.Blue : Theme.Row, () => { _selId = id; RenderGrid(); RenderDetails(); });
                var pic = UI.Picture(b.transform, skin.skinThumbnail, Theme.Track);
                UI.Place(pic.rectTransform, 7, 7, 134, 124);
                pic.raycastTarget = false;
                var name = UI.Text(b.transform, skin.skinName, 15, Theme.Text, true, TextAlignmentOptions.TopLeft);
                UI.Place(name.rectTransform, 9, 136, 132, 22);
                name.enableWordWrapping = false;
                bool tweaked = !VanillaTweaks.Get(sp, skin).Empty;
                var s2 = UI.Text(b.transform, tweaked ? "<color=#f0b75a>changed</color>" : "vanilla", 13, sel ? Theme.Text : Theme.TextMuted, tweaked, TextAlignmentOptions.TopLeft);
                UI.Place(s2.rectTransform, 9, 157, 132, 18);
                int wearing = SkinApplier.CountWearing(sp, idx);
                var s3 = UI.Text(b.transform, wearing > 0 ? "<color=#8fe06a>" + wearing + " in park</color>" : "", 13, Theme.TextDim, true, TextAlignmentOptions.TopLeft);
                UI.Place(s3.rectTransform, 9, 175, 132, 18);
            }
        }

        private static void RenderCustomCards(Transform c, List<PanelEntry> list)
        {

            var grid = UI.Rect("Grid", c);
            var g = grid.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(148, 196);
            g.spacing = new Vector2(10, 10);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = 3;

            foreach (var e in list)
            {
                var entry = e;
                bool sel = e.Id == _selId;
                var b = UI.Button(grid, "", sel ? Theme.Blue : Theme.Row, () => { _selId = entry.Id; RenderGrid(); RenderDetails(); });
                var pic = UI.Picture(b.transform, CardOf(e), Theme.Track);
                UI.Place(pic.rectTransform, 7, 7, 134, 124);
                pic.raycastTarget = false;
                if (e.Src.Disabled)
                {
                    var dim = UI.Box(pic.transform, "Off", new Color(0, 0, 0, 0.55f), Theme.Round);
                    UI.Fill(dim.rectTransform, 0, 0, 0, 0);
                    dim.raycastTarget = false;
                    var off = UI.Text(dim.transform, "OFF", 20, Theme.Text, true, TextAlignmentOptions.Center);
                    UI.Fill(off.rectTransform, 0, 0, 0, 0);
                }
                var name = UI.Text(b.transform, e.Name, 15, Theme.Text, true, TextAlignmentOptions.TopLeft);
                UI.Place(name.rectTransform, 9, 136, 132, 22);
                name.enableWordWrapping = false;
                string sub = _selSpecies == null ? SkinFiles.SpeciesLabel(e.Species) : (e.Author != null ? "by " + e.Author : e.Src.Legacy ? "flat files" : "");
                var s2 = UI.Text(b.transform, sub, 13, sel ? Theme.Text : Theme.TextMuted, false, TextAlignmentOptions.TopLeft);
                UI.Place(s2.rectTransform, 9, 157, 132, 18);
                s2.enableWordWrapping = false;
                string st = e.Src.Disabled ? "<color=#e07070>switched off</color>" : e.Wearing > 0 ? "<color=#8fe06a>" + e.Wearing + " in park</color>" : "";
                var s3 = UI.Text(b.transform, st, 13, Theme.TextDim, true, TextAlignmentOptions.TopLeft);
                UI.Place(s3.rectTransform, 9, 175, 132, 18);
            }
        }

        // ------------------------------------------------------------------ right: details + settings

        private static void Section(Transform c, string title, string hint)
        {
            var gap = UI.Rect("Gap", c); UI.Size(gap, 4);
            var line = UI.Box(c, "Line", new Color(1, 1, 1, 0.08f), null); UI.Size(line, 2);
            UI.Para(c, title, 18, Theme.Text, true);
            if (!string.IsNullOrEmpty(hint)) UI.Para(c, hint, 14, Theme.TextMuted);
        }

        private static RectTransform Row(Transform c, float height)
        {
            var row = UI.Rect("Row", c);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childForceExpandWidth = true; h.childForceExpandHeight = true;
            h.childControlWidth = true; h.childControlHeight = true;
            UI.Size(row, height);
            return row;
        }

        private static void LabeledSlider(Transform c, string label, float min, float max, float value, string fmt, Action<float> release)
        {
            var row = Row(c, 32);
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            var t = UI.Text(row, label, 15, Theme.TextDim);
            UI.Size(t, -1, 130);
            var s = UI.Slider(row, min, max, value, fmt, null, release);
            s.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1;
        }

        private static void RenderDetails()
        {
            KeepScroll(_right, "details|" + (_selId ?? ""));
            UI.Clear(_right);
            _rightScroll = null;
            var e = Selected;
            ScrollRect sr;
            var c = UI.Scroll(_right, 8, new RectOffset(16, 16, 14, 18), out sr);
            _rightScroll = sr;
            BaseAnimalData vsp; int vidx;
            if (e == null && ParseV(_selId, out vsp, out vidx)) { RenderVanillaDetails(c, vsp, vidx); return; }
            if (e == null)
            {
                UI.Para(c, "Select a skin", 20, Theme.Text, true);
                return;
            }
            var src = e.Src;
            var bv = SkinFiles.BaseVariation(e);

            // ---- header
            var top = UI.Rect("Top", c);
            UI.Size(top, 150);
            var pic = UI.Picture(top, CardOf(e), Theme.Track);
            UI.Place(pic.rectTransform, 0, 0, 150, 150);
            var name = UI.Text(top, e.Name, 24, Theme.Text, true, TextAlignmentOptions.TopLeft);
            UI.Place(name.rectTransform, 166, 2, RightW - 200, 32);
            name.enableWordWrapping = false;
            var lines = new List<string>();
            if (e.Author != null) lines.Add("by " + e.Author);
            lines.Add(SkinFiles.SpeciesLabel(e.Species) + (src.Pack != null ? "  |  pack: " + (src.PackName ?? src.Pack) + (src.PackVersion != null ? " v" + src.PackVersion : "") : ""));
            lines.Add(src.Disabled ? "<color=#e07070>Switched off</color>" : "<color=#8fe06a>On</color>" + (e.Wearing > 0 ? "  |  " + e.Wearing + " animal(s) in this park" : ""));
            if (e.Slot >= 0) lines.Add("<color=#8b9199>Skin slot #" + e.Slot + (src.Legacy ? "  |  flat files" : "") + "</color>");
            var info = UI.Text(top, string.Join("\n", lines.ToArray()), 15, Theme.TextDim, false, TextAlignmentOptions.TopLeft);
            UI.Place(info.rectTransform, 166, 38, RightW - 200, 112);

            // ---- quick tools
            var tools = Row(c, 38);
            UI.Button(tools, "Open folder", Theme.Button, () =>
                SkinFiles.OpenFolder(SkinFiles.FolderOf(src), src.Legacy ? e.Def.FileFor(TexSlots.Diffuse, true) : null), 15);
            UI.Button(tools, src.Disabled ? "Switch on" : "Switch off", src.Disabled ? Theme.Green : Theme.Red, () =>
            {
                string err = SkinFiles.SetEnabled(src, src.Disabled);
                if (err == null && src.Disabled && SkinFiles.UnderscoreFolder(src) != null)
                    _selId = e.Species.name + "|" + src.Location.Replace("/_", "/").TrimStart('_');
                Saved(err);
            }, 15);
            UI.Button(tools, "Templates", Theme.Button, () => MelonCoroutines.Start(OpenTemplates(e)), 15);

            bool ownPattern = src.Files[TexSlots.Pattern, 0] != null || src.Files[TexSlots.Pattern, 1] != null;
            string gen = SkinFiles.GeneratedPatternFor(e);
            if (gen != null)
            {
                var row2 = Row(c, 38);
                UI.Button(row2, "Show generated pattern", Theme.Button, () => SkinFiles.OpenFolder(Path.GetDirectoryName(gen), gen), 15);
                if (!ownPattern)
                    UI.Button(row2, "Use it as pattern.png", Theme.Button, () => Saved(SkinFiles.UseGeneratedPattern(e)), 15);
            }

            // ---- what the skin provides
            UI.Para(c, "<color=#8b9199>Your files:</color> " + FilesSummary(e), 14, Theme.TextDim);

            // ---- base skin
            Section(c, "Base skin", "Everything you didn't paint comes from here: normal map, fur, crests and spikes (cut-outs).");
            var bases = SkinFiles.VanillaSkins(e.Species);
            AnimalSkinData current = e.Live != null ? e.Live.BaseSkin : null;
            var bg = UI.Rect("Bases", c);
            var g = bg.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(104, 128); g.spacing = new Vector2(8, 8);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = 4;
            BaseCard(bg, null, "Automatic", src.Base == null, () => Saved(SkinFiles.SetSetting(src, "base", null)));
            foreach (var kv in bases)
            {
                var skin = kv.Value;
                bool sel = src.Base != null && (current != null ? ReferenceEquals(current, skin)
                           : string.Equals(src.Base, skin.skinName, StringComparison.OrdinalIgnoreCase) || src.Base == kv.Key.ToString());
                BaseCard(bg, skin.skinThumbnail, skin.skinName, sel, () => Saved(SkinFiles.SetSetting(src, "base", skin.skinName)));
            }

            // ---- pattern
            Section(c, "Pattern", ownPattern
                ? "Automatic uses your pattern.png. The pattern is what the colour sliders, random colours and mutations paint on."
                : "Automatic makes it from your diffuse. The pattern is what the colour sliders, random colours and mutations paint on.");
            string[] pmodes = { null, "diffuse", "base", "solid", "none" };
            int psel = Array.IndexOf(pmodes, src.PatternMode); if (psel < 0) psel = 0;
            UI.Segmented(c, new[] { "Automatic", "From diffuse", "Base skin", "Whole body", "None" }, psel,
                i => Saved(SkinFiles.SetSetting(src, "pattern", pmodes[i] == null ? null : new JValue(pmodes[i]))));

            // ---- iridescence (only for species that have it)
            if (HasIridescence(e.Species, src))
            {
                Section(c, "Iridescence", "On/off is decided when an animal is created; strength changes right away.");
                int isel = src.Iridescence == null ? 0 : src.Iridescence.Value ? 1 : 2;
                UI.Segmented(c, new[] { "Like base skin", "On", "Off" }, isel,
                    i => Saved(SkinFiles.SetSetting(src, "iridescence", i == 0 ? null : new JValue(i == 1))));
                float baseStrength = bv != null && bv.defaultVariationData != null ? bv.defaultVariationData.iridescenceStrength : 1f;
                LabeledSlider(c, "Strength", 0f, 1f, src.IridescenceStrength ?? baseStrength, "0.00",
                    v => Saved(SkinFiles.SetSetting(src, "iridescenceStrength", new JValue(Math.Round(v, 2)))));
                if (src.IridescenceStrength != null)
                    UI.Size(UI.Button(c, "Reset strength", Theme.Button, () => Saved(SkinFiles.SetSetting(src, "iridescenceStrength", null)), 14), 32);
            }

            // ---- fur
            FurSection(c, e.Species, src.FurCoverage, src.FurLength,
                v => Saved(SkinFiles.SetSetting(src, "furCoverage", v == null ? null : new JValue(v))),
                v => Saved(SkinFiles.SetSetting(src, "furLength", v == null ? null : new JValue(Math.Round(v.Value, 2)))));

            // ---- fur droop
            Section(c, "Fur droop", "How fur and fuzz hang. Up / down is the one you'll see most. Changes right away.");
            Vector3 d = src.FuzzDroopiness ?? (bv != null ? bv.adultFuzzDroopiness : Vector3.zero);
            Action<int, float> setDroop = (axis, v) =>
            {
                var cur = src.FuzzDroopiness ?? (bv != null ? bv.adultFuzzDroopiness : Vector3.zero);
                cur[axis] = (float)Math.Round(v, 1);
                Saved(SkinFiles.SetSetting(src, "fuzzDroopiness", new JArray(cur.x, cur.y, cur.z)));
            };
            LabeledSlider(c, "Sideways (X)", -50f, 50f, d.x, "0.0", v => setDroop(0, v));
            LabeledSlider(c, "Up / down (Y)", -50f, 50f, d.y, "0.0", v => setDroop(1, v));
            LabeledSlider(c, "Front / back (Z)", -50f, 50f, d.z, "0.0", v => setDroop(2, v));
            if (src.FuzzDroopiness != null)
                UI.Size(UI.Button(c, "Reset fur droop", Theme.Button, () => Saved(SkinFiles.SetSetting(src, "fuzzDroopiness", null)), 14), 32);

            // ---- cut-outs
            Section(c, "Cut-outs", "Transparent parts of the diffuse hide model parts (crests, spikes, feathers). Automatic: yours if you painted any, otherwise the base skin's.");
            string[] cmodes = { "auto", "base", "own" };
            int csel = Math.Max(0, Array.IndexOf(cmodes, src.Cutout));
            UI.Segmented(c, new[] { "Automatic", "Base skin", "Your diffuse" }, csel,
                i => Saved(SkinFiles.SetSetting(src, "cutout", i == 0 ? null : new JValue(cmodes[i]))));

            var gap = UI.Rect("Gap", c); UI.Size(gap, 6);
            UI.Para(c, "Settings are saved in " + (src.Legacy ? src.LegacyName + ".json" : "skin.json") + " and applied straight away.", 13, Theme.TextMuted);
        }

        private static void RenderVanillaDetails(Transform c, BaseAnimalData sp, int idx)
        {
            var skin = sp.skinsData[idx];
            var t = VanillaTweaks.Get(sp, skin);
            var bv = skin.maleVariationData ?? skin.femaleVariationData;
            int wearing = SkinApplier.CountWearing(sp, idx);

            var top = UI.Rect("Top", c);
            UI.Size(top, 150);
            var pic = UI.Picture(top, skin.skinThumbnail, Theme.Track);
            UI.Place(pic.rectTransform, 0, 0, 150, 150);
            var name = UI.Text(top, skin.skinName, 24, Theme.Text, true, TextAlignmentOptions.TopLeft);
            UI.Place(name.rectTransform, 166, 2, RightW - 200, 32);
            name.enableWordWrapping = false;
            var lines = new List<string>();
            lines.Add("Vanilla skin");
            lines.Add(SkinFiles.SpeciesLabel(sp));
            lines.Add(t.Empty ? "<color=#8b9199>Unchanged</color>" : "<color=#f0b75a>Changed by you</color>");
            if (wearing > 0) lines.Add("<color=#8fe06a>" + wearing + " animal(s) in this park</color>");
            var info = UI.Text(top, string.Join("\n", lines.ToArray()), 15, Theme.TextDim, false, TextAlignmentOptions.TopLeft);
            UI.Place(info.rectTransform, 166, 38, RightW - 200, 112);

            var tools = Row(c, 38);
            UI.Button(tools, "Make a custom skin from it", Theme.Green, () =>
            {
                _wzSpecies = sp; _wzBase = idx;
                if (Config.PanelCompact.Value) { Config.PanelCompact.Value = false; MelonPreferences.Save(); }
                Show(Mode.Wizard);
            }, 15);
            if (!t.Empty)
                UI.Button(tools, "Reset to vanilla", Theme.Red, () => Saved(VanillaTweaks.Change(sp, skin, x => { x.Droop = null; x.Iridescence = null; x.IridescenceStrength = null; x.FurLength = null; x.FurCoverage = null; })), 15);

            if (HasIridescence(sp, null))
            {
                Section(c, "Iridescence", "Changes right away, for every animal wearing this skin.");
                int isel = t.Iridescence == null ? 0 : t.Iridescence.Value ? 1 : 2;
                UI.Segmented(c, new[] { "Like vanilla", "On", "Off" }, isel,
                    i => Saved(VanillaTweaks.Change(sp, skin, x => x.Iridescence = i == 0 ? (bool?)null : i == 1)));
                float baseStrength = bv != null && bv.defaultVariationData != null ? bv.defaultVariationData.iridescenceStrength : 1f;
                LabeledSlider(c, "Strength", 0f, 1f, t.IridescenceStrength ?? baseStrength, "0.00",
                    v => Saved(VanillaTweaks.Change(sp, skin, x => x.IridescenceStrength = (float)Math.Round(v, 2))));
                if (t.IridescenceStrength != null)
                    UI.Size(UI.Button(c, "Reset strength", Theme.Button, () => Saved(VanillaTweaks.Change(sp, skin, x => x.IridescenceStrength = null)), 14), 32);
            }

            FurSection(c, sp, t.FurCoverage, t.FurLength,
                v => Saved(VanillaTweaks.Change(sp, skin, x => x.FurCoverage = v)),
                v => Saved(VanillaTweaks.Change(sp, skin, x => x.FurLength = v.HasValue ? (float?)(float)Math.Round(v.Value, 2) : null)));

            Section(c, "Fur droop", "How fur and fuzz hang. Up / down is the one you'll see most. Changes right away.");
            Vector3 d = t.Droop ?? (bv != null ? bv.adultFuzzDroopiness : Vector3.zero);
            Action<int, float> setDroop = (axis, v) => Saved(VanillaTweaks.Change(sp, skin, x =>
            {
                var cur = x.Droop ?? (bv != null ? bv.adultFuzzDroopiness : Vector3.zero);
                cur[axis] = (float)Math.Round(v, 1);
                x.Droop = cur;
            }));
            LabeledSlider(c, "Sideways (X)", -50f, 50f, d.x, "0.0", v => setDroop(0, v));
            LabeledSlider(c, "Up / down (Y)", -50f, 50f, d.y, "0.0", v => setDroop(1, v));
            LabeledSlider(c, "Front / back (Z)", -50f, 50f, d.z, "0.0", v => setDroop(2, v));
            if (t.Droop != null)
                UI.Size(UI.Button(c, "Reset fur droop", Theme.Button, () => Saved(VanillaTweaks.Change(sp, skin, x => x.Droop = null)), 14), 32);

            var gap = UI.Rect("Gap", c); UI.Size(gap, 6);
            UI.Para(c, "Saved in UserData/PKSkinLoader/vanilla-tweaks.json. Saves and game files are not changed; remove the mod and these skins are vanilla again. To change textures, make a custom skin from it.", 13, Theme.TextMuted);
        }

        /// <summary>Fur coverage + length controls (custom skins, vanilla skins and single animals).</summary>
        private static void FurSection(Transform c, BaseAnimalData sp, string coverage, float? length, Action<string> setCoverage, Action<float?> setLength)
        {
            bool? capable = null;
            try
            {
                foreach (var a in SkinApplier.Known.ToList())
                {
                    BaseAnimalData d;
                    try { d = a.BaseData; } catch { continue; }
                    if (d == null || d.name != sp.name) continue;
                    capable = SkinApplier.CanHaveFur(a);
                    break;
                }
            }
            catch { }
            string hint = "Give bald adults the baby's fuzz or a whole coat, or make fuzzy ones bald. Length changes right away.";
            if (capable == false) hint = "<color=#f0b75a>EXPERIMENT: this species normally has no fur layer. Whole body switches the game's fur feature on for it - if it looks broken, pick Skin's own again.</color>";
            Section(c, "Fur", hint);
            string[] modes = { null, "baby", "body", "none" };
            int sel = Math.Max(0, Array.IndexOf(modes, coverage));
            UI.Segmented(c, new[] { "Skin's own", "Baby fuzz", "Whole body", "Bald" }, sel, i => setCoverage(modes[i]));
            LabeledSlider(c, "Length", 0f, 3f, length ?? 1f, "0.00", v => setLength(Math.Abs(v - 1f) < 0.005f ? (float?)null : v));
            if (length != null)
                UI.Size(UI.Button(c, "Reset length", Theme.Button, () => setLength(null), 14), 32);
        }

        // ================================================================== one animal

        private static Animal SelectedAnimal()
        {
            try
            {
                var sel = Game.Selection;
                return sel != null ? sel.SingleSelected as Animal : null;
            }
            catch { return null; }
        }

        private static void RenderAnimalNav()
        {
            UI.Clear(_nav);
            string label = _animal != null ? SafeName(_animal) + "  <size=14><color=#8b9199>" + SkinFiles.SpeciesLabel(_animal.BaseData) + "</color></size>"
                                           : "<color=#8b9199>Select an animal in your park</color>";
            var t = UI.Text(_nav, label, 18, Theme.Text, true, TextAlignmentOptions.Center);
            UI.Place(t.rectTransform, 8, 0, RightW - 16, 44);
            t.enableWordWrapping = false;
        }

        private static string SafeName(IAnimal a)
        {
            try { return string.IsNullOrEmpty(a.Name) ? "Unnamed" : a.Name; } catch { return "?"; }
        }

        private static void RenderAnimalInfo()
        {
            UI.Clear(_mid);
            ScrollRect sr;
            var c = UI.Scroll(_mid, 8, new RectOffset(18, 18, 14, 18), out sr);
            UI.Para(c, "Individual animals", 20, Theme.Text, true);
            UI.Para(c, "Click an animal in your park to give it its own look: a longer or shorter coat, a winter coat, droop or shimmer. "
                + "It's kept with your park when you save.", 15, Theme.TextDim);
            var gap = UI.Rect("Gap", c); UI.Size(gap, 6);
            UI.Para(c, "Genes", 18, Theme.Text, true);
            UI.Para(c, "Babies inherit a mix of their parents' looks: somewhere between mum and dad, with a little variation. "
                + "If only one parent is special, there's about a 50/50 chance a trait is passed on. Breed for shaggy coats!", 15, Theme.TextDim);
            var gap2 = UI.Rect("Gap", c); UI.Size(gap2, 6);
            UI.Para(c, AnimalTweaks.Count + " animal(s) in this park have their own look.", 15, Theme.TextMuted);
        }

        private static void AnimalChanged(Action<AnimalTweak> change)
        {
            var a = _animal;
            if (a == null) return;
            AnimalTweaks.Change(a, change);
            SkinApplier.Refresh(a);
            Toast("Changed - it's kept when you save the park.", Theme.GreenLight, 3f);
            RenderAnimal();
        }

        private static void RenderAnimal()
        {
            if (_canvasGo == null) return;
            var a = _animal;
            ulong id = a != null ? AnimalTweaks.IdOf(a) : 0;
            KeepScroll(_right, "animal|" + id);
            UI.Clear(_right);
            ScrollRect sr;
            var c = UI.Scroll(_right, 8, new RectOffset(16, 16, 14, 18), out sr);
            if (a == null || a.BaseData == null)
            {
                UI.Para(c, "Select an animal", 22, Theme.Text, true);
                UI.Para(c, "Click any animal in your park (not in the nursery). Its own look appears here.", 15, Theme.TextDim);
                return;
            }
            var sp = a.BaseData;
            int idx = a.CurrentSkinIDX;
            var def = SkinInjector.DefFor(sp, idx);
            var before = AnimalTweaks.Get(a);
            var own = AnimalTweaks.For(a) ?? new AnimalTweak();   // For() also lets a newborn inherit right now
            if (before == null && !own.Empty) SkinApplier.Refresh(a);

            // ---- header
            var top = UI.Rect("Top", c);
            UI.Size(top, 150);
            Sprite portrait = null;
            try { portrait = a.Portrait; } catch { }
            var pic = UI.Picture(top, portrait, Theme.Track);
            UI.Place(pic.rectTransform, 0, 0, 150, 150);
            var name = UI.Text(top, SafeName(a), 24, Theme.Text, true, TextAlignmentOptions.TopLeft);
            UI.Place(name.rectTransform, 166, 2, RightW - 200, 32);
            name.enableWordWrapping = false;
            string skinName = def != null ? new PanelEntry { Def = def, Species = sp }.Name : (idx >= 0 && idx < sp.skinsData.Count ? sp.skinsData[idx].skinName : "?");
            var lines = new List<string>();
            lines.Add(SkinFiles.SpeciesLabel(sp) + "  |  " + skinName);
            try { lines.Add(a.Sex + ", " + (a.AgeMonths < 12 ? Mathf.Max(0, a.AgeMonths).ToString("0.#") + " months" : (a.AgeMonths / 12f).ToString("0.#") + " years")); } catch { }
            lines.Add(own.Empty ? "<color=#8b9199>Looks like its skin</color>" : "<color=#f0b75a>Has its own look</color>");
            if (own.InheritedFrom != null) lines.Add("<color=#8fe06a>Inherited from " + own.InheritedFrom + "</color>");
            else
            {
                string parents = AnimalTweaks.ParentsInfo(a);
                if (parents != null) lines.Add("<color=#8b9199>" + parents + (SkinModBase.Verbose ? "  [id " + id + ", " + AnimalTweaks.Status(a) + "]" : "") + "</color>");
            }
            var info = UI.Text(top, string.Join("\n", lines.ToArray()), 15, Theme.TextDim, false, TextAlignmentOptions.TopLeft);
            UI.Place(info.rectTransform, 166, 38, RightW - 200, 112);

            var tools = Row(c, 38);
            UI.Button(tools, "Edit its skin", Theme.Button, () =>
            {
                _mode = Mode.Browse;
                _selSpecies = _species.FirstOrDefault(x => x.name == sp.name) ?? sp;
                _selId = def != null ? sp.name + "|" + def.Source.Location : VId(_selSpecies, idx);
                Render();
            }, 15);
            if (!own.Empty)
                UI.Button(tools, "Reset to its skin's look", Theme.Red, () => AnimalChanged(x => { x.FurLength = null; x.FurCoverage = null; x.Droop = null; x.IridescenceStrength = null; x.InheritedFrom = null; }), 15);

            // ---- fur
            FurSection(c, sp, own.FurCoverage, own.FurLength,
                v => AnimalChanged(x => x.FurCoverage = v),
                v => AnimalChanged(x => x.FurLength = v.HasValue ? (float?)(float)Math.Round(v.Value, 2) : null));

            // ---- droop
            Vector3 skinDroop = Vector3.zero;
            try
            {
                var vt = def == null ? VanillaTweaks.For(sp, idx) : null;
                var bskin = def != null ? def.BaseSkin : sp.skinsData[idx];
                var bv = bskin != null ? (bskin.maleVariationData ?? bskin.femaleVariationData) : null;
                skinDroop = def != null ? (def.Source.FuzzDroopiness ?? (bv != null ? bv.adultFuzzDroopiness : Vector3.zero))
                                        : (vt != null && vt.Droop.HasValue ? vt.Droop.Value : (bv != null ? bv.adultFuzzDroopiness : Vector3.zero));
            }
            catch { }
            Section(c, "Fur droop", "How this animal's fur hangs. Up / down is the one you'll see most.");
            Vector3 d = own.Droop ?? skinDroop;
            Action<int, float> setDroop = (axis, val) => AnimalChanged(x =>
            {
                var cur = x.Droop ?? skinDroop;
                cur[axis] = (float)Math.Round(val, 1);
                x.Droop = cur;
            });
            LabeledSlider(c, "Sideways (X)", -50f, 50f, d.x, "0.0", val => setDroop(0, val));
            LabeledSlider(c, "Up / down (Y)", -50f, 50f, d.y, "0.0", val => setDroop(1, val));
            LabeledSlider(c, "Front / back (Z)", -50f, 50f, d.z, "0.0", val => setDroop(2, val));
            if (own.Droop != null)
                UI.Size(UI.Button(c, "Reset fur droop", Theme.Button, () => AnimalChanged(x => x.Droop = null), 14), 32);

            // ---- iridescence
            if (HasIridescence(sp, def != null ? def.Source : null))
            {
                Section(c, "Shimmer", "This animal's iridescence strength.");
                float cur = 0.5f;
                try { cur = a.SkinVariationRuntime.iridescenceStrength; } catch { }
                LabeledSlider(c, "Strength", 0f, 1f, own.IridescenceStrength ?? cur, "0.00",
                    val => AnimalChanged(x => x.IridescenceStrength = (float)Math.Round(val, 2)));
                if (own.IridescenceStrength != null)
                    UI.Size(UI.Button(c, "Reset shimmer", Theme.Button, () => AnimalChanged(x => x.IridescenceStrength = null), 14), 32);
            }

            var gap = UI.Rect("Gap", c); UI.Size(gap, 6);
            UI.Para(c, "Kept with this park when you save. Its babies inherit a mix of both parents' looks.", 13, Theme.TextMuted);
        }

        private static void BaseCard(Transform grid, Sprite sprite, string label, bool selected, Action click)
        {
            var b = UI.Button(grid, "", selected ? Theme.Blue : Theme.Row, click);
            if (sprite != null)
            {
                var pic = UI.Picture(b.transform, sprite, Theme.Track);
                UI.Place(pic.rectTransform, 6, 6, 92, 88);
                pic.raycastTarget = false;
            }
            else
            {
                var a = UI.Text(b.transform, "AUTO", 20, Theme.TextDim, true, TextAlignmentOptions.Center);
                UI.Place(a.rectTransform, 6, 6, 92, 88);
            }
            var t = UI.Text(b.transform, label, 13, Theme.Text, selected, TextAlignmentOptions.Center);
            UI.Place(t.rectTransform, 4, 98, 96, 26);
            t.enableWordWrapping = false;
        }

        private static bool HasIridescence(BaseAnimalData sp, SkinSource src)
        {
            if (src != null && (src.Files[TexSlots.Iridescence, 0] != null || src.Files[TexSlots.Iridescence, 1] != null)) return true;
            foreach (var kv in SkinFiles.VanillaSkins(sp))
            {
                var v = kv.Value.maleVariationData ?? kv.Value.femaleVariationData;
                try
                {
                    if (v != null && v.defaultVariationData != null && v.defaultVariationData.iridescenceMap != null
                        && v.defaultVariationData.iridescenceMap.RuntimeKeyIsValid()) return true;
                }
                catch { }
            }
            return false;
        }

        private static string FilesSummary(PanelEntry e)
        {
            var names = new[] { "diffuse", "normal", "extra", "pattern", "fur" };
            var have = new List<string>();
            for (int slot = 0; slot < TexSlots.Count; slot++)
            {
                if (e.Src.Files[slot, 0] == null && e.Src.Files[slot, 1] == null) continue;
                string n;
                if (slot == TexSlots.Iridescence) n = "iridescence";
                else if (slot == TexSlots.IridescenceGradient) n = "iridescence gradient";
                else if (TexSlots.IsInfant(slot)) n = "infant " + names[TexSlots.Kind(slot) - TexSlots.Diffuse];
                else n = names[TexSlots.Kind(slot) - TexSlots.Diffuse];
                if (e.Src.Files[slot, 0] != null && e.Src.Files[slot, 1] != null && e.Src.Files[slot, 0] != e.Src.Files[slot, 1]) n += " (male + female)";
                have.Add(n);
            }
            if (e.Src.ThumbnailPath != null && !e.Src.Legacy) have.Add("card");
            if (e.Src.Portraits.Count > 0) have.Add("portrait");
            return have.Count == 0 ? "none" : string.Join(", ", have.ToArray());
        }

        private static IEnumerator OpenTemplates(PanelEntry e)
        {
            if (_busy) yield break;
            _busy = true;
            Toast("Exporting templates for " + SkinFiles.SpeciesLabel(e.Species) + "...", Theme.TextDim, 30f);
            var it = Flatten(SkinDumper.ExportSpecies(e.Species));
            while (true)
            {
                bool more;
                try { more = it.MoveNext(); }
                catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); break; }
                if (!more) break;
                yield return it.Current;
            }
            _busy = false;
            int idx = 0;
            var baseSkin = e.Live != null ? e.Live.BaseSkin : null;
            if (baseSkin != null) idx = Math.Max(0, e.Species.skinsData.IndexOf(baseSkin));
            else { var v = SkinFiles.VanillaSkins(e.Species); if (v.Count > 0) idx = v[0].Key; }
            string dir = SkinDumper.TemplateDir(e.Species, idx);
            if (Directory.Exists(dir)) { SkinFiles.OpenFolder(dir, null); Toast("Templates opened in Explorer.", Theme.GreenLight); }
            else Toast("Templates could not be exported - see the console.", Theme.Amber, 7f);
        }

        /// <summary>Runs nested coroutines inside one (works whatever the coroutine runner supports).</summary>
        private static IEnumerator Flatten(IEnumerator e)
        {
            while (e.MoveNext())
            {
                var inner = e.Current as IEnumerator;
                if (inner != null)
                {
                    var f = Flatten(inner);
                    while (f.MoveNext()) yield return f.Current;
                }
                else yield return e.Current;
            }
        }

        // ------------------------------------------------------------------ problems

        private static void RenderProblems()
        {
            UI.Clear(_mid);
            ScrollRect sr;
            var c = UI.Scroll(_mid, 8, new RectOffset(18, 18, 14, 18), out sr);
            var list = Problems();
            UI.Para(c, list.Count == 0 ? "No problems found" : list.Count + " problem(s)", 20, Theme.Text, true);
            UI.Para(c, list.Count == 0
                ? "Every skin loaded cleanly."
                : "From the last start or reload. Fix the file, then press Reload.", 15, Theme.TextMuted);
            foreach (var p in list)
            {
                var box = UI.Box(c, "Problem", Theme.Row, Theme.Round);
                var v = box.gameObject.AddComponent<VerticalLayoutGroup>();
                v.padding = new RectOffset(12, 12, 8, 8);
                v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false;
                UI.Para(box.transform, p, 15, Theme.TextDim);
            }
        }

        // ================================================================== new skin wizard

        private static void RenderWizardSpecies()
        {
            UI.Clear(_left);
            var search = UI.Input(_left, "Search species...", _wzSearch, v => { _wzSearch = v; RenderWizardSpeciesList(); });
            UI.Place((RectTransform)search.transform, 8, 8, LeftW - 16, 36);
            _wzList = UI.Rect("List", _left);
            UI.Place(_wzList, 0, 50, LeftW, BodyH - 50);
            RenderWizardSpeciesList();
        }

        private static void RenderWizardSpeciesList()
        {
            UI.Clear(_wzList);
            ScrollRect sr;
            var c = UI.Scroll(_wzList, 4, new RectOffset(8, 8, 4, 8), out sr);
            string q = (_wzSearch ?? "").Trim().ToLowerInvariant();
            foreach (var sp in _species)
            {
                string label = SkinFiles.SpeciesLabel(sp);
                if (q.Length > 0 && label.ToLowerInvariant().IndexOf(q) < 0 && sp.name.ToLowerInvariant().IndexOf(q) < 0) continue;
                var s = sp;
                int count = _entries.Count(x => x.Species == sp);
                SpeciesButton(c, label, count, s == _wzSpecies, () =>
                {
                    _wzSpecies = s; _wzBase = -1;
                    RenderWizardSpeciesList(); RenderWizardBases(); RenderWizardForm();
                });
            }
        }

        private static void RenderWizardBases()
        {
            KeepScroll(_mid, "bases|" + (_wzSpecies != null ? _wzSpecies.name : ""));
            UI.Clear(_mid);
            ScrollRect sr;
            var c = UI.Scroll(_mid, 10, new RectOffset(14, 14, 12, 14), out sr);
            if (_wzSpecies == null) { UI.Para(c, "Pick a species on the left", 20, Theme.Text, true); return; }
            var bases = SkinFiles.VanillaSkins(_wzSpecies);
            if (_wzBase < 0 && bases.Count > 0) _wzBase = bases[0].Key;
            UI.Para(c, "Start from which skin?", 20, Theme.Text, true);
            UI.Para(c, "Your new skin begins as a copy of this one. Its crests, spikes, feathers and fur come along.", 15, Theme.TextMuted);
            var grid = UI.Rect("Grid", c);
            var g = grid.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(148, 176); g.spacing = new Vector2(10, 10);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = 3;
            foreach (var kv in bases)
            {
                int idx = kv.Key;
                bool sel = idx == _wzBase;
                var b = UI.Button(grid, "", sel ? Theme.Blue : Theme.Row, () => { _wzBase = idx; RenderWizardBases(); RenderWizardForm(); });
                var pic = UI.Picture(b.transform, kv.Value.skinThumbnail, Theme.Track);
                UI.Place(pic.rectTransform, 7, 7, 134, 124);
                pic.raycastTarget = false;
                var t = UI.Text(b.transform, kv.Value.skinName, 15, Theme.Text, true, TextAlignmentOptions.TopLeft);
                UI.Place(t.rectTransform, 9, 138, 132, 30);
                t.enableWordWrapping = false;
            }
        }

        private static void RenderWizardForm()
        {
            KeepScroll(_right, "wizard");
            UI.Clear(_right);
            ScrollRect sr;
            var c = UI.Scroll(_right, 8, new RectOffset(16, 16, 14, 18), out sr);
            if (_wzSpecies == null || _wzBase < 0) { UI.Para(c, "New skin", 24, Theme.Text, true); UI.Para(c, "Pick a species and a skin to start from.", 16, Theme.TextDim); return; }

            if (_wzAuthor == null)
                _wzAuthor = SkinScanner.Sources.Select(s => s.Author ?? s.PackAuthor).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? "";

            UI.Para(c, "New " + SkinFiles.SpeciesLabel(_wzSpecies) + " skin", 24, Theme.Text, true);
            UI.Para(c, "Starting from: " + _wzSpecies.skinsData[_wzBase].skinName, 15, Theme.TextDim);

            Section(c, "Name", "Shown in the nursery. Pick it carefully - renaming later gives the skin a new slot.");
            UI.Input(c, "e.g. Ember", _wzName, v => _wzName = v);

            Section(c, "Author", "Credit shown in the nursery (optional).");
            UI.Input(c, "Your name", _wzAuthor, v => _wzAuthor = v);

            Section(c, "Pack", "Optional folder that groups your skins for sharing. Leave empty for none.");
            UI.Input(c, "e.g. Moshepit's Raptors", _wzPack, v => _wzPack = v);
            var packs = SkinFiles.ExistingPacks();
            if (packs.Count > 0)
            {
                var row = Row(c, 32);
                foreach (var p in packs.Take(3)) { var pk = p; UI.Button(row, pk, Theme.Button, () => { _wzPack = pk; RenderWizardForm(); }, 13); }
            }

            Section(c, "Your diffuse (optional)", "Already painted it? Pick your PNG and it becomes the skin's diffuse. Otherwise the base skin's diffuse is copied for you to paint over.");
            if (_wzDiffuse != null)
            {
                UI.Para(c, Path.GetFileName(_wzDiffuse), 16, Theme.GreenLight, true);
                var r = Row(c, 34);
                UI.Button(r, "Choose another", Theme.Button, PickDiffuse, 14);
                UI.Button(r, "Use the base skin's instead", Theme.Button, () => { _wzDiffuse = null; RenderWizardForm(); }, 14);
            }
            else UI.Size(UI.Button(c, "Choose PNG...", Theme.Button, PickDiffuse, 15), 36);

            var gap = UI.Rect("Gap", c); UI.Size(gap, 10);
            var create = UI.Button(c, _busy ? "Creating..." : "Create skin", Theme.Green, () => { if (!_busy) MelonCoroutines.Start(CreateRoutine()); }, 20);
            UI.Size(create, 52);
            create.interactable = !_busy;

            UI.Para(c, "Creates the skin folder, puts it in the nursery and opens it in Explorer. "
                + "Paint, save, then press Reload. The other templates (normal, pattern, fur...) are under the Templates button.", 14, Theme.TextMuted);
        }

        private static void PickDiffuse()
        {
            string start = _wzDiffuse != null ? Path.GetDirectoryName(_wzDiffuse) : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string f = FileDialog.OpenPng("Choose your diffuse texture", start);
            if (f != null) _wzDiffuse = f;
            RenderWizardForm();
        }

        private static IEnumerator CreateRoutine()
        {
            var sp = _wzSpecies;
            int baseIdx = _wzBase;
            string name = SkinFiles.CleanFolderName(_wzName);
            string pack = SkinFiles.CleanFolderName(_wzPack);
            string author = (_wzAuthor ?? "").Trim();
            if (sp == null || baseIdx < 0) yield break;
            if (name.Length == 0) { Toast("Give your skin a name first.", Theme.Amber); yield break; }
            if (Directory.Exists(SkinFiles.NewSkinFolder(sp, pack, name))) { Toast("There's already a skin called '" + name + "' there.", Theme.Amber); yield break; }

            _busy = true;
            RenderWizardForm();
            Toast(_wzDiffuse == null ? "Exporting " + SkinFiles.SpeciesLabel(sp) + " templates..." : "Creating...", Theme.TextDim, 60f);

            string own = _wzDiffuse;
            if (own == null)   // the base skin's diffuse comes from its exported templates
            {
                var it = Flatten(SkinDumper.ExportSpecies(sp));
                while (true)
                {
                    bool more;
                    try { more = it.MoveNext(); }
                    catch (Exception ex) { SkinModBase.Log.Error("[Panel] " + ex); break; }
                    if (!more) break;
                    yield return it.Current;
                }
            }

            string err = SkinFiles.CreateSkin(sp, baseIdx, pack.Length > 0 ? pack : null, name, author, own);
            _busy = false;
            if (err != null) { Toast(err, Theme.Amber, 8f); RenderWizardForm(); yield break; }

            string folder = SkinFiles.NewSkinFolder(sp, pack.Length > 0 ? pack : null, name);
            _wzName = "";
            _wzDiffuse = null;
            _mode = Mode.Browse;
            _selSpecies = sp;
            _selId = sp.name + "|" + SkinScanner.Rel(folder);
            SkinModBase.Reload();   // re-renders the panel
            if (!IsOpen) yield break;
            Refresh();
            string diffuse = Directory.GetFiles(folder, "diffuse*.png").FirstOrDefault();
            SkinFiles.OpenFolder(folder, diffuse);
            Toast("'" + name + "' created - it's in the nursery now. Paint the diffuse, save, press Reload.", Theme.GreenLight, 10f);
        }
    }
}
