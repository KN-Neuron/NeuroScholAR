using UnityEngine;
using TMPro;

[ExecuteAlways]
[RequireComponent(typeof(TMP_Text))]
public class CurvedTMP : MonoBehaviour
{
    [Tooltip("Match this to your background curve radius")]
    public float radius = 2.0f;

    [Tooltip("Fine-tune how tight the text wraps")]
    public float bendMultiplier = 1.0f;

    private TMP_Text textComponent;
    private bool needsUpdate = false;

    void Awake()
    {
        textComponent = GetComponent<TMP_Text>();
    }

    void OnEnable()
    {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
        needsUpdate = true; // Force an initial curve
    }

    void OnDisable()
    {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
    }

    void OnTextChanged(Object obj)
    {
        if (obj == textComponent)
        {
            needsUpdate = true;
        }
    }

    void LateUpdate()
    {
        if (needsUpdate)
        {
            CurveTheText();
            needsUpdate = false;
        }
    }

    void CurveTheText()
    {
        textComponent.ForceMeshUpdate();
        TMP_TextInfo textInfo = textComponent.textInfo;
        int characterCount = textInfo.characterCount;

        if (characterCount == 0) return;

        // Loop through every single letter's 3D vertices and bend them
        for (int i = 0; i < characterCount; i++)
        {
            if (!textInfo.characterInfo[i].isVisible)
                continue;

            int vertexIndex = textInfo.characterInfo[i].vertexIndex;
            int materialIndex = textInfo.characterInfo[i].materialReferenceIndex;
            Vector3[] vertices = textInfo.meshInfo[materialIndex].vertices;

            for (int j = 0; j < 4; j++)
            {
                Vector3 orig = vertices[vertexIndex + j];

                // Calculate the cylindrical curve
                float angle = (orig.x * bendMultiplier) / radius;

                float curvedX = Mathf.Sin(angle) * radius;
                float curvedZ = Mathf.Cos(angle) * radius - radius;

                // Apply the new curved positions
                vertices[vertexIndex + j] = new Vector3(curvedX, orig.y, orig.z + curvedZ);
            }
        }

        // Push the curved mesh back to TextMeshPro
        textComponent.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
    }
}