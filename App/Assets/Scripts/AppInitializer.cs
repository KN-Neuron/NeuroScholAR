using UnityEngine;

public class AppInitializer : MonoBehaviour
{
    public static DatabaseHelper DB;

    void Awake()
    {
        DB = new DatabaseHelper();
        Debug.Log("Database initialized");
    }
}