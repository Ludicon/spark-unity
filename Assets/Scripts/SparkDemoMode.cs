using UnityEngine;

/// <summary>
/// Abstract base for a SparkDemo mode. Each mode is a MonoBehaviour on a child
/// GameObject of the SparkDemo controller. The controller enables/disables the
/// mode GameObjects so inactive modes pay no cost.
/// </summary>
public abstract class SparkDemoMode : MonoBehaviour
{
    /// <summary>Short label shown in the controller's tab strip.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Reference to the parent controller. Set by SparkDemo at Awake.</summary>
    public SparkDemo Controller { get; internal set; }

    /// <summary>Called when this mode becomes the active mode. Allocate resources here.</summary>
    public virtual void Activate() { }

    /// <summary>Called when this mode is replaced by another. Release/pause resources here.</summary>
    public virtual void Deactivate() { }

    /// <summary>Per-frame tick while this mode is active. Called from controller's LateUpdate.</summary>
    public virtual void OnTick() { }

    /// <summary>
    /// Draw the demo's background before the tab strip is rendered. Bounds is the full screen area.
    /// </summary>
    public virtual void OnGUIBackground(Rect bounds) { }

    /// <summary>
    /// Draw the mode's foreground (buttons, overlays, input handlers) after the tab strip. Bounds is
    /// the safe rendering area minus the tab strip height.
    /// </summary>
    public virtual void OnGUIForeground(Rect bounds) { }
}
