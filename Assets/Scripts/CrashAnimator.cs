using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages the crash game graph animation.
/// </summary>
public class CrashAnimator : MonoBehaviour
{
    [Header("Object References")]
    [Tooltip("Wave image displayed at the end of the graph line.")]
    public RectTransform waveImage;
    [Tooltip("LineRenderer component that draws the graph trail.")]
    public LineRenderer trailRenderer;

    [Header("Graph Settings")]
    [Tooltip("Drawing area size (Width, Height) in UI units.")]
    public Vector2 graphSize = new Vector2(800, 400);
    [Tooltip("Maximum time on the X axis for graph scaling.")]
    public float maxTimeOnXAxis = 10f;
    [Tooltip("Maximum multiplier on the Y axis for graph scaling.")]
    public float maxMultiplierOnYAxis = 10f;

    [Header("Line Colors")]
    [Tooltip("Line color at the start point during gameplay.")]
    public Color normalStartColor = Color.red;
    [Tooltip("Line color at the end point during gameplay.")]
    public Color normalEndColor = Color.green;
    [Tooltip("Line color after a crash.")]
    public Color crashColor = Color.red;

    private Vector2 startPosition;
    private List<Vector3> trailPoints = new List<Vector3>();

    public static CrashAnimator _Instance;

    private void Awake()
    {
        _Instance = this;
    }

    void Start()
    {
        if (waveImage == null || trailRenderer == null)
        {
            Debug.LogError("CrashAnimator is missing required component references!");
            this.enabled = false;
            return;
        }

        startPosition = waveImage.anchoredPosition;
        ResetAnimation();
    }

    /// <summary>
    /// Resets the animation to its initial state and restores the default line color.
    /// </summary>
    public void ResetAnimation()
    {
        waveImage.gameObject.SetActive(false);
        waveImage.anchoredPosition = startPosition;

        if (trailRenderer != null)
        {
            trailRenderer.positionCount = 0;
            trailPoints.Clear();
            trailPoints.Add(waveImage.transform.position);
            trailRenderer.positionCount = 1;
            trailRenderer.SetPosition(0, trailPoints[0]);

            trailRenderer.startColor = normalStartColor;
            trailRenderer.endColor = normalEndColor;
        }
    }

    /// <summary>
    /// Ends the animation and triggers the appropriate visual effect.
    /// </summary>
    /// <param name="didCrash">Whether the round ended in a crash.</param>
    public void EndAnimation(bool didCrash)
    {
        if (didCrash)
        {
            if (trailRenderer != null)
            {
                trailRenderer.startColor = crashColor;
                trailRenderer.endColor = crashColor;
            }
        }
    }

    public void UpdateAnimation(float currentMultiplier, float timeElapsed)
    {
        if (!waveImage.gameObject.activeSelf)
        {
            waveImage.gameObject.SetActive(true);
        }

        float x_normalized = timeElapsed / maxTimeOnXAxis;
        float y_normalized = (currentMultiplier - 1.0f) / (maxMultiplierOnYAxis - 1.0f);

        float newX = startPosition.x + Mathf.Clamp01(x_normalized) * graphSize.x;
        float newY = startPosition.y + Mathf.Clamp01(y_normalized) * graphSize.y;

        waveImage.anchoredPosition = new Vector2(newX, newY);

        Vector3 newWorldPosition = waveImage.transform.position;

        if (trailPoints.Count > 0 && Vector3.Distance(newWorldPosition, trailPoints[trailPoints.Count - 1]) > 0.1f)
        {
            trailPoints.Add(newWorldPosition);
            trailRenderer.positionCount = trailPoints.Count;
            trailRenderer.SetPositions(trailPoints.ToArray());
        }
        else if (trailPoints.Count == 0)
        {
            trailPoints.Add(newWorldPosition);
        }
    }
}
