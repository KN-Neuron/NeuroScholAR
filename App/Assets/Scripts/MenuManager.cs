using UnityEngine;
using TMPro;

public class MenuManager : MonoBehaviour
{
    [SerializeField] private TMP_InputField usernameInput;
    [SerializeField] private TMP_InputField passwordInput;

    public void ExitApplication()
    {
        Application.Quit();
    }

    public void Login()
    {
        // Here you would typically validate the username and password
        if(usernameInput.text == "user" && passwordInput.text == "Qwerty1!") 
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        }
    }
}
