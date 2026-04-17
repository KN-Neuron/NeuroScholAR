using SQLite4Unity3d;
using UnityEngine;
public class UserPreference
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string Key { get; set; }

    public string Value { get; set; }
}