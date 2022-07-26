using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace AireAPIExample
{
    class ArtistSearchResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("artists")]
        public List<Artist> Artists { get; set; }
    }
}
