using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

public class PersistentKeyboard : MonoBehaviour
{
    private GameObject lastSelected;

    void Update()
    {
        // 1. If we are currently pointing at/clicking something, remember it
        if (EventSystem.current.currentSelectedGameObject != null)
        {
            lastSelected = EventSystem.current.currentSelectedGameObject;
        }
        // 2. If the laser disappears (becomes null) BUT we were just typing in a text box...
        else if (lastSelected != null && lastSelected.GetComponent<TMP_InputField>() != null)
        {
            // ...Force the EventSystem to keep the text box selected!
            EventSystem.current.SetSelectedGameObject(lastSelected);
        }
    }
}