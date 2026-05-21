using UnityEngine;

// Per-mode pan/zoom state + input handling, layered on top of PanZoomView
// Modes hold one of these and forward their lifecycle calls (Configure / Tick / HandleInput
// / DrawTexture). Encapsulates the input event plumbing, raw-pixel bounds computation,
// scroll-wheel zoom, two-finger pinch, and tab-strip gating that used to live in
// SlideshowMode.
public class PanZoomController
{
    public float wheelZoomStep = 0.25f;

    readonly PanZoomView _view = new PanZoomView();
    bool _initialized;
    Rect _displayRect;          // display area in raw pixels — set by Configure()

    // Input state.
    Vector2 _lastMousePos;
    bool _dragging;
    bool _pinching;
    float _pinchPrevSeparation;
    Vector2 _pinchPrevCenter;

    public PanZoomView View => _view;
    public Rect DisplayRect => _displayRect;
    public bool IsInitialized => _initialized;

    // Mark the view as needing reset on the next Configure call. Call when the
    // content changes (e.g. slideshow advances to a new texture).
    public void Invalidate() { _initialized = false; }
    // Configure with a mode-area rect in scaled GUI coords; the controller converts
    // to raw pixels using the SparkDemo's UI scale factor. Display rect = full mode area.
    public Rect Configure(Rect bounds, SparkDemo controller, int contentW, int contentH)
    {
        float scale = controller.UiScaleFactor;
        Rect raw = new Rect(bounds.x * scale, bounds.y * scale, bounds.width * scale, bounds.height * scale);
        return Configure(raw, contentW, contentH);
    }
    // Configure with an explicit display rect in raw pixels. Use this when the mode
    // wants the pan/zoom area to be a sub-rect of bounds (e.g. mipmap's 2:1 fit).
    public Rect Configure(Rect displayRect, int contentW, int contentH)
    {
        _displayRect = displayRect;
        if (!_initialized)
        {
            int rw = Mathf.Max(1, Mathf.RoundToInt(_displayRect.width));
            int rh = Mathf.Max(1, Mathf.RoundToInt(_displayRect.height));
            _view.Reset(rw, rh, contentW, contentH);
            _initialized = true;
        }
        return _displayRect;
    }

    // Current pan/zoom UV transform for content of the given natural dimensions.
    // <c>uvOff</c> is the UV at the top-left of the display rect; <c>uvScale</c> is UV delta
    // per screen pixel (multiply by displayRect.width/height for the full uv range).
    public void ComputeUV(int contentW, int contentH, out Vector2 uvOff, out Vector2 uvScale)
    {
        int rw = Mathf.Max(1, Mathf.RoundToInt(_displayRect.width));
        int rh = Mathf.Max(1, Mathf.RoundToInt(_displayRect.height));
        _view.ComputeTexCoords(rw, rh, contentW, contentH, out uvOff, out uvScale);
    }

    // Convenience: draw a texture at the display rect with the current pan/zoom
    // transform. The texture's wrap mode determines what happens past the edges — Repeat
    // tiles, Clamp stretches the edge texels. Reset GUI.matrix locally so the draw happens
    // in raw pixel coords.
    public void DrawTexture(Texture texture)
    {
        if (texture == null || !_initialized) return;
        ComputeUV(texture.width, texture.height, out Vector2 uvOff, out Vector2 uvScale);
        int rw = Mathf.Max(1, Mathf.RoundToInt(_displayRect.width));
        int rh = Mathf.Max(1, Mathf.RoundToInt(_displayRect.height));

        Matrix4x4 prev = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        Rect uv = new Rect(uvOff.x, uvOff.y, uvScale.x * rw, uvScale.y * rh);
        GUI.DrawTextureWithTexCoords(_displayRect, texture, uv, alphaBlend: false);
        GUI.matrix = prev;
    }

    // Like <see cref="DrawTexture"/> but fills areas outside the texture's [0,1]²
    // UV range with <paramref name="borderColor"/> instead of repeating/edge-stretching.
    // Implemented by clipping the texture draw to the valid sub-rect and filling the rest
    // with a solid color — no custom shader required.
    public void DrawTextureClampToBorder(Texture texture, Color borderColor)
    {
        if (texture == null || !_initialized) return;
        ComputeUV(texture.width, texture.height, out Vector2 uvOff, out Vector2 uvScale);
        int rw = Mathf.Max(1, Mathf.RoundToInt(_displayRect.width));
        int rh = Mathf.Max(1, Mathf.RoundToInt(_displayRect.height));

        float totalUvW = uvScale.x * rw;
        float totalUvH = uvScale.y * rh;

        Matrix4x4 prevMat = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;

        // Border fill behind everything.
        Color prevCol = GUI.color;
        GUI.color = borderColor;
        GUI.DrawTexture(_displayRect, Texture2D.whiteTexture);
        GUI.color = prevCol;

        // Compute the t-range within displayRect where uv is in [0,1] on both axes.
        if (totalUvW > 0f && totalUvH > 0f)
        {
            // X axis: standard interpretation. uvOff.x = U at screen left of displayRect; U
            // grows monotonically with t (left to right).
            float tMinX = Mathf.Clamp01(-uvOff.x / totalUvW);
            float tMaxX = Mathf.Clamp01((1f - uvOff.x) / totalUvW);

            // Y axis: Unity's GUI.DrawTextureWithTexCoords treats Rect.y as V at the source
            // region's screen *bottom*, and V increases going *up*. So at screen_t (0=top,
            // 1=bottom), V = uvOff.y + (1 - t) * totalUvH. V drops to 1 at the *top* of the
            // visible texture, V drops to 0 at the *bottom*.
            float tMinY = Mathf.Clamp01((uvOff.y + totalUvH - 1f) / totalUvH);
            float tMaxY = Mathf.Clamp01((uvOff.y + totalUvH) / totalUvH);

            if (tMaxX > tMinX && tMaxY > tMinY)
            {
                Rect screenSub = new Rect(
                    _displayRect.x + tMinX * _displayRect.width,
                    _displayRect.y + tMinY * _displayRect.height,
                    (tMaxX - tMinX) * _displayRect.width,
                    (tMaxY - tMinY) * _displayRect.height);

                // uvSub.y = V at the sub-rect's screen *bottom* = uvOff.y + (1 - tMaxY)*totalUvH.
                // When tMaxY hits the clamp, this resolves to 0 (V at bottom of texture).
                Rect uvSub = new Rect(
                    uvOff.x + tMinX * totalUvW,
                    uvOff.y + (1f - tMaxY) * totalUvH,
                    (tMaxX - tMinX) * totalUvW,
                    (tMaxY - tMinY) * totalUvH);

                GUI.DrawTextureWithTexCoords(screenSub, texture, uvSub, alphaBlend: false);
            }
        }

        GUI.matrix = prevMat;
    }

    // Lerp scale toward target and reconcile offset around the pivot.
    public void Tick(float dt, int contentW, int contentH)
    {
        if (!_initialized || _displayRect.width <= 0f) return;
        _view.Tick(dt,
                   Mathf.Max(1, Mathf.RoundToInt(_displayRect.width)),
                   Mathf.Max(1, Mathf.RoundToInt(_displayRect.height)),
                   contentW, contentH);
    }

    // Process mouse drag, scroll-wheel zoom, and two-finger pinch. Should be called
    // from <c>OnGUIForeground</c> AFTER drawing any buttons so they consume their own clicks
    // first.
    public void HandleInput(SparkDemo controller)
    {
        Event e = Event.current;
        if (e == null) return;

        // The mode runs under the controller's scaled GUI.matrix, which means
        // Event.current.mousePosition (and its deltas) come through divided by the UI scale.
        // Reset to identity so the deltas we feed PanZoomView are raw pixels — same units as
        // _displayRect — and 1 mouse pixel = 1 view pixel as expected.
        Matrix4x4 prev = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        try
        {
            HandleInputInner(controller, e);
        }
        finally
        {
            GUI.matrix = prev;
        }
    }

    void HandleInputInner(SparkDemo controller, Event e)
    {

        int rw = Mathf.Max(1, Mathf.RoundToInt(_displayRect.width));
        int rh = Mathf.Max(1, Mathf.RoundToInt(_displayRect.height));

        // Disable single-finger drag while multi-touch is active so the pinch handler
        // owns the gesture cleanly (Android's first touch is also reported as a mouse).
        bool multiTouch = Input.touchCount >= 2;
        if (multiTouch) _dragging = false;

        Vector2 mouse = e.mousePosition;
        bool insideView = _displayRect.Contains(mouse);

        if (!multiTouch && e.type == EventType.MouseDown && e.button == 0 && insideView && e.type != EventType.Used)
        {
            _dragging = true;
            _lastMousePos = mouse;
            e.Use();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            _dragging = false;
        }
        else if (!multiTouch && e.type == EventType.MouseDrag && _dragging)
        {
            Vector2 delta = mouse - _lastMousePos;
            _view.Pan(delta.x, -delta.y);
            _lastMousePos = mouse;
            e.Use();
        }
        else if (e.type == EventType.ScrollWheel && insideView)
        {
            float pivotX = mouse.x - _displayRect.x;
            float pivotY = _displayRect.height - (mouse.y - _displayRect.y);
            _view.Zoom(e.delta.y * wheelZoomStep, pivotX, pivotY, rw, rh);
            e.Use();
        }

        if (Input.touchCount == 2)
        {
            var t0 = Input.GetTouch(0);
            var t1 = Input.GetTouch(1);
            // Touches are bottom-left; convert to top-left for the displayRect tests.
            Vector2 p0 = new Vector2(t0.position.x, Screen.height - t0.position.y);
            Vector2 p1 = new Vector2(t1.position.x, Screen.height - t1.position.y);
            Vector2 center = (p0 + p1) * 0.5f;
            float separation = (p1 - p0).magnitude;

            if (!_pinching || t0.phase == TouchPhase.Began || t1.phase == TouchPhase.Began)
            {
                _pinching = true;
                _pinchPrevSeparation = separation;
                _pinchPrevCenter = center;
            }
            else
            {
                if (_pinchPrevSeparation > 0f && _displayRect.Contains(center))
                {
                    // Matches example.cpp: target_scale *= old / new. Spreading the fingers
                    // (separation > prev) yields ratio < 1, which decreases scale = zooms in.
                    float ratio = _pinchPrevSeparation / separation;
                    float pivotX = center.x - _displayRect.x;
                    float pivotY = _displayRect.height - (center.y - _displayRect.y);
                    _view.Pinch(ratio, pivotX, pivotY, rw, rh);
                }
                Vector2 panDelta = center - _pinchPrevCenter;
                if (panDelta.sqrMagnitude > 0f)
                    _view.Pan(panDelta.x, -panDelta.y);

                _pinchPrevSeparation = separation;
                _pinchPrevCenter = center;
            }
        }
        else
        {
            _pinching = false;
        }
    }

    // Snap to 1:1 (one screen pixel per content pixel).
    public void OneToOne()
    {
        _view.targetScale = 1f;
        _view.pivot = Vector2.zero;
    }

    // Reset to "fit content into display rect" — same as the initial state.
    public void ResetFit(int contentW, int contentH)
    {
        _view.Reset(Mathf.Max(1, Mathf.RoundToInt(_displayRect.width)),
                    Mathf.Max(1, Mathf.RoundToInt(_displayRect.height)),
                    contentW, contentH);
    }
}
