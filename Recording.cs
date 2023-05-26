using System;
using System.Globalization;
using System.Text.Json.Serialization;
//simple class
namespace AireAPIExample
{
    public class Recording
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        public int LyricCount { get; set; }
    }
}