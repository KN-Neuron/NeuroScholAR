using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace NeuroScholAR.Core.Learning
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum StudySetType
    {
        [EnumMember(Value = "term_definition")]
        TermDefinition,

        [EnumMember(Value = "term_date")]
        TermDate,
    }

    /// <summary>One thing to memorise: a term and its definition, or an event and its date.</summary>
    public sealed class StudyPair
    {
        public StudyPair(string id, string prompt, string answer)
        {
            Id = id;
            Prompt = prompt;
            Answer = answer;
        }

        [JsonProperty("id")] public string Id { get; }
        [JsonProperty("prompt")] public string Prompt { get; }
        [JsonProperty("answer")] public string Answer { get; }
    }

    /// <summary>Study material, matching the spec's import schema (<c>set_name</c>, <c>type</c>, <c>pairs</c>).</summary>
    public sealed class StudySet
    {
        public StudySet(string setName, StudySetType type, IReadOnlyList<StudyPair> pairs)
        {
            SetName = setName;
            Type = type;
            Pairs = pairs;
        }

        [JsonProperty("set_name")] public string SetName { get; }
        [JsonProperty("type")] public StudySetType Type { get; }
        [JsonProperty("pairs")] public IReadOnlyList<StudyPair> Pairs { get; }
    }

    /// <summary>Sets that ship with the app, so a session can start with zero preparation.</summary>
    public static class PredefinedSets
    {
        public static readonly IReadOnlyList<StudySet> All = new[]
        {
            new StudySet("Biologia - komórka", StudySetType.TermDefinition, new[]
            {
                new StudyPair("p1", "Mitochondrium", "Organellum produkujące ATP"),
                new StudyPair("p2", "Rybosom", "Miejsce syntezy białek"),
                new StudyPair("p3", "Jądro komórkowe", "Przechowuje DNA i steruje komórką"),
                new StudyPair("p4", "Aparat Golgiego", "Modyfikuje i pakuje białka"),
                new StudyPair("p5", "Lizosom", "Trawi zużyte struktury komórki"),
                new StudyPair("p6", "Chloroplast", "Prowadzi fotosyntezę"),
                new StudyPair("p7", "Błona komórkowa", "Kontroluje transport do i z komórki"),
                new StudyPair("p8", "Retikulum endoplazmatyczne", "Sieć kanałów: synteza białek i lipidów"),
            }),
            new StudySet("Historia Polski - daty", StudySetType.TermDate, new[]
            {
                new StudyPair("h1", "Chrzest Polski", "966"),
                new StudyPair("h2", "Bitwa pod Grunwaldem", "1410"),
                new StudyPair("h3", "Unia lubelska", "1569"),
                new StudyPair("h4", "Konstytucja 3 Maja", "1791"),
                new StudyPair("h5", "Odzyskanie niepodległości", "1918"),
                new StudyPair("h6", "Wybory czerwcowe", "1989"),
            }),
            new StudySet("Angielski - słówka", StudySetType.TermDefinition, new[]
            {
                new StudyPair("e1", "Reliable", "Niezawodny"),
                new StudyPair("e2", "Thorough", "Dokładny, gruntowny"),
                new StudyPair("e3", "Eager", "Chętny, gorliwy"),
                new StudyPair("e4", "Ubiquitous", "Wszechobecny"),
                new StudyPair("e5", "Meticulous", "Drobiazgowy"),
            }),
        };
    }
}
