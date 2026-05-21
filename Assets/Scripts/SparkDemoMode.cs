using UnityEngine;

// Abstract base for a SparkDemo mode. Each mode is a MonoBehaviour on a child GameObject of the SparkDemo controller.
// The controller enables/disables the mode GameObjects so inactive modes pay no cost.
public abstract class SparkDemoMode : MonoBehaviour
{
    // Short label shown in the controller's tab strip.
    public abstract string DisplayName { get; }

    // Reference to the parent controller. Set by SparkDemo at Awake.
    public SparkDemo Controller { get; internal set; }

    // Called when this mode becomes the active mode. Allocate resources here.
    public virtual void Activate() { }

    // Called when this mode is replaced by another. Release/pause resources here.
    public virtual void Deactivate() { }

    // Per-frame tick while this mode is active. Called from controller's LateUpdate.
    public virtual void OnTick() { }

    // Draw the demo's background before the tab strip is rendered. Bounds is the full screen area.
    public virtual void OnGUIBackground(Rect bounds) { }

    // Draw the mode's foreground (buttons, overlays, input handlers) after the tab strip. Bounds is
    // the safe rendering area minus the tab strip height.
    public virtual void OnGUIForeground(Rect bounds) { }
}
