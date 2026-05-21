using UnityEngine;

/// <summary>
/// 2D pan + zoom view transform with a pivot-relative zoom. Ported from the
/// view math in spark/examples/example.cpp so behavior matches the Android/desktop
/// Spark demos. Maintains a smooth target_scale → scale lerp so zoom feels animated.
///
/// Coordinate model (matching the reference):
///   * pivot and offset are in pixels, centered on the screen (origin at the
///     screen's center).
///   * scale is "texture texels per screen pixel" — smaller means more zoomed in
///     (one texel covers more screen pixels). fitScale = max(texW/screenW, texH/screenH).
///   * When a wheel/pinch event happens, set target_scale and pivot. Tick() lerps
///     scale toward target_scale and adjusts offset so the pivot's texcoord is fixed.
///
/// To display the result, call ComputeTexCoords(screenSize, texSize, out uvOffset, out uvScale)
/// and use GUI.DrawTextureWithTexCoords(screenRect, tex, new Rect(uvOffset, uvScale)).
/// </summary>
public class PanZoomView
{
    public Vector2 pivot;       // Pixel coords, centered on screen.
    public Vector2 offset;      // Pixel coords, centered on screen.
    public float   targetScale;
    public float   scale;

    /// <summary>Resets the view so the texture fits the screen (max of either axis ratio).</summary>
    public void Reset(int screenW, int screenH, int texW, int texH)
    {
        float s = Mathf.Max((float)texW / screenW, (float)texH / screenH);
        if (s <= 0f) s = 1f;
        pivot = Vector2.zero;
        offset = Vector2.zero;
        scale = s;
        targetScale = s;
    }

    /// <summary>Drag the view by (dx, dy) pixels. Positive dx moves the image right.</summary>
    public void Pan(float dx, float dy)
    {
        // Reference subtracts mouse delta from offset (mouse-following pan).
        offset.x -= dx;
        offset.y -= dy;
    }

    /// <summary>
    /// Adjust the target scale by a multiplicative factor. zoomDelta > 0 zooms in.
    /// pivotX/Y are mouse pixel coordinates (top-left origin); they're converted to
    /// centered coordinates internally.
    /// </summary>
    public void Zoom(float zoomDelta, float pivotXScreen, float pivotYScreen, int screenW, int screenH)
    {
        targetScale = Mathf.Pow(2f, Mathf.Log(targetScale, 2f) + zoomDelta);
        pivot.x = pivotXScreen - 0.5f * screenW;
        pivot.y = pivotYScreen - 0.5f * screenH;
    }

    /// <summary>
    /// Multiplicative scale change (e.g. from pinch gesture). Use ratio > 1 for zoom-in.
    /// </summary>
    public void Pinch(float ratio, float pivotXScreen, float pivotYScreen, int screenW, int screenH)
    {
        targetScale *= ratio;
        pivot.x = pivotXScreen - 0.5f * screenW;
        pivot.y = pivotYScreen - 0.5f * screenH;
    }

    /// <summary>Lerp scale toward targetScale and reconcile offset so the pivot stays fixed.</summary>
    public void Tick(float timeDelta, int screenW, int screenH, int texW, int texH)
    {
        // Zoom bounds. scale = texels per screen pixel (smaller = more zoomed in).
        //   Zoom-in limit:  show at least 4 texels on the largest screen axis.
        //     visible_texels(axis) = screen_axis * scale  ⇒  minScale = 4 / max(screenW, screenH).
        //   Zoom-out limit: at most 2× the texture extents visible on the limiting axis.
        //     fitScale = max(texW/screenW, texH/screenH) makes the limiting axis show exactly
        //     one texture extent; 2× fitScale shows two.
        int screenMax = Mathf.Max(1, Mathf.Max(screenW, screenH));
        float minScale = 4f / screenMax;
        float fitScale = Mathf.Max((float)texW / Mathf.Max(1, screenW), (float)texH / Mathf.Max(1, screenH));
        // Mathf.Max guards against degenerate cases (tiny texture, big screen) where 2× fit < min.
        float maxScale = Mathf.Max(2f * fitScale, minScale);
        targetScale = Mathf.Clamp(targetScale, minScale, maxScale);

        float oldScale = scale;
        // Reference: scale = lerp(scale, target_scale, 1 - 0.0002^dt)
        scale = Mathf.Lerp(scale, targetScale, 1f - Mathf.Pow(0.0002f, timeDelta));
        scale = Mathf.Clamp(scale, minScale, maxScale);

        // Keep the pivot at the same texcoord across the scale change.
        if (scale > 0f)
        {
            offset.x = (pivot.x + offset.x) * oldScale / scale - pivot.x;
            offset.y = (pivot.y + offset.y) * oldScale / scale - pivot.y;
        }

        // Clamp offset so the texture stays roughly visible.
        float lx = 0.5f * screenW + 0.5f * texW / scale;
        float ly = 0.5f * screenH + 0.5f * texH / scale;
        if (offset.x < -lx) offset.x = -lx;
        if (offset.x >  lx) offset.x =  lx;
        if (offset.y < -ly) offset.y = -ly;
        if (offset.y >  ly) offset.y =  ly;
    }

    /// <summary>
    /// Computes UV offset+scale for sampling the source texture into a full-screen draw.
    /// Mirrors the push-constant math in example.cpp / demo.frag.glsl.
    /// </summary>
    public void ComputeTexCoords(int screenW, int screenH, int texW, int texH, out Vector2 uvOffset, out Vector2 uvScale)
    {
        // push_constants[0..1]: uv offset
        uvOffset.x = (offset.x - 0.5f * screenW + 0.5f * texW / scale) * scale / texW;
        uvOffset.y = (offset.y - 0.5f * screenH + 0.5f * texH / scale) * scale / texH;
        // push_constants[2..3]: uv scale (per-screen-pixel uv delta)
        uvScale.x = scale / texW;
        uvScale.y = scale / texH;
    }
}
