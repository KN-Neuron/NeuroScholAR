using UnityEngine;

using SQLite4Unity3d;
using System.IO;
using System.Linq;

public class DatabaseHelper
{
    private SQLiteConnection db;

    public DatabaseHelper()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, "app.db");

        db = new SQLiteConnection(dbPath);

        db.CreateTable<UserPreference>();
    }

    public void SavePreference(string key, string value)
    {
        var existing = db.Table<UserPreference>().FirstOrDefault(x => x.Key == key);

        if (existing != null)
        {
            existing.Value = value;
            db.Update(existing);
        }
        else
        {
            db.Insert(new UserPreference { Key = key, Value = value });
        }
    }

    public string LoadPreference(string key)
    {
        var pref = db.Table<UserPreference>().FirstOrDefault(x => x.Key == key);

        return pref != null ? pref.Value : null;
    }
}