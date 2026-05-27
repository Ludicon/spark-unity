using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// 3D object viewer. Loads a glTF/.glb at runtime from StreamingAssets via glTFast; its JPEG/PNG
// textures are decoded and Spark-encoded on device by SparkGltfastAddon (an IDefaultImageFormatLoader
// glTFast add-on). Owns its own Camera, directional Light, and the loaded instance — all parented
// under the mode's transform and lazily created on first Activate. Orbit / pan / pinch-zoom with
// inertia. No edit-time import, prefab, placeholder, or manifest — everything is runtime.
public class ObjectMode : SparkDemoMode
{
    public override string DisplayName => "glTF";

    [Header("Model")]
    [Tooltip("StreamingAssets-relative path to the .gltf/.glb loaded at runtime, e.g. \"Models/FlightHelmet-jpeg.glb\". For a .gltf, its .bin and image files must sit next to it (glTFast resolves them relative to the .gltf). Textures are Spark-encoded on device by SparkGltfastAddon.")]
    public string streamingAssetsModelPath = "Models/FlightHelmet-jpeg.glb";

    [Header("View")]
    [Tooltip("Auto-orbit yaw rate (degrees/sec) used until the user touches the view. Stops on first drag/pinch/scroll.")]
    public float autoOrbitSpeed = 20f;

    [Tooltip("Initial camera distance. Auto-fitted to the spawned model's bounding sphere on Activate; respects [minDistance, maxDistance].")]
    public float cameraDistance = 3.2f;

    [Tooltip("Drag sensitivity in degrees of orbit per screen pixel.")]
    public float dragSensitivity = 0.3f;

    [Tooltip("How quickly drag inertia decays per second (higher = stops faster).")]
    public float dragInertiaDecay = 4f;

    [Tooltip("How quickly pinch-zoom inertia decays per second (higher = stops faster).")]
    public float zoomInertiaDecay = 6f;

    [Tooltip("Max coasting angular speed (deg/s).")]
    public float maxAngularSpeed = 240f;

    [Tooltip("Max coasting zoom speed (world units/s).")]
    public float maxZoomSpeed = 4f;

    [Tooltip("Closest the camera can get to the model via pinch / wheel zoom.")]
    public float minDistance = 0.3f;

    [Tooltip("Farthest the camera can get from the model via pinch / wheel zoom.")]
    public float maxDistance = 12f;

    [Tooltip("How quickly pan inertia decays per second (higher = stops faster).")]
    public float panInertiaDecay = 5f;

    [Tooltip("Max coasting pan speed in world units per second.")]
    public float maxPanSpeed = 4f;

    [Header("Lighting")]
    [Tooltip("Sky/environment ambient intensity. Unity defaults to 1.0 which washes everything out; ~0.3 lets the directional light's shape read.")]
    [Range(0f, 2f)]
    public float ambientIntensity = 0.3f;

    [Tooltip("Directional key-light intensity.")]
    [Range(0f, 4f)]
    public float keyLightIntensity = 1.5f;

    [Tooltip("Shadow strength on the key light (0 = no shadow, 1 = full).")]
    [Range(0f, 1f)]
    public float shadowStrength = 0.85f;

    Camera _camera;
    Light _light;
    GameObject _instance;
    GLTFast.GltfImport _gltf;       // kept alive while the runtime-loaded model exists; disposed in OnDestroy
    bool _runtimeLoadStarted;
    string _status;

    // Orbit state — yaw around world-Y, pitch around camera-local-X (clamped to ±85°).
    Vector2 _yawPitch = new Vector2(30f, 20f);
    bool _userInteracted;
    bool _dragging;
    Vector2 _lastDrag;
    bool _pinching;
    float _pinchPrev;
    Vector2 _angularVel;
    float _zoomVel;

    // Pan state: right-mouse-drag or two-finger center-of-mass translation moves
    // _modelCenter. _panVel coasts after release like the orbit/zoom inertia.
    bool _panning;
    Vector2 _lastPan;
    Vector2 _pinchPrevCenter;
    Vector3 _panVel;

    // Model bounding sphere — used as the orbit target and to auto-fit the camera.
    Vector3 _modelCenter = Vector3.zero;
    float _modelRadius = 1f;        // replaced once the model loads

    // Texture stats for the overlay, computed once after load (the Spark-compressed textures are
    // owned by glTFast and freed via _gltf.Dispose()).
    int _texCount;
    long _texVMem;

    public override void Activate()
    {
        BuildScene();          // kicks off the async runtime load on first activation
        ConfigureLighting();
    }

    void BuildScene()
    {
        if (_camera == null)
        {
            var camGo = new GameObject("ObjectMode_Camera");
            camGo.transform.SetParent(transform, worldPositionStays: false);
            _camera = camGo.AddComponent<Camera>();
            _camera.clearFlags      = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.06f, 0.06f, 0.08f);
            _camera.fieldOfView     = 35f;
            _camera.nearClipPlane   = 0.02f;
            _camera.farClipPlane    = 50f;
            // Sit above any pre-existing Camera.main; the SolidColor clear ensures we don't
            // leak whatever the other camera last drew.
            _camera.depth = 100f;
        }
        _camera.enabled = true;

        if (_light == null)
        {
            var lightGo = new GameObject("ObjectMode_KeyLight");
            lightGo.transform.SetParent(transform, worldPositionStays: false);
            _light = lightGo.AddComponent<Light>();
            _light.type      = LightType.Directional;
            _light.intensity = 1.2f;
            _light.color     = Color.white;
            _light.transform.rotation = Quaternion.Euler(45f, 30f, 0f);
        }
        _light.enabled = true;

        if (_instance == null)
        {
            // An empty root the glTFast scene gets instantiated under once the async load
            // completes. Bounds + camera-fit run in StartRuntimeLoad, after instantiation.
            _instance = new GameObject("ObjectMode_RuntimeModel");
            _instance.transform.SetParent(transform, worldPositionStays: false);
            _modelCenter = _instance.transform.position;
            _modelRadius = 1f;
            StartRuntimeLoad(_instance.transform);
        }
        _instance.SetActive(true);
    }

    void ConfigureLighting()
    {
        RenderSettings.ambientIntensity = ambientIntensity;
        if (_light != null)
        {
            _light.intensity        = keyLightIntensity;
            _light.shadows          = LightShadows.Soft;
            _light.shadowResolution = UnityEngine.Rendering.LightShadowResolution.VeryHigh;
            _light.shadowBias       = 0.02f;
            _light.shadowNormalBias = 0.4f;
            _light.shadowStrength   = shadowStrength;
            _light.shadowNearPlane  = 0.05f;
        }
        QualitySettings.shadowDistance = Mathf.Max(2f, _modelRadius * 8f);
        QualitySettings.shadowCascades = 1;
    }

    public override void Deactivate()
    {
        if (_camera != null)   _camera.enabled = false;
        if (_light != null)    _light.enabled = false;
        if (_instance != null) _instance.SetActive(false);
    }

    void OnDestroy()
    {
        if (_gltf != null) { _gltf.Dispose(); _gltf = null; }   // frees glTFast-imported textures/materials/meshes
        if (_instance != null)        Destroy(_instance);
        if (_camera != null && _camera.gameObject != null) Destroy(_camera.gameObject);
        if (_light != null  && _light.gameObject  != null) Destroy(_light.gameObject);
    }

    /// <summary>Load the glTF from StreamingAssets at runtime via glTFast and instantiate it
    /// under <paramref name="parent"/>. async void (fire-and-forget) is the standard way to
    /// drive glTFast's Task API from a MonoBehaviour; we keep the GltfImport alive in _gltf so
    /// its textures/materials aren't disposed, and tear it down in OnDestroy. Bounds + camera-fit
    /// run here, after instantiation, since they need the loaded geometry.</summary>
    async void StartRuntimeLoad(Transform parent)
    {
        if (_runtimeLoadStarted) return;
        _runtimeLoadStarted = true;
        _status = "Loading…";

        try
        {
            string url = System.IO.Path.Combine(Application.streamingAssetsPath, streamingAssetsModelPath);
            var gltf = new GLTFast.GltfImport();

            if (!await gltf.Load(url))
            {
                _status = $"glTF load failed: {streamingAssetsModelPath}";
                Debug.LogError($"[ObjectMode] {_status}");
                gltf.Dispose();
                return;
            }

            // The mode may have been torn down while we awaited.
            if (parent == null || _instance == null)
            {
                gltf.Dispose();
                return;
            }

            if (!await gltf.InstantiateMainSceneAsync(parent))
            {
                _status = "glTF instantiate failed";
                Debug.LogError($"[ObjectMode] {_status}");
                gltf.Dispose();
                return;
            }

            if (_instance == null) { gltf.Dispose(); return; }

            _gltf = gltf;   // keep alive; Dispose() in OnDestroy frees the textures/materials
            ComputeModelBounds();
            _camera.nearClipPlane = Mathf.Max(0.001f, _modelRadius * 0.01f);
            _camera.farClipPlane  = Mathf.Max(50f, _modelRadius * 100f);
            FitCameraToModel();
            ComputeTextureStats();
            _status = null;
            Debug.Log($"[ObjectMode] Runtime-loaded {streamingAssetsModelPath} (radius {_modelRadius:F2})");
        }
        catch (System.Exception e)
        {
            _status = $"glTF load error: {e.Message}";
            Debug.LogException(e);
        }
    }

    void ComputeModelBounds()
    {
        var rens = _instance.GetComponentsInChildren<Renderer>();
        if (rens.Length == 0) { _modelCenter = _instance.transform.position; _modelRadius = 1f; return; }
        Bounds b = rens[0].bounds;
        for (int i = 1; i < rens.Length; i++) b.Encapsulate(rens[i].bounds);
        _modelCenter = b.center;
        _modelRadius = b.extents.magnitude;
    }

    // Pick a camera distance that fits the bounding sphere into the vertical FoV
    // with a small margin. Clamps to [minDistance, maxDistance].
    void FitCameraToModel()
    {
        float halfFov = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
        cameraDistance = Mathf.Clamp(_modelRadius / Mathf.Sin(halfFov) * 1.15f, minDistance, maxDistance);
    }

    // Sum the GPU memory of the loaded model's textures
    void ComputeTextureStats()
    {
        _texCount = 0;
        _texVMem = 0;
        if (_instance == null) return;

        var seen = new HashSet<Texture>();
        foreach (var r in _instance.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) continue;
                foreach (var id in mat.GetTexturePropertyNameIDs())
                {
                    var t = mat.GetTexture(id);
                    if (t == null || !seen.Add(t)) continue;
                    _texCount++;
                    _texVMem += GraphicsFormatUtility.ComputeMipChainSize(t.width, t.height, t.graphicsFormat, t.mipmapCount);
                }
            }
        }
    }

    public override void OnTick()
    {
        if (_instance == null || _camera == null || !_instance.activeSelf) return;

        float dt = Time.unscaledDeltaTime;

        if (!_userInteracted)
            _yawPitch.x += dt * autoOrbitSpeed;

        if (!_dragging && _angularVel.sqrMagnitude > 1e-4f)
        {
            _yawPitch += _angularVel * dt;
            _angularVel *= Mathf.Exp(-dragInertiaDecay * dt);
        }
        if (!_pinching && Mathf.Abs(_zoomVel) > 1e-4f)
        {
            cameraDistance += _zoomVel * dt;
            _zoomVel *= Mathf.Exp(-zoomInertiaDecay * dt);
        }
        if (!_panning && !_pinching && _panVel.sqrMagnitude > 1e-4f)
        {
            _modelCenter += _panVel * dt;
            _panVel *= Mathf.Exp(-panInertiaDecay * dt);
        }

        UpdateCameraTransform();
    }

    // Orbit the camera on a sphere of radius `cameraDistance` around
    // `_modelCenter` (the loaded model's bounding-sphere center). World-space
    // positioning so the camera tracks even if the model has off-center geometry.
    void UpdateCameraTransform()
    {
        _yawPitch.y = Mathf.Clamp(_yawPitch.y, -85f, 85f);
        cameraDistance = Mathf.Clamp(cameraDistance, minDistance, maxDistance);

        float yaw   = _yawPitch.x * Mathf.Deg2Rad;
        float pitch = _yawPitch.y * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch),
                                  Mathf.Sin(pitch),
                                 -Mathf.Cos(yaw) * Mathf.Cos(pitch));
        _camera.transform.position = _modelCenter + dir * cameraDistance;
        _camera.transform.LookAt(_modelCenter, Vector3.up);
    }

    // No OnGUIBackground, the 3D camera fills the screen behind the IMGUI overlay.
    public override void OnGUIForeground(Rect bounds)
    {
        var lines = new List<string>();
        lines.Add($"<b>{System.IO.Path.GetFileName(streamingAssetsModelPath)}</b>");
        if (_texCount > 0)
            lines.Add($"{_texCount} textures   VMem {FormatBytes(_texVMem)}");
        if (!string.IsNullOrEmpty(_status)) lines.Add(_status);

        if (lines.Count > 0)
        {
            float pad = 6f;
            var style = OverlayStyle();
            var content = new GUIContent(string.Join("\n", lines));
            var size = style.CalcSize(content);
            var rect = new Rect(bounds.x + pad, bounds.y + pad, size.x + pad * 2, size.y + pad * 2);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + pad, rect.y + pad, rect.width, rect.height), content, style);
        }

        HandleOrbitInput();
    }

    // Convert a screen-pixel delta into a world-space pan vector aligned with the
    // camera's right/up. The mapping is the exact inverse of the perspective projection at
    // the current zoom — a one-screen-height drag shifts the pivot by the full visible
    // world height at the orbit distance, so dragging matches the cursor at any zoom.
    Vector3 ComputeWorldPan(Vector2 screenDelta)
    {
        if (_camera == null) return Vector3.zero;
        float screenH = Mathf.Max(1, Screen.height);
        float worldPerPixel = 2f * cameraDistance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / screenH;
        Vector3 right = _camera.transform.right;
        Vector3 up    = _camera.transform.up;
        // "Drag the object" convention: drag right pushes the model right, which means the
        // *pivot* (orbit target) moves left in world space; the camera follows the pivot,
        // and the unchanged model geometry ends up to the right of the new view direction.
        return -right * (screenDelta.x * worldPerPixel) + up * (screenDelta.y * worldPerPixel);
    }

    // Orbit + zoom input. Mouse-drag / single-touch-drag orbits; scroll wheel and
    // two-finger pinch zoom. Drag-the-object convention (drag right pushes the model right,
    // camera orbits opposite). Inertia captured from per-event velocities, clamped to
    // `maxAngularSpeed` / `maxZoomSpeed` so a tiny-dt frame can't whirl the camera
    // before the exponential decay catches up.
    void HandleOrbitInput()
    {
        Event e = Event.current;
        if (e == null) return;

        Matrix4x4 prev = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        try
        {
            bool multiTouch = Input.touchCount >= 2;
            if (multiTouch) _dragging = false;

            Vector2 mouse = e.mousePosition;

            if (!multiTouch && e.type == EventType.MouseDown && e.button == 0)
            {
                _dragging       = true;
                _userInteracted = true;
                _lastDrag       = mouse;
                _angularVel     = Vector2.zero;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                _dragging = false;
            }
            else if (!multiTouch && e.type == EventType.MouseDrag && e.button == 0 && _dragging)
            {
                Vector2 delta = mouse - _lastDrag;
                Vector2 deltaDeg = new Vector2(-delta.x, delta.y) * dragSensitivity;
                _yawPitch += deltaDeg;
                Vector2 v = deltaDeg / Mathf.Max(1e-3f, Time.unscaledDeltaTime);
                if (v.magnitude > maxAngularSpeed)
                    v = v.normalized * maxAngularSpeed;
                _angularVel = v;
                _lastDrag = mouse;
                e.Use();
            }
            // Right-mouse-button: pan. button == 1 is right, button == 2 is middle — both
            // are common conventions for "drag the camera" in 3D viewers; map both to pan.
            else if (!multiTouch && e.type == EventType.MouseDown && (e.button == 1 || e.button == 2))
            {
                _panning        = true;
                _userInteracted = true;
                _lastPan        = mouse;
                _panVel         = Vector3.zero;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && (e.button == 1 || e.button == 2))
            {
                _panning = false;
            }
            else if (!multiTouch && e.type == EventType.MouseDrag && _panning)
            {
                Vector2 delta = mouse - _lastPan;
                Vector3 worldPan = ComputeWorldPan(delta);
                _modelCenter += worldPan;
                _panVel = Vector3.ClampMagnitude(
                    worldPan / Mathf.Max(1e-3f, Time.unscaledDeltaTime), maxPanSpeed);
                _lastPan = mouse;
                e.Use();
            }
            else if (e.type == EventType.ScrollWheel)
            {
                cameraDistance += e.delta.y * 0.15f * cameraDistance;
                _userInteracted = true;
                e.Use();
            }

            if (Input.touchCount == 2)
            {
                var t0 = Input.GetTouch(0);
                var t1 = Input.GetTouch(1);
                float separation = (t1.position - t0.position).magnitude;
                // Convert from bottom-left touch coords to top-left GUI coords so the pan
                // math matches the mouse path.
                Vector2 centerBL = (t0.position + t1.position) * 0.5f;
                Vector2 center   = new Vector2(centerBL.x, Screen.height - centerBL.y);

                if (!_pinching || t0.phase == TouchPhase.Began || t1.phase == TouchPhase.Began)
                {
                    _pinching        = true;
                    _userInteracted  = true;
                    _pinchPrev       = separation;
                    _pinchPrevCenter = center;
                    _zoomVel         = 0f;
                    _panVel          = Vector3.zero;
                }
                else
                {
                    // Spread → zoom.
                    if (_pinchPrev > 0f && separation > 0f)
                    {
                        float prevDist = cameraDistance;
                        cameraDistance *= _pinchPrev / separation;
                        _zoomVel = Mathf.Clamp(
                            (cameraDistance - prevDist) / Mathf.Max(1e-3f, Time.unscaledDeltaTime),
                            -maxZoomSpeed, maxZoomSpeed);
                        _pinchPrev = separation;
                    }
                    // Center-of-mass translation → pan.
                    Vector2 panDelta = center - _pinchPrevCenter;
                    if (panDelta.sqrMagnitude > 0f)
                    {
                        Vector3 worldPan = ComputeWorldPan(panDelta);
                        _modelCenter += worldPan;
                        _panVel = Vector3.ClampMagnitude(
                            worldPan / Mathf.Max(1e-3f, Time.unscaledDeltaTime), maxPanSpeed);
                    }
                    _pinchPrevCenter = center;
                }
            }
            else
            {
                _pinching = false;
            }
        }
        finally
        {
            GUI.matrix = prev;
        }
    }

    static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024f:F1} KB";
        return $"{n / (1024f * 1024f):F2} MB";
    }

    static GUIStyle s_overlayStyle;
    static GUIStyle OverlayStyle()
    {
        if (s_overlayStyle == null)
            s_overlayStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
        return s_overlayStyle;
    }
}
