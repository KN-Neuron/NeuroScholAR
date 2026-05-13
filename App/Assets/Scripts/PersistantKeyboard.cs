using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

public class PersistantKeyboard : MonoBehaviour
{
    private GameObject lastSelected;
    private TMP_InputField activeInputField;

    void Update()
    {
        GameObject currentObj = EventSystem.current.currentSelectedGameObject;

        if (currentObj != null)
        {
            if (currentObj != lastSelected)
            {
                lastSelected = currentObj;

                // if you click on the inputbox it will be selected and the keyboard will pop up 
                TMP_InputField newInput = currentObj.GetComponent<TMP_InputField>();

                if (newInput != null)
                {
                    if (activeInputField != null)
                        activeInputField.onSubmit.RemoveListener(CloseKeyboard);

                    activeInputField = newInput;
                    activeInputField.onSubmit.AddListener(CloseKeyboard);
                }
                else
                {
                    // if you click a different button it should stop the keyboard from coming up
                    activeInputField = null;
                }
            }
        }
        else if (lastSelected != null && activeInputField != null)
        {
            EventSystem.current.SetSelectedGameObject(lastSelected);
        }
    }

    private void CloseKeyboard(string text)
    {
        // if the ok/checkbox button is pressed it should close the keyboard and deselect the input field
        lastSelected = null;

        if (activeInputField != null)
        {
            activeInputField.onSubmit.RemoveListener(CloseKeyboard);
            activeInputField = null;
        }

        EventSystem.current.SetSelectedGameObject(null);
    }
}