using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System.Collections.Generic;
using TMPro;

public class FlashcardAnchorManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject flashcardPrefab;
    [SerializeField] private ARCameraManager cameraManager;

    private ARAnchorManager anchorManager;
    private List<ARAnchor> activeAnchors = new List<ARAnchor>();

    private Dictionary<string, string> savedAnchorIds = new Dictionary<string, string>();

    void Awake()
    {
        anchorManager = GetComponent<ARAnchorManager>();
        if(anchorManager == null)
        {
            anchorManager = gameObject.AddComponent<ARAnchorManager>();
            Debug.Log("ARAnchorManager component added to XR Origin");
        }
    }

    void OnEnable()
    {
        if(anchorManager == null)
        {
            anchorManager.anchorsChanged += OnAnchorsChanged;
        }

        if(cameraManager == null)
        {
            cameraManager.frameReceived += OnCameraFrameReceived;
        }
    }

    void OnDisable()
    {
        if(anchorManager != null)
        {
            anchorManager.anchorsChanged -= OnAnchorsChanged;
        }

        if(cameraManager != null)
        {
            cameraManager.frameReceived -= OnCameraFrameReceived;
        }
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        if(anchorManager.subsystem != null && anchorManager.subsystem.running)
        {
            Debug.Log("AR Anchor subsystem is running");
        }
    }

    public async void CreateAnchoredFlashcard(Vector3 position, Quaternion rotation, string flashcardText = "Hello, World!")
    {
        GameObject flashcard = Instantiate(flashcardPrefab, position, rotation);

        TextMeshPro textMesh = flashcard.GetComponentInChildren<TextMeshPro>();
        if(textMesh != null)
        {
            textMesh.text = flashcardText;
        }

        Pose anchorPose = new Pose(position, rotation);
        Result<ARAnchor> result = await anchorManager.TryAddAnchorAsync(anchorPose);
        ARAnchor anchor = result.value;

        if(anchor != null)
        {
            flashcard.transform.SetParent(anchor.transform, false);

            activeAnchors.Add(anchor);

            Debug.Log($"Anchor created with ID: {anchor.trackableId}");

            SaveAnchorId(anchor.trackableId.ToString(), "flashcard_1");
        }
        else
        {
            Debug.LogError("Failed to create anchor - check if ARAnchorManager is properly configured");

            // Fallback: just place the object without anchor
            flashcard.transform.position = position;
        }
    }

    private void OnAnchorsChanged(ARAnchorsChangedEventArgs args)
    {
        foreach (var anchor in args.added)
        {
            Debug.Log($"Anchor added: {anchor.trackableId}, Tracking state: {anchor.trackingState}");
        }

        foreach (var anchor in args.updated)
        {
            if (anchor.trackingState == TrackingState.Tracking)
            {
                SetAnchorVisualState(anchor, true);
            }
            else if (anchor.trackingState == TrackingState.Limited)
            {
                SetAnchorVisualState(anchor, false);
            }
        }

        foreach (var anchor in args.removed)
        {
            Debug.Log($"Anchor removed: {anchor.trackableId}");
            activeAnchors.Remove(anchor);
        }
    }

    private void SetAnchorVisualState(ARAnchor anchor, bool isTracking)
    {
        Renderer[] renderers = anchor.GetComponentsInChildren<Renderer>();
        foreach (var renderer in renderers)
        {
            if (isTracking)
            {
                renderer.material.color = Color.green; // Good tracking
            }
            else
            {
                renderer.material.color = Color.yellow; // Limited tracking
            }
        }
    }

    public void PlaceFlashcardAtCamera(float distanceFromCamera = 1.5f)
    {
        if (cameraManager == null)
        {
            Debug.LogError("Camera Manager not assigned!");
            return;
        }

        // Get camera position and forward direction
        Transform cameraTransform = cameraManager.transform;
        Vector3 placementPosition = cameraTransform.position + cameraTransform.forward * distanceFromCamera;
        
        // Raycast to find surfaces
        RaycastHit hit;
        if (Physics.Raycast(cameraTransform.position, cameraTransform.forward, out hit, 5.0f))
        {
            placementPosition = hit.point;
        }
        
        CreateAnchoredFlashcard(placementPosition, Quaternion.identity, "Test Flashcard");
    }

    private void SaveAnchorId(string anchorId, string objectName)
    {
        PlayerPrefs.SetString(objectName + "_anchor_id", anchorId);
        PlayerPrefs.Save();
        Debug.Log($"Saved anchor ID {anchorId} for {objectName}");
    }

    private string LoadAnchorId(string objectName)
    {
        return PlayerPrefs.GetString(objectName + "_anchor_id", "");
    }

    public void LoadSavedAnchors()
    {
        string savedId = LoadAnchorId("flashcard_1");
        if (!string.IsNullOrEmpty(savedId))
        {
            Debug.Log($"Found saved anchor ID: {savedId}");
        }
    }

    public void CheckARStatus()
    {
        Debug.Log($"AR Session running: {ARSession.state == ARSessionState.SessionTracking}");
        Debug.Log($"Anchor Manager enabled: {anchorManager.enabled}");
        Debug.Log($"Anchor Manager subsystem running: {anchorManager.subsystem?.running}");
        Debug.Log($"Active anchors: {activeAnchors.Count}");
        
        foreach (var anchor in activeAnchors)
        {
            Debug.Log($"Anchor {anchor.trackableId} tracking state: {anchor.trackingState}");
        }
    }

    private bool hasPlacedFlashcard = false;
    private float placementDelay = 3.0f; // Wait 3 seconds after start
    private float timer = 0f;

    void Update()
    {
        // Auto-place after delay
        if (!hasPlacedFlashcard)
        {
            timer += Time.deltaTime;
            if (timer >= placementDelay)
            {
                Debug.Log("🎯 Auto-placing flashcard after delay");
                PlaceFlashcardAtCamera(2.0f);
                hasPlacedFlashcard = true;
            }
        }
    }
}
