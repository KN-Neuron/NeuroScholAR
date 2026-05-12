using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PasswordToggle : MonoBehaviour
{
    [Header("UI References")]
    public TMP_InputField passwordField;
    public Image buttonIcon;

    [Header("Icons")]
    public Sprite eyeClosedIcon;
    public Sprite eyeOpenIcon;

    private bool isVisible = false;

    public void TogglePassword()
    {
        isVisible = !isVisible;

        if (isVisible)
        {
            // Show password
            passwordField.contentType = TMP_InputField.ContentType.Standard;
            buttonIcon.sprite = eyeOpenIcon;
        }
        else
        {
            // Hide password
            passwordField.contentType = TMP_InputField.ContentType.Password;
            buttonIcon.sprite = eyeClosedIcon;
        }

        passwordField.ForceLabelUpdate();
    }
}