using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace AireAPIExample
{
    class Artist
    {
        [JsonPropertyName("id")]
        public string ID { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("country")]
        public string Country { get; set; }

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("recording-list")]
        public List<Recording> Recordings { get; set; }
    }
}
