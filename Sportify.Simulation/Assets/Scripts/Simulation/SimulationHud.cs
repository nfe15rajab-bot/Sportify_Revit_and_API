using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// Text and panels burned into the video: a title bar, the running clock and
    /// counters, a feed of crossings, full-screen title/summary cards, and
    /// labels floating in the scene.
    ///
    /// Built from 3D TextMesh objects parented to the camera rather than a UI
    /// canvas or IMGUI: a canvas would need the ugui package added to the
    /// project, and IMGUI draws to the screen instead of into the render
    /// texture the video is recorded from. Anything under the camera is simply
    /// part of the render.
    /// </summary>
    public class SimulationHud
    {
        const int GlyphPx = 64;                 // raster size of the dynamic font glyphs
        const float DistanceM = 3f;             // how far in front of the camera the HUD sits

        // A line of TextMesh text is about characterSize * fontSize * this many
        // world units tall. Measured from rendered frames.
        const float TextHeightFactor = 0.1f;

        // Draw order. Transparent objects are otherwise sorted by distance from
        // the camera, which puts a big centred panel in front of off-centre
        // text and dims it - so every layer gets an explicit order instead.
        const int OrderShadow = 5;      // outline behind a label in the scene
        const int OrderLabel = 6;       // labels in the scene
        const int OrderBar = 10;        // HUD strips
        const int OrderHudText = 12;
        const int OrderCard = 20;       // full-screen card
        const int OrderCardText = 22;

        static readonly Color Ink = new Color(0.95f, 0.96f, 0.98f);
        static readonly Color Muted = new Color(0.66f, 0.70f, 0.76f);
        public static readonly Color CrossingText = new Color(1.00f, 0.42f, 0.40f);   // legible on dark panels
        static readonly Color Outline = new Color(0.04f, 0.05f, 0.08f, 0.95f);

        readonly Camera _cam;
        readonly Font _font;
        readonly Transform _root;
        readonly int _widthPx, _heightPx;
        readonly float _worldPerPx;

        readonly TextMesh _title, _caseStudy, _clock, _counts, _feed, _legend, _preliminary;
        readonly GameObject _preliminaryBar;
        readonly int _preliminaryChars;
        readonly GameObject _card;
        readonly TextMesh _cardHead, _cardSub, _cardBody;
        readonly Transform _cardBack;
        readonly float _cardBackHeight, _cardBackTop;      // the panel's height and top edge (px from the middle), for ShowCard's height scale
        readonly float _cardHeadSize, _cardBodySize;       // the type sizes the card was built with, for ShowCard's scales

        readonly GameObject _arrowPanel;
        readonly Transform _arrowPivot;

        /// <param name="title">Heading in the top bar.</param>
        /// <param name="shotLegend">The ball simulation's legend of shot colours; other analyses set their own with SetLegend.</param>
        public SimulationHud(Camera cam, int widthPx, int heightPx, string title = "Ball trajectory simulation", bool shotLegend = true)
        {
            _cam = cam;
            _widthPx = widthPx;
            _heightPx = heightPx;
            _font = LoadFont();

            var halfHeight = DistanceM * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _worldPerPx = 2f * halfHeight / heightPx;

            _root = new GameObject("HUD").transform;
            _root.SetParent(cam.transform, false);

            var m = 44f;   // outer margin in pixels
            Bar("TopBar", 0f, heightPx * 0.5f - 62f, widthPx, 124f, new Color(0f, 0f, 0f, 0.45f), OrderBar);
            Bar("BottomBar", 0f, -heightPx * 0.5f + 90f, widthPx, 180f, new Color(0f, 0f, 0f, 0.45f), OrderBar);

            _title = Text("Title", 34f, TextAnchor.UpperLeft, TextAlignment.Left, -widthPx * 0.5f + m, heightPx * 0.5f - 22f, Ink, OrderHudText);
            _title.text = "<b>" + title + "</b>";
            _caseStudy = Text("CaseStudy", 21f, TextAnchor.UpperLeft, TextAlignment.Left, -widthPx * 0.5f + m, heightPx * 0.5f - 68f, Muted, OrderHudText);

            _clock = Text("Clock", 27f, TextAnchor.UpperCenter, TextAlignment.Center, 0f, heightPx * 0.5f - 26f, Ink, OrderHudText);
            _counts = Text("Counts", 27f, TextAnchor.UpperRight, TextAlignment.Right, widthPx * 0.5f - m, heightPx * 0.5f - 26f, Ink, OrderHudText);

            _feed = Text("Feed", 22f, TextAnchor.LowerLeft, TextAlignment.Left, -widthPx * 0.5f + m, -heightPx * 0.5f + 26f, Ink, OrderHudText);
            _legend = Text("Legend", 19f, TextAnchor.LowerRight, TextAlignment.Right, widthPx * 0.5f - m, -heightPx * 0.5f + 22f, Muted, OrderHudText);

            // A strip under the top bar for a warning that has to stay on screen in every scene (a result that rests on unconfirmed inputs).
            var stripW = widthPx - 300f;
            Bar("PreliminaryBar", -150f, heightPx * 0.5f - 124f - 34f, stripW, 68f, new Color(1f, 0.78f, 0.14f, 0.93f), OrderBar);
            _preliminaryBar = _root.Find("PreliminaryBar").gameObject;
            _preliminary = Text("Preliminary", 21f, TextAnchor.UpperLeft, TextAlignment.Left, -widthPx * 0.5f + m, heightPx * 0.5f - 124f - 8f, new Color(0.10f, 0.07f, 0.0f), OrderHudText);
            _preliminaryChars = Mathf.Max(40, (int)((stripW - 2f * m) / (21f * 0.55f)));
            _preliminaryBar.SetActive(false);
            _preliminary.gameObject.SetActive(false);

            // The four shots every court gets always play the same roles, whatever the sport.
            if (shotLegend) _legend.text =
                Tint("Long shot toward one end", ShotVisual.SlotColors[0]) + "\n" +
                Tint("Long shot toward the other", ShotVisual.SlotColors[1]) + "\n" +
                Tint("Hard flat shot (smash / spike / drive)", ShotVisual.SlotColors[2]) + "\n" +
                Tint("Wide mishit", ShotVisual.SlotColors[3]) + "\n" +
                Tint("Red trail = crossed a boundary", CrossingText) + "\n" +
                Tint("Amber floor = circulation space", new Color(1f, 0.78f, 0.14f));

            _card = new GameObject("Card");
            _card.transform.SetParent(_root, false);
            var cardBack = GameObject.CreatePrimitive(PrimitiveType.Quad);
            cardBack.name = "CardBack";
            SceneBuilder.RemoveCollider(cardBack);
            cardBack.transform.SetParent(_card.transform, false);
            cardBack.transform.localPosition = new Vector3(0f, 0f, DistanceM + 0.04f);
            cardBack.transform.localScale = new Vector3(widthPx * 0.62f * _worldPerPx, heightPx * 0.66f * _worldPerPx, 1f);
            _cardBack = cardBack.transform;
            _cardBackHeight = heightPx * 0.66f;
            _cardBackTop = heightPx * 0.33f;
            var cardRenderer = cardBack.GetComponent<Renderer>();
            cardRenderer.sharedMaterial = SceneBuilder.OverlayMaterial(new Color(0.03f, 0.04f, 0.06f, 0.88f));
            cardRenderer.sortingOrder = OrderCard;

            var cx = -widthPx * 0.27f;
            _cardHead = Text("CardHead", 54f, TextAnchor.UpperLeft, TextAlignment.Left, cx, heightPx * 0.27f, Ink, OrderCardText, _card.transform);
            _cardSub = Text("CardSub", 26f, TextAnchor.UpperLeft, TextAlignment.Left, cx, heightPx * 0.27f - 84f, Muted, OrderCardText, _card.transform);
            _cardBody = Text("CardBody", 27f, TextAnchor.UpperLeft, TextAlignment.Left, cx, heightPx * 0.27f - 150f, Ink, OrderCardText, _card.transform);
            _cardHeadSize = _cardHead.characterSize;
            _cardBodySize = _cardBody.characterSize;
            _card.SetActive(false);

            // A wind-direction arrow under the counters, rotated to the direction the wind blows toward.
            _arrowPanel = new GameObject("WindArrow");
            _arrowPanel.transform.SetParent(_root, false);
            var ax = widthPx * 0.5f - 130f;
            var ay = heightPx * 0.5f - 124f - 110f;
            _arrowPanel.transform.localPosition = new Vector3(ax * _worldPerPx, ay * _worldPerPx, 0f);
            ArrowPart("ArrowBack", _arrowPanel.transform, 0f, 0f, 190f, 190f, 0f, new Color(0f, 0f, 0f, 0.45f), OrderBar + 1);
            _arrowPivot = new GameObject("ArrowPivot").transform;
            _arrowPivot.SetParent(_arrowPanel.transform, false);
            var arrowColor = new Color(0.62f, 0.86f, 1f, 1f);
            ArrowPart("Shaft", _arrowPivot, -14f, 0f, 104f, 15f, 0f, arrowColor, OrderHudText);
            ArrowPart("Head", _arrowPivot, 52f, 0f, 38f, 38f, 45f, arrowColor, OrderHudText);
            _arrowPanel.SetActive(false);
        }

        void ArrowPart(string name, Transform parent, float xPx, float yPx, float widthPx, float heightPx, float zRotationDeg, Color color, int order)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Quad);
            part.name = name;
            SceneBuilder.RemoveCollider(part);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = new Vector3(xPx * _worldPerPx, yPx * _worldPerPx, DistanceM + 0.03f);
            part.transform.localRotation = Quaternion.Euler(0f, 0f, zRotationDeg);
            part.transform.localScale = new Vector3(widthPx * _worldPerPx, heightPx * _worldPerPx, 1f);
            var partRenderer = part.GetComponent<Renderer>();
            partRenderer.sharedMaterial = SceneBuilder.OverlayMaterial(color);
            partRenderer.sortingOrder = order;
        }

        /// <summary>
        /// Shows the wind arrow pointing the way the wind blows. Angles are layout angles (0 = toward the
        /// right edge, 90 = toward the plan's bottom edge); the plan's y axis runs down the screen, so
        /// the on-screen angle is the negative.
        /// </summary>
        public void ShowWindArrow(float towardDeg)
        {
            _arrowPivot.localRotation = Quaternion.Euler(0f, 0f, -towardDeg);
            _arrowPanel.SetActive(true);
        }

        public void HideWindArrow()
        {
            _arrowPanel.SetActive(false);
        }

        /// <summary>Shows a warning strip under the top bar in every scene (two lines at most); an empty text hides it.</summary>
        public void SetPreliminary(string text)
        {
            var on = !string.IsNullOrEmpty(text);
            _preliminaryBar.SetActive(on);
            _preliminary.gameObject.SetActive(on);
            if (!on) return;

            var lines = new List<string>();
            var rest = text;
            while (rest.Length > 0 && lines.Count < 2)
            {
                if (rest.Length <= _preliminaryChars) { lines.Add(rest); rest = ""; break; }
                var cut = rest.LastIndexOf(' ', _preliminaryChars);
                if (cut <= 0) cut = _preliminaryChars;
                lines.Add(rest.Substring(0, cut));
                rest = rest.Substring(cut).TrimStart();
            }
            if (rest.Length > 0) lines[lines.Count - 1] = lines[lines.Count - 1].TrimEnd('.', ' ') + "...";
            _preliminary.text = "<b>" + string.Join("\n", lines) + "</b>";

            // the strip is as tall as its lines
            var stripPx = 16f + 28f * lines.Count;
            _preliminaryBar.transform.localScale = new Vector3((_widthPx - 300f) * _worldPerPx, stripPx * _worldPerPx, 1f);
            _preliminaryBar.transform.localPosition = new Vector3(-150f * _worldPerPx, (_heightPx * 0.5f - 124f - stripPx * 0.5f) * _worldPerPx, DistanceM + 0.06f);
        }

        public void SetLegend(IList<string> lines)
        {
            _legend.text = string.Join("\n", lines);
        }

        public void SetCountsText(string text)
        {
            _counts.text = text;
        }

        /// <summary>Changes the words of a label made by WorldLabel, including its dark outline copy.</summary>
        public static void SetLabelText(TextMesh label, string text)
        {
            if (label == null) return;
            label.text = text;
            var outline = label.transform.Find("Outline");
            if (outline != null)
            {
                var shadow = outline.GetComponent<TextMesh>();
                if (shadow != null) shadow.text = text;
            }
        }

        public static string Tint(string text, Color color)
        {
            return "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + text + "</color>";
        }

        static Font LoadFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", GlyphPx);
            return font;
        }

        // ------------------------------------------------------------------ building blocks

        void Bar(string name, float xPx, float yPx, float widthPx, float heightPx, Color color, int order)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bar.name = name;
            SceneBuilder.RemoveCollider(bar);
            bar.transform.SetParent(_root, false);
            bar.transform.localPosition = new Vector3(xPx * _worldPerPx, yPx * _worldPerPx, DistanceM + 0.06f);
            bar.transform.localScale = new Vector3(widthPx * _worldPerPx, heightPx * _worldPerPx, 1f);
            var barRenderer = bar.GetComponent<Renderer>();
            barRenderer.sharedMaterial = SceneBuilder.OverlayMaterial(color);
            barRenderer.sortingOrder = order;
        }

        TextMesh Text(string name, float sizePx, TextAnchor anchor, TextAlignment alignment,
                      float xPx, float yPx, Color color, int order, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : _root, false);
            go.transform.localPosition = new Vector3(xPx * _worldPerPx, yPx * _worldPerPx, DistanceM);
            var tm = go.AddComponent<TextMesh>();
            Configure(tm, sizePx * _worldPerPx, anchor, alignment, color, order);
            return tm;
        }

        void Configure(TextMesh tm, float heightWorld, TextAnchor anchor, TextAlignment alignment, Color color, int order)
        {
            tm.font = _font;
            tm.fontSize = GlyphPx;
            tm.characterSize = heightWorld / (GlyphPx * TextHeightFactor);
            tm.anchor = anchor;
            tm.alignment = alignment;
            tm.richText = true;
            tm.lineSpacing = 1.1f;
            tm.color = color;

            var meshRenderer = tm.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _font.material;
            meshRenderer.sortingOrder = order;
        }

        // ------------------------------------------------------------------ live HUD

        public void SetCaseStudy(string text)
        {
            _caseStudy.text = text;
        }

        public void SetClock(float simTimeS, float slowMotion)
        {
            // Invariant culture: the text around it is English, so no decimal commas.
            _clock.text = "SLOW MOTION " + slowMotion.ToString("0.##", CultureInfo.InvariantCulture) + "x    t = " +
                          simTimeS.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>Replaces the running clock with a caption, for the sections after the flight.</summary>
        public void SetBanner(string text)
        {
            _clock.text = text;
        }

        public void SetCounts(int shots, int finished, int crossings)
        {
            _counts.text = "SHOTS " + shots + "   FINISHED " + finished + "   " +
                           Tint("CROSSINGS " + crossings, crossings > 0 ? ShotVisual.CrossingColor : Ink);
        }

        public void SetFeed(IList<string> lines)
        {
            _feed.text = string.Join("\n", lines);
        }

        // ------------------------------------------------------------------ cards

        /// <param name="headlineScale">Multiplies the headline's type size (1 = as built): a long headline is set smaller, and wrapped by the caller.</param>
        /// <param name="bodyScale">The same for the body lines.</param>
        /// <param name="panelHeightScale">Multiplies the panel's height, growing it downward from its top edge.</param>
        public void ShowCard(string headline, Color headlineColor, string subtitle, IList<string> bodyLines, float headlineScale = 1f, float bodyScale = 1f, float panelHeightScale = 1f)
        {
            var h = _cardBackHeight * panelHeightScale;
            _cardBack.localScale = new Vector3(_cardBack.localScale.x, h * _worldPerPx, 1f);
            _cardBack.localPosition = new Vector3(0f, (_cardBackTop - h / 2f) * _worldPerPx, _cardBack.localPosition.z);
            _cardHead.characterSize = _cardHeadSize * headlineScale;
            _cardBody.characterSize = _cardBodySize * bodyScale;
            _cardHead.text = headline;
            _cardHead.color = headlineColor;
            _cardSub.text = subtitle;

            var sb = new StringBuilder();
            for (var i = 0; i < bodyLines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(bodyLines[i]);
            }
            _cardBody.text = sb.ToString();
            _card.SetActive(true);
        }

        public void HideCard()
        {
            _card.SetActive(false);
        }

        // ------------------------------------------------------------------ labels in the scene

        /// <summary>
        /// A label standing in the scene, turned to face the (fixed) camera.
        /// A dark copy is drawn just behind it as an outline, so the text reads
        /// on the pale roof and on the dark backdrop alike.
        /// </summary>
        public TextMesh WorldLabel(string text, Vector3 position, float heightWorld, Color color)
        {
            var go = new GameObject("Label_" + text);
            go.transform.position = position;
            go.transform.rotation = _cam.transform.rotation;
            var tm = go.AddComponent<TextMesh>();
            Configure(tm, heightWorld, TextAnchor.MiddleCenter, TextAlignment.Center, color, OrderLabel);
            tm.text = text;

            var shadowGo = new GameObject("Outline");
            shadowGo.transform.SetParent(go.transform, false);
            var offset = heightWorld * 0.07f;
            shadowGo.transform.localPosition = new Vector3(offset, -offset, 0.02f);
            var shadow = shadowGo.AddComponent<TextMesh>();
            Configure(shadow, heightWorld, TextAnchor.MiddleCenter, TextAlignment.Center, Outline, OrderShadow);
            shadow.text = text;

            return tm;
        }
    }
}
